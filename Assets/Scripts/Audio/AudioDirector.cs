// -----------------------------------------------------------------------------
// Vigil — adaptive score and stingers.
//
// THE LOAD-BEARING IDEA: horror scoring is SUBTRACTIVE.
//
// The obvious implementation raises music volume as threat rises. It is wrong. A
// loud peak produces noise, and noise is something the player tunes out. What
// actually lands is REMOVAL: the ambient bed that has been quietly filling the
// room for two minutes drops away, and the player is suddenly alone with their
// own breathing and whatever is in the corridor with them.
//
// So the loudest bed here is BUILDUP, not Peak. Peak is nearly silent. Anyone
// "fixing" that will make the game markedly less frightening while making the
// numbers look more sensible.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Events;
using Vigil.Core.Services;
using Vigil.Data;

namespace Vigil.Audio
{
    public sealed class AudioDirector : MonoBehaviour
    {
        [SerializeField] VigilAudioService _audio = null;

        [SerializeField, Range(0.05f, 4f), Tooltip("Seconds for a bed layer to reach its new target. Slow on purpose - an audible fade is a cue in itself.")]
        float _crossfadeSeconds = 1.6f;

        AudioVoice _low, _mid, _high;

        IDisposable _phaseSub, _chaseStartSub, _chaseEndSub, _awarenessSub, _downedSub, _killedSub;
        IEventBus _events;

        StingerSystem _stingers;

        float _targetLow, _targetMid, _targetHigh;
        float _currentLow, _currentMid, _currentHigh;

        public DreadPhase CurrentPhase { get; private set; } = DreadPhase.Fadeout;

        /// <summary>Aggregate bed level 0..1, for the debug overlay.</summary>
        public float CurrentIntensity => Mathf.Clamp01(_currentLow + _currentMid + _currentHigh);

        void Start()
        {
            if (_audio == null) _audio = FindAnyObjectByType<VigilAudioService>();
            if (_audio == null || !_audio.IsReady)
            {
                VLog.Warn(LogCat.Audio, "AudioDirector has no ready VigilAudioService - score disabled.", this);
                enabled = false;
                return;
            }

            _stingers = new StingerSystem(_audio);

            StartBed();
            ApplyPhase(DreadPhase.Fadeout, instant: true);
            Subscribe();
        }

        void StartBed()
        {
            // Non-spatial: the score is not in the world, and it must never be
            // occluded by a wall the player happens to be standing behind.
            _low = _audio.PlayLoop(ClipKind.DroneLow, Vector3.zero, 0f, SoundPriority.Ambient, spatial: false);
            _mid = _audio.PlayLoop(ClipKind.DroneMid, Vector3.zero, 0f, SoundPriority.Ambient, spatial: false);
            _high = _audio.PlayLoop(ClipKind.DroneHigh, Vector3.zero, 0f, SoundPriority.Ambient, spatial: false);
        }

        void Subscribe()
        {
            _events = Services.TryGet<IEventBus>();
            if (_events == null)
            {
                VLog.Warn(LogCat.Audio, "No IEventBus - the score will not react to gameplay.", this);
                return;
            }

            _phaseSub = _events.Subscribe<DreadPhaseChangedEvent>(e => ApplyPhase(e.To, instant: false));

            _chaseStartSub = _events.Subscribe<ChaseStartedEvent>(_ => _stingers.Play(StingerKind.ChaseStart, ClipKind.StingerChase));
            _chaseEndSub = _events.Subscribe<ChaseEndedEvent>(_ => _stingers.Play(StingerKind.ChaseEnd, ClipKind.StingerFalse));
            _downedSub = _events.Subscribe<PlayerDownedEvent>(_ => _stingers.Play(StingerKind.Downed, ClipKind.StingerReveal));
            _killedSub = _events.Subscribe<PlayerKilledEvent>(_ => _stingers.Play(StingerKind.Death, ClipKind.StingerReveal));

            _awarenessSub = _events.Subscribe<AwarenessChangedEvent>(e =>
            {
                if (e.Level >= AwarenessLevel.Confirmed)
                {
                    _stingers.Play(StingerKind.Sighting, ClipKind.StingerChase);
                }
                else if (e.Level == AwarenessLevel.Suspicious)
                {
                    // The entity noticed something that was not you. Cheap, frequent,
                    // and heavily cooldown-gated so it stays a tell rather than a tic.
                    _stingers.Play(StingerKind.FalseScare, ClipKind.StingerFalse);
                }
            });
        }

