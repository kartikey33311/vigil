// -----------------------------------------------------------------------------
// Vigil — interest management.
//
// THIS IS AN ANTI-CHEAT BOUNDARY FIRST, AND A BANDWIDTH OPTIMISATION SECOND.
//
// The entire game rests on players not knowing where the antagonist is. A client
// that receives the monster's transform every tick can be trivially modified to
// render it through walls — at which point there is no game left to protect. No
// amount of client-side "don't draw it" logic helps; the only defence is to not
// send the data at all.
//
// So the server decides, per entity per client, whether that entity is replicated
// at all, using NetworkObject.NetworkShow / NetworkHide.
//
// Hysteresis on the boundary is not polish: without it an entity hovering at the
// interest radius toggles every evaluation, which is both a bandwidth spike and a
// visible spawn/despawn pop on the client.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Simulation;
using Vigil.Data;

namespace Vigil.Net.Interest
{
    /// <summary>How aggressively an entity is hidden.</summary>
    public enum InterestProfile : byte
    {
        /// <summary>Always replicated. Doors, objectives, anything whose state must stay consistent.</summary>
        Always = 0,

        /// <summary>Standard distance-based culling.</summary>
        Proximity = 1,

        /// <summary>
        /// The antagonist. Culled aggressively, and additionally requires region
        /// adjacency — a client several sealed rooms away is told nothing at all.
        /// </summary>
        Concealed = 2
    }

    public sealed class InterestManager : IInterestManager, ITickable
    {
        struct Entry
        {
            public NetworkObject Object;
            public InterestProfile Profile;
            public bool Alive;
        }

        struct Override
        {
            public ulong EntityId;
            public ulong ClientId;
            public double ExpiresAt;
        }

        readonly NetworkTuningConfig _config;
        readonly IRegionGraph _regions;

        readonly List<Entry> _entries = new List<Entry>(64);
        readonly Dictionary<ulong, int> _byEntityId = new Dictionary<ulong, int>(64);

        // (entityId, clientId) -> currently visible. Avoids redundant Show/Hide calls,
        // which NGO logs warnings for and which cost a message each.
        readonly HashSet<long> _visible = new HashSet<long>();

        readonly List<Override> _overrides = new List<Override>(8);
        readonly int[] _neighbourBuffer = new int[16];

        double _nextEvaluationAt;
        double _now;

        public InterestManager(NetworkTuningConfig config, IRegionGraph regions)
        {
            _config = config;
            _regions = regions;
        }

        public void Register(NetworkObject obj, InterestProfile profile)
        {
            if (obj == null) return;

            ulong id = obj.NetworkObjectId;
            if (_byEntityId.ContainsKey(id)) return;

            _byEntityId[id] = _entries.Count;
            _entries.Add(new Entry { Object = obj, Profile = profile, Alive = true });
        }

        public void Unregister(NetworkObject obj)
        {
            if (obj == null) return;
            Unregister(obj.NetworkObjectId);
        }

        public void Unregister(ulong entityId)
        {
            if (!_byEntityId.TryGetValue(entityId, out int i)) return;

            Entry e = _entries[i];
            e.Alive = false;
            e.Object = null;
            _entries[i] = e;

            _byEntityId.Remove(entityId);

            // Drop cached visibility so a recycled id cannot inherit stale state.
            _visible.RemoveWhere(key => (ulong)(key >> 16) == entityId);
        }

        // ------------------------------------------------------------------- tick

        public void OnSimTick(in SimTime time)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || !nm.IsListening) return;

            _now = time.Elapsed;

            float interval = _config != null ? _config.InterestUpdateInterval : 0.4f;
            if (_now < _nextEvaluationAt) return;
            _nextEvaluationAt = _now + interval;

