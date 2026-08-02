# Performance Budgets — Project Vigil

Budgets are meaningless unless something enforces them. Each one below names the
mechanism that does, and the test that proves it.

---

## 1. Frame budget

Target **60 fps (16.6 ms)** on the minimum spec, of which simulation gets **4 ms**.

| System | Budget | Enforced by |
|---|---|---|
| Path queries | **1.2 ms/tick** | `NavigationConfig.QueryBudgetMs` — the pending queue drains under a `Stopwatch` and stops mid-drain |
| AI tick (all agents) | **2.0 ms/tick** | `TickScheduler.BudgetMilliseconds` — overflow defers and *resumes from the deferral point* |
| Interest management | amortised | slow tick at `InterestUpdateInterval` (0.4 s) |
| Perception broadcast | O(receivers in range) | uniform spatial hash in `PerceptionBus` |

The important property is not the numbers — it is that **worst case is bounded**.
Adding agents degrades AI *update frequency*, not frame time. That is what the
budget + resume-cursor combination buys, and `TickSchedulerTests` asserts the
deferral resumes without starving anyone.

## 2. AI LOD

| Bucket | Divisor | Applied to |
|---|---|---|
| `Critical` | 1 | agent with a `Confirmed` target |
| `High` | 2 | `Alerted` or `Lost` |
| `Normal` | 4 | `Suspicious`, or Director pressure > 0.05 |
| `Low` | 8 | `Unaware` during downtime |
| `Dormant` | never | no player within range |

`NpcAgent.DesiredBudget` keys off **awareness, not distance**. Distance-only LOD
demotes exactly the agent the players are about to run into.

Phase offsets de-synchronise agents inside a bucket, so 40 agents never think on
the same tick. Without that, frame time is a sawtooth even while the *average*
stays inside budget.

## 3. Allocation budget

**Zero steady-state GC allocation** in the simulation loop.

| Risk | Mitigation |
|---|---|
| Awareness model | fixed 8-entry array, no `Dictionary` |
| Path results | `Pool<NavPath>`, capacity = max concurrent queries |
| Path corners | `NavMeshPath.GetCornersNonAlloc` into a reused buffer |
| Physics queries | `*NonAlloc` with pre-allocated buffers, explicit `LayerMask` |
| Stimulus routing | reused scratch index list in `PerceptionBus` |
| Blackboard | one typed store per primitive — no boxing |
| Event payloads | structs, `in` parameters, per-instance channels |
| Logging | `VLog.Is()` guard before any interpolated string; `[Conditional]` on the call itself |

**Review rule:** no LINQ, no `foreach` over a `Dictionary`, no closure capture
inside a loop, and no `string` concatenation anywhere reachable from `OnSimTick`.

## 4. Bandwidth budget

Target **≤ 16 KB/s per client** at 4 players.

| Stream | Rate | Size |
|---|---|---|
| Input command (client→server) | 30 Hz | 11 bytes + redundancy (×3) |
| Player transform | 20 Hz | quantised position + yaw |
| Antagonist transform | 20 Hz | **only to clients that may perceive it** |
| Antagonist state + awareness band | on change | 2 bytes |
| World state (doors, power, objectives) | event-driven | — |

Interest management is the dominant lever. It is also an **anti-cheat boundary**:
a client that never receives the entity's transform cannot render it through a
wall. See [`NETWORKING.md §4`](NETWORKING.md).

Never replicated: awareness models, path corridors, Director internals, region
search memory.

## 5. Memory

| Item | Budget |
|---|---|
| `AwarenessModel` per agent | 8 targets + 6 investigation points, fixed |
| `NavPath` pool | `MaxConcurrentQueries` × 2, 128 corners each |
| Region graph | O(n²) cost matrix, n = rooms (tens) → a few KB |
| Scent trail | 128-entry ring buffer, shared |
| Audio emitters | `AudioConfig.EmitterPoolSize` (24), with voice stealing |

Every one of these is a fixed allocation made once at startup. There is no
unbounded collection anywhere in the simulation path.

## 6. Load time

| Step | Budget | Notes |
|---|---|---|
| NavMesh bake | < 500 ms | done at level load in `VigilLevelSpawner.BakeNavMesh` |
| Region graph build | < 5 ms | Floyd–Warshall over tens of nodes |
| Service construction | < 10 ms | `GameBootstrap`, one pass |

## 7. How to verify

```bash
pwsh ./Tools/verify-compile.ps1 -RunTests
```

For runtime profiling, use `Vigil ▸ AI Debug Window` while playing — it surfaces
`TickScheduler.LastDeferredCount` and `NavigationService.PendingQueryCount`.
Sustained non-zero deferral means the AI budget is genuinely saturated, not that
something is broken.

**Regression rule:** any PR that raises a budget constant must say why in the
description. The constants are the design; raising one silently converts a bounded
system into an unbounded one.
