// -----------------------------------------------------------------------------
// Vigil — procedural audio synthesis.
//
// This project ships no binary audio assets, for the same reason it ships no
// hand-authored scenes: the repository stays small, diffable and verifiable in
// CI, and nothing can silently rot. Every sound below is synthesised into an
// AudioClip at boot from a deterministic seed.
//
// It is also, genuinely, the right technique for several of these. Footsteps are
// filtered noise bursts; a sampled footstep played 400 times a session is the
// single most recognisable "video game" tell there is, whereas a synthesised one
// can vary its spectrum, envelope and duration per step for free.
//
// Replace the footsteps and stingers with recorded material before ship. Keep the
// drone bed and the heartbeat — they are parametric on purpose, because they are
// driven continuously by composure and dread rather than triggered.
// -----------------------------------------------------------------------------

using UnityEngine;
using Vigil.Core.Mathx;
using Vigil.Data;

namespace Vigil.Audio
{
    /// <summary>Categories of synthesised clip.</summary>
    public enum ClipKind : byte
    {
        FootstepConcrete = 0,
        FootstepMetal = 1,
        FootstepWood = 2,
        DoorMove = 3,
        Machinery = 4,
        Impact = 5,
        Breath = 6,
        Heartbeat = 7,
        StingerReveal = 8,
        StingerChase = 9,
        StingerFalse = 10,
        Scream = 11,
        DroneLow = 12,
        DroneMid = 13,
        DroneHigh = 14
    }

    public static class ProceduralAudio
    {
        public const int SampleRate = 44100;

        /// <summary>
        /// Builds every clip once. Called by the audio service at startup; the
        /// whole set costs a few hundred kilobytes and a few milliseconds.
        /// </summary>
        public static AudioClip[] BuildLibrary(uint seed = 0x51A17u)
        {
            AudioClip[] clips = new AudioClip[15];

            clips[(int)ClipKind.FootstepConcrete] = Footstep("step_concrete", seed + 1, 900f, 0.085f, 0.55f);
            clips[(int)ClipKind.FootstepMetal] = Footstep("step_metal", seed + 2, 2400f, 0.13f, 0.75f);
            clips[(int)ClipKind.FootstepWood] = Footstep("step_wood", seed + 3, 1300f, 0.10f, 0.6f);

            clips[(int)ClipKind.DoorMove] = DoorCreak("door_move", seed + 4);
            clips[(int)ClipKind.Machinery] = MachineryHum("machinery", seed + 5);
            clips[(int)ClipKind.Impact] = Impact("impact", seed + 6);

            clips[(int)ClipKind.Breath] = Breath("breath", seed + 7);
            clips[(int)ClipKind.Heartbeat] = Heartbeat("heartbeat");

            clips[(int)ClipKind.StingerReveal] = Stinger("sting_reveal", seed + 8, 180f, 55f, 1.6f);
            clips[(int)ClipKind.StingerChase] = Stinger("sting_chase", seed + 9, 320f, 90f, 1.1f);
            clips[(int)ClipKind.StingerFalse] = Stinger("sting_false", seed + 10, 640f, 240f, 0.5f);
            clips[(int)ClipKind.Scream] = Scream("scream", seed + 11);

            // The bed layers are long and looping; the director crossfades them.
            clips[(int)ClipKind.DroneLow] = Drone("drone_low", seed + 12, 46f, 0.5f);
            clips[(int)ClipKind.DroneMid] = Drone("drone_mid", seed + 13, 92f, 0.35f);
            clips[(int)ClipKind.DroneHigh] = Drone("drone_high", seed + 14, 233f, 0.18f);

            return clips;
        }

        // ---------------------------------------------------------------- helpers

        static AudioClip Make(string name, float[] data, bool loop = false)
        {
            AudioClip clip = AudioClip.Create(name, data.Length, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        static float[] Buffer(float seconds) => new float[Mathf.Max(1, Mathf.RoundToInt(SampleRate * seconds))];

        /// <summary>One-pole low-pass. Cheap, and enough to shape noise into a material.</summary>
        static void LowPass(float[] data, float cutoffHz)
        {
            float rc = 1f / (2f * Mathf.PI * Mathf.Max(1f, cutoffHz));
            float dt = 1f / SampleRate;
            float alpha = dt / (rc + dt);

            float last = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                last += alpha * (data[i] - last);
                data[i] = last;
            }
        }

        static void Normalise(float[] data, float peak)
        {
            float max = 0f;
            for (int i = 0; i < data.Length; i++) max = Mathf.Max(max, Mathf.Abs(data[i]));
            if (max < 1e-5f) return;

            float scale = peak / max;
            for (int i = 0; i < data.Length; i++) data[i] *= scale;
        }

        /// <summary>Applies a short fade at both ends so a clip never starts or stops on a click.</summary>
        static void FadeEdges(float[] data, int fadeSamples = 256)
        {
            int n = Mathf.Min(fadeSamples, data.Length / 2);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                data[i] *= t;
                data[data.Length - 1 - i] *= t;
            }
        }

