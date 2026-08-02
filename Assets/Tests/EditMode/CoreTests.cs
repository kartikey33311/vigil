// -----------------------------------------------------------------------------
// Vigil — EditMode tests for the Core simulation layer.
//
// These deliberately target the properties that DEGRADE SILENTLY. None of the
// behaviours below throw when they break; they just make the game feel subtly
// wrong, which is the hardest class of bug to catch by playing.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Vigil.Core.Contracts;
using Vigil.Core.Events;
using Vigil.Core.Mathx;
using Vigil.Core.Simulation;
using Vigil.Core.StateMachine;

namespace Vigil.Tests
{
    // =========================================================================
    // State machine
    // =========================================================================

    public class StateMachineTests
    {
        class Ctx
        {
            public bool AllowA, AllowB, Interrupt;
            public int TickCount;
        }

        class Probe : StateBase<Ctx>
        {
            readonly int _id;
            public int Entered, Exited, Ticked;
            public StateStatus Result = StateStatus.Running;

            public Probe(int id) { _id = id; }

            public override int Id => _id;
            public override string Name => "S" + _id;

            public override void OnEnter(Ctx c, in SimTime t) { Entered++; }
            public override void OnExit(Ctx c, in SimTime t) { Exited++; }
            public override StateStatus OnTick(Ctx c, in SimTime t) { Ticked++; c.TickCount++; return Result; }
        }

        static SimTime T(int tick, double elapsed) => new SimTime(tick, elapsed, 1f / 30f, 1f / 30f, 1f);

        [Test]
        public void HigherPriorityTransitionWins()
        {
            Ctx ctx = new Ctx { AllowA = true, AllowB = true };
            Probe s0 = new Probe(0), s1 = new Probe(1), s2 = new Probe(2);

            StateMachine<Ctx> fsm = new StateMachine<Ctx>(99, "test");
            fsm.AddState(s0).AddState(s1).AddState(s2);
            fsm.AddTransition(0, 1, c => c.AllowA, priority: 5, label: "low");
            fsm.AddTransition(0, 2, c => c.AllowB, priority: 50, label: "high");

            SimTime t = T(0, 0);
            fsm.OnEnter(ctx, in t);
            fsm.OnTick(ctx, in t);

            Assert.AreEqual(2, fsm.CurrentStateId, "The higher-priority edge must win when both guards pass.");
        }

        [Test]
        public void MinDwellSuppressesAnOtherwiseValidTransition()
        {
            Ctx ctx = new Ctx { AllowA = true };
            Probe s0 = new Probe(0), s1 = new Probe(1);

            StateMachine<Ctx> fsm = new StateMachine<Ctx>(99, "test");
            fsm.AddState(s0).AddState(s1);
            fsm.AddTransition(0, 1, c => c.AllowA, priority: 10, minDwellSeconds: 1.0f);

            SimTime t0 = T(0, 0.0);
            fsm.OnEnter(ctx, in t0);
            fsm.OnTick(ctx, in t0);

            Assert.AreEqual(0, fsm.CurrentStateId, "Transition must be suppressed before the dwell time elapses.");

            SimTime t1 = T(60, 2.0);
            fsm.OnTick(ctx, in t1);

            Assert.AreEqual(1, fsm.CurrentStateId, "Transition must fire once the dwell time has elapsed.");
        }

