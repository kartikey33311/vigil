// -----------------------------------------------------------------------------
// Vigil — the context every antagonist state operates on.
//
// All Unity coupling is behind IAgentBody. That is not architectural purity for
// its own sake: it means the entire behaviour graph — every transition, every
// search heuristic, every attack timing — can be exercised in an EditMode test
// with a stub body, no scene, no NavMesh and no network. Behaviour bugs found in
// a 20ms unit test instead of a 5-minute playtest is the single largest
// productivity difference between this and a MonoBehaviour-per-state design.
//
// Path requests are managed HERE rather than in each state, because "request,
// poll, handle failure, respect the repath interval" is identical in six states
// and getting it subtly wrong in one of them produces an agent that freezes only
// in that specific behaviour.
// -----------------------------------------------------------------------------

using Unity.Mathematics;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Events;
using Vigil.Core.Mathx;
using Vigil.Core.Simulation;
using Vigil.Data;

namespace Vigil.AI.StateMachine
{
    /// <summary>FSM state ids for the antagonist. Cast to int for <see cref="Core.StateMachine.IState{T}"/>.</summary>
    public enum AgentStateId
    {
        Dormant = 0,
        Idle = 1,
        Patrol = 2,
        Investigate = 3,
        Search = 4,
        Stalk = 5,
        Reposition = 6,
        Chase = 7,
        Attack = 8,
        Grapple = 9,
        Retreat = 10,
        Stunned = 11,
        TraverseLink = 12,
        ReturnToTerritory = 13,

        /// <summary>Composite node containing Chase/Reposition/Attack/Grapple.</summary>
        HuntRoot = 100,

        /// <summary>Composite node containing Investigate/Search.</summary>
        InvestigateRoot = 101
    }

    /// <summary>
    /// Locomotion and presentation surface of an agent. Implemented by NpcAgent in
    /// the scene, and by a stub in tests.
    /// </summary>
    public interface IAgentBody
    {
        float3 Position { get; }
        quaternion Rotation { get; }
        float3 Velocity { get; }
        float3 EyePosition { get; }
        float3 Forward { get; }

        /// <summary>Applies a desired velocity for this tick. The body handles acceleration limits.</summary>
        void Move(float3 desiredVelocity, in SimTime time);

        /// <summary>Turns toward a world direction at the archetype's turn rate.</summary>
        void FaceDirection(float3 direction, in SimTime time);

        /// <summary>Teleports during a link traversal. Bypasses collision by design.</summary>
        void SetTraversalPose(float3 position, quaternion rotation);

        /// <summary>Fire-and-forget animation trigger. Must no-op safely with no Animator.</summary>
        void PlayAnimation(string stateName);

        /// <summary>Attempts damage against whatever occupies the strike volume. Server-only.</summary>
        bool TryStrike(float damage, float range);

        /// <summary>Emits a stimulus into the world (the agent's own footsteps, roars, impacts).</summary>
        void EmitStimulus(in Stimulus stimulus);
    }

    /// <summary>
    /// Encapsulates one agent's outstanding path request, including the repath
    /// cadence. Shared by every state that moves.
    /// </summary>
    public sealed class PathRequestSlot
    {
        readonly INavigationService _nav;
        readonly NavPath _result = new NavPath();

        PathHandle _handle = PathHandle.Invalid;
        double _lastRequestAt = double.NegativeInfinity;
        float3 _lastGoal;

        public PathQueryStatus LastStatus { get; private set; } = PathQueryStatus.Idle;
        public bool HasPending => _handle.IsValid;
        public NavPath Result => _result;

        public PathRequestSlot(INavigationService nav)
        {
            _nav = nav;
        }

