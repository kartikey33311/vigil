// -----------------------------------------------------------------------------
// Vigil — pursuit, engagement and disengagement.
//
// The two states that matter most here are the ones that make the monster WORSE
// at winning:
//
//   AttackState has a commitment window during which the agent cannot turn. If it
//   could track through its own strike, the optimal player input would be "hold W"
//   and the encounter would have no texture at all.
//
//   RetreatState exists because the Director forces disengagement on a timer even
//   when the monster is winning. Sustained pressure produces adaptation, and
//   adapted players are not frightened — they are annoyed.
// -----------------------------------------------------------------------------

using Unity.Mathematics;
using Vigil.Core.Contracts;
using Vigil.Core.Events;
using Vigil.Core.Simulation;
using Vigil.Core.StateMachine;
using Vigil.Data;
using Vigil.AI.StateMachine;

namespace Vigil.AI.States
{
    /// <summary>
    /// Buildup-phase behaviour: hold the Director's standoff distance, stay OUT of
    /// line of sight, stay WITHIN earshot.
    ///
    /// <para>This is the state that generates most of the game's tension, and it
    /// never touches the player. Being heard but not seen is more frightening than
    /// being chased, because the player's imagination is doing the work.</para>
    /// </summary>
    public sealed class StalkState : StateBase<AgentContext>
    {
        float3 _goal;
        bool _hasGoal;
        float _repositionTimer;

        public override int Id => (int)AgentStateId.Stalk;
        public override string Name => "Stalk";

        public override void OnEnter(AgentContext ctx, in SimTime time)
        {
            _hasGoal = false;
            _repositionTimer = 0f;
            ctx.Path.Invalidate();
            ctx.Body.PlayAnimation("Stalk");
            ctx.LogState("stalking");
        }

        public override StateStatus OnTick(AgentContext ctx, in SimTime time)
        {
            if (!ctx.TryGetTarget(out PerceivedTarget target)) return StateStatus.Failed;

            _repositionTimer -= time.DeltaTime;

            float standoff = math.max(4f, ctx.Intent.DesiredStandoffDistance);
            float distance = math.distance(ctx.Position, target.LastKnownPosition);

            bool needsNewSpot = !_hasGoal
                                || _repositionTimer <= 0f
                                || ctx.Follower.HasArrived
                                || ctx.Follower.IsStuck
                                || math.abs(distance - standoff) > standoff * 0.5f;

            if (needsNewSpot)
            {
                // Look for concealment at roughly the standoff ring around the target.
                if (ctx.Navigation.TryFindConcealedPoint(
                        target.LastKnownPosition, target.LastKnownPosition,
                        standoff, NavArea.MaskAll, ref ctx.NavSeed, out float3 spot))
                {
                    _goal = spot;
                    _hasGoal = true;
                }
                else
                {
                    // No concealment available. Back off to the ring anyway rather
                    // than closing — being seen during Buildup collapses the phase.
                    if (ctx.Navigation.TryFindPointAwayFrom(
                            ctx.Position, target.LastKnownPosition, standoff,
                            NavArea.MaskAll, ref ctx.NavSeed, out float3 fallback))
                    {
                        _goal = fallback;
                        _hasGoal = true;
                    }
                }

                _repositionTimer = ctx.Random.NextFloat(4f, 9f);
                ctx.Path.Invalidate();
            }

            if (_hasGoal)
            {
                ctx.MoveTo(_goal, AgentMoveState.Stalk, PathPriority.Low);
            }
            else
            {
                ctx.StopMoving();
                ctx.FaceTowards(target.LastKnownPosition);
            }

            // Deliberate noise. The monster WANTS to be heard during Buildup — an
            // approach nobody notices is an approach that builds nothing.
            if (ctx.Random.Chance(0.012f))
            {
                ctx.Emit(StimulusChannel.Sound, StimulusTag.Machinery, 0.35f, 26f);
            }

            return StateStatus.Running;
        }
    }

