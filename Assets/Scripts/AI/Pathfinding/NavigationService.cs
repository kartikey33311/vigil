// -----------------------------------------------------------------------------
// Vigil — asynchronous navigation service.
//
// Every query is request/poll. Nothing here blocks, for two reasons:
//
//   1. A synchronous NavMesh.CalculatePath across 40 agents is a guaranteed frame
//      spike — the cost lands entirely on whichever frame the agents happen to
//      repath on.
//   2. More subtly, a blocking API makes worst-case cost UNBOUNDABLE. The whole
//      point of the budgeted tick scheduler is that adding agents degrades update
//      frequency rather than frame time, and a blocking path call defeats that.
//
// Unity's NavMesh API is main-thread-only, so "async" here means TIME-SLICED, not
// threaded: the pending queue is drained under a millisecond budget each tick and
// the remainder carries to the next. That gives the same bounded-cost guarantee
// without fighting the engine's threading rules.
//
// Handles are (Slot, Version). Recycling a slot bumps its version, so a poll from
// a despawned agent fails cleanly instead of silently reading whichever agent
// inherited the slot — a bug class that is essentially undebuggable once it ships.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AI;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Mathx;
using Vigil.Core.Pooling;
using Vigil.Core.Simulation;
using Vigil.Data;

namespace Vigil.AI.Pathfinding
{
    public sealed class NavigationService : INavigationService, ITickable
    {
        enum SlotState : byte
        {
            Free = 0,
            Queued = 1,
            Complete = 2
        }

        struct Slot
        {
            public int Version;
            public SlotState State;
            public NavPathRequest Request;
            public NavMeshPath Scratch;
            public NavPath Result;
        }

        readonly NavigationConfig _config;
        readonly Slot[] _slots;
        readonly Pool<NavPath> _pathPool;
        readonly Stack<int> _freeSlots;

        // One FIFO per priority band. A comparison-based heap would be tidier but
        // allocates on resize and reorders equal-priority requests unpredictably;
        // four small queues are faster and keep request order stable, which matters
        // because unstable ordering makes repro cases non-deterministic.
        readonly Queue<int>[] _pending;

        readonly Vector3[] _cornerScratch = new Vector3[NavPath.MaxCorners];
        readonly Stopwatch _watch = new Stopwatch();

        int _currentTick;

        public int PendingQueryCount { get; private set; }

        /// <summary>Queries completed since construction. Telemetry for the debug overlay.</summary>
        public int CompletedQueryCount { get; private set; }

        /// <summary>Requests rejected because every slot was in use.</summary>
        public int RejectedQueryCount { get; private set; }

        public NavigationService(NavigationConfig config)
        {
            _config = config;

            int capacity = config != null ? math.max(4, config.MaxConcurrentQueries) : 24;
            _slots = new Slot[capacity];
            _freeSlots = new Stack<int>(capacity);

            for (int i = capacity - 1; i >= 0; i--)
            {
                _slots[i] = new Slot
                {
                    Version = 1,
                    State = SlotState.Free,
                    Scratch = new NavMeshPath()
                };
                _freeSlots.Push(i);
            }

            _pending = new Queue<int>[4];
            for (int i = 0; i < _pending.Length; i++) _pending[i] = new Queue<int>(capacity);

            _pathPool = new Pool<NavPath>(() => new NavPath(), prewarm: capacity, maxRetained: capacity * 2,
                onReturn: p => p.Clear());
        }

        // ---------------------------------------------------------------- requests

        public PathHandle RequestPath(in NavPathRequest request)
        {
            if (_freeSlots.Count == 0)
            {
                // Saturation is a legitimate runtime condition under load, not an
                // error. Callers degrade (keep the old corridor, retry next tick);
                // nothing blocks and nothing throws.
                RejectedQueryCount++;
                return PathHandle.Invalid;
            }

            int slot = _freeSlots.Pop();

            _slots[slot].State = SlotState.Queued;
            _slots[slot].Request = request;

            int band = math.clamp((int)request.Priority, 0, _pending.Length - 1);
            _pending[band].Enqueue(slot);
            PendingQueryCount++;

            return new PathHandle(slot, _slots[slot].Version);
        }

        public PathQueryStatus Poll(PathHandle handle, NavPath into)
        {
            if (!TryResolve(handle, out int slot)) return PathQueryStatus.Idle;

            switch (_slots[slot].State)
            {
                case SlotState.Queued:
                    return PathQueryStatus.Pending;

                case SlotState.Complete:
                {
                    NavPath result = _slots[slot].Result;
                    PathQueryStatus status = result != null ? result.Status : PathQueryStatus.Failed;
                    into?.CopyFrom(result);
                    Retire(slot);
                    return status;
                }

                default:
                    return PathQueryStatus.Idle;
            }
        }

