// -----------------------------------------------------------------------------
// Vigil — agent blackboard.
//
// Shared scratch memory between sensors, states, steering and the director.
// States stay stateless-ish and communicate through here, which is what makes
// them recombinable across different NPC archetypes.
//
// Deliberately NOT Dictionary<string, object>: that boxes every float and int
// written, so a 40-agent scene writing a dozen values each per tick would churn
// hundreds of allocations a second and produce visible GC hitches. Instead there
// is one strongly-typed store per primitive we actually use, keyed by a
// pre-hashed int. Reads and writes are allocation-free.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using Unity.Mathematics;

namespace Vigil.Core.Contracts
{
    /// <summary>
    /// Compile-time-ish key. Construct these once in a static class, never per tick
    /// (the constructor hashes the name string).
    /// </summary>
    public readonly struct BlackboardKey<T>
    {
        public readonly int Hash;
        public readonly string Name;

        public BlackboardKey(string name)
        {
            Name = name;
            Hash = name != null ? name.GetHashCode() : 0;
        }

        public override string ToString() => Name;
    }

    public sealed class Blackboard
    {
        readonly Dictionary<int, float> _floats = new Dictionary<int, float>(16);
        readonly Dictionary<int, int> _ints = new Dictionary<int, int>(16);
        readonly Dictionary<int, bool> _bools = new Dictionary<int, bool>(16);
        readonly Dictionary<int, ulong> _ulongs = new Dictionary<int, ulong>(8);
        readonly Dictionary<int, float3> _vectors = new Dictionary<int, float3>(16);
        readonly Dictionary<int, object> _objects = new Dictionary<int, object>(8);

        /// <summary>Tick each key was last written on — lets states age out stale data.</summary>
        readonly Dictionary<int, int> _writeTicks = new Dictionary<int, int>(32);

        public void Set(BlackboardKey<float> key, float value, int tick = 0)
        {
            _floats[key.Hash] = value;
            _writeTicks[key.Hash] = tick;
        }

        public void Set(BlackboardKey<int> key, int value, int tick = 0)
        {
            _ints[key.Hash] = value;
            _writeTicks[key.Hash] = tick;
        }

        public void Set(BlackboardKey<bool> key, bool value, int tick = 0)
        {
            _bools[key.Hash] = value;
            _writeTicks[key.Hash] = tick;
        }

        public void Set(BlackboardKey<ulong> key, ulong value, int tick = 0)
        {
            _ulongs[key.Hash] = value;
            _writeTicks[key.Hash] = tick;
        }

        public void Set(BlackboardKey<float3> key, float3 value, int tick = 0)
        {
            _vectors[key.Hash] = value;
            _writeTicks[key.Hash] = tick;
        }

        public void SetObject<T>(BlackboardKey<T> key, T value, int tick = 0) where T : class
        {
            _objects[key.Hash] = value;
            _writeTicks[key.Hash] = tick;
        }

        public float Get(BlackboardKey<float> key, float fallback = 0f)
            => _floats.TryGetValue(key.Hash, out float v) ? v : fallback;

        public int Get(BlackboardKey<int> key, int fallback = 0)
            => _ints.TryGetValue(key.Hash, out int v) ? v : fallback;

        public bool Get(BlackboardKey<bool> key, bool fallback = false)
            => _bools.TryGetValue(key.Hash, out bool v) ? v : fallback;

        public ulong Get(BlackboardKey<ulong> key, ulong fallback = 0UL)
            => _ulongs.TryGetValue(key.Hash, out ulong v) ? v : fallback;

        public float3 Get(BlackboardKey<float3> key, float3 fallback = default)
            => _vectors.TryGetValue(key.Hash, out float3 v) ? v : fallback;

        public T GetObject<T>(BlackboardKey<T> key) where T : class
            => _objects.TryGetValue(key.Hash, out object v) ? v as T : null;

