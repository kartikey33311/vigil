// -----------------------------------------------------------------------------
// Vigil — ambient wildlife.
//
// Rats. They cannot hurt you and they are not the antagonist. They exist to make
// noise you cannot immediately identify.
//
// This is the single cheapest way to make a stealth game tense. Once ANY noise in
// the level might be vermin, the player can no longer treat "I heard something"
// as "the monster is there" — every sound becomes a decision instead of a fact.
// A level where every sound is the threat trains players to react perfectly
// within one session.
//
// They also feed the antagonist's perception: a startled rat emits a real
// Stimulus, so the monster investigates genuinely empty rooms. Watching it
// commit to a false lead is one of the best things the AI does.
//
// Runs on the SAME StateMachine<TContext> engine as the antagonist, on a Low tick
// budget — three states, no perception rig, no pathfinding queries.
// -----------------------------------------------------------------------------

using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Events;
using Vigil.Core.Mathx;
using Vigil.Core.Services;
using Vigil.Core.Simulation;
using Vigil.Core.StateMachine;

namespace Vigil.AI.Agents
{
    /// <summary>Shared state for the ambient FSM. Deliberately tiny.</summary>
    public sealed class AmbientContext
    {
        public Transform Body;
        public DeterministicRandom Random;
        public SimTime Time;

        public float3 Home;
        public float3 Goal;
        public float WanderRadius = 6f;
        public float Speed = 2.2f;

        /// <summary>Set by the agent when something loud happened nearby.</summary>
        public bool Startled;

        public float3 Position => Body != null ? (float3)Body.position : float3.zero;

        /// <summary>Moves toward a goal, snapping to the NavMesh so rats stay on the floor.</summary>
        public void StepToward(float3 goal, float dt)
        {
            if (Body == null) return;

            float3 delta = goal - (float3)Body.position;
            delta.y = 0f;

            float distance = math.length(delta);
            if (distance < 0.05f) return;

            float3 next = (float3)Body.position + (delta / distance) * Speed * dt;

            if (NavMesh.SamplePosition((Vector3)next, out NavMeshHit hit, 1.5f, NavMesh.AllAreas))
            {
                Body.position = hit.position;
            }

            Body.rotation = Quaternion.LookRotation(new Vector3(delta.x, 0f, delta.z).normalized, Vector3.up);
        }