        // -------------------------------------------------------------- generators

        /// <summary>
        /// Filtered noise burst with a fast exponential decay. Cutoff selects the
        /// material: low reads as concrete, high and ringing reads as metal grating.
        /// </summary>
        static AudioClip Footstep(string name, uint seed, float cutoff, float length, float peak)
        {
            DeterministicRandom rng = new DeterministicRandom(seed);
            float[] data = Buffer(length);

            for (int i = 0; i < data.Length; i++)
            {
                float t = i / (float)data.Length;
                float envelope = Mathf.Exp(-9f * t);
                data[i] = (rng.NextFloat() * 2f - 1f) * envelope;
            }

            LowPass(data, cutoff);

            // A little ring on metal, none on concrete.
            if (cutoff > 2000f)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    float t = i / (float)SampleRate;
                    data[i] += Mathf.Sin(2f * Mathf.PI * 3100f * t) * Mathf.Exp(-26f * t) * 0.25f;
                }
            }

            Normalise(data, peak);
            FadeEdges(data, 64);
            return Make(name, data);
        }

        static AudioClip DoorCreak(string name, uint seed)
        {
            DeterministicRandom rng = new DeterministicRandom(seed);
            float[] data = Buffer(0.9f);

            // Frequency-swept noise: a hinge is essentially a slow, irregular squeal.
            float phase = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float t = i / (float)data.Length;
                float freq = Mathf.Lerp(220f, 460f, t) + Mathf.Sin(t * 40f) * 25f;
                phase += 2f * Mathf.PI * freq / SampleRate;

                float envelope = Mathf.Sin(Mathf.PI * t);
                float grain = 0.6f + 0.4f * rng.NextFloat();

                data[i] = Mathf.Sin(phase) * envelope * grain * 0.5f;
            }

            LowPass(data, 3200f);
            Normalise(data, 0.5f);
            FadeEdges(data);
            return Make(name, data);
        }

        static AudioClip MachineryHum(string name, uint seed)
        {
            DeterministicRandom rng = new DeterministicRandom(seed);
            float[] data = Buffer(2f);

            for (int i = 0; i < data.Length; i++)
            {
                float t = i / (float)SampleRate;

                // Mains-style hum plus harmonics and a little mechanical noise.
                float v = Mathf.Sin(2f * Mathf.PI * 50f * t) * 0.6f;
                v += Mathf.Sin(2f * Mathf.PI * 100f * t) * 0.25f;
                v += Mathf.Sin(2f * Mathf.PI * 150f * t) * 0.12f;
                v += (rng.NextFloat() * 2f - 1f) * 0.08f;

                data[i] = v;
            }

            LowPass(data, 1800f);
            Normalise(data, 0.55f);
            FadeEdges(data, 2048);
            return Make(name, data, loop: true);
        }

        static AudioClip Impact(string name, uint seed)
        {
            DeterministicRandom rng = new DeterministicRandom(seed);
            float[] data = Buffer(0.35f);

            for (int i = 0; i < data.Length; i++)
            {
                float t = i / (float)data.Length;
                float envelope = Mathf.Exp(-14f * t);

                float body = Mathf.Sin(2f * Mathf.PI * 70f * (i / (float)SampleRate));
                float crack = (rng.NextFloat() * 2f - 1f);

                data[i] = (body * 0.7f + crack * 0.5f) * envelope;
            }

            LowPass(data, 1400f);
            Normalise(data, 0.85f);
            FadeEdges(data, 64);
            return Make(name, data);
        }

        static AudioClip Breath(string name, uint seed)
        {
            DeterministicRandom rng = new DeterministicRandom(seed);
            float[] data = Buffer(1.5f);

            // Inhale then exhale: two noise swells with a brief gap.
            for (int i = 0; i < data.Length; i++)
            {
                float t = i / (float)data.Length;

                float inhale = Mathf.Exp(-Mathf.Pow((t - 0.22f) / 0.14f, 2f));
                float exhale = Mathf.Exp(-Mathf.Pow((t - 0.66f) / 0.20f, 2f)) * 0.85f;

                data[i] = (rng.NextFloat() * 2f - 1f) * (inhale + exhale);
            }

            LowPass(data, 1100f);
            Normalise(data, 0.42f);
            FadeEdges(data, 512);
            return Make(name, data);
        }

        static AudioClip Heartbeat(string name)
        {
            float[] data = Buffer(1.0f);

            // Lub-dub: two low thumps, the second softer and slightly higher.
            AddThump(data, 0.00f, 62f, 1.00f);
            AddThump(data, 0.22f, 74f, 0.62f);

            Normalise(data, 0.8f);
            FadeEdges(data, 256);
            return Make(name, data);
        }

        static void AddThump(float[] data, float startSeconds, float freq, float amplitude)
        {
            int start = Mathf.RoundToInt(startSeconds * SampleRate);
            int length = Mathf.RoundToInt(0.16f * SampleRate);

            for (int i = 0; i < length && start + i < data.Length; i++)
            {
                float t = i / (float)SampleRate;
                float envelope = Mathf.Exp(-22f * t);
                data[start + i] += Mathf.Sin(2f * Mathf.PI * freq * t) * envelope * amplitude;
            }
        }

        /// <summary>
        /// Downward-swept tone cluster. Descending pitch is the classic dread cue
        /// because it reads as something arriving rather than departing.
        /// </summary>
        static AudioClip Stinger(string name, uint seed, float startHz, float endHz, float length)
        {
            DeterministicRandom rng = new DeterministicRandom(seed);
            float[] data = Buffer(length);

            float p1 = 0f, p2 = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float t = i / (float)data.Length;
                float freq = Mathf.Lerp(startHz, endHz, t * t);

                p1 += 2f * Mathf.PI * freq / SampleRate;
                p2 += 2f * Mathf.PI * freq * 1.5f / SampleRate;   // detuned fifth

                float envelope = Mathf.Exp(-2.2f * t);
                float noise = (rng.NextFloat() * 2f - 1f) * 0.12f * Mathf.Exp(-8f * t);

                data[i] = (Mathf.Sin(p1) * 0.7f + Mathf.Sin(p2) * 0.35f + noise) * envelope;
            }

            Normalise(data, 0.9f);
            FadeEdges(data, 256);
            return Make(name, data);
        }

        static AudioClip Scream(string name, uint seed)
        {
            DeterministicRandom rng = new DeterministicRandom(seed);
            float[] data = Buffer(1.1f);

            float phase = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float t = i / (float)data.Length;

                // Wide, unstable vibrato — the instability is what makes it read as
                // a creature rather than an instrument.
                float freq = Mathf.Lerp(420f, 260f, t) * (1f + Mathf.Sin(t * 90f) * 0.09f);
                phase += 2f * Mathf.PI * freq / SampleRate;

                float envelope = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t * 1.1f));
                float grit = (rng.NextFloat() * 2f - 1f) * 0.35f;

                data[i] = (Mathf.Sin(phase) + Mathf.Sin(phase * 2.01f) * 0.4f + grit) * envelope;
            }

            LowPass(data, 4200f);
            Normalise(data, 0.95f);
            FadeEdges(data, 256);
            return Make(name, data);
        }

        /// <summary>
        /// Slow, detuned sine bed. Detuning produces a beat frequency that never
        /// quite settles, which is what makes a drone feel uneasy rather than calm.
        /// </summary>
        static AudioClip Drone(string name, uint seed, float baseHz, float peak)
        {
            DeterministicRandom rng = new DeterministicRandom(seed);

            // 4 seconds, chosen so the detune beat completes an integer number of
            // cycles and the loop point is inaudible.
            float[] data = Buffer(4f);

            float detune = 0.35f;
            for (int i = 0; i < data.Length; i++)
            {
                float t = i / (float)SampleRate;

                float v = Mathf.Sin(2f * Mathf.PI * baseHz * t);
                v += Mathf.Sin(2f * Mathf.PI * (baseHz + detune) * t) * 0.9f;
                v += Mathf.Sin(2f * Mathf.PI * baseHz * 2f * t) * 0.18f;
                v += (rng.NextFloat() * 2f - 1f) * 0.03f;

                data[i] = v * 0.25f;
            }

            LowPass(data, baseHz * 8f);
            Normalise(data, peak);

            // No edge fade: this loops, and a fade would produce an audible dip.
            return Make(name, data, loop: true);
        }
    }
}