    /// <summary>
    /// Flanking. Paths to a point that approaches the target from a different
    /// region than the one the monster was last visible from, so a player who has
    /// locked their camera on a doorway is punished for it.
    /// </summary>
    public sealed class RepositionState : StateBase<AgentContext>
    {
        float3 _goal;
        bool _hasGoal;
        float _timeout;

        public override int Id => (int)AgentStateId.Reposition;
        public override string Name => "Reposition";

        public override void OnEnter(AgentContext ctx, in SimTime time)
        {
            _hasGoal = false;
            _timeout = 8f;
            ctx.Path.Invalidate();
            ctx.Body.PlayAnimation("Run");

            if (!ctx.TryGetTarget(out PerceivedTarget target)) return;

            float3 toTarget = target.LastKnownPosition - ctx.Position;
            toTarget.y = 0f;

            if (math.lengthsq(toTarget) < 1e-3f) return;

            // Approach vector rotated 90 degrees, side chosen from the agent's own
            // deterministic stream so flanks are not always the same way round.
            float3 dir = math.normalize(toTarget);
            float3 perpendicular = new float3(-dir.z, 0f, dir.x) * (ctx.Random.Chance(0.5f) ? 1f : -1f);

            float3 candidate = target.LastKnownPosition + perpendicular * 9f - dir * 4f;
            _hasGoal = ctx.CanReach(candidate, out _goal);
        }

        public override StateStatus OnTick(AgentContext ctx, in SimTime time)
        {
            _timeout -= time.DeltaTime;

            if (!_hasGoal || _timeout <= 0f) return StateStatus.Succeeded;
            if (ctx.Follower.IsStuck || ctx.Blackboard.Get(BBKeys.PathFailed)) return StateStatus.Failed;

            ctx.MoveTo(_goal, AgentMoveState.Chase, PathPriority.High);

            if (ctx.Follower.HasArrived) return StateStatus.Succeeded;
            return StateStatus.Running;
        }
    }

    /// <summary>Direct pursuit, with velocity leading and straight-line shortcutting.</summary>
    public sealed class ChaseState : StateBase<AgentContext>
    {
        float _chaseStartedAt;
        bool _announced;

        public override int Id => (int)AgentStateId.Chase;
        public override string Name => "Chase";

        public override void OnEnter(AgentContext ctx, in SimTime time)
        {
            _chaseStartedAt = (float)time.Elapsed;
            _announced = false;
            ctx.Path.Invalidate();
            ctx.Body.PlayAnimation("Sprint");

            if (ctx.TryGetTarget(out PerceivedTarget target))
            {
                ctx.Events?.Publish(new ChaseStartedEvent { EntityId = ctx.EntityId, TargetId = target.TargetId });
                _announced = true;
                ctx.LogState($"chasing {target.TargetId}");
            }

            // A chase the player does not know has started is just an ambush.
            ctx.Emit(StimulusChannel.Sound, StimulusTag.Scream, 1f, 45f);
        }

        public override void OnExit(AgentContext ctx, in SimTime time)
        {
            if (!_announced) return;

            ctx.TryGetTarget(out PerceivedTarget target);
            ctx.Events?.Publish(new ChaseEndedEvent
            {
                EntityId = ctx.EntityId,
                TargetId = target.TargetId,
                DurationSeconds = (float)time.Elapsed - _chaseStartedAt
            });
        }