        /// <summary>
        /// Bed layer volumes for a phase. Pure and static so the subtractive mix rule
        /// can be asserted directly by a test rather than inferred from a running
        /// scene — see AudioTests.ScoreIsQuieterAtPeakThanAtBuildup.
        /// </summary>
        public static void ComputeBedTargets(
            DreadPhase phase, AudioConfig cfg, out float low, out float mid, out float high)
        {
            float intensity = cfg != null ? cfg.MusicIntensity(phase) : 0.5f;
            float bedAtPeak = cfg != null ? cfg.AmbientBedAtPeak : 0.12f;

            switch (phase)
            {
                case DreadPhase.Buildup:
                    // Loudest bed in the game. This is where tension is manufactured:
                    // all three layers present and slowly thickening.
                    low = 0.85f * intensity;
                    mid = 0.60f * intensity;
                    high = 0.35f * intensity;
                    break;

                case DreadPhase.Peak:
                    // THE SUBTRACTION. Strip the bed to almost nothing. The player is
                    // left with footsteps, breathing and the entity — and the sudden
                    // absence is what makes the moment land.
                    low = bedAtPeak;
                    mid = 0f;
                    high = 0f;
                    break;

                case DreadPhase.Relax:
                    // The bed returns as the threat withdraws, which is how the player
                    // knows they are allowed to breathe.
                    low = 0.45f * intensity;
                    mid = 0.20f * intensity;
                    high = 0f;
                    break;

                default: // Fadeout
                    low = 0.22f * intensity;
                    mid = 0.06f * intensity;
                    high = 0f;
                    break;
            }
        }

        void ApplyPhase(DreadPhase phase, bool instant)
        {
            CurrentPhase = phase;

            ComputeBedTargets(phase, _audio.Config, out _targetLow, out _targetMid, out _targetHigh);

            if (instant)
            {
                _currentLow = _targetLow;
                _currentMid = _targetMid;
                _currentHigh = _targetHigh;
                PushVolumes();
            }

            VLog.Info(LogCat.Audio, $"Score -> {phase} (low {_targetLow:F2} mid {_targetMid:F2} high {_targetHigh:F2})");
        }

        void Update()
        {
            if (_audio == null || !_audio.IsReady) return;

            _stingers?.Tick(Time.deltaTime);

            // Exponential approach rather than MoveTowards: a linear fade has an
            // audible "arrival" at the end, an exponential one just settles.
            float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, _crossfadeSeconds));

            _currentLow = Mathf.Lerp(_currentLow, _targetLow, k);
            _currentMid = Mathf.Lerp(_currentMid, _targetMid, k);
            _currentHigh = Mathf.Lerp(_currentHigh, _targetHigh, k);

            PushVolumes();
        }

        void PushVolumes()
        {
            _audio.SetVolume(_low, _currentLow);
            _audio.SetVolume(_mid, _currentMid);
            _audio.SetVolume(_high, _currentHigh);
        }

        void OnDestroy()
        {
            _phaseSub?.Dispose();
            _chaseStartSub?.Dispose();
            _chaseEndSub?.Dispose();
            _awarenessSub?.Dispose();
            _downedSub?.Dispose();
            _killedSub?.Dispose();

            if (_audio == null) return;
            _audio.Stop(_low);
            _audio.Stop(_mid);
            _audio.Stop(_high);
        }
    }

    /// <summary>
    /// One-shot cues with per-kind cooldowns.
    ///
    /// <para>The cooldown is the entire point. Awareness bands flicker near their
    /// thresholds by design (hysteresis damps it but does not eliminate it), and
    /// without a gate the same sting fires several times a second. That turns the
    /// scariest sound in the game into comedy within one session.</para>
    /// </summary>
    public sealed class StingerSystem
    {
        readonly VigilAudioService _audio;
        readonly float[] _cooldownRemaining;

        public StingerSystem(VigilAudioService audio)
        {
            _audio = audio;
            _cooldownRemaining = new float[ConfigCounts.StingerKinds];
        }

        public void Tick(float deltaTime)
        {
            for (int i = 0; i < _cooldownRemaining.Length; i++)
            {
                if (_cooldownRemaining[i] > 0f) _cooldownRemaining[i] -= deltaTime;
            }
        }

        public bool Play(StingerKind kind, ClipKind clip, float volume = 0.9f)
        {
            if (_audio == null || !_audio.IsReady) return false;

            int i = (int)kind;
            if (i < 0 || i >= _cooldownRemaining.Length) return false;
            if (_cooldownRemaining[i] > 0f) return false;

            AudioConfig cfg = _audio.Config;
            _cooldownRemaining[i] = cfg != null ? cfg.StingerCooldown(kind) : 8f;

            // Critical priority: a stinger is never the cue that gets stolen when the
            // pool is under pressure. Played at the listener so it reads as score
            // rather than as something in the room.
            _audio.PlayOneShot(clip, _audio.ListenerPosition, volume, SoundPriority.Critical, pitchJitter: 0.03f);
            return true;
        }
    }
}