        public bool TryPickWanderGoal(out float3 goal)
        {
            float3 candidate = Home + Random.NextPointInDiscXZ(WanderRadius);

            if (NavMesh.SamplePosition((Vector3)candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            {
                goal = hit.position;
                return true;
            }

            goal = Home;
            return false;
        }
    }

    public enum AmbientStateId { Idle = 0, Wander = 1, Startle = 2 }

    public sealed class AmbientIdleState : StateBase<AmbientContext>
    {
        float _remaining;

        public override int Id => (int)AmbientStateId.Idle;
        public override string Name => "Idle";

        public override void OnEnter(AmbientContext c, in SimTime t) => _remaining = c.Random.NextFloat(1.5f, 5f);

        public override StateStatus OnTick(AmbientContext c, in SimTime t)
        {
            _remaining -= t.DeltaTime;
            return _remaining <= 0f ? StateStatus.Succeeded : StateStatus.Running;
        }
    }

    public sealed class AmbientWanderState : StateBase<AmbientContext>
    {
        float _timeout;

        public override int Id => (int)AmbientStateId.Wander;
        public override string Name => "Wander";

        public override void OnEnter(AmbientContext c, in SimTime t)
        {
            c.TryPickWanderGoal(out c.Goal);
            _timeout = 6f;
        }

        public override StateStatus OnTick(AmbientContext c, in SimTime t)
        {
            _timeout -= t.DeltaTime;

            c.StepToward(c.Goal, t.DeltaTime);

            if (_timeout <= 0f) return StateStatus.Succeeded;
            return math.distance(c.Position, c.Goal) < 0.4f ? StateStatus.Succeeded : StateStatus.Running;
        }
    }

    /// <summary>Bolts away from whatever startled it, loudly. This is the useful state.</summary>
    public sealed class AmbientStartleState : StateBase<AmbientContext>
    {
        float _remaining;

        public override int Id => (int)AmbientStateId.Startle;
        public override string Name => "Startle";

        public override void OnEnter(AmbientContext c, in SimTime t)
        {
            _remaining = c.Random.NextFloat(1.2f, 2.4f);
            c.Goal = c.Position + c.Random.NextDirectionXZ() * 7f;
            c.Startled = false;
        }

        public override StateStatus OnTick(AmbientContext c, in SimTime t)
        {
            _remaining -= t.DeltaTime;

            // Triple speed: the sudden scatter is the whole point.
            float3 delta = c.Goal - c.Position;
            if (math.lengthsq(delta) > 0.04f)
            {
                float saved = c.Speed;
                c.Speed = saved * 3f;
                c.StepToward(c.Goal, t.DeltaTime);
                c.Speed = saved;
            }

            return _remaining <= 0f ? StateStatus.Succeeded : StateStatus.Running;
        }
    }

    [RequireComponent(typeof(NetworkObject))]
    public sealed class AmbientNpc : NetworkBehaviour, IAdaptiveTickable, IStimulusReceiver
    {
        [SerializeField, Min(0.5f)] float _wanderRadius = 6f;
        [SerializeField, Min(0.1f)] float _speed = 2.2f;

        [SerializeField, Range(0f, 1f), Tooltip("Per-tick chance of emitting a scuttle while wandering.")]
        float _noiseChance = 0.05f;

        [SerializeField, Min(1f), Tooltip("Distance beyond which this stops simulating entirely.")]
        float _dormantDistance = 45f;

        readonly NetworkVariable<Vector3> _netPosition = new NetworkVariable<Vector3>(
            Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        AmbientContext _context;
        StateMachine<AmbientContext> _brain;
        TickScheduler _scheduler;
        Perception.PerceptionBus _bus;
        IEventBus _events;

        Transform _transform;
        bool _ready;
        Vector3 _renderPosition;

        public string DebugStatePath => _brain != null ? _brain.GetActivePath() : "<inactive>";

        void Awake()
        {
            _transform = transform;
            _renderPosition = _transform.position;
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer || !Services.IsReady) return;

            ServiceContext ctx = Services.Active;
            _scheduler = ctx.TryGet<TickScheduler>();
            _bus = ctx.TryGet<Perception.PerceptionBus>();
            _events = ctx.TryGet<IEventBus>();

            _context = new AmbientContext
            {
                Body = _transform,
                Random = DeterministicRandom.ForEntity((uint)NetworkObjectId, NetworkObjectId, 0x4A7u),
                Home = _transform.position,
                WanderRadius = _wanderRadius,
                Speed = _speed
            };

            _brain = new StateMachine<AmbientContext>(0, "Ambient");
            _brain.AddState(new AmbientIdleState())
                  .AddState(new AmbientWanderState())
                  .AddState(new AmbientStartleState());
            _brain.SetInitialState((int)AmbientStateId.Idle);

            _brain.AddTransition((int)AmbientStateId.Idle, (int)AmbientStateId.Wander,
                c => true, priority: 10, requiredStatus: StateStatus.Succeeded, label: "move on");

            _brain.AddTransition((int)AmbientStateId.Wander, (int)AmbientStateId.Idle,
                c => true, priority: 10, requiredStatus: StateStatus.Succeeded, label: "settle");

            _brain.AddTransition((int)AmbientStateId.Startle, (int)AmbientStateId.Idle,
                c => true, priority: 10, requiredStatus: StateStatus.Succeeded, label: "calm down");

            // The interrupt. Same AnyState mechanism the antagonist uses for stun.
            _brain.AddAnyTransition((int)AmbientStateId.Startle,
                c => c.Startled, priority: 100, cooldownSeconds: 2f, label: "startled");

            SimClock clock = ctx.TryGet<SimClock>();
            SimTime now = clock != null ? clock.Snapshot() : default;
            _context.Time = now;
            _brain.OnEnter(_context, in now);

            _bus?.Register(this);
            _scheduler?.Register(this, TickBudget.Low);
            _ready = true;
        }

        public override void OnNetworkDespawn()
        {
            if (!_ready) return;
            _scheduler?.Unregister(this);
            _bus?.Unregister(this);
            _ready = false;
        }

        public TickBudget DesiredBudget => _ready ? TickBudget.Low : TickBudget.Dormant;

        public void OnSimTick(in SimTime time)
        {
            if (!IsServer || !_ready) return;

            _context.Time = time;
            _brain.OnTick(_context, in time);

            // Scuttling. Small radius on purpose — audible only from nearby, so it
            // reads as "something small, close" rather than a threat across the map.
            if (_context.Random.Chance(_noiseChance))
            {
                Stimulus s = new Stimulus(
                    StimulusChannel.Sound, StimulusTag.Footstep, _transform.position,
                    0.28f, 11f, NetworkObjectId, time.Tick);

                _bus?.Broadcast(in s);
                _events?.Publish(new NoiseEmittedEvent { Stimulus = s });
            }

            _netPosition.Value = _transform.position;
        }

        // ---- IStimulusReceiver: loud noises scare them -------------------------

        public float3 ListenerPosition => _transform != null ? (float3)_transform.position : float3.zero;
        public float ListenerRange => 18f;
        public bool IsListening => _ready;

        public void OnStimulus(in Stimulus stimulus)
        {
            if (stimulus.SourceId == NetworkObjectId) return;
            if (_context == null) return;

            // Only genuinely loud things startle them, so a crouching player can move
            // past without giving themselves away — which makes crouch matter twice.
            if (stimulus.IntensityAt(ListenerPosition) > 0.35f) _context.Startled = true;
        }

        void Update()
        {
            if (IsServer) return;

            float t = 1f - Mathf.Exp(-10f * Time.deltaTime);
            _renderPosition = Vector3.Lerp(_renderPosition, _netPosition.Value, t);
            _transform.position = _renderPosition;
        }
    }
}
