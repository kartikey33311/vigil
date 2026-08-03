// -----------------------------------------------------------------------------
// Vigil — fear-driven post-processing.
//
// Owner-only. Builds its own runtime VolumeProfile (never an asset) and layers it
// on top of the level's global volume at a higher priority, so a level artist can
// keep authoring the room's look while this file owns the player's *state*.
//
// The grammar it is speaking:
//
//   * Vignette closes in as composure falls and as the antagonist gets nearer. It
//     is the cheapest way to say "your world is getting smaller" without taking
//     control away from the player.
//   * Chromatic aberration is the chase signature. It is near zero the rest of the
//     time on purpose — an effect that is always on stops meaning anything.
//   * Lens distortion breathes, slowly, only when composure is low. The player
//     reads it as their own body before they read it as an effect.
//   * Saturation drains toward grey with fear, which makes the eventual return of
//     colour do the emotional work of "you got away".
//   * A heartbeat pulses the vignette. Deliberately tiny — a pulse you can
//     consciously see is nauseating inside a minute. Felt, not noticed.
//
// Chase transitions are ASYMMETRIC: ~0.3s in, several seconds out. Snapping back
// to calm the instant the AI drops the chase makes the chase feel weightless;
// relief has to be earned by outlasting the decay.
//
// ---------------------------------------------------------------------------
// WHY THIS FILE USES REFLECTION
//
// Vigil.Gameplay.asmdef does NOT reference Unity.RenderPipelines.Core.Runtime or
// Unity.RenderPipelines.Universal.Runtime (only Vigil.Editor does), and the
// assembly layout is frozen. Naming Volume/VolumeProfile/Vignette directly here
// would not compile, and a compile error in Vigil.Gameplay takes Vigil.UI,
// Vigil.Bootstrap and Vigil.Editor down with it.
//
// So the URP volume stack is bound by reflection ONCE, in Start, and every
// per-frame write goes through a cached strongly-typed Action<float> — no boxing,
// no reflection, and no allocation in Update. If URP is absent or a parameter has
// been renamed, the matching setter is simply null and that effect no-ops.
//
// If the asmdef ever gains those two references, this file can be flattened to
// direct API calls with no behavioural change.
// -----------------------------------------------------------------------------

using System;
using System.Reflection;
using UnityEngine;
using Vigil.Core.Diagnostics;
using Vigil.Core.Events;
using Vigil.Core.Services;

namespace Vigil.Gameplay.Player
{
    /// <summary>
    /// Owner-only post-processing that turns composure, threat proximity and chase
    /// state into vignette, chromatic aberration, lens distortion and desaturation.
    /// </summary>
    /// <remarks>
    /// Runs after <see cref="PlayerCameraRig"/> (execution order 100) so the camera
    /// it configures already exists.
    /// </remarks>
    [DefaultExecutionOrder(200)]
    public sealed class HorrorEffects : MonoBehaviour
    {
        // =====================================================================
        // Inspector
        // =====================================================================

        [Header("Volume")]
        [SerializeField, Tooltip("Must beat the level's global volume so this layers on top of it rather than under it.")]
        float _volumePriority = 100f;

        [SerializeField, Tooltip("Force post-processing on for the owning camera. Without it none of this renders and the bug looks like 'the effects are broken'.")]
        bool _forceEnablePostProcessing = true;

        [SerializeField, Min(1f), Tooltip("Composure value that counts as fully calm. Mirrors ComposureConfig.MaxComposure.")]
        float _maxComposure = 100f;

        [Header("Vignette")]
        [SerializeField, Range(0f, 0.5f)] float _vignetteBase = 0.16f;
        [SerializeField, Range(0f, 0.6f)] float _vignetteFromPanic = 0.20f;
        [SerializeField, Range(0f, 0.6f)] float _vignetteFromThreat = 0.16f;
        [SerializeField, Range(0f, 0.6f)] float _vignetteFromChase = 0.14f;
        [SerializeField, Range(0f, 0.6f)] float _vignetteWhenDowned = 0.34f;

        [SerializeField, Range(0.3f, 0.95f), Tooltip("Hard ceiling. A vignette near 1 is a black screen, which reads as a rendering bug rather than as fear.")]
        float _vignetteCeiling = 0.78f;