            Evaluate(in time);
        }

        public void Evaluate(in SimTime time)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            ExpireOverrides();

            float radius = _config != null ? _config.InterestRadius : 55f;
            float hysteresis = _config != null ? _config.InterestHysteresis : 9f;

            IReadOnlyList<ulong> clients = nm.ConnectedClientsIds;

            for (int e = 0; e < _entries.Count; e++)
            {
                Entry entry = _entries[e];
                if (!entry.Alive || entry.Object == null || !entry.Object.IsSpawned) continue;
                if (entry.Profile == InterestProfile.Always) continue;

                float3 entityPos = entry.Object.transform.position;
                int entityRegion = _regions != null ? _regions.GetRegionAt(entityPos) : 0;

                for (int c = 0; c < clients.Count; c++)
                {
                    ulong clientId = clients[c];

                    // Never hide from the host's own client — it shares the server's
                    // process and already has the data.
                    if (nm.IsHost && clientId == nm.LocalClientId) continue;

                    // The owner always sees its own object.
                    if (entry.Object.OwnerClientId == clientId) continue;

                    if (!TryGetClientPosition(nm, clientId, out float3 clientPos)) continue;

                    bool currentlyVisible = _visible.Contains(Key(entry.Object.NetworkObjectId, clientId));

                    bool shouldBeVisible = HasOverride(entry.Object.NetworkObjectId, clientId)
                        || ShouldReplicate(in entry, entityPos, entityRegion, clientPos, radius, hysteresis, currentlyVisible);

                    if (shouldBeVisible == currentlyVisible) continue;

                    ApplyVisibility(entry.Object, clientId, shouldBeVisible);
                }
            }
        }

        bool ShouldReplicate(
            in Entry entry, float3 entityPos, int entityRegion,
            float3 clientPos, float radius, float hysteresis, bool currentlyVisible)
        {
            // Asymmetric threshold: it must come CLOSER than `radius` to appear, and
            // travel further than `radius + hysteresis` to disappear.
            float threshold = currentlyVisible ? radius + hysteresis : radius;

            float distance = math.distance(entityPos, clientPos);
            if (distance > threshold) return false;

            if (entry.Profile != InterestProfile.Concealed) return true;

            // The antagonist additionally requires region adjacency. Being 40m away
            // through three sealed rooms is not a legitimate reason to receive its
            // position, even though the raw distance passes.
            if (_regions == null || _regions.RegionCount == 0) return true;

            int clientRegion = _regions.GetRegionAt(clientPos);
            if (clientRegion == entityRegion) return true;

            int count = _regions.GetNeighbours(clientRegion, _neighbourBuffer);
            for (int i = 0; i < count; i++)
            {
                if (_neighbourBuffer[i] == entityRegion) return true;
            }

            return false;
        }

        void ApplyVisibility(NetworkObject obj, ulong clientId, bool visible)
        {
            long key = Key(obj.NetworkObjectId, clientId);

            try
            {
                if (visible)
                {
                    if (!obj.IsNetworkVisibleTo(clientId)) obj.NetworkShow(clientId);
                    _visible.Add(key);
                }
                else
                {
                    if (obj.IsNetworkVisibleTo(clientId)) obj.NetworkHide(clientId);
                    _visible.Remove(key);
                }
            }
            catch (System.Exception ex)
            {
                // NGO throws if the object despawns between our check and the call.
                // That is a benign race, not a bug worth taking the tick down for.
                VLog.Warn(LogCat.Net, $"Visibility change failed for {obj.NetworkObjectId}->{clientId}: {ex.Message}");
                _visible.Remove(key);
            }
        }

        static bool TryGetClientPosition(NetworkManager nm, ulong clientId, out float3 position)
        {
            position = default;

            if (!nm.ConnectedClients.TryGetValue(clientId, out NetworkClient client)) return false;
            if (client.PlayerObject == null) return false;

            position = client.PlayerObject.transform.position;
            return true;
        }

        static long Key(ulong entityId, ulong clientId) => (long)((entityId << 16) | (clientId & 0xFFFF));

        // -------------------------------------------------------------- overrides

        public void ForceVisible(ulong entityId, ulong clientId, float seconds)
        {
            _overrides.Add(new Override
            {
                EntityId = entityId,
                ClientId = clientId,
                ExpiresAt = _now + seconds
            });

            // Apply immediately — a scripted reveal that waits for the next slow
            // evaluation tick lands up to 400ms late, which is very visible.
            if (_byEntityId.TryGetValue(entityId, out int i) && _entries[i].Object != null)
            {
                ApplyVisibility(_entries[i].Object, clientId, true);
            }
        }

        bool HasOverride(ulong entityId, ulong clientId)
        {
            for (int i = 0; i < _overrides.Count; i++)
            {
                if (_overrides[i].EntityId == entityId && _overrides[i].ClientId == clientId) return true;
            }
            return false;
        }

        void ExpireOverrides()
        {
            for (int i = _overrides.Count - 1; i >= 0; i--)
            {
                if (_overrides[i].ExpiresAt <= _now) _overrides.RemoveAt(i);
            }
        }

        // ---------------------------------------------------------------- queries

        public bool IsVisibleTo(ulong entityId, ulong clientId) => _visible.Contains(Key(entityId, clientId));

        public int GetVisibleCount(ulong clientId)
        {
            int n = 0;
            foreach (long key in _visible)
            {
                if ((ulong)(key & 0xFFFF) == (clientId & 0xFFFF)) n++;
            }
            return n;
        }

        public void Clear()
        {
            _entries.Clear();
            _byEntityId.Clear();
            _visible.Clear();
            _overrides.Clear();
        }
    }
}
