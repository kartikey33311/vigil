// -----------------------------------------------------------------------------
// Vigil — interactable world objects.
//
// Doors are the most important object in the game after the antagonist itself.
// A door that is only a visual is worthless; this one carves the NavMesh via a
// NavMeshObstacle when shut, so the entity genuinely has to path around it or
// break it down. Closing a door has to MEAN something or the entire stealth layer
// collapses into "run in a straight line".
//
// Generators are the loud objective. Progress is staged and persists, because
// losing 100% of a 26-second repair to one scare is punishing without being
// interesting — the decision we want is "do I risk one more stage?", not "was I
// unlucky?".
// -----------------------------------------------------------------------------

using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Vigil.AI.Perception;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Events;
using Vigil.Core.Services;
using Vigil.Core.Simulation;
using Vigil.Data;

namespace Vigil.Gameplay.Interaction
{
    /// <summary>Shared plumbing for networked interactables.</summary>
    [RequireComponent(typeof(NetworkObject))]
    public abstract class InteractableBase : NetworkBehaviour, IInteractable
    {
        [SerializeField] protected GameplayConfig _gameplay = null;
        [SerializeField] protected Transform _interactionAnchor = null;

        protected PerceptionBus Bus;
        protected IEventBus Events;

        public ulong EntityId => NetworkObjectId;

        public virtual float3 InteractionPoint =>
            _interactionAnchor != null ? (float3)_interactionAnchor.position : (float3)transform.position;

        public virtual float InteractionRange => _gameplay != null ? _gameplay.InteractionRange : 2.6f;

        public override void OnNetworkSpawn()
        {
            if (Services.IsReady)
            {
                Bus = Services.TryGet<PerceptionBus>();
                Events = Services.TryGet<IEventBus>();
            }
        }

        public abstract InteractionPrompt GetPrompt(ulong requesterId);
        public abstract bool CanInteract(ulong requesterId, out InteractionResult reason);
        public abstract InteractionResult Interact(ulong requesterId);

        /// <summary>Hold interactions receive continuous progress from the server.</summary>
        public virtual void TickHold(ulong requesterId, float deltaTime) { }

        /// <summary>Called server-side when a hold is released before completion.</summary>
        public virtual void CancelHold(ulong requesterId) { }

        /// <summary>
        /// Server-side: feeds the AI's perception. Only the server simulates hearing.
        /// </summary>
        protected void EmitNoise(StimulusTag tag, float intensity, float radius)
        {
            if (Bus == null) return;

            Stimulus s = BuildStimulus(tag, intensity, radius);
            Bus.Broadcast(in s);
        }

        /// <summary>
        /// Local-peer: feeds the AUDIO layer, which lives in an assembly that cannot
        /// see the perception bus and therefore only learns about the world through
        /// events. Safe to call on clients — it is presentation only.
        /// </summary>
        protected void PublishLocalNoise(StimulusTag tag, float intensity, float radius)
        {
            if (Events == null) return;

            Stimulus s = BuildStimulus(tag, intensity, radius);
            Events.Publish(new NoiseEmittedEvent { Stimulus = s });
        }

        Stimulus BuildStimulus(StimulusTag tag, float intensity, float radius)
        {
            int tick = 0;
            SimClock clock = Services.IsReady ? Services.TryGet<SimClock>() : null;
            if (clock != null) tick = clock.CurrentTick;

            return new Stimulus(
                StimulusChannel.Sound, tag, transform.position, intensity, radius, EntityId, tick);
        }
    }

    // =========================================================================
    // Door
    // =========================================================================

    public sealed class Door : InteractableBase
    {
        [Header("Door")]
        [SerializeField] Transform _leaf = null;
        [SerializeField] float _openAngle = 95f;
        [SerializeField] bool _startsLocked = false;

        [SerializeField, Tooltip("Carves the NavMesh while shut so the entity must path around or break through.")]
        NavMeshObstacle _obstacle = null;

