// -----------------------------------------------------------------------------
// Vigil — navigation contracts.
//
// TWO TIERS, because one graph cannot serve both jobs:
//
//   Tier 1 — NavMesh (fine).   Metre-scale locomotion: corners, funnel-smoothed
//                              corridors, off-mesh links for vents and windows,
//                              dynamic carving for doors and debris.
//
//   Tier 2 — Region graph (coarse). Room-scale strategy: "which rooms connect to
//                              the room I lost him in, and which have I not swept
//                              yet?" Running that reasoning on raw NavMesh polys
//                              is both too slow and too granular to be legible —
//                              players read the monster as smart when it clears
//                              ROOMS in a sensible order, not when it walks a
//                              mathematically optimal polygon sequence.
//
// ASYNC BY CONSTRUCTION. Every query returns a handle and is polled. A blocking
// NavMesh.CalculatePath on 40 agents is a guaranteed frame spike; more subtly, a
// blocking API makes it impossible to bound worst-case cost, which is the whole
// point of the tick scheduler. Nothing in this interface may block.
// -----------------------------------------------------------------------------

using Unity.Mathematics;

namespace Vigil.Core.Contracts
{
    public enum PathQueryStatus : byte
    {
        /// <summary>Handle is not associated with a live query.</summary>
        Idle = 0,

        /// <summary>Queued or in progress.</summary>
        Pending = 1,

        /// <summary>Complete path to the requested goal.</summary>
        Success = 2,

        /// <summary>
        /// Path reaches only part-way (goal unreachable or off-mesh). Still usable —
        /// an agent that walks as far as it can toward you reads far better than one
        /// that gives up and stands still.
        /// </summary>
        PartialSuccess = 3,

        /// <summary>No path exists, or start/goal could not be projected onto the mesh.</summary>
        Failed = 4,

        Cancelled = 5
    }

    public enum PathPriority : byte
    {
        /// <summary>Ambient repathing. May be delayed several ticks under load.</summary>
        Low = 0,

        /// <summary>Default.</summary>
        Normal = 1,

        /// <summary>Active pursuit. Serviced before Normal.</summary>
        High = 2,

        /// <summary>Serviced this tick regardless of budget. Use sparingly.</summary>
        Immediate = 3
    }

    /// <summary>
    /// Opaque, versioned handle to an in-flight query. The version guards against
    /// the classic use-after-free: an agent despawns, its slot is recycled, and a
    /// stale poll from the old owner silently reads someone else's path.
    /// </summary>
    public readonly struct PathHandle : System.IEquatable<PathHandle>
    {
        public readonly int Slot;
        public readonly int Version;

        public PathHandle(int slot, int version)
        {
            Slot = slot;
            Version = version;
        }

        public static readonly PathHandle Invalid = new PathHandle(-1, 0);
        public bool IsValid => Slot >= 0;

        public bool Equals(PathHandle other) => Slot == other.Slot && Version == other.Version;
        public override bool Equals(object obj) => obj is PathHandle o && Equals(o);
        public override int GetHashCode() => (Slot * 397) ^ Version;
        public override string ToString() => IsValid ? $"Path[{Slot}.{Version}]" : "Path[invalid]";
    }

    /// <summary>Navigation area cost classes. Must mirror the NavMesh area indices.</summary>
    public static class NavArea
    {
        public const int Walkable = 0;
        public const int NotWalkable = 1;
        public const int Jump = 2;

        // Project-specific areas, baked in the editor generator.
        public const int Vent = 3;
        public const int Window = 4;
        public const int Door = 5;
        public const int Debris = 6;

        /// <summary>Loud surfaces — glass, metal grating. Agents prefer them when hunting.</summary>
        public const int Noisy = 7;

        public const int MaskAll = ~0;

        public static int Mask(params int[] areas)
        {
            int m = 0;
            for (int i = 0; i < areas.Length; i++) m |= 1 << areas[i];
            return m;
        }
    }

    public struct NavPathRequest
    {
        public float3 Start;
        public float3 Goal;

        /// <summary>Bitmask of traversable <see cref="NavArea"/> values.</summary>
        public int AreaMask;

