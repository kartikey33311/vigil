# Project Vigil

A 1–4 player co-operative survival-horror game built in Unity 6, with a
server-authoritative multiplayer stack, sensory NPC AI, two-tier pathfinding, a
hierarchical state machine, and a pacing Director.

> **Codename:** Vigil · **Engine:** Unity 6000.0.25f1 (URP 17.0.3) ·
> **Netcode:** Netcode for GameObjects 2.4.0 over Unity Transport 2.4.0

---

## The pitch

Four people are inside a facility that has lost power. Something is inside with
them. They have to restore the generators and reach extraction. They cannot fight
it; they can only manage what it knows.

The design thesis is that **fear is a derivative**. It comes from the transition
between safety and threat, not from the threat itself. A monster that applies
constant pressure stops being frightening in about ten minutes. So the entire
technical stack is built to control the *rate of change* of threat:

- The antagonist's knowledge of you is **continuous, not binary** — it accrues
  awareness and it decays, so "it heard something" is a real state you can play against.
- Its knowledge is **allowed to be wrong**. Sound heard through walls localises
  poorly on purpose, so it searches the wrong room and you watch it do so.
- A **Director** forces the antagonist to disengage on a timer even when it is
  winning, because the next approach only lands if there was a lull before it.

---

## Quick start

```bash
git clone <this repo> && cd ProjectVigil
```

1. Open the project in **Unity 6000.0.25f1**. First import resolves packages and
   takes a few minutes.
2. Run **`Vigil ▸ Configure Project Settings`** (creates layers, tags, physics matrix).
3. Run **`Vigil ▸ Generate Playable Sample`** (builds configs, materials, the
   facility level, the NavMesh bake, prefabs and both scenes).
4. Open `Assets/Scenes/Bootstrap.unity` and press Play.

Nothing in this repo is hand-authored Unity YAML. Scenes, prefabs, materials,
render-pipeline assets and every ScriptableObject are **generated from code** by
the editor tooling. That is deliberate: hand-written `.unity`/`.prefab` files
carry GUIDs that will not match the ones Unity assigns on import, which produces
references that are silently null. Generating them means the repo is small,
diffable, and cannot rot.

### Multiplayer testing

- **In-editor:** install *Multiplayer Play Mode* and enable 1–3 virtual players.
- **Two builds:** `Vigil ▸ Build ▸ Windows Client`, run one as Host, one as Client.
- **Dedicated server:** `Vigil ▸ Build ▸ Linux Dedicated Server`, then
  `./Vigil.x86_64 -server -port 7777 -seed 12345`

### Verifying a change

```bash
pwsh ./Tools/verify-compile.ps1 -RunTests
```

Headless compile + EditMode tests. Same command CI runs.

---

## Repository layout

```
Assets/Scripts/
  Core/          Vigil.Core        Engine-agnostic foundation. FROZEN CONTRACT LAYER.
    Contracts/     Interfaces every other assembly codes against
    StateMachine/  Generic hierarchical FSM
    Simulation/    Fixed-step clock + budgeted tick scheduler
    Events/        Typed, per-instance event bus
    Mathx/         Deterministic PRNG
  Data/          Vigil.Data        ScriptableObject tuning layer
  Networking/    Vigil.Net         Session, replication, interest, prediction
  AI/            Vigil.AI          Perception, pathfinding, states, director, steering
  Gameplay/      Vigil.Gameplay    Player, interaction, world systems
  Audio/         Vigil.Audio       Occlusion, propagation, adaptive score
  UI/            Vigil.UI          UI Toolkit HUD and menus
  Bootstrap/     Vigil.Bootstrap   Composition root
  Editor/        Vigil.Editor      Generation, validation, debug tooling
Assets/Tests/    EditMode + PlayMode
docs/            Architecture, AI design, netcode, GDD, performance budgets
Tools/           Compile gate and local CI helpers
```

Assembly dependencies flow strictly one direction, enforced by asmdef references:

