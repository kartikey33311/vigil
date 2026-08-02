// -----------------------------------------------------------------------------
// Vigil — stimulus routing.
//
// Sound, scent, damage and environmental cues are PUSHED. Sight is PULLED by each
// agent on its own tick budget. That split is a cost decision: pushed channels are
// sparse and event-driven, so when the level is quiet perception costs literally
// nothing, whereas vision must be evaluated against the agent's current head
// transform and therefore has to be polled.
//
// Receivers live in a uniform spatial hash so a broadcast costs O(receivers near
// the source) rather than O(all receivers). Re-bucketing happens only when a
// receiver crosses a cell boundary — re-hashing every mover every tick would cost
// more than the linear scan it replaced.
//
// The bus also owns the IPerceivable registry, because "everything that can be
// sensed" and "everything that senses" are two halves of the same problem and
// splitting them across two services just means two things to keep in sync.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using Unity.Mathematics;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Data;

namespace Vigil.AI.Perception
{
    public sealed class PerceptionBus : IPerceptionBus
    {
        struct Entry
        {
            public IStimulusReceiver Receiver;
            public int3 Cell;
            public bool Alive;
        }

        readonly float _cellSize;
        readonly float _invCellSize;

        readonly List<Entry> _entries = new List<Entry>(64);
        readonly Dictionary<IStimulusReceiver, int> _index = new Dictionary<IStimulusReceiver, int>(64);
        readonly Dictionary<int3, List<int>> _cells = new Dictionary<int3, List<int>>(256);

        // Reused across broadcasts so routing never allocates.
        readonly List<int> _scratch = new List<int>(64);

        readonly List<IPerceivable> _perceivables = new List<IPerceivable>(16);

        bool _compactPending;

        public int ReceiverCount => _index.Count;
        public int PerceivableCount => _perceivables.Count;

        /// <summary>Stimuli broadcast since construction. Telemetry.</summary>
        public int BroadcastCount { get; private set; }

        public PerceptionBus(PerceptionConfig config)
        {
            _cellSize = config != null ? math.max(1f, config.BusCellSize) : 12f;
            _invCellSize = 1f / _cellSize;
        }

        // ------------------------------------------------------------- receivers

        public void Register(IStimulusReceiver receiver)
        {
            if (receiver == null || _index.ContainsKey(receiver)) return;

            int slot = _entries.Count;
            int3 cell = CellOf(receiver.ListenerPosition);

            _entries.Add(new Entry { Receiver = receiver, Cell = cell, Alive = true });
            _index[receiver] = slot;
            AddToCell(cell, slot);
        }

        public void Unregister(IStimulusReceiver receiver)
        {
            if (receiver == null || !_index.TryGetValue(receiver, out int slot)) return;

            Entry e = _entries[slot];
            RemoveFromCell(e.Cell, slot);

            e.Alive = false;
            e.Receiver = null;
            _entries[slot] = e;

            _index.Remove(receiver);
            _compactPending = true;
        }

        /// <summary>
        /// Re-buckets receivers that have moved. Call once per tick from the owning
        /// system; it is a no-op for anything that stayed inside its cell.
        /// </summary>
        public void RefreshPositions()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                if (!e.Alive || e.Receiver == null) continue;

                int3 cell = CellOf(e.Receiver.ListenerPosition);
                if (cell.Equals(e.Cell)) continue;

                RemoveFromCell(e.Cell, i);
                AddToCell(cell, i);

                e.Cell = cell;
                _entries[i] = e;
            }

            if (_compactPending) Compact();
        }

        public void Broadcast(in Stimulus stimulus)
        {
            BroadcastCount++;

            _scratch.Clear();
            GatherNear(stimulus.Position, stimulus.Radius, _scratch);

            for (int i = 0; i < _scratch.Count; i++)
            {
                Entry e = _entries[_scratch[i]];
                if (!e.Alive || e.Receiver == null) continue;

                IStimulusReceiver r = e.Receiver;
                if (!r.IsListening) continue;

                // A receiver never hears itself — otherwise the monster's own
                // footsteps would keep it permanently alerted to itself.
                float3 listener = r.ListenerPosition;

                float reach = stimulus.Radius + r.ListenerRange;
                if (math.distancesq(listener, stimulus.Position) > reach * reach) continue;

                r.OnStimulus(in stimulus);
            }
        }

        void GatherNear(float3 position, float radius, List<int> results)
        {
            // Expand by the largest plausible listener range so we do not miss an
            // agent whose own hearing radius reaches into the sound's cell.
            float expanded = radius + _cellSize;
            int span = math.max(1, (int)math.ceil(expanded * _invCellSize));

            int3 center = CellOf(position);

            for (int x = -span; x <= span; x++)
            {
                for (int z = -span; z <= span; z++)
                {
                    // Y is intentionally not swept: levels are floor-based, and a
                    // vertical sweep multiplies cost for almost no additional hits.
                    int3 cell = new int3(center.x + x, center.y, center.z + z);
                    if (!_cells.TryGetValue(cell, out List<int> bucket)) continue;

                    for (int i = 0; i < bucket.Count; i++) results.Add(bucket[i]);
                }
            }
        }

        int3 CellOf(float3 p) => new int3(
            (int)math.floor(p.x * _invCellSize),
            (int)math.floor(p.y * _invCellSize),
            (int)math.floor(p.z * _invCellSize));

        void AddToCell(int3 cell, int slot)
        {
            if (!_cells.TryGetValue(cell, out List<int> bucket))
            {
                bucket = new List<int>(4);
                _cells[cell] = bucket;
            }
            bucket.Add(slot);
        }

        void RemoveFromCell(int3 cell, int slot)
        {
            if (!_cells.TryGetValue(cell, out List<int> bucket)) return;
            bucket.Remove(slot);
            if (bucket.Count == 0) _cells.Remove(cell);
        }

        void Compact()
        {
            _compactPending = false;

            // Rebuild rather than shuffle: cell buckets store slot indices, so any
            // compaction that moves entries invalidates every bucket anyway.
            List<Entry> live = new List<Entry>(_entries.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Alive && _entries[i].Receiver != null) live.Add(_entries[i]);
            }

            _entries.Clear();
            _index.Clear();
            _cells.Clear();

            for (int i = 0; i < live.Count; i++)
            {
                Entry e = live[i];
                e.Cell = CellOf(e.Receiver.ListenerPosition);
                _entries.Add(e);
                _index[e.Receiver] = i;
                AddToCell(e.Cell, i);
            }
        }

        // ----------------------------------------------------------- perceivables

        public void RegisterPerceivable(IPerceivable perceivable)
        {
            if (perceivable == null || _perceivables.Contains(perceivable)) return;
            _perceivables.Add(perceivable);
        }

        public void UnregisterPerceivable(IPerceivable perceivable)
        {
            if (perceivable == null) return;
            _perceivables.Remove(perceivable);
        }

        /// <summary>
        /// Direct list access for vision sensors. Returned by reference and never
        /// copied — sensors iterate by index and must not mutate it.
        /// </summary>
        public IReadOnlyList<IPerceivable> Perceivables => _perceivables;

        public void Clear()
        {
            _entries.Clear();
            _index.Clear();
            _cells.Clear();
            _perceivables.Clear();
            _compactPending = false;
            VLog.Info(LogCat.Perception, "PerceptionBus cleared.");
        }
    }
}
