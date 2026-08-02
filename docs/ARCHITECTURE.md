# Architecture — Project Vigil

---

## 1. Assembly graph

Ten assemblies, with dependencies flowing strictly one direction. This is enforced
mechanically by `.asmdef` references, not by convention — a violating `using`
fails the build.

```
                    ┌──────────────┐
                    │  Vigil.Core  │   contracts, FSM engine, sim clock,
                    └──────┬───────┘   event bus, pooling, PRNG, logging
                           │
                    ┌──────▼───────┐
                    │  Vigil.Data  │   ScriptableObject tuning layer
                    └──────┬───────┘
              ┌────────────┼────────────┐
              │            │            │
       ┌──────▼─────┐ ┌────▼──────┐     │
       │ Vigil.Net  │ │Vigil.Audio│     │
       └──────┬─────┘ └───────────┘     │
              │                          │
       ┌──────▼─────┐                    │
       │  Vigil.AI  │◀───────────────────┘
       └──────┬─────┘
              │
     ┌────────▼────────┐
     │ Vigil.Gameplay  │
     └────────┬────────┘
              │
       ┌──────▼─────┐
       │  Vigil.UI  │
       └──────┬─────┘
              │
    ┌─────────▼────────┐        ┌──────────────┐
    │ Vigil.Bootstrap  │        │ Vigil.Editor │ (Editor platform only)
    └──────────────────┘        └──────────────┘
```

### Why these boundaries

- **`Vigil.Core` is a frozen contract layer.** Changing a signature in
  `Core/Contracts/` is a breaking change across six assemblies and is reviewed as
  one. Everything else codes against interfaces declared there, which is what let
  the whole system be built by parallel teams without integration drift.

- **`Vigil.Core` has no networking dependency.** AI, gameplay and audio all need
  to ask *"am I the authority?"* and *"what tick is the server on?"*. None of them
  should become uncompilable or untestable without a networking package present,
  so those questions are asked through `INetworkAuthority`, declared in Core and
  implemented in `Vigil.Net`.

- **`Vigil.Audio` sees only Core and Data.** It cannot reference `Vigil.Gameplay`
  or `Vigil.AI`. It reacts to events on the `IEventBus`, never to objects. This
  single constraint is what stops audio becoming the cross-cutting dumping ground
  it becomes on most projects.

- **`Vigil.AI` cannot see `Vigil.Gameplay`.** The AI damages an `IDamageable` and
  perceives an `IPerceivable`; it has no idea what a `PlayerCharacter` is. That is
  what allows the AI to be unit-tested with no scene and no player.

---

## 2. Simulation model

### Three clocks

| Clock | Rate | Drives |
|---|---|---|
| Render (`Update`/`LateUpdate`) | uncapped | camera, VFX, interpolation, UI |
| `SimClock` | 30 Hz fixed | all gameplay and AI simulation |
| Unity physics | 60 Hz fixed | character controller fidelity |

Simulation is deliberately **not** in `Update` and **not** in `FixedUpdate`:

- Not `Update`, because behaviour must be identical at 30 fps and 240 fps, and the
  headless server runs at neither.
- Not `FixedUpdate`, because that couples AI cognition rate to physics rate. We
  want physics at 60 Hz and cognition at 30 Hz, where it is imperceptibly cheaper.

`SimClockRunner` accumulates real time in `Update`, emits whole fixed ticks, and
dispatches them through `TickScheduler`. `SimClock.Alpha` is exposed so render-side
code interpolates simulated transforms — without it, 30 Hz simulation looks like
30 fps no matter the framerate.

### The tick scheduler

`TickScheduler` is the reason agent count scales. Naive `foreach (agent) agent.Think()`
produces a sawtooth frame time that becomes visible stutter as soon as agent count
rises, and gives you no way to bound worst case.

Three mechanisms:

1. **LOD buckets** — `Critical`/`High`/`Normal`/`Low`/`Dormant` map to tick
   divisors 1/2/4/8/never. `NpcAgent` implements `IAdaptiveTickable` and
   re-declares its bucket from its own awareness band, so LOD tracks *narrative
   relevance*, not just camera distance.
2. **Phase offsets** — agents within a bucket are staggered so forty agents never
   think on the same tick.
3. **A millisecond budget** — when exceeded, remaining tickables defer to the next
   tick and **resume from the deferral point**, so nothing starves. Adding agents
   degrades update *frequency*, not frame time.

A tickable that throws is evicted rather than being allowed to kill the loop.

### Determinism

Simulation code never reads `UnityEngine.Time` (it receives `SimTime`) and never
uses `UnityEngine.Random` (it uses `DeterministicRandom`, xoshiro128\*\*, seeded per
`(sessionSeed, entityId, purpose)`).

Independent streams per purpose matter: adding a random call to one subsystem must
not perturb another's sequence, or every tuning change silently invalidates every
repro case. The session seed is replicated by `SessionSeedSync` so every machine
agrees.

---

## 3. Composition root

There is exactly one: `GameBootstrap`.

```csharp
var ctx = new ServiceContext("session");
ctx.Register<IEventBus>(new EventBus());
ctx.Register(new SimClock(tickRate));
ctx.Register(new TickScheduler());
ctx.Register<INetworkAuthority>(new NetworkAuthority());
ctx.Register<ISessionDriver>(new SessionDriver(...));
ctx.Register<INavigationService>(new NavigationService(...));
ctx.Register<IRegionGraph>(new RegionGraph(...));
ctx.Register<IPerceptionBus>(new PerceptionBus(...));
ctx.Register<IAIDirector>(new AIDirector(...));
ctx.Register<IInterestManager>(new InterestManager(...));
Services.SetActive(ctx);
```

