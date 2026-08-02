// -----------------------------------------------------------------------------
// Vigil — ambient and search behaviours.
//
// SearchState is the most important state in the game. Chases are memorable, but
// they are short and largely resolved by movement speed. The minutes the monster
// spends LOOKING for a player who is hiding two rooms away are where the tension
// actually lives, and they are entirely a function of how legible the search is.
// -----------------------------------------------------------------------------

using Unity.Mathematics;
using Vigil.Core.Contracts;
using Vigil.Core.StateMachine;
using Vigil.Core.Simulation;
using Vigil.Data;
using Vigil.AI.StateMachine;

namespace Vigil.AI.States
{
    /// <summary>
    /// Fully asleep. Costs nothing — the tick scheduler demotes agents here to
    /// Dormant budget, so a level can hold many more agents than are ever active.
    /// </summary>
    public sealed class DormantState : StateBase<AgentContext>
    {
        public override int Id => (int)AgentStateId.Dormant;
        public override string Name => "Dormant";

        public override void OnEnter(AgentContext ctx, in SimTime time)
        {
            ctx.StopMoving();
            ctx.Body.PlayAnimation("Dormant");
            ctx.LogState("entering dormancy");
        }

        public override StateStatus OnTick(AgentContext ctx, in SimTime time) => StateStatus.Running;
    }

    /// <summary>Awake but idle. Randomised look-around so it never reads as frozen.</summary>
    public sealed class IdleState : StateBase<AgentContext>
    {
        float _nextGlanceAt;
        float3 _glanceTarget;

        public override int Id => (int)AgentStateId.Idle;
        public override string Name => "Idle";

        public override void OnEnter(AgentContext ctx, in SimTime time)
        {
            ctx.StopMoving();
            ctx.Body.PlayAnimation("Idle");
            _nextGlanceAt = 0f;
        }

        public override StateStatus OnTick(AgentContext ctx, in SimTime time)
        {
            _nextGlanceAt -= time.DeltaTime;
            if (_nextGlanceAt > 0f) return StateStatus.Running;

            // Irregular intervals. A fixed cadence is read as a mechanism; an
            // irregular one is read as attention.
            _nextGlanceAt = ctx.Random.NextFloat(1.6f, 4.2f);
            _glanceTarget = ctx.Position + ctx.Random.NextDirectionXZ() * 6f;

            ctx.FaceTowards(_glanceTarget);
            return StateStatus.Running;
        }
    }

    /// <summary>
    /// Wanders between regions, biased toward the ones swept longest ago.
    ///
    /// <para>Explicitly NOT a fixed patrol route. A fixed loop is memorised by
    /// players within a single session, after which the monster becomes a moving
    /// obstacle with a published timetable rather than a threat. Weighting by
    /// staleness keeps coverage sensible while remaining unpredictable.</para>
    /// </summary>
    public sealed class PatrolState : StateBase<AgentContext>
    {
        readonly int[] _neighbourBuffer = new int[16];

        int _targetRegion;
        float3 _goal;
        bool _hasGoal;
        float _repickTimer;

        public override int Id => (int)AgentStateId.Patrol;
        public override string Name => "Patrol";

        public override void OnEnter(AgentContext ctx, in SimTime time)
        {
            _hasGoal = false;
            _repickTimer = 0f;
            ctx.Body.PlayAnimation("Walk");
            ctx.Path.Invalidate();
        }

        public override StateStatus OnTick(AgentContext ctx, in SimTime time)
        {
            _repickTimer -= time.DeltaTime;

            bool needNewGoal = !_hasGoal
                               || ctx.Follower.HasArrived
                               || ctx.Follower.IsStuck
                               || ctx.Blackboard.Get(BBKeys.PathFailed)
                               || _repickTimer <= 0f;

            if (needNewGoal)
            {
                if (!PickGoal(ctx)) return StateStatus.Failed;

                // Hard ceiling so a goal that turns out to be unreachable cannot
                // pin the agent forever.
                _repickTimer = 25f;
                ctx.Path.Invalidate();
                ctx.Blackboard.Set(BBKeys.PathFailed, false, time.Tick);
            }

            ctx.MoveTo(_goal, AgentMoveState.Patrol, PathPriority.Low);

            if (ctx.Regions != null && ctx.Follower.RemainingDistance < 4f)
            {
                ctx.Regions.MarkSearched(_targetRegion, time.Tick);
            }

            return StateStatus.Running;
        }

