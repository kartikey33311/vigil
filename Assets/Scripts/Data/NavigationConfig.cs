using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Simulation;

namespace Vigil.Data
{
    [CreateAssetMenu(menuName = "Vigil/AI/Navigation Config", fileName = "NavigationConfig")]
    public sealed class NavigationConfig : ScriptableObject, IValidatableConfig
    {
        [Header("Agent metrics â€” must match the NavMesh bake")]
        [SerializeField, Min(0.1f)] float _agentRadius = 0.45f;
        [SerializeField, Min(0.5f)] float _agentHeight = 2.0f;
        [SerializeField, Min(0f)] float _stepHeight = 0.4f;
        [SerializeField, Range(0f, 60f)] float _maxSlope = 45f;

        [Header("Query budget")]
        [SerializeField, Range(1, 64), Tooltip("Concurrent in-flight path queries. Requests beyond this are rejected with PathHandle.Invalid â€” callers must degrade, never block.")]
        int _maxConcurrentQueries = 24;

        [SerializeField, Range(0.1f, 8f), Tooltip("Milliseconds per tick spent servicing path queries. This is the hard bound on pathfinding cost.")]
        float _queryBudgetMs = 1.2f;

        [SerializeField, Min(1f), Tooltip("Reject paths longer than this. Stops an agent walking 400m around a building to reach someone 2m away through a wall.")]
        float _maxPathLength = 260f;

        [SerializeField, Min(0.1f), Tooltip("Radius used to project a requested point onto the NavMesh.")]
        float _snapRadius = 2.5f;

        [Header("Repath cadence â€” indexed by AwarenessLevel")]
        [SerializeField, Tooltip("Seconds between repaths. Chasing repaths often; patrolling barely ever. Repathing every tick is pure waste and is the single most common AI perf mistake.")]
        float[] _repathInterval = new float[ConfigCounts.AwarenessLevels] { 3.0f, 1.5f, 0.8f, 0.35f, 1.0f };

        [SerializeField, Min(1), Tooltip("Ticks after which a computed corridor is considered stale and re-requested.")]
        int _corridorStaleTicks = 90;

        [SerializeField, Tooltip("Use PartialSuccess paths. Strongly recommended â€” an agent that walks as far as it can reads far better than one that stands still.")]
        bool _acceptPartialPaths = true;

        [Header("Following")]
        [SerializeField, Min(0.05f)] float _arrivalRadius = 0.6f;
        [SerializeField, Min(0.05f), Tooltip("Lookahead used to smooth corners so the agent does not visibly snap.")]
        float _cornerLookahead = 1.4f;

        [SerializeField, Range(2, 120), Tooltip("Ticks of no forward progress before the follower reports IsStuck.")]
        int _stuckTicks = 24;

        [SerializeField, Min(0.001f)] float _stuckProgressEpsilon = 0.05f;

        [Header("Link traversal (seconds)")]
        [SerializeField, Min(0.1f)] float _ventDuration = 2.6f;
        [SerializeField, Min(0.1f)] float _windowDuration = 0.9f;
        [SerializeField, Min(0.1f)] float _ledgeDropDuration = 0.7f;

        [Header("Point sampling")]
        [SerializeField, Range(4, 64), Tooltip("Candidate points sampled by TryFindConcealedPoint / TryFindPointAwayFrom.")]
        int _sampleCandidates = 16;

        [SerializeField, Tooltip("Layers treated as sight blockers when scoring concealment.")]
        LayerMask _concealmentMask = ~0;

        public float AgentRadius => _agentRadius;
        public float AgentHeight => _agentHeight;
        public float StepHeight => _stepHeight;
        public float MaxSlope => _maxSlope;
        public int MaxConcurrentQueries => _maxConcurrentQueries;
        public float QueryBudgetMs => _queryBudgetMs;
        public float MaxPathLength => _maxPathLength;
        public float SnapRadius => _snapRadius;
        public int CorridorStaleTicks => _corridorStaleTicks;
        public bool AcceptPartialPaths => _acceptPartialPaths;
        public float ArrivalRadius => _arrivalRadius;
        public float CornerLookahead => _cornerLookahead;
        public int StuckTicks => _stuckTicks;
        public float StuckProgressEpsilon => _stuckProgressEpsilon;
        public float VentDuration => _ventDuration;
        public float WindowDuration => _windowDuration;
        public float LedgeDropDuration => _ledgeDropDuration;
        public int SampleCandidates => _sampleCandidates;
        public LayerMask ConcealmentMask => _concealmentMask;

        public float RepathInterval(AwarenessLevel level)
        {
            int i = (int)level;
            return (_repathInterval != null && i >= 0 && i < _repathInterval.Length) ? _repathInterval[i] : 1f;
        }

        public float LinkDuration(int navArea)
        {
            switch (navArea)
            {
                case NavArea.Vent: return _ventDuration;
                case NavArea.Window: return _windowDuration;
                default: return _ledgeDropDuration;
            }
        }

        public void Validate(IList<string> problems)
        {
            if (_repathInterval == null || _repathInterval.Length != ConfigCounts.AwarenessLevels)
            {
                problems.Add($"{name}: repathInterval must have exactly {ConfigCounts.AwarenessLevels} entries.");
            }
            if (_queryBudgetMs > 4f)
            {
                problems.Add($"{name}: queryBudgetMs {_queryBudgetMs:F1}ms is a large slice of a 33ms tick; pathfinding will show up in frame spikes.");
            }
            if (_concealmentMask == 0)
            {
                problems.Add($"{name}: concealmentMask is empty â€” every candidate point will score as fully concealed.");
            }
        }
    }
}
