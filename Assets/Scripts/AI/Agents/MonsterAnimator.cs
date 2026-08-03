// -----------------------------------------------------------------------------
// Vigil — procedural animation for the antagonist.
//
// There is no Animator and no animation data. Every pose in this file is produced
// by rotating the rig transforms directly in LateUpdate.
//
// It runs off OBSERVED MOTION — the root transform's position delta — and never
// off network state. That is the whole trick that makes it work in co-op: the
// server moves the agent through steering, remote clients move the same root by
// interpolating a replicated position, and both peers therefore see an identical
// gait without replicating a single animation byte. Anything driven from the
// simulation (awareness, path, FSM state) would animate on the host and stand
// still on every client.
//
// LateUpdate, not Update, because NpcAgent writes the root pose in Update on
// clients and during the sim tick on the server; reading the delta afterwards is
// the only way to sample the final position for the frame.
//
// Everything is composed on top of the rest pose captured in Awake, so this
// cooperates with whatever bind pose the rig builder authored instead of
// overwriting it.
//
// Sign convention (Unity, +Z forward, limbs modelled hanging DOWN from their
// pivot): a POSITIVE X rotation tips a segment's top toward +Z and its bottom
// toward -Z. So positive X = torso leaning forward, arm/leg swinging backward,
// knee folding heel-backward. Negative X = elbow folding hand-forward.
// -----------------------------------------------------------------------------

using UnityEngine;
using Vigil.Core.Diagnostics;