        [Header("Chromatic Aberration")]
        [SerializeField, Range(0f, 0.2f)] float _chromaticIdle = 0.02f;
        [SerializeField, Range(0f, 1f)] float _chromaticFromChase = 0.55f;
        [SerializeField, Range(0f, 1f)] float _chromaticFromThreat = 0.18f;
        [SerializeField, Range(0f, 1f)] float _chromaticFromPanic = 0.10f;

        [Header("Lens Distortion")]
        [SerializeField, Range(0f, 0.35f), Tooltip("Peak breathing swing at zero composure. Above ~0.15 this stops reading as breathing and starts reading as a drunk filter.")]
        float _lensBreathAmplitude = 0.10f;

        [SerializeField, Range(0f, 0.4f)] float _lensWhenDowned = 0.12f;
        [SerializeField, Range(4f, 30f), Tooltip("Breaths per minute at full composure.")] float _breathRateCalm = 13f;
        [SerializeField, Range(10f, 60f), Tooltip("Breaths per minute at zero composure.")] float _breathRatePanic = 34f;

        [Header("Colour")]
        [SerializeField, Range(0f, 100f)] float _saturationDrain = 62f;
        [SerializeField, Range(0f, 100f)] float _downedSaturationDrain = 38f;
        [SerializeField, Range(0f, 3f)] float _downedExposureDrop = 0.75f;

        [Header("Heartbeat")]
        [SerializeField, Range(0f, 0.15f), Tooltip("Vignette added at the peak of a beat. Keep this small — a pulse the player can consciously see is nauseating within a minute.")]
        float _heartbeatAmplitude = 0.045f;

        [SerializeField, Range(0f, 0.9f), Tooltip("Fear below this produces no pulse at all, so a calm player is never nagged by it.")]
        float _heartbeatOnset = 0.22f;

        [SerializeField, Range(40f, 100f)] float _heartRateCalm = 58f;
        [SerializeField, Range(90f, 200f)] float _heartRatePanic = 168f;

        [Header("Response")]
        [SerializeField, Min(0.05f), Tooltip("Seconds to reach full 'hunted' when a chase starts.")]
        float _chaseAttackSeconds = 0.3f;

        [SerializeField, Min(0.1f), Tooltip("Seconds to decay back to calm after a chase ends. Long on purpose: relief should be earned.")]
        float _chaseReleaseSeconds = 5.5f;

        [SerializeField, Min(0.05f)] float _threatSlewPerSecond = 2f;
        [SerializeField, Min(0.1f)] float _composureSlew = 3f;

        // =====================================================================
        // Runtime state
        // =====================================================================

        PlayerCharacter _character;
        PlayerCameraRig _rig;

        GameObject _volumeObject;
        Component _volumeComponent;
        ScriptableObject _profile;

        // VolumeComponents this instance created and therefore must destroy.
        readonly ScriptableObject[] _ownedComponents = new ScriptableObject[4];

        // Cached, strongly-typed parameter setters. Null means "not available" and
        // the corresponding effect silently does nothing.
        Action<float> _vignetteIntensity;
        Action<float> _vignetteSmoothness;
        Action<float> _chromaticIntensity;
        Action<float> _lensIntensity;
        Action<float> _lensScale;
        Action<float> _saturation;
        Action<float> _postExposure;

        IDisposable _chaseStartedSub;
        IDisposable _chaseEndedSub;
        IDisposable _downedSub;
        IDisposable _composureSub;

        float _composure01 = 1f;
        float _composureTarget = 1f;
        bool _composureFromEvents;

        float _threat01;
        float _threatTarget;

        float _chase01;
        int _chaseCount;

        float _downed01;
        bool _downedLatch;

        float _heartPhase;
        float _breathPhase;

        bool _bound;

        const string CoreAssembly = "Unity.RenderPipelines.Core.Runtime";
        const string UniversalAssembly = "Unity.RenderPipelines.Universal.Runtime";

        static readonly Type[] FloatArg = { typeof(float) };
        static readonly Type[] ProfileAddArgs = { typeof(Type), typeof(bool) };

        // =====================================================================
        // Lifecycle
        // =====================================================================

        void Awake()
        {
            _character = GetComponent<PlayerCharacter>();
            _rig = GetComponent<PlayerCameraRig>();
        }