        [Test]
        public void CooldownPreventsImmediateReentryOfTheSameEdge()
        {
            // AllowB gates the return edge so each tick performs exactly one
            // transition. Leaving both edges permanently open would let the machine
            // legitimately bounce 0->1->0 within a single tick (it resolves
            // transitions until none apply), which tests a different property.
            Ctx ctx = new Ctx { AllowA = true, AllowB = false };
            Probe s0 = new Probe(0), s1 = new Probe(1);

            StateMachine<Ctx> fsm = new StateMachine<Ctx>(99, "test");
            fsm.AddState(s0).AddState(s1);
            fsm.AddTransition(0, 1, c => c.AllowA, priority: 10, cooldownSeconds: 5f, label: "there");
            fsm.AddTransition(1, 0, c => c.AllowB, priority: 10, label: "back");

            SimTime t0 = T(0, 0.0);
            fsm.OnEnter(ctx, in t0);
            fsm.OnTick(ctx, in t0);
            Assert.AreEqual(1, fsm.CurrentStateId, "The outbound edge should fire on the first tick.");

            // Return to 0.
            ctx.AllowB = true;
            SimTime t1 = T(10, 0.33);
            fsm.OnTick(ctx, in t1);
            Assert.AreEqual(0, fsm.CurrentStateId);

            // The outbound edge is still cooling down, so we must stay put.
            ctx.AllowB = false;
            SimTime t2 = T(20, 0.66);
            fsm.OnTick(ctx, in t2);
            Assert.AreEqual(0, fsm.CurrentStateId, "A cooling-down edge must not re-fire.");

            // Past the cooldown it becomes available again.
            SimTime t3 = T(300, 10.0);
            fsm.OnTick(ctx, in t3);
            Assert.AreEqual(1, fsm.CurrentStateId, "The edge must be available once the cooldown expires.");
        }

        [Test]
        public void AnyStateTransitionBeatsLocalAtHigherPriority()
        {
            Ctx ctx = new Ctx { AllowA = true, Interrupt = true };
            Probe s0 = new Probe(0), s1 = new Probe(1), s2 = new Probe(2);

            StateMachine<Ctx> fsm = new StateMachine<Ctx>(99, "test");
            fsm.AddState(s0).AddState(s1).AddState(s2);
            fsm.AddTransition(0, 1, c => c.AllowA, priority: 10);
            fsm.AddAnyTransition(2, c => c.Interrupt, priority: 1000, label: "interrupt");

            SimTime t = T(0, 0);
            fsm.OnEnter(ctx, in t);
            fsm.OnTick(ctx, in t);

            Assert.AreEqual(2, fsm.CurrentStateId, "An interrupt must outrank a local transition.");
        }

        [Test]
        public void NestedMachineEnterAndExitPropagateThroughTheSubtree()
        {
            Ctx ctx = new Ctx();
            Probe child = new Probe(10);

            StateMachine<Ctx> nested = new StateMachine<Ctx>(5, "nested");
            nested.AddState(child);
            nested.SetInitialState(10);

            Probe sibling = new Probe(1);

            StateMachine<Ctx> root = new StateMachine<Ctx>(0, "root");
            root.AddState(nested).AddState(sibling);
            root.SetInitialState(5);
            root.AddTransition(5, 1, c => c.AllowA, priority: 10);

            SimTime t = T(0, 0);
            root.OnEnter(ctx, in t);

            Assert.AreEqual(1, child.Entered, "Entering the parent must enter the nested initial state.");

            root.OnTick(ctx, in t);
            Assert.Greater(child.Ticked, 0, "Nested state must tick while its parent is active.");

            ctx.AllowA = true;
            SimTime t2 = T(1, 0.1);
            root.OnTick(ctx, in t2);

            Assert.AreEqual(1, child.Exited, "Exiting the parent must exit the nested state.");
            Assert.AreEqual(1, root.CurrentStateId);
        }

        [Test]
        public void GetActivePathReportsTheNestedPath()
        {
            Ctx ctx = new Ctx();
            StateMachine<Ctx> nested = new StateMachine<Ctx>(5, "Hunt");
            nested.AddState(new Probe(10));
            nested.SetInitialState(10);

            StateMachine<Ctx> root = new StateMachine<Ctx>(0, "Root");
            root.AddState(nested);
            root.SetInitialState(5);

            SimTime t = T(0, 0);
            root.OnEnter(ctx, in t);

            StringAssert.Contains("Root", root.GetActivePath());
            StringAssert.Contains("Hunt", root.GetActivePath());
        }

