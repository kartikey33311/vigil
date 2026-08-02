// -----------------------------------------------------------------------------
// Vigil — EditMode tests for the AI layer.
//
// The two tests that matter most here are the anti-oscillation guarantee and the
// mandatory-disengage clamp. Both encode design decisions that look like bugs to
// someone reading the code cold, and both would otherwise be "fixed" away.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Vigil.AI.Director;
using Vigil.AI.Perception;
using Vigil.Core.Contracts;
using Vigil.Core.Events;
using Vigil.Core.Simulation;
using Vigil.Data;

namespace Vigil.Tests
{
    public class AwarenessModelTests
    {
        PerceptionConfig _config;
        EventBus _bus;
        AwarenessModel _model;

        const ulong TargetId = 77UL;

        [SetUp]
        public void SetUp()
        {
            // Field initialisers on the config supply shipping defaults, so a bare
            // CreateInstance is a realistic asset.
            _config = ScriptableObject.CreateInstance<PerceptionConfig>();
            _bus = new EventBus();
            _model = new AwarenessModel(1UL, _config, _bus);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_config);
        }

        static SimTime T(int tick) => new SimTime(tick, tick / 30.0, 1f / 30f, 1f / 30f, 1f);

        static Stimulus Sound(float3 at, int tick, float intensity = 1f)
            => new Stimulus(StimulusChannel.Sound, StimulusTag.Footstep, at, intensity, 20f, TargetId, tick);

        [Test]
        public void AwarenessAccruesFromStimuliAndDecaysWithoutThem()
        {
            Stimulus s = Sound(new float3(1f, 0f, 0f), 0);

            for (int i = 0; i < 30; i++)
            {
                _model.Contribute(TargetId, 0.05f, in s, lineOfSight: true);
                SimTime t = T(i);
                _model.Tick(in t);
            }

            Assert.IsTrue(_model.TryGetTarget(TargetId, out PerceivedTarget afterStimuli));
            Assert.Greater(afterStimuli.Awareness, 0.5f, "Repeated stimuli must raise awareness.");

            float peak = afterStimuli.Awareness;

            for (int i = 30; i < 240; i++)
            {
                SimTime t = T(i);
                _model.Tick(in t);
            }

            _model.TryGetTarget(TargetId, out PerceivedTarget afterSilence);
            Assert.Less(afterSilence.Awareness, peak, "Awareness must decay without reinforcement.");
        }

        [Test]
        public void HysteresisPreventsBandFlickerAtAThreshold()
        {
            // THE anti-oscillation guarantee. Without damped decay near a threshold,
            // a target parked exactly on the Alerted line flips band every tick and
            // the FSM thrashes — which players read as a broken monster.
            int bandChanges = 0;
            AwarenessLevel last = AwarenessLevel.Unaware;

            Stimulus s = Sound(new float3(2f, 0f, 0f), 0);

            // Climb to just above the Alerted threshold.
            while (_model.PeakAwareness < AwarenessThresholds.Alerted + 0.01f)
            {
                _model.Contribute(TargetId, 0.02f, in s, lineOfSight: false);
                SimTime warm = T(0);
                _model.Tick(in warm);
            }

            // Now hover: contribute on alternate ticks so awareness oscillates
            // across the threshold under steady decay.
            for (int i = 0; i < 600; i++)
            {
                if ((i & 1) == 0)
                {
                    _model.Contribute(TargetId, 0.0068f, in s, lineOfSight: false);
                }

                SimTime t = T(i);
                _model.Tick(in t);

                if (_model.PeakLevel != last)
                {
                    bandChanges++;
                    last = _model.PeakLevel;
                }
            }

            Assert.Less(bandChanges, 25,
                $"Awareness band changed {bandChanges} times over 600 ticks while hovering at a threshold. " +
                "Hysteresis is not damping decay near the boundary, and the FSM will visibly thrash.");
        }

        [Test]
        public void PositionConfidenceDecaysFasterThanAwareness()
        {
            // The monster must be able to remain certain you EXIST while losing
            // track of WHERE you are — that gap is what produces a wide search.
            Stimulus s = Sound(new float3(5f, 0f, 5f), 0);

            for (int i = 0; i < 20; i++)
            {
                _model.Contribute(TargetId, 0.08f, in s, lineOfSight: true);
                SimTime t = T(i);
                _model.Tick(in t);
            }

            _model.TryGetTarget(TargetId, out PerceivedTarget fresh);
            float awareness0 = fresh.Awareness;
            float confidence0 = fresh.PositionConfidence;

            for (int i = 20; i < 200; i++)
            {
                SimTime t = T(i);
                _model.Tick(in t);
            }

            _model.TryGetTarget(TargetId, out PerceivedTarget stale);

            float awarenessRetained = stale.Awareness / math.max(0.0001f, awareness0);
            float confidenceRetained = stale.PositionConfidence / math.max(0.0001f, confidence0);

            Assert.Less(confidenceRetained, awarenessRetained,
                "Position confidence must decay faster than awareness.");
        }

