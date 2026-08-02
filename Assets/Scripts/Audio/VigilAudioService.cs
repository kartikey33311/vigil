// -----------------------------------------------------------------------------
// Vigil — the audio service and its voice pool.
//
// One entry point for playback. Everything else in this assembly, and everything
// outside it, goes through here rather than touching AudioSource directly.
//
// Voice stealing is not an optimisation detail — it is a design control. When the
// generator, three footstep emitters, a door and a stinger all fire in the same
// second, SOMETHING has to be dropped. Dropping it by priority means the stinger
// is never the thing that gets cut, which is exactly the guarantee a horror mix
// needs. Letting Unity allocate unbounded AudioSources instead produces a mushy
// wall where the important cue is inaudible.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.Audio;
using Vigil.Core.Diagnostics;
using Vigil.Core.Mathx;
using Vigil.Data;

namespace Vigil.Audio
{
    /// <summary>Mix priority. Higher wins when the pool is exhausted.</summary>
    public enum SoundPriority : byte
    {
        Ambient = 0,
        Footstep = 1,
        World = 2,
        Threat = 3,

        /// <summary>Never stolen. Stingers and screams.</summary>
        Critical = 4
    }

    /// <summary>A live voice. Returned so callers can stop or reposition a loop.</summary>
    public sealed class AudioVoice
    {
        public AudioSource Source;
        public AudioLowPassFilter LowPass;
        public SoundPriority Priority;
        public bool InUse;
        public bool IsLoop;
        public float BaseVolume;

        /// <summary>Occlusion 0 (clear) .. 1 (fully blocked). Smoothed by SoundOcclusion.</summary>
        public float Occlusion;
        public float TargetOcclusion;

        /// <summary>Monotonic id so a stale handle cannot control a recycled voice.</summary>
        public int Version;
    }

    public sealed class VigilAudioService : MonoBehaviour
    {
        [SerializeField] AudioConfig _config = null;
        [SerializeField] AudioMixerGroup _sfxGroup = null;

        AudioClip[] _clips;
        AudioVoice[] _voices;
        Transform _root;
        DeterministicRandom _rng;

        /// <summary>Where the local listener is. Set by the camera rig owner each frame.</summary>
        public Vector3 ListenerPosition { get; private set; }

        public AudioConfig Config => _config;
        public AudioVoice[] Voices => _voices;

        /// <summary>Voices stolen since startup. Sustained growth means the pool is too small.</summary>
        public int StealCount { get; private set; }

        public bool IsReady { get; private set; }

        public void Initialise(AudioConfig config)
        {
            if (config != null) _config = config;

            _rng = new DeterministicRandom(0x5EED1u);
            _clips = ProceduralAudio.BuildLibrary();

            _root = new GameObject("[Audio Voices]").transform;
            _root.SetParent(transform, false);

            int poolSize = _config != null ? _config.EmitterPoolSize : 24;
            _voices = new AudioVoice[Mathf.Max(4, poolSize)];

            for (int i = 0; i < _voices.Length; i++)
            {
                GameObject go = new GameObject($"Voice_{i}");
                go.transform.SetParent(_root, false);

                AudioSource src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 1f;                       // fully 3D by default
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = 1.5f;
                src.maxDistance = 45f;
                src.dopplerLevel = 0f;                       // doppler on footsteps sounds wrong
                if (_sfxGroup != null) src.outputAudioMixerGroup = _sfxGroup;

                AudioLowPassFilter lpf = go.AddComponent<AudioLowPassFilter>();
                lpf.cutoffFrequency = _config != null ? _config.CutoffOpen : 22000f;

                _voices[i] = new AudioVoice { Source = src, LowPass = lpf, Version = 1 };
            }

            IsReady = true;
            VLog.Info(LogCat.Audio, $"Audio ready: {_clips.Length} synthesised clips, {_voices.Length} voices.");
        }

        public void SetListenerPosition(Vector3 position) => ListenerPosition = position;

        public AudioClip GetClip(ClipKind kind)
        {
            int i = (int)kind;
            return (_clips != null && i >= 0 && i < _clips.Length) ? _clips[i] : null;
        }

        // =====================================================================
        // Playback
        // =====================================================================

