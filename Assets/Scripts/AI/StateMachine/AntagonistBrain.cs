// -----------------------------------------------------------------------------
// Vigil — the antagonist's behaviour graph.
//
// Topology:
//
//   Root
//   ├── Dormant                 (asleep, zero cost)
//   ├── Idle
//   ├── Patrol
//   ├── InvestigateRoot ────┐   nested machine
//   │                       ├── Investigate
//   │                       └── Search
//   ├── Stalk                   (Buildup: heard, not seen)
//   ├── HuntRoot ───────────┐   nested machine
//   │                       ├── Chase
//   │                       ├── Reposition
//   │                       ├── Attack
//   │                       └── Grapple
//   ├── Retreat                 (Director-mandated disengage)
//   ├── ReturnToTerritory       (leash)
//   ├── TraverseLink
//   └── Stunned                 (interrupt)
//
// Every transition below carries an explicit priority, and most carry a minimum
// dwell time. That is not defensive padding — an agent sitting exactly on an
// awareness threshold will flip states EVERY TICK without it, and players read
// that as a broken monster rather than an undecided one.
//
// AnyState edges are restricted to four genuine interrupts: Stunned, Retreat,
// ReturnToTerritory and TraverseLink. Every additional AnyState edge erodes the
// hierarchy back toward the flat spaghetti it exists to prevent.
// -----------------------------------------------------------------------------

using Unity.Mathematics;
using Vigil.Core.Contracts;
using Vigil.Core.StateMachine;
using Vigil.AI.States;
using Vigil.Data;

namespace Vigil.AI.StateMachine
{
    public static class AntagonistBrain
    {
        // Priority bands. Named so a reader can see the intended precedence at a
        // glance rather than reverse-engineering it from magic numbers.
        const int PriorityInterrupt = 1000;   // stun
        const int PriorityLeash = 900;
        const int PriorityDirectorOverride = 800;
        const int PriorityLink = 700;
        const int PriorityEngage = 200;
        const int PriorityDisengage = 150;
        const int PriorityAlert = 100;
        const int PriorityAmbient = 10;