namespace Vigil.AI.Agents
{
    /// <summary>
    /// Animates the creature rig procedurally from observed root motion: stride,
    /// idle breathing, head tracking, a lunge pose and an aggression-driven gait
    /// escalation. Sits on the same GameObject as <see cref="NpcAgent"/>, but does
    /// not depend on it — it works on a bare prefab in a test scene too.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MonsterAnimator : MonoBehaviour
    {
        const float Tau = Mathf.PI * 2f;

        // ---- stride ----------------------------------------------------------

        [Header("Stride")]
        [SerializeField, Min(0.1f), Tooltip("Speed (m/s) at which the stride reaches full amplitude.")]
        float _referenceSpeed = 3.4f;

        [SerializeField, Min(0.01f), Tooltip("Stride cycles per second, per m/s of travel.")]
        float _strideHzPerSpeed = 0.42f;

        [SerializeField, Min(0.1f), Tooltip("Hard ceiling on stride frequency so a shove never blurs the legs.")]
        float _maxStrideHz = 3.2f;

        [SerializeField, Min(0f)] float _legSwingDegrees = 34f;
        [SerializeField, Min(0f)] float _kneeBendDegrees = 38f;
        [SerializeField, Min(0f)] float _armSwingDegrees = 26f;
        [SerializeField, Min(0f)] float _elbowBendDegrees = 24f;
        [SerializeField, Min(0f)] float _hipYawDegrees = 7f;
        [SerializeField, Min(0f)] float _bodyRollDegrees = 3.5f;

        [SerializeField, Min(0f), Tooltip("Vertical dip of the whole Rig on each footfall, in metres.")]
        float _strideBobMetres = 0.07f;

        [SerializeField, Min(0.1f), Tooltip("How fast stride amplitude settles. Low values make stopping look boneless.")]
        float _strideSettleRate = 7f;

        // ---- idle ------------------------------------------------------------

        [Header("Idle")]
        [SerializeField, Min(0.01f), Tooltip("Below this speed (m/s) the idle breath is at full weight.")]
        float _idleSpeedThreshold = 0.4f;

        [SerializeField, Min(0.01f)] float _breathHz = 0.22f;
        [SerializeField, Min(0f)] float _breathBobMetres = 0.035f;
        [SerializeField, Min(0f)] float _breathTwistDegrees = 4f;
        [SerializeField, Min(0f)] float _breathPitchDegrees = 2.5f;

        // ---- head and eyes ---------------------------------------------------

        [Header("Head")]
        [SerializeField, Range(0f, 120f)] float _headYawLimit = 78f;
        [SerializeField, Range(0f, 89f)] float _headPitchLimit = 45f;
        [SerializeField, Min(0.1f)] float _headTrackRate = 5.5f;
        [SerializeField, Min(0f), Tooltip("Slow scan applied when nothing is being tracked.")]
        float _idleScanDegrees = 13f;

        [SerializeField, Range(0f, 60f)] float _eyeYawLimit = 20f;
        [SerializeField, Range(0f, 60f)] float _eyePitchLimit = 14f;
        [SerializeField, Min(0.1f), Tooltip("Eyes snap faster than the head, so they lead it.")]
        float _eyeTrackRate = 15f;

        // ---- lunge -----------------------------------------------------------

        [Header("Lunge")]
        [SerializeField, Min(0.01f)] float _lungeStrikeSeconds = 0.15f;
        [SerializeField, Min(0.01f)] float _lungeRecoverSeconds = 0.35f;
        [SerializeField] float _lungeTorsoDegrees = 44f;
        [SerializeField] float _lungeArmDegrees = 64f;
        [SerializeField] float _lungeElbowDegrees = 26f;
        [SerializeField] float _lungeHeadDegrees = 16f;
        [SerializeField, Tooltip("Forward shove of the whole Rig during the strike, in metres.")]
        float _lungeReachMetres = 0.22f;

        // ---- aggression ------------------------------------------------------

        [Header("Aggression")]
        [SerializeField, Min(0f)] float _hunchIdleDegrees = 5f;
        [SerializeField, Min(0f)] float _hunchAggroDegrees = 29f;
        [SerializeField, Min(1f), Tooltip("Stride frequency multiplier at full aggression.")]
        float _aggroFrequencyBoost = 1.7f;
        [SerializeField, Min(0f), Tooltip("Extra limb swing, as a fraction, at full aggression.")]
        float _aggroSwingBoost = 0.55f;
        [SerializeField, Min(0f), Tooltip("Out-of-phase arm wobble that only appears while hunting.")]
        float _aggroJitterDegrees = 15f;
        [SerializeField, Min(0.1f)] float _aggroBlendRate = 2.5f;

        // ---- motion sampling -------------------------------------------------

        [Header("Motion Sampling")]
        [SerializeField, Min(0.1f)] float _speedSmoothing = 8f;
        [SerializeField, Min(1f), Tooltip("Apparent speed above which a frame is treated as a teleport and ignored.")]
        float _teleportSpeed = 25f;

        // ---- cached rig ------------------------------------------------------

        Transform _transform;
        Transform _rig;
        Transform _torso;
        Transform _head;
        Transform _eyeL;
        Transform _eyeR;
        Transform _armLUpper;
        Transform _armLLower;
        Transform _armRUpper;
        Transform _armRLower;
        Transform _hip;
        Transform _legLUpper;
        Transform _legLLower;
        Transform _legRUpper;
        Transform _legRLower;

        // ---- rest pose -------------------------------------------------------

        Vector3 _rigRestPosition;
        Quaternion _torsoRest;
        Quaternion _headRest;
        Quaternion _eyeLRest;
        Quaternion _eyeRRest;
        Quaternion _armLUpperRest;
        Quaternion _armLLowerRest;
        Quaternion _armRUpperRest;
        Quaternion _armRLowerRest;
        Quaternion _hipRest;
        Quaternion _legLUpperRest;
        Quaternion _legLLowerRest;
        Quaternion _legRUpperRest;
        Quaternion _legRLowerRest;

        // ---- runtime ---------------------------------------------------------

        Vector3 _lastPosition;
        float _speed;
        float _gait;
        float _stridePhase;
        float _breathPhase;

        float _aggression;
        float _aggressionTarget;

        bool _lungeActive;
        float _lungeTime;
        float _lungeWeight;

        bool _hasLookTarget;
        Vector3 _lookTarget;
        float _headYaw;
        float _headPitch;
        float _eyeYaw;
        float _eyePitch;

        /// <summary>Smoothed horizontal speed in m/s, as observed from root motion.</summary>
        public float ObservedSpeed => _speed;

        /// <summary>Current blended aggression, 0 = patrolling, 1 = hunting.</summary>
        public float Aggression => _aggression;

        /// <summary>True while the head is tracking a world position.</summary>
        public bool HasLookTarget => _hasLookTarget;

        // =====================================================================
        // Lifecycle
        // =====================================================================

        void Awake()
        {
            _transform = transform;
            _lastPosition = _transform.position;

            if (!ResolveRig())
            {
                // Disable rather than limp along: a half-resolved rig would animate a
                // torso with no legs, which looks like a bug in the rig builder.
                VLog.Warn(LogCat.AI, $"MonsterAnimator on '{name}': incomplete creature rig, procedural animation disabled.", this);
                enabled = false;
                return;
            }

            CaptureRestPose();
        }

        void OnEnable()
        {
            // Re-enabling after a spawn or a teleport must not read a stale position:
            // one huge delta would spike the gait to full sprint for half a second.
            if (_transform != null) _lastPosition = _transform.position;
        }

        bool ResolveRig()
        {
            bool ok = true;

            _rig = Resolve(_transform, "Rig", ref ok);

            _torso = Resolve(_rig, "Torso", ref ok);
            _head = Resolve(_torso, "Head", ref ok);
            _eyeL = Resolve(_head, "Eye_L", ref ok);
            _eyeR = Resolve(_head, "Eye_R", ref ok);

            _armLUpper = Resolve(_torso, "Arm_L_Upper", ref ok);
            _armLLower = Resolve(_armLUpper, "Arm_L_Lower", ref ok);
            _armRUpper = Resolve(_torso, "Arm_R_Upper", ref ok);
            _armRLower = Resolve(_armRUpper, "Arm_R_Lower", ref ok);

            _hip = Resolve(_rig, "Hip", ref ok);
            _legLUpper = Resolve(_hip, "Leg_L_Upper", ref ok);
            _legLLower = Resolve(_legLUpper, "Leg_L_Lower", ref ok);
            _legRUpper = Resolve(_hip, "Leg_R_Upper", ref ok);
            _legRLower = Resolve(_legRUpper, "Leg_R_Lower", ref ok);

            return ok;
        }

        /// <summary>Null-tolerant child lookup: a missing parent poisons the flag instead of throwing.</summary>
        static Transform Resolve(Transform parent, string childName, ref bool ok)
        {
            if (parent == null)
            {
                ok = false;
                return null;
            }

            Transform found = parent.Find(childName);
            if (found == null) ok = false;
            return found;
        }

        void CaptureRestPose()
        {
            _rigRestPosition = _rig.localPosition;

            _torsoRest = _torso.localRotation;
            _headRest = _head.localRotation;
            _eyeLRest = _eyeL.localRotation;
            _eyeRRest = _eyeR.localRotation;

            _armLUpperRest = _armLUpper.localRotation;
            _armLLowerRest = _armLLower.localRotation;
            _armRUpperRest = _armRUpper.localRotation;
            _armRLowerRest = _armRLower.localRotation;

            _hipRest = _hip.localRotation;
            _legLUpperRest = _legLUpper.localRotation;
            _legLLowerRest = _legLLower.localRotation;
            _legRUpperRest = _legRUpper.localRotation;
            _legRLowerRest = _legRLower.localRotation;
        }

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>
        /// Point the head at a world position. Safe to call every frame with a moving
        /// target; the head eases toward it within its yaw/pitch limits.
        /// </summary>
        public void SetLookTarget(Vector3 worldPosition)
        {
            if (!IsFinite(worldPosition)) return;

            _lookTarget = worldPosition;
            _hasLookTarget = true;
        }

        /// <summary>Stop head tracking. The head drifts back to a slow idle scan.</summary>
        public void ClearLookTarget()
        {
            _hasLookTarget = false;
        }

        /// <summary>
        /// Snap into the attack pose: torso forward, arms back, then recover.
        /// Retriggering restarts the pose from the strike.
        /// </summary>
        public void TriggerLunge()
        {
            _lungeTime = 0f;
            _lungeActive = true;
        }

        /// <summary>
        /// Gait escalation, 0 = patrolling, 1 = hunting. Blends over ~a second, so a
        /// caller may set it every tick without popping the pose.
        /// </summary>
        public void SetAggression(float zeroToOne)
        {
            // A NaN here would propagate into every rotation and never wash out.
            if (float.IsNaN(zeroToOne)) return;

            _aggressionTarget = Mathf.Clamp01(zeroToOne);
        }

        // =====================================================================
        // Per-frame
        // =====================================================================

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // Any of these can be destroyed out from under us by a pooled despawn.
            if (_rig == null || _torso == null)
            {
                enabled = false;
                return;
            }

            UpdateAggression(dt);
            UpdateObservedSpeed(dt);
            UpdateLunge(dt);

            float idle = 1f - Mathf.Clamp01(_speed / Mathf.Max(0.01f, _idleSpeedThreshold));

            // Frequency is proportional to speed, so the feet appear to grip the
            // ground instead of skating; aggression multiplies it on top.
            float frequency = Mathf.Min(
                _speed * _strideHzPerSpeed * Mathf.Lerp(1f, _aggroFrequencyBoost, _aggression),
                _maxStrideHz);

            _stridePhase = Mathf.Repeat(_stridePhase + frequency * Tau * dt, Tau);
            _breathPhase = Mathf.Repeat(_breathPhase + _breathHz * Tau * dt, Tau);

            // Amplitude, not phase, is what settles at a standstill. Freezing the phase
            // alone would leave the creature stopped mid-step like a paused video.
            float gaitTarget = Mathf.Clamp01(_speed / Mathf.Max(0.01f, _referenceSpeed));
            _gait = Mathf.Lerp(_gait, gaitTarget, 1f - Mathf.Exp(-_strideSettleRate * dt));

            float swing = Mathf.Sin(_stridePhase);
            float limbScale = _gait * Mathf.Lerp(1f, 1f + _aggroSwingBoost, _aggression);

            ApplyBody(swing, idle);
            ApplyLegs(swing, limbScale);
            ApplyArms(swing, limbScale);
            ApplyHead(dt, idle);
            ApplyEyes(dt);
        }