`ServiceContext` is explicitly **not** a set of static singletons. Host and client
run in the same process during playtests and under Multiplayer Play Mode virtual
players, and "one global instance per system" breaks down immediately there. It
also makes PlayMode tests deterministic instead of leaking state between cases.

Teardown disposes in reverse registration order, so dependents die before their
dependencies.

`GameBootstrap` installs via `[RuntimeInitializeOnLoadMethod]`, so it exists even
when a developer presses Play directly in a gameplay scene. A bootstrap that only
works if you start from the menu scene costs a team hours every single day.

---

## 4. Data flow, one AI tick

```
SimClockRunner.Update
  └─ SimClock.Advance(dt) → N ticks
       └─ TickScheduler.Tick(simTime)          [budgeted, staggered]
            ├─ AIDirector.OnSimTick             aggregate stress → DirectorIntent
            ├─ NavigationService.OnSimTick      drain path queue under ms budget
            ├─ InterestManager.OnSimTick        (slow tick) NetworkShow/Hide
            └─ NpcAgent.OnSimTick               [server only]
                 ├─ PerceptionRig.Tick          sensors → AwarenessModel
                 │    ├─ VisionSensor           pull: cones + LOS + darkness
                 │    ├─ HearingSensor          push: drain ring buffer + occlusion
                 │    ├─ ScentSensor            trail gradient
                 │    └─ TouchSensor            contact
                 ├─ read DirectorIntent         pressure, authorisation, standoff
                 ├─ StateMachine.OnTick         guards → transition → state tick
                 │    └─ state issues RequestPathTo / Attack / EmitStimulus
                 ├─ PathFollower                corridor → DesiredVelocity
                 ├─ SteeringSolver              + separation + obstacle avoidance
                 └─ IAgentBody.Move             locomotion → replicated transform
```

Everything above the `IAgentBody.Move` line is pure logic over interfaces, which
is why the AI is testable headlessly.

---

## 5. Threading

**Single-threaded by design.** All simulation runs on the main thread.

This is a deliberate trade. Unity's `NavMesh`, `Physics` and `Transform` APIs are
main-thread-only, so a job-based AI would have to marshal every query anyway, and
the marshalling would eat most of the gain at this agent count. Meanwhile the tick
scheduler already bounds worst-case cost, which is the actual problem
multithreading would have been solving.

`Pool<T>` and `EventBus` are therefore not thread-safe on purpose — a lock would
cost more than it saves and would *hide* accidental cross-thread access instead of
surfacing it as a crash during development.

Burst/Jobs remain available (`com.unity.burst`, `com.unity.collections` are
referenced) for genuinely parallel work — batch LOS resolution and spatial hash
rebuilds are the identified candidates if profiling ever demands it.

---

## 6. Content generation instead of authored assets

The repository contains **no** `.unity`, `.prefab`, or `.asset` files.

Hand-written Unity YAML carries GUIDs that will not match the ones Unity assigns
on import, which produces references that are silently null — the failure appears
at runtime, far from the cause. Instead `VigilContentGenerator` builds everything
from code via `AssetDatabase`:

- the nine ScriptableObject configs and the registry
- URP pipeline + renderer assets, wired into graphics settings
- materials, `PanelSettings` for the UI Toolkit HUD
- both scenes and the facility level geometry
- the `NavMeshSurface` and its bake
- `RegionGraphData` derived from region volumes + NavMesh connectivity
- player/NPC prefabs, registered into the `NetworkManager` prefab list

Consequences: the repo is small and diffable, the level is reproducible from
source, and a regenerate is always correct. `Vigil ▸ Validate Project` checks the
result.

---

## 7. Error handling posture

| Situation | Response |
|---|---|
| A tickable throws | evict it, log, keep the loop alive |
| An event subscriber throws | unsubscribe it, others still receive |
| A path request fails | states must handle `Failed`/`PartialSuccess`; no state may idle forever |
| Path queue saturated | `PathHandle.Invalid`; caller degrades, never blocks |
| Stale path handle polled | version mismatch → fails safely, cannot read another agent's path |
| Config asset missing | `VigilConfigRegistry.Validate()` logs it; systems use documented defaults |
| Audio assets missing | degrade silently — the sample level ships without them |
| Build hash mismatch on join | reject with a `DisconnectReason` the UI can display |

The theme: **one broken thing must not cascade**. In a 4-player session, a single
exception taking down the simulation loop ends everyone's run.

---

## 8. Testing strategy

| Layer | Where | What |
|---|---|---|
| Pure logic | EditMode | FSM semantics, clock stepping, scheduler budgeting/starvation, PRNG determinism, event-bus isolation, awareness hysteresis, director phase clamps, handle versioning |
| Integration | PlayMode | navigation request/poll lifecycle, prediction replay, agent spawn/despawn registration leaks |
| Compile gate | `Tools/verify-compile.ps1` | headless full compile, parsed error report |
| CI | GitHub Actions | the above on every push |

The EditMode suite deliberately targets the properties that **degrade silently**:
scheduler starvation, awareness oscillation, forced-Relax being optimised away,
event-bus cross-wiring between host and client. None of these produce an
exception; all of them produce a game that feels subtly wrong.

---

## 9. Extension points

| To add… | Do this |
|---|---|
| A new antagonist | new `AgentArchetypeConfig` + optionally new `IState<AgentContext>` implementations; reuse `AntagonistBrain` or compose a new graph |
| A new sense | implement `ISensor`, add it to the `PerceptionRig` |
| A new traversal | implement `INavLinkAction`, add a NavArea index |
| A new objective | implement `IInteractable`, register with `ObjectiveSystem` |
| A new pacing model | implement `IAIDirector`; nothing else changes |
| A different transport | implement `ISessionDriver`; the rest of the stack is transport-agnostic |