        [Test]
        public void TransitionCycleIsBoundedByMaxTransitionsPerTick()
        {
            Ctx ctx = new Ctx { AllowA = true, AllowB = true };
            Probe s0 = new Probe(0), s1 = new Probe(1);

            StateMachine<Ctx> fsm = new StateMachine<Ctx>(99, "test");
            fsm.AddState(s0).AddState(s1);
            fsm.MaxTransitionsPerTick = 4;

            // A deliberately pathological pair of always-true edges.
            fsm.AddTransition(0, 1, c => true, priority: 10);
            fsm.AddTransition(1, 0, c => true, priority: 10);

            SimTime t = T(0, 0);
            fsm.OnEnter(ctx, in t);

            // Must terminate rather than spin forever.
            Assert.DoesNotThrow(() => fsm.OnTick(ctx, in t));
            Assert.LessOrEqual(fsm.TransitionCount, 4, "Transition storm must be clamped.");
        }
    }

    // =========================================================================
    // Clock
    // =========================================================================

    public class SimClockTests
    {
        [Test]
        public void BeginFrameYieldsTheExpectedTickCount()
        {
            SimClock clock = new SimClock(30);

            Assert.AreEqual(0, clock.BeginFrame(0.01f), "Less than one tick of time yields no ticks.");
            Assert.AreEqual(1, clock.BeginFrame(0.03f), "Accumulated time past one interval yields one tick.");
        }

        [Test]
        public void StepAdvancesExactlyOneTickAndReportsDistinctTimes()
        {
            SimClock clock = new SimClock(30);

            // 0.11s rather than 0.10s on purpose: 3 * (1/30f) is 0.100000005 in
            // float, so exactly 0.1 is ambiguous at the boundary. Asserting on a
            // value that sits on a floating-point knife edge tests the FPU, not the
            // clock.
            int due = clock.BeginFrame(0.11f);

            Assert.AreEqual(3, due);

            List<int> ticks = new List<int>();
            for (int i = 0; i < due; i++) ticks.Add(clock.Step().Tick);

            // The bug this guards against: dispatching N ticks that all report the
            // same tick index, which silently breaks every time-based behaviour on
            // exactly the frames where catch-up was needed.
            CollectionAssert.AllItemsAreUnique(ticks);
            Assert.AreEqual(new[] { 1, 2, 3 }, ticks.ToArray());
        }

        [Test]
        public void CatchUpIsClampedAndBacklogIsDropped()
        {
            SimClock clock = new SimClock(30) { MaxCatchUpTicks = 5 };

            // A 2-second hitch is 60 ticks of backlog. We must run at most 5 and
            // discard the rest rather than compounding it into the next frame.
            int due = clock.BeginFrame(2.0f);

            Assert.LessOrEqual(due, 5, "Catch-up must be clamped.");

            for (int i = 0; i < due; i++) clock.Step();

            int next = clock.BeginFrame(0f);
            Assert.AreEqual(0, next, "Backlog must be discarded, not carried into the next frame.");
        }

        [Test]
        public void AlphaStaysNormalised()
        {
            SimClock clock = new SimClock(30);

            for (int i = 0; i < 50; i++)
            {
                clock.BeginFrame(0.017f);
                Assert.GreaterOrEqual(clock.Alpha, 0f);
                Assert.Less(clock.Alpha, 1f, "Alpha must remain a fraction of one tick.");
            }
        }

        [Test]
        public void TickArithmeticIsWrapSafe()
        {
            SimTime t = new SimTime(int.MinValue + 2, 0, 0.033f, 0.033f, 1f);

            // Wrapping past int.MaxValue must still produce a small positive delta.
            Assert.AreEqual(5, t.TicksSince(int.MaxValue - 2));
        }
    }

    // =========================================================================
    // Tick scheduler
    // =========================================================================

    public class TickSchedulerTests
    {
        class Counter : ITickable
        {
            public int Ticks;
            public void OnSimTick(in SimTime time) { Ticks++; }
        }

