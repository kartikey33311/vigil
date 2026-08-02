// -----------------------------------------------------------------------------
// Vigil — corridor following and off-mesh link traversal.
//
// Turning a corner list into movement is where most AI locomotion visibly falls
// apart. Two failure modes this is built to avoid:
//
//   * CORNER SNAPPING. Steering straight at corner[i] and switching to corner[i+1]
//     on arrival produces a hard direction change every corner. The agent looks
//     like it is on rails. Lookahead blending fixes it: aim at a point projected
//     along the corridor rather than at the vertex itself.
//
//   * SILENT DEADLOCK. An agent wedged on geometry keeps a valid path and a valid
//     desired velocity forever, and simply never arrives. Nothing errors, so it can
//     survive to ship. IsStuck makes it observable so states can repath or give up.
// -----------------------------------------------------------------------------

using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Simulation;
using Vigil.Data;

namespace Vigil.AI.Pathfinding
{
    public sealed class PathFollower : IPathFollower
    {
        readonly NavPath _path = new NavPath();
        readonly NavigationConfig _config;

        int _cursor;
        float3 _lastPosition;
        float _distanceAtLastCheck;
        int _noProgressTicks;

        INavLinkAction _activeLink;
        float _linkElapsed;

        public bool HasPath => _path.IsUsable && _cursor < _path.CornerCount;
        public float3 DesiredVelocity { get; private set; }
        public float RemainingDistance { get; private set; }
        public bool IsTraversingLink => _activeLink != null;

        /// <summary>True once the agent has made no forward progress for StuckTicks.</summary>
        public bool IsStuck { get; private set; }

        /// <summary>True when the final corner has been reached.</summary>
        public bool HasArrived { get; private set; }

        /// <summary>Tick the current corridor was computed on — used to age out stale paths.</summary>
        public int PathAgeTicks(int currentTick) => _path.IsUsable ? unchecked(currentTick - _path.ComputedTick) : int.MaxValue;

        /// <summary>Position/rotation while traversing a link; otherwise the last evaluated position.</summary>
        public float3 LinkPosition { get; private set; }
        public quaternion LinkRotation { get; private set; } = quaternion.identity;

        public PathFollower(NavigationConfig config)
        {
            _config = config;
            LinkRotation = quaternion.identity;
        }

        public void SetPath(NavPath path)
        {
            _path.CopyFrom(path);
            _cursor = 0;
            _noProgressTicks = 0;
            IsStuck = false;
            HasArrived = false;
            _distanceAtLastCheck = float.MaxValue;

            // Skip the first corner when it is effectively where we already stand.
            // NavMesh.CalculatePath always emits the start position as corner 0, and
            // steering toward it produces a visible stutter on every repath.
            if (_path.CornerCount > 1 && math.distancesq(_path.Corners[0], _lastPosition) < 0.09f)
            {
                _cursor = 1;
            }
        }

        public void ClearPath()
        {
            _path.Clear();
            _cursor = 0;
            DesiredVelocity = float3.zero;
            RemainingDistance = 0f;
            IsStuck = false;
            HasArrived = false;
            _activeLink = null;
        }

        /// <summary>
        /// Advances the follower one tick. Returns the desired velocity, which the
        /// steering solver then blends with avoidance and separation.
        /// </summary>
        public float3 Evaluate(float3 currentPosition, float maxSpeed, in SimTime time)
        {
            _lastPosition = currentPosition;

            if (_activeLink != null)
            {
                EvaluateLink(time);
                return DesiredVelocity;
            }

            if (!_path.IsUsable || _cursor >= _path.CornerCount)
            {
                DesiredVelocity = float3.zero;
                RemainingDistance = 0f;
                return DesiredVelocity;
            }

            float arrival = _config != null ? _config.ArrivalRadius : 0.6f;
            float lookahead = _config != null ? _config.CornerLookahead : 1.4f;

            // Consume every corner already within the arrival radius. Doing this in a
            // loop rather than one-per-tick matters when corners bunch up at a doorway.
            while (_cursor < _path.CornerCount)
            {
                float3 corner = _path.Corners[_cursor];
                if (DistanceXZ(currentPosition, corner) > arrival) break;
                _cursor++;
            }

            if (_cursor >= _path.CornerCount)
            {
                HasArrived = true;
                DesiredVelocity = float3.zero;
                RemainingDistance = 0f;
                return DesiredVelocity;
            }

            float3 target = _path.Corners[_cursor];

            // Blend toward the NEXT corner proportionally to how close we are to this
            // one. This is what rounds the turn instead of pivoting on the vertex.
            if (_cursor + 1 < _path.CornerCount)
            {
                float distToCorner = DistanceXZ(currentPosition, target);
                if (distToCorner < lookahead)
                {
                    float blend = 1f - math.saturate(distToCorner / math.max(0.01f, lookahead));
                    target = math.lerp(target, _path.Corners[_cursor + 1], blend * 0.65f);
                }
            }

            float3 toTarget = target - currentPosition;
            toTarget.y = 0f;

            float dist = math.length(toTarget);
            float3 direction = dist > 1e-4f ? toTarget / dist : float3.zero;

            // Ease into the final corner so the agent settles rather than overshooting
            // and jittering back.
            float speed = maxSpeed;
            if (_cursor == _path.CornerCount - 1)
            {
                speed = math.min(maxSpeed, maxSpeed * math.saturate(dist / math.max(0.01f, arrival * 2f)));
            }

            DesiredVelocity = direction * speed;
            RemainingDistance = ComputeRemaining(currentPosition);

            UpdateStuckDetection();

            return DesiredVelocity;
        }