        /// <summary>Entity requesting; used for cancellation on despawn and for debug.</summary>
        public ulong RequesterId;

        public PathPriority Priority;

        /// <summary>
        /// Abort if the resulting path exceeds this length. Stops an agent
        /// pathing 400 m around the building to reach a player two metres away
        /// through a wall.
        /// </summary>
        public float MaxPathLength;

        /// <summary>Radius used to project Start/Goal onto the mesh.</summary>
        public float SnapRadius;

        public static NavPathRequest Create(float3 start, float3 goal, ulong requesterId,
            PathPriority priority = PathPriority.Normal, int areaMask = NavArea.MaskAll)
        {
            return new NavPathRequest
            {
                Start = start,
                Goal = goal,
                AreaMask = areaMask,
                RequesterId = requesterId,
                Priority = priority,
                MaxPathLength = 500f,
                SnapRadius = 2f
            };
        }
    }

    /// <summary>
    /// Result corridor. Pooled and reused — treat instances as borrowed, never
    /// cache one past the tick you read it on without calling <see cref="CopyFrom"/>.
    /// </summary>
    public sealed class NavPath
    {
        public const int MaxCorners = 128;

        public readonly float3[] Corners = new float3[MaxCorners];
        public readonly int[] CornerAreas = new int[MaxCorners];

        public int CornerCount;
        public PathQueryStatus Status = PathQueryStatus.Idle;
        public float TotalLength;

        /// <summary>Tick the path was computed on — used to age out stale corridors.</summary>
        public int ComputedTick;

        /// <summary>Goal actually reached, which differs from the request on PartialSuccess.</summary>
        public float3 EffectiveGoal;

        public bool IsUsable => (Status == PathQueryStatus.Success || Status == PathQueryStatus.PartialSuccess)
                                && CornerCount > 0;

        public void Clear()
        {
            CornerCount = 0;
            Status = PathQueryStatus.Idle;
            TotalLength = 0f;
            ComputedTick = 0;
            EffectiveGoal = default;
        }

        public void CopyFrom(NavPath other)
        {
            if (other == null) { Clear(); return; }
            CornerCount = other.CornerCount;
            System.Array.Copy(other.Corners, Corners, CornerCount);
            System.Array.Copy(other.CornerAreas, CornerAreas, CornerCount);
            Status = other.Status;
            TotalLength = other.TotalLength;
            ComputedTick = other.ComputedTick;
            EffectiveGoal = other.EffectiveGoal;
        }

        /// <summary>Recomputes <see cref="TotalLength"/> from the corner list.</summary>
        public void RecalculateLength()
        {
            float len = 0f;
            for (int i = 1; i < CornerCount; i++) len += math.distance(Corners[i - 1], Corners[i]);
            TotalLength = len;
        }
    }

    /// <summary>
    /// Async navigation service. Implemented in Vigil.AI, resolved from the
    /// ServiceContext. Server-authoritative — clients never path.
    /// </summary>
    public interface INavigationService
    {
        /// <summary>Enqueues a query. Never blocks. Returns Invalid if the queue is saturated.</summary>
        PathHandle RequestPath(in NavPathRequest request);

        /// <summary>
        /// Polls a handle. On Success/PartialSuccess the result is written into
        /// <paramref name="into"/> and the handle is retired.
        /// </summary>
        PathQueryStatus Poll(PathHandle handle, NavPath into);

        void Cancel(PathHandle handle);

        /// <summary>Cancels every outstanding query owned by an entity (despawn path).</summary>
        void CancelAllFor(ulong requesterId);

        /// <summary>Projects a world position onto the navigable surface.</summary>
        bool SamplePosition(float3 position, float maxDistance, int areaMask, out float3 result);

        /// <summary>
        /// True if a straight walkable line exists between two points. Cheap;
        /// used to skip full path queries when the target is in the open.
        /// </summary>
        bool HasStraightPath(float3 from, float3 to, int areaMask);

        /// <summary>
        /// A reachable point roughly <paramref name="desiredDistance"/> away, biased
        /// away from <paramref name="avoid"/>. Backs flee and reposition behaviour.
        /// </summary>
        bool TryFindPointAwayFrom(float3 origin, float3 avoid, float desiredDistance, int areaMask, ref DeterministicSeed seed, out float3 result);

