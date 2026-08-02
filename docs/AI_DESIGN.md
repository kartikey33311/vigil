# AI Design — Project Vigil

This document explains what the antagonist AI does, and more importantly *why it
is built the way it is*. Most of the decisions here are counter-intuitive, and
several of them deliberately make the AI worse at winning in order to make it
better at being frightening.

---

## 1. The governing idea

> A perfectly competent monster is not scary. It is a timer.

If the antagonist always knows where you are and always takes the optimal path,
the encounter has exactly one outcome and players learn that within two sessions.
Everything below exists to produce a monster that is **legibly reasoning, often
wrong, and occasionally terrifyingly right**.

Three properties matter more than optimality:

| Property | Why it matters |
|---|---|
| **Legibility** | The player must be able to infer *what it thinks* from what it does. A monster that pauses, turns toward a noise and walks to the wrong door is telling a story. |
| **Fallibility** | Being wrong is what creates the moments players retell. Perfect tracking creates none. |
| **Escalation** | Threat must have a shape over time — approach, contact, withdrawal — not a constant level. |

---

## 2. Perception

### 2.1 Awareness is continuous

The antagonist never has a boolean "sees player". It maintains a per-target
`PerceivedTarget` with an `Awareness` value in `0..1` that accrues from stimuli
and decays over time, crossing named bands:

```
0.00 ─────── 0.25 ─────────── 0.55 ─────────── 0.85 ─────── 1.00
   Unaware      Suspicious        Alerted         Confirmed
                (orients)      (investigates)     (pursues)
```

`Lost` is a fifth band, entered when awareness falls below `Alerted` *after*
having been `Confirmed`. It is what drives search rather than patrol.

This single change is what produces the core horror beat — *"it heard something
and it is coming to look"* — which binary detection cannot express at all.

### 2.2 Detection is time-based, never instant

Vision accumulates awareness proportional to:

```
visibility × angleFactor × distanceFactor × Δt
```

A crouched player at the edge of the far cone in an unlit room takes **seconds**
to register. Instant detection reads to players as the AI cheating, even when it
is technically fair, because they never get the chance to react to being noticed.

### 2.3 Vision is three bands, not one cone

A single cone has a hard edge that players learn to hug, and a blind spot
directly behind the agent that becomes an exploit.

- **Focus cone** — narrow, long range. Reads as "looking at something".
- **Mid cone** — wide, medium range. Ordinary awareness.
- **Near band** — nearly omnidirectional, very short range. You cannot stand
  behind its shoulder and be safe.

Visibility is further scaled by `IPerceivable.VisibilityScale`, which falls when
crouched, still, or in shadow — and **rises when the flashlight is on**. That is
the central risk/reward of the game: light is the only way to see, and it is the
loudest thing you can do visually.

### 2.4 Hearing is deliberately inaccurate

Sound is pushed through `IPerceptionBus` to receivers in range, then attenuated
by occlusion (a raycast counting blockers). The important part:

> **Positional error is added in proportion to attenuation.**

A sound heard clearly through open air localises precisely. A sound heard through
two walls localises *badly* — and the monster walks to the wrong place. This is
not a limitation being modelled for realism; it is the single richest source of
tension in the system, and it is why players learn to make noise deliberately.

### 2.5 Scent gives it a trail

Players drop pooled, decaying `ScentMarker`s as they move. The scent sensor reads
the freshest marker in range and derives a gradient, so the monster can follow
where you **went** rather than where you are. This is what makes running in a
straight line a mistake and doubling back a real tactic.

### 2.6 Cost control

- Vision is a **pull** sensor, evaluated on the agent's own tick budget, with a
  cap on candidates per tick and round-robin over the remainder.
- Sound/scent/touch/damage are **push**, routed through a uniform spatial hash so
  a broadcast costs O(receivers in range) rather than O(all receivers). When the
  level is quiet, perception costs nothing at all.

---

## 3. State machine

### 3.1 Why hierarchical

A flat FSM with fifteen states needs up to 210 possible edges, and in practice
becomes unreviewable at about ten. The hierarchy keeps any single graph small:

```
Root
├── Dormant
├── Idle
├── Patrol
├── Investigate ──┐  (nested machine)
│                 ├── Investigate
│                 └── Search
├── Stalk
├── Hunt ─────────┐  (nested machine)
│                 ├── Chase
│                 ├── Reposition
│                 ├── Attack
│                 └── Grapple
├── Retreat
├── TraverseLink
├── ReturnToTerritory
└── Stunned
```

