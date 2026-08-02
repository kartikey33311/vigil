// -----------------------------------------------------------------------------
// Vigil — first-person camera and look control.
//
// Owner-only. Runs in LateUpdate off already-simulated state and interpolates
// with SimClock.Alpha, so 30 Hz simulation does not read as 30 fps.
//
// The camera is doing narrative work, not just presentation:
//
//   * Breathing sway scales with (1 - composure), so a frightened player's view
//     becomes physically unsteady. The player feels the meter before they read it.
//   * FOV widens slightly while sprinting, which makes fleeing feel faster than
//     the actual speed change justifies.
//   * Head bob is gait-scaled and amplitude-limited — bob is the most common
//     source of motion sickness in first-person horror, so it is deliberately
//     subtle and fully disableable.
// -----------------------------------------------------------------------------

using UnityEngine;
using Vigil.Core.Services;
using Vigil.Core.Simulation;
using Vigil.Data;

namespace Vigil.Gameplay.Player
{
    [DefaultExecutionOrder(100)]
    public sealed class PlayerCameraRig : MonoBehaviour
    {
        [Header("Look")]
        [SerializeField, Range(0.05f, 2f)] float _mouseSensitivity = 0.22f;
        [SerializeField, Range(30f, 89f)] float _pitchLimit = 85f;
        [SerializeField] bool _invertY = false;

        [Header("Camera")]
        [SerializeField, Range(50f, 100f)] float _baseFieldOfView = 68f;
        [SerializeField, Range(0f, 20f)] float _sprintFovBoost = 7f;
        [SerializeField, Min(0.01f)] float _standEyeHeight = 1.65f;
        [SerializeField, Min(0.01f)] float _crouchEyeHeight = 0.95f;

        [Header("Feel")]
        [SerializeField, Range(0f, 0.12f), Tooltip("Head bob amplitude in metres. Keep this small — bob is the leading cause of motion sickness in first-person horror.")]
        float _bobAmplitude = 0.035f;

        [SerializeField, Range(0f, 20f)] float _bobFrequency = 9f;

        [SerializeField, Range(0f, 0.1f), Tooltip("Breathing sway at zero composure. This is how the player FEELS fear before they read the meter.")]
        float _panicSwayAmplitude = 0.028f;

        [SerializeField] bool _enableHeadBob = true;

        PlayerCharacter _character;
        Transform _head;
        Camera _camera;
        SimClock _clock;

        float _yaw;
        float _pitch;
        float _bobPhase;
        float _swayPhase;
        float _currentEyeHeight;
        float _shakeMagnitude;
        float _shakeDecay = 4f;

        bool _cursorLocked;

        /// <summary>Body yaw in degrees. Read by <see cref="PlayerCharacter"/> when sampling input.</summary>
        public float Yaw => _yaw;

        /// <summary>Head pitch in degrees.</summary>
        public float Pitch => _pitch;

        public Camera Camera => _camera;

        void Awake()
        {
            _character = GetComponent<PlayerCharacter>();
            _currentEyeHeight = _standEyeHeight;
            _yaw = transform.eulerAngles.y;
        }

        void Start()
        {
            // Only the owning client gets a camera. Two enabled cameras render the
            // scene twice and halve the framerate; two AudioListeners make Unity
            // spam warnings and pick one arbitrarily. Both must be switched OFF on
            // remote proxies, not merely left unused.
            if (_character != null && !_character.IsOwner)
            {
                Camera[] cameras = GetComponentsInChildren<Camera>(true);
                for (int i = 0; i < cameras.Length; i++) cameras[i].enabled = false;

                AudioListener[] listeners = GetComponentsInChildren<AudioListener>(true);
                for (int i = 0; i < listeners.Length; i++) listeners[i].enabled = false;

                enabled = false;
                return;
            }

            _clock = Services.IsReady ? Services.TryGet<SimClock>() : null;

            EnsureRig();
            LockCursor(true);
            BindAudio();
        }

        void BindAudio()
        {
            _audioService = FindAnyObjectByType<Vigil.Audio.VigilAudioService>();
            if (_audioService == null) return;

            // The audio layer has no idea what a player is, so the owner tells it
            // which composure stream belongs to the person holding the controller.
            // Without this the host would breathe in time with whichever teammate
            // happened to publish last.
            Vigil.Audio.PlayerBodyAudio body = FindAnyObjectByType<Vigil.Audio.PlayerBodyAudio>();
            if (body != null && _character != null) body.SetLocalPlayer(_character.EntityId);
        }

        Vigil.Audio.VigilAudioService _audioService;

        void EnsureRig()
        {
            _head = transform.Find("Head");
            if (_head == null)
            {
                GameObject headGo = new GameObject("Head");
                headGo.transform.SetParent(transform, false);
                _head = headGo.transform;
            }

            _head.localPosition = new Vector3(0f, _standEyeHeight, 0f);

            _camera = _head.GetComponentInChildren<Camera>();
            if (_camera == null)
            {
                // Adopt the scene's existing camera rather than creating a second
                // one — two enabled cameras render twice and halve the framerate,
                // which reads as "the game is badly optimised".
                Camera existing = Camera.main;
                if (existing != null && existing.transform.parent == null)
                {
                    _camera = existing;
                    _camera.transform.SetParent(_head, false);
                    _camera.transform.localPosition = Vector3.zero;
                    _camera.transform.localRotation = Quaternion.identity;
                }
                else
                {
                    GameObject camGo = new GameObject("PlayerCamera");
                    camGo.transform.SetParent(_head, false);
                    _camera = camGo.AddComponent<Camera>();
                    camGo.AddComponent<AudioListener>();
                }
            }

            _camera.fieldOfView = _baseFieldOfView;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 300f;
            _camera.tag = "MainCamera";
        }

