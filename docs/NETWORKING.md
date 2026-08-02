# Networking — Project Vigil

Netcode for GameObjects 2.4.0 over Unity Transport 2.4.0. Supports listen-server
(host), dedicated server, and Relay-brokered sessions.

---

## 1. Authority model

**Server-authoritative, without exception.**

This is not a performance position, it is a design requirement. The entire game
rests on players not knowing where the antagonist is. A client that is *told*
where the monster is can be made to render it through walls, and at that point
there is no game left to protect. So:

- Clients send **input intent**. They never send position, damage, pickups, or
  interaction results.
- The server simulates AI, movement, damage, doors, power, objectives and line of
  sight, and replicates outcomes.
- The server decides **what each client is even told about** (§4).

Every system takes `INetworkAuthority` rather than reading `NetworkManager.Singleton`
directly. That keeps systems headlessly testable and makes them behave correctly
under Multiplayer Play Mode, where host and client share a process.

```csharp
if (!_authority.IsServer) return;   // the first line of every simulation method
```

---

## 2. Timebase

Three clocks, deliberately separated:

| Clock | Rate | Purpose |
|---|---|---|
| Render | uncapped | drawing, camera, VFX |
| `SimClock` | 30 Hz | gameplay + AI simulation |
| NGO network tick | 30 Hz | replication and RPC ordering |

`SimClock` is decoupled from both render framerate and Unity's `FixedUpdate`:

- **From render** — AI must behave identically at 30 fps and 240 fps. The server
  is headless and runs at a different rate from every client.
- **From physics** — we can raise physics to 60 Hz for character-controller
  fidelity while keeping AI cognition at 30 Hz, where it is imperceptibly cheaper.

`SimClock.Alpha` (fraction through the current tick) is exposed so render-side
code can interpolate simulated transforms. Without it, 30 Hz simulation *looks*
like 30 fps regardless of actual framerate.

`NetTickSynchroniser` maps NGO's tick onto `SimClock` and estimates the server
tick on clients as `localTick + RTT/2`, smoothed. Prediction and interpolation
both key off this.

### Spiral-of-death protection

`SimClock.Advance` executes at most `MaxCatchUpTicks` (default 5) per frame. Past
that it **drops** the backlog rather than compounding it. For a real-time game
that is the correct failure mode: better to slow down than to lock up trying to
catch up. `SimClockTests` covers it.

---

## 3. Client prediction and reconciliation

Without prediction, every footstep waits a full RTT. With naive prediction, every
correction is visible as jitter. The pipeline:

```
OWNER CLIENT                          SERVER
────────────                          ──────
sample input ──┐
               ├─ store (tick, input, state)
apply locally ─┘   in ring buffers
               │
       send command(s) ─────────────▶ buffer per client
                                      execute ONE per server tick
                                      through the SAME step function
                                      │
       ◀───────── ack(tick, state) ───┘
       │
compare against stored prediction[tick]
  error > threshold ? snap + REPLAY inputs tick+1..now
  error ≤ threshold ? do nothing
```

### The shared step function

`IMovementStepper.Step(in PredictionState, in PlayerInputCommand, float dt)` is
executed on **both** client and server and must be **pure**:

> Any divergence between the client's and the server's execution of this function
> is *the* root cause of reconciliation jitter.

So it never reads `Time`, never reads `Random`, and never reads scene state beyond
physics queries it performs identically on both sides. `CharacterMotor` implements
it, and the purity constraint is called out in that file specifically so it
survives future edits.

### Deliberate non-correction

Below the error threshold, **nothing happens**. Correcting sub-centimetre error
every tick produces continuous visible jitter, which is strictly worse than the
error being corrected. Above the threshold, large corrections are smoothed over a
few frames rather than teleporting the camera.

### Input redundancy

Each message carries the last N unacknowledged commands. A single dropped packet
therefore does not stall the server's input queue. If a client starves the jitter
buffer, the server repeats the last input rather than freezing that player —
freezing looks like a crash, repeating looks like lag.

### Anti-flood

The server clamps commands executed per tick. A client that floods input cannot
move faster than one that does not.

---

## 4. Interest management

`IInterestManager` decides, per entity per client, whether that entity is
replicated at all — via `NetworkObject.NetworkShow` / `NetworkHide`.