        void Start()
        {
            // Remote proxies have no camera and no composure worth reading, and a
            // second global volume from every teammate would stack on the local
            // player's view.
            if (_character != null && !_character.IsOwner)
            {
                enabled = false;
                return;
            }

            if (_character != null) _composureTarget = Mathf.Clamp01(_character.Composure / Mathf.Max(1f, _maxComposure));
            _composure01 = _composureTarget;

            _bound = BuildRuntimeVolume();
            if (!_bound)
            {
                VLog.Warn(LogCat.Gameplay,
                    "HorrorEffects: URP volume stack not resolvable at runtime; post-processing fear response is disabled.", this);
            }

            SubscribeToEvents();
        }

        void OnDestroy()
        {
            // A forgotten unsubscribe keeps this destroyed MonoBehaviour alive and
            // throws on the next publish — see the note at the top of EventBus.
            if (_chaseStartedSub != null) { _chaseStartedSub.Dispose(); _chaseStartedSub = null; }
            if (_chaseEndedSub != null) { _chaseEndedSub.Dispose(); _chaseEndedSub = null; }
            if (_downedSub != null) { _downedSub.Dispose(); _downedSub = null; }
            if (_composureSub != null) { _composureSub.Dispose(); _composureSub = null; }

            TeardownRuntimeVolume();
        }

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>
        /// How close the antagonist is, 0 = nowhere near, 1 = on top of the player.
        /// Fed by gameplay; blended into vignette and chromatic aberration.
        /// </summary>
        public void SetThreatProximity(float zeroToOne)
        {
            // NaN would poison every downstream Lerp for the rest of the session.
            if (float.IsNaN(zeroToOne) || float.IsInfinity(zeroToOne)) return;
            _threatTarget = Mathf.Clamp01(zeroToOne);
        }

        // =====================================================================
        // Events
        // =====================================================================

        void SubscribeToEvents()
        {
            IEventBus events = Services.TryGet<IEventBus>();
            if (events == null) return;

            _chaseStartedSub = events.Subscribe<ChaseStartedEvent>(OnChaseStarted);
            _chaseEndedSub = events.Subscribe<ChaseEndedEvent>(OnChaseEnded);
            _downedSub = events.Subscribe<PlayerDownedEvent>(OnPlayerDowned);
            _composureSub = events.Subscribe<CompositionChangedEvent>(OnComposureChanged);
        }

        /// <summary>True when an event about <paramref name="playerId"/> concerns this player.</summary>
        bool IsLocalPlayer(ulong playerId)
        {
            // With no PlayerCharacter this is a bare test scene, so take everything.
            return _character == null || playerId == _character.EntityId;
        }

        void OnChaseStarted(ChaseStartedEvent evt)
        {
            if (!IsLocalPlayer(evt.TargetId)) return;

            // Counted, not latched: two hunters that both give up would otherwise
            // have the first ChaseEnded drop the player out of the chase early.
            _chaseCount++;
        }

        void OnChaseEnded(ChaseEndedEvent evt)
        {
            if (!IsLocalPlayer(evt.TargetId)) return;
            if (_chaseCount > 0) _chaseCount--;
        }

        void OnPlayerDowned(PlayerDownedEvent evt)
        {
            if (!IsLocalPlayer(evt.PlayerId)) return;

            _downedLatch = true;

            // Jump partway immediately. Going down is an impact, not a fade.
            if (_downed01 < 0.55f) _downed01 = 0.55f;
        }

        void OnComposureChanged(CompositionChangedEvent evt)
        {
            if (!IsLocalPlayer(evt.PlayerId)) return;

            _composureFromEvents = true;
            _composureTarget = Mathf.Clamp01(evt.Composure / Mathf.Max(1f, _maxComposure));
        }

        // =====================================================================
        // Per-frame drive
        // =====================================================================

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            AdvanceEnvelopes(dt);
            if (_bound) ApplyEffects(dt);
        }

