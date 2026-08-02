// -----------------------------------------------------------------------------
// Vigil — mission state and the facility's power/light model.
//
// This is what turns a level containing an AI into a GAME: an objective chain,
// a win condition, a loss condition, and a light model that closes the loop
// between "restore power" and "you are now easier to see".
//
// It also supplies IDarknessSampler, which is the missing link that makes several
// already-built systems actually matter:
//
//   darkness -> composure drain      (PlayerCharacter.TickComposure)
//   darkness -> visibility           (VisionSensor light modulation)
//   darkness -> search preference    (SearchState region scoring)
//
// Until something answered "how dark is it here?", all three ran on a constant
// 0.5 and none of the lighting trade-offs existed.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using Vigil.AI.Agents;
using Vigil.AI.Director;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Events;
using Vigil.Core.Services;
using Vigil.Core.Simulation;
using Vigil.Data;
using Vigil.Gameplay.Interaction;
using Vigil.Gameplay.Player;

namespace Vigil.Gameplay.Systems
{
    public enum MissionOutcome : byte
    {
        InProgress = 0,
        Extracted = 1,
        Wiped = 2
    }

    [RequireComponent(typeof(NetworkObject))]
    public sealed class MissionDirector : NetworkBehaviour, ITickable, IDarknessSampler
    {
        [SerializeField] GameplayConfig _gameplay = null;

        [SerializeField, Tooltip("Ambient darkness where no light reaches. 1 = pitch black.")]
        [Range(0f, 1f)] float _baseDarkness = 0.92f;

        readonly NetworkVariable<int> _generatorsOnline = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<int> _generatorsRequired = new NetworkVariable<int>(
            3, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<bool> _extractionArmed = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<byte> _outcome = new NetworkVariable<byte>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly List<Generator> _generators = new List<Generator>(8);
        readonly List<Light> _lights = new List<Light>(32);

        ExtractionPoint _extraction;
        PlayerRegistry _players;
        IAIDirector _aiDirector;
        IEventBus _events;
        TickScheduler _scheduler;
        System.IDisposable _objectiveSub;

        float _evaluateTimer;

        public int GeneratorsOnline => _generatorsOnline.Value;
        public int GeneratorsRequired => _generatorsRequired.Value;
        public bool ExtractionArmed => _extractionArmed.Value;
        public MissionOutcome Outcome => (MissionOutcome)_outcome.Value;

        /// <summary>Live objective text for the HUD.</summary>
        public string ObjectiveText
        {
            get
            {
                switch (Outcome)
                {
                    case MissionOutcome.Extracted: return "EXTRACTED";
                    case MissionOutcome.Wiped: return "ALL PERSONNEL LOST";
                    default:
                        return _extractionArmed.Value
                            ? "Power restored - reach the exit"
                            : $"Restore power  {_generatorsOnline.Value}/{_generatorsRequired.Value}";
                }
            }
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public override void OnNetworkSpawn()
        {
            CacheSceneObjects();

            if (Services.IsReady)
            {
                ServiceContext ctx = Services.Active;

                _events = ctx.TryGet<IEventBus>();
                _scheduler = ctx.TryGet<TickScheduler>();
                _aiDirector = ctx.TryGet<IAIDirector>();
                _players = ctx.TryGet<PlayerRegistry>();

                // Registering as the darkness source is what activates the lighting
                // trade-offs across perception, composure and AI search. Replace
                // rather than skip: a stale sampler from a previous level points at a
                // destroyed object and silently reports the wrong light everywhere.
                ctx.Unregister<IDarknessSampler>();
                ctx.Register<IDarknessSampler>(this);
                _players?.SetDarknessSampler(this);
            }

            if (IsServer)
            {
                int required = _gameplay != null ? _gameplay.GeneratorsRequired : 3;
                _generatorsRequired.Value = Mathf.Min(required, Mathf.Max(1, _generators.Count));

                _objectiveSub = _events?.Subscribe<ObjectiveCompletedEvent>(OnObjectiveCompleted);
                _scheduler?.Register(this, TickBudget.Normal);
            }

            VLog.Info(LogCat.Gameplay,
                $"Mission ready: {_generators.Count} generator(s), {_lights.Count} light(s), extraction {(_extraction != null ? "present" : "MISSING")}.");
        }

        public override void OnNetworkDespawn()
        {
            _objectiveSub?.Dispose();
            if (IsServer) _scheduler?.Unregister(this);

            ServiceContext ctx = Services.Active;
            if (ctx != null && ReferenceEquals(ctx.TryGet<IDarknessSampler>(), this))
            {
                ctx.Unregister<IDarknessSampler>();
            }
        }

        void CacheSceneObjects()
        {
            // Include inactive: scene-placed NetworkObjects are not guaranteed to be
            // active-and-spawned at the moment THIS object spawns, and a lookup that
            // silently misses one leaves the mission permanently unwinnable.
            _generators.Clear();
            _generators.AddRange(FindObjectsByType<Generator>(FindObjectsInactive.Include, FindObjectsSortMode.None));

            ExtractionPoint[] exits = FindObjectsByType<ExtractionPoint>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            _extraction = exits.Length > 0 ? exits[0] : null;

            // Cached once at level load. Lights do not move in this game, so the
            // per-query cost is a loop over a small fixed array rather than a scene
            // search — which would be catastrophic given how often darkness is sampled.
            _lights.Clear();
            Light[] all = FindObjectsByType<Light>(FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].type == LightType.Directional) continue;   // handled by _baseDarkness
                _lights.Add(all[i]);
            }
        }