        void Update()
        {
            if (_character != null && !_character.IsOwner) return;

            HandleCursor();
            if (_cursorLocked) ReadLook();
        }

        void HandleCursor()
        {
            // Escape releases the cursor so the player can reach the menu or alt-tab
            // without the window swallowing the pointer.
            if (Input.GetKeyDown(KeyCode.Escape)) LockCursor(false);
            if (!_cursorLocked && Input.GetMouseButtonDown(0)) LockCursor(true);
        }

        void LockCursor(bool locked)
        {
            _cursorLocked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        void ReadLook()
        {
            float mx = Input.GetAxisRaw("Mouse X");
            float my = Input.GetAxisRaw("Mouse Y");

            _yaw += mx * _mouseSensitivity * 60f * Time.deltaTime * 3f;
            _pitch += (_invertY ? my : -my) * _mouseSensitivity * 60f * Time.deltaTime * 3f;

            _pitch = Mathf.Clamp(_pitch, -_pitchLimit, _pitchLimit);

            if (_yaw > 360f) _yaw -= 360f;
            else if (_yaw < 0f) _yaw += 360f;
        }

        void LateUpdate()
        {
            if (_head == null || (_character != null && !_character.IsOwner)) return;

            float dt = Time.deltaTime;

            // --- eye height (crouch) -------------------------------------------
            bool crouched = _character != null && _character.CurrentGait == Gait.Crouch;
            float targetEye = crouched ? _crouchEyeHeight : _standEyeHeight;
            _currentEyeHeight = Mathf.MoveTowards(_currentEyeHeight, targetEye, 4f * dt);

            // --- head bob -------------------------------------------------------
            Vector3 offset = Vector3.zero;

            if (_enableHeadBob && _character != null)
            {
                Gait gait = _character.CurrentGait;

                if (gait != Gait.Idle)
                {
                    float speedScale = gait == Gait.Sprint ? 1.45f : (gait == Gait.Crouch ? 0.55f : 1f);
                    _bobPhase += dt * _bobFrequency * speedScale;

                    offset.y += Mathf.Sin(_bobPhase) * _bobAmplitude * speedScale;
                    offset.x += Mathf.Cos(_bobPhase * 0.5f) * _bobAmplitude * 0.5f * speedScale;
                }
                else
                {
                    // Settle back to centre rather than freezing mid-bob.
                    _bobPhase = Mathf.Lerp(_bobPhase, 0f, 6f * dt);
                }
            }

            // --- breathing sway from composure ----------------------------------
            float composure01 = 1f;
            if (_character != null) composure01 = Mathf.Clamp01(_character.Composure / 100f);

            float panic = 1f - composure01;
            if (panic > 0.01f)
            {
                _swayPhase += dt * Mathf.Lerp(1.2f, 3.4f, panic);

                offset.y += Mathf.Sin(_swayPhase * 2.1f) * _panicSwayAmplitude * panic;
                offset.x += Mathf.Sin(_swayPhase * 1.3f) * _panicSwayAmplitude * panic * 0.7f;
            }

            // --- damage / event shake -------------------------------------------
            if (_shakeMagnitude > 0.001f)
            {
                offset.x += (Mathf.PerlinNoise(Time.time * 26f, 0f) - 0.5f) * _shakeMagnitude;
                offset.y += (Mathf.PerlinNoise(0f, Time.time * 26f) - 0.5f) * _shakeMagnitude;
                _shakeMagnitude = Mathf.MoveTowards(_shakeMagnitude, 0f, _shakeDecay * dt);
            }

            _head.localPosition = new Vector3(offset.x, _currentEyeHeight + offset.y, offset.z);
            _head.localRotation = Quaternion.Euler(_pitch, 0f, 0f);

            // Body yaw is applied here for the owner so the view is frame-rate
            // smooth; the server re-derives it from the commanded yaw in the input.
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);

            // Occlusion and body audio both key off where the ear actually is, which
            // is the head after bob and sway — not the character's feet.
            if (_audioService != null) _audioService.SetListenerPosition(_head.position);

            // --- FOV kick --------------------------------------------------------
            if (_camera != null)
            {
                bool sprinting = _character != null && _character.CurrentGait == Gait.Sprint;
                float targetFov = _baseFieldOfView + (sprinting ? _sprintFovBoost : 0f);
                _camera.fieldOfView = Mathf.Lerp(_camera.fieldOfView, targetFov, 6f * dt);
            }
        }

        /// <summary>Adds camera shake. Driven by damage and by Director set-pieces.</summary>
        public void AddShake(float magnitude, float decay = 4f)
        {
            _shakeMagnitude = Mathf.Max(_shakeMagnitude, magnitude);
            _shakeDecay = decay;
        }
    }
}
