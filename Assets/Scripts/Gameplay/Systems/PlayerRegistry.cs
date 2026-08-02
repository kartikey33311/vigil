// -----------------------------------------------------------------------------
// Vigil — player roster and the Director's data feed.
//
// The Director must not know what a PlayerCharacter IS — that would put a
// gameplay dependency inside the AI assembly and make the pacing system
// untestable without a scene. So the roster lives here, in Gameplay, and satisfies
// the IDirectorPlayerSource contract the Director declares.
//
// Isolation distance is computed here rather than per-player because it is
// inherently an O(n^2) question over the roster, and doing it once for four
// players is trivially cheap while doing it per-player-per-tick is not.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using Unity.Mathematics;
using Vigil.AI.Agents;
using Vigil.AI.Director;
using Vigil.Core.Contracts;
using Vigil.Core.Simulation;
using Vigil.Gameplay.Player;

namespace Vigil.Gameplay.Systems
{
    public sealed class PlayerRegistry : IDirectorPlayerSource, ITickable
    {
        readonly List<PlayerCharacter> _players = new List<PlayerCharacter>(8);
        readonly Dictionary<ulong, float> _lastThreatAt = new Dictionary<ulong, float>(8);

        IDarknessSampler _darkness;
        float3 _antagonistPosition;
        bool _hasAntagonist;
        double _now;

        public IReadOnlyList<PlayerCharacter> Players => _players;

        public void SetDarknessSampler(IDarknessSampler sampler) => _darkness = sampler;

        public void Register(PlayerCharacter player)
        {
            if (player == null || _players.Contains(player)) return;
            _players.Add(player);
        }

        public void Unregister(PlayerCharacter player)
        {
            if (player == null) return;
            _players.Remove(player);
            _lastThreatAt.Remove(player.EntityId);
        }

        /// <summary>Reported by the antagonist each tick so proximity stress can be computed.</summary>
        public void ReportAntagonistPosition(float3 position)
        {
            _antagonistPosition = position;
            _hasAntagonist = true;
        }

        public void ClearAntagonist() => _hasAntagonist = false;

        /// <summary>Called when a player enters a chase, so time-since-threat is meaningful.</summary>
        public void MarkThreatened(ulong playerId) => _lastThreatAt[playerId] = (float)_now;

        public void OnSimTick(in SimTime time)
        {
            _now = time.Elapsed;
            RecomputeIsolation();
        }

        void RecomputeIsolation()
        {
            for (int i = 0; i < _players.Count; i++)
            {
                PlayerCharacter a = _players[i];
                if (a == null) continue;

                float nearest = float.MaxValue;

                for (int j = 0; j < _players.Count; j++)
                {
                    if (i == j) continue;

                    PlayerCharacter b = _players[j];
                    if (b == null || !b.IsAlive || b.IsDowned) continue;

                    float d = math.distance(a.PerceivablePosition, b.PerceivablePosition);
                    if (d < nearest) nearest = d;
                }

                // A lone survivor is maximally isolated by definition, not
                // undefined — the Director must still be able to score them.
                a.IsolationDistance = nearest == float.MaxValue ? 999f : nearest;
            }
        }

        // ------------------------------------------------------- IDirectorPlayerSource

        public int GetPlayerSamples(PlayerStressSample[] buffer)
        {
            if (buffer == null) return 0;

            int count = 0;

            for (int i = 0; i < _players.Count && count < buffer.Length; i++)
            {
                PlayerCharacter p = _players[i];
                if (p == null) continue;

                float3 position = p.PerceivablePosition;

                float darkness = _darkness != null ? _darkness.DarknessAt(position) : 0.5f;

                float timeSinceThreat = _lastThreatAt.TryGetValue(p.EntityId, out float last)
                    ? (float)(_now - last)
                    : 999f;

                buffer[count++] = new PlayerStressSample
                {
                    PlayerId = p.EntityId,
                    Position = position,
                    Composure = p.Composure / 100f,
                    IsolationDistance = p.IsolationDistance,
                    TimeSinceLastThreat = timeSinceThreat,
                    Darkness = darkness,
                    IsAlive = p.IsAlive,
                    IsDowned = p.IsDowned,

                    // Stress is aggregated by the Director from the components above;
                    // supplying a pre-baked value here would double-count.
                    Stress = 0f
                };
            }

            return count;
        }

        public bool TryGetAntagonistPosition(out float3 position)
        {
            position = _antagonistPosition;
            return _hasAntagonist;
        }

        public void Clear()
        {
            _players.Clear();
            _lastThreatAt.Clear();
            _hasAntagonist = false;
        }
    }
}
