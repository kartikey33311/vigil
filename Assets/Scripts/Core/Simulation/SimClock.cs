// -----------------------------------------------------------------------------
// Vigil — the fixed-step simulation driver and budgeted tick scheduler.
//
// Two responsibilities, deliberately kept in one file because they are a single
// contract:
//
//   SimClock       accumulates real time and emits a whole number of fixed
//                  simulation ticks per frame, with spiral-of-death protection.
//   TickScheduler  distributes registered ITickables across ticks by budget
//                  bucket AND by a per-agent phase offset, so 40 agents never
//                  all think on the same tick. Enforces a soft millisecond
//                  budget with round-robin starvation protection.
//
// The scheduler is the reason this scales: a naive `foreach (agent) agent.Think()`
// in Update() produces a sawtooth frame time that shows up as stutter the moment
// agent count rises. Here, worst-case cost per tick is bounded.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Diagnostics;
using Vigil.Core.Diagnostics;

namespace Vigil.Core.Simulation
{
    /// <summary>
    /// Fixed-timestep clock. Owned by the bootstrap; stepped from Update() by
    /// <see cref="SimClockRunner"/>. Deterministic: given the same sequence of
    /// Advance() calls it produces the same tick sequence.
    /// </summary>
    public sealed class SimClock
    {
        /// <summary>Simulation ticks per second. 30 Hz is the shipping default.</summary>
        public int TickRate { get; }

        /// <summary>Seconds per simulation tick.</summary>
        public float TickInterval { get; }

        /// <summary>Current tick index.</summary>
        public int CurrentTick { get; private set; }

        /// <summary>Simulated seconds since start.</summary>
        public double Elapsed { get; private set; }

        /// <summary>Global time scale. 0 pauses simulation without pausing rendering.</summary>
        public float TimeScale { get; set; } = 1f;

        /// <summary>
        /// Fraction (0..1) through the current tick, for render-side interpolation
        /// of simulated transforms. Without this, 30 Hz AI movement looks like
        /// 30 fps movement no matter the render rate.
        /// </summary>
        public float Alpha { get; private set; }

        /// <summary>
        /// Maximum ticks executed in one Advance(). Prevents the "spiral of death"
        /// where a long hitch queues more simulation than the next frame can run,
        /// which queues more still. Beyond this we drop simulated time on the floor
        /// — correct behaviour for a real-time game (better to slow down than lock up).
        /// </summary>
        public int MaxCatchUpTicks { get; set; } = 5;

        double _accumulator;

        public SimClock(int tickRate = 30)
        {
            TickRate = tickRate < 1 ? 1 : tickRate;
            TickInterval = 1f / TickRate;
        }

        /// <summary>
        /// Feeds real elapsed seconds in and returns how many whole fixed ticks are
        /// now due. Does NOT advance the tick counter — call <see cref="Step"/> that
        /// many times, so each tick gets its own distinct <see cref="SimTime"/>.
        ///
        /// <para>The two-phase split matters: a single Advance() that both consumed
        /// time and moved the counter forced callers to snapshot AFTER the fact, so
        /// every tick in a multi-tick frame saw the same (final) tick index. That
        /// silently breaks anything time-based — cooldowns, dwell times, decay —
        /// exactly on the frames where a hitch made catch-up necessary, which is the
        /// hardest case to reproduce.</para>
        /// </summary>
        public int BeginFrame(float unscaledDeltaSeconds)
        {
            // Clamp pathological deltas (alt-tab, breakpoint, editor domain reload).
            if (unscaledDeltaSeconds > 0.25f) unscaledDeltaSeconds = 0.25f;
            if (unscaledDeltaSeconds < 0f) unscaledDeltaSeconds = 0f;

            _accumulator += unscaledDeltaSeconds;

            int due = 0;
            while (_accumulator - due * (double)TickInterval >= TickInterval && due < MaxCatchUpTicks)
            {
                due++;
            }

            _accumulator -= due * (double)TickInterval;

            if (_accumulator >= TickInterval)
            {
                // Hit the catch-up ceiling. Discard the backlog rather than letting it
                // compound across frames — for a real-time game, slowing down is the
                // correct failure mode; locking up trying to catch up is not.
                int dropped = (int)(_accumulator / TickInterval);
                _accumulator %= TickInterval;
                if (VLog.Is(LogCat.Core))
                {
                    VLog.Warn(LogCat.Core, $"SimClock dropped {dropped} tick(s) — frame hitch or overload.");
                }
            }

            Alpha = (float)(_accumulator / TickInterval);
            return due;
        }

        /// <summary>
        /// Advances exactly one tick and returns its time. Call once per tick
        /// reported by <see cref="BeginFrame"/>.
        /// </summary>
        public SimTime Step()
        {
            CurrentTick = unchecked(CurrentTick + 1);
            Elapsed += TickInterval * TimeScale;
            return Snapshot();
        }

        /// <summary>
        /// Convenience wrapper for tests and headless drivers: runs the whole frame
        /// and reports the final tick. Prefer BeginFrame + Step in the game loop,
        /// where each tick must be dispatched individually.
        /// </summary>
        public int Advance(float unscaledDeltaSeconds, out SimTime lastTime)
        {
            int steps = BeginFrame(unscaledDeltaSeconds);
            for (int i = 0; i < steps; i++) Step();
            lastTime = Snapshot();
            return steps;
        }