        void UpdateStuckDetection()
        {
            float epsilon = _config != null ? _config.StuckProgressEpsilon : 0.05f;
            int limit = _config != null ? _config.StuckTicks : 24;

            if (_distanceAtLastCheck - RemainingDistance > epsilon)
            {
                _noProgressTicks = 0;
                IsStuck = false;
            }
            else
            {
                _noProgressTicks++;
                if (_noProgressTicks >= limit && !IsStuck)
                {
                    IsStuck = true;
                    if (VLog.Is(LogCat.Pathfinding))
                    {
                        VLog.Warn(LogCat.Pathfinding, $"PathFollower stuck: {RemainingDistance:F2}m remaining, no progress for {limit} ticks.");
                    }
                }
            }

            _distanceAtLastCheck = RemainingDistance;
        }

        float ComputeRemaining(float3 from)
        {
            if (_cursor >= _path.CornerCount) return 0f;

            float total = DistanceXZ(from, _path.Corners[_cursor]);
            for (int i = _cursor + 1; i < _path.CornerCount; i++)
            {
                total += math.distance(_path.Corners[i - 1], _path.Corners[i]);
            }
            return total;
        }

        static float DistanceXZ(float3 a, float3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return math.sqrt(dx * dx + dz * dz);
        }

        // ------------------------------------------------------------------ links

        /// <summary>Begins an off-mesh traversal. The agent is committed until it completes.</summary>
        public void BeginLink(INavLinkAction action)
        {
            _activeLink = action;
            _linkElapsed = 0f;
            DesiredVelocity = float3.zero;
        }

        void EvaluateLink(in SimTime time)
        {
            _linkElapsed += time.DeltaTime;

            float duration = math.max(0.01f, _activeLink.Duration);
            float t = math.saturate(_linkElapsed / duration);

            if (!_activeLink.Tick(t, out float3 position, out quaternion rotation) || t >= 1f)
            {
                _activeLink = null;
                DesiredVelocity = float3.zero;
                return;
            }

            LinkPosition = position;
            LinkRotation = rotation;
            DesiredVelocity = float3.zero;
        }
    }

    // -------------------------------------------------------------------------
    // Link actions
    // -------------------------------------------------------------------------

    /// <summary>
    /// Slow prone crawl through a vent. Deliberately the longest traversal in the
    /// game: it is the antagonist's most powerful movement option (it bypasses every
    /// door the players barricaded), so it has to carry a real commitment cost.
    /// </summary>
    public sealed class VentLinkAction : INavLinkAction
    {
        float3 _start, _end;

        public int AreaType => NavArea.Vent;
        public float Duration { get; private set; }
        public string AnimationState => "VentCrawl";

        public VentLinkAction(float3 start, float3 end, float duration)
        {
            _start = start;
            _end = end;
            Duration = duration;
        }

        public bool Tick(float t, out float3 position, out quaternion rotation)
        {
            position = math.lerp(_start, _end, t);

            float3 forward = _end - _start;
            forward.y = 0f;
            rotation = math.lengthsq(forward) > 1e-4f
                ? quaternion.LookRotationSafe(math.normalize(forward), math.up())
                : quaternion.identity;

            return true;
        }
    }

    /// <summary>Fast vault through a window. Parabolic so it reads as a leap, not a slide.</summary>
    public sealed class WindowVaultAction : INavLinkAction
    {
        readonly float3 _start, _end;
        readonly float _apex;

        public int AreaType => NavArea.Window;
        public float Duration { get; }
        public string AnimationState => "WindowVault";

        public WindowVaultAction(float3 start, float3 end, float duration, float apexHeight = 1.1f)
        {
            _start = start;
            _end = end;
            Duration = duration;
            _apex = apexHeight;
        }

        public bool Tick(float t, out float3 position, out quaternion rotation)
        {
            float3 flat = math.lerp(_start, _end, t);

            // 4t(1-t) peaks at 1.0 when t = 0.5 — a clean unit parabola.
            flat.y += _apex * 4f * t * (1f - t);
            position = flat;

            float3 forward = _end - _start;
            forward.y = 0f;
            rotation = math.lengthsq(forward) > 1e-4f
                ? quaternion.LookRotationSafe(math.normalize(forward), math.up())
                : quaternion.identity;

            return true;
        }
    }

    /// <summary>Gravity-accelerated drop from a ledge.</summary>
    public sealed class LedgeDropAction : INavLinkAction
    {
        readonly float3 _start, _end;

        public int AreaType => NavArea.Jump;
        public float Duration { get; }
        public string AnimationState => "LedgeDrop";

        public LedgeDropAction(float3 start, float3 end, float duration)
        {
            _start = start;
            _end = end;
            Duration = duration;
        }

        public bool Tick(float t, out float3 position, out quaternion rotation)
        {
            // Horizontal travel is linear; vertical accelerates. A linear drop reads
            // as floating, which instantly breaks the sense of weight.
            float3 horizontal = math.lerp(_start, _end, t);
            float fall = t * t;

            position = new float3(horizontal.x, math.lerp(_start.y, _end.y, fall), horizontal.z);

            float3 forward = _end - _start;
            forward.y = 0f;
            rotation = math.lengthsq(forward) > 1e-4f
                ? quaternion.LookRotationSafe(math.normalize(forward), math.up())
                : quaternion.identity;

            return true;
        }
    }
}
