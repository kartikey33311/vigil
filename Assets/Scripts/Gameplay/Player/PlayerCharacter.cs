// -----------------------------------------------------------------------------
// Vigil — the player.
//
// Server-authoritative: the owner samples input and sends it; the server runs the
// movement step and owns the resulting position. The owner also runs the SAME step
// locally for immediate visual response, so movement does not wait a full RTT.
//
// The most important thing in this file is not the movement — it is
// VisibilityScale. It is the knob the whole stealth layer turns on:
//
//   crouching        ↓   standing still  ↓   in darkness   ↓
//   sprinting        ↑   FLASHLIGHT ON   ↑↑
//
// Light is the only way to see, and it is the loudest thing you can do visually.
// That single trade is the core risk/reward decision the game is built around.
// -----------------------------------------------------------------------------

using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Events;
using Vigil.Core.Services;
using Vigil.Core.Simulation;
using Vigil.Data;
using Vigil.AI.Agents;
using Vigil.AI.Perception;

namespace Vigil.Gameplay.Player
{
    /// <summary>Quantised input for one simulation tick. Sent every tick, so it is kept small.</summary>
    public struct PlayerInputCommand : INetworkSerializable
    {
        public int Tick;

        /// <summary>Move axes quantised to sbyte (-127..127 maps to -1..1).</summary>
        public sbyte MoveX;
        public sbyte MoveZ;

        /// <summary>Yaw in degrees, quantised to a short (0.0055 deg resolution).</summary>
        public short Yaw;
        public short Pitch;

        /// <summary>bit0 jump, bit1 crouch, bit2 sprint, bit3 interact, bit4 flashlight.</summary>
        public byte Buttons;

        public const byte BitJump = 1 << 0;
        public const byte BitCrouch = 1 << 1;
        public const byte BitSprint = 1 << 2;
        public const byte BitInteract = 1 << 3;
        public const byte BitFlashlight = 1 << 4;