```
Core ← Data ← Net ← AI ← Gameplay ← UI ← Bootstrap
              Audio ←┘ (Audio depends only on Core + Data)
```

`Vigil.Audio` deliberately cannot see `Vigil.Gameplay` or `Vigil.AI` — it reacts
to events, never to objects. That one constraint is what keeps audio from
becoming the dumping ground it becomes on most projects.

---

## Documentation

| Document | What it covers |
|---|---|
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Assembly graph, simulation model, service composition, threading |
| [`docs/AI_DESIGN.md`](docs/AI_DESIGN.md) | Perception, awareness, FSM topology, pathfinding tiers, Director |
| [`docs/NETWORKING.md`](docs/NETWORKING.md) | Authority model, prediction/reconciliation, interest management |
| [`docs/GDD.md`](docs/GDD.md) | Game design: loop, systems, progression, failure states |
| [`docs/PERFORMANCE_BUDGETS.md`](docs/PERFORMANCE_BUDGETS.md) | Frame and bandwidth budgets, and how they are enforced |
| [`docs/CONTRIBUTING.md`](docs/CONTRIBUTING.md) | Code standards, review checklist, branch policy |

---

## Engineering constraints

These are enforced in review, and several are covered by tests:

1. **Server-authoritative, always.** Every simulation write is gated on `IsServer`.
   In a game whose tension rests on not knowing where the monster is, a trusted
   client is not a performance trade-off — it is the game being over.
2. **No per-tick allocation** in AI or networking hot paths. No LINQ, no boxing,
   no closure capture in loops, no `GetComponent` inside a tick.
3. **Determinism.** Simulation code never reads `UnityEngine.Time` or
   `UnityEngine.Random`. It takes `SimTime` and uses `DeterministicRandom`, seeded
   per (session, entity, purpose).
4. **Bounded worst case.** AI runs on a budgeted scheduler with per-agent LOD
   buckets, so adding agents degrades *update frequency*, not frame time.
5. **The Core assembly is a contract.** Changing a signature in
   `Assets/Scripts/Core/Contracts/` is a breaking change across six assemblies and
   is reviewed as such.

---

## Status

**Playable.** Verified on Unity 6000.0.25f1:

- compiles clean headlessly
- **49/49 EditMode tests pass**
- **7/7 PlayMode tests pass** — 5 audio (clip synthesis, voice-pool priority,
  subtractive mix rule, occlusion exemptions) and 2 end-to-end, which boot the
  real Bootstrap scene,
  start a real host, load the level through Netcode's scene manager, and assert
  the NavMesh baked, the player spawned with a camera, the antagonist is running
  its brain, and the simulation clock is advancing
- `Vigil ▸ Generate Playable Sample` runs to completion in batch mode

A recorded smoke run shows the antagonist going `Dormant` →
`Antagonist/Investigate/Investigate` — the nested hierarchical FSM path — after
perception woke it, with the mission reading `Restore power 0/3`.

### How to play

WASD move · mouse look · Shift sprint · Ctrl crouch · **E** interact ·
**F** flashlight · Esc release cursor · **~** debug overlay

Host from the main menu, repair 3 generators (hold **E** — loud, staged, and
progress persists), then reach the exit at the centre of the facility. Doors can
be shut behind you and genuinely carve the NavMesh, so the entity has to path
around them.

### Implemented