        void AdvanceEnvelopes(float dt)
        {
            // Fast in, slow out.
            bool hunted = _chaseCount > 0;
            float chaseRate = hunted
                ? 1f / Mathf.Max(0.05f, _chaseAttackSeconds)
                : 1f / Mathf.Max(0.1f, _chaseReleaseSeconds);
            _chase01 = Mathf.MoveTowards(_chase01, hunted ? 1f : 0f, chaseRate * dt);

            // The replicated flag is the source of truth so a revive clears this by
            // itself; the event only supplies the immediate kick.
            bool downed;
            if (_character != null)
            {
                downed = _character.IsDowned;
                if (!downed) _downedLatch = false;
            }
            else
            {
                downed = _downedLatch;
            }

            float downedRate = downed ? (1f / 0.9f) : (1f / 2.2f);
            _downed01 = Mathf.MoveTowards(_downed01, downed ? 1f : 0f, downedRate * dt);

            // Slewed so a coarse or bursty proximity feed cannot pop the vignette.
            _threat01 = Mathf.MoveTowards(_threat01, _threatTarget, _threatSlewPerSecond * dt);

            if (!_composureFromEvents && _character != null)
            {
                _composureTarget = Mathf.Clamp01(_character.Composure / Mathf.Max(1f, _maxComposure));
            }

            // Exponential rather than linear so the response is frame-rate independent.
            _composure01 = Mathf.Lerp(_composure01, _composureTarget, 1f - Mathf.Exp(-_composureSlew * dt));
        }

        void ApplyEffects(float dt)
        {
            float panic = Mathf.Clamp01(1f - _composure01);

            // Chase is discounted slightly: being hunted while composed should not
            // read as loudly as being hunted while falling apart.
            float fear = Mathf.Clamp01(Mathf.Max(panic, Mathf.Max(_threat01, _chase01 * 0.9f)));

            float heartbeat = AdvanceHeartbeat(dt, fear);

            if (_vignetteIntensity != null)
            {
                float v = _vignetteBase
                        + _vignetteFromPanic * panic
                        + _vignetteFromThreat * _threat01
                        + _vignetteFromChase * _chase01
                        + _vignetteWhenDowned * _downed01
                        + heartbeat;

                _vignetteIntensity(Mathf.Clamp(v, 0f, _vignetteCeiling));
            }

            // A softer edge at high fear keeps the closing vignette from reading as
            // a hard black frame pasted over the game.
            if (_vignetteSmoothness != null) _vignetteSmoothness(Mathf.Lerp(0.32f, 0.55f, fear));

            if (_chromaticIntensity != null)
            {
                float ca = _chromaticIdle
                         + _chromaticFromChase * _chase01
                         + _chromaticFromThreat * _threat01
                         + _chromaticFromPanic * panic;

                _chromaticIntensity(Mathf.Clamp01(ca));
            }

            if (_lensIntensity != null)
            {
                _breathPhase += dt * (Mathf.Lerp(_breathRateCalm, _breathRatePanic, panic) / 60f);
                _breathPhase -= Mathf.Floor(_breathPhase);

                float breath = Mathf.Sin(_breathPhase * Mathf.PI * 2f) * _lensBreathAmplitude * panic;
                breath -= _lensWhenDowned * _downed01;
                breath = Mathf.Clamp(breath, -1f, 1f);

                _lensIntensity(breath);

                // Pincushion pulls the image in and would expose the frame edge; a
                // matching overscan hides it.
                if (_lensScale != null) _lensScale(1f + Mathf.Abs(breath) * 0.35f);
            }

            if (_saturation != null)
            {
                float grey = Mathf.Clamp01(Mathf.Max(panic, Mathf.Max(_threat01 * 0.45f, _chase01 * 0.5f)));
                float sat = -(_saturationDrain * grey) - (_downedSaturationDrain * _downed01);
                _saturation(Mathf.Clamp(sat, -100f, 0f));
            }

            if (_postExposure != null) _postExposure(-_downedExposureDrop * _downed01);
        }

        /// <summary>Advances the heart phase and returns the vignette bump for this frame.</summary>
        float AdvanceHeartbeat(float dt, float fear)
        {
            float beatsPerSecond = Mathf.Lerp(_heartRateCalm, _heartRatePanic, fear) / 60f;

            // Phase keeps running below the onset so the pulse fades in mid-rhythm
            // instead of restarting from a thump the moment fear crosses the line.
            _heartPhase += dt * beatsPerSecond;
            _heartPhase -= Mathf.Floor(_heartPhase);

            float audible = Mathf.InverseLerp(_heartbeatOnset, 1f, fear);
            if (audible <= 0f) return 0f;

            // lub-DUB: two offset gaussians, the second quieter and slightly late.
            float beat = Thump(_heartPhase, 0.02f, 0.055f) + 0.62f * Thump(_heartPhase, 0.30f, 0.055f);
            return beat * _heartbeatAmplitude * audible;
        }

