// -----------------------------------------------------------------------------
// Vigil — headless server entry point.
//
// Starts the authoritative server from configuration and loads the level. Without
// this, a headless build boots to a main menu that nobody can click.
//
// Everything here is shaped by the fact that it runs unattended in a container:
//
//   * It must log its state to stdout, because container logs are the only
//     debugging channel you get.
//   * It must EXIT on failure rather than idling. A process that stays alive after
//     failing to bind looks healthy to an orchestrator, which will happily leave a
//     dead server in the rotation forever.
//   * It must cap framerate. An uncapped headless Unity process spins a core at
//     100% doing nothing, which on metered hosting is a bill.
//   * It must shut down cleanly on SIGTERM so players get a disconnect reason
//     instead of a timeout.
// -----------------------------------------------------------------------------

using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Services;

namespace Vigil.Bootstrap
{
    [DefaultExecutionOrder(-7000)]
    public sealed class DedicatedServerBootstrap : MonoBehaviour
    {
        [SerializeField, Tooltip("Server tick budget. 60 is plenty for a 30Hz simulation and leaves headroom for the scheduler.")]
        int _targetFrameRate = 60;

        [SerializeField, Min(1f), Tooltip("Seconds to wait for the level to load before giving up and exiting non-zero.")]
        float _levelLoadTimeout = 60f;

        ISessionDriver _session;
        bool _started;

        /// <summary>
        /// Installs itself in a headless process. Runs before the first scene so the
        /// server never renders a frame of menu.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoInstall()
        {
            if (!CommandLineArgs.IsDedicatedServer) return;

            // Second guard, belt and braces. Entering play mode in the editor must
            // never silently become a dedicated server — it would seize the port,
            // load a different scene than the developer chose, and (in the test
            // runner) hang the suite. CommandLineArgs already excludes the editor
            // from its headless heuristic; this catches an explicit -server flag
            // left in the editor's command line by a previous batch run.
            if (Application.isEditor && !Application.isPlaying) return;

            if (FindAnyObjectByType<DedicatedServerBootstrap>() != null) return;

            GameObject go = new GameObject("[Vigil Dedicated Server]");
            go.AddComponent<DedicatedServerBootstrap>();
        }

        void Awake()
        {
            DontDestroyOnLoad(gameObject);

            // Headless hygiene. Each of these is a real cost on a rented VM.
            Application.targetFrameRate = _targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            // Plain output: colour codes turn container logs into escape-sequence soup.
            VLog.UseRichText = false;

            Application.wantsToQuit += OnWantsToQuit;
        }

        IEnumerator Start()
        {
            // Wait for GameBootstrap to publish the service context. It installs at
            // AfterSceneLoad too, and execution order between two such callbacks is
            // not guaranteed.
            float deadline = Time.realtimeSinceStartup + 15f;
            while (!Services.IsReady && Time.realtimeSinceStartup < deadline) yield return null;

            if (!Services.IsReady)
            {
                Fail("ServiceContext never became ready - GameBootstrap did not run.");
                yield break;
            }

            _session = Services.TryGet<ISessionDriver>();
            if (_session == null)
            {
                Fail("No ISessionDriver registered.");
                yield break;
            }

            yield return StartServer();
        }

        IEnumerator StartServer()
        {
            SessionOptions options = SessionOptions.Default;
            options.SessionName = "Vigil Dedicated";
            options.MaxPlayers = Mathf.Clamp(CommandLineArgs.MaxPlayers, 1, 8);
            options.Port = CommandLineArgs.Port;
            options.BindAddress = CommandLineArgs.BindAddress;
            options.SessionSeed = CommandLineArgs.HasSeed
                ? CommandLineArgs.Seed
                : (uint)System.Environment.TickCount;

            VLog.Info(LogCat.Session,
                $"Starting dedicated server: bind={options.BindAddress} port={options.Port} " +
                $"maxPlayers={options.MaxPlayers} seed={options.SessionSeed}");

            System.Threading.Tasks.Task<bool> task = _session.StartDedicatedServerAsync(options);

            while (!task.IsCompleted) yield return null;

            if (!task.Result)
            {
                Fail($"Failed to bind {options.BindAddress}:{options.Port}.");
                yield break;
            }

            _started = true;
            VLog.Info(LogCat.Session, "Server listening.");

            yield return LoadLevel();
        }

        IEnumerator LoadLevel()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || nm.SceneManager == null)
            {
                Fail("NetworkManager.SceneManager unavailable - cannot load the level.");
                yield break;
            }

            string sceneName = CommandLineArgs.LevelScene;
            bool loaded = false;

            nm.SceneManager.OnLoadEventCompleted += (s, mode, done, timedOut) =>
            {
                if (s == sceneName) loaded = true;
            };

            SceneEventProgressStatus status = nm.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
            if (status != SceneEventProgressStatus.Started)
            {
                Fail($"LoadScene('{sceneName}') refused: {status}. Is the scene in Build Settings?");
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + _levelLoadTimeout;
            while (!loaded && Time.realtimeSinceStartup < deadline) yield return null;

            if (!loaded)
            {
                Fail($"Level '{sceneName}' did not finish loading within {_levelLoadTimeout:F0}s.");
                yield break;
            }

            VLog.Info(LogCat.Session, $"Level '{sceneName}' ready. Accepting players on :{CommandLineArgs.Port}.");
        }

        /// <summary>
        /// Logs and exits non-zero.
        ///
        /// <para>Exiting matters: an orchestrator restarts a crashed container but
        /// will leave a running-but-broken one serving traffic indefinitely. Failing
        /// loudly is the only way a health check can do its job.</para>
        /// </summary>
        void Fail(string reason)
        {
            VLog.Error(LogCat.Session, "DEDICATED SERVER FAILED: " + reason);

#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(1);
#endif
        }

        bool OnWantsToQuit()
        {
            if (!_started) return true;

            // Give connected players a real disconnect rather than a timeout.
            VLog.Info(LogCat.Session, "Shutdown requested - closing session.");
            _session?.Shutdown(DisconnectReason.ServerShutdown);
            _started = false;

            return true;
        }

        void OnDestroy()
        {
            Application.wantsToQuit -= OnWantsToQuit;
        }
    }
}
