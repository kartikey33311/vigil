using System.Collections.Generic;
using UnityEngine;
using Vigil.AI.Agents;
using Vigil.AI.Director;
using Vigil.Audio;
using Vigil.AI.Pathfinding;
using Vigil.AI.Perception;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Events;
using Vigil.Core.Services;
using Vigil.Core.Simulation;
using Vigil.Data;
using Vigil.Net.Interest;
using Vigil.Net.Session;

namespace Vigil.Bootstrap
{
    /// <summary>
    /// Drives the fixed simulation clock. Exactly one per session, owned by the
    /// bootstrap.
    ///
    /// <para>This is the only place in the project that reads
    /// <c>UnityEngine.Time</c> for simulation purposes â€” everything downstream
    /// receives <see cref="SimTime"/>. That single choke point is what makes the
    /// simulation reproducible.</para>
    /// </summary>
    [DefaultExecutionOrder(-9000)]
    public sealed class SimClockRunner : MonoBehaviour
    {
        SimClock _clock;
        TickScheduler _scheduler;

        /// <summary>Ticks executed on the most recent frame. Telemetry for the debug overlay.</summary>
        public int LastFrameTicks { get; private set; }

        public SimClock Clock => _clock;

        public void Initialise(SimClock clock, TickScheduler scheduler)
        {
            _clock = clock;
            _scheduler = scheduler;
        }

        void Update()
        {
            if (_clock == null || _scheduler == null) return;

            int steps = _clock.BeginFrame(Time.unscaledDeltaTime);
            LastFrameTicks = steps;

            for (int i = 0; i < steps; i++)
            {
                // Step() advances one tick and returns THAT tick's time. Dispatching
                // several ticks with the same SimTime would silently break every
                // time-based behaviour on exactly the frames where a hitch made
                // catch-up necessary.
                SimTime time = _clock.Step();
                _scheduler.Tick(in time);
            }
        }
    }
}
