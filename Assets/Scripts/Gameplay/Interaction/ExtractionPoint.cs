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
