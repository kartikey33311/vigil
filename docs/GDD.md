# Game Design — Project Vigil

**Genre:** co-operative survival horror · **Players:** 1–4 · **Session:** 20–35 min
**Perspective:** first person · **Combat:** none (evasion and denial only)

---

## 1. Premise

Four contractors enter a decommissioned research facility to restore power and
recover a data core. The facility is not empty. The thing inside cannot be killed,
only avoided, delayed and misdirected.

## 2. The core loop

```
        ┌──────────────────────────────────────────────┐
        │                                              │
   EXPLORE ──► LOCATE generator ──► REPAIR (loud, slow) ──► POWER a region
        ▲                │                      │                │
        │                │                      ▼                ▼
        │                │              attracts the entity   light returns
        │                │                      │            (composure ↑,
        │                ▼                      ▼             visibility ↑)
        └──────── HIDE / EVADE ◄──── CHASE ◄────┘
                          │
                          ▼
              all generators → EXTRACTION window
```

Every objective is **loud**. That is the whole design: progress and safety are
directly opposed, and the players choose the exchange rate.

## 3. The central tensions

| You want | It costs you |
|---|---|
| **Light** — to see, navigate, and recover composure | Roughly doubles how visible you are (`VisibilityScale`) |
| **Speed** — to cover ground and escape | Sprint noise radius is ~6× crouch, and drains stamina |
| **Together** — teammates restore composure and can revive | A group is loud, and the Director targets isolated players *less* |
| **Progress** — generators, doors, the data core | Every one of them emits a stimulus the entity hears |

None of these have a correct answer. That is the point.

## 4. Resources

### Health / Downed
Two hits down you; downed starts a ~55s bleed-out. A teammate revives in ~5.5s.
Bleed-out is deliberately long — the interesting decision is *whether the others
come back for you while the entity is still nearby*, and a short timer removes
that decision by making it obviously impossible.

### Composure (the horror resource)
0–100. Drains in darkness, near the entity, when isolated, and on witnessing a
teammate go down. Recovers in light and near teammates.

It is **not** a fail state — it is a feedback loop. Low composure raises
`NoiseMultiplier`, so a panicking player is *literally easier for the AI to hear*.
Fear becomes mechanically real rather than purely presentational.

### Stamina
Gates sprinting, with a regen delay so sprint is never free.

### Items (4 slots)
Keys, batteries, medkits, flares. **Flares stun the entity** — the only real
counterplay — and simultaneously emit light and a loud stimulus. Buying four
seconds of safety costs you your position.

## 5. The antagonist

One entity, always. It cannot be killed. Full behavioural detail is in
[`AI_DESIGN.md`](AI_DESIGN.md); the design-facing summary:

- It builds **awareness** of you continuously; it is never simply "aware" or "not".
- It **mis-hears**. Sound through walls localises badly, so it searches the wrong
  room, and you watch it do so.
- It **follows trails**. Running in a straight line is a mistake; doubling back works.
- It **uses vents**. No space is permanently safe.
- It **leaves**. The Director forces disengagement on a timer.

## 6. Pacing

The Director drives Buildup → Peak → Relax → Fadeout (see
[`AI_DESIGN.md §5`](AI_DESIGN.md)). Design consequences:

- Roughly 60–70% of a session has **no contact**. That is correct. The quiet is
  what makes contact land.
- The entity is **forbidden from engaging** outside Peak, even when it knows
  exactly where you are. During Buildup it stalks — heard, not seen.
- The Director pressures the **isolated and composed** player, not the weakest.
  Pressuring a broken player produces a rout and a flat ending; pressuring the
  confident one produces the run people retell.

## 7. Level design rules

1. **No dead ends without a hiding spot.** A dead end with no out is a coin flip,
   not a decision.
2. **Every room has two exits, minimum** — except designated ambush rooms, which
   have exactly one and are always well lit as a tell.
3. **Vent links connect non-adjacent regions.** Players must never be able to
   fully model the entity's traversal graph from the walkable one.
4. **Generators sit in high-enclosure, low-visibility rooms.** The objective
   should feel expensive before it is.
5. **Light is scarce and directional.** Ambient is near-zero by design
   (`Ambient` directional light at 0.12 intensity in the sample level).

## 8. Failure and success

| Outcome | Condition |
|---|---|
| **Extraction** | All generators repaired, data core recovered, ≥1 player reaches the exit within the extraction window |
| **Partial** | Extraction with players left behind (they are lost) |
| **Wipe** | All players downed or dead simultaneously |

During the extraction window Director pressure is **unclamped** — the only time
the game is allowed to be unfair, and the reason extraction feels like a sprint
rather than a formality.

## 9. Difficulty

Difficulty is not a health multiplier. It shifts `DirectorConfig`:

| Tier | Peak max duration | Relax cooldown | Standoff (Buildup) |
|---|---|---|---|
| Standard | 45s | 18s | 22m |
| Harsh | 60s | 12s | 16m |
| Vigil | 75s | 6s | 11m |

Longer Peaks, shorter lulls, closer stalking. The entity is not faster or
stronger — it is simply *around* more.

## 10. Explicit non-goals

- **No weapons.** The moment the entity can be fought, it stops being a threat and
  becomes an encounter.
- **No jump-scare spam.** The `StingerSystem` enforces per-cue cooldowns precisely
  so the game cannot machine-gun scares.
- **No permanent progression that trivialises stealth.** Unlocks change options,
  never noise or visibility floors.