        // =====================================================================
        // Mission logic
        // =====================================================================

        void OnObjectiveCompleted(ObjectiveCompletedEvent evt)
        {
            if (!IsServer) return;

            // -1 is the extraction sentinel.
            if (evt.ObjectiveId == -1)
            {
                Complete(MissionOutcome.Extracted);
                return;
            }

            RecountGenerators();

            _aiDirector?.Report(DirectorEvent.GeneratorStarted, evt.CompletedBy, transform.position);
            _aiDirector?.Report(DirectorEvent.ObjectiveCompleted, evt.CompletedBy, transform.position);
        }

        void RecountGenerators()
        {
            int online = 0;
            for (int i = 0; i < _generators.Count; i++)
            {
                if (_generators[i] != null && _generators[i].IsRunning) online++;
            }

            _generatorsOnline.Value = online;

            if (online < _generatorsRequired.Value || _extractionArmed.Value) return;

            _extractionArmed.Value = true;
            _extraction?.Arm();

            // The extraction window is the one time the game is allowed to be
            // unfair: pressure is unclamped and the entity is authorised throughout.
            _aiDirector?.ForcePhase(DreadPhase.Peak);

            VLog.Info(LogCat.Gameplay, "Power restored - extraction armed.");
        }

        public void OnSimTick(in SimTime time)
        {
            if (!IsServer || Outcome != MissionOutcome.InProgress) return;

            _evaluateTimer -= time.DeltaTime;
            if (_evaluateTimer > 0f) return;
            _evaluateTimer = 1f;

            RecountGenerators();
            CheckWipe();
        }

        void CheckWipe()
        {
            if (_players == null || _players.Players.Count == 0) return;

            bool anyAlive = false;
            for (int i = 0; i < _players.Players.Count; i++)
            {
                PlayerCharacter p = _players.Players[i];
                if (p == null) continue;
                if (p.IsAlive && !p.IsDowned) { anyAlive = true; break; }
            }

            if (!anyAlive) Complete(MissionOutcome.Wiped);
        }

        void Complete(MissionOutcome outcome)
        {
            if (_outcome.Value != (byte)MissionOutcome.InProgress) return;

            _outcome.Value = (byte)outcome;
            VLog.Info(LogCat.Gameplay, $"Mission ended: {outcome}");

            // Stop pressuring a table that has already finished.
            _aiDirector?.ForcePhase(DreadPhase.Fadeout);
        }

        // =====================================================================
        // IDarknessSampler
        // =====================================================================

        public float DarknessAt(float3 position)
        {
            float illumination = 0f;
            Vector3 p = position;

            for (int i = 0; i < _lights.Count; i++)
            {
                Light light = _lights[i];
                if (light == null || !light.enabled || !light.gameObject.activeInHierarchy) continue;

                float range = light.range;
                if (range <= 0.01f) continue;

                float distance = Vector3.Distance(light.transform.position, p);
                if (distance >= range) continue;

                // Linear falloff is not physically correct, but it is far cheaper
                // than the real curve and the AI only needs a coarse "lit / not lit".
                float falloff = 1f - (distance / range);
                float contribution = falloff * Mathf.Clamp01(light.intensity / 3f);

                // Spot lights only illuminate what they point at.
                if (light.type == LightType.Spot)
                {
                    Vector3 toPoint = (p - light.transform.position).normalized;
                    float angle = Vector3.Angle(light.transform.forward, toPoint);
                    if (angle > light.spotAngle * 0.5f) continue;
                }

                illumination += contribution;
                if (illumination >= 1f) return 0f;
            }

            return Mathf.Clamp01(_baseDarkness - illumination);
        }
    }
}