        public override StateStatus OnTick(AgentContext ctx, in SimTime time)
        {
            if (!ctx.TryGetTarget(out PerceivedTarget target)) return StateStatus.Failed;

            // Lead the target while confidence is high. Pathing to where they ARE
            // means always arriving where they WERE.
            float lead = math.saturate(target.PositionConfidence) * 0.6f;
            float3 goal = target.LastKnownPosition + target.LastKnownVelocity * lead;

            if (!ctx.CanReach(goal, out float3 snapped))
            {
                if (!ctx.CanReach(target.LastKnownPosition, out snapped)) return StateStatus.Failed;
            }

            // In the open, steer directly. Full path queries every repath interval
            // during a chase across an open room are pure waste, and the corridor
            // corners actually make the pursuit look less direct than it should.
            if (ctx.HasDirectLine(snapped))
            {
                float3 toTarget = snapped - ctx.Position;
                toTarget.y = 0f;

                if (math.lengthsq(toTarget) > 1e-4f)
                {
                    float3 dir = math.normalize(toTarget);
                    float speed = ctx.SpeedFor(AgentMoveState.Chase);
                    ctx.Body.Move(dir * speed, in time);
                    ctx.Body.FaceDirection(dir, in time);
                }
            }
            else
            {
                ctx.MoveTo(snapped, AgentMoveState.Chase, PathPriority.High);

                if (ctx.Blackboard.Get(BBKeys.PathFailed) && ctx.Follower.IsStuck)
                {
                    return StateStatus.Failed;
                }
            }

            ctx.Blackboard.Set(BBKeys.LastKnownPosition, target.LastKnownPosition, time.Tick);
            return StateStatus.Running;
        }
    }

    /// <summary>
    /// Windup → commit → recover.
    ///
    /// <para>During the commit window the agent CANNOT turn. This is what makes
    /// dodging a genuine skill rather than a dice roll, and it is the single most
    /// important piece of counterplay the players have.</para>
    /// </summary>
    public sealed class AttackState : StateBase<AgentContext>
    {
        enum Phase : byte { Windup, Commit, Recover }

        Phase _phase;
        float _phaseTimer;
        float3 _committedDirection;
        bool _struck;

        public override int Id => (int)AgentStateId.Attack;
        public override string Name => "Attack";

        public override void OnEnter(AgentContext ctx, in SimTime time)
        {
            _phase = Phase.Windup;
            _phaseTimer = ctx.Archetype != null ? ctx.Archetype.AttackWindup : 0.45f;
            _struck = false;

            ctx.StopMoving();
            ctx.Body.PlayAnimation("AttackWindup");
            ctx.Emit(StimulusChannel.Sound, StimulusTag.Scream, 1f, 30f);
        }

        public override StateStatus OnTick(AgentContext ctx, in SimTime time)
        {
            _phaseTimer -= time.DeltaTime;
            AgentArchetypeConfig cfg = ctx.Archetype;

            switch (_phase)
            {
                case Phase.Windup:
                    // Tracking is allowed ONLY during windup. This is the player's
                    // reaction window and the whole reason the attack is dodgeable.
                    if (ctx.TryGetTarget(out PerceivedTarget target))
                    {
                        ctx.FaceTowards(target.LastKnownPosition);

                        float3 dir = target.LastKnownPosition - ctx.Position;
                        dir.y = 0f;
                        if (math.lengthsq(dir) > 1e-4f) _committedDirection = math.normalize(dir);
                    }

                    if (_phaseTimer <= 0f)
                    {
                        _phase = Phase.Commit;
                        _phaseTimer = cfg != null ? cfg.AttackCommit : 0.3f;
                        ctx.Body.PlayAnimation("AttackStrike");
                    }
                    break;

                case Phase.Commit:
                {
                    // Locked. No steering toward the target, no re-aim. The agent
                    // lunges along the direction it committed to during windup.
                    float speed = ctx.SpeedFor(AgentMoveState.Chase) * 0.55f;
                    ctx.Body.Move(_committedDirection * speed, in time);

                    if (!_struck)
                    {
                        float damage = cfg != null ? cfg.AttackDamage : 40f;
                        float range = cfg != null ? cfg.AttackRange : 2f;

                        if (ctx.Body.TryStrike(damage, range))
                        {
                            _struck = true;
                            ctx.Emit(StimulusChannel.Sound, StimulusTag.Impact, 1f, 24f);
                        }
                    }

                    if (_phaseTimer <= 0f)
                    {
                        _phase = Phase.Recover;
                        _phaseTimer = cfg != null ? cfg.AttackRecover : 0.6f;
                        ctx.Body.PlayAnimation("AttackRecover");
                        ctx.StopMoving();
                    }
                    break;
                }

                case Phase.Recover:
                    if (_phaseTimer <= 0f)
                    {
                        float cooldown = cfg != null ? cfg.AttackCooldown : 2f;
                        ctx.Blackboard.Set(BBKeys.AttackCooldown, (float)time.Elapsed + cooldown, time.Tick);
                        return _struck ? StateStatus.Succeeded : StateStatus.Failed;
                    }
                    break;
            }

            return StateStatus.Running;
        }
    }

