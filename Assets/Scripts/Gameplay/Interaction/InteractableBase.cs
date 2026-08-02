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
        /// events. Safe to call on clients â€” it is presentation only.
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
}
