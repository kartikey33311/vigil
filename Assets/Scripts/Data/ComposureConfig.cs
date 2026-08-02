using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;

namespace Vigil.Data
{
    [CreateAssetMenu(menuName = "Vigil/Gameplay/Composure Config", fileName = "ComposureConfig")]
    public sealed class ComposureConfig : ScriptableObject, IValidatableConfig
    {
        [Header("Drain (composure/second)")]
        [SerializeField, Tooltip("Drain vs normalised darkness (1 = pitch black).")]
        AnimationCurve _drainByDarkness = null;

        [SerializeField, Tooltip("Drain vs normalised proximity to the entity (1 = touching).")]
        AnimationCurve _drainByProximity = null;

        [SerializeField, Min(0f)] float _isolationDrain = 0.9f;
        [SerializeField, Min(1f)] float _isolationDistance = 25f;
        [SerializeField, Min(0f), Tooltip("One-shot composure cost of seeing a teammate go down.")]
        float _witnessDownedCost = 18f;

        [SerializeField, Min(0f), Tooltip("One-shot cost of direct line of sight to the entity.")]
        float _sightingCost = 12f;

        [Header("Recovery (composure/second)")]
        [SerializeField, Min(0f)] float _recoverInLight = 2.6f;
        [SerializeField, Min(0f)] float _recoverNearTeammate = 1.8f;
        [SerializeField, Min(0.5f)] float _teammateRadius = 7f;

        [Header("Tiers â€” thresholds descending, indexed by ComposureTier")]
        [SerializeField] float[] _tierThreshold = new float[ConfigCounts.ComposureTiers] { 100f, 72f, 48f, 24f, 0f };

        [Header("Feedback into perception")]
        [SerializeField, Tooltip("Noise multiplier vs composure (x=0 broken, x=1 calm). A panicking player breathes hard and is literally easier to hear â€” this is what closes the loop between the player's emotional state and the AI.")]
        AnimationCurve _noiseByComposure = null;

        [SerializeField, Min(1f)] float _maxComposure = 100f;

        public float MaxComposure => _maxComposure;
        public float IsolationDrain => _isolationDrain;
        public float IsolationDistance => _isolationDistance;
        public float WitnessDownedCost => _witnessDownedCost;
        public float SightingCost => _sightingCost;
        public float RecoverInLight => _recoverInLight;
        public float RecoverNearTeammate => _recoverNearTeammate;
        public float TeammateRadius => _teammateRadius;

        public float DrainByDarkness(float n) => ConfigCurve.Evaluate01(_drainByDarkness, n, n * 1.6f);
        public float DrainByProximity(float n) => ConfigCurve.Evaluate01(_drainByProximity, n, n * n * 5f);

        /// <summary>Noise multiplier for a normalised composure value (0 broken .. 1 calm).</summary>
        public float NoiseMultiplier(float normalisedComposure)
            => ConfigCurve.Evaluate01(_noiseByComposure, normalisedComposure, math.lerp(1.9f, 1f, normalisedComposure));

        public ComposureTier TierFor(float composure)
        {
            if (_tierThreshold == null) return ComposureTier.Calm;
            for (int i = ConfigCounts.ComposureTiers - 1; i >= 0; i--)
            {
                if (composure <= _tierThreshold[i]) return (ComposureTier)i;
            }
            return ComposureTier.Calm;
        }

        public void Validate(IList<string> problems)
        {
            if (_tierThreshold == null || _tierThreshold.Length != ConfigCounts.ComposureTiers)
            {
                problems.Add($"{name}: tierThreshold must have exactly {ConfigCounts.ComposureTiers} entries.");
                return;
            }
            for (int i = 1; i < _tierThreshold.Length; i++)
            {
                if (_tierThreshold[i] > _tierThreshold[i - 1])
                {
                    problems.Add($"{name}: tier thresholds must descend ({(ComposureTier)i} > {(ComposureTier)(i - 1)}).");
                }
            }
        }
    }
}