        public SimTime Snapshot() => new SimTime(
            CurrentTick,
            Elapsed,
            TickInterval * TimeScale,
            TickInterval,
            TimeScale);

        public void Reset()
        {
            CurrentTick = 0;
            Elapsed = 0;
            _accumulator = 0;
            Alpha = 0f;
        }
    }

    /// <summary>
    /// Budgeted, phase-staggered dispatcher for <see cref="ITickable"/>.
    /// </summary>
    public sealed class TickScheduler
    {
        struct Entry
        {
            public ITickable Target;
            public TickBudget Budget;
            public int PhaseOffset;   // de-synchronises agents in the same bucket
            public bool Alive;
        }

        // Tick divisor per budget bucket, indexed by (int)TickBudget.
        static readonly int[] Divisors = { 1, 2, 4, 8, 0 };

        readonly List<Entry> _entries = new List<Entry>(256);
        readonly Dictionary<ITickable, int> _index = new Dictionary<ITickable, int>(256);
        readonly Stopwatch _watch = new Stopwatch();

        int _nextPhase;
        int _resumeCursor;
        bool _compactPending;

        /// <summary>
        /// Soft per-tick budget in milliseconds. When exceeded, the remaining
        /// tickables are deferred to the next tick and resumed from where we
        /// stopped, so nobody starves. Set to 0 to disable budgeting.
        /// </summary>
        public double BudgetMilliseconds { get; set; } = 2.0;

        /// <summary>Number of tickables deferred on the most recent tick (telemetry).</summary>
        public int LastDeferredCount { get; private set; }

        public int Count => _index.Count;

        public void Register(ITickable tickable, TickBudget budget = TickBudget.Normal)
        {
            if (tickable == null || _index.ContainsKey(tickable)) return;

            _index[tickable] = _entries.Count;
            _entries.Add(new Entry
            {
                Target = tickable,
                Budget = budget,
                // Spread phases across the largest divisor so buckets interleave.
                PhaseOffset = _nextPhase++ & 7,
                Alive = true
            });
        }

        public void Unregister(ITickable tickable)
        {
            if (tickable == null || !_index.TryGetValue(tickable, out int i)) return;

            // Tombstone rather than remove: Unregister is legal from inside a tick
            // (an agent despawning itself), and list mutation mid-iteration is not.
            Entry e = _entries[i];
            e.Alive = false;
            e.Target = null;
            _entries[i] = e;
            _index.Remove(tickable);
            _compactPending = true;
        }

        public void SetBudget(ITickable tickable, TickBudget budget)
        {
            if (tickable == null || !_index.TryGetValue(tickable, out int i)) return;
            Entry e = _entries[i];
            e.Budget = budget;
            _entries[i] = e;
        }

        /// <summary>Dispatches one simulation tick across the registered set.</summary>
        public void Tick(in SimTime time)
        {
            int count = _entries.Count;
            if (count == 0) return;

            bool budgeted = BudgetMilliseconds > 0.0;
            if (budgeted)
            {
                _watch.Restart();
            }

            LastDeferredCount = 0;

            int start = _resumeCursor >= count ? 0 : _resumeCursor;
            int processed = 0;

            for (int n = 0; n < count; n++)
            {
                int i = start + n;
                if (i >= count) i -= count;

                Entry e = _entries[i];
                if (!e.Alive || e.Target == null) continue;

                // Adaptive tickables re-declare their bucket each visit.
                if (e.Target is IAdaptiveTickable adaptive)
                {
                    TickBudget desired = adaptive.DesiredBudget;
                    if (desired != e.Budget)
                    {
                        e.Budget = desired;
                        _entries[i] = e;
                    }
                }

                int divisor = Divisors[(int)e.Budget];
                if (divisor == 0) continue;                                   // dormant
                if (divisor > 1 && ((time.Tick + e.PhaseOffset) % divisor) != 0) continue;

                try
                {
                    e.Target.OnSimTick(in time);
                }
                catch (System.Exception ex)
                {
                    // One misbehaving agent must never take down the simulation loop.
                    VLog.Exception(LogCat.Core, ex);
                    e.Alive = false;
                    e.Target = null;
                    _entries[i] = e;
                    _compactPending = true;
                }

                processed++;

                // Check the clock every 8 agents — Stopwatch reads are not free.
                if (budgeted && (processed & 7) == 0 && _watch.Elapsed.TotalMilliseconds >= BudgetMilliseconds)
                {
                    _resumeCursor = i + 1;
                    LastDeferredCount = count - n - 1;
                    if (VLog.Is(LogCat.Core) && LastDeferredCount > 0)
                    {
                        VLog.Info(LogCat.Core, $"TickScheduler deferred {LastDeferredCount} tickable(s) over budget.");
                    }
                    return;
                }
            }

            _resumeCursor = 0;
            if (_compactPending) Compact();
        }

        void Compact()
        {
            _compactPending = false;
            int write = 0;
            for (int read = 0; read < _entries.Count; read++)
            {
                if (!_entries[read].Alive) continue;
                if (write != read) _entries[write] = _entries[read];
                _index[_entries[write].Target] = write;
                write++;
            }
            _entries.RemoveRange(write, _entries.Count - write);
            if (_resumeCursor > _entries.Count) _resumeCursor = 0;
        }

        public void Clear()
        {
            _entries.Clear();
            _index.Clear();
            _resumeCursor = 0;
            _nextPhase = 0;
            _compactPending = false;
        }
    }
}
