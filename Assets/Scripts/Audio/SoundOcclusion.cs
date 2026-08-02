// -----------------------------------------------------------------------------
// Vigil — occlusion.
//
// Two decisions worth defending:
//
//   ROUND-ROBIN, NOT EVERY VOICE EVERY FRAME. Most emitters are stationary and so
//   is the listener most of the time. Re-solving 24 voices at 144 fps is ~3,500
//   raycasts a second to learn almost nothing. A handful per frame is
//   indistinguishable to the ear and roughly an order of magnitude cheaper.
//
//   INTERPOLATE, NEVER SNAP. A low-pass cutoff that jumps is audible as a click,
//   and a click is a worse artefact than having no occlusion at all. Every value
//   here approaches its target over a configured time constant.
// -----------------------------------------------------------------------------

using UnityEngine;
using Vigil.Core.Diagnostics;
using Vigil.Data;

namespace Vigil.Audio
{
    public sealed class SoundOcclusion : MonoBehaviour
    {
        [SerializeField] VigilAudioService _audio = null;

        [SerializeField, Tooltip("Layers that muffle sound. Must NOT include players or NPCs - a teammate standing between you and a noise should not filter it.")]
        LayerMask _occluderMask = ~0;

        readonly RaycastHit[] _hits = new RaycastHit[8];

        int _cursor;

        public int LastSolvedCount { get; private set; }
        public float AverageOcclusion { get; private set; }

        void Start()
        {
            if (_audio == null) _audio = FindAnyObjectByType<VigilAudioService>();
            if (_audio == null)
            {
                VLog.Warn(LogCat.Audio, "SoundOcclusion has no VigilAudioService - occlusion disabled.", this);
                enabled = false;
            }
        }

        void Update()
        {
            if (_audio == null || !_audio.IsReady) return;

            AudioVoice[] voices = _audio.Voices;
            if (voices == null || voices.Length == 0) return;

            AudioConfig cfg = _audio.Config;
            int budget = cfg != null ? Mathf.Max(1, cfg.OcclusionSolvesPerFrame) : 6;

            SolveSome(voices, cfg, budget);
            ApplyAll(voices, cfg);
        }

        void SolveSome(AudioVoice[] voices, AudioConfig cfg, int budget)
        {
            Vector3 listener = _audio.ListenerPosition;
            int maxBlockers = cfg != null ? Mathf.Max(1, cfg.MaxOcclusionBlockers) : 4;

            int solved = 0;

            for (int n = 0; n < voices.Length && solved < budget; n++)
            {
                int i = (_cursor + n) % voices.Length;
                AudioVoice v = voices[i];

                if (!IsOccludable(v)) continue;

                Vector3 from = v.Source.transform.position;
                Vector3 delta = listener - from;
                float distance = delta.magnitude;

                if (distance < 0.05f)
                {
                    v.TargetOcclusion = 0f;
                    solved++;
                    continue;
                }

                int blockers = Physics.RaycastNonAlloc(
                    new Ray(from, delta / distance), _hits, distance,
                    _occluderMask, QueryTriggerInteraction.Ignore);

                v.TargetOcclusion = Mathf.Clamp01(Mathf.Min(blockers, maxBlockers) / (float)maxBlockers);
                solved++;
            }

            _cursor = (_cursor + budget) % voices.Length;
            LastSolvedCount = solved;
        }

        void ApplyAll(AudioVoice[] voices, AudioConfig cfg)
        {
            float open = cfg != null ? cfg.CutoffOpen : 22000f;
            float closed = cfg != null ? cfg.CutoffOccluded : 780f;
            float smoothing = cfg != null ? Mathf.Max(0.01f, cfg.OcclusionSmoothing) : 0.18f;

            float k = 1f - Mathf.Exp(-Time.deltaTime / smoothing);

            float total = 0f;
            int counted = 0;

            for (int i = 0; i < voices.Length; i++)
            {
                AudioVoice v = voices[i];
                if (!IsOccludable(v)) continue;

                v.Occlusion = Mathf.Lerp(v.Occlusion, v.TargetOcclusion, k);

                // Cutoff is interpolated in log space: linear Hz interpolation spends
                // most of its travel in frequencies the ear barely distinguishes, so a
                // linear fade sounds like it happens all at once near the end.
                float t = v.Occlusion;
                v.LowPass.cutoffFrequency = Mathf.Exp(Mathf.Lerp(Mathf.Log(open), Mathf.Log(closed), t));

                // Attenuate too. A wall does not only darken a sound, it quietens it —
                // filtering alone reads as "underwater" rather than "through a door".
                v.Source.volume = v.BaseVolume * (1f - t * 0.75f);

                total += v.Occlusion;
                counted++;
            }

            AverageOcclusion = counted > 0 ? total / counted : 0f;
        }

        /// <summary>
        /// Non-spatial voices are exempt. The score and the player's own breathing sit
        /// in front of the mix by design and must never be muffled by geometry the
        /// player happens to be standing behind.
        /// </summary>
        static bool IsOccludable(AudioVoice v)
        {
            if (v == null || !v.InUse || v.Source == null) return false;
            if (!v.Source.isPlaying) return false;
            if (v.Source.spatialBlend <= 0.01f) return false;
            return v.LowPass != null;
        }
    }
}