        class Thrower : ITickable
        {
            public int Ticks;
            public void OnSimTick(in SimTime time) { Ticks++; throw new InvalidOperationException("boom"); }
        }

        class SelfUnregistering : ITickable
        {
            public TickScheduler Scheduler;
            public int Ticks;
            public void OnSimTick(in SimTime time) { Ticks++; Scheduler.Unregister(this); }
        }

        static SimTime T(int tick) => new SimTime(tick, tick / 30.0, 1f / 30f, 1f / 30f, 1f);

        [Test]
        public void BudgetBucketsTickAtTheirDivisor()
        {
            TickScheduler scheduler = new TickScheduler { BudgetMilliseconds = 0 };

            Counter critical = new Counter();
            Counter normal = new Counter();

            scheduler.Register(critical, TickBudget.Critical);
            scheduler.Register(normal, TickBudget.Normal);

            for (int i = 0; i < 64; i++)
            {
                SimTime t = T(i);
                scheduler.Tick(in t);
            }

            Assert.AreEqual(64, critical.Ticks, "Critical must tick every tick.");
            Assert.AreEqual(16, normal.Ticks, "Normal must tick every 4th tick.");
        }

        [Test]
        public void DormantTickablesNeverTick()
        {
            TickScheduler scheduler = new TickScheduler { BudgetMilliseconds = 0 };
            Counter dormant = new Counter();
            scheduler.Register(dormant, TickBudget.Dormant);

            for (int i = 0; i < 32; i++) { SimTime t = T(i); scheduler.Tick(in t); }

            Assert.AreEqual(0, dormant.Ticks);
        }

        [Test]
        public void PhaseOffsetsDesynchroniseAgentsInTheSameBucket()
        {
            TickScheduler scheduler = new TickScheduler { BudgetMilliseconds = 0 };

            // Eight agents in the same bucket. If they all ticked on the same tick,
            // frame time would be a sawtooth.
            Counter[] agents = new Counter[8];
            for (int i = 0; i < agents.Length; i++)
            {
                agents[i] = new Counter();
                scheduler.Register(agents[i], TickBudget.Normal);
            }

            HashSet<int> tickCountsAfterOneTick = new HashSet<int>();

            SimTime t0 = T(0);
            scheduler.Tick(in t0);

            for (int i = 0; i < agents.Length; i++) tickCountsAfterOneTick.Add(agents[i].Ticks);

            Assert.Greater(tickCountsAfterOneTick.Count, 1,
                "Agents in one bucket must not all tick on the same tick.");
        }

        [Test]
        public void UnregisteringFromInsideATickIsSafe()
        {
            TickScheduler scheduler = new TickScheduler { BudgetMilliseconds = 0 };

            SelfUnregistering suicidal = new SelfUnregistering { Scheduler = scheduler };
            Counter survivor = new Counter();

            scheduler.Register(suicidal, TickBudget.Critical);
            scheduler.Register(survivor, TickBudget.Critical);

            SimTime t0 = T(0);
            Assert.DoesNotThrow(() => scheduler.Tick(in t0));

            SimTime t1 = T(1);
            scheduler.Tick(in t1);

            Assert.AreEqual(1, suicidal.Ticks, "An unregistered tickable must stop being ticked.");
            Assert.AreEqual(2, survivor.Ticks, "Other tickables must be unaffected.");
        }

        [Test]
        public void ThrowingTickableIsEvictedWithoutKillingTheLoop()
        {
            TickScheduler scheduler = new TickScheduler { BudgetMilliseconds = 0 };

            Thrower bad = new Thrower();
            Counter good = new Counter();

            scheduler.Register(bad, TickBudget.Critical);
            scheduler.Register(good, TickBudget.Critical);

            // The exception is logged by the scheduler; the loop must survive it.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;

            SimTime t0 = T(0);
            Assert.DoesNotThrow(() => scheduler.Tick(in t0));

            SimTime t1 = T(1);
            scheduler.Tick(in t1);

            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual(1, bad.Ticks, "A throwing tickable must be evicted after its first failure.");
            Assert.AreEqual(2, good.Ticks, "One broken agent must never take down the simulation loop.");
        }
    }