    /// <summary>Holds a downed player. Teammates can interrupt, which is the co-op hook.</summary>
    public sealed class GrappleState : StateBase<AgentContext>
    {
        float _remaining;
        ulong _victimId;

        public override int Id => (int)AgentStateId.Grapple;
        public override string Name => "Grapple";

        public override void OnEnter(AgentContext ctx, in SimTime time)
        {
            _remaining = ctx.Archetype != null ? ctx.Archetype.GrappleDuration : 3.5f;
            _victimId = ctx.TryGetTarget(out PerceivedTarget t) ? t.TargetId : 0UL;

            ctx.StopMoving();
            ctx.Body.PlayAnimation("Grapple");
        }

        public override StateStatus OnTick(AgentContext ctx, in SimTime time)
        {
            if (_victimId == 0UL) return StateStatus.Failed;

            _remaining -= time.DeltaTime;

            float dps = ctx.Archetype != null ? ctx.Archetype.GrappleDps : 20f;
            float range = ctx.Archetype != null ? ctx.Archetype.AttackRange : 2f;
            ctx.Body.TryStrike(dps * time.DeltaTime, range);

            if (ctx.TryGetTarget(out PerceivedTarget target)) ctx.FaceTowards(target.LastKnownPosition);

            return _remaining <= 0f ? StateStatus.Succeeded : StateStatus.Running;
        }
    }

    /// <summary>
    /// Director-mandated disengagement.
    ///
    /// <para>Critically, this must LOOK like a decision rather than a despawn. The
    /// agent breaks line of sight first and only then goes quiet, so players hear it
    /// leave. A monster that simply stops existing teaches players the threat was
    /// never real.</para>
    /// </summary>
    public sealed class RetreatState : StateBase<AgentContext>
    {
        float3 _goal;
        bool _hasGoal;
        float _timeout;

        public override int Id => (int)AgentStateId.Retreat;
        public override string Name => "Retreat";

        public override void OnEnter(AgentContext ctx, in SimTime time)
        {
            _timeout = 22f;
            ctx.Path.Invalidate();
            ctx.Body.PlayAnimation("Walk");
            ctx.LogState("director mandated disengage");

            float3 from = ctx.TryGetTarget(out PerceivedTarget t) ? t.LastKnownPosition : ctx.Position;

            float distance = math.max(30f, ctx.Intent.DesiredStandoffDistance);
            _hasGoal = ctx.Navigation.TryFindPointAwayFrom(
                ctx.Position, from, distance, NavArea.MaskAll, ref ctx.NavSeed, out _goal);

            // Audible withdrawal — the player needs to know it is going.
            ctx.Emit(StimulusChannel.Sound, StimulusTag.Machinery, 0.6f, 34f);
        }

        public override StateStatus OnTick(AgentContext ctx, in SimTime time)
        {
            _timeout -= time.DeltaTime;

            if (!_hasGoal || _timeout <= 0f) return StateStatus.Succeeded;

            ctx.MoveTo(_goal, AgentMoveState.Patrol, PathPriority.Normal);

            if (ctx.Follower.HasArrived || ctx.Follower.IsStuck) return StateStatus.Succeeded;
            return StateStatus.Running;
        }
    }

