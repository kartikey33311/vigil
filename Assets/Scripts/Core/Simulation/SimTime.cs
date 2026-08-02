// -----------------------------------------------------------------------------
// Vigil — fixed-step simulation time.
//
// Gameplay/AI simulation runs on its OWN fixed tick, deliberately decoupled from
// both render framerate and Unity's physics FixedUpdate:
//
//   * Decoupled from render  -> AI behaviour is identical at 30 fps and 240 fps,
//                               which is a hard requirement for a server-
//                               authoritative game (the server is headless and
//                               runs at a different rate than any client).
//   * Decoupled from physics -> we can raise physics to 60 Hz for character
//                               controller fidelity while keeping AI cognition
//                               at 20-30 Hz, where it is imperceptible but ~2-3x
//                               cheaper.
//
// Tick is an int and wraps after ~414 days at 60 Hz; all comparisons must use
// SimTime.TicksSince rather than raw subtraction.
// -----------------------------------------------------------------------------

using System.Runtime.CompilerServices;

namespace Vigil.Core.Simulation
{
    /// <summary>Immutable snapshot of the simulation clock for one tick.</summary>
    public readonly struct SimTime
    {
        /// <summary>Monotonic simulation tick index since the session started.</summary>
        public readonly int Tick;

        /// <summary>Seconds of simulated time elapsed since session start.</summary>
        public readonly double Elapsed;

        /// <summary>Fixed seconds represented by this tick (already time-scaled).</summary>
        public readonly float DeltaTime;

        /// <summary>Unscaled fixed seconds represented by this tick.</summary>
        public readonly float UnscaledDeltaTime;

        /// <summary>Current global time scale (1 = normal, 0 = paused).</summary>
        public readonly float TimeScale;

        public SimTime(int tick, double elapsed, float deltaTime, float unscaledDeltaTime, float timeScale)
        {
            Tick = tick;
            Elapsed = elapsed;
            DeltaTime = deltaTime;
            UnscaledDeltaTime = unscaledDeltaTime;
            TimeScale = timeScale;
        }

        /// <summary>Wrap-safe tick difference (current - earlier).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int TicksSince(int earlierTick) => unchecked(Tick - earlierTick);

        /// <summary>Wrap-safe elapsed seconds since an earlier tick.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float SecondsSince(int earlierTick, float tickInterval) => TicksSince(earlierTick) * tickInterval;

        public override string ToString() => $"t{Tick} ({Elapsed:F2}s dt={DeltaTime:F4})";
    }

    /// <summary>
    /// Anything driven by the fixed simulation clock. Implementations must be
    /// deterministic given (state, SimTime) — no UnityEngine.Time reads, no
    /// UnityEngine.Random. Use <see cref="Vigil.Core.Mathx.DeterministicRandom"/>.
    /// </summary>
    public interface ITickable
    {
        void OnSimTick(in SimTime time);
    }

    /// <summary>
    /// Scheduling priority. The scheduler divides tick rate per bucket so distant
    /// or dormant agents cost a fraction of a fully-simulated one. This is the
    /// single biggest lever on AI cost at scale.
    /// </summary>
    public enum TickBudget
    {
        /// <summary>Every tick. Reserve for the agent actively hunting a player.</summary>
        Critical = 0,

        /// <summary>Every 2nd tick. Agents in play but not the active threat.</summary>
        High = 1,

        /// <summary>Every 4th tick. Off-screen / distant agents.</summary>
        Normal = 2,

        /// <summary>Every 8th tick. Far-field ambient NPCs.</summary>
        Low = 3,

        /// <summary>Never ticked until promoted. Fully dormant.</summary>
        Dormant = 4
    }

    /// <summary>
    /// Optional interface for tickables whose cost bucket changes at runtime
    /// (e.g. an agent that wakes when a player enters its region).
    /// </summary>
    public interface IAdaptiveTickable : ITickable
    {
        TickBudget DesiredBudget { get; }
    }
}