    // =========================================================================
    // Deterministic RNG
    // =========================================================================

    public class DeterministicRandomTests
    {
        [Test]
        public void SameSeedProducesTheSameSequence()
        {
            DeterministicRandom a = new DeterministicRandom(1234u);
            DeterministicRandom b = new DeterministicRandom(1234u);

            for (int i = 0; i < 256; i++) Assert.AreEqual(a.NextUInt(), b.NextUInt());
        }

        [Test]
        public void DifferentEntitiesGetIndependentStreams()
        {
            DeterministicRandom a = DeterministicRandom.ForEntity(99u, 1UL, 0xA17u);
            DeterministicRandom b = DeterministicRandom.ForEntity(99u, 2UL, 0xA17u);

            int identical = 0;
            for (int i = 0; i < 64; i++)
            {
                if (a.NextUInt() == b.NextUInt()) identical++;
            }

            Assert.Less(identical, 4, "Adjacent entity ids must produce well-separated streams.");
        }

        [Test]
        public void DifferentPurposesGetIndependentStreams()
        {
            // This matters because adding a random call to one subsystem must not
            // perturb another's sequence, or every tuning change invalidates every
            // existing repro case.
            DeterministicRandom a = DeterministicRandom.ForEntity(7u, 42UL, 1u);
            DeterministicRandom b = DeterministicRandom.ForEntity(7u, 42UL, 2u);

            Assert.AreNotEqual(a.NextUInt(), b.NextUInt());
        }

        [Test]
        public void StateRoundTrips()
        {
            DeterministicRandom rng = new DeterministicRandom(555u);
            for (int i = 0; i < 10; i++) rng.NextUInt();

            uint4 saved = rng.GetState();
            uint expected = rng.NextUInt();

            rng.SetState(saved);
            Assert.AreEqual(expected, rng.NextUInt());
        }

        [Test]
        public void FloatsStayInRange()
        {
            DeterministicRandom rng = new DeterministicRandom(3u);

            for (int i = 0; i < 1000; i++)
            {
                float f = rng.NextFloat();
                Assert.GreaterOrEqual(f, 0f);
                Assert.Less(f, 1f);
            }
        }

        [Test]
        public void DiscSamplingStaysInsideTheRadius()
        {
            DeterministicRandom rng = new DeterministicRandom(11u);

            for (int i = 0; i < 500; i++)
            {
                float3 p = rng.NextPointInDiscXZ(5f);
                Assert.LessOrEqual(math.length(p), 5.001f);
                Assert.AreEqual(0f, p.y);
            }
        }
    }

    // =========================================================================
    // Event bus
    // =========================================================================

    public class EventBusTests
    {
        struct Ping { public int Value; }

        [Test]
        public void PublishReachesSubscribers()
        {
            EventBus bus = new EventBus();
            int received = 0;

            bus.Subscribe<Ping>(p => received = p.Value);
            bus.Publish(new Ping { Value = 42 });

            Assert.AreEqual(42, received);
        }

        [Test]
        public void DisposingASubscriptionUnsubscribes()
        {
            EventBus bus = new EventBus();
            int count = 0;

            IDisposable token = bus.Subscribe<Ping>(p => count++);
            bus.Publish(new Ping());
            token.Dispose();
            bus.Publish(new Ping());

            Assert.AreEqual(1, count);
        }

        [Test]
        public void TwoBusInstancesAreIsolated()
        {
            // The host-and-client-in-one-process case. A static channel would
            // cross-wire them and the client would react to the server's simulation.
            EventBus serverBus = new EventBus();
            EventBus clientBus = new EventBus();

            int serverHits = 0, clientHits = 0;

            serverBus.Subscribe<Ping>(p => serverHits++);
            clientBus.Subscribe<Ping>(p => clientHits++);

            serverBus.Publish(new Ping());

            Assert.AreEqual(1, serverHits);
            Assert.AreEqual(0, clientHits, "Event buses must not share subscriber state.");
        }

