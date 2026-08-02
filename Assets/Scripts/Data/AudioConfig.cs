using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;

namespace Vigil.Data
{
    [CreateAssetMenu(menuName = "Vigil/Audio/Audio Config", fileName = "AudioConfig")]
    public sealed class AudioConfig : ScriptableObject, IValidatableConfig
    {
        [Header("Occlusion â€” indexed by SurfaceMaterialClass")]
        [SerializeField, Tooltip("Fraction of volume that survives one surface of this class.")]
        float[] _transmission = new float[ConfigCounts.SurfaceMaterialClasses]
        { 1f, 0.55f, 0.48f, 0.62f, 0.30f, 0.22f, 0.70f, 0.80f };

        [SerializeField, Min(100f), Tooltip("Low-pass cutoff (Hz) with no occluder.")]
        float _cutoffOpen = 22000f;

        [SerializeField, Min(100f), Tooltip("Low-pass cutoff (Hz) fully occluded. This is what 'muffled through a wall' actually is.")]
        float _cutoffOccluded = 780f;

        [SerializeField, Range(1, 8)] int _maxOcclusionBlockers = 4;
        [SerializeField, Range(1, 32), Tooltip("Emitters re-solved for occlusion per frame. Round-robin â€” solving all of them every frame is pure waste.")]
        int _occlusionSolvesPerFrame = 6;

        [SerializeField, Min(0.01f), Tooltip("Seconds to interpolate toward a new occlusion value. Snapping is audible as a click.")]
        float _occlusionSmoothing = 0.18f;

        [Header("Propagation")]
        [SerializeField, Range(1, 4), Tooltip("Portal hops considered when routing a sound around corners.")]
        int _maxPropagationHops = 2;

        [SerializeField, Min(0f), Tooltip("Extra attenuation per portal hop.")]
        float _perHopAttenuation = 0.25f;

        [Header("Score â€” indexed by DreadPhase")]
        [SerializeField, Tooltip("Music intensity 0..1.")]
        float[] _musicIntensity = new float[ConfigCounts.DreadPhases] { 0.5f, 1f, 0.3f, 0.08f };

        [SerializeField, Range(0f, 1f), Tooltip("Ambient bed level at Peak. Kept LOW deliberately: horror scoring is subtractive. At peak dread you remove the bed and leave the player alone with their own breathing.")]
        float _ambientBedAtPeak = 0.12f;

        [Header("Stingers â€” cooldown seconds, indexed by StingerKind")]
        [SerializeField]
        float[] _stingerCooldown = new float[ConfigCounts.StingerKinds]
        { 6f, 12f, 8f, 3f, 10f, 15f, 45f, 20f };

        [SerializeField, Range(4, 64)] int _emitterPoolSize = 24;

        public float Transmission(SurfaceMaterialClass c) => Idx(_transmission, (int)c, 0.5f);
        public float MusicIntensity(DreadPhase p) => Idx(_musicIntensity, (int)p, 0.5f);
        public float StingerCooldown(StingerKind k) => Idx(_stingerCooldown, (int)k, 8f);

        public float CutoffOpen => _cutoffOpen;
        public float CutoffOccluded => _cutoffOccluded;
        public int MaxOcclusionBlockers => _maxOcclusionBlockers;
        public int OcclusionSolvesPerFrame => _occlusionSolvesPerFrame;
        public float OcclusionSmoothing => _occlusionSmoothing;
        public int MaxPropagationHops => _maxPropagationHops;
        public float PerHopAttenuation => _perHopAttenuation;
        public float AmbientBedAtPeak => _ambientBedAtPeak;
        public int EmitterPoolSize => _emitterPoolSize;

        static float Idx(float[] a, int i, float f) => (a != null && i >= 0 && i < a.Length) ? a[i] : f;

        public void Validate(IList<string> problems)
        {
            if (_transmission == null || _transmission.Length != ConfigCounts.SurfaceMaterialClasses)
            {
                problems.Add($"{name}: transmission must have exactly {ConfigCounts.SurfaceMaterialClasses} entries.");
            }
            if (_cutoffOccluded >= _cutoffOpen)
            {
                problems.Add($"{name}: occluded cutoff must be below open cutoff, or occlusion brightens sound.");
            }
        }
    }
}