        /// <summary>
        /// Builds the complete graph. The returned machine is entered by the agent
        /// on spawn and ticked once per simulation tick.
        /// </summary>
        public static StateMachine<AgentContext> Build(AgentContext ctx, AgentArchetypeConfig archetype)
        {
            StunnedState stunned = new StunnedState();

            StateMachine<AgentContext> investigateRoot = BuildInvestigateSubtree();
            StateMachine<AgentContext> huntRoot = BuildHuntSubtree();

            StateMachine<AgentContext> root = new StateMachine<AgentContext>(0, "Antagonist");

            root.AddState(new DormantState())
                .AddState(new IdleState())
                .AddState(new PatrolState())
                .AddState(investigateRoot)
                .AddState(new StalkState())
                .AddState(huntRoot)
                .AddState(new RetreatState())
                .AddState(new ReturnToTerritoryState())
                .AddState(new TraverseLinkState())
                .AddState(stunned);

            root.SetInitialState((int)AgentStateId.Dormant);

            // ---------------------------------------------------------------- interrupts

            root.AddAnyTransition(
                (int)AgentStateId.Stunned,
                c => c.IsStunned,
                priority: PriorityInterrupt,
                label: "stunned");

            // Stun is the ONLY state that suppresses everything else, so its exit is
            // a plain local transition gated on the state reporting completion.
            root.AddTransition(
                (int)AgentStateId.Stunned, (int)AgentStateId.Patrol,
                c => true,
                priority: PriorityAmbient,
                requiredStatus: StateStatus.Succeeded,
                label: "stun expired");

            root.AddAnyTransition(
                (int)AgentStateId.ReturnToTerritory,
                c => c.Archetype != null &&
                     math.distance(c.Position, c.TerritoryCenter) > c.Archetype.LeashRadius,
                priority: PriorityLeash,
                minDwellSeconds: 1f,
                label: "leash");

            root.AddTransition(
                (int)AgentStateId.ReturnToTerritory, (int)AgentStateId.Patrol,
                c => true,
                priority: PriorityAmbient,
                requiredStatus: StateStatus.Succeeded,
                label: "returned home");

            root.AddAnyTransition(
                (int)AgentStateId.TraverseLink,
                c => c.PendingLink != null,
                priority: PriorityLink,
                label: "link queued");

            root.AddTransition(
                (int)AgentStateId.TraverseLink, (int)AgentStateId.Patrol,
                c => true,
                priority: PriorityAmbient,
                requiredStatus: StateStatus.Succeeded,
                label: "link complete");

            // Director-mandated withdrawal. Deliberately outranks every pursuit edge:
            // when the Director says Relax, the monster leaves even if it is winning.
            root.AddAnyTransition(
                (int)AgentStateId.Retreat,
                c => c.Intent.Phase == DreadPhase.Relax &&
                     !c.Intent.EngagementAuthorised &&
                     c.AwarenessLevel >= AwarenessLevel.Alerted,
                priority: PriorityDirectorOverride,
                minDwellSeconds: 0.5f,
                cooldownSeconds: 12f,
                label: "director: relax");

            root.AddTransition(
                (int)AgentStateId.Retreat, (int)AgentStateId.Patrol,
                c => true,
                priority: PriorityAmbient,
                requiredStatus: StateStatus.Succeeded,
                label: "withdrawn");

            // ------------------------------------------------------------------ ambient

            root.AddTransition(
                (int)AgentStateId.Dormant, (int)AgentStateId.Patrol,
                c => c.Intent.Pressure > 0.02f || c.AwarenessLevel > AwarenessLevel.Unaware,
                priority: PriorityAmbient,
                label: "wake");

            root.AddTransition(
                (int)AgentStateId.Idle, (int)AgentStateId.Patrol,
                c => true,
                priority: PriorityAmbient,
                minDwellSeconds: 4f,
                label: "resume patrol");

            root.AddTransition(
                (int)AgentStateId.Patrol, (int)AgentStateId.Idle,
                c => c.Intent.Phase == DreadPhase.Fadeout && c.AwarenessLevel == AwarenessLevel.Unaware,
                priority: PriorityAmbient,
                minDwellSeconds: 20f,
                cooldownSeconds: 30f,
                label: "fadeout rest");

            // ------------------------------------------------------------ alert ladder

            // Patrol -> Investigate on the first real cue. Half-second dwell so a
            // single spurious stimulus does not yank the agent out of patrol.
            root.AddTransition(
                (int)AgentStateId.Patrol, (int)AgentStateId.InvestigateRoot,
                c => c.AwarenessLevel >= AwarenessLevel.Suspicious,
                priority: PriorityAlert,
                minDwellSeconds: 0.5f,
                label: "heard something");

            root.AddTransition(
                (int)AgentStateId.Idle, (int)AgentStateId.InvestigateRoot,
                c => c.AwarenessLevel >= AwarenessLevel.Suspicious,
                priority: PriorityAlert,
                label: "heard something");

            // Investigation resolved without finding anything.
            root.AddTransition(
                (int)AgentStateId.InvestigateRoot, (int)AgentStateId.Patrol,
                c => c.AwarenessLevel < AwarenessLevel.Suspicious,
                priority: PriorityAmbient,
                minDwellSeconds: 2f,
                requiredStatus: StateStatus.Succeeded,
                label: "nothing there");

            // --------------------------------------------------------------- engagement

            // The central gate: contact is only authorised when the Director says so.
            // During Buildup the monster knows exactly where you are and is forbidden
            // from acting on it, which is the entire source of that phase's tension.
            root.AddTransition(
                (int)AgentStateId.InvestigateRoot, (int)AgentStateId.HuntRoot,
                c => c.AwarenessLevel >= AwarenessLevel.Confirmed && c.Intent.EngagementAuthorised,
                priority: PriorityEngage,
                minDwellSeconds: 0.3f,
                label: "target confirmed");

            root.AddTransition(
                (int)AgentStateId.Stalk, (int)AgentStateId.HuntRoot,
                c => c.AwarenessLevel >= AwarenessLevel.Confirmed && c.Intent.EngagementAuthorised,
                priority: PriorityEngage,
                minDwellSeconds: 0.3f,
                label: "engagement authorised");

            root.AddTransition(
                (int)AgentStateId.Patrol, (int)AgentStateId.HuntRoot,
                c => c.AwarenessLevel >= AwarenessLevel.Confirmed && c.Intent.EngagementAuthorised,
                priority: PriorityEngage,
                label: "spotted");

            // Buildup stalking.
            root.AddTransition(
                (int)AgentStateId.InvestigateRoot, (int)AgentStateId.Stalk,
                c => c.AwarenessLevel >= AwarenessLevel.Alerted && !c.Intent.EngagementAuthorised,
                priority: PriorityAlert + 20,
                minDwellSeconds: 1f,
                label: "stalk (not authorised)");

            root.AddTransition(
                (int)AgentStateId.Patrol, (int)AgentStateId.Stalk,
                c => c.AwarenessLevel >= AwarenessLevel.Alerted &&
                     !c.Intent.EngagementAuthorised &&
                     c.Intent.Phase == DreadPhase.Buildup,
                priority: PriorityAlert + 10,
                minDwellSeconds: 1f,
                label: "begin buildup");

            // -------------------------------------------------------------- disengage

            // Hunt -> Investigate when contact is lost. A full second of dwell:
            // dropping out of a chase the instant awareness dips below Confirmed
            // produces the classic "monster gives up mid-lunge" tell.
            root.AddTransition(
                (int)AgentStateId.HuntRoot, (int)AgentStateId.InvestigateRoot,
                c => c.AwarenessLevel < AwarenessLevel.Alerted,
                priority: PriorityDisengage,
                minDwellSeconds: 1f,
                label: "lost contact");

            root.AddTransition(
                (int)AgentStateId.HuntRoot, (int)AgentStateId.Stalk,
                c => !c.Intent.EngagementAuthorised,
                priority: PriorityDisengage + 10,
                minDwellSeconds: 0.5f,
                label: "authorisation revoked");

            root.AddTransition(
                (int)AgentStateId.Stalk, (int)AgentStateId.Patrol,
                c => c.AwarenessLevel < AwarenessLevel.Suspicious,
                priority: PriorityAmbient,
                minDwellSeconds: 3f,
                label: "stalk lost target");

            root.AddTransition(
                (int)AgentStateId.Stalk, (int)AgentStateId.InvestigateRoot,
                c => c.AwarenessLevel < AwarenessLevel.Alerted && c.AwarenessLevel >= AwarenessLevel.Suspicious,
                priority: PriorityAlert,
                minDwellSeconds: 2f,
                label: "stalk lost track");

            // Hunt failed outright (path impossible, target despawned).
            root.AddTransition(
                (int)AgentStateId.HuntRoot, (int)AgentStateId.InvestigateRoot,
                c => true,
                priority: PriorityDisengage - 10,
                requiredStatus: StateStatus.Failed,
                minDwellSeconds: 0.5f,
                label: "hunt failed");

            return root;
        }

