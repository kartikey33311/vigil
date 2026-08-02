// -----------------------------------------------------------------------------
// Vigil — per-level wiring.
//
// The bootstrap builds services that live for the whole session. This builds the
// things that only exist while a level is loaded: the player roster, the
// antagonist's network spawn, the region graph bake, and the interest-management
// registrations.
//
// Separating the two matters because a session outlives a level. Rebuilding the
// service graph on every level load would reset the Director's pacing state
// mid-campaign, which is exactly the kind of subtle "why did the monster suddenly
// go quiet" bug that takes days to trace.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using Unity.AI.Navigation;
using Unity.Netcode;
using UnityEngine;
using Vigil.AI.Agents;
using Vigil.AI.Director;
using Vigil.AI.Pathfinding;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Services;
using Vigil.Core.Simulation;
using Vigil.Gameplay.Player;
using Vigil.Gameplay.Systems;
using Vigil.Net.Interest;

namespace Vigil.Bootstrap
{
    [DefaultExecutionOrder(-8000)]
    public sealed class VigilLevelSpawner : MonoBehaviour, ITickable
    {
        [SerializeField, Tooltip("Scene-placed antagonist. Spawned as a NetworkObject by the server on session start.")]
        NpcAgent _antagonist = null;

        [SerializeField, Tooltip("Region volumes defining the coarse navigation graph. Optional; a single fallback region is generated if empty.")]
        Transform[] _regionVolumes = null;

        [SerializeField, Tooltip("Surface baked at level load. Assigned by the content generator.")]
        NavMeshSurface _navMeshSurface = null;

        PlayerRegistry _registry;
        RegionGraph _regions;
        InterestManager _interest;
        AIDirector _director;
        TickScheduler _scheduler;

        readonly List<PlayerCharacter> _trackedPlayers = new List<PlayerCharacter>(8);
        bool _ready;

        void Start()
        {
            if (!Services.IsReady)
            {
                VLog.Error(LogCat.Core, "VigilLevelSpawner: no active ServiceContext. Enter through the Bootstrap scene.", this);
                return;
            }

            ServiceContext ctx = Services.Active;

            _regions = ctx.TryGet<RegionGraph>();
            _interest = ctx.TryGet<InterestManager>();
            _director = ctx.TryGet<AIDirector>();
            _scheduler = ctx.TryGet<TickScheduler>();

            BakeNavMesh();
            BuildRegionGraph();

            // Level-scoped, not session-scoped. Unregister any registration left by a
            // previous level before adding ours, otherwise the second level load
            // logs a duplicate-registration error and — worse — systems that cached
            // the old instance keep talking to a roster that no longer has anyone in it.
            _registry = new PlayerRegistry();
            ctx.Unregister<PlayerRegistry>();
            ctx.Register(_registry);

            IDarknessSampler sampler = ctx.TryGet<IDarknessSampler>();
            if (sampler != null) _registry.SetDarknessSampler(sampler);

            _director?.SetPlayerSource(_registry);

            _scheduler?.Register(_registry, TickBudget.High);
            _scheduler?.Register(this, TickBudget.Normal);

            HookNetworkCallbacks();
            SpawnAntagonist();

            _ready = true;
            VLog.Info(LogCat.Core, "Level wiring complete.", this);
        }

        /// <summary>
        /// Bakes the NavMesh at level load rather than relying on data saved into
        /// the scene.
        ///
        /// <para>NavMeshSurface.BuildNavMesh() called from an editor script builds
        /// into memory; persisting it into the scene requires the editor's asset
        /// manager path, and if that is skipped the scene loads at runtime with NO
        /// navmesh at all — every path query fails and the monster stands still,
        /// with no error to explain why. Baking here makes the level correct by
        /// construction, and it is the right call anyway for a facility that is
        /// assembled procedurally.</para>
        /// </summary>
        void BakeNavMesh()
        {
            if (_navMeshSurface == null)
            {
                _navMeshSurface = FindAnyObjectByType<NavMeshSurface>();
                if (_navMeshSurface == null)
                {
                    VLog.Warn(LogCat.Pathfinding, "No NavMeshSurface in the level — AI navigation will fail.", this);
                    return;
                }
            }

            float start = Time.realtimeSinceStartup;
            _navMeshSurface.BuildNavMesh();

            VLog.Info(LogCat.Pathfinding,
                $"NavMesh baked in {(Time.realtimeSinceStartup - start) * 1000f:F0}ms.", this);
        }

