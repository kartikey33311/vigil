// -----------------------------------------------------------------------------
// Vigil — coarse region graph (navigation tier 2).
//
// The NavMesh answers "what is my next corner?". This answers "which rooms have I
// not swept yet?" — and those are genuinely different questions.
//
// Running search planning on raw NavMesh polygons is both too slow and too
// granular to be LEGIBLE. Players read the monster as intelligent when it clears
// ROOMS in a defensible order. They cannot read intelligence in a mathematically
// optimal polygon sequence, because they cannot see polygons.
//
// Travel costs are precomputed all-pairs with Floyd-Warshall at bake time. That is
// O(n^3), but n is the number of ROOMS — tens, not thousands — so it costs
// microseconds once and makes GetTravelCost O(1) forever after. The Director and
// SearchState both query it constantly, so O(1) matters far more than bake time.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;

namespace Vigil.AI.Pathfinding
{
    /// <summary>
    /// Serialisable bake product. Produced by the editor generator from authored
    /// region volumes plus NavMesh connectivity, consumed by <see cref="RegionGraph"/>
    /// at runtime.
    /// </summary>
    [Serializable]
    public sealed class RegionGraphData
    {
        [Serializable]
        public struct Node
        {
            public int Id;
            public Vector3 Center;
            public Vector3 Extents;
            public string DisplayName;

            [Range(0f, 1f)] public float Darkness;
            [Range(0f, 1f)] public float Enclosure;
        }

        [Serializable]
        public struct Edge
        {
            public int From;
            public int To;

            /// <summary>Traversal cost in metres. 0 means "use the centre distance".</summary>
            public float Cost;
        }

        public List<Node> Nodes = new List<Node>();
        public List<Edge> Edges = new List<Edge>();

        public bool IsEmpty => Nodes == null || Nodes.Count == 0;
    }

    public sealed class RegionGraph : IRegionGraph
    {
        const float Unreachable = float.MaxValue * 0.25f;

        RegionInfo[] _regions = Array.Empty<RegionInfo>();
        int[][] _neighbours = Array.Empty<int[]>();
        float[] _cost = Array.Empty<float>();     // flattened n*n
        int[] _next = Array.Empty<int>();         // flattened n*n path reconstruction
        int[] _lastSearchedTick = Array.Empty<int>();

        Bounds[] _bounds = Array.Empty<Bounds>();
        int _count;

        public int RegionCount => _count;

        /// <summary>Region ids that cannot reach at least one other region. Diagnostic only.</summary>
        public readonly List<int> UnreachablePairsFrom = new List<int>();

        public void Build(RegionGraphData data)
        {
            if (data == null || data.IsEmpty)
            {
                VLog.Warn(LogCat.Pathfinding, "RegionGraph.Build called with no data — strategic search will be disabled.");
                _count = 0;
                return;
            }

            _count = data.Nodes.Count;
            _regions = new RegionInfo[_count];
            _bounds = new Bounds[_count];
            _lastSearchedTick = new int[_count];

            Dictionary<int, int> idToIndex = new Dictionary<int, int>(_count);

            for (int i = 0; i < _count; i++)
            {
                RegionGraphData.Node n = data.Nodes[i];
                idToIndex[n.Id] = i;

                _regions[i] = new RegionInfo
                {
                    Id = n.Id,
                    Center = n.Center,
                    Extents = n.Extents,
                    DisplayName = string.IsNullOrEmpty(n.DisplayName) ? $"Region {n.Id}" : n.DisplayName,
                    Darkness = n.Darkness,
                    Enclosure = n.Enclosure,
                    ExitCount = 0
                };

                _bounds[i] = new Bounds(n.Center, n.Extents * 2f);
                _lastSearchedTick[i] = int.MinValue;
            }

            // Adjacency
            List<int>[] adjacency = new List<int>[_count];
            for (int i = 0; i < _count; i++) adjacency[i] = new List<int>(4);

            _cost = new float[_count * _count];
            _next = new int[_count * _count];

            for (int i = 0; i < _cost.Length; i++) _cost[i] = Unreachable;
            for (int i = 0; i < _next.Length; i++) _next[i] = -1;

            for (int i = 0; i < _count; i++)
            {
                _cost[i * _count + i] = 0f;
                _next[i * _count + i] = i;
            }

            if (data.Edges != null)
            {
                for (int e = 0; e < data.Edges.Count; e++)
                {
                    RegionGraphData.Edge edge = data.Edges[e];
                    if (!idToIndex.TryGetValue(edge.From, out int a) || !idToIndex.TryGetValue(edge.To, out int b)) continue;
                    if (a == b) continue;

                    float cost = edge.Cost > 0f ? edge.Cost : math.distance(_regions[a].Center, _regions[b].Center);

                    // Undirected: a doorway is passable both ways.
                    if (!adjacency[a].Contains(b)) adjacency[a].Add(b);
                    if (!adjacency[b].Contains(a)) adjacency[b].Add(a);

                    int ab = a * _count + b;
                    int ba = b * _count + a;
                    if (cost < _cost[ab]) { _cost[ab] = cost; _next[ab] = b; }
                    if (cost < _cost[ba]) { _cost[ba] = cost; _next[ba] = a; }
                }
            }

            _neighbours = new int[_count][];
            for (int i = 0; i < _count; i++)
            {
                _neighbours[i] = adjacency[i].ToArray();
                _regions[i].ExitCount = _neighbours[i].Length;
            }

            FloydWarshall();
            DetectUnreachable();

            VLog.Info(LogCat.Pathfinding, $"RegionGraph built: {_count} regions, {(data.Edges?.Count ?? 0)} edges.");
        }