        public bool TryGet(BlackboardKey<float> key, out float value) => _floats.TryGetValue(key.Hash, out value);
        public bool TryGet(BlackboardKey<int> key, out int value) => _ints.TryGetValue(key.Hash, out value);
        public bool TryGet(BlackboardKey<bool> key, out bool value) => _bools.TryGetValue(key.Hash, out value);
        public bool TryGet(BlackboardKey<float3> key, out float3 value) => _vectors.TryGetValue(key.Hash, out value);
        public bool TryGet(BlackboardKey<ulong> key, out ulong value) => _ulongs.TryGetValue(key.Hash, out value);

        /// <summary>Ticks since a key was written, or int.MaxValue if never.</summary>
        public int AgeOf<T>(BlackboardKey<T> key, int currentTick)
            => _writeTicks.TryGetValue(key.Hash, out int t) ? unchecked(currentTick - t) : int.MaxValue;

        public bool Has<T>(BlackboardKey<T> key) => _writeTicks.ContainsKey(key.Hash);

        public void Remove<T>(BlackboardKey<T> key)
        {
            int h = key.Hash;
            _floats.Remove(h); _ints.Remove(h); _bools.Remove(h);
            _ulongs.Remove(h); _vectors.Remove(h); _objects.Remove(h);
            _writeTicks.Remove(h);
        }

        public void Clear()
        {
            _floats.Clear(); _ints.Clear(); _bools.Clear();
            _ulongs.Clear(); _vectors.Clear(); _objects.Clear();
            _writeTicks.Clear();
        }
    }

    /// <summary>
    /// Canonical blackboard keys. Every subsystem must use these rather than
    /// declaring its own — a typo in a key name is otherwise a silent no-op that
    /// is extremely hard to trace.
    /// </summary>
    public static class BBKeys
    {
        // --- perception ---
        public static readonly BlackboardKey<ulong>  TargetId          = new BlackboardKey<ulong>("target.id");
        public static readonly BlackboardKey<float3> TargetPosition    = new BlackboardKey<float3>("target.position");
        public static readonly BlackboardKey<float3> LastKnownPosition = new BlackboardKey<float3>("target.lastKnown");
        public static readonly BlackboardKey<float>  Awareness         = new BlackboardKey<float>("perception.awareness");
        public static readonly BlackboardKey<int>    AwarenessLevel    = new BlackboardKey<int>("perception.level");
        public static readonly BlackboardKey<bool>   HasLineOfSight    = new BlackboardKey<bool>("perception.los");
        public static readonly BlackboardKey<int>    LastSeenTick      = new BlackboardKey<int>("perception.lastSeenTick");
        public static readonly BlackboardKey<float3> InvestigatePoint  = new BlackboardKey<float3>("perception.investigate");

        // --- navigation ---
        public static readonly BlackboardKey<float3> MoveGoal          = new BlackboardKey<float3>("nav.goal");
        public static readonly BlackboardKey<bool>   HasPath           = new BlackboardKey<bool>("nav.hasPath");
        public static readonly BlackboardKey<float>  RemainingDistance = new BlackboardKey<float>("nav.remaining");
        public static readonly BlackboardKey<int>    CurrentRegion     = new BlackboardKey<int>("nav.region");
        public static readonly BlackboardKey<int>    TargetRegion      = new BlackboardKey<int>("nav.targetRegion");
        public static readonly BlackboardKey<bool>   PathFailed        = new BlackboardKey<bool>("nav.pathFailed");

        // --- combat / state ---
        public static readonly BlackboardKey<bool>   IsStunned         = new BlackboardKey<bool>("agent.stunned");
        public static readonly BlackboardKey<float>  Aggression        = new BlackboardKey<float>("agent.aggression");
        public static readonly BlackboardKey<float>  MoveSpeed         = new BlackboardKey<float>("agent.moveSpeed");
        public static readonly BlackboardKey<int>    SearchAttempts    = new BlackboardKey<int>("agent.searchAttempts");
        public static readonly BlackboardKey<float>  AttackCooldown    = new BlackboardKey<float>("agent.attackCooldown");

        // --- director ---
        public static readonly BlackboardKey<int>    DreadPhase        = new BlackboardKey<int>("director.phase");
        public static readonly BlackboardKey<float>  DirectorPressure  = new BlackboardKey<float>("director.pressure");
        public static readonly BlackboardKey<bool>   HuntAuthorised    = new BlackboardKey<bool>("director.huntAuthorised");
    }
}
