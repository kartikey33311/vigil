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
}
