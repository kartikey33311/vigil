using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Simulation;

namespace Vigil.Data
{
    [CreateAssetMenu(menuName = "Vigil/AI/Perception Config", fileName = "PerceptionConfig")]
    public sealed class PerceptionConfig : ScriptableObject, IValidatableConfig
    {
        [System.Serializable]
        public struct TagWeight
        {
            public StimulusTag Tag;

            [Range(0f, 4f)] public float Weight;
        }

        [Header("Vision â€” three bands")]
        [SerializeField, Range(1f, 60f), Tooltip("Half-angle of the narrow 'focus' cone. Reads to players as the monster looking directly at something.")]
        float _focusHalfAngle = 18f;

        [SerializeField, Min(1f), Tooltip("Range of the focus cone. The longest distance at which you can be seen at all.")]
        float _focusRange = 42f;

        [SerializeField, Range(10f, 110f), Tooltip("Half-angle of the wide mid cone â€” ordinary peripheral awareness.")]
        float _midHalfAngle = 62f;

        [SerializeField, Min(1f), Tooltip("Range of the mid cone.")]
        float _midRange = 22f;

        [SerializeField, Min(0.5f), Tooltip("Near-omnidirectional band. Stops players hugging the monster's blind spot.")]
        float _nearRange = 4.5f;

        [SerializeField, Range(1, 8), Tooltip("Body sample points tested for line of sight. More = fairer partial cover, but costs a raycast each.")]
        int _visibilitySamples = 3;

        [SerializeField, Tooltip("Layers that block line of sight. Must NOT include the player or NPC layers.")]
        LayerMask _occlusionMask = ~0;

        [Header("Vision â€” response")]
        [SerializeField, Tooltip("Awareness gained per second at full visibility, vs normalised distance (0 = touching, 1 = max range).")]
        AnimationCurve _sightGainByDistance = null;

        [SerializeField, Tooltip("Multiplier on sight gain vs normalised angle off-centre (0 = dead ahead, 1 = cone edge).")]
        AnimationCurve _sightGainByAngle = null;

        [SerializeField, Range(0f, 1f), Tooltip("Visibility multiplier in full darkness. 0 makes unlit rooms perfect cover, which trivialises the game.")]
        float _darknessVisibility = 0.36f;

        [SerializeField, Min(0.05f), Tooltip("Seconds to reach Confirmed from Unaware under ideal conditions (close, lit, dead ahead). Below ~0.4s reads as the AI cheating.")]
        float _minTimeToDetect = 0.40f;

        [Header("Non-visual channels")]
        [SerializeField, Tooltip("Awareness contributed per unit of perceived stimulus intensity, indexed by StimulusChannel.")]
        float[] _channelGain = new float[ConfigCounts.StimulusChannels] { 1.0f, 0.55f, 0.30f, 1.0f, 0.22f, 0.85f };

        [SerializeField, Tooltip("Per-tag multipliers. A gunshot must dwarf a footstep, and a decoy is deliberately over-weighted so players can exploit it.")]
        TagWeight[] _tagWeights = null;

        [SerializeField, Range(0f, 1f), Tooltip("Fraction of sound intensity that survives ONE blocking surface.")]
        float _occlusionTransmission = 0.45f;

        [SerializeField, Min(0f), Tooltip("Metres of positional error added per unit of sound attenuation. THIS IS THE TENSION KNOB â€” it is why the monster searches the wrong room.")]
        float _soundLocalisationError = 7.5f;

        [Header("Decay & hysteresis")]
        [SerializeField, Tooltip("Awareness lost per second, indexed by AwarenessLevel (Unaware..Lost).")]
        float[] _decayPerSecond = new float[ConfigCounts.AwarenessLevels] { 0.24f, 0.11f, 0.065f, 0.042f, 0.032f };

        [SerializeField, Range(0f, 0.9f), Tooltip("Decay is multiplied by this while just BELOW a band threshold. Values under 1 create hysteresis and are what stop the monster flip-flopping between states.")]
        float _hysteresisDamping = 0.35f;

        [SerializeField, Range(0f, 0.2f), Tooltip("Width of the hysteresis window either side of a threshold, in awareness units.")]
        float _hysteresisBand = 0.06f;

        [SerializeField, Min(0.5f), Tooltip("Half-life in seconds of positional confidence after contact is lost. MUST stay well below the awareness decay time â€” the monster needs to remain certain you exist while losing track of where you are, because that gap is what produces a wide search instead of a beeline.")]
        float _confidenceHalfLife = 6.0f;

        [Header("Scent")]
        [SerializeField, Min(0f), Tooltip("Seconds a scent marker remains followable. 0 disables scent tracking entirely.")]
        float _scentLifetime = 22f;

        [SerializeField, Min(0.1f), Tooltip("Metres between dropped scent markers.")]
        float _scentDropInterval = 2.5f;