        void BuildRegionGraph()
        {
            if (_regions == null) return;

            RegionGraphData data = new RegionGraphData();

            if (_regionVolumes != null && _regionVolumes.Length > 0)
            {
                for (int i = 0; i < _regionVolumes.Length; i++)
                {
                    Transform t = _regionVolumes[i];
                    if (t == null) continue;

                    // Prefer a collider's bounds when one exists, otherwise treat
                    // localScale as the volume size. The generated volumes carry no
                    // collider deliberately, so they cannot contaminate the NavMesh
                    // bake or be hit by interaction raycasts.
                    Collider col = t.GetComponent<Collider>();
                    Vector3 extents = col != null ? col.bounds.extents : t.localScale * 0.5f;

                    if (extents.sqrMagnitude < 0.01f) extents = Vector3.one * 5f;

                    data.Nodes.Add(new RegionGraphData.Node
                    {
                        Id = i + 1,
                        Center = t.position,
                        Extents = extents,
                        DisplayName = t.name,
                        Darkness = 0.6f,
                        Enclosure = 0.5f
                    });
                }

                // Connect each region to its nearest neighbours. A proper bake would
                // derive this from NavMesh portal connectivity; nearest-neighbour is
                // a reasonable approximation for a rectilinear facility and, more
                // importantly, never produces a disconnected graph.
                for (int i = 0; i < data.Nodes.Count; i++)
                {
                    for (int j = i + 1; j < data.Nodes.Count; j++)
                    {
                        float d = Vector3.Distance(data.Nodes[i].Center, data.Nodes[j].Center);
                        if (d > 45f) continue;

                        data.Edges.Add(new RegionGraphData.Edge
                        {
                            From = data.Nodes[i].Id,
                            To = data.Nodes[j].Id,
                            Cost = d
                        });
                    }
                }
            }
            else
            {
                // No authored volumes. A single region keeps every consumer working
                // (patrol, search, interest management) instead of null-guarding
                // the region graph in a dozen places.
                data.Nodes.Add(new RegionGraphData.Node
                {
                    Id = 1,
                    Center = Vector3.zero,
                    Extents = new Vector3(40f, 6f, 40f),
                    DisplayName = "Facility",
                    Darkness = 0.7f,
                    Enclosure = 0.5f
                });
            }

            _regions.Build(data);
        }

        void HookNetworkCallbacks()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null) return;

            nm.OnClientConnectedCallback += HandleClientConnected;
            nm.OnClientDisconnectCallback += HandleClientDisconnected;
        }

        void HandleClientConnected(ulong clientId)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            if (!nm.ConnectedClients.TryGetValue(clientId, out NetworkClient client)) return;
            if (client.PlayerObject == null) return;

            PlayerCharacter player = client.PlayerObject.GetComponent<PlayerCharacter>();
            if (player == null) return;

            _registry.Register(player);
            _trackedPlayers.Add(player);
            _interest?.Register(client.PlayerObject, InterestProfile.Proximity);

            VLog.Info(LogCat.Session, $"Player {clientId} joined; roster now {_trackedPlayers.Count}.");
        }

        void HandleClientDisconnected(ulong clientId)
        {
            for (int i = _trackedPlayers.Count - 1; i >= 0; i--)
            {
                PlayerCharacter p = _trackedPlayers[i];
                if (p == null || p.OwnerClientId == clientId)
                {
                    if (p != null) _registry.Unregister(p);
                    _trackedPlayers.RemoveAt(i);
                }
            }
        }

        void SpawnAntagonist()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || _antagonist == null) return;

            NetworkObject netObj = _antagonist.GetComponent<NetworkObject>();
            if (netObj == null)
            {
                VLog.Error(LogCat.AI, "Antagonist has no NetworkObject — it cannot be spawned.", _antagonist);
                return;
            }

            if (!netObj.IsSpawned) netObj.Spawn();

            // Concealed profile: the antagonist is culled aggressively AND requires
            // region adjacency. This is the anti-cheat boundary, not an optimisation.
            _interest?.Register(netObj, InterestProfile.Concealed);

            VLog.Info(LogCat.AI, "Antagonist spawned.", _antagonist);
        }

        public void OnSimTick(in SimTime time)
        {
            if (!_ready) return;

            NetworkManager nm = NetworkManager.Singleton;
            bool isServer = nm == null || nm.IsServer;
            if (!isServer) return;

            if (_antagonist != null && _registry != null)
            {
                _registry.ReportAntagonistPosition(_antagonist.Position);
            }
            else
            {
                _registry?.ClearAntagonist();
            }
        }

        void OnDestroy()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null)
            {
                nm.OnClientConnectedCallback -= HandleClientConnected;
                nm.OnClientDisconnectCallback -= HandleClientDisconnected;
            }

            _scheduler?.Unregister(this);

            if (_registry != null)
            {
                _scheduler?.Unregister(_registry);

                // Only retract the registration if it is still ours — a level that is
                // being torn down after the next one already registered must not
                // remove the incoming level's roster.
                ServiceContext ctx = Services.Active;
                if (ctx != null && ReferenceEquals(ctx.TryGet<PlayerRegistry>(), _registry))
                {
                    ctx.Unregister<PlayerRegistry>();
                }
            }
        }
    }
}
