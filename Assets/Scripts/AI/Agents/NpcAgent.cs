// -----------------------------------------------------------------------------
// Vigil — the antagonist component.
//
// Everything simulated here runs on the SERVER ONLY. Clients receive a smoothed
// pose plus a coarse state byte, and nothing else. They do not receive the
// awareness model, the current path, or the Director's intent — a client that
// holds any of those can be modified to show the player exactly what the monster
// knows, which ends the game.
//
// The LOD system (DesiredBudget) is what makes the agent count scale. Note that it
// keys off AWARENESS, not distance: an agent that is hunting stays Critical even
// when nobody can see it, because its decisions still matter. Distance-only LOD
// demotes exactly the agent the players are about to run into.
// -----------------------------------------------------------------------------

using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using Vigil.AI.Perception;
using Vigil.AI.Pathfinding;
using Vigil.AI.StateMachine;
using Vigil.AI.Steering;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Events;
using Vigil.Core.Services;
using Vigil.Core.Simulation;
using Vigil.Core.StateMachine;
using Vigil.Data;

namespace Vigil.AI.Agents
{
    /// <summary>
    /// Optional service supplying ambient light level. Registered by the power/light
    /// system; perception falls back to a mid value when absent so the AI still works
    /// in a bare test scene.
    /// </summary>
    public interface IDarknessSampler
    {
        /// <summary>0 = fully lit, 1 = pitch black.</summary>
        float DarknessAt(float3 position);
    }