| Area | What exists |
|---|---|
| **Core** | Contracts, hierarchical FSM engine, fixed-step clock, budgeted/LOD tick scheduler, per-instance event bus, service context, pooling, deterministic PRNG, categorised logging |
| **Data** | 9 tuning configs + validating registry |
| **AI — pathfinding** | Async versioned-handle `NavigationService`, `RegionGraph` (Floyd–Warshall, reachability diagnostics), `PathFollower` with corner smoothing + stuck detection, vent/window/ledge link actions |
| **AI — perception** | Spatial-hash `PerceptionBus`, `AwarenessModel` with hysteresis, vision (3-band, time-based), hearing (occlusion + deliberate localisation error), scent trail, touch |
| **AI — behaviour** | 14 states, `AntagonistBrain` hierarchical graph with priorities/dwell/cooldowns, `SteeringSolver`, `NpcAgent` with awareness-driven LOD |
| **AI — director** | Full Buildup/Peak/Relax/Fadeout curve, stress aggregation, mandatory-disengage clamp, target preference |
| **Networking** | `NetworkAuthority`, `SessionDriver` (direct IP + dedicated + pluggable relay, build-hash approval), `InterestManager` with hysteresis |
| **Gameplay** | `PlayerCharacter` (server-authoritative movement, `IPerceivable` stealth surface, noise emission, composure, downed/revive), `PlayerCameraRig` (mouse look, gait head-bob, composure-driven breathing sway, FOV kick, shake), `PlayerRegistry` |
| **Interaction** | Server-revalidated `InteractionSystem`, `Door` (NavMeshObstacle carving, breachable), `Generator` (staged persistent repair, loud), `ExtractionPoint` |
| **Mission** | `MissionDirector` — objective chain, extraction arming, wipe detection, and the `IDarknessSampler` light model that drives composure drain, AI vision and search preference |
| **UI** | Host/join menu, crosshair, interaction prompt with hold ring, objective tracker, composure vignette, vitals, ~ (backquote) debug overlay (IMGUI placeholder — see below) |
| **Audio** | Fully procedural — 15 clips synthesised at boot, **zero binary assets**. Pooled voices with priority-based stealing, round-robin occlusion (log-space cutoff + attenuation), subtractive adaptive score, cooldown-gated stingers, composure-driven breathing and heartbeat, footsteps derived from replicated displacement (no per-step bandwidth) |
| **Bootstrap** | `GameBootstrap`, `SimClockRunner`, `VigilLevelSpawner` (runtime NavMesh bake, region graph, spawning) |
| **Editor** | Full content generator (configs, materials, prefabs, level, NavMesh, scenes, build settings), project-settings configurator |
| **Tests** | 49 EditMode tests over FSM semantics, clock, scheduler budgeting/starvation, PRNG, event-bus isolation, blackboard, awareness hysteresis, director invariants |

### Documented but not yet implemented

These are specified in `docs/` and have contracts in `Core`, but no implementation
behind them yet. **The docs describe the intended design, not shipped code:**

- **Audio: portal propagation** — occlusion is implemented (raycast blocker count →
  low-pass + attenuation), but sound still travels in a straight line. A noise
  around a corner arrives through the wall rather than from the doorway.
  `docs/AI_DESIGN.md` and `ARCHITECTURE.md` describe the portal-based version.
- **Audio: recorded material** — every clip is synthesised. The drone bed and
  heartbeat are parametric by design and should stay; footsteps, doors and
  stingers should be replaced with recorded audio before ship.
- **UI Toolkit** — the HUD works but is IMGUI. `docs/ARCHITECTURE.md` specifies UI
  Toolkit; that migration has not happened. The state it reads is already correct.
- **Full prediction/reconciliation** — `PlayerCharacter` runs the movement step on
  owner and server, but does not yet replay buffered inputs on correction, so
  high-latency clients will see correction snapping
- **World systems** — lockers/hiding volumes, inventory, flares, medkits, save
- **AI extras** — utility scorers, squad coordination, ambient NPCs
- **UGS Relay backend** — `IRelayBackend` exists; only the direct-IP path is implemented
- Editor debug window, project validation and build-pipeline tools
- **Known issue:** scenes still serialize as binary despite `ForceText`, so they
  are not diffable yet. Prefabs and assets are text.

Art, animation and audio assets are placeholders — the sample level is built from
primitives so the repository stays engine-verifiable without binary dependencies.

## Licence

Proprietary. All rights reserved.
