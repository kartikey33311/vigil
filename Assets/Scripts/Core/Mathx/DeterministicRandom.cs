// -----------------------------------------------------------------------------
// Vigil — deterministic PRNG for simulation code.
//
// UnityEngine.Random is a global, shared, non-reproducible generator. Using it
// inside AI means two clients (or a client and the server) can diverge, and it
// makes bug reports unreproducible: "the monster went left that one time".
//
// Every simulation subsystem takes its own DeterministicRandom seeded from
// (sessionSeed, entityId, purpose) so streams are independent and replayable.
// xoshiro128** — fast, good statistical quality, 16 bytes of state, no allocation.
// -----------------------------------------------------------------------------

using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace Vigil.Core.Mathx
{
    public struct DeterministicRandom
    {
        uint _s0, _s1, _s2, _s3;

        public DeterministicRandom(uint seed)
        {
            // SplitMix32 expansion so that adjacent seeds produce well-separated streams.
            uint z = seed == 0u ? 0x9E3779B9u : seed;
            _s0 = Mix(ref z);
            _s1 = Mix(ref z);
            _s2 = Mix(ref z);
            _s3 = Mix(ref z);
        }

        /// <summary>
        /// Derives an independent stream from a session seed, an entity id and a
        /// purpose tag. Two subsystems on the same entity get different streams,
        /// so adding a new random call to one does not perturb the other.
        /// </summary>
        public static DeterministicRandom ForEntity(uint sessionSeed, ulong entityId, uint purpose)
        {
            uint a = sessionSeed * 0x85EBCA6Bu;
            uint b = (uint)(entityId ^ (entityId >> 32)) * 0xC2B2AE35u;
            uint c = purpose * 0x27D4EB2Fu;
            return new DeterministicRandom(a ^ b ^ c);
        }

        static uint Mix(ref uint z)
        {
            z += 0x9E3779B9u;
            uint x = z;
            x = (x ^ (x >> 16)) * 0x85EBCA6Bu;
            x = (x ^ (x >> 13)) * 0xC2B2AE35u;
            return x ^ (x >> 16);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Rotl(uint x, int k) => (x << k) | (x >> (32 - k));

        /// <summary>Uniform uint over the full range.</summary>
        public uint NextUInt()
        {
            uint result = Rotl(_s1 * 5u, 7) * 9u;
            uint t = _s1 << 9;

            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = Rotl(_s3, 11);

            return result;
        }

        /// <summary>Uniform float in [0,1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float NextFloat() => (NextUInt() >> 8) * (1f / 16777216f);

        /// <summary>Uniform float in [min,max).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float NextFloat(float min, float max) => min + NextFloat() * (max - min);

        /// <summary>Uniform int in [min,max). Returns min when the range is empty.</summary>
        public int NextInt(int min, int max)
        {
            if (max <= min) return min;
            uint range = (uint)(max - min);
            return min + (int)(NextUInt() % range);
        }

        /// <summary>True with the given probability (0..1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Chance(float probability) => NextFloat() < probability;

        /// <summary>Uniform point on the unit circle, in the XZ plane.</summary>
        public float3 NextDirectionXZ()
        {
            float a = NextFloat() * math.PI * 2f;
            math.sincos(a, out float s, out float c);
            return new float3(c, 0f, s);
        }

        /// <summary>Uniform point inside a disc of the given radius, in the XZ plane.</summary>
        public float3 NextPointInDiscXZ(float radius)
        {
            // sqrt keeps the distribution uniform by area rather than clustering at the centre.
            float r = math.sqrt(NextFloat()) * radius;
            return NextDirectionXZ() * r;
        }

        /// <summary>Fisher-Yates shuffle. Deterministic given the stream state.</summary>
        public void Shuffle<T>(T[] items, int count = -1)
        {
            if (items == null) return;
            int n = count < 0 || count > items.Length ? items.Length : count;
            for (int i = n - 1; i > 0; i--)
            {
                int j = NextInt(0, i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
        }

        /// <summary>Opaque state capture, for save games and network sync.</summary>
        public uint4 GetState() => new uint4(_s0, _s1, _s2, _s3);

        public void SetState(uint4 state)
        {
            _s0 = state.x; _s1 = state.y; _s2 = state.z; _s3 = state.w;
        }
    }
}