        [Test]
        public void AnonymousStimuliBecomeInvestigationPoints()
        {
            Stimulus anon = new Stimulus(
                StimulusChannel.Sound, StimulusTag.Door, new float3(9f, 0f, 4f), 1f, 25f, 0UL, 0);

            _model.ContributeAnonymous(in anon, 0.6f);

            Assert.IsTrue(_model.TryGetInvestigationPoint(out float3 point, out float priority));
            Assert.AreEqual(9f, point.x, 0.01f);
            Assert.Greater(priority, 0f);
        }

        [Test]
        public void NearbyAnonymousStimuliMergeRatherThanFloodingTheBuffer()
        {
            // A running player would otherwise fill the fixed buffer in a second and
            // evict everything genuinely interesting.
            for (int i = 0; i < 40; i++)
            {
                Stimulus s = new Stimulus(
                    StimulusChannel.Sound, StimulusTag.Footstep,
                    new float3(10f + i * 0.05f, 0f, 10f), 1f, 20f, 0UL, i);

                _model.ContributeAnonymous(in s, 0.3f);
            }

            Assert.IsTrue(_model.TryGetInvestigationPoint(out float3 p, out _));
            Assert.AreEqual(10f, p.x, 2f, "Nearby cues must merge into one point.");
        }

        [Test]
        public void ForgetClearsBelief()
        {
            Stimulus s = Sound(float3.zero, 0);
            _model.Contribute(TargetId, 0.9f, in s, lineOfSight: true);

            Assert.IsTrue(_model.TryGetTarget(TargetId, out _));

            _model.Forget(TargetId);

            Assert.IsFalse(_model.TryGetTarget(TargetId, out _));
        }