**This is an anti-cheat boundary first and a bandwidth optimisation second.**

Inputs to the decision:

- Distance
- Region adjacency via `IRegionGraph` (a client two sealed rooms away has no
  legitimate need for the monster's transform)
- For the antagonist specifically: whether that client could *plausibly perceive* it

Evaluation runs on a slow tick, with **hysteresis** on the boundary so an entity
hovering at the edge does not show/hide repeatedly — which would be both a
bandwidth spike and a visible pop.

`ForceVisible(entity, client, seconds)` exists for scripted reveals and expires
automatically.

---

## 5. Transform replication

`NetworkedTransformSync`: server-authoritative, quantised position/rotation,
snapshot-buffered on clients with interpolation depth from `NetworkConfig`.

On packet loss it extrapolates for a **bounded** number of ticks, then **freezes**.
Unbounded extrapolation puts the monster through a wall, and a monster that
briefly hitches is far less damaging than one that visibly clips through geometry.

---

## 6. Session lifecycle

`ISessionDriver` has three entry points — `StartHostAsync`, `StartClientAsync`,
`StartDedicatedServerAsync` — over three backends:

| Backend | When |
|---|---|
| **Relay + Lobby** (UGS) | public matchmaking, NAT traversal |
| **Direct IP** | LAN, playtests, automated tests |
| **Dedicated** | headless authoritative server |

The Relay/Lobby path is guarded by `#if VIGIL_UGS_RELAY` / `VIGIL_UGS_LOBBY` /
`VIGIL_UGS_AUTH`, supplied by `versionDefines` in `Vigil.Net.asmdef`. **The project
compiles and runs with those packages absent**, falling back to direct IP. A build
that cannot compile because an optional online service is missing is a build that
blocks the whole team.

### Connection approval

`NetworkManager.ConnectionApprovalCallback` validates:

- **Build hash** — rejected on mismatch. A client on a different build is a crash
  report waiting to happen, and the resulting bug is nearly impossible to
  diagnose from the symptom. The lobby screen shows the local build hash so a
  support ticket is answerable in seconds.
- **Player cap**
- **Session state** (no joining mid-extraction)

Rejections carry a `DisconnectReason` so the UI can say something true.

### Failure handling

Every async session method catches, converts to `false` + `ConnectionState.Failed`,
and tears down cleanly. A failed join must never leave the game half-connected —
that state is the source of the "it says I'm in the lobby but nothing works" class
of bug.

`SessionSeedSync` replicates `SessionOptions.SessionSeed` so every deterministic
system on every machine agrees.

---

## 7. What is replicated

| Data | Direction | Notes |
|---|---|---|
| `PlayerInputCommand` | client → server | every tick, quantised, redundant |
| Authoritative player state | server → owner | for reconciliation |
| Player transforms | server → clients | interpolated |
| Antagonist transform | server → *permitted* clients | interest-gated |
| Antagonist coarse state + awareness band | server → permitted clients | for audio/animation only |
| Door / power / objective state | server → all | event-driven, not per-tick |
| Composure | server → owner | HUD only |

**Never replicated:** the awareness model, current path corridors, Director
internals, or region search memory. A client has no legitimate use for any of it,
and all of it is exploitable.

---

## 8. Testing

- **In-editor:** Multiplayer Play Mode with 1–3 virtual players. Note that host
  and client share a process — this is exactly why `EventBus` storage is
  per-instance rather than static, and `EventBusTests` asserts that isolation.
- **Headless:** `Tools/verify-compile.ps1 -RunTests`
- **PlayMode:** `PredictionReconciliationTests` asserts that replaying buffered
  inputs after a forced correction lands the client on the server's state, and
  that sub-threshold error is *not* corrected.

---

## 9. Known constraints

- Relay adds ~20–40 ms over direct connection. Acceptable for co-op PvE; it would
  not be for a competitive title.
- Interest management is evaluated on a slow tick, so a very fast entity can be
  briefly visible before being hidden. The hysteresis window is tuned to be
  shorter than human reaction time at the boundary distance.
- NGO's scene management is used for level transitions; late-joining mid-level is
  supported but late-joining mid-extraction is rejected by design.