    /// <summary>
    /// Interrupt state. Immune to transitions until it expires — that immunity is
    /// enforced by the brain's transition guards, and is what makes a flare feel
    /// like it actually did something.
    /// </summary>
    public sealed class StunnedState : StateBase<AgentContext>
    {
        float _remaining;

        public override int Id => (int)AgentStateId.Stunned;
        public override string Name => "Stunned";

        /// <summary>Read by the brain's transition guards.</summary>
        public bool IsExpired { get; private set; }

        public override void OnEnter(AgentContext ctx, in SimTime time)
        {
            _remaining = ctx.Archetype != null ? ctx.Archetype.StunDuration : 4f;
            IsExpired = false;

            ctx.StopMoving();
            ctx.Body.PlayAnimation("Stunned");
            ctx.Emit(StimulusChannel.Sound, StimulusTag.Scream, 1f, 40f);
            ctx.LogState($"stunned for {_remaining:F1}s");
        }

        public override void OnExit(AgentContext ctx, in SimTime time)
        {
            ctx.IsStunned = false;
            IsExpired = false;
        }

        public override StateStatus OnTick(AgentContext ctx, in SimTime time)
        {
            _remaining -= time.DeltaTime;

            if (_remaining <= 0f)
            {
                IsExpired = true;
                ctx.IsStunned = false;
                return StateStatus.Succeeded;
            }

            return StateStatus.Running;
        }
    }

    /// <summary>Drives an off-mesh traversal to completion. The agent is committed and vulnerable.</summary>
    public sealed class TraverseLinkState : StateBase<AgentContext>
    {
        public override int Id => (int)AgentStateId.TraverseLink;
        public override string Name => "TraverseLink";

        public override void OnEnter(AgentContext ctx, in SimTime time)
        {
            if (ctx.PendingLink == null) return;

            ctx.Follower.BeginLink(ctx.PendingLink);
            ctx.Body.PlayAnimation(ctx.PendingLink.AnimationState);
            ctx.Emit(StimulusChannel.Sound, StimulusTag.Machinery, 0.5f, 18f);
        }

        public override void OnExit(AgentContext ctx, in SimTime time)
        {
            ctx.PendingLink = null;
        }

        public override StateStatus OnTick(AgentContext ctx, in SimTime time)
        {
            if (ctx.PendingLink == null) return StateStatus.Failed;

            ctx.Follower.Evaluate(ctx.Position, 0f, in time);

            if (!ctx.Follower.IsTraversingLink) return StateStatus.Succeeded;

            ctx.Body.SetTraversalPose(ctx.Follower.LinkPosition, ctx.Follower.LinkRotation);
            return StateStatus.Running;
        }
    }

    /// <summary>Leash. Pulls the agent home when dragged beyond its territory.</summary>
    public sealed class ReturnToTerritoryState : StateBase<AgentContext>
    {
        float3 _goal;
        bool _hasGoal;

        public override int Id => (int)AgentStateId.ReturnToTerritory;
        public override string Name => "ReturnToTerritory";

        public override void OnEnter(AgentContext ctx, in SimTime time)
        {
            ctx.Path.Invalidate();
            _hasGoal = ctx.CanReach(ctx.TerritoryCenter, out _goal);
            ctx.Body.PlayAnimation("Walk");
        }

        public override StateStatus OnTick(AgentContext ctx, in SimTime time)
        {
            if (!_hasGoal) return StateStatus.Failed;

            ctx.MoveTo(_goal, AgentMoveState.Patrol, PathPriority.Normal);

            float leash = ctx.Archetype != null ? ctx.Archetype.LeashRadius : 200f;
            if (math.distance(ctx.Position, ctx.TerritoryCenter) < leash * 0.5f) return StateStatus.Succeeded;
            if (ctx.Follower.IsStuck) return StateStatus.Failed;

            return StateStatus.Running;
        }
    }
}