Because `StateMachine<TContext>` implements `IState<TContext>`, a machine *is* a
state. Entering `Hunt` enters its child; exiting `Hunt` unwinds the whole subtree.
No special-case code.

### 3.2 Anti-oscillation is a hard requirement

An agent sitting exactly on an awareness threshold will flip between two states
every tick unless prevented. Players read that as a broken monster. Three
mechanisms, all in the Core engine:

1. **`MinDwellSeconds`** — the source state must have been active for N seconds
   before an edge may fire.
2. **`CooldownSeconds`** — the same edge cannot re-fire immediately.
3. **Awareness hysteresis** — decay is *slower* just below a band boundary than
   just above it, so crossing back is harder than crossing forward.

`AwarenessModelTests` asserts that a target parked on a threshold produces fewer
than a small bounded number of band changes over a simulated window. That test
exists specifically to stop this regressing.

### 3.3 Transition priority

Guards overlap in practice. Evaluating in registration order makes behaviour
depend on the order someone happened to type things in. Every edge carries an
explicit integer priority, sorted descending with a stable tiebreak, and
`AnyState` edges outrank local ones at equal priority so interrupts always win.

`AnyState` is restricted to three cases — `Stunned`, `Retreat` (Director
override), and `Dormant`. Overusing it recreates exactly the spaghetti the
hierarchy exists to prevent.

### 3.4 Attack has a commitment window

`AttackState` runs windup → strike → recover, and during the strike the agent
**cannot turn**. This is what makes dodging a real skill rather than a dice roll:
if the monster could track you through its own animation, the correct play would
always be "hold W", and the encounter would have no texture.

---

## 4. Pathfinding

### 4.1 Two tiers

| Tier | Graph | Scale | Answers |
|---|---|---|---|
| 1 | NavMesh | metres | "What is my next corner?" |
| 2 | Region graph | rooms | "Which rooms have I not swept?" |

Running strategic reasoning on raw NavMesh polygons is both too slow and too
granular to be *legible*. Players read the monster as intelligent when it clears
**rooms** in a sensible order — not when it walks a mathematically optimal polygon
sequence. The region graph exists to make that reasoning possible.

Region travel costs are precomputed all-pairs (Floyd–Warshall at bake time —
regions number in the tens, so this is trivial) which makes `GetTravelCost` O(1).
The Director queries it constantly.

### 4.2 Everything is async

`INavigationService` is request/poll with versioned handles. Nothing blocks.

- A blocking `NavMesh.CalculatePath` across 40 agents is a guaranteed frame spike.
- More subtly, a blocking API makes worst-case cost **unboundable**, which defeats
  the entire point of the tick scheduler.

Handles are `(Slot, Version)`. Recycling a slot bumps the version, so a stale poll
from a despawned agent fails cleanly instead of silently reading another agent's
path. `PathHandleTests` covers this.

### 4.3 Partial paths are used, not discarded

`PathQueryStatus.PartialSuccess` is treated as usable. An agent that walks as far
toward you as it can reads far better than one that gives up and stands still —
and standing still is the single most immersion-breaking AI failure there is.

### 4.4 Off-mesh links matter more than they look

Vents, windows and ledge drops (`INavLinkAction`) are what stop the antagonist
being confined to the same corridors as the player. The moment it comes through a
vent, every space in the level stops feeling safe. Traversals are timed and
committing — the agent is vulnerable during them, which is a deliberate
counterweight.

### 4.5 Search is the most important behaviour in the game

`SearchState` runs when contact is lost. It expands over **regions**, ordered by:

1. Reachability from the last known position
2. How long since that region was last swept (`IRegionGraph.MarkSearched`)
3. Darkness and enclosure — it checks the places you would actually hide

Search radius scales with `PerceivedTarget.PositionConfidence`, which decays with
time. Low confidence means search wide. After N regions it gives up and degrades
to patrol — the monster *deciding to stop looking* is a beat as important as any
chase, because it is the moment the player is allowed to breathe.

---

## 5. The Director

### 5.1 The curve