        /// <summary>Fires a positional one-shot. Returns null only if the pool refused it.</summary>
        public AudioVoice PlayOneShot(
            ClipKind kind, Vector3 position, float volume = 1f,
            SoundPriority priority = SoundPriority.World, float pitchJitter = 0.08f, float maxDistance = 45f)
        {
            if (!IsReady) return null;

            AudioClip clip = GetClip(kind);
            if (clip == null) return null;

            AudioVoice voice = Acquire(priority);
            if (voice == null) return null;

            // Per-instance pitch and volume jitter. Identical repeats are the single
            // most recognisable "video game" tell in a footstep loop.
            float pitch = 1f + (_rng.NextFloat() * 2f - 1f) * pitchJitter;

            voice.Source.clip = clip;
            voice.Source.loop = false;
            voice.Source.pitch = pitch;
            voice.Source.volume = volume;
            voice.Source.maxDistance = maxDistance;
            voice.Source.spatialBlend = 1f;
            voice.Source.transform.position = position;

            voice.BaseVolume = volume;
            voice.IsLoop = false;
            voice.Occlusion = 0f;
            voice.TargetOcclusion = 0f;

            voice.Source.Play();
            return voice;
        }

        /// <summary>
        /// Starts a looping voice (drone bed, machinery). Caller owns it until Stop.
        /// Non-spatial when <paramref name="spatial"/> is false — used for the score
        /// and for the player's own breathing, which must sit in front of the mix.
        /// </summary>
        public AudioVoice PlayLoop(
            ClipKind kind, Vector3 position, float volume,
            SoundPriority priority = SoundPriority.Ambient, bool spatial = true, float maxDistance = 45f)
        {
            if (!IsReady) return null;

            AudioClip clip = GetClip(kind);
            if (clip == null) return null;

            AudioVoice voice = Acquire(priority);
            if (voice == null) return null;

            voice.Source.clip = clip;
            voice.Source.loop = true;
            voice.Source.pitch = 1f;
            voice.Source.volume = volume;
            voice.Source.spatialBlend = spatial ? 1f : 0f;
            voice.Source.maxDistance = maxDistance;
            voice.Source.transform.position = position;

            voice.BaseVolume = volume;
            voice.IsLoop = true;
            voice.Occlusion = 0f;
            voice.TargetOcclusion = 0f;

            voice.Source.Play();
            return voice;
        }

        public void Stop(AudioVoice voice)
        {
            if (voice == null || !voice.InUse) return;

            voice.Source.Stop();
            voice.Source.clip = null;
            voice.InUse = false;
            voice.IsLoop = false;
            unchecked { voice.Version++; }
        }

        public void SetVolume(AudioVoice voice, float volume)
        {
            if (voice == null || !voice.InUse) return;
            voice.BaseVolume = volume;
            voice.Source.volume = volume * (1f - voice.Occlusion * 0.75f);
        }

        public void SetPosition(AudioVoice voice, Vector3 position)
        {
            if (voice == null || !voice.InUse) return;
            voice.Source.transform.position = position;
        }

        AudioVoice Acquire(SoundPriority priority)
        {
            // Prefer a genuinely free voice.
            for (int i = 0; i < _voices.Length; i++)
            {
                AudioVoice v = _voices[i];
                if (v.InUse && v.Source.isPlaying) continue;

                v.InUse = true;
                v.Priority = priority;
                return v;
            }

            // Pool exhausted. Steal the lowest-priority voice that outranks nothing,
            // preferring the quietest so the theft is least audible.
            int bestIndex = -1;
            float bestScore = float.MaxValue;

            for (int i = 0; i < _voices.Length; i++)
            {
                AudioVoice v = _voices[i];
                if (v.Priority >= priority) continue;   // never steal from an equal or better cue
                if (v.Priority == SoundPriority.Critical) continue;

                float score = (int)v.Priority * 10f + v.Source.volume;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                // Everything playing is at least as important. Dropping this cue is
                // the correct outcome — it is what keeps the mix legible.
                return null;
            }

            StealCount++;

            AudioVoice stolen = _voices[bestIndex];
            stolen.Source.Stop();
            unchecked { stolen.Version++; }
            stolen.InUse = true;
            stolen.Priority = priority;

            return stolen;
        }

        void Update()
        {
            if (!IsReady) return;

            // Release finished one-shots so Acquire's fast path stays fast.
            for (int i = 0; i < _voices.Length; i++)
            {
                AudioVoice v = _voices[i];
                if (v.InUse && !v.IsLoop && !v.Source.isPlaying)
                {
                    v.InUse = false;
                    v.Source.clip = null;
                }
            }
        }

        void OnDestroy()
        {
            if (_voices == null) return;
            for (int i = 0; i < _voices.Length; i++)
            {
                if (_voices[i]?.Source != null) _voices[i].Source.Stop();
            }
        }
    }
}
