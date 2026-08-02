# Contributing — Project Vigil

## Before you push

```bash
pwsh ./Tools/verify-compile.ps1 -RunTests
```

Compile must be clean and all EditMode tests must pass. This is the same command
CI runs, so a green local run means a green PR.

---

## Environment

- **Unity 6000.0.25f1** exactly. The version is pinned in
  `ProjectSettings/ProjectVersion.txt`; a different editor will silently upgrade
  serialized assets and produce an unreviewable diff.
- Package versions are pinned in `Packages/manifest.json`. Do not float them.

There are **no hand-authored scenes, prefabs or `.asset` files** in this repo.
Run `Vigil ▸ Generate Playable Sample` to produce them locally. If you need to
change generated content, change the generator — a hand-edit will be overwritten
and, worse, will not reproduce on anyone else's machine.

---

## Code standards

- **C# 9.** No file-scoped namespaces, no `record`, no `init`, no global usings.
- 4-space indent, Allman braces, `_camelCase` private fields, `[SerializeField]`
  for inspector-exposed privates.
- Every public type gets a `/// <summary>`.
- **Comment the *why*, not the *what*.** Assume a senior engineer is reading. A
  comment explaining that `i++` increments `i` is noise; a comment explaining why
  a transition has a 1-second minimum dwell is the most valuable line in the file.

---

## The rules that are actually enforced in review

### 1. Server authority
Every simulation write is gated on `IsServer`, obtained from `INetworkAuthority` —
never from `NetworkManager.Singleton` directly. A client that simulates the
antagonist is a client that knows where it is.

### 2. No per-tick allocation
No LINQ, no boxing, no closure capture in loops, no string concatenation, no
`GetComponent`/`Find` in anything reachable from `OnSimTick`. Physics queries use
`*NonAlloc` with pre-allocated buffers and explicit `LayerMask`s — never the
default "everything".

### 3. Determinism
Simulation code never reads `UnityEngine.Time` (it receives `SimTime`) and never
uses `UnityEngine.Random` (it uses `DeterministicRandom`). Breaking this makes bug
reports unreproducible, which is far more expensive than it sounds.

### 4. `Vigil.Core` is a contract
Changing a signature under `Assets/Scripts/Core/Contracts/` is a breaking change
across six assemblies. Say so in the PR description and expect a slower review.

### 5. Assembly boundaries
Dependencies flow one way: `Core ← Data ← Net ← AI ← Gameplay ← UI ← Bootstrap`,
with `Audio` depending only on `Core` and `Data`. If you find yourself wanting to
reference "upward", the thing you want belongs in `Core/Contracts` as an interface.

### 6. No state may idle forever
Every `IState<AgentContext>` must handle: unreachable goal, target despawned
mid-state, and a path request rejected because the queue was saturated. An agent
standing still with no error is the most immersion-breaking AI failure there is,
and it never throws, so nothing catches it but review.

---

## Tuning vs. code

If a value would ever be playtested, it belongs in a `Vigil.Data` config, not in
a `const`. Response curves belong in `AnimationCurve` fields — designers expect
curves, not magic numbers.

---

## Design invariants that are NOT bugs

These make the antagonist worse at winning. That is intentional, they are covered
by tests, and PRs that "fix" them will be rejected:

| Invariant | Why | Test |
|---|---|---|
| The Director forces `Relax` on a timer even while winning | Fear is a derivative; removing the lull removes the fear | `DirectorTests.MaximumPhaseDurationForcesRelaxEvenWhileStressIsHigh` |
| Sound localises badly through walls | The monster searching the wrong room is the best tension in the game | — (`PerceptionConfig.SoundLocalisationError`) |
| The Director targets the *calm, isolated* player, not the weakest | Pressuring a broken player produces a rout, not a story | `DirectorTests.PreferredTargetFavoursTheIsolatedAndCalmPlayerOverTheBrokenOne` |
| The entity cannot turn during an attack's commit window | Otherwise the correct play is "hold W" and dodging is meaningless | — (`AgentArchetypeConfig.AttackCommit`) |
| Awareness decay is damped near a threshold | Prevents FSM thrash that players read as a broken monster | `AwarenessModelTests.HysteresisPreventsBandFlickerAtAThreshold` |
| Detection is time-based, never instant | Instant detection reads as the AI cheating even when it is fair | — (`PerceptionConfig.MinTimeToDetect`) |

If you believe one of these is wrong, that is a design conversation — open an
issue, don't open a PR.

---

## Branches and commits

- Branch from `develop`. `main` is release-only.
- `feat/`, `fix/`, `perf/`, `refactor/`, `docs/` prefixes.
- Reference the design doc section a behavioural change affects.

## PR checklist

- [ ] `verify-compile.ps1 -RunTests` is green
- [ ] No new per-tick allocations
- [ ] Simulation writes gated on `IsServer`
- [ ] New tuning values live in a `Vigil.Data` config
- [ ] New states handle failure paths
- [ ] Docs updated if behaviour or an invariant changed
