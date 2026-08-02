// -----------------------------------------------------------------------------
// Vigil — composition root.
//
// The ONE place services are constructed and wired. Everything else resolves from
// the ServiceContext this owns.
//
// It installs itself via [RuntimeInitializeOnLoadMethod] so it exists even when a
// developer presses Play directly in a gameplay scene. That is not a convenience:
// a bootstrap that only works if you start from the menu scene costs a team hours
// every day, and it is the single most common reason people stop using a
// composition root and go back to singletons.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using Vigil.AI.Agents;
using Vigil.AI.Director;
using Vigil.Audio;
using Vigil.AI.Pathfinding;
using Vigil.AI.Perception;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Events;
using Vigil.Core.Services;
using Vigil.Core.Simulation;
using Vigil.Data;
using Vigil.Net.Interest;
using Vigil.Net.Session;

namespace Vigil.Bootstrap
{
    [DefaultExecutionOrder(-10000)]
    public sealed class GameBootstrap : MonoBehaviour
    {
        const string ConfigResourcePath = "VigilConfigRegistry";

        [SerializeField] VigilConfigRegistry _configs = null;

        [SerializeField, Tooltip("Simulation ticks per second. Should match NetworkConfig.TickRate.")]
        int _tickRateOverride = 0;

        static GameBootstrap _instance;

        ServiceContext _context;
        SimClock _clock;
        TickScheduler _scheduler;

        public static GameBootstrap Instance => _instance;
        public ServiceContext Context => _context;
        public VigilConfigRegistry Configs => _configs;

        /// <summary>
        /// Guarantees a bootstrap exists no matter which scene was entered. Runs
        /// after the first scene loads so a scene-placed instance wins.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (_instance != null) return;

            GameBootstrap existing = FindAnyObjectByType<GameBootstrap>();
            if (existing != null) return;

            GameObject go = new GameObject("[Vigil Bootstrap]");
            go.AddComponent<GameBootstrap>();
            VLog.Info(LogCat.Core, "GameBootstrap auto-installed (scene entered without one).");
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            ResolveConfigs();
            BuildServices();
            EnsureClockRunner();
        }

        void ResolveConfigs()
        {
            if (_configs != null) return;

            _configs = Resources.Load<VigilConfigRegistry>(ConfigResourcePath);

            if (_configs == null)
            {
                VLog.Warn(LogCat.Core,
                    $"No VigilConfigRegistry assigned and none found at Resources/{ConfigResourcePath}. " +
                    "Run 'Vigil > Generate Playable Sample' to create the tuning assets. " +
                    "Systems will fall back to documented defaults.");
            }
        }

        void BuildServices()
        {
            _context = new ServiceContext("session");

            // --- foundation -----------------------------------------------------
            EventBus events = new EventBus();
            _context.Register<IEventBus>(events);
            _context.Register(events);

            NetworkTuningConfig netConfig = _configs != null ? _configs.Network : null;
            int tickRate = _tickRateOverride > 0
                ? _tickRateOverride
                : (netConfig != null ? netConfig.TickRate : 30);

            _clock = new SimClock(tickRate);
            _scheduler = new TickScheduler();
            _context.Register(_clock);
            _context.Register(_scheduler);

            // --- networking -----------------------------------------------------
            NetworkAuthority authority = new NetworkAuthority();
            _context.Register<INetworkAuthority>(authority);
            _context.Register(authority);

            SessionDriver session = new SessionDriver(authority, CreateRelayBackend());
            _context.Register<ISessionDriver>(session);
            _context.Register(session);

            // --- navigation -----------------------------------------------------
            NavigationConfig navConfig = _configs != null ? _configs.Navigation : null;
            NavigationService navigation = new NavigationService(navConfig);
            _context.Register<INavigationService>(navigation);
            _context.Register(navigation);

            RegionGraph regions = new RegionGraph();
            _context.Register<IRegionGraph>(regions);
            _context.Register(regions);

            // --- perception -----------------------------------------------------
            PerceptionConfig perceptionConfig = _configs != null ? _configs.Perception : null;
            PerceptionBus perception = new PerceptionBus(perceptionConfig);
            _context.Register<IPerceptionBus>(perception);
            _context.Register(perception);

            ScentTrail scent = new ScentTrail();
            _context.Register(scent);

            // --- director -------------------------------------------------------
            DirectorConfig directorConfig = _configs != null ? _configs.Director : null;
            AIDirector director = new AIDirector(directorConfig, events, regions);
            _context.Register<IAIDirector>(director);
            _context.Register(director);

            // --- interest -------------------------------------------------------
            InterestManager interest = new InterestManager(netConfig, regions);
            _context.Register<IInterestManager>(interest);
            _context.Register(interest);

            Services.SetActive(_context);

            // Order of registration on the scheduler IS the per-tick execution order.
            // Navigation must drain before agents poll; the Director must publish
            // intent before agents read it; interest evaluates last, after everything
            // has moved.
            _scheduler.Register(director, TickBudget.High);
            _scheduler.Register(navigation, TickBudget.Critical);
            _scheduler.Register(interest, TickBudget.Normal);

            InstallAudio();
            ValidateConfigs();

            VLog.Info(LogCat.Core, $"Bootstrap complete — {tickRate}Hz simulation, {_scheduler.Count} core tickables.");
        }