        bool PickGoal(AgentContext ctx)
        {
            if (ctx.Regions == null || ctx.Regions.RegionCount == 0)
            {
                // No region graph baked — fall back to wandering on the NavMesh so
                // the agent still behaves sanely in a test scene.
                float3 candidate = ctx.Position + ctx.Random.NextPointInDiscXZ(22f);
                if (!ctx.CanReach(candidate, out _goal)) return false;
                _hasGoal = true;
                return true;
            }

            int current = ctx.CurrentRegion;
            int count = ctx.Regions.GetNeighbours(current, _neighbourBuffer);

            int bestRegion = current;
            float bestScore = float.NegativeInfinity;

            for (int i = 0; i < count; i++)
            {
                int candidate = _neighbourBuffer[i];
                if (!ctx.Regions.TryGetRegion(candidate, out RegionInfo info)) continue;

                int lastSearched = ctx.Regions.GetLastSearchedTick(candidate);
                float staleness = lastSearched == int.MinValue
                    ? 10000f
                    : unchecked(ctx.Time.Tick - lastSearched);

                // Staleness dominates; darkness and a random nudge break ties so
                // two runs through the same level do not produce the same route.
                float score = staleness * 0.01f
                              + info.Darkness * 2.5f
                              + ctx.Random.NextFloat() * 3f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestRegion = candidate;
                }
            }

            if (!ctx.Regions.TryGetRegion(bestRegion, out RegionInfo target)) return false;

            // Aim at a random point inside the region, not its exact centre —
            // walking to the geometric middle of every room looks robotic.
            float3 jitter = ctx.Random.NextPointInDiscXZ(math.max(1f, math.min(target.Extents.x, target.Extents.z) * 0.6f));
            if (!ctx.CanReach(target.Center + jitter, out _goal))
            {
                if (!ctx.CanReach(target.Center, out _goal)) return false;
            }