        // ---------------------------------------------------------------- subtrees

        static StateMachine<AgentContext> BuildInvestigateSubtree()
        {
            StateMachine<AgentContext> m = new StateMachine<AgentContext>(
                (int)AgentStateId.InvestigateRoot, "Investigate");

            m.AddState(new InvestigateState())
             .AddState(new SearchState());

            m.SetInitialState((int)AgentStateId.Investigate);

            // Investigating a specific point failed or came up empty while the agent
            // still believes someone is around -> widen into a region sweep.
            m.AddTransition(
                (int)AgentStateId.Investigate, (int)AgentStateId.Search,
                c => c.AwarenessLevel >= AwarenessLevel.Alerted || c.AwarenessLevel == AwarenessLevel.Lost,
                priority: 50,
                requiredStatus: StateStatus.Failed,
                label: "escalate to search");

            m.AddTransition(
                (int)AgentStateId.Investigate, (int)AgentStateId.Search,
                c => c.AwarenessLevel == AwarenessLevel.Lost,
                priority: 40,
                requiredStatus: StateStatus.Succeeded,
                minDwellSeconds: 0.5f,
                label: "still lost — search");

            // A fresh, concrete cue during a sweep pulls the agent back to a point.
            m.AddTransition(
                (int)AgentStateId.Search, (int)AgentStateId.Investigate,
                c => c.Awareness != null &&
                     c.Awareness.TryGetInvestigationPoint(out _, out float priority) &&
                     priority > 0.4f,
                priority: 60,
                minDwellSeconds: 2f,
                cooldownSeconds: 4f,
                label: "new cue");

            return m;
        }

