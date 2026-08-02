# Deploying the dedicated server

The Vigil server is a **stateful, long-running, UDP** process. That rules out more
hosting than people expect, so the platform choice is the first decision, not the
last.

---

## 1. Why not Vercel / Netlify / Cloudflare Pages

Three independent blockers, any one of which is fatal:

1. **Browsers cannot open UDP sockets.** Netcode for GameObjects runs on Unity
   Transport over UDP. A WebGL client physically cannot speak it without switching
   the transport to WebSockets.
2. **Serverless has no persistent process.** The server is a 30 Hz authoritative
   simulation that must run for the whole match and hold state between packets.
   Functions are request-scoped and time-limited. This is not a configuration
   problem; it is the wrong shape of infrastructure.
3. Those platforms terminate HTTP/TCP only. There is nothing to route UDP to.

They are fine for a landing page. They cannot host the game.

## 1b. Playing in a browser (WebGL) — single player only

A browser **cannot be a server**. Netcode for GameObjects is client-only on WebGL:
there are no UDP sockets, and nothing can bind a listener. Since every gameplay
object in this project is a `NetworkBehaviour` that only initialises when a server
spawns it, "just disable networking" does not work either — nothing would spawn.

The solution is `LoopbackTransport`: a `NetworkTransport` that never touches a
socket. The game starts a **host** on it, so every `NetworkObject` spawns and
every `OnNetworkSpawn` fires, `IsServer` is true, and the full simulation — AI,
Director, mission, audio — runs locally with zero network traffic.

```bash
# Build (10-25 minutes; the emscripten link stage dominates)
Unity.exe -batchmode -quit -nographics -projectPath . -logFile - \
  -executeMethod Vigil.Editor.Tools.VigilBuildPipeline.BuildWebGL
```

Output: `Build/WebGL/`.

### Deploying it

```bash
cp Tools/webgl/vercel.json Build/WebGL/vercel.json
cd Build/WebGL && vercel --prod
```

Any static host works — Vercel, Netlify, GitHub Pages, itch.io.

**Copy `vercel.json` or the page will appear broken.** Unity ships the payload
pre-compressed as `.gz`. A host that serves those without a `Content-Encoding:
gzip` header hands the browser raw gzip bytes: the page loads, the loading bar
never moves, and the console reports a wasm magic-word error. That one missing
header is the most common reason a perfectly good WebGL build looks dead.

The build also enables `decompressionFallback`, so it still runs on a host that
ignores the headers entirely — just more slowly.

### Constraints worth knowing

| | |
|---|---|
| **Multiplayer** | Unavailable. The menu hides Host/Join on WebGL rather than showing dead buttons. |
| **Download size** | Tens of MB. First load is slow; `dataCaching` is on so repeat visits are fast. |
| **Performance** | Single-threaded. The AI budget still applies, but expect lower framerates than the desktop build. |
| **Audio** | Browsers block audio until first user interaction — sound starts after the first click. |

## 2. Why not Railway / Render / most PaaS

They run persistent containers, which solves blocker (2) — but they expose
**TCP/HTTP only**. Without raw UDP ingress the transport cannot connect.

Viable if you switch UnityTransport to WebSocket mode. That is a real option and
it is what you would do to support a browser client, but it costs head-of-line
blocking and a little latency, and it is not what this project is configured for.

## 3. Recommended: Fly.io

Fly is used here because it supports **raw UDP** on a persistent machine.

```bash
fly launch --no-deploy        # once — creates the app from fly.toml
fly ips allocate-v4           # REQUIRED, see below
fly deploy
fly status
fly logs
```

### Two things that will silently break this

**A dedicated IPv4 address is mandatory.** Fly's *shared* IPv4 pool does not
forward UDP. Without `fly ips allocate-v4` the server binds successfully, reports
healthy, and never receives a single packet. There is no error anywhere.

**Bind to `fly-global-services`, not `0.0.0.0`.** Fly routes UDP only to that
special address. `fly.toml` already sets `VIGIL_BIND=fly-global-services`. A
server bound to `0.0.0.0` on Fly listens forever and hears nothing.

Both failures look identical from outside: a running server nobody can join.