        void UpdateAggression(float dt)
        {
            _aggression = Mathf.Lerp(_aggression, _aggressionTarget, 1f - Mathf.Exp(-_aggroBlendRate * dt));
        }

        void UpdateObservedSpeed(float dt)
        {
            Vector3 position = _transform.position;

            Vector3 delta = position - _lastPosition;
            delta.y = 0f; // Falling and stair steps are not stride.
            _lastPosition = position;

            float raw = delta.magnitude / dt;

            // A NavMesh snap, a vault, or a late packet on a client can move the root
            // metres in one frame. Holding the previous speed hides the discontinuity.
            if (raw > _teleportSpeed) raw = _speed;

            _speed = Mathf.Lerp(_speed, raw, 1f - Mathf.Exp(-_speedSmoothing * dt));
        }

        void UpdateLunge(float dt)
        {
            if (!_lungeActive)
            {
                _lungeWeight = 0f;
                return;
            }

            _lungeTime += dt;

            float strike = Mathf.Max(0.01f, _lungeStrikeSeconds);
            if (_lungeTime < strike)
            {
                // Ease-out: most of the travel happens in the first frames, which is
                // what sells it as a snap rather than a lean.
                float x = _lungeTime / strike;
                float inv = 1f - x;
                _lungeWeight = 1f - inv * inv;
                return;
            }

            float recover = Mathf.Max(0.01f, _lungeRecoverSeconds);
            float r = (_lungeTime - strike) / recover;

            if (r >= 1f)
            {
                _lungeActive = false;
                _lungeWeight = 0f;
                return;
            }

            _lungeWeight = 1f - r * r * (3f - 2f * r);
        }