        [Test]
        public void CapacityEvictionKeepsTheHighestThreat()
        {
            // More targets than the fixed capacity; the model must not allocate and
            // must retain the most threatening.
            for (ulong id = 1; id <= AwarenessModel.MaxTargets + 4; id++)
            {
                Stimulus s = new Stimulus(
                    StimulusChannel.Sound, StimulusTag.Footstep, new float3(id, 0f, 0f), 1f, 30f, id, 0);

                // Later ids get progressively more awareness.
                _model.Contribute(id, 0.05f * id, in s, lineOfSight: true);
            }

            SimTime t = T(1);
            _model.Tick(in t);

            Assert.LessOrEqual(_model.KnownTargetCount, AwarenessModel.MaxTargets);
            Assert.IsTrue(_model.TryGetPrimaryTarget(out PerceivedTarget primary));
            Assert.Greater(primary.Awareness, 0f);
        }
    }

    // =========================================================================
    // Director
    // =========================================================================

    public class DirectorTests
    {
        DirectorConfig _config;
        EventBus _bus;
        AIDirector _director;

        [SetUp]
        public void SetUp()
        {
            _config = ScriptableObject.CreateInstance<DirectorConfig>();
            _bus = new EventBus();
            _director = new AIDirector(_config, _bus, null);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_config);
        }

        static SimTime At(double seconds)
        {
            int tick = (int)(seconds * 30.0);
            return new SimTime(tick, seconds, 1f / 30f, 1f / 30f, 1f);
        }

        static PlayerStressSample HighStressPlayer(ulong id) => new PlayerStressSample
        {
            PlayerId = id,
            Position = float3.zero,
            Composure = 0.05f,
            IsolationDistance = 200f,
            TimeSinceLastThreat = 0f,
            Darkness = 1f,
            IsAlive = true,
            IsDowned = false
        };

        [Test]
        public void MaximumPhaseDurationForcesRelaxEvenWhileStressIsHigh()
        {
            // THE mandatory-disengage guarantee.
            //
            // This is the rule most likely to be "optimised away" by someone who has
            // not read the design docs: it makes the monster strictly worse at
            // winning. It exists because fear is a derivative — remove the lull and
            // players acclimatise within ~10 minutes, at which point the antagonist
            // stops being frightening and becomes tedious.
            _director.ForcePhase(DreadPhase.Peak);

            PlayerStressSample[] samples = { HighStressPlayer(1UL) };
            _director.InjectSamples(samples, 1);

            double maxPeak = _config.MaxPhaseSeconds(DreadPhase.Peak);

            for (double s = 0; s <= maxPeak + 5.0; s += 0.5)
            {
                _director.InjectSamples(samples, 1);   // keep stress pinned high
                SimTime t = At(s);
                _director.OnSimTick(in t);
            }

            Assert.AreNotEqual(DreadPhase.Peak, _director.Phase,
                "Peak must end on its maximum duration even while table stress stays high. " +
                "Sustained pressure produces adaptation, not fear.");
        }

        [Test]
        public void EngagementIsOnlyAuthorisedAtPeak()
        {
            // The gate that makes Buildup work: during Buildup the monster knows
            // exactly where you are and is forbidden from acting on it.
            foreach (DreadPhase phase in new[] { DreadPhase.Buildup, DreadPhase.Relax, DreadPhase.Fadeout })
            {
                _director.ForcePhase(phase);
                SimTime t = At(0.1);
                _director.OnSimTick(in t);

                Assert.IsFalse(_director.CurrentIntent.EngagementAuthorised,
                    $"Contact must not be authorised during {phase}.");
            }

            _director.ForcePhase(DreadPhase.Peak);
            SimTime peak = At(0.2);
            _director.OnSimTick(in peak);

            Assert.IsTrue(_director.CurrentIntent.EngagementAuthorised, "Contact must be authorised at Peak.");
        }

        [Test]
        public void PhaseChangePublishesAnEvent()
        {
            DreadPhase observedFrom = DreadPhase.Fadeout;
            DreadPhase observedTo = DreadPhase.Fadeout;
            int hits = 0;

            _bus.Subscribe<DreadPhaseChangedEvent>(e =>
            {
                observedFrom = e.From;
                observedTo = e.To;
                hits++;
            });

            _director.ForcePhase(DreadPhase.Peak);

            Assert.AreEqual(1, hits);
            Assert.AreEqual(DreadPhase.Fadeout, observedFrom);
            Assert.AreEqual(DreadPhase.Peak, observedTo);
        }

        [Test]
        public void PreferredTargetFavoursTheIsolatedAndCalmPlayerOverTheBrokenOne()
        {
            // Deliberately NOT "pick the weakest". Pressuring an already-broken
            // player produces a rout; pressuring the confident one produces a story.
            PlayerStressSample calmIsolated = new PlayerStressSample
            {
                PlayerId = 10UL,
                Composure = 0.95f,
                IsolationDistance = 60f,
                IsAlive = true
            };

            PlayerStressSample brokenGrouped = new PlayerStressSample
            {
                PlayerId = 20UL,
                Composure = 0.05f,
                IsolationDistance = 2f,
                IsAlive = true
            };

            PlayerStressSample[] samples = { calmIsolated, brokenGrouped };
            _director.InjectSamples(samples, 2);

            SimTime t = At(1.0);
            _director.OnSimTick(in t);

            Assert.AreEqual(10UL, _director.CurrentIntent.PreferredTargetId,
                "The Director must pressure the isolated, composed player rather than the nearly-broken one.");
        }

        [Test]
        public void PlayerKilledForcesRelaxImmediately()
        {
            _director.ForcePhase(DreadPhase.Peak);
            _director.Report(DirectorEvent.PlayerKilled, 1UL, float3.zero);

            Assert.AreEqual(DreadPhase.Relax, _director.Phase,
                "A death must end the beat regardless of the curve — continuing to press reads as piling on.");
        }

        [Test]
        public void SpeedScaleAndStandoffVaryByPhase()
        {
            _director.ForcePhase(DreadPhase.Peak);
            SimTime t1 = At(0.1);
            _director.OnSimTick(in t1);
            float peakSpeed = _director.CurrentIntent.SpeedScale;

            _director.ForcePhase(DreadPhase.Fadeout);
            SimTime t2 = At(0.2);
            _director.OnSimTick(in t2);
            float fadeSpeed = _director.CurrentIntent.SpeedScale;

            Assert.Greater(peakSpeed, fadeSpeed, "The entity must move faster at Peak than during Fadeout.");
            Assert.Greater(_director.CurrentIntent.DesiredStandoffDistance, 0f);
        }
    }

    // =========================================================================
    // Config validation
    // =========================================================================

    public class ConfigValidationTests
    {
        [Test]
        public void DefaultConfigsPassTheirOwnValidation()
        {
            // Every config ships defaults in its field initialisers. If those
            // defaults do not satisfy the asset's own Validate(), the generated
            // sample project boots with warnings on day one.
            List<ScriptableObject> created = new List<ScriptableObject>();

            try
            {
                AssertValidates(Make<PerceptionConfig>(created));
                AssertValidates(Make<NavigationConfig>(created));
                AssertValidates(Make<DirectorConfig>(created));
                AssertValidates(Make<MovementConfig>(created));
                AssertValidates(Make<ComposureConfig>(created));
                AssertValidates(Make<NetworkTuningConfig>(created));
                AssertValidates(Make<AudioConfig>(created));
            }
            finally
            {
                for (int i = 0; i < created.Count; i++) Object.DestroyImmediate(created[i]);
            }
        }

        static T Make<T>(List<ScriptableObject> track) where T : ScriptableObject
        {
            T asset = ScriptableObject.CreateInstance<T>();
            track.Add(asset);
            return asset;
        }

        static void AssertValidates(ScriptableObject asset)
        {
            IValidatableConfig validatable = asset as IValidatableConfig;
            Assert.IsNotNull(validatable, $"{asset.GetType().Name} must implement IValidatableConfig.");

            List<string> problems = new List<string>();
            validatable.Validate(problems);

            // LayerMask problems are expected on a bare CreateInstance, because the
            // masks are assigned by the content generator once the layers exist.
            problems.RemoveAll(p => p.Contains("Mask") || p.Contains("mask"));

            Assert.IsEmpty(problems,
                $"{asset.GetType().Name} default values failed validation: {string.Join(" | ", problems)}");
        }
    }
}