        /// <summary>
        /// Builds the audio stack on the bootstrap object so it survives the scene
        /// change into a level.
        ///
        /// <para>Audio is client-side presentation, so it is installed on every peer
        /// including a pure client — but NOT on a dedicated server, which has no
        /// listener and would waste a synthesis pass and 24 AudioSources on sound
        /// nobody will ever hear.</para>
        /// </summary>
        void InstallAudio()
        {
            if (CommandLineArgs.IsDedicatedServer)
            {
                VLog.Info(LogCat.Audio, "Dedicated server - audio stack skipped.");
                return;
            }

            VigilAudioService audio = gameObject.GetComponent<VigilAudioService>();
            if (audio == null) audio = gameObject.AddComponent<VigilAudioService>();

            audio.Initialise(_configs != null ? _configs.Audio : null);

            _context.Register(audio);

            if (gameObject.GetComponent<AudioDirector>() == null) gameObject.AddComponent<AudioDirector>();
            if (gameObject.GetComponent<SoundOcclusion>() == null) gameObject.AddComponent<SoundOcclusion>();
            if (gameObject.GetComponent<WorldAudioListener>() == null) gameObject.AddComponent<WorldAudioListener>();
            if (gameObject.GetComponent<PlayerBodyAudio>() == null) gameObject.AddComponent<PlayerBodyAudio>();
        }

        void ValidateConfigs()
        {
            if (_configs == null) return;

            List<string> problems = new List<string>();
            if (!_configs.Validate(problems))
            {
                VLog.Error(LogCat.Core, $"Config validation failed with {problems.Count} problem(s) — see preceding errors.");
            }
        }

        static IRelayBackend CreateRelayBackend()
        {
#if VIGIL_UGS_RELAY && VIGIL_UGS_AUTH
            return new UgsRelayBackend();
#else
            // Direct IP only. This is the correct default: it works offline, on a
            // LAN, and in CI, none of which should require an online service.
            return new NullRelayBackend();
#endif
        }

        void EnsureClockRunner()
        {
            if (GetComponent<SimClockRunner>() != null) return;

            SimClockRunner runner = gameObject.AddComponent<SimClockRunner>();
            runner.Initialise(_clock, _scheduler);
        }

        void OnDestroy()
        {
            if (_instance != this) return;

            Services.SetActive(null);
            _context?.Dispose();
            _context = null;
            _instance = null;

            VLog.Info(LogCat.Core, "Bootstrap torn down.");
        }
    }

    /// <summary>
    /// Drives the fixed simulation clock. Exactly one per session, owned by the
    /// bootstrap.
    ///
    /// <para>This is the only place in the project that reads
    /// <c>UnityEngine.Time</c> for simulation purposes — everything downstream
    /// receives <see cref="SimTime"/>. That single choke point is what makes the
    /// simulation reproducible.</para>
    /// </summary>
    [DefaultExecutionOrder(-9000)]
    public sealed class SimClockRunner : MonoBehaviour
    {
        SimClock _clock;
        TickScheduler _scheduler;

        /// <summary>Ticks executed on the most recent frame. Telemetry for the debug overlay.</summary>
        public int LastFrameTicks { get; private set; }

        public SimClock Clock => _clock;

        public void Initialise(SimClock clock, TickScheduler scheduler)
        {
            _clock = clock;
            _scheduler = scheduler;
        }

        void Update()
        {
            if (_clock == null || _scheduler == null) return;

            int steps = _clock.BeginFrame(Time.unscaledDeltaTime);
            LastFrameTicks = steps;

            for (int i = 0; i < steps; i++)
            {
                // Step() advances one tick and returns THAT tick's time. Dispatching
                // several ticks with the same SimTime would silently break every
                // time-based behaviour on exactly the frames where a hitch made
                // catch-up necessary.
                SimTime time = _clock.Step();
                _scheduler.Tick(in time);
            }
        }
    }
}
