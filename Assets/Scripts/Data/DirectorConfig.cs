using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Simulation;

namespace Vigil.Data
{
    [CreateAssetMenu(menuName = "Vigil/AI/Director Config", fileName = "DirectorConfig")]
    public sealed class DirectorConfig : ScriptableObject, IValidatableConfig
    {
        [Header("Phase durations (seconds) â€” indexed by DreadPhase")]
        [SerializeField, Tooltip("Minimum time in each phase. Prevents the curve from degenerating into rapid oscillation.")]
        float[] _minPhaseSeconds = new float[ConfigCounts.DreadPhases] { 10f, 18f, 10f, 6f };

        [SerializeField, Tooltip("MAXIMUM time in each phase. The Peak entry is the mandatory-disengage clamp â€” it forces Relax even when the entity is winning. Do not raise it to 'make the game harder'; removing the lull removes the fear.")]
        float[] _maxPhaseSeconds = new float[ConfigCounts.DreadPhases] { 30f, 45f, 25f, 14f };

        [Header("Stress thresholds (0..1)")]
        [SerializeField, Range(0f, 1f), Tooltip("Aggregate table stress above which Buildup may advance to Peak.")]
        float _peakEntryStress = 0.28f;

        [SerializeField, Range(0f, 1f), Tooltip("Table stress below which Relax may advance to Fadeout.")]
        float _relaxExitStress = 0.22f;

        [SerializeField, Min(0f), Tooltip("Seconds of enforced quiet after Relax before Buildup may start again.")]
        float _relaxCooldown = 8f;

        [Header("Stress model")]
        [SerializeField, Tooltip("Stress delta applied per DirectorEvent, indexed by DirectorEvent.")]
        float[] _eventStress = new float[ConfigCounts.DirectorEvents]
        { 0.30f, -0.15f, 0.45f, 0.25f, -0.20f, -0.10f, 0.12f, 0.05f, -0.25f, 0.08f };

        [SerializeField, Tooltip("Stress contribution vs normalised proximity to the entity (1 = touching).")]
        AnimationCurve _stressByProximity = null;

        [SerializeField, Tooltip("Stress contribution vs normalised isolation from teammates (1 = fully alone).")]
        AnimationCurve _stressByIsolation = null;

        [SerializeField, Min(1f), Tooltip("Metres at which a player counts as fully isolated.")]
        float _isolationDistance = 28f;

        [SerializeField, Range(0f, 1f), Tooltip("Weight of darkness in the per-player stress sample.")]
        float _darknessWeight = 0.25f;

        [SerializeField, Min(0.1f), Tooltip("Seconds over which stress decays back toward baseline.")]
        float _stressHalfLife = 20f;

        [Header("Intent by phase")]
        [SerializeField, Tooltip("Standoff distance in metres, indexed by DreadPhase. Buildup wants 'close enough to be heard, far enough not to be seen'.")]
        float[] _standoffDistance = new float[ConfigCounts.DreadPhases] { 22f, 0f, 60f, 90f };

        [SerializeField, Tooltip("Movement speed multiplier, indexed by DreadPhase.")]
        float[] _speedScale = new float[ConfigCounts.DreadPhases] { 0.85f, 1.15f, 0.9f, 0.7f };

        [SerializeField, Tooltip("Pressure 0..1, indexed by DreadPhase. Scales hearing acuity and repath cadence.")]
        float[] _pressure = new float[ConfigCounts.DreadPhases] { 0.55f, 1f, 0.25f, 0.05f };

        [Header("Target preference")]
        [SerializeField, Range(0f, 2f), Tooltip("Weight favouring ISOLATED players. Kept high on purpose.")]
        float _isolationPreference = 1.15f;

        [SerializeField, Range(0f, 2f), Tooltip("Weight favouring CALM (high-composure) players. Pressuring a broken player produces a rout; pressuring the confident one produces a story.")]
        float _composurePreference = 0.85f;

        [SerializeField, Range(-2f, 0f), Tooltip("Penalty for targeting a nearly-dead player. Negative by design.")]
        float _lowHealthPenalty = -0.6f;

        [SerializeField, Range(0f, 1f), Tooltip("Stickiness bonus for the current preferred target, so preference does not thrash.")]
        float _targetStickiness = 0.3f;

        public float MinPhaseSeconds(DreadPhase p) => Indexed(_minPhaseSeconds, (int)p, 15f);
        public float MaxPhaseSeconds(DreadPhase p) => Indexed(_maxPhaseSeconds, (int)p, 60f);
        public float StandoffDistance(DreadPhase p) => Indexed(_standoffDistance, (int)p, 25f);
        public float SpeedScale(DreadPhase p) => Indexed(_speedScale, (int)p, 1f);
        public float Pressure(DreadPhase p) => Indexed(_pressure, (int)p, 0.5f);
        public float EventStress(DirectorEvent e) => Indexed(_eventStress, (int)e, 0f);

        public float PeakEntryStress => _peakEntryStress;
        public float RelaxExitStress => _relaxExitStress;
        public float RelaxCooldown => _relaxCooldown;
        public float IsolationDistance => _isolationDistance;
        public float DarknessWeight => _darknessWeight;
        public float StressHalfLife => _stressHalfLife;
        public float IsolationPreference => _isolationPreference;
        public float ComposurePreference => _composurePreference;
        public float LowHealthPenalty => _lowHealthPenalty;
        public float TargetStickiness => _targetStickiness;

        public float StressByProximity(float n) => ConfigCurve.Evaluate01(_stressByProximity, n, n * n);
        public float StressByIsolation(float n) => ConfigCurve.Evaluate01(_stressByIsolation, n, n);

        static float Indexed(float[] a, int i, float fallback)
            => (a != null && i >= 0 && i < a.Length) ? a[i] : fallback;

        public void Validate(IList<string> problems)
        {
            if (_minPhaseSeconds == null || _minPhaseSeconds.Length != ConfigCounts.DreadPhases ||
                _maxPhaseSeconds == null || _maxPhaseSeconds.Length != ConfigCounts.DreadPhases)
            {
                problems.Add($"{name}: phase duration arrays must have exactly {ConfigCounts.DreadPhases} entries.");
                return;
            }

            for (int i = 0; i < ConfigCounts.DreadPhases; i++)
            {
                if (_maxPhaseSeconds[i] < _minPhaseSeconds[i])
                {
                    problems.Add($"{name}: max phase duration < min for {(DreadPhase)i}.");
                }
            }

            if (_maxPhaseSeconds[(int)DreadPhase.Peak] > 120f)
            {
                problems.Add($"{name}: Peak max duration is {_maxPhaseSeconds[(int)DreadPhase.Peak]:F0}s. Sustained pressure past ~2 minutes produces adaptation, not fear.");
            }
            if (_eventStress == null || _eventStress.Length != ConfigCounts.DirectorEvents)
            {
                problems.Add($"{name}: eventStress must have exactly {ConfigCounts.DirectorEvents} entries.");
            }
        }
    }
}
