// -----------------------------------------------------------------------------
// Vigil — the player's own body.
//
// Breathing and heartbeat, driven by composure. This is the closing half of the
// loop the composure system opens: low composure makes you louder to the MONSTER
// (PlayerCharacter.NoiseMultiplier), and it makes you louder to YOURSELF here.
// The player hears their own panic before they think to read the meter.
//
// These are the only sounds in the game that are never occluded and never
// positional. They are inside your head, not in the room, and treating them as
// world audio — muffling your own breathing because you stood behind a crate —
// instantly breaks the effect.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;
using Vigil.Core.Diagnostics;
using Vigil.Core.Events;
using Vigil.Core.Services;

namespace Vigil.Audio
{
    public sealed class PlayerBodyAudio : MonoBehaviour
    {
        [SerializeField] VigilAudioService _audio = null;

        [SerializeField, Range(0f, 1f), Tooltip("Composure below which the heartbeat starts. Above it the player is calm enough not to hear their own pulse.")]
        float _heartbeatThreshold = 0.55f;

        [SerializeField, Min(0.5f), Tooltip("Seconds between breaths at full composure.")]
        float _calmBreathInterval = 5.5f;

        [SerializeField, Min(0.2f), Tooltip("Seconds between breaths at zero composure.")]
        float _panicBreathInterval = 1.5f;

        [SerializeField, Min(1f)] float _maxComposure = 100f;

        IDisposable _composureSub;

        AudioVoice _heartbeat;
        float _breathTimer;
        float _composure01 = 1f;
        ulong _localPlayerId;
        bool _hasLocalPlayer;

        /// <summary>Normalised composure the audio is currently reacting to.</summary>
        public float Composure01 => _composure01;

        public bool HeartbeatActive => _heartbeat != null && _heartbeat.InUse;

        void Start()
        {
            if (_audio == null) _audio = FindAnyObjectByType<VigilAudioService>();

            IEventBus events = Services.TryGet<IEventBus>();
            if (events == null)
            {
                VLog.Warn(LogCat.Audio, "PlayerBodyAudio found no IEventBus - body audio disabled.", this);
                return;
            }

            _composureSub = events.Subscribe<CompositionChangedEvent>(OnComposureChanged);
        }

        /// <summary>
        /// Binds this to one player. Without it, a host would hear every player's
        /// composure driving its own breathing, which is both wrong and confusing.
        /// </summary>
        public void SetLocalPlayer(ulong playerId)
        {
            _localPlayerId = playerId;
            _hasLocalPlayer = true;
        }

        void OnComposureChanged(CompositionChangedEvent evt)
        {
            if (_hasLocalPlayer && evt.PlayerId != _localPlayerId) return;

            _composure01 = Mathf.Clamp01(evt.Composure / Mathf.Max(1f, _maxComposure));
        }

        void Update()
        {
            if (_audio == null || !_audio.IsReady) return;

            float panic = 1f - _composure01;

            TickBreathing(panic);
            TickHeartbeat(panic);
        }

        void TickBreathing(float panic)
        {
            _breathTimer -= Time.deltaTime;
            if (_breathTimer > 0f) return;

            float interval = Mathf.Lerp(_calmBreathInterval, _panicBreathInterval, panic);
            _breathTimer = interval;

            // Volume rises with panic but never reaches full — breathing that dominates
            // the mix stops being a tell and becomes an irritation.
            float volume = Mathf.Lerp(0.18f, 0.65f, panic);

            AudioVoice voice = _audio.PlayOneShot(
                ClipKind.Breath, _audio.ListenerPosition, volume,
                SoundPriority.Threat, pitchJitter: 0.06f);

            MakeNonSpatial(voice, Mathf.Lerp(0.95f, 1.25f, panic));
        }

        void TickHeartbeat(float panic)
        {
            bool shouldBeat = _composure01 < _heartbeatThreshold;

            if (!shouldBeat)
            {
                if (_heartbeat != null)
                {
                    _audio.Stop(_heartbeat);
                    _heartbeat = null;
                }
                return;
            }

            if (_heartbeat == null || !_heartbeat.InUse)
            {
                _heartbeat = _audio.PlayLoop(
                    ClipKind.Heartbeat, _audio.ListenerPosition, 0f,
                    SoundPriority.Threat, spatial: false);

                if (_heartbeat == null) return;
            }

            // Normalised 0..1 across the band below the threshold, so the ramp starts
            // subtle exactly where the heartbeat first becomes audible rather than
            // punching in at full volume.
            float t = Mathf.InverseLerp(_heartbeatThreshold, 0f, _composure01);

            _audio.SetVolume(_heartbeat, Mathf.Lerp(0.12f, 0.7f, t));

            // Rate rises with panic. The clip is one lub-dub per second at pitch 1.
            _heartbeat.Source.pitch = Mathf.Lerp(1f, 1.75f, t);
            _heartbeat.Source.spatialBlend = 0f;
        }

        static void MakeNonSpatial(AudioVoice voice, float pitch)
        {
            if (voice == null || voice.Source == null) return;
            voice.Source.spatialBlend = 0f;
            voice.Source.pitch = pitch;
        }

        void OnDestroy()
        {
            _composureSub?.Dispose();
            if (_audio != null && _heartbeat != null) _audio.Stop(_heartbeat);
        }
    }
}
