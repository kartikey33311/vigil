// -----------------------------------------------------------------------------
// Vigil — player interaction.
//
// The client raycasts to drive the PROMPT. The server re-validates everything
// before executing. That split is the whole security model here: the client is
// telling the server "I am looking at object 47", and a modified client will
// happily claim to be looking at the extraction point from across the level. So
// the server never trusts the claim — it independently re-checks that the object
// exists, is interactable, and is genuinely within range of that player.
// -----------------------------------------------------------------------------

using Unity.Netcode;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Services;
using Vigil.Core.Simulation;
using Vigil.Data;
using Vigil.Gameplay.Player;

namespace Vigil.Gameplay.Interaction
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class InteractionSystem : NetworkBehaviour, ITickable
    {
        [SerializeField] GameplayConfig _gameplay = null;
        [SerializeField] LayerMask _interactableMask = ~0;

        readonly RaycastHit[] _hits = new RaycastHit[8];

        PlayerCameraRig _rig;
        PlayerCharacter _character;
        TickScheduler _scheduler;
        Camera _camera;

        // ---- owner-side (drives the HUD) --------------------------------------
        InteractableBase _focused;
        InteractionPrompt _prompt = InteractionPrompt.None;
        bool _holding;

        // ---- server-side ------------------------------------------------------
        ulong _serverHoldTarget;
        bool _serverIsHolding;

        /// <summary>Current prompt for the local player. Read by the HUD.</summary>
        public InteractionPrompt CurrentPrompt => _prompt;

        /// <summary>0..1 progress of an in-flight hold, for the HUD ring.</summary>
        public float HoldProgress { get; private set; }

        public bool HasFocus => _focused != null;

        void Awake()
        {
            _rig = GetComponent<PlayerCameraRig>();
            _character = GetComponent<PlayerCharacter>();
        }

        public override void OnNetworkSpawn()
        {
            if (Services.IsReady)
            {
                _scheduler = Services.TryGet<TickScheduler>();
                if (IsServer) _scheduler?.Register(this, TickBudget.Critical);
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer) _scheduler?.Unregister(this);
        }

        float Range => _gameplay != null ? _gameplay.InteractionRange : 2.6f;

        // =====================================================================
        // Owner
        // =====================================================================

        void Update()
        {
            if (!IsOwner) return;

            AcquireFocus();
            HandleInput();
        }

        void AcquireFocus()
        {
            if (_camera == null)
            {
                _camera = _rig != null && _rig.Camera != null ? _rig.Camera : Camera.main;
                if (_camera == null) return;
            }

            Ray ray = new Ray(_camera.transform.position, _camera.transform.forward);

            int count = Physics.RaycastNonAlloc(
                ray, _hits, Range + 0.5f, _interactableMask, QueryTriggerInteraction.Collide);

            InteractableBase best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                InteractableBase candidate = _hits[i].collider.GetComponentInParent<InteractableBase>();
                if (candidate == null) continue;

                if (_hits[i].distance < bestDistance)
                {
                    bestDistance = _hits[i].distance;
                    best = candidate;
                }
            }

            if (best != _focused)
            {
                // Losing focus mid-hold must release the hold, or a player could
                // start a generator and walk away while it kept repairing.
                if (_holding) ReleaseHold();
                _focused = best;
            }

            _prompt = _focused != null ? _focused.GetPrompt(OwnerClientId) : InteractionPrompt.None;
        }

        void HandleInput()
        {
            bool pressed = Input.GetKeyDown(KeyCode.E);
            bool held = Input.GetKey(KeyCode.E);

            if (_focused == null || !_prompt.Available)
            {
                if (_holding) ReleaseHold();
                HoldProgress = 0f;
                return;
            }

            if (_prompt.Kind == InteractionKind.Hold)
            {
                if (held && !_holding)
                {
                    _holding = true;
                    RequestHoldRpc(_focused.NetworkObjectId, true);
                }
                else if (!held && _holding)
                {
                    ReleaseHold();
                }

                if (_holding && _prompt.Duration > 0f)
                {
                    HoldProgress = Mathf.Clamp01(HoldProgress + Time.deltaTime / _prompt.Duration);
                }
                else
                {
                    HoldProgress = 0f;
                }
            }
            else if (pressed)
            {
                RequestInteractRpc(_focused.NetworkObjectId);
            }
        }

        void ReleaseHold()
        {
            _holding = false;
            HoldProgress = 0f;
            if (_focused != null) RequestHoldRpc(_focused.NetworkObjectId, false);
        }

        // =====================================================================
        // Server
        // =====================================================================

        [Rpc(SendTo.Server)]
        void RequestInteractRpc(ulong targetId)
        {
            if (!TryResolve(targetId, out InteractableBase target)) return;

            if (!target.CanInteract(OwnerClientId, out InteractionResult reason))
            {
                if (VLog.Is(LogCat.Gameplay))
                {
                    VLog.Info(LogCat.Gameplay, $"Interact refused for {OwnerClientId} on {targetId}: {reason}");
                }
                return;
            }

            target.Interact(OwnerClientId);
        }

        [Rpc(SendTo.Server)]
        void RequestHoldRpc(ulong targetId, bool isHolding)
        {
            if (!isHolding)
            {
                if (_serverIsHolding && TryResolve(_serverHoldTarget, out InteractableBase previous))
                {
                    previous.CancelHold(OwnerClientId);
                }

                _serverIsHolding = false;
                _serverHoldTarget = 0UL;
                return;
            }

            if (!TryResolve(targetId, out InteractableBase target)) return;
            if (!target.CanInteract(OwnerClientId, out _)) return;

            _serverHoldTarget = targetId;
            _serverIsHolding = true;
        }

        public void OnSimTick(in SimTime time)
        {
            if (!IsServer || !_serverIsHolding) return;

            if (!TryResolve(_serverHoldTarget, out InteractableBase target))
            {
                _serverIsHolding = false;
                return;
            }

            target.TickHold(OwnerClientId, time.DeltaTime);
        }

        /// <summary>
        /// Server-side resolution and validation. The client's claim about what it
        /// is looking at is never trusted — only the id is taken, and range is
        /// re-checked against the server's own copy of both transforms.
        /// </summary>
        bool TryResolve(ulong targetId, out InteractableBase target)
        {
            target = null;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return false;

            if (!nm.SpawnManager.SpawnedObjects.TryGetValue(targetId, out NetworkObject obj) || obj == null)
            {
                return false;
            }

            target = obj.GetComponent<InteractableBase>();
            if (target == null) return false;

            float distance = Vector3.Distance(transform.position, (Vector3)target.InteractionPoint);
            float allowed = target.InteractionRange + 1.0f;   // small tolerance for latency

            if (distance > allowed)
            {
                if (VLog.Is(LogCat.Gameplay))
                {
                    VLog.Warn(LogCat.Gameplay,
                        $"Client {OwnerClientId} claimed interaction with {targetId} at {distance:F1}m (max {allowed:F1}m) - rejected.");
                }

                target = null;
                return false;
            }

            return true;
        }
    }
}