```
        pressure
           ▲
           │        ╭──────╮
           │      ╭─╯      ╰─╮
           │    ╭─╯          ╰──╮
           │  ╭─╯                ╰────────╮
           └──┴────────┴─────────┴────────┴──────▶ time
             Buildup    Peak      Relax    Fadeout
```

- **Buildup** — the entity closes distance and cues intensify, but contact is
  *not authorised*. This is `StalkState`: stay out of line of sight, stay within
  earshot.
- **Peak** — engagement authorised. The chase.
- **Relax** — the entity is **forced to disengage**, regardless of how well it was
  doing. This is the counter-intuitive one and it is implemented as a hard clamp
  on maximum phase duration, not a suggestion.
- **Fadeout** — near-silence. Players regroup, loot, talk.

### 5.2 Why forced disengagement

Because fear is a derivative. A monster that never leaves produces adaptation, and
adapted players are not frightened, they are annoyed. The withdrawal is what makes
the next approach land. `DirectorTests` asserts that the maximum phase duration
forces `Relax` even while stress remains high, precisely because this is the rule
most likely to be "optimised away" by someone who has not read this document.

`RetreatState` also has to *look* like a decision rather than a despawn: it breaks
line of sight before it stops being audible, so players hear it leave.

### 5.3 Target selection is deliberately not optimal

The Director prefers to pressure the **isolated and the calm** player — not the
nearly-dead one.

Pressuring a broken player produces a rout: they die, the team collapses, the
session ends flat. Pressuring the confident player produces a *story* — the one
where the person who was doing fine suddenly wasn't. The stress model tracks
composure, isolation distance, time since last threat and darkness per player,
which is what makes this selection possible at all.

---

## 6. Multi-agent coordination

The shipping default is a single antagonist, but `SquadCoordinator` supports more:

- Roles (`Pursuer`, `Flanker`, `Blocker`, `Watcher`) are assigned from a shared
  blackboard through a **claim/release ticket system**, so two agents never take
  the same role or path to the same flank point.
- `RegionClaim` reserves a region for one agent's search, so a group sweeps
  efficiently instead of three agents clearing the same room — which is both
  wasteful and reads as stupid.

It degrades to zero overhead with one agent.

---

## 7. Performance model

The reason this scales is the **budgeted, phase-staggered tick scheduler**
(`Vigil.Core.Simulation.TickScheduler`):

| Bucket | Divisor | Applied to |
|---|---|---|
| `Critical` | every tick | the agent actively hunting a player |
| `High` | every 2nd | in play, not the active threat |
| `Normal` | every 4th | off-screen / distant |
| `Low` | every 8th | far-field ambient NPCs |
| `Dormant` | never | asleep until a player approaches |

`NpcAgent` implements `IAdaptiveTickable` and re-declares its bucket from its own
awareness band, so LOD is driven by *narrative relevance*, not just distance.

Two further guarantees:

- **Phase offsets** de-synchronise agents within a bucket, so 40 agents never
  think on the same tick. Without this, the frame time is a sawtooth.
- **A millisecond budget** defers the remainder to the next tick and **resumes
  from the deferral point**, so nobody starves. Adding agents degrades update
  *frequency*, not frame time.

`TickSchedulerTests` covers the divisors, the stagger, the deferral, and the
resume-without-starvation property.

---

## 8. What is deliberately not here

- **No behaviour trees.** The FSM plus utility scoring covers this design's needs
  with far less machinery, and an FSM's active state is trivially inspectable in
  the debug overlay — which matters enormously during playtest tuning.
- **No GOAP / planning.** Emergent plans are hard to make legible. A player who
  cannot infer the reasoning experiences it as randomness, not intelligence.
- **No ML-driven behaviour.** Non-determinism is unacceptable in a
  server-authoritative game, and untunable behaviour is unshippable.

---

## 9. Tuning entry points

Everything above is data-driven through `Vigil.Data`:

| Config | Governs |
|---|---|
| `PerceptionConfig` | cones, radii, per-channel and per-tag awareness weights, decay curves |
| `NavigationConfig` | agent metrics, area costs, query budget, repath intervals |
| `AgentArchetypeConfig` | the monster stat block — speeds per state, attack timings, stun |
| `DirectorConfig` | phase durations, stress thresholds, standoff distances, speed scales |

Use `Vigil ▸ AI Debug Window` while playing to watch the active FSM path,
awareness model contents and current path corridor per agent.
