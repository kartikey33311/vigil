// -----------------------------------------------------------------------------
// Vigil — world sounds.
//
// Turns NoiseEmittedEvent into audible one-shots. This is the only thing in the
// audio assembly that knows a world exists, and it learns about it purely through
// events — it never touches a player, a door or an NPC.
//
// The per-source throttle matters more than it looks. A generator emits machinery
// noise several times a second and a sprinting player emits footsteps; without a
// gate, two emitters can occupy the entire voice pool and starve the stinger that
// is supposed to be the loudest thing in the scene. Throttling here is what keeps
// the mix legible under load.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Events;
using Vigil.Core.Services;

namespace Vigil.Audio
{
    public sealed class WorldAudioListener : MonoBehaviour
    {
        [SerializeField] VigilAudioService _audio = null;

        [SerializeField, Range(0.02f, 1f), Tooltip("Minimum seconds between one-shots from the same emitter.")]
        float _perSourceInterval = 0.16f;

        [SerializeField, Range(4, 64)] int _trackedSources = 24;

        IDisposable _noiseSub;

        // Fixed-capacity ring of (sourceId, lastPlayedAt). A Dictionary that grows
        // with every entity id seen would leak across a long session.
        ulong[] _sourceIds;
        float[] _lastPlayed;
        int _writeCursor;

        public int PlayedCount { get; private set; }
        public int ThrottledCount { get; private set; }

        void Start()
        {
            if (_audio == null) _audio = FindAnyObjectByType<VigilAudioService>();

            _sourceIds = new ulong[Mathf.Max(4, _trackedSources)];
            _lastPlayed = new float[_sourceIds.Length];

            IEventBus events = Services.TryGet<IEventBus>();
            if (events == null)
            {
                VLog.Warn(LogCat.Audio, "WorldAudioListener found no IEventBus - world sounds disabled.", this);
                return;
            }

            _noiseSub = events.Subscribe<NoiseEmittedEvent>(OnNoise);
        }

        void OnNoise(NoiseEmittedEvent evt)
        {
            if (_audio == null || !_audio.IsReady) return;

            Stimulus s = evt.Stimulus;

            if (s.Intensity <= 0.01f) return;
            if (!ShouldPlay(s.SourceId)) { ThrottledCount++; return; }

            ClipKind clip;
            SoundPriority priority;
            ResolveClip(s, out clip, out priority);

            // Radius is the design-authored audible range of the noise, so it maps
            // directly to rolloff distance. A crouch genuinely does not carry.
            float maxDistance = Mathf.Max(4f, s.Radius);
            float volume = Mathf.Clamp01(s.Intensity);

            _audio.PlayOneShot(clip, (Vector3)s.Position, volume, priority, pitchJitter: 0.10f, maxDistance: maxDistance);
            PlayedCount++;
        }

        static void ResolveClip(in Stimulus s, out ClipKind clip, out SoundPriority priority)
        {
            // StimulusTag is a [Flags] enum, so these must be bitwise tests. Equality
            // would silently fail the moment an emitter sets two tags.
            StimulusTag tag = s.Tag;

            if ((tag & StimulusTag.Scream) != 0)
            {
                clip = ClipKind.Scream;
                priority = SoundPriority.Threat;
                return;
            }

            if ((tag & (StimulusTag.Footstep | StimulusTag.Sprint)) != 0)
            {
                clip = FootstepVariant(s.SourceId);
                priority = SoundPriority.Footstep;
                return;
            }

            if ((tag & StimulusTag.Door) != 0)
            {
                clip = ClipKind.DoorMove;
                priority = SoundPriority.World;
                return;
            }

            if ((tag & StimulusTag.Machinery) != 0)
            {
                clip = ClipKind.Machinery;
                priority = SoundPriority.World;
                return;
            }

            if ((tag & (StimulusTag.Impact | StimulusTag.Breakage)) != 0)
            {
                clip = ClipKind.Impact;
                priority = SoundPriority.World;
                return;
            }

            clip = ClipKind.Impact;
            priority = SoundPriority.World;
        }

        /// <summary>
        /// Picks a footstep material from the source id, so a given emitter sounds
        /// consistent. Randomising per step would make one character audibly change
        /// shoes mid-corridor.
        /// </summary>
        static ClipKind FootstepVariant(ulong sourceId)
        {
            uint h = (uint)(sourceId ^ (sourceId >> 32)) * 2654435761u;
            switch (h % 3u)
            {
                case 0u: return ClipKind.FootstepConcrete;
                case 1u: return ClipKind.FootstepMetal;
                default: return ClipKind.FootstepWood;
            }
        }

        bool ShouldPlay(ulong sourceId)
        {
            float now = Time.time;

            for (int i = 0; i < _sourceIds.Length; i++)
            {
                if (_sourceIds[i] != sourceId) continue;

                if (now - _lastPlayed[i] < _perSourceInterval) return false;

                _lastPlayed[i] = now;
                return true;
            }

            // Unseen source: claim the next ring slot, evicting the oldest entry.
            _sourceIds[_writeCursor] = sourceId;
            _lastPlayed[_writeCursor] = now;
            _writeCursor = (_writeCursor + 1) % _sourceIds.Length;

            return true;
        }

        void OnDestroy()
        {
            _noiseSub?.Dispose();
        }
    }
}