        readonly NetworkVariable<bool> _isOpen = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<bool> _isLocked = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<float> _health = new NetworkVariable<float>(
            100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        Quaternion _closedRotation;
        float _visualAngle;

        public bool IsOpen => _isOpen.Value;

        void Awake()
        {
            if (_leaf == null) _leaf = transform;
            _closedRotation = _leaf.localRotation;

            if (_obstacle == null) _obstacle = GetComponentInChildren<NavMeshObstacle>();

            if (_obstacle != null)
            {
                // Carving (rather than just obstructing) is what makes the entity
                // actually re-path instead of walking into the door and grinding.
                _obstacle.carving = true;
                _obstacle.carveOnlyStationary = false;
            }
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (IsServer)
            {
                _isLocked.Value = _startsLocked;
                _health.Value = _gameplay != null ? _gameplay.DoorHealth : 160f;
            }

            _isOpen.OnValueChanged += OnOpenChanged;
            ApplyObstacle(_isOpen.Value);
        }

        public override void OnNetworkDespawn()
        {
            _isOpen.OnValueChanged -= OnOpenChanged;
        }

        void OnOpenChanged(bool previous, bool current)
        {
            ApplyObstacle(current);

            // Fires on every peer, so a client hears a door that the server opened.
            float radius = _gameplay != null ? _gameplay.DoorNoiseRadius : 15f;
            PublishLocalNoise(StimulusTag.Door, 0.6f, radius);
        }

        void ApplyObstacle(bool open)
        {
            if (_obstacle != null) _obstacle.enabled = !open;
        }

        public override InteractionPrompt GetPrompt(ulong requesterId)
        {
            if (_isLocked.Value)
            {
                return new InteractionPrompt
                {
                    Verb = "Locked",
                    Subject = "Door",
                    Kind = InteractionKind.Instant,
                    Available = false,
                    BlockedReason = "Requires a key"
                };
            }

            return new InteractionPrompt
            {
                Verb = _isOpen.Value ? "Close" : "Open",
                Subject = "Door",
                Kind = InteractionKind.Instant,
                Available = true
            };
        }

        public override bool CanInteract(ulong requesterId, out InteractionResult reason)
        {
            if (_isLocked.Value) { reason = InteractionResult.Locked; return false; }
            reason = InteractionResult.Success;
            return true;
        }

        public override InteractionResult Interact(ulong requesterId)
        {
            if (!IsServer) return InteractionResult.Failed;
            if (_isLocked.Value) return InteractionResult.Locked;

            _isOpen.Value = !_isOpen.Value;

            // Doors are loud. Opening one to escape is a real trade.
            float radius = _gameplay != null ? _gameplay.DoorNoiseRadius : 15f;
            EmitNoise(StimulusTag.Door, 0.6f, radius);

            return InteractionResult.Success;
        }

        /// <summary>Server-side: the entity battering a shut door.</summary>
        public void ApplyBreachDamage(float amount)
        {
            if (!IsServer || _isOpen.Value) return;

            _health.Value = Mathf.Max(0f, _health.Value - amount);
            EmitNoise(StimulusTag.Breakage, 0.9f, 28f);

            if (_health.Value <= 0f)
            {
                _isOpen.Value = true;
                _isLocked.Value = false;
                VLog.Info(LogCat.Gameplay, $"Door {EntityId} breached.");
            }
        }

        void Update()
        {
            // Presentation only; the authoritative state is the bool.
            float target = _isOpen.Value ? _openAngle : 0f;
            float speed = _gameplay != null ? (_openAngle / Mathf.Max(0.05f, _gameplay.DoorSwingSeconds)) : 160f;

            _visualAngle = Mathf.MoveTowards(_visualAngle, target, speed * Time.deltaTime);
            _leaf.localRotation = _closedRotation * Quaternion.Euler(0f, _visualAngle, 0f);
        }
    }

    // =========================================================================
    // Generator
    // =========================================================================

    public sealed class Generator : InteractableBase
    {
        [Header("Generator")]
        [SerializeField, Tooltip("Region this generator powers. 0 = the whole level.")]
        int _regionId = 0;

        [SerializeField] Light[] _poweredLights = null;

        readonly NetworkVariable<int> _stagesComplete = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<float> _stageProgress = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<bool> _isRunning = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        float _noiseTimer;

        public int RegionId => _regionId;
        public bool IsRunning => _isRunning.Value;

        /// <summary>0..1 overall repair completion, for the HUD.</summary>
        public float Completion
        {
            get
            {
                int stages = _gameplay != null ? Mathf.Max(1, _gameplay.GeneratorStages) : 4;
                return Mathf.Clamp01((_stagesComplete.Value + _stageProgress.Value) / stages);
            }
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _isRunning.OnValueChanged += OnRunningChanged;
            ApplyLights(_isRunning.Value);
        }

        public override void OnNetworkDespawn()
        {
            _isRunning.OnValueChanged -= OnRunningChanged;
        }

        void OnRunningChanged(bool previous, bool current)
        {
            ApplyLights(current);

            float radius = _gameplay != null ? _gameplay.GeneratorNoiseRadius : 34f;
            PublishLocalNoise(StimulusTag.Machinery, 1f, radius);
        }

        float _localHumTimer;

        void Update()
        {
            // A running generator is a continuous landmark: it tells players where
            // they have already been, and it tells the entity where they might be.
            // Published locally on every peer so clients hear it too.
            if (!_isRunning.Value) return;

            _localHumTimer -= Time.deltaTime;
            if (_localHumTimer > 0f) return;

            _localHumTimer = 1.9f;

            float radius = _gameplay != null ? _gameplay.GeneratorNoiseRadius : 34f;
            PublishLocalNoise(StimulusTag.Machinery, 0.5f, radius);
        }

        void ApplyLights(bool on)
        {
            if (_poweredLights == null) return;
            for (int i = 0; i < _poweredLights.Length; i++)
            {
                if (_poweredLights[i] != null) _poweredLights[i].enabled = on;
            }
        }