        /// <summary>
        /// Requests a path if enough time has passed and the goal has moved
        /// meaningfully. Returns true when a NEW corridor became available this tick.
        /// </summary>
        public bool Update(
            float3 from, float3 goal, ulong requesterId, in SimTime time,
            float repathInterval, PathPriority priority, float goalTolerance = 1.5f)
        {
            // Poll first — a completed request must be consumed before we consider
            // issuing another, or handles leak and the slot pool starves.
            if (_handle.IsValid)
            {
                PathQueryStatus status = _nav.Poll(_handle, _result);
                if (status != PathQueryStatus.Pending)
                {
                    _handle = PathHandle.Invalid;
                    LastStatus = status;
                    return status == PathQueryStatus.Success || status == PathQueryStatus.PartialSuccess;
                }
                return false;
            }

            bool goalMoved = math.distancesq(goal, _lastGoal) > goalTolerance * goalTolerance;
            bool intervalElapsed = (time.Elapsed - _lastRequestAt) >= repathInterval;

            if (!goalMoved && !intervalElapsed) return false;

            NavPathRequest request = NavPathRequest.Create(from, goal, requesterId, priority);
            _handle = _nav.RequestPath(in request);

            if (_handle.IsValid)
            {
                _lastRequestAt = time.Elapsed;
                _lastGoal = goal;
            }
            else
            {
                // Queue saturated. Back off slightly so we do not spin on a full
                // queue every tick and starve lower-priority agents entirely.
                _lastRequestAt = time.Elapsed - repathInterval * 0.5;
                LastStatus = PathQueryStatus.Failed;
            }

            return false;
        }

        public void Cancel()
        {
            if (_handle.IsValid) _nav.Cancel(_handle);
            _handle = PathHandle.Invalid;
            LastStatus = PathQueryStatus.Idle;
            _lastGoal = new float3(float.MaxValue, 0f, float.MaxValue);
        }

        /// <summary>Forces the next Update to issue a request regardless of cadence.</summary>
        public void Invalidate()
        {
            _lastRequestAt = double.NegativeInfinity;
            _lastGoal = new float3(float.MaxValue, 0f, float.MaxValue);
        }
    }

    public sealed class AgentContext
    {
        public ulong EntityId { get; }
        public Blackboard Blackboard { get; } = new Blackboard();

        public IAgentBody Body { get; }
        public IAwarenessModel Awareness { get; }
        public INavigationService Navigation { get; }
        public IRegionGraph Regions { get; }
        public Pathfinding.PathFollower Follower { get; }
        public AgentArchetypeConfig Archetype { get; }
        public IEventBus Events { get; }

        public PathRequestSlot Path { get; }

        /// <summary>Refreshed by the agent each tick from the Director.</summary>
        public DirectorIntent Intent { get; set; } = DirectorIntent.Default;

        /// <summary>Current tick's time. Set by the agent before ticking the FSM.</summary>
        public SimTime Time { get; set; }

        public DeterministicRandom Random;

        /// <summary>Seed cursor handed to navigation helpers so their randomness stays in our stream.</summary>
        public DeterministicSeed NavSeed;

        /// <summary>Home position used by the leash behaviour.</summary>
        public float3 TerritoryCenter { get; set; }

        /// <summary>Set by the agent when a link traversal should begin.</summary>
        public INavLinkAction PendingLink { get; set; }

        public float3 Position => Body.Position;

        public AgentContext(
            ulong entityId,
            IAgentBody body,
            IAwarenessModel awareness,
            INavigationService navigation,
            IRegionGraph regions,
            Pathfinding.PathFollower follower,
            AgentArchetypeConfig archetype,
            IEventBus events,
            uint seed)
        {
            EntityId = entityId;
            Body = body;
            Awareness = awareness;
            Navigation = navigation;
            Regions = regions;
            Follower = follower;
            Archetype = archetype;
            Events = events;

            Random = DeterministicRandom.ForEntity(seed, entityId, 0xA17u);
            NavSeed = new DeterministicSeed(Random.NextUInt());
            Path = new PathRequestSlot(navigation);
            TerritoryCenter = body != null ? body.Position : float3.zero;
        }

        // --------------------------------------------------------------- helpers

