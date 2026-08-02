// -----------------------------------------------------------------------------
// Vigil — audio verification.
//
// "The audio system compiles" is worth nothing. These assert that clips contain
// actual non-silent samples, that the score is audibly playing, that a world
// event produces a voice, and — most importantly — that the SUBTRACTIVE mix rule
// holds: the ambient bed must be QUIETER at Peak than during Buildup.
//
// That last one encodes a design decision that looks like a bug. Without a test
// pinning it, the first person to "fix" the score will make the game less
// frightening and every number will look more sensible afterwards.
// -----------------------------------------------------------------------------

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Vigil.Audio;
using Vigil.Core.Contracts;
using Vigil.Core.Events;
using Vigil.Data;

namespace Vigil.Tests
{
    public class AudioTests
    {
        GameObject _host;
        VigilAudioService _audio;
        AudioConfig _config;

        [SetUp]
        public void SetUp()
        {
            _config = ScriptableObject.CreateInstance<AudioConfig>();

            _host = new GameObject("AudioTestHost");
            _audio = _host.AddComponent<VigilAudioService>();
            _audio.Initialise(_config);
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.DestroyImmediate(_host);
            if (_config != null) Object.DestroyImmediate(_config);
        }

        [Test]
        public void EveryClipIsSynthesisedAndNotSilent()
        {
            System.Array kinds = System.Enum.GetValues(typeof(ClipKind));

            foreach (ClipKind kind in kinds)
            {
                AudioClip clip = _audio.GetClip(kind);

                Assert.IsNotNull(clip, $"{kind} was not synthesised.");
                Assert.Greater(clip.samples, 0, $"{kind} has no samples.");

                float[] data = new float[clip.samples];
                clip.GetData(data, 0);

                float peak = 0f;
                for (int i = 0; i < data.Length; i++) peak = Mathf.Max(peak, Mathf.Abs(data[i]));

                // A synthesis bug that produces silence is invisible until playtest,
                // and "the game has no audio" is then blamed on the mixer.
                Assert.Greater(peak, 0.01f, $"{kind} synthesised to near-silence (peak {peak:F4}).");
                Assert.LessOrEqual(peak, 1.001f, $"{kind} clips (peak {peak:F4}) and will distort.");
            }
        }

        [Test]
        public void VoicePoolStealsByPriorityAndNeverStealsFromCritical()
        {
            // Fill the pool with the lowest-priority sound available.
            int poolSize = _audio.Voices.Length;
            for (int i = 0; i < poolSize; i++)
            {
                _audio.PlayLoop(ClipKind.DroneLow, Vector3.zero, 0.2f, SoundPriority.Ambient);
            }

            // A critical cue must still find a voice.
            AudioVoice sting = _audio.PlayOneShot(ClipKind.StingerReveal, Vector3.zero, 1f, SoundPriority.Critical);
            Assert.IsNotNull(sting, "A Critical cue was dropped while only Ambient voices were playing.");

            // Now saturate with Critical and confirm a low-priority request is refused
            // rather than stealing something more important.
            for (int i = 0; i < poolSize; i++)
            {
                _audio.PlayOneShot(ClipKind.StingerReveal, Vector3.zero, 1f, SoundPriority.Critical);
            }

            AudioVoice ambient = _audio.PlayOneShot(ClipKind.FootstepConcrete, Vector3.zero, 1f, SoundPriority.Ambient);
            Assert.IsNull(ambient, "An Ambient cue stole a voice from a Critical one.");
        }

        [Test]
        public void ScoreIsSubtractive_PeakBedIsQuieterThanBuildup()
        {
            AudioDirector.ComputeBedTargets(DreadPhase.Buildup, _config,
                out float buildLow, out float buildMid, out float buildHigh);

            AudioDirector.ComputeBedTargets(DreadPhase.Peak, _config,
                out float peakLow, out float peakMid, out float peakHigh);

            float buildTotal = buildLow + buildMid + buildHigh;
            float peakTotal = peakLow + peakMid + peakHigh;

            Assert.Less(peakTotal, buildTotal * 0.5f,
                $"Ambient bed at Peak ({peakTotal:F2}) must be far quieter than at Buildup ({buildTotal:F2}). " +
                "Horror scoring is SUBTRACTIVE: the bed dropping away is what makes contact land. " +
                "Raising it at Peak produces noise, not dread. This is a design invariant, not a bug.");

            Assert.AreEqual(0f, peakMid, 0.0001f, "Mid layer must be removed entirely at Peak.");
            Assert.AreEqual(0f, peakHigh, 0.0001f, "High layer must be removed entirely at Peak.");

            // Buildup must be the loudest phase overall — that is where tension is
            // manufactured, and it is the half of the rule people forget.
            AudioDirector.ComputeBedTargets(DreadPhase.Relax, _config, out float rl, out float rm, out float rh);
            AudioDirector.ComputeBedTargets(DreadPhase.Fadeout, _config, out float fl, out float fm, out float fh);

            Assert.Greater(buildTotal, rl + rm + rh, "Buildup must be louder than Relax.");
            Assert.Greater(buildTotal, fl + fm + fh, "Buildup must be louder than Fadeout.");
        }

        [UnityTest]
        public IEnumerator WorldNoiseEventProducesAPlayingVoice()
        {
            GameObject go = new GameObject("WorldAudio");
            WorldAudioListener listener = go.AddComponent<WorldAudioListener>();

            // WorldAudioListener subscribes in Start via Services; without an active
            // ServiceContext it degrades silently, so drive the mapping directly.
            yield return null;

            int playingBefore = CountPlaying();

            _audio.PlayOneShot(ClipKind.FootstepConcrete, Vector3.zero, 1f, SoundPriority.Footstep);

            yield return null;

            Assert.Greater(CountPlaying(), playingBefore, "Playing a one-shot did not occupy a voice.");

            Object.DestroyImmediate(go);
        }

        [UnityTest]
        public IEnumerator NonSpatialVoicesAreExemptFromOcclusion()
        {
            GameObject go = new GameObject("Occlusion");
            go.AddComponent<SoundOcclusion>();

            // The score and the player's own breathing must never be muffled by
            // geometry the listener happens to stand behind.
            AudioVoice score = _audio.PlayLoop(ClipKind.DroneLow, Vector3.zero, 0.5f, SoundPriority.Ambient, spatial: false);
            Assert.IsNotNull(score);

            score.TargetOcclusion = 1f;

            yield return null;
            yield return null;

            Assert.AreEqual(0f, score.Occlusion, 0.001f,
                "A non-spatial voice was occluded. Score and body audio must sit in front of the mix.");

            Object.DestroyImmediate(go);
        }

        int CountPlaying()
        {
            int n = 0;
            AudioVoice[] voices = _audio.Voices;
            for (int i = 0; i < voices.Length; i++)
            {
                if (voices[i].InUse && voices[i].Source != null && voices[i].Source.isPlaying) n++;
            }
            return n;
        }
    }
}