        void FloydWarshall()
        {
            for (int k = 0; k < _count; k++)
            {
                int kRow = k * _count;
                for (int i = 0; i < _count; i++)
                {
                    int iRow = i * _count;
                    float ik = _cost[iRow + k];
                    if (ik >= Unreachable) continue;   // early out: no route through k

                    for (int j = 0; j < _count; j++)
                    {
                        float through = ik + _cost[kRow + j];
                        if (through < _cost[iRow + j])
                        {
                            _cost[iRow + j] = through;
                            _next[iRow + j] = _next[iRow + k];
                        }
                    }
                }
            }
        }

        void DetectUnreachable()
        {
            // An unreachable region is a monster that stands still forever, and it is
            // invisible until someone playtests that specific room. Surfacing it at
            // bake time is far cheaper than finding it in a session.
            UnreachablePairsFrom.Clear();
            for (int i = 0; i < _count; i++)
            {
                for (int j = 0; j < _count; j++)
                {
                    if (i == j) continue;
                    if (_cost[i * _count + j] >= Unreachable)
                    {
                        UnreachablePairsFrom.Add(_regions[i].Id);
                        VLog.Warn(LogCat.Pathfinding,
                            $"Region '{_regions[i].DisplayName}' cannot reach '{_regions[j].DisplayName}'.");
                        break;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- queries

        public int GetRegionAt(float3 position)
        {
            Vector3 p = position;

            // Bounds test first — cheap and usually decisive.
            for (int i = 0; i < _count; i++)
            {
                if (_bounds[i].Contains(p)) return _regions[i].Id;
            }

            // Fall back to nearest centre so an agent standing in a doorway or on a
            // seam still gets a usable answer rather than "nowhere".
            float bestSq = float.MaxValue;
            int best = 0;
            for (int i = 0; i < _count; i++)
            {
                float d = math.distancesq(position, _regions[i].Center);
                if (d < bestSq) { bestSq = d; best = _regions[i].Id; }
            }

            return _count > 0 ? best : 0;
        }

        public bool TryGetRegion(int regionId, out RegionInfo info)
        {
            int i = IndexOf(regionId);
            if (i < 0) { info = default; return false; }
            info = _regions[i];
            return true;
        }

        public int GetNeighbours(int regionId, int[] buffer)
        {
            int i = IndexOf(regionId);
            if (i < 0 || buffer == null) return 0;

            int[] src = _neighbours[i];
            int n = math.min(src.Length, buffer.Length);
            for (int k = 0; k < n; k++) buffer[k] = _regions[src[k]].Id;
            return n;
        }

        public float GetTravelCost(int fromRegion, int toRegion)
        {
            int a = IndexOf(fromRegion);
            int b = IndexOf(toRegion);
            if (a < 0 || b < 0) return float.PositiveInfinity;

            float c = _cost[a * _count + b];
            return c >= Unreachable ? float.PositiveInfinity : c;
        }

        public bool TryGetNextRegionTowards(int fromRegion, int toRegion, out int nextRegion)
        {
            nextRegion = 0;
            int a = IndexOf(fromRegion);
            int b = IndexOf(toRegion);
            if (a < 0 || b < 0) return false;

            int step = _next[a * _count + b];
            if (step < 0) return false;

            nextRegion = _regions[step].Id;
            return true;
        }

        public void MarkSearched(int regionId, int tick)
        {
            int i = IndexOf(regionId);
            if (i >= 0) _lastSearchedTick[i] = tick;
        }

        public int GetLastSearchedTick(int regionId)
        {
            int i = IndexOf(regionId);
            return i >= 0 ? _lastSearchedTick[i] : int.MinValue;
        }

        /// <summary>Region id at a flat index, for iteration by scorers.</summary>
        public int RegionIdAt(int index) => (index >= 0 && index < _count) ? _regions[index].Id : 0;

        int IndexOf(int regionId)
        {
            // Ids are assigned densely by the baker, so the fast path is a direct hit.
            for (int i = 0; i < _count; i++)
            {
                if (_regions[i].Id == regionId) return i;
            }
            return -1;
        }
    }
}