        /// <summary>Gaussian bump on a wrapped 0..1 phase, so the pulse is continuous across the seam.</summary>
        static float Thump(float phase, float centre, float width)
        {
            float d = phase - centre;
            if (d > 0.5f) d -= 1f;
            else if (d < -0.5f) d += 1f;

            return Mathf.Exp(-(d * d) / (2f * width * width));
        }

        // =====================================================================
        // Runtime URP volume construction
        // =====================================================================

        bool BuildRuntimeVolume()
        {
            Type volumeType = ResolveType("UnityEngine.Rendering.Volume", CoreAssembly);
            Type profileType = ResolveType("UnityEngine.Rendering.VolumeProfile", CoreAssembly);
            if (volumeType == null || profileType == null) return false;

            MethodInfo add = GetMethod(profileType, "Add", ProfileAddArgs);
            if (add == null) return false;

            _profile = ScriptableObject.CreateInstance(profileType);
            if (_profile == null) return false;

            _profile.name = "HorrorEffects (Runtime)";

            // Not an asset and must never become one: HideAndDontSave keeps it out of
            // the project window and out of any scene save. OnDestroy owns its life.
            _profile.hideFlags = HideFlags.HideAndDontSave;

            object vignette = AddVolumeComponent(add, "UnityEngine.Rendering.Universal.Vignette", 0);
            object chromatic = AddVolumeComponent(add, "UnityEngine.Rendering.Universal.ChromaticAberration", 1);
            object lens = AddVolumeComponent(add, "UnityEngine.Rendering.Universal.LensDistortion", 2);
            object colour = AddVolumeComponent(add, "UnityEngine.Rendering.Universal.ColorAdjustments", 3);

            _vignetteIntensity = BindFloat(vignette, "intensity");
            _vignetteSmoothness = BindFloat(vignette, "smoothness");
            _chromaticIntensity = BindFloat(chromatic, "intensity");
            _lensIntensity = BindFloat(lens, "intensity");
            _lensScale = BindFloat(lens, "scale");
            _saturation = BindFloat(colour, "saturation");
            _postExposure = BindFloat(colour, "postExposure");

            Camera camera = _rig != null ? _rig.Camera : null;
            if (camera == null) camera = Camera.main;

            int layer = PrepareCamera(camera);

            _volumeObject = new GameObject("HorrorEffectsVolume");
            _volumeObject.transform.SetParent(transform, false);

            // The volume must sit on a layer the camera's volume mask actually
            // includes. The player root is on the "Player" layer, which URP's
            // default mask does not, so inheriting it would make this whole file a
            // no-op with nothing in the console to explain why.
            _volumeObject.layer = layer;

            _volumeComponent = _volumeObject.AddComponent(volumeType);
            if (_volumeComponent == null) return false;

            SetMember(_volumeComponent, "isGlobal", true);
            SetMember(_volumeComponent, "priority", _volumePriority);
            SetMember(_volumeComponent, "weight", 1f);
            SetMember(_volumeComponent, "blendDistance", 0f);
            SetMember(_volumeComponent, "sharedProfile", _profile);

            return _vignetteIntensity != null
                || _chromaticIntensity != null
                || _lensIntensity != null
                || _saturation != null;
        }

        object AddVolumeComponent(MethodInfo add, string typeName, int slot)
        {
            Type type = ResolveType(typeName, UniversalAssembly);
            if (type == null) return null;

            object created;
            try
            {
                created = add.Invoke(_profile, new object[] { type, true });
            }
            catch (Exception ex)
            {
                VLog.Warn(LogCat.Gameplay, $"HorrorEffects: could not add {typeName} — {ex.Message}", this);
                return null;
            }

            ScriptableObject component = created as ScriptableObject;
            if (component == null) return null;

            component.hideFlags = HideFlags.HideAndDontSave;
            SetMember(component, "active", true);

            if (slot >= 0 && slot < _ownedComponents.Length) _ownedComponents[slot] = component;
            return component;
        }