        [Test]
        public void ThrowingSubscriberIsEvictedButOthersStillReceive()
        {
            EventBus bus = new EventBus();
            int goodHits = 0;

            bus.Subscribe<Ping>(p => throw new InvalidOperationException("boom"));
            bus.Subscribe<Ping>(p => goodHits++);

            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            bus.Publish(new Ping());
            bus.Publish(new Ping());
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual(2, goodHits, "A throwing subscriber must not break the publish for others.");
        }

        [Test]
        public void RecursivePublishIsDepthLimited()
        {
            EventBus bus = new EventBus();
            bus.MaxDispatchDepth = 4;

            int hits = 0;
            bus.Subscribe<Ping>(p =>
            {
                hits++;
                bus.Publish(new Ping());   // deliberately republishes itself
            });

            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            Assert.DoesNotThrow(() => bus.Publish(new Ping()));
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;

            Assert.LessOrEqual(hits, 5, "Recursive publish must be bounded.");
        }
    }

    // =========================================================================
    // Blackboard
    // =========================================================================

    public class BlackboardTests
    {
        [Test]
        public void TypedValuesRoundTrip()
        {
            Blackboard bb = new Blackboard();

            bb.Set(BBKeys.Awareness, 0.75f, 10);
            bb.Set(BBKeys.TargetId, 12345UL, 10);
            bb.Set(BBKeys.HasLineOfSight, true, 10);
            bb.Set(BBKeys.MoveGoal, new float3(1f, 2f, 3f), 10);

            Assert.AreEqual(0.75f, bb.Get(BBKeys.Awareness), 0.0001f);
            Assert.AreEqual(12345UL, bb.Get(BBKeys.TargetId));
            Assert.IsTrue(bb.Get(BBKeys.HasLineOfSight));
            Assert.AreEqual(new float3(1f, 2f, 3f), bb.Get(BBKeys.MoveGoal));
        }

        [Test]
        public void MissingKeysReturnTheFallback()
        {
            Blackboard bb = new Blackboard();
            Assert.AreEqual(-1f, bb.Get(BBKeys.Awareness, -1f));
            Assert.IsFalse(bb.Has(BBKeys.Awareness));
        }

        [Test]
        public void AgeOfReportsTicksSinceWrite()
        {
            Blackboard bb = new Blackboard();
            bb.Set(BBKeys.Awareness, 1f, 100);

            Assert.AreEqual(20, bb.AgeOf(BBKeys.Awareness, 120));
            Assert.AreEqual(int.MaxValue, bb.AgeOf(BBKeys.Aggression, 120));
        }

        [Test]
        public void RemoveClearsEveryStore()
        {
            Blackboard bb = new Blackboard();
            bb.Set(BBKeys.Awareness, 1f, 0);
            bb.Remove(BBKeys.Awareness);

            Assert.IsFalse(bb.Has(BBKeys.Awareness));
            Assert.AreEqual(0f, bb.Get(BBKeys.Awareness));
        }
    }

    // =========================================================================
    // Path handles
    // =========================================================================

    public class PathHandleTests
    {
        [Test]
        public void RecycledSlotBumpsVersionSoStaleHandlesFail()
        {
            PathHandle original = new PathHandle(3, 1);
            PathHandle recycled = new PathHandle(3, 2);

            Assert.AreNotEqual(original, recycled,
                "A recycled slot must not compare equal to the handle that previously owned it — " +
                "otherwise a despawned agent's poll silently reads another agent's path.");
        }

        [Test]
        public void InvalidHandleIsDetectable()
        {
            Assert.IsFalse(PathHandle.Invalid.IsValid);
            Assert.IsTrue(new PathHandle(0, 1).IsValid);
        }
    }
}