        /// <summary>
        /// A reachable point near <paramref name="origin"/> with no line of sight to
        /// <paramref name="hideFrom"/>. Backs ambush and stalk behaviour.
        /// </summary>
        bool TryFindConcealedPoint(float3 origin, float3 hideFrom, float searchRadius, int areaMask, ref DeterministicSeed seed, out float3 result);

        /// <summary>Outstanding query count (telemetry / tests).</summary>
        int PendingQueryCount { get; }
    }

    /// <summary>
    /// Seed cursor passed by ref into navigation helpers so their randomness stays
    /// inside the caller's deterministic stream instead of touching a global RNG.
    /// </summary>
    public struct DeterministicSeed
    {
        public uint Value;
        public DeterministicSeed(uint value) { Value = value; }
    }

    // -------------------------------------------------------------------------
    // Tier 2 — coarse region graph
    // -------------------------------------------------------------------------

    /// <summary>Region ids are small ints; 0 is reserved for "outside all regions".</summary>
    public struct RegionInfo
    {
        public int Id;
        public float3 Center;
        public float3 Extents;

        /// <summary>Human-readable label, e.g. "Boiler Room". Shown in AI debug HUD.</summary>
        public string DisplayName;

        /// <summary>Ambient darkness 0..1. Agents bias searches toward dark rooms.</summary>
        public float Darkness;

        /// <summary>How enclosed the region is, 0..1. Drives ambush site scoring.</summary>
        public float Enclosure;

        /// <summary>Count of exits. Dead ends (1) are prime ambush and panic sites.</summary>
        public int ExitCount;
    }

    /// <summary>
    /// Room-scale connectivity over the level, built at bake time from the NavMesh
    /// plus authored region volumes. Used for search planning, the AI director's
    /// spawn/approach decisions, and audio zoning.
    /// </summary>
    public interface IRegionGraph
    {
        int RegionCount { get; }

        /// <summary>Region containing a world position, or 0.</summary>
        int GetRegionAt(float3 position);

        bool TryGetRegion(int regionId, out RegionInfo info);

        /// <summary>Fills <paramref name="buffer"/> with adjacent region ids; returns the count written.</summary>
        int GetNeighbours(int regionId, int[] buffer);

        /// <summary>Coarse walking distance between region centres. Precomputed, O(1).</summary>
        float GetTravelCost(int fromRegion, int toRegion);

        /// <summary>
        /// Next region to step through on the coarse route. Lets an agent commit to
        /// a room-by-room sweep without recomputing a full fine path each time.
        /// </summary>
        bool TryGetNextRegionTowards(int fromRegion, int toRegion, out int nextRegion);

        /// <summary>Marks a region as swept at the given tick (search memory).</summary>
        void MarkSearched(int regionId, int tick);

        /// <summary>Tick a region was last swept, or int.MinValue if never.</summary>
        int GetLastSearchedTick(int regionId);
    }

    // -------------------------------------------------------------------------
    // Locomotion
    // -------------------------------------------------------------------------

    /// <summary>
    /// A special traversal (vent crawl, window vault, ledge drop). Off-mesh links
    /// are what stop the antagonist being confined to the same corridors as the
    /// player — the moment it comes through a vent, the space stops feeling safe.
    /// </summary>
    public interface INavLinkAction
    {
        /// <summary>Area index this action services, e.g. <see cref="NavArea.Vent"/>.</summary>
        int AreaType { get; }

        /// <summary>Seconds the traversal takes. Agent is committed and vulnerable during it.</summary>
        float Duration { get; }

        /// <summary>Animation state name triggered on entry.</summary>
        string AnimationState { get; }

        /// <summary>Evaluates the traversal; returns false when it should be aborted.</summary>
        bool Tick(float normalisedTime, out float3 position, out quaternion rotation);
    }

    /// <summary>Consumes a <see cref="NavPath"/> and produces steering output.</summary>
    public interface IPathFollower
    {
        bool HasPath { get; }
        float3 DesiredVelocity { get; }
        float RemainingDistance { get; }
        bool IsTraversingLink { get; }

        void SetPath(NavPath path);
        void ClearPath();
    }
}