## 4. Alternatives that also work

| Platform | Notes |
|---|---|
| **A plain VPS** (Hetzner, DigitalOcean) | Simplest and cheapest. `docker run -p 7777:7777/udp`. You own the uptime. |
| **Unity Multiplay / Game Server Hosting** | Purpose-built: matchmaking, scaling, allocation. The right answer at scale; overkill for a slice. |
| **AWS GameLift / Agones on GKE** | Industry standard for fleets. Significant operational overhead. |

---

## 5. Building the server

The Linux server build requires Unity's **Linux Dedicated Server** module. If it
is not installed locally, install it via Unity Hub (*Add modules → Linux Dedicated
Server Build Support*), or let CI do it — the GitHub Actions workflow builds inside
a game-ci image that already has it.

```bash
# Locally, once the module is installed:
Unity.exe -batchmode -quit -nographics \
  -projectPath . \
  -executeMethod Vigil.Editor.Tools.VigilBuildPipeline.BuildLinuxServer \
  -logFile -
```

Output: `Build/LinuxServer/VigilServer`.

`VigilBuildPipeline` **exits non-zero on failure**. Unity normally returns 0 from a
failed `-executeMethod` build, which means a CI pipeline that trusts the exit
status will happily build a container around a binary that was never produced.

### Container

```bash
docker build -t vigil-server .
docker run --rm -p 7777:7777/udp \
  -e VIGIL_MAX_PLAYERS=4 \
  -e VIGIL_SEED=12345 \
  vigil-server
```

Note `7777/udp`. Publishing `7777` alone gives you TCP and a server nobody can
reach — the single most common mistake here.

---

## 6. Configuration

Environment variables (preferred — container platforms configure this way) with
command-line equivalents that override them:

| Env | Flag | Default | Meaning |
|---|---|---|---|
| `VIGIL_SERVER` | `-server` | — | Run headless as authoritative server |
| `VIGIL_PORT` | `-port` | `7777` | UDP listen port |
| `VIGIL_BIND` | `-bind` | `0.0.0.0` | Listen address (`fly-global-services` on Fly) |
| `VIGIL_MAX_PLAYERS` | `-maxplayers` | `4` | Connection cap, enforced at approval |
| `VIGIL_LEVEL` | `-level` | `Level_Facility` | Scene to load |
| `VIGIL_SEED` | `-seed` | random | Fixes all deterministic systems — set it to reproduce a bug |
| `VIGIL_LOG` | `-vigil-log` | — | Comma-separated `LogCat` names, or `all` / `none` |

---

## 7. Operational notes

- **Do not scale to zero.** A player's connect attempt is a single UDP packet; it
  is dropped while the machine wakes, and they see "cannot connect" rather than
  "starting". `fly.toml` deliberately omits `auto_stop_machines`.
- **Clients must match the build hash.** `SessionDriver` rejects mismatched
  clients at connection approval. Redeploying the server without shipping the
  matching client locks everyone out — this is intended, and the lobby screen
  shows the hash so the support answer takes seconds.
- **`-logFile /dev/stdout` is required in the container.** Without it Unity writes
  to a file inside the container and `docker logs` shows nothing.
- **`VLog.Info`/`Warn` are compiled out of release builds** (they are
  `[Conditional]` on `UNITY_EDITOR`/`DEVELOPMENT_BUILD`). A release server logs
  only errors. Build with `-development` if you need the detail.

---

## 8. What is verified, and what is not

| | Status |
|---|---|
| Server runtime path (session start, bind, level load) | Exercised in the editor by `SessionSmokeTests` |
| Build pipeline correctness, incl. non-zero exit on failure | **Verified** — a real failure was caught and reported |
| Windows client player build | **Verified** — 93.2 MB, exits 0 |
| Linux server binary | **Not built** — module not installed on the dev machine; CI builds it |
| Docker image | **Not built** — Docker not installed locally |
| Fly deployment | **Not performed** |

The Dockerfile and `fly.toml` are written against the documented behaviour of
those platforms but have not been executed. Treat the first `fly deploy` as the
real test, and check `fly logs` for `Server listening.` before assuming success.