        static StateMachine<AgentContext> BuildHuntSubtree()
        {
            StateMachine<AgentContext> m = new StateMachine<AgentContext>(
                (int)AgentStateId.HuntRoot, "Hunt");

            m.AddState(new ChaseState())
             .AddState(new RepositionState())
             .AddState(new AttackState())
             .AddState(new GrappleState());

            m.SetInitialState((int)AgentStateId.Chase);

            m.AddTransition(
                (int)AgentStateId.Chase, (int)AgentStateId.Attack,
                InRangeAndOffCooldown,
                priority: 100,
                minDwellSeconds: 0.2f,
                label: "in range");

            // Attack always returns to Chase regardless of whether it connected —
            // the cooldown recorded by AttackState is what prevents an immediate
            // re-entry, not a transition guard.
            m.AddTransition(
                (int)AgentStateId.Attack, (int)AgentStateId.Chase,
                c => true,
                priority: 50,
                requiredStatus: StateStatus.Succeeded,
                label: "attack landed");

            m.AddTransition(
                (int)AgentStateId.Attack, (int)AgentStateId.Chase,
                c => true,
                priority: 50,
                requiredStatus: StateStatus.Failed,
                label: "attack missed");

            // Blocked or repeatedly failing to close -> try a flank instead of
            // grinding into the same corner forever.
            m.AddTransition(
                (int)AgentStateId.Chase, (int)AgentStateId.Reposition,
                c => c.Follower.IsStuck || c.Blackboard.Get(BBKeys.PathFailed),
                priority: 70,
                minDwellSeconds: 1.5f,
                cooldownSeconds: 6f,
                label: "blocked — flank");

            m.AddTransition(
                (int)AgentStateId.Reposition, (int)AgentStateId.Chase,
                c => true,
                priority: 40,
                requiredStatus: StateStatus.Succeeded,
                label: "flank complete");

            m.AddTransition(
                (int)AgentStateId.Reposition, (int)AgentStateId.Chase,
                c => true,
                priority: 40,
                requiredStatus: StateStatus.Failed,
                label: "flank failed");

            m.AddTransition(
                (int)AgentStateId.Attack, (int)AgentStateId.Grapple,
                c => c.Blackboard.Get(BBKeys.TargetId) != 0UL &&
                     c.Awareness != null &&
                     c.Awareness.TryGetTarget(c.Blackboard.Get(BBKeys.TargetId), out PerceivedTarget t) &&
                     t.HasLineOfSight &&
                     math.distance(c.Position, t.LastKnownPosition) < 2f,
                priority: 120,
                requiredStatus: StateStatus.Succeeded,
                cooldownSeconds: 8f,
                label: "grapple");

            m.AddTransition(
                (int)AgentStateId.Grapple, (int)AgentStateId.Chase,
                c => true,
                priority: 40,
                requiredStatus: StateStatus.Succeeded,
                label: "grapple done");

            m.AddTransition(
                (int)AgentStateId.Grapple, (int)AgentStateId.Chase,
                c => true,
                priority: 40,
                requiredStatus: StateStatus.Failed,
                label: "grapple broken");

            return m;
        }

        static bool InRangeAndOffCooldown(AgentContext c)
        {
            if (c.Archetype == null) return false;

            float readyAt = c.Blackboard.Get(BBKeys.AttackCooldown);
            if (c.Time.Elapsed < readyAt) return false;

            if (!c.TryGetTarget(out PerceivedTarget target)) return false;
            if (!target.HasLineOfSight) return false;

            return math.distance(c.Position, target.LastKnownPosition) <= c.Archetype.AttackRange;
        }
    }
}