        /// <summary>Repath cadence for the agent's current awareness band.</summary>
        public float RepathInterval
        {
            get
            {
                AwarenessLevel level = Awareness != null ? Awareness.PeakLevel : AwarenessLevel.Unaware;
                NavigationConfig nav = Archetype != null ? Archetype.Navigation : null;
                return nav != null ? nav.RepathInterval(level) : 1f;
            }
        }

        /// <summary>
        /// Drives a move toward a goal. Returns true once a usable corridor exists
        /// and the follower is producing motion.
        /// </summary>
        public bool MoveTo(float3 goal, AgentMoveState moveState, PathPriority priority = PathPriority.Normal)
        {
            SimTime time = Time;

            if (Path.Update(Body.Position, goal, EntityId, in time, RepathInterval, priority))
            {
                Follower.SetPath(Path.Result);
                Blackboard.Set(BBKeys.HasPath, true, time.Tick);
                Blackboard.Set(BBKeys.PathFailed, false, time.Tick);
            }
            else if (Path.LastStatus == PathQueryStatus.Failed)
            {
                Blackboard.Set(BBKeys.PathFailed, true, time.Tick);
            }

            Blackboard.Set(BBKeys.MoveGoal, goal, time.Tick);

            if (!Follower.HasPath) return false;

            float speed = SpeedFor(moveState);
            float3 desired = Follower.Evaluate(Body.Position, speed, in time);

            Blackboard.Set(BBKeys.RemainingDistance, Follower.RemainingDistance, time.Tick);

            if (math.lengthsq(desired) > 1e-4f)
            {
                Body.Move(desired, in time);
                Body.FaceDirection(math.normalize(desired), in time);
                return true;
            }

            return false;
        }

        public float SpeedFor(AgentMoveState moveState)
        {
            float baseSpeed = Archetype != null ? Archetype.MoveSpeed(moveState) : 2f;
            return baseSpeed * math.clamp(Intent.SpeedScale, 0.25f, 2f);
        }

        public void StopMoving()
        {
            Body.Move(float3.zero, Time);
            Follower.ClearPath();
            Path.Cancel();
            Blackboard.Set(BBKeys.HasPath, false, Time.Tick);
        }

        public void FaceTowards(float3 worldPosition)
        {
            float3 dir = worldPosition - Body.Position;
            dir.y = 0f;
            if (math.lengthsq(dir) < 1e-4f) return;

            SimTime time = Time;
            Body.FaceDirection(math.normalize(dir), in time);
        }

        /// <summary>True when a straight walkable line exists — lets states skip a full path query.</summary>
        public bool HasDirectLine(float3 to) => Navigation != null && Navigation.HasStraightPath(Body.Position, to, NavArea.MaskAll);

        public bool CanReach(float3 goal, out float3 snapped)
        {
            float snap = Archetype != null && Archetype.Navigation != null ? Archetype.Navigation.SnapRadius : 2f;
            return Navigation.SamplePosition(goal, snap, NavArea.MaskAll, out snapped);
        }

        public int CurrentRegion => Regions != null ? Regions.GetRegionAt(Body.Position) : 0;

        /// <summary>Convenience: the agent's best current target, if any.</summary>
        public bool TryGetTarget(out PerceivedTarget target)
        {
            if (Awareness != null) return Awareness.TryGetPrimaryTarget(out target);
            target = default;
            return false;
        }

        public AwarenessLevel AwarenessLevel => Awareness != null ? Awareness.PeakLevel : AwarenessLevel.Unaware;

        public bool IsStunned
        {
            get => Blackboard.Get(BBKeys.IsStunned);
            set => Blackboard.Set(BBKeys.IsStunned, value, Time.Tick);
        }

        public void Emit(StimulusChannel channel, StimulusTag tag, float intensity, float radius)
        {
            Stimulus s = new Stimulus(channel, tag, Body.Position, intensity, radius, EntityId, Time.Tick, Body.Velocity);
            Body.EmitStimulus(in s);
        }

        public void LogState(string message)
        {
            if (VLog.Is(LogCat.Fsm)) VLog.Info(LogCat.Fsm, $"Agent {EntityId}: {message}");
        }
    }
}