        // =====================================================================
        // Pose
        // =====================================================================

        void ApplyBody(float swing, float idle)
        {
            float hunch = Mathf.Lerp(_hunchIdleDegrees, _hunchAggroDegrees, _aggression);
            float breathPitch = Mathf.Sin(_breathPhase) * _breathPitchDegrees * idle;
            float twist = Mathf.Sin(_breathPhase * 0.5f) * _breathTwistDegrees * idle;
            float roll = swing * _bodyRollDegrees * _gait;

            _torso.localRotation = _torsoRest * Quaternion.Euler(
                hunch + breathPitch + _lungeTorsoDegrees * _lungeWeight, twist, roll);

            // Hips counter-yaw against the shoulders — the single cheapest cue that a
            // walk has weight behind it.
            if (_hip != null)
            {
                _hip.localRotation = _hipRest * Quaternion.Euler(0f, -swing * _hipYawDegrees * _gait, 0f);
            }

            // Two dips per stride cycle: |sin| peaks at each contact.
            float footfall = -Mathf.Abs(swing) * _strideBobMetres * _gait;
            float breath = Mathf.Sin(_breathPhase) * _breathBobMetres * idle;

            Vector3 rigPosition = _rigRestPosition;
            rigPosition.y += footfall + breath;
            rigPosition.z += _lungeReachMetres * _lungeWeight;
            _rig.localPosition = rigPosition;
        }

        void ApplyLegs(float swing, float limbScale)
        {
            if (_legLUpper == null || _legRUpper == null) return;

            float amplitude = _legSwingDegrees * limbScale;
            float legL = swing * amplitude;
            float legR = -legL;

            _legLUpper.localRotation = _legLUpperRest * Quaternion.Euler(legL, 0f, 0f);
            _legRUpper.localRotation = _legRUpperRest * Quaternion.Euler(legR, 0f, 0f);

            // The knee only folds on the trailing half of the cycle. A knee that bends
            // both ways instantly reads as a puppet.
            if (_legLLower != null)
            {
                float kneeL = Mathf.Max(0f, swing) * _kneeBendDegrees * _gait;
                _legLLower.localRotation = _legLLowerRest * Quaternion.Euler(kneeL, 0f, 0f);
            }

            if (_legRLower != null)
            {
                float kneeR = Mathf.Max(0f, -swing) * _kneeBendDegrees * _gait;
                _legRLower.localRotation = _legRLowerRest * Quaternion.Euler(kneeR, 0f, 0f);
            }
        }