        /// <summary>
        /// Turns post-processing on for the owning camera and returns a layer the
        /// camera's volume mask accepts.
        /// </summary>
        int PrepareCamera(Camera camera)
        {
            // URP's default volume mask is the Default layer, so that is the safe
            // fallback when the additional camera data cannot be read.
            const int fallbackLayer = 0;

            if (camera == null) return fallbackLayer;

            Type dataType = ResolveType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData", UniversalAssembly);
            if (dataType == null) return fallbackLayer;

            Component data = camera.GetComponent(dataType);
            if (data == null) return fallbackLayer;

            if (_forceEnablePostProcessing) SetMember(data, "renderPostProcessing", true);

            object raw = GetMember(data, "volumeLayerMask");
            int mask;

            if (raw is LayerMask) mask = ((LayerMask)raw).value;
            else if (raw is int) mask = (int)raw;
            else return fallbackLayer;

            if (mask == 0) return fallbackLayer;

            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0) return i;
            }

            return fallbackLayer;
        }

        void TeardownRuntimeVolume()
        {
            _vignetteIntensity = null;
            _vignetteSmoothness = null;
            _chromaticIntensity = null;
            _lensIntensity = null;
            _lensScale = null;
            _saturation = null;
            _postExposure = null;

            // Drop the profile before the volume dies so VolumeManager cannot read a
            // half-destroyed profile on the frame in between.
            if (_volumeComponent != null) SetMember(_volumeComponent, "sharedProfile", null);

            DestroyOwned(_volumeObject);
            _volumeObject = null;
            _volumeComponent = null;

            // VolumeProfile does not own its components' lifetimes, so every
            // VolumeComponent created above would leak one ScriptableObject per
            // session if it were not destroyed here.
            for (int i = 0; i < _ownedComponents.Length; i++)
            {
                DestroyOwned(_ownedComponents[i]);
                _ownedComponents[i] = null;
            }

            DestroyOwned(_profile);
            _profile = null;
            _bound = false;
        }

        static void DestroyOwned(UnityEngine.Object obj)
        {
            if (obj == null) return;

            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }

        // =====================================================================
        // Reflection plumbing — all of it runs once, in Start or OnDestroy
        // =====================================================================

        static Type ResolveType(string fullName, string assemblyName)
        {
            Type resolved = null;

            try
            {
                resolved = Type.GetType(fullName + ", " + assemblyName, false);
            }
            catch (Exception)
            {
                resolved = null;
            }

            if (resolved != null) return resolved;

            // Fall back to a scan in case the package assembly was renamed or the
            // type moved between the core and universal assemblies.
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    resolved = assemblies[i].GetType(fullName, false);
                    if (resolved != null) return resolved;
                }
                catch (Exception)
                {
                    // A single unloadable assembly must not abort the search.
                }
            }

            return null;
        }

        static MethodInfo GetMethod(Type type, string name, Type[] argTypes)
        {
            if (type == null) return null;

            try
            {
                return type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, argTypes, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Binds a <c>VolumeParameter&lt;float&gt;</c> field to a strongly-typed
        /// setter. The reflection cost is paid once here so that Update writes
        /// through a plain delegate — no boxing and no allocation per frame.
        /// </summary>
        static Action<float> BindFloat(object volumeComponent, string fieldName)
        {
            if (volumeComponent == null) return null;

            FieldInfo field;
            try
            {
                field = volumeComponent.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception)
            {
                return null;
            }

            if (field == null) return null;

            object parameter;
            try
            {
                parameter = field.GetValue(volumeComponent);
            }
            catch (Exception)
            {
                return null;
            }

            if (parameter == null) return null;

            // Without overrideState the volume stack keeps the default and every
            // write below is silently discarded.
            SetMember(parameter, "overrideState", true);

            MethodInfo setter = GetMethod(parameter.GetType(), "set_value", FloatArg);
            if (setter == null) return null;

            try
            {
                return (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), parameter, setter, false);
            }
            catch (Exception)
            {
                // AOT platforms can refuse to synthesise the delegate; the effect
                // then no-ops rather than throwing once per frame.
                return null;
            }
        }

        static object GetMember(object target, string name)
        {
            if (target == null) return null;

            Type type = target.GetType();

            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanRead) return property.GetValue(target, null);
            }
            catch (Exception)
            {
                // Fall through to the field lookup.
            }

            try
            {
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null) return field.GetValue(target);
            }
            catch (Exception)
            {
                return null;
            }

            return null;
        }

        static void SetMember(object target, string name, object value)
        {
            if (target == null) return;

            Type type = target.GetType();

            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(target, value, null);
                    return;
                }
            }
            catch (Exception)
            {
                // URP has moved several of these between fields and properties across
                // versions, so a miss here is expected rather than exceptional.
            }

            try
            {
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null) field.SetValue(target, value);
            }
            catch (Exception)
            {
                // Degrade silently — a missing knob must not break the frame.
            }
        }
    }
}