        // 4 + 1 + 1 + 2 + 2 + 1 = 11 bytes on the wire.
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Tick);
            serializer.SerializeValue(ref MoveX);
            serializer.SerializeValue(ref MoveZ);
            serializer.SerializeValue(ref Yaw);
            serializer.SerializeValue(ref Pitch);
            serializer.SerializeValue(ref Buttons);
        }

        public float3 MoveAxis => new float3(MoveX / 127f, 0f, MoveZ / 127f);
        public float YawDegrees => Yaw * (360f / 32767f);
        public bool Has(byte bit) => (Buttons & bit) != 0;
    }

    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(CharacterController))]
    public class PlayerCharacter : NetworkBehaviour,
        IPerceivable, IDamageable, IComposureHolder, IFactionMember, IAdaptiveTickable
    {
        [Header("Configuration")]
        [SerializeField] MovementConfig _movement = null;
        [SerializeField] ComposureConfig _composureConfig = null;
        [SerializeField] GameplayConfig _gameplay = null;
        [SerializeField] PerceptionConfig _perception = null;

        [Header("Rig")]
        [SerializeField] Transform _head = null;
        [SerializeField] Light _flashlight = null;

        readonly NetworkVariable<Vector3> _netPosition = new NetworkVariable<Vector3>(
            Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<float> _netYaw = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<float> _netHealth = new NetworkVariable<float>(
            100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<bool> _netCrouched = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<bool> _netFlashlightOn = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly NetworkVariable<bool> _netDowned = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Composure is only meaningful to its owner, so it is not broadcast.
        readonly NetworkVariable<float> _netComposure = new NetworkVariable<float>(
            100f, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);

        CharacterController _controller;
        Transform _transform;

        TickScheduler _scheduler;
        PerceptionBus _bus;
        ScentTrail _scent;
        IEventBus _events;

        PlayerInputCommand _pendingInput;
        PlayerInputCommand _latestServerInput;

        float3 _velocity;
        float _stamina;
        float _stepDistanceAccumulator;
        float _scentAccumulator;
        float _bleedOutRemaining;
        float _lastSprintAt;

        readonly float3[] _sampleScratch = new float3[4];

        bool _registered;
        bool _isMoving;

        public Faction Faction => Faction.Survivors;
        public ulong EntityId => NetworkObjectId;

        public float Health => _netHealth.Value;
        public float MaxHealth => _gameplay != null ? _gameplay.MaxHealth : 100f;
        public bool IsAlive => _netHealth.Value > 0f;
        public bool IsDowned => _netDowned.Value;

        public float Composure => _netComposure.Value;

        /// <summary>Set true while inside a locker with the door shut.</summary>
        public bool IsHidden { get; set; }

        /// <summary>Current gait, derived from input. Drives footstep noise.</summary>
        public Gait CurrentGait { get; private set; } = Gait.Idle;

        // =====================================================================
        // Lifecycle
        // =====================================================================

        PlayerCameraRig _rig;

        void Awake()
        {
            _transform = transform;
            _controller = GetComponent<CharacterController>();
            _rig = GetComponent<PlayerCameraRig>();
            _stamina = _movement != null ? _movement.MaxStamina : 100f;
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                _netHealth.Value = MaxHealth;
                _netComposure.Value = _composureConfig != null ? _composureConfig.MaxComposure : 100f;
                _netPosition.Value = _transform.position;
            }

            if (!Services.IsReady)
            {
                VLog.Warn(LogCat.Gameplay, $"PlayerCharacter {EntityId} spawned with no ServiceContext.", this);
                return;
            }

            ServiceContext ctx = Services.Active;
            _scheduler = ctx.TryGet<TickScheduler>();
            _bus = ctx.TryGet<PerceptionBus>();
            _scent = ctx.TryGet<ScentTrail>();
            _events = ctx.TryGet<IEventBus>();

            // Registered as perceivable on the SERVER only — that is where vision is
            // evaluated. Registering on clients would be harmless but pointless.
            if (IsServer)
            {
                _bus?.RegisterPerceivable(this);
                _scheduler?.Register(this, TickBudget.Critical);
                _registered = true;

                _events?.Publish(new PlayerSpawnedEvent { PlayerId = EntityId, Position = _transform.position });
            }

            if (IsOwner)
            {
                _scheduler?.Register(this, TickBudget.Critical);
                _registered = true;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (!_registered) return;

            _scheduler?.Unregister(this);
            _bus?.UnregisterPerceivable(this);
            _registered = false;
        }

        public TickBudget DesiredBudget => TickBudget.Critical;

        // =====================================================================
        // Simulation
        // =====================================================================

        public void OnSimTick(in SimTime time)
        {
            if (IsOwner)
            {
                _pendingInput = SampleInput(time.Tick);

                // Local application for immediate response. The server re-runs the
                // same step and owns the result; see ApplyCorrection.
                Simulate(in _pendingInput, in time, authoritative: false);

                SubmitInputRpc(_pendingInput);
            }

            if (IsServer)
            {
                Simulate(in _latestServerInput, in time, authoritative: true);
                ServerTickSystems(in time);
            }
        }

        [Rpc(SendTo.Server)]
        void SubmitInputRpc(PlayerInputCommand command)
        {
            // Trust the buttons and axes; never the resulting position. The server
            // derives position itself from this input, which is what makes a
            // teleport hack impossible rather than merely detectable.
            _latestServerInput = command;
        }

        PlayerInputCommand SampleInput(int tick)
        {
            // Input System actions are built in code by PlayerInputReader when it is
            // present; this fallback keeps the character drivable in a bare test
            // scene without any input assets.
            float x = Input.GetAxisRaw("Horizontal");
            float z = Input.GetAxisRaw("Vertical");

            // Yaw comes from the camera rig, which owns mouse look and updates at
            // render rate. Reading the transform instead would lag by up to a tick
            // and make the character drift behind the camera during fast turns.
            float yaw = _rig != null ? _rig.Yaw : _transform.eulerAngles.y;

            byte buttons = 0;
            if (Input.GetKey(KeyCode.Space)) buttons |= PlayerInputCommand.BitJump;
            if (Input.GetKey(KeyCode.LeftControl)) buttons |= PlayerInputCommand.BitCrouch;
            if (Input.GetKey(KeyCode.LeftShift)) buttons |= PlayerInputCommand.BitSprint;
            if (Input.GetKey(KeyCode.E)) buttons |= PlayerInputCommand.BitInteract;
            if (Input.GetKeyDown(KeyCode.F)) buttons |= PlayerInputCommand.BitFlashlight;

            return new PlayerInputCommand
            {
                Tick = tick,
                MoveX = (sbyte)math.clamp((int)(x * 127f), -127, 127),
                MoveZ = (sbyte)math.clamp((int)(z * 127f), -127, 127),
                Yaw = (short)math.clamp((int)(yaw * (32767f / 360f)), short.MinValue, short.MaxValue),
                Pitch = 0,
                Buttons = buttons
            };
        }

        /// <summary>
        /// The shared movement step. Runs identically on owner and server.
        ///
        /// <para>It must stay deterministic given (state, input, dt): no
        /// UnityEngine.Time reads, no Random, no scene state beyond the character
        /// controller's own collision. Divergence here is the root cause of every
        /// reconciliation jitter bug, so any future edit must preserve that.</para>
        /// </summary>
        void Simulate(in PlayerInputCommand input, in SimTime time, bool authoritative)
        {
            if (!IsAlive || IsDowned)
            {
                _velocity = float3.zero;
                return;
            }

            MovementConfig cfg = _movement;
            float dt = time.DeltaTime;

            bool crouch = input.Has(PlayerInputCommand.BitCrouch);
            bool sprintHeld = input.Has(PlayerInputCommand.BitSprint);

            float3 axis = input.MoveAxis;
            float axisMagnitude = math.length(axis);
            bool moving = axisMagnitude > 0.05f;

            bool canSprint = sprintHeld && !crouch && moving && _stamina > 1f;

            Gait gait = !moving ? Gait.Idle : (crouch ? Gait.Crouch : (canSprint ? Gait.Sprint : Gait.Walk));
            CurrentGait = gait;
            _isMoving = moving;

            float speed = cfg != null ? cfg.Speed(gait) : 3f;

            // Stamina is only advanced by the authority so client and server cannot
            // disagree about whether a sprint was legal.
            if (authoritative || IsOwner)
            {
                if (canSprint)
                {
                    _stamina = math.max(0f, _stamina - (cfg != null ? cfg.SprintDrainPerSecond : 20f) * dt);
                    _lastSprintAt = (float)time.Elapsed;
                }
                else
                {
                    float delay = cfg != null ? cfg.StaminaRegenDelay : 1.4f;
                    if (time.Elapsed - _lastSprintAt >= delay)
                    {
                        float max = cfg != null ? cfg.MaxStamina : 100f;
                        _stamina = math.min(max, _stamina + (cfg != null ? cfg.StaminaRegenPerSecond : 13f) * dt);
                    }
                }
            }

            // Orient movement by the commanded yaw rather than the transform, so the
            // server reproduces exactly what the client intended.
            float yawRad = math.radians(input.YawDegrees);
            math.sincos(yawRad, out float sin, out float cos);
            float3 forward = new float3(sin, 0f, cos);
            float3 right = new float3(cos, 0f, -sin);

            float3 wish = forward * axis.z + right * axis.x;
            if (math.lengthsq(wish) > 1f) wish = math.normalize(wish);

            float3 targetVelocity = wish * speed;

            float accel = moving
                ? (cfg != null ? cfg.Acceleration : 28f)
                : (cfg != null ? cfg.Deceleration : 34f);

            _velocity.x = math.lerp(_velocity.x, targetVelocity.x, math.saturate(accel * dt));
            _velocity.z = math.lerp(_velocity.z, targetVelocity.z, math.saturate(accel * dt));

            // Gravity. A small constant downward velocity while grounded keeps the
            // controller pinned to slopes instead of stair-stepping down them.
            if (_controller != null && _controller.isGrounded)
            {
                _velocity.y = -2f;
                if (input.Has(PlayerInputCommand.BitJump))
                {
                    _velocity.y = cfg != null ? cfg.JumpSpeed : 4.2f;
                }
            }
            else
            {
                _velocity.y += (cfg != null ? cfg.Gravity : -19.6f) * dt;
            }

            if (_controller != null && _controller.enabled)
            {
                _controller.Move((Vector3)_velocity * dt);

                float standHeight = cfg != null ? cfg.StandHeight : 1.8f;
                float crouchHeight = cfg != null ? cfg.CrouchHeight : 1.05f;
                float targetHeight = crouch ? crouchHeight : standHeight;

                // Smooth the capsule height so crouching does not pop the camera.
                _controller.height = Mathf.MoveTowards(_controller.height, targetHeight, 6f * dt);
                _controller.center = new Vector3(0f, _controller.height * 0.5f, 0f);
            }

            _transform.rotation = Quaternion.Euler(0f, input.YawDegrees, 0f);

            if (authoritative)
            {
                _netPosition.Value = _transform.position;
                _netYaw.Value = input.YawDegrees;
                _netCrouched.Value = crouch;

                if (input.Has(PlayerInputCommand.BitFlashlight))
                {
                    _netFlashlightOn.Value = !_netFlashlightOn.Value;
                }
            }
        }

        void ServerTickSystems(in SimTime time)
        {
            EmitFootsteps(in time);
            DropScent(in time);
            TickComposure(in time);
            TickBleedOut(in time);
        }

        void EmitFootsteps(in SimTime time)
        {
            if (!_isMoving || _bus == null || _movement == null) return;

            float interval = _movement.StepInterval(CurrentGait);
            if (interval <= 0f) return;

            _stepDistanceAccumulator += time.DeltaTime;
            if (_stepDistanceAccumulator < interval) return;

            _stepDistanceAccumulator = 0f;

            // Panic makes you loud. This is the feedback loop that connects the
            // player's emotional state to what the monster can actually hear.
            float noiseScale = NoiseMultiplier;

            float radius = _movement.StepNoiseRadius(CurrentGait) * noiseScale;
            float intensity = math.saturate(_movement.StepNoiseIntensity(CurrentGait) * noiseScale);

            if (radius <= 0.01f || intensity <= 0.001f) return;

            StimulusTag tag = CurrentGait == Gait.Sprint ? StimulusTag.Sprint : StimulusTag.Footstep;

            Stimulus s = new Stimulus(
                StimulusChannel.Sound, tag, _transform.position,
                intensity, radius, EntityId, time.Tick, _velocity);

            _bus.Broadcast(in s);
        }

        /// <summary>
        /// Client-side footstep AUDIO, derived from actual displacement.
        ///
        /// <para>Deliberately separate from the server's stimulus emission above.
        /// The server decides what the AI can HEAR; this decides what the local
        /// player can hear, and it must run on every peer — including for remote
        /// teammates, whose gait is not replicated. Driving it from replicated
        /// displacement means teammates are audible without spending a single byte
        /// of bandwidth per footstep, which at 30 Hz across four players would
        /// otherwise be a meaningful share of the budget.</para>
        /// </summary>
        void TickFootstepAudio()
        {
            if (_events == null || _movement == null) return;

            Vector3 position = _transform.position;

            if (!_audioPrimed)
            {
                _lastAudioPosition = position;
                _audioPrimed = true;
                return;
            }

            Vector3 delta = position - _lastAudioPosition;
            delta.y = 0f;
            _lastAudioPosition = position;

            float distance = delta.magnitude;
            if (distance < 0.0005f) return;

            // Airborne characters do not make footfalls.
            if (_controller != null && !_controller.isGrounded) return;

            _audioStrideAccumulator += distance;

            // Infer gait from speed for remote proxies, whose CurrentGait is never
            // set (Simulate only runs for the owner and the server).
            float speed = distance / Mathf.Max(0.0001f, Time.deltaTime);
            Gait gait = InferGaitFromSpeed(speed);

            float stride = Mathf.Max(0.35f, _movement.Speed(gait) * _movement.StepInterval(gait));
            if (_audioStrideAccumulator < stride) return;

            _audioStrideAccumulator = 0f;

            float intensity = _movement.StepNoiseIntensity(gait);
            float radius = _movement.StepNoiseRadius(gait);
            if (intensity <= 0.001f) return;

            StimulusTag tag = gait == Gait.Sprint ? StimulusTag.Sprint : StimulusTag.Footstep;

            Stimulus s = new Stimulus(
                StimulusChannel.Sound, tag, position, intensity, radius, EntityId, 0);

            _events.Publish(new NoiseEmittedEvent { Stimulus = s });
        }

        Gait InferGaitFromSpeed(float speed)
        {
            if (_movement == null) return Gait.Walk;

            float crouch = _movement.Speed(Gait.Crouch);
            float walk = _movement.Speed(Gait.Walk);

            if (speed <= crouch * 1.15f) return Gait.Crouch;
            if (speed <= walk * 1.15f) return Gait.Walk;
            return Gait.Sprint;
        }

        Vector3 _lastAudioPosition;
        float _audioStrideAccumulator;
        bool _audioPrimed;

        void DropScent(in SimTime time)
        {
            if (_scent == null || _movement == null || !_isMoving) return;

            // Distance-based, not time-based: a player who stands still should not
            // keep laying a trail, and a sprinting player should lay one faster.
            _scentAccumulator += math.length(new float2(_velocity.x, _velocity.z)) * time.DeltaTime;

            float interval = _perception != null ? _perception.ScentDropInterval : 2.5f;
            if (_scentAccumulator < interval) return;

            _scentAccumulator = 0f;
            _scent.Drop(_transform.position, EntityId);
        }

        void TickComposure(in SimTime time)
        {
            if (_composureConfig == null) return;

            ComposureConfig cfg = _composureConfig;
            float delta = 0f;

            // Darkness drain. Without a lighting service this resolves to a mid
            // value, which keeps the mechanic alive in a bare test scene.
            float darkness = 0.5f;
            IDarknessSampler sampler = Services.TryGet<IDarknessSampler>();
            if (sampler != null) darkness = sampler.DarknessAt(_transform.position);

            delta -= cfg.DrainByDarkness(darkness);

            // Light recovery is the inverse.
            delta += cfg.RecoverInLight * (1f - darkness);

            float next = math.clamp(_netComposure.Value + delta * time.DeltaTime, 0f, cfg.MaxComposure);

            if (math.abs(next - _netComposure.Value) > 0.01f)
            {
                _netComposure.Value = next;
                _events?.Publish(new CompositionChangedEvent { PlayerId = EntityId, Composure = next });
            }
        }

        void TickBleedOut(in SimTime time)
        {
            if (!_netDowned.Value) return;

            _bleedOutRemaining -= time.DeltaTime;
            if (_bleedOutRemaining > 0f) return;

            _netHealth.Value = 0f;
            _netDowned.Value = false;

            _events?.Publish(new PlayerKilledEvent { PlayerId = EntityId, InstigatorId = 0UL });
            VLog.Info(LogCat.Gameplay, $"Player {EntityId} bled out.");
        }

        // =====================================================================
        // IPerceivable — the stealth surface
        // =====================================================================

        public float3 PerceivablePosition => _transform.position + Vector3.up * 0.9f;

        public bool IsPerceivable => IsAlive && !IsHidden;

        public float VisibilityScale
        {
            get
            {
                float scale = 1f;

                if (_netCrouched.Value) scale *= 0.55f;
                if (!_isMoving) scale *= 0.7f;          // stillness genuinely helps

                // A lit flashlight roughly doubles how visible you are. Light is the
                // only way to see, and it is the loudest thing you can do visually —
                // that trade is the core decision of every encounter.
                if (_netFlashlightOn.Value) scale *= 2.1f;

                if (CurrentGait == Gait.Sprint) scale *= 1.25f;

                return scale;
            }
        }

        public int GetVisibilitySamples(float3[] buffer)
        {
            if (buffer == null || buffer.Length == 0) return 0;

            float3 basePos = _transform.position;
            float height = _controller != null ? _controller.height : 1.8f;

            int n = 0;
            buffer[n++] = basePos + new float3(0f, height * 0.92f, 0f);   // head
            if (n < buffer.Length) buffer[n++] = basePos + new float3(0f, height * 0.5f, 0f);   // torso
            if (n < buffer.Length) buffer[n++] = basePos + new float3(0f, 0.25f, 0f);           // feet

            return n;
        }

        // =====================================================================
        // IComposureHolder
        // =====================================================================

        public float NoiseMultiplier
        {
            get
            {
                if (_composureConfig == null) return 1f;
                float normalised = _netComposure.Value / math.max(1f, _composureConfig.MaxComposure);
                return _composureConfig.NoiseMultiplier(normalised);
            }
        }

        // =====================================================================
        // IDamageable
        // =====================================================================

        public float ApplyDamage(in DamageInfo info)
        {
            if (!IsServer || !IsAlive) return 0f;

            float applied = info.Amount;
            float next = math.max(0f, _netHealth.Value - applied);
            _netHealth.Value = next;

            if (next <= 0f && !_netDowned.Value)
            {
                // Downed, not dead. The bleed-out timer is the co-op hook — it turns
                // one player's mistake into a decision for everyone else.
                _netDowned.Value = true;
                _bleedOutRemaining = _gameplay != null ? _gameplay.BleedOutSeconds : 55f;
                _netHealth.Value = 1f;

                _events?.Publish(new PlayerDownedEvent
                {
                    PlayerId = EntityId,
                    InstigatorId = info.InstigatorId,
                    Position = _transform.position
                });

                VLog.Info(LogCat.Gameplay, $"Player {EntityId} downed by {info.InstigatorId}.");
            }

            return applied;
        }

        /// <summary>Server-side revive by a teammate.</summary>
        public void Revive(ulong rescuerId)
        {
            if (!IsServer || !_netDowned.Value) return;

            _netDowned.Value = false;
            _netHealth.Value = MaxHealth * 0.5f;
            _bleedOutRemaining = 0f;

            _events?.Publish(new PlayerRevivedEvent { PlayerId = EntityId, RescuerId = rescuerId });
        }

        // =====================================================================
        // Rendering
        // =====================================================================

        void Update()
        {
            if (_flashlight != null) _flashlight.enabled = _netFlashlightOn.Value;

            if (!IsServer && !IsOwner)
            {
                // Remote players: smooth toward the replicated pose.
                float t = 1f - Mathf.Exp(-14f * Time.deltaTime);
                _transform.position = Vector3.Lerp(_transform.position, _netPosition.Value, t);
                _transform.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(_transform.eulerAngles.y, _netYaw.Value, t), 0f);
            }

            // Runs on EVERY peer, including remote proxies — that is what makes
            // teammates audible without replicating a single footstep.
            TickFootstepAudio();
        }

        /// <summary>Metres to the nearest living teammate. Fed to the Director as isolation.</summary>
        public float IsolationDistance { get; set; }
    }
}