        [SerializeField, Min(0.1f), Tooltip("Radius within which the monster can pick up a scent marker.")]
        float _scentRange = 6f;

        [Header("Budget")]
        [SerializeField, Range(1, 32), Tooltip("Max perceivables evaluated for vision per tick. The remainder round-robin to the next tick.")]
        int _maxVisionCandidatesPerTick = 8;

        [SerializeField, Min(1f), Tooltip("Spatial-hash cell size for the perception bus, in metres. Should be near the typical sound radius.")]
        float _busCellSize = 12f;

        // -- baked --
        float[] _tagLut;

        public float FocusHalfAngle => _focusHalfAngle;
        public float FocusRange => _focusRange;
        public float MidHalfAngle => _midHalfAngle;
        public float MidRange => _midRange;
        public float NearRange => _nearRange;
        public int VisibilitySamples => _visibilitySamples;
        public LayerMask OcclusionMask => _occlusionMask;
        public float DarknessVisibility => _darknessVisibility;
        public float MinTimeToDetect => _minTimeToDetect;
        public float OcclusionTransmission => _occlusionTransmission;
        public float SoundLocalisationError => _soundLocalisationError;
        public float HysteresisDamping => _hysteresisDamping;
        public float HysteresisBand => _hysteresisBand;
        public float ConfidenceHalfLife => _confidenceHalfLife;
        public float ScentLifetime => _scentLifetime;
        public float ScentDropInterval => _scentDropInterval;
        public float ScentRange => _scentRange;
        public int MaxVisionCandidatesPerTick => _maxVisionCandidatesPerTick;
        public float BusCellSize => _busCellSize;

        /// <summary>Longest distance at which anything can be seen â€” used to size broadphase queries.</summary>
        public float MaxSightRange => math.max(_focusRange, math.max(_midRange, _nearRange));

        public float SightGainByDistance(float normalised) =>
            ConfigCurve.Evaluate01(_sightGainByDistance, normalised, 1f - normalised);

        public float SightGainByAngle(float normalised) =>
            ConfigCurve.Evaluate01(_sightGainByAngle, normalised, 1f - 0.6f * normalised);

        public float ChannelGain(StimulusChannel channel)
        {
            int i = (int)channel;
            return (_channelGain != null && i >= 0 && i < _channelGain.Length) ? _channelGain[i] : 1f;
        }

        public float TagWeightFor(StimulusTag tag)
        {
            if (_tagLut == null) BakeTagLut();
            int i = StimulusTagIndex.IndexOf(tag);
            return (i >= 0 && i < _tagLut.Length) ? _tagLut[i] : 1f;
        }

        public float DecayPerSecond(AwarenessLevel level)
        {
            int i = (int)level;
            return (_decayPerSecond != null && i >= 0 && i < _decayPerSecond.Length) ? _decayPerSecond[i] : 0.1f;
        }

        void OnEnable() => BakeTagLut();
        void OnValidate()
        {
            // A mid cone narrower than the focus cone is always an authoring slip and
            // produces a monster with a hole in its vision that nobody can explain.
            if (_midHalfAngle < _focusHalfAngle) _midHalfAngle = _focusHalfAngle;
            if (_midRange > _focusRange) _midRange = _focusRange;
            BakeTagLut();
        }

        void BakeTagLut()
        {
            _tagLut = new float[ConfigCounts.StimulusTagBits];
            for (int i = 0; i < _tagLut.Length; i++) _tagLut[i] = 1f;

            if (_tagWeights == null) return;
            for (int i = 0; i < _tagWeights.Length; i++)
            {
                int idx = StimulusTagIndex.IndexOf(_tagWeights[i].Tag);
                if (idx >= 0) _tagLut[idx] = _tagWeights[i].Weight;
            }
        }

        public void Validate(IList<string> problems)
        {
            if (_channelGain == null || _channelGain.Length != ConfigCounts.StimulusChannels)
            {
                problems.Add($"{name}: channelGain must have exactly {ConfigCounts.StimulusChannels} entries.");
            }
            if (_decayPerSecond == null || _decayPerSecond.Length != ConfigCounts.AwarenessLevels)
            {
                problems.Add($"{name}: decayPerSecond must have exactly {ConfigCounts.AwarenessLevels} entries.");
            }
            if (_hysteresisDamping >= 1f)
            {
                problems.Add($"{name}: hysteresisDamping >= 1 disables anti-oscillation; the monster will flicker between states.");
            }
            if (_minTimeToDetect < 0.3f)
            {
                problems.Add($"{name}: minTimeToDetect {_minTimeToDetect:F2}s is below 0.3s and will read to players as the AI cheating.");
            }
            if (_occlusionMask == 0)
            {
                problems.Add($"{name}: occlusionMask is empty â€” nothing will ever block line of sight.");
            }
        }
    }
}