        public override InteractionPrompt GetPrompt(ulong requesterId)
        {
            if (_isRunning.Value)
            {
                return new InteractionPrompt
                {
                    Verb = "Running",
                    Subject = "Generator",
                    Kind = InteractionKind.Instant,
                    Available = false,
                    BlockedReason = "Already restored"
                };
            }

            int stages = _gameplay != null ? _gameplay.GeneratorStages : 4;

            return new InteractionPrompt
            {
                Verb = "Repair",
                Subject = $"Generator ({_stagesComplete.Value}/{stages})",
                Kind = InteractionKind.Hold,
                Duration = StageDuration,
                Available = true
            };
        }

        float StageDuration
        {
            get
            {
                if (_gameplay == null) return 6.5f;
                return _gameplay.GeneratorRepairSeconds / Mathf.Max(1, _gameplay.GeneratorStages);
            }
        }

        public override bool CanInteract(ulong requesterId, out InteractionResult reason)
        {
            if (_isRunning.Value) { reason = InteractionResult.Blocked; return false; }
            reason = InteractionResult.Success;
            return true;
        }

        public override InteractionResult Interact(ulong requesterId)
        {
            // Hold-driven; a tap does nothing but is not an error.
            return _isRunning.Value ? InteractionResult.Blocked : InteractionResult.Success;
        }

        public override void TickHold(ulong requesterId, float deltaTime)
        {
            if (!IsServer || _isRunning.Value) return;

            _stageProgress.Value += deltaTime / Mathf.Max(0.05f, StageDuration);

            // Repairing is loud and continuous. This is the risk half of the loop.
            _noiseTimer -= deltaTime;
            if (_noiseTimer <= 0f)
            {
                _noiseTimer = 0.6f;
                float radius = _gameplay != null ? _gameplay.GeneratorNoiseRadius : 34f;
                EmitNoise(StimulusTag.Machinery, 0.75f, radius);
            }

            if (_stageProgress.Value < 1f) return;

            _stageProgress.Value = 0f;
            _stagesComplete.Value++;

            int stages = _gameplay != null ? _gameplay.GeneratorStages : 4;

            if (_stagesComplete.Value >= stages)
            {
                _isRunning.Value = true;
                EmitNoise(StimulusTag.Machinery, 1f, 60f);

                Events?.Publish(new ObjectiveCompletedEvent
                {
                    ObjectiveId = (int)(EntityId & 0x7FFFFFFF),
                    CompletedBy = requesterId
                });

                VLog.Info(LogCat.Gameplay, $"Generator {EntityId} restored by {requesterId}.");
            }
        }

        public override void CancelHold(ulong requesterId)
        {
            // Completed stages persist. Only the in-progress stage is lost, so being
            // driven off costs you seconds rather than everything.
            if (!IsServer) return;
            _stageProgress.Value = 0f;
        }
    }

    // =========================================================================
    // Extraction
    // =========================================================================

    public sealed class ExtractionPoint : InteractableBase
    {
        readonly NetworkVariable<bool> _isArmed = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public bool IsArmed => _isArmed.Value;

        /// <summary>Server-only. Opened by the objective system once all generators run.</summary>
        public void Arm()
        {
            if (!IsServer) return;
            _isArmed.Value = true;
            EmitNoise(StimulusTag.Machinery, 1f, 70f);
        }

        public override InteractionPrompt GetPrompt(ulong requesterId)
        {
            if (!_isArmed.Value)
            {
                return new InteractionPrompt
                {
                    Verb = "Sealed",
                    Subject = "Exit",
                    Kind = InteractionKind.Instant,
                    Available = false,
                    BlockedReason = "Restore power first"
                };
            }

            return new InteractionPrompt
            {
                Verb = "Escape",
                Subject = "Exit",
                Kind = InteractionKind.Hold,
                Duration = 2.5f,
                Available = true
            };
        }

        public override bool CanInteract(ulong requesterId, out InteractionResult reason)
        {
            if (!_isArmed.Value) { reason = InteractionResult.Blocked; return false; }
            reason = InteractionResult.Success;
            return true;
        }

        public override InteractionResult Interact(ulong requesterId) => InteractionResult.Success;

        float _escapeProgress;

        public override void TickHold(ulong requesterId, float deltaTime)
        {
            if (!IsServer || !_isArmed.Value) return;

            _escapeProgress += deltaTime;
            if (_escapeProgress < 2.5f) return;

            _escapeProgress = 0f;

            Events?.Publish(new ObjectiveCompletedEvent { ObjectiveId = -1, CompletedBy = requesterId });
            VLog.Info(LogCat.Gameplay, $"Player {requesterId} extracted.");
        }

        public override void CancelHold(ulong requesterId)
        {
            if (IsServer) _escapeProgress = 0f;
        }
    }
}