        public void Cancel(PathHandle handle)
        {
            if (!TryResolve(handle, out int slot)) return;
            if (_slots[slot].State == SlotState.Queued) PendingQueryCount--;
            Retire(slot);
        }

        public void CancelAllFor(ulong requesterId)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].State == SlotState.Free) continue;
                if (_slots[i].Request.RequesterId != requesterId) continue;

                if (_slots[i].State == SlotState.Queued) PendingQueryCount--;
                Retire(i);
            }
        }

        bool TryResolve(PathHandle handle, out int slot)
        {
            slot = handle.Slot;
            if (slot < 0 || slot >= _slots.Length) return false;
            // The version check is the whole point of the handle design.
            return _slots[slot].Version == handle.Version && _slots[slot].State != SlotState.Free;
        }

        void Retire(int slot)
        {
            if (_slots[slot].Result != null)
            {
                _pathPool.Return(_slots[slot].Result);
                _slots[slot].Result = null;
            }

            _slots[slot].State = SlotState.Free;
            _slots[slot].Request = default;

            // Bump on retire so any handle still held by a caller is now stale.
            unchecked { _slots[slot].Version++; }
            if (_slots[slot].Version == 0) _slots[slot].Version = 1;

            _freeSlots.Push(slot);
        }

        // ------------------------------------------------------------------- tick

        public void OnSimTick(in SimTime time)
        {
            _currentTick = time.Tick;

            double budgetMs = _config != null ? _config.QueryBudgetMs : 1.2f;
            _watch.Restart();

            // Immediate bypasses the budget entirely — reserved for the handful of
            // cases (a chase committing to a lunge) where a one-tick delay is
            // visible to the player.
            Queue<int> immediate = _pending[(int)PathPriority.Immediate];
            while (immediate.Count > 0)
            {
                Service(immediate.Dequeue());
            }

            // Then High -> Normal -> Low under budget.
            for (int band = (int)PathPriority.High; band >= (int)PathPriority.Low; band--)
            {
                Queue<int> queue = _pending[band];
                while (queue.Count > 0)
                {
                    if (_watch.Elapsed.TotalMilliseconds >= budgetMs) return;
                    Service(queue.Dequeue());
                }
            }
        }

        void Service(int slot)
        {
            // Cancelled while queued — the slot was already recycled.
            if (_slots[slot].State != SlotState.Queued) return;

            PendingQueryCount--;

            NavPathRequest request = _slots[slot].Request;
            NavPath result = _pathPool.Rent();
            result.Clear();
            result.ComputedTick = _currentTick;

            int areaMask = request.AreaMask == 0 ? NavArea.MaskAll : request.AreaMask;
            float snap = request.SnapRadius > 0f ? request.SnapRadius : (_config != null ? _config.SnapRadius : 2f);

            // Project both endpoints. A goal a few centimetres off the mesh is the
            // single most common cause of "the AI just refuses to move", so we snap
            // rather than fail.
            if (!SamplePosition(request.Start, snap, areaMask, out float3 start) ||
                !SamplePosition(request.Goal, snap, areaMask, out float3 goal))
            {
                result.Status = PathQueryStatus.Failed;
                Complete(slot, result);
                return;
            }

            NavMeshPath scratch = _slots[slot].Scratch;
            bool computed = NavMesh.CalculatePath(start, goal, areaMask, scratch);

            if (!computed || scratch.status == NavMeshPathStatus.PathInvalid)
            {
                result.Status = PathQueryStatus.Failed;
                Complete(slot, result);
                return;
            }

            int count = scratch.GetCornersNonAlloc(_cornerScratch);
            if (count <= 0)
            {
                result.Status = PathQueryStatus.Failed;
                Complete(slot, result);
                return;
            }

            float length = 0f;
            for (int i = 0; i < count; i++)
            {
                float3 corner = _cornerScratch[i];
                result.Corners[i] = corner;
                result.CornerAreas[i] = NavArea.Walkable;
                if (i > 0) length += math.distance(result.Corners[i - 1], corner);
            }

            result.CornerCount = count;
            result.TotalLength = length;
            result.EffectiveGoal = result.Corners[count - 1];

            float maxLength = request.MaxPathLength > 0f
                ? request.MaxPathLength
                : (_config != null ? _config.MaxPathLength : 500f);

            if (length > maxLength)
            {
                // Reject rather than truncate: a truncated corridor sends the agent
                // confidently in the wrong direction, which reads far worse than it
                // choosing to do something else.
                result.Status = PathQueryStatus.Failed;
                Complete(slot, result);
                return;
            }

            bool partial = scratch.status == NavMeshPathStatus.PathPartial;
            bool acceptPartial = _config == null || _config.AcceptPartialPaths;

            result.Status = partial
                ? (acceptPartial ? PathQueryStatus.PartialSuccess : PathQueryStatus.Failed)
                : PathQueryStatus.Success;

            Complete(slot, result);
        }

        void Complete(int slot, NavPath result)
        {
            _slots[slot].Result = result;
            _slots[slot].State = SlotState.Complete;
            CompletedQueryCount++;
        }

        // -------------------------------------------------------------- sampling

        public bool SamplePosition(float3 position, float maxDistance, int areaMask, out float3 result)
        {
            if (NavMesh.SamplePosition(position, out NavMeshHit hit, maxDistance, areaMask))
            {
                result = hit.position;
                return true;
            }

            result = position;
            return false;
        }

        public bool HasStraightPath(float3 from, float3 to, int areaMask)
        {
            // NavMesh.Raycast returns TRUE when the ray was BLOCKED — the inversion
            // here is deliberate and is a classic source of inverted-logic bugs.
            return !NavMesh.Raycast(from, to, out NavMeshHit _, areaMask);
        }

        public bool TryFindPointAwayFrom(
            float3 origin, float3 avoid, float desiredDistance, int areaMask,
            ref DeterministicSeed seed, out float3 result)
        {
            int candidates = _config != null ? _config.SampleCandidates : 16;
            float snap = _config != null ? _config.SnapRadius : 2f;

            DeterministicRandom rng = new DeterministicRandom(seed.Value);

            float3 away = origin - avoid;
            float awayLenSq = math.lengthsq(away);
            float3 preferred = awayLenSq > 1e-4f ? math.normalize(away) : rng.NextDirectionXZ();
            preferred.y = 0f;

            float bestScore = float.NegativeInfinity;
            float3 best = origin;
            bool found = false;

            for (int i = 0; i < candidates; i++)
            {
                // Bias the cone toward "directly away" but keep enough spread that
                // fleeing does not always produce the same straight line.
                float spread = math.lerp(0.2f, 1.6f, rng.NextFloat());
                float3 jitter = rng.NextDirectionXZ() * spread;
                float3 dir = math.normalizesafe(preferred + jitter, preferred);

                float3 candidate = origin + dir * desiredDistance * rng.NextFloat(0.7f, 1.25f);

                if (!SamplePosition(candidate, snap, areaMask, out float3 onMesh)) continue;

                float distanceFromThreat = math.distance(onMesh, avoid);
                float travel = math.distance(onMesh, origin);

                // Reward getting away; mildly penalise having to travel a long way
                // to do it, so the agent does not sprint across the whole level.
                float score = distanceFromThreat - travel * 0.35f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = onMesh;
                    found = true;
                }
            }

            seed.Value = rng.NextUInt();
            result = best;
            return found;
        }

        public bool TryFindConcealedPoint(
            float3 origin, float3 hideFrom, float searchRadius, int areaMask,
            ref DeterministicSeed seed, out float3 result)
        {
            int candidates = _config != null ? _config.SampleCandidates : 16;
            float snap = _config != null ? _config.SnapRadius : 2f;
            int mask = _config != null ? _config.ConcealmentMask.value : ~0;

            DeterministicRandom rng = new DeterministicRandom(seed.Value);

            float bestScore = float.NegativeInfinity;
            float3 best = origin;
            bool found = false;

            for (int i = 0; i < candidates; i++)
            {
                float3 candidate = origin + rng.NextPointInDiscXZ(searchRadius);
                if (!SamplePosition(candidate, snap, areaMask, out float3 onMesh)) continue;

                // Sample at roughly chest height — a point is only concealed if the
                // agent's BODY is hidden, not its feet.
                Vector3 eye = (Vector3)onMesh + Vector3.up * 1.2f;
                Vector3 threatEye = (Vector3)hideFrom + Vector3.up * 1.6f;

                bool blocked = Physics.Linecast(eye, threatEye, mask, QueryTriggerInteraction.Ignore);
                if (!blocked) continue;

                // Among concealed points, prefer ones that stay CLOSE to the threat.
                // Stalking means "hidden but near", not "hidden and gone" — the
                // latter is just leaving.
                float distanceToThreat = math.distance(onMesh, hideFrom);
                float score = -distanceToThreat;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = onMesh;
                    found = true;
                }
            }

            seed.Value = rng.NextUInt();
            result = best;

            if (!found && VLog.Is(LogCat.Pathfinding))
            {
                VLog.Info(LogCat.Pathfinding, $"No concealed point found within {searchRadius:F1}m of {origin}.");
            }

            return found;
        }
    }
}