            _targetRegion = bestRegion;
            _hasGoal = true;
            ctx.Blackboard.Set(BBKeys.TargetRegion, bestRegion, ctx.Time.Tick);
            return true;
        }
    }

    /// <summary>
    /// Moves to a specific point of interest and looks around. Entered from a noise
    /// that could not be attributed to a known target.
    /// </summary>
    public sealed class InvestigateState : StateBase<AgentContext>
    {
        float3 _point;
        bool _hasPoint;
        float _dwellRemaining;
        bool _arrived;

        public override int Id => (int)AgentStateId.Investigate;
        public override string Name => "Investigate";

        public override void OnEnter(AgentContext ctx, in SimTime time)
        {
            _arrived = false;
            _dwellRemaining = ctx.Archetype != null ? ctx.Archetype.InvestigateDwell : 3f;
            ctx.Path.Invalidate();
            ctx.Body.PlayAnimation("Alert");

            _hasPoint = ResolvePoint(ctx);
            if (_hasPoint) ctx.LogState($"investigating {_point}");
        }

        bool ResolvePoint(AgentContext ctx)
        {
            // Prefer a concrete target's last known position; fall back to an
            // anonymous investigation cue.
            if (ctx.TryGetTarget(out PerceivedTarget target) && target.Level >= AwarenessLevel.Suspicious)
            {
                return ctx.CanReach(target.LastKnownPosition, out _point);
            }

            if (ctx.Awareness != null && ctx.Awareness.TryGetInvestigationPoint(out float3 p, out _))
            {
                return ctx.CanReach(p, out _point);
            }

            return false;
        }

        public override StateStatus OnTick(AgentContext ctx, in SimTime time)
        {
            // Nothing to investigate, or the point is unreachable — succeed rather
            // than stall, so the parent transitions us onward. A state that can
            // silently do nothing forever is the worst failure mode in an FSM.
            if (!_hasPoint) return StateStatus.Succeeded;

            if (ctx.Blackboard.Get(BBKeys.PathFailed) || ctx.Follower.IsStuck)
            {
                ctx.Awareness?.TryGetInvestigationPoint(out _, out _);
                return StateStatus.Failed;
            }

            if (!_arrived)
            {
                ctx.MoveTo(_point, AgentMoveState.Investigate, PathPriority.Normal);

                if (ctx.Follower.HasArrived || math.distance(ctx.Position, _point) < 2f)
                {
                    _arrived = true;
                    ctx.StopMoving();
                    ctx.Body.PlayAnimation("LookAround");
                }

                return StateStatus.Running;
            }

            // Dwell and sweep. The pause matters: a monster that arrives and
            // immediately leaves reads as not having looked at all.
            _dwellRemaining -= time.DeltaTime;

            if ((int)(_dwellRemaining * 2f) % 2 == 0)
            {
                ctx.FaceTowards(_point + ctx.Random.NextDirectionXZ() * 5f);
            }

            if (_dwellRemaining <= 0f)
            {
                if (ctx.Awareness is Perception.AwarenessModel model)
                {
                    model.ClearInvestigationPointNear(_point, 4f);
                }

                ctx.Regions?.MarkSearched(ctx.CurrentRegion, time.Tick);
                return StateStatus.Succeeded;
            }

            return StateStatus.Running;
        }
    }

    /// <summary>
    /// The "I lost him" behaviour. Sweeps REGIONS outward from the last known
    /// position, ordered by staleness, darkness and enclosure.
    ///
    /// <para>Search radius scales with <see cref="PerceivedTarget.PositionConfidence"/>:
    /// the monster can be certain you exist while having no idea where you are, and
    /// that state is what produces a wide, frantic sweep rather than a beeline.</para>
    ///
    /// <para>It gives up after a bounded number of regions. The monster deciding to
    /// STOP looking is as important a beat as any chase — it is the moment the
    /// player is allowed to breathe, and without it the game has no rhythm.</para>
    /// </summary>
    public sealed class SearchState : StateBase<AgentContext>
    {
        readonly int[] _neighbourBuffer = new int[16];
        readonly int[] _visited = new int[16];

        int _visitedCount;
        int _currentTargetRegion;
        float3 _goal;
        bool _hasGoal;
        float _regionTimer;

        public override int Id => (int)AgentStateId.Search;
        public override string Name => "Search";

        public override void OnEnter(AgentContext ctx, in SimTime time)
        {
            _visitedCount = 0;
            _hasGoal = false;
            _regionTimer = 0f;
            ctx.Path.Invalidate();
            ctx.Body.PlayAnimation("Search");
            ctx.Blackboard.Set(BBKeys.SearchAttempts, 0, time.Tick);
            ctx.LogState("lost contact — beginning search");
        }

        public override void OnExit(AgentContext ctx, in SimTime time)
        {
            ctx.Blackboard.Set(BBKeys.SearchAttempts, _visitedCount, time.Tick);
        }

        public override StateStatus OnTick(AgentContext ctx, in SimTime time)
        {
            int maxRegions = ctx.Archetype != null ? ctx.Archetype.MaxSearchRegions : 5;

            if (_visitedCount >= maxRegions)
            {
                ctx.LogState($"search exhausted after {_visitedCount} regions — standing down");
                return StateStatus.Succeeded;
            }

            _regionTimer -= time.DeltaTime;

            bool needNewRegion = !_hasGoal
                                 || ctx.Follower.HasArrived
                                 || ctx.Follower.IsStuck
                                 || ctx.Blackboard.Get(BBKeys.PathFailed)
                                 || _regionTimer <= 0f;

            if (needNewRegion)
            {
                if (_hasGoal)
                {
                    ctx.Regions?.MarkSearched(_currentTargetRegion, time.Tick);
                    RecordVisited(_currentTargetRegion);
                }

                if (!PickSearchGoal(ctx))
                {
                    return StateStatus.Succeeded;
                }

                _regionTimer = 20f;
                ctx.Path.Invalidate();
                ctx.Blackboard.Set(BBKeys.PathFailed, false, time.Tick);
            }

            ctx.MoveTo(_goal, AgentMoveState.Search, PathPriority.Normal);
            return StateStatus.Running;
        }

        void RecordVisited(int regionId)
        {
            if (_visitedCount >= _visited.Length) return;
            _visited[_visitedCount++] = regionId;
        }

        bool AlreadyVisited(int regionId)
        {
            for (int i = 0; i < _visitedCount; i++)
            {
                if (_visited[i] == regionId) return true;
            }
            return false;
        }

        bool PickSearchGoal(AgentContext ctx)
        {
            float confidence = 0.3f;
            float3 anchor = ctx.Position;

            if (ctx.TryGetTarget(out PerceivedTarget target))
            {
                anchor = target.LastKnownPosition;
                confidence = target.PositionConfidence;
            }

            // No region graph: fall back to a confidence-scaled disc around the last
            // known position. Low confidence searches wide.
            if (ctx.Regions == null || ctx.Regions.RegionCount == 0)
            {
                float radius = math.lerp(24f, 6f, math.saturate(confidence));
                float3 candidate = anchor + ctx.Random.NextPointInDiscXZ(radius);
                if (!ctx.CanReach(candidate, out _goal)) return false;
                _hasGoal = true;
                return true;
            }

            int anchorRegion = ctx.Regions.GetRegionAt(anchor);
            int current = ctx.CurrentRegion;

            int count = ctx.Regions.GetNeighbours(current, _neighbourBuffer);

            int best = -1;
            float bestScore = float.NegativeInfinity;

            for (int i = 0; i < count; i++)
            {
                int candidate = _neighbourBuffer[i];
                if (AlreadyVisited(candidate)) continue;
                if (!ctx.Regions.TryGetRegion(candidate, out RegionInfo info)) continue;

                // Proximity to where contact was lost dominates while confidence is
                // high, and matters less as confidence decays.
                float travelFromAnchor = ctx.Regions.GetTravelCost(anchorRegion, candidate);
                if (float.IsInfinity(travelFromAnchor)) continue;

                float proximityScore = -travelFromAnchor * math.lerp(0.02f, 0.35f, math.saturate(confidence));

                int lastSearched = ctx.Regions.GetLastSearchedTick(candidate);
                float staleness = lastSearched == int.MinValue ? 10000f : unchecked(ctx.Time.Tick - lastSearched);

                // Dark, enclosed, dead-end rooms are where people hide. Checking them
                // first is what makes the search read as informed rather than random.
                float hidingScore = info.Darkness * 2f + info.Enclosure * 2f + (info.ExitCount <= 1 ? 1.5f : 0f);

                float score = proximityScore + staleness * 0.008f + hidingScore + ctx.Random.NextFloat() * 1.2f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            // Every neighbour already swept — widen to the anchor region itself.
            if (best < 0) best = anchorRegion;

            if (!ctx.Regions.TryGetRegion(best, out RegionInfo chosen)) return false;

            float spread = math.lerp(
                math.max(2f, math.min(chosen.Extents.x, chosen.Extents.z)),
                1.5f,
                math.saturate(confidence));

            float3 jittered = chosen.Center + ctx.Random.NextPointInDiscXZ(spread);

            if (!ctx.CanReach(jittered, out _goal) && !ctx.CanReach(chosen.Center, out _goal)) return false;

            _currentTargetRegion = best;
            _hasGoal = true;
            ctx.Blackboard.Set(BBKeys.TargetRegion, best, ctx.Time.Tick);
            return true;
        }
    }
}