        void ApplyArms(float swing, float limbScale)
        {
            if (_armLUpper == null || _armRUpper == null) return;

            float amplitude = _armSwingDegrees * limbScale;

            // Two incommensurate multipliers, so the wobble never resolves into a loop
            // the player can read. Only audible at high aggression.
            float jitter = _aggroJitterDegrees * _aggression * _gait;
            float jitterL = Mathf.Sin(_stridePhase * 1.73f + 0.6f) * jitter;
            float jitterR = Mathf.Sin(_stridePhase * 2.11f + 2.4f) * jitter;

            float lungeArm = _lungeArmDegrees * _lungeWeight;

            // Counter-swing: the left arm opposes the left leg.
            float armL = -swing * amplitude + jitterL + lungeArm;
            float armR = swing * amplitude + jitterR + lungeArm;

            _armLUpper.localRotation = _armLUpperRest * Quaternion.Euler(armL, 0f, 0f);
            _armRUpper.localRotation = _armRUpperRest * Quaternion.Euler(armR, 0f, 0f);

            float baseFlex = _elbowBendDegrees * Mathf.Lerp(0.35f, 1f, _aggression);
            float lungeElbow = _lungeElbowDegrees * _lungeWeight;

            if (_armLLower != null)
            {
                float flexL = baseFlex + Mathf.Max(0f, -armL) * 0.35f + lungeElbow;
                _armLLower.localRotation = _armLLowerRest * Quaternion.Euler(-flexL, 0f, 0f);
            }

            if (_armRLower != null)
            {
                float flexR = baseFlex + Mathf.Max(0f, -armR) * 0.35f + lungeElbow;
                _armRLower.localRotation = _armRLowerRest * Quaternion.Euler(-flexR, 0f, 0f);
            }
        }

        void ApplyHead(float dt, float idle)
        {
            if (_head == null) return;

            float targetYaw = 0f;
            float targetPitch = 0f;

            if (_hasLookTarget)
            {
                // Resolved in the torso's space, and only after the torso has already
                // been posed this frame — otherwise the hunch would drag the aim with
                // it and the head would never actually point at anything.
                Vector3 local = _torso.InverseTransformDirection(_lookTarget - _head.position);
                float length = local.magnitude;

                if (length > 1e-4f)
                {
                    local /= length;

                    targetYaw = Mathf.Clamp(
                        Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -_headYawLimit, _headYawLimit);

                    targetPitch = Mathf.Clamp(
                        -Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg, -_headPitchLimit, _headPitchLimit);
                }
            }
            else
            {
                // A dead-still head reads as "switched off"; a slow scan reads as alive.
                targetYaw = Mathf.Sin(_breathPhase * 0.37f) * _idleScanDegrees * idle;
            }

            targetPitch += _lungeHeadDegrees * _lungeWeight;

            float rate = Mathf.Lerp(_headTrackRate, _headTrackRate * 2.2f, _aggression);
            float k = 1f - Mathf.Exp(-rate * dt);

            _headYaw = Mathf.Lerp(_headYaw, targetYaw, k);
            _headPitch = Mathf.Lerp(_headPitch, targetPitch, k);

            _head.localRotation = _headRest * Quaternion.Euler(_headPitch, _headYaw, 0f);
        }

        void ApplyEyes(float dt)
        {
            if (_eyeL == null || _eyeR == null) return;

            float targetYaw = 0f;
            float targetPitch = 0f;

            if (_hasLookTarget)
            {
                // Relative to the head, which is now posed — so the eyes take up the
                // residual the clamped neck could not, and lead the turn.
                Vector3 local = _head.InverseTransformDirection(_lookTarget - _head.position);
                float length = local.magnitude;

                if (length > 1e-4f)
                {
                    local /= length;

                    targetYaw = Mathf.Clamp(
                        Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -_eyeYawLimit, _eyeYawLimit);

                    targetPitch = Mathf.Clamp(
                        -Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg, -_eyePitchLimit, _eyePitchLimit);
                }
            }

            float k = 1f - Mathf.Exp(-_eyeTrackRate * dt);
            _eyeYaw = Mathf.Lerp(_eyeYaw, targetYaw, k);
            _eyePitch = Mathf.Lerp(_eyePitch, targetPitch, k);

            Quaternion offset = Quaternion.Euler(_eyePitch, _eyeYaw, 0f);
            _eyeL.localRotation = _eyeLRest * offset;
            _eyeR.localRotation = _eyeRRest * offset;
        }

        static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
                && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        }
    }
}