    [RequireComponent(typeof(NetworkObject))]
    public class NpcAgent : NetworkBehaviour,
        IAdaptiveTickable, IStimulusReceiver, IDamageable, IFactionMember, IAgentBody, ISensorHost
    {
        [Header("Configuration")]
        [SerializeField] AgentArchetypeConfig _archetype = null;

        [Header("Body")]
        [SerializeField] Transform _eye = null;
        [SerializeField, Min(0.1f)] float _eyeHeight = 1.7f;

        [Header("Layers")]
        [SerializeField] LayerMask _obstacleMask = ~0;
        [SerializeField] LayerMask _agentMask = 0;
        [SerializeField] LayerMask _strikeMask = ~0;

        // ---- replicated -------------------------------------------------------
        // Deliberately minimal. Position and yaw for rendering, a state byte for
        // animation, an awareness byte for client-side audio cues.
        readonly NetworkVariable<Vector3> _netPosition = new NetworkVariable<Vector3>(
            Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<float> _netYaw = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<byte> _netState = new NetworkVariable<byte>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<byte> _netAwareness = new NetworkVariable<byte>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // ---- server-side simulation -------------------------------------------
        AgentContext _context;
        StateMachine<AgentContext> _brain;
        PerceptionRig _rig;
        AwarenessModel _model;
        PathFollower _follower;
        SteeringSolver _steering;
        SteeringSettings _steerSettings;

        TickScheduler _scheduler;
        PerceptionBus _bus;
        INavigationService _nav;
        IRegionGraph _regions;
        IAIDirector _director;
        IEventBus _events;
        IDarknessSampler _darkness;

        readonly Collider[] _strikeBuffer = new Collider[8];
        readonly float3[] _visibilityScratch = new float3[4];

        float _health;
        bool _simulationReady;

        // ---- client-side interpolation ----------------------------------------
        Vector3 _renderPosition;
        float _renderYaw;

        Transform _transform;
        Animator _animator;
        MonsterAnimator _monster;

        public Faction Faction => _archetype != null ? _archetype.Faction : Faction.Entity;
        public ulong EntityId => NetworkObjectId;
        public float Health => _health;
        public float MaxHealth => _archetype != null ? _archetype.MaxHealth : 1000f;
        public bool IsAlive => _health > 0f;

        /// <summary>Active FSM path, for the debug overlay. Allocates — never call from a tick.</summary>
        public string DebugStatePath => _brain != null ? _brain.GetActivePath() : "<not simulating>";

        public AwarenessModel DebugAwareness => _model;
        public AgentContext DebugContext => _context;

        // =====================================================================
        // Lifecycle
        // =====================================================================

        void Awake()
        {
            _transform = transform;
            _animator = GetComponentInChildren<Animator>();
            _monster = GetComponent<MonsterAnimator>();
            _renderPosition = _transform.position;
        }

        public override void OnNetworkSpawn()
        {
            _health = MaxHealth;
            _renderPosition = _transform.position;
            _renderYaw = _transform.eulerAngles.y;

            // Resolved on every peer: clients need the bus to hear the entity, even
            // though they never simulate it.
            if (Services.IsReady) _events = Services.TryGet<IEventBus>();

            if (!IsServer)
            {
                // Clients render only. No perception, no pathfinding, no scheduler
                // registration — a client that simulates the monster is a client that
                // knows where it is.
                return;
            }

            if (!Services.IsReady)
            {
                VLog.Error(LogCat.AI, $"NpcAgent {EntityId} spawned with no active ServiceContext — simulation disabled.", this);
                return;
            }

            BuildSimulation();
        }

        void BuildSimulation()
        {
            ServiceContext ctx = Services.Active;

            _scheduler = ctx.TryGet<TickScheduler>();
            _bus = ctx.TryGet<PerceptionBus>();
            _nav = ctx.TryGet<INavigationService>();
            _regions = ctx.TryGet<IRegionGraph>();
            _director = ctx.TryGet<IAIDirector>();
            _events = ctx.TryGet<IEventBus>();
            _darkness = ctx.TryGet<IDarknessSampler>();

            if (_nav == null)
            {
                VLog.Error(LogCat.AI, $"NpcAgent {EntityId}: no INavigationService registered.", this);
                return;
            }

            if (_archetype == null)
            {
                VLog.Error(LogCat.AI, $"NpcAgent {EntityId}: no AgentArchetypeConfig assigned.", this);
                return;
            }

            PerceptionConfig perception = _archetype.Perception;
            NavigationConfig navigation = _archetype.Navigation;

            uint seed = (uint)(EntityId * 2654435761UL);

            _model = new AwarenessModel(EntityId, perception, _events);
            _follower = new PathFollower(navigation);
            _steering = new SteeringSolver();

            ScentTrail trail = ctx.TryGet<ScentTrail>();
            _rig = new PerceptionRig(this, perception, _bus, trail, _model, seed);

            _context = new AgentContext(
                EntityId, this, _model, _nav, _regions, _follower, _archetype, _events, seed)
            {
                TerritoryCenter = _transform.position
            };

            _brain = AntagonistBrain.Build(_context, _archetype);
            _brain.OnTransition += OnBrainTransition;

            _steerSettings = new SteeringSettings
            {
                MaxSpeed = _archetype.MaxMoveSpeed,
                MaxAcceleration = _archetype.Acceleration,
                SeparationRadius = 1.8f,
                SeparationWeight = 1.2f,
                AvoidanceProbeDistance = 2.4f,
                AvoidanceWeight = 1.6f,
                AgentRadius = navigation != null ? navigation.AgentRadius : 0.45f,
                ObstacleMask = _obstacleMask,
                AgentMask = _agentMask
            };

            SimTime now = ctx.TryGet<SimClock>() != null ? ctx.Get<SimClock>().Snapshot() : default;
            _context.Time = now;
            _brain.OnEnter(_context, in now);

            _bus?.Register(this);
            _scheduler?.Register(this, TickBudget.Normal);

            _simulationReady = true;
            VLog.Info(LogCat.AI, $"NpcAgent {EntityId} ({_archetype.DisplayName}) simulating.", this);
        }

        public override void OnNetworkDespawn()
        {
            if (!_simulationReady) return;

            // Order matters: stop being ticked before releasing anything the tick
            // depends on, or a queued tick can run against half-torn-down state.
            _scheduler?.Unregister(this);
            _bus?.Unregister(this);
            _nav?.CancelAllFor(EntityId);

            if (_brain != null)
            {
                _brain.OnTransition -= OnBrainTransition;
                SimTime t = _context != null ? _context.Time : default;
                _brain.OnExit(_context, in t);
            }

            _simulationReady = false;
        }

        void OnBrainTransition(int from, int to, string label)
        {
            _netState.Value = (byte)math.clamp(to, 0, 255);

            _events?.Publish(new AgentStateChangedEvent
            {
                EntityId = EntityId,
                FromState = from,
                ToState = to
            });
        }

        // =====================================================================
        // Simulation
        // =====================================================================

        public TickBudget DesiredBudget
        {
            get
            {
                if (!_simulationReady || _model == null) return TickBudget.Dormant;

                TickBudget budget;
                switch (_model.PeakLevel)
                {
                    case AwarenessLevel.Confirmed: budget = TickBudget.Critical; break;
                    case AwarenessLevel.Alerted:   budget = TickBudget.High; break;
                    case AwarenessLevel.Lost:      budget = TickBudget.High; break;
                    case AwarenessLevel.Suspicious: budget = TickBudget.Normal; break;
                    default:
                        // Unaware. Stay at Normal while the Director has any pressure
                        // (it may be steering us toward a player we cannot sense yet);
                        // drop to Low only during genuine downtime.
                        budget = _director != null && _director.CurrentIntent.Pressure > 0.05f
                            ? TickBudget.Normal
                            : TickBudget.Low;
                        break;
                }

                // An archetype may forbid being demoted past a floor — a boss that
                // visibly freezes because it was demoted is worse than the cost saved.
                TickBudget floor = _archetype != null ? _archetype.MinimumBudget : TickBudget.Dormant;
                return (TickBudget)math.min((int)budget, (int)floor);
            }
        }

        public void OnSimTick(in SimTime time)
        {
            if (!IsServer || !_simulationReady) return;

            _context.Time = time;
            _model.SetOwnerPosition(_transform.position);

            // 1. Sense
            _rig.Tick(in time, _model);

            // 2. Director intent
            if (_director != null) _context.Intent = _director.CurrentIntent;

            // 3. Publish belief to the blackboard for transition guards
            if (_model.TryGetPrimaryTarget(out PerceivedTarget target))
            {
                _context.Blackboard.Set(BBKeys.TargetId, target.TargetId, time.Tick);
                _context.Blackboard.Set(BBKeys.TargetPosition, target.LastKnownPosition, time.Tick);
                _context.Blackboard.Set(BBKeys.HasLineOfSight, target.HasLineOfSight, time.Tick);
            }
            else
            {
                _context.Blackboard.Set(BBKeys.TargetId, 0UL, time.Tick);
                _context.Blackboard.Set(BBKeys.HasLineOfSight, false, time.Tick);
            }

            _context.Blackboard.Set(BBKeys.Awareness, _model.PeakAwareness, time.Tick);
            _context.Blackboard.Set(BBKeys.AwarenessLevel, (int)_model.PeakLevel, time.Tick);

            // 4. Decide
            _brain.OnTick(_context, in time);

            // 5. Replicate
            _netPosition.Value = _transform.position;
            _netYaw.Value = _transform.eulerAngles.y;
            _netAwareness.Value = (byte)_model.PeakLevel;
        }

        // =====================================================================
        // IAgentBody
        // =====================================================================

        public float3 Position => _transform.position;
        public quaternion Rotation => _transform.rotation;
        public float3 Velocity => _steering != null ? _steering.CurrentVelocity : float3.zero;
        public float3 EyePosition => _eye != null ? (float3)_eye.position : (float3)_transform.position + new float3(0f, _eyeHeight, 0f);
        public float3 Forward => _transform.forward;

        public void Move(float3 desiredVelocity, in SimTime time)
        {
            if (_steering == null) return;

            float3 solved = _steering.Solve(_transform.position, desiredVelocity, in _steerSettings, _transform, in time);

            if (math.lengthsq(solved) < 1e-6f) return;

            // Kinematic move, then snap back onto the NavMesh. Snapping is what keeps
            // steering avoidance from gradually walking the agent off the mesh.
            Vector3 next = _transform.position + (Vector3)solved * time.DeltaTime;

            if (_nav != null && _nav.SamplePosition(next, 1.5f, NavArea.MaskAll, out float3 onMesh))
            {
                next = onMesh;
            }

            _transform.position = next;
        }

        public void FaceDirection(float3 direction, in SimTime time)
        {
            direction.y = 0f;
            if (math.lengthsq(direction) < 1e-5f) return;

            float turnRate = _archetype != null ? _archetype.TurnRateDegrees : 240f;

            Quaternion target = Quaternion.LookRotation((Vector3)math.normalize(direction), Vector3.up);
            _transform.rotation = Quaternion.RotateTowards(_transform.rotation, target, turnRate * time.DeltaTime);
        }

        public void SetTraversalPose(float3 position, quaternion rotation)
        {
            _transform.SetPositionAndRotation((Vector3)position, rotation);
        }

        public void PlayAnimation(string stateName)
        {
            // The generated sample level uses primitives, so no Animator is present.
            // Silently ignoring that is correct: a warning per animation call per tick
            // would bury the log.
            // Procedural rig first: it is the one that actually exists in the
            // shipping prefab. The Animator path remains for future rigged art.
            if (_monster != null && stateName == "AttackStrike") _monster.TriggerLunge();

            if (_animator == null || string.IsNullOrEmpty(stateName)) return;
            _animator.CrossFade(stateName, 0.15f);
        }

        public bool TryStrike(float damage, float range)
        {
            if (!IsServer) return false;

            Vector3 origin = _transform.position + _transform.forward * (range * 0.5f) + Vector3.up;

            int count = Physics.OverlapSphereNonAlloc(
                origin, range * 0.6f, _strikeBuffer, _strikeMask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                Collider c = _strikeBuffer[i];
                if (c == null) continue;

                IDamageable damageable = c.GetComponentInParent<IDamageable>();
                if (damageable == null || ReferenceEquals(damageable, this)) continue;
                if (!damageable.IsAlive) continue;

                DamageInfo info = new DamageInfo
                {
                    Amount = damage,
                    Type = DamageType.Melee,
                    InstigatorId = EntityId,
                    HitPoint = c.ClosestPoint(origin),
                    HitNormal = _transform.forward,
                    Tick = _context != null ? _context.Time.Tick : 0
                };

                damageable.ApplyDamage(in info);
                return true;
            }

            return false;
        }

        public void EmitStimulus(in Stimulus stimulus)
        {
            // Perception (server-authoritative): what other agents can hear.
            _bus?.Broadcast(in stimulus);

            // Audio (local): what the listener on THIS peer can hear.
            _events?.Publish(new NoiseEmittedEvent { Stimulus = stimulus });

            // Loud cues are replicated so remote clients hear the entity roar or
            // strike. Deliberately gated on intensity: replicating every scuff would
            // cost bandwidth for sounds nobody can hear anyway, and would leak the
            // entity's position to clients that interest management is hiding it from.
            if (IsServer && stimulus.Intensity >= 0.7f)
            {
                EntityNoiseRpc((uint)stimulus.Tag, (Vector3)stimulus.Position, stimulus.Intensity, stimulus.Radius);
            }
        }

        [Rpc(SendTo.NotServer)]
        void EntityNoiseRpc(uint tag, Vector3 position, float intensity, float radius)
        {
            if (_events == null) return;

            Stimulus s = new Stimulus(
                StimulusChannel.Sound, (StimulusTag)tag, position, intensity, radius, EntityId, 0);

            _events.Publish(new NoiseEmittedEvent { Stimulus = s });
        }

        // =====================================================================
        // ISensorHost
        // =====================================================================

        public ulong OwnerId => EntityId;
        public float3 EyeForward => _eye != null ? (float3)_eye.forward : (float3)_transform.forward;

        public float DarknessAt(float3 position)
        {
            // 0.5 is a deliberate middle default: with no lighting system present the
            // AI should be neither blind nor omniscient.
            return _darkness != null ? _darkness.DarknessAt(position) : 0.5f;
        }

        // =====================================================================
        // IStimulusReceiver
        // =====================================================================

        public float3 ListenerPosition => _transform != null ? (float3)_transform.position : float3.zero;

        public float ListenerRange =>
            _archetype != null && _archetype.Perception != null ? _archetype.Perception.MaxSightRange * 2f : 60f;

        public bool IsListening => _simulationReady && IsAlive && !_context.IsStunned;

        public void OnStimulus(in Stimulus stimulus)
        {
            // Never react to our own noise — otherwise the monster's footsteps keep it
            // permanently alerted to itself.
            if (stimulus.SourceId == EntityId) return;
            _rig?.Hearing.Enqueue(in stimulus);
        }

        // =====================================================================
        // IDamageable
        // =====================================================================

        public float ApplyDamage(in DamageInfo info)
        {
            if (!IsServer || !IsAlive) return 0f;

            _health = math.max(0f, _health - info.Amount);

            // Flares and equivalents stun rather than kill. The stun is the players'
            // only real counterplay, so it is driven directly off damage type.
            if (info.Type == DamageType.Psychological || info.Type == DamageType.Environmental)
            {
                if (_context != null) _context.IsStunned = true;
            }

            return info.Amount;
        }

        // =====================================================================
        // Client-side rendering
        // =====================================================================

        void Update()
        {
            DriveCreaturePose();

            if (IsServer) return;

            // Exponential smoothing toward the replicated pose. Cheap, stable under
            // jitter, and — unlike extrapolation — cannot push the monster through a
            // wall when a packet is late.
            float t = 1f - Mathf.Exp(-12f * Time.deltaTime);

            _renderPosition = Vector3.Lerp(_renderPosition, _netPosition.Value, t);
            _renderYaw = Mathf.LerpAngle(_renderYaw, _netYaw.Value, t);

            _transform.position = _renderPosition;
            _transform.rotation = Quaternion.Euler(0f, _renderYaw, 0f);

            if (_animator != null)
            {
                _animator.SetFloat("Speed", (_netPosition.Value - _renderPosition).magnitude / Mathf.Max(0.0001f, Time.deltaTime));
            }
        }

        /// <summary>
        /// Feeds the procedural rig. Runs on EVERY peer.
        ///
        /// <para>Aggression is derived from the REPLICATED awareness byte rather
        /// than the awareness model, because that model is server-only — driving
        /// the pose from it would leave remote clients rendering a calm patrol gait
        /// while the host renders a hunt. The gait is a tell the player reads, so
        /// it has to be correct on the machine doing the looking.</para>
        /// </summary>
        void DriveCreaturePose()
        {
            if (_monster == null) return;

            // Unaware .. Confirmed maps onto 0..1. Lost sits high: an entity that
            // has just lost you is at its most agitated, not its calmest.
            float aggression;
            switch ((AwarenessLevel)_netAwareness.Value)
            {
                case AwarenessLevel.Confirmed: aggression = 1f; break;
                case AwarenessLevel.Lost: aggression = 0.8f; break;
                case AwarenessLevel.Alerted: aggression = 0.65f; break;
                case AwarenessLevel.Suspicious: aggression = 0.3f; break;
                default: aggression = 0f; break;
            }

            _monster.SetAggression(aggression);

            // Head tracking is server-driven: only the server knows where the
            // target actually is, and the head pose is carried to clients by the
            // replicated rotation anyway.
            if (!IsServer || _model == null) return;

            if (_model.TryGetPrimaryTarget(out PerceivedTarget target) &&
                target.Level >= AwarenessLevel.Suspicious)
            {
                _monster.SetLookTarget(target.LastKnownPosition);
            }
            else
            {
                _monster.ClearLookTarget();
            }
        }

        // =====================================================================
        // IPerceivable-facing helper used by other agents' vision
        // =====================================================================

        /// <summary>Body sample points, for agents that can perceive each other.</summary>
        public int GetVisibilitySamples(float3[] buffer)
        {
            if (buffer == null || buffer.Length == 0) return 0;

            float3 basePos = _transform.position;
            int n = 0;

            buffer[n++] = basePos + new float3(0f, _eyeHeight, 0f);
            if (n < buffer.Length) buffer[n++] = basePos + new float3(0f, _eyeHeight * 0.55f, 0f);
            if (n < buffer.Length) buffer[n++] = basePos + new float3(0f, 0.2f, 0f);

            return n;
        }
    }
}
