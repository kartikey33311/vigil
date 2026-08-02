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
}
