using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;

namespace Vigil.Data
{
    [CreateAssetMenu(menuName = "Vigil/Gameplay/Movement Config", fileName = "MovementConfig")]
    public sealed class MovementConfig : ScriptableObject, IValidatableConfig
    {
        [Header("Speeds (m/s) â€” indexed by Gait: Idle, Crouch, Walk, Sprint")]
        [SerializeField] float[] _speed = new float[ConfigCounts.Gaits] { 0f, 1.35f, 3.1f, 5.4f };

        [SerializeField, Min(0.1f)] float _acceleration = 28f;
        [SerializeField, Min(0.1f)] float _deceleration = 34f;
        [SerializeField, Range(0f, 1f), Tooltip("Fraction of ground control retained mid-air.")]
        float _airControl = 0.25f;

        [Header("Capsule")]
        [SerializeField, Min(0.5f)] float _standHeight = 1.8f;
        [SerializeField, Min(0.4f)] float _crouchHeight = 1.05f;
        [SerializeField, Min(0.1f)] float _radius = 0.35f;
        [SerializeField, Min(0f)] float _stepHeight = 0.35f;
        [SerializeField, Range(0f, 70f)] float _slopeLimit = 50f;
        [SerializeField] float _gravity = -19.6f;
        [SerializeField, Min(0f)] float _jumpSpeed = 4.2f;
        [SerializeField, Tooltip("Layers the character collides with. Must exclude the player's own layer.")]
        LayerMask _collisionMask = ~0;

        [Header("Stamina")]
        [SerializeField, Min(1f)] float _maxStamina = 100f;
        [SerializeField, Min(0f)] float _sprintDrainPerSecond = 22f;
        [SerializeField, Min(0f)] float _staminaRegenPerSecond = 13f;
        [SerializeField, Min(0f), Tooltip("Seconds after sprinting before regen begins. Without a delay, sprint becomes free and the resource stops mattering.")]
        float _staminaRegenDelay = 1.4f;

        [Header("Footstep noise â€” indexed by Gait")]
        [SerializeField, Tooltip("Seconds between footsteps.")]
        float[] _stepInterval = new float[ConfigCounts.Gaits] { 0f, 0.72f, 0.50f, 0.34f };

        [SerializeField, Tooltip("Stimulus radius in metres. THIS is the stealth system â€” crouching must be meaningfully quieter than walking or there is no decision to make.")]
        float[] _stepNoiseRadius = new float[ConfigCounts.Gaits] { 0f, 4.5f, 13f, 26f };

        [SerializeField, Tooltip("Stimulus intensity 0..1.")]
        float[] _stepNoiseIntensity = new float[ConfigCounts.Gaits] { 0f, 0.22f, 0.55f, 0.95f };

        [SerializeField, Min(0f)] float _landingNoiseRadius = 17f;

        public float Speed(Gait g) => Idx(_speed, (int)g, 3f);
        public float StepInterval(Gait g) => Idx(_stepInterval, (int)g, 0.5f);
        public float StepNoiseRadius(Gait g) => Idx(_stepNoiseRadius, (int)g, 10f);
        public float StepNoiseIntensity(Gait g) => Idx(_stepNoiseIntensity, (int)g, 0.5f);

        public float Acceleration => _acceleration;
        public float Deceleration => _deceleration;
        public float AirControl => _airControl;
        public float StandHeight => _standHeight;
        public float CrouchHeight => _crouchHeight;
        public float Radius => _radius;
        public float StepHeight => _stepHeight;
        public float SlopeLimit => _slopeLimit;
        public float Gravity => _gravity;
        public float JumpSpeed => _jumpSpeed;
        public LayerMask CollisionMask => _collisionMask;
        public float MaxStamina => _maxStamina;
        public float SprintDrainPerSecond => _sprintDrainPerSecond;
        public float StaminaRegenPerSecond => _staminaRegenPerSecond;
        public float StaminaRegenDelay => _staminaRegenDelay;
        public float LandingNoiseRadius => _landingNoiseRadius;

        static float Idx(float[] a, int i, float f) => (a != null && i >= 0 && i < a.Length) ? a[i] : f;

        public void Validate(IList<string> problems)
        {
            if (_speed == null || _speed.Length != ConfigCounts.Gaits)
            {
                problems.Add($"{name}: speed must have exactly {ConfigCounts.Gaits} entries.");
                return;
            }

            // Noise must be monotonic in gait or the stealth decision inverts.
            for (int i = 1; i < ConfigCounts.Gaits; i++)
            {
                if (_stepNoiseRadius != null && _stepNoiseRadius.Length == ConfigCounts.Gaits &&
                    _stepNoiseRadius[i] < _stepNoiseRadius[i - 1])
                {
                    problems.Add($"{name}: {(Gait)i} is quieter than {(Gait)(i - 1)} â€” noise must increase with gait.");
                }
            }

            if (_crouchHeight >= _standHeight) problems.Add($"{name}: crouchHeight must be below standHeight.");
            if (_gravity >= 0f) problems.Add($"{name}: gravity must be negative.");
            if (_collisionMask == 0) problems.Add($"{name}: collisionMask is empty â€” the player will fall through the world.");
        }
    }
}
