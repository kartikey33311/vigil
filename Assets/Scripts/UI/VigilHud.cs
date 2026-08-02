// -----------------------------------------------------------------------------
// Vigil — session menu and in-game HUD.
//
// PRESENTATION IS PLACEHOLDER, BEHAVIOUR IS NOT. This is IMGUI, deliberately:
//
//   UI Toolkit is the right long-term choice and the architecture doc specifies
//   it, but a runtime UI Toolkit panel requires a PanelSettings asset with a
//   valid ThemeStyleSheet. Get that wrong and the panel renders NOTHING, with no
//   error — which is the worst possible failure mode for the one system whose job
//   is to tell you the game is working. IMGUI needs zero assets and cannot fail
//   to draw, so the vertical slice is verifiable today.
//
//   Replace with UI Toolkit before this is player-facing. The state this reads is
//   already correct and will not need to change.
// -----------------------------------------------------------------------------

using UnityEngine;
using Unity.Netcode;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Services;
using Vigil.Gameplay.Interaction;
using Vigil.Gameplay.Player;
using Vigil.Gameplay.Systems;
using Vigil.Net.Session;

namespace Vigil.UI
{
    public sealed class VigilHud : MonoBehaviour
    {
        [SerializeField] string _levelSceneName = "Level_Facility";

        ISessionDriver _session;
        IAIDirector _aiDirector;

        PlayerCharacter _localPlayer;
        InteractionSystem _localInteraction;
        MissionDirector _mission;

        string _joinAddress = "127.0.0.1";
        string _statusMessage = "";
        bool _busy;
        bool _showDebug;

        /// <summary>Seconds of input suppression after the menu appears. See DrawMenu.</summary>
        const float MenuInputDelay = 0.5f;
        float _menuShownAt = -1f;

        Texture2D _white;
        GUIStyle _label, _big, _prompt, _title, _small;
        bool _stylesReady;

        void Awake()
        {
            DontDestroyOnLoad(gameObject);

            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
        }

        void Start()
        {
            if (Services.IsReady)
            {
                _session = Services.TryGet<ISessionDriver>();
                _aiDirector = Services.TryGet<IAIDirector>();
            }
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F3)) _showDebug = !_showDebug;

            // Re-acquire lazily: the local player does not exist until a session
            // starts and the level scene has loaded.
            if (_localPlayer == null || !_localPlayer.IsSpawned)
            {
                _localPlayer = FindLocalPlayer();
                _localInteraction = _localPlayer != null ? _localPlayer.GetComponent<InteractionSystem>() : null;
            }

            if (_mission == null) _mission = FindAnyObjectByType<MissionDirector>();
        }

        static PlayerCharacter FindLocalPlayer()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient) return null;

            if (nm.LocalClient.PlayerObject != null)
            {
                return nm.LocalClient.PlayerObject.GetComponent<PlayerCharacter>();
            }

            PlayerCharacter[] all = FindObjectsByType<PlayerCharacter>(FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].IsOwner) return all[i];
            }

            return null;
        }

        bool InSession
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                return nm != null && nm.IsListening;
            }
        }

        // =====================================================================
        // Draw
        // =====================================================================

        void OnGUI()
        {
            EnsureStyles();

            if (!InSession)
            {
                DrawMenu();
                return;
            }

            // In a session: arm the suppression window again so returning to the menu
            // cannot inherit a stale click from the frame the session ended on.
            _menuShownAt = -1f;

            DrawHud();
            if (_showDebug) DrawDebug();
        }

        void EnsureStyles()
        {
            if (_stylesReady) return;

            _label = new GUIStyle(GUI.skin.label) { fontSize = 14, normal = { textColor = new Color(0.86f, 0.86f, 0.88f) } };
            _big = new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.92f, 0.9f, 0.86f) } };
            _prompt = new GUIStyle(GUI.skin.label) { fontSize = 17, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(1f, 0.95f, 0.82f) } };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 40, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.75f, 0.15f, 0.12f) } };

            _small = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = new Color(0.62f, 0.62f, 0.66f) }
            };

            _stylesReady = true;
        }

        // ---------------------------------------------------------------- menu

        void DrawMenu()
        {
            // Stamped on the first frame the menu is drawn, and reset whenever a
            // session ends, so the suppression window applies on every return to menu.
            if (_menuShownAt < 0f) _menuShownAt = Time.unscaledTime;

            Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.03f, 0.03f, 0.04f, 1f));

            // Wide enough that the tagline fits on one line at _big's size. The
            // previous 340px panel wrapped it to two lines inside a 24px-tall rect,
            // so the second line was clipped and overlapped the title.
            float w = 460f;
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height * 0.22f;

            GUI.Label(new Rect(x, y, w, 56), "VIGIL", _title);
            y += 64f;

            // Height matches the style's own line height rather than a guessed
            // constant, so it cannot clip if the font or text changes.
            GUIContent tagline = new GUIContent("1-4 player co-operative survival horror");
            float taglineHeight = _small.CalcHeight(tagline, w);
            GUI.Label(new Rect(x, y, w, taglineHeight), tagline, _small);
            y += taglineHeight + 28f;

            // Ignore activations for a moment after the menu first appears.
            //
            // A mouse event queued before the window existed (or delivered as it
            // takes focus) was being consumed by the first IMGUI button, so the game
            // auto-hosted on launch with no click: the menu flashed up, loaded the
            // level, and the player never got to press JOIN. Suppressing input
            // briefly is cheap and removes a whole class of stray-click bugs.
            bool acceptInput = Time.unscaledTime - _menuShownAt > MenuInputDelay;

            GUI.enabled = !_busy && acceptInput;

            if (GUI.Button(new Rect(x, y, w, 42), "SINGLE PLAYER") && acceptInput) StartOffline();
            y += 52f;

#if UNITY_WEBGL && !UNITY_EDITOR
            // Hidden rather than disabled on WebGL. A browser cannot listen for
            // connections, so hosting is impossible, and joining would need a
            // WebSocket server this build has no address for. Dead buttons only
            // invite a click and the conclusion that the game is broken.
            GUI.enabled = true;
            GUI.Label(new Rect(x, y, w, 46),
                "Multiplayer needs the desktop build:\na browser cannot host a session.", _small);
            y += 56f;
#else
            if (GUI.Button(new Rect(x, y, w, 42), "HOST SESSION") && acceptInput) StartHost();
            y += 52f;

            GUI.Label(new Rect(x, y, 70, 24), "Address", _label);
            _joinAddress = GUI.TextField(new Rect(x + 74, y, w - 74, 24), _joinAddress);
            y += 32f;

            if (GUI.Button(new Rect(x, y, w, 42), "JOIN SESSION") && acceptInput) StartClient();
            y += 60f;

            GUI.enabled = true;
#endif

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUI.Label(new Rect(x, y, w, 26), _statusMessage, _small);
                y += 34f;
            }

            GUI.Label(new Rect(x, y, w, 110),
                "WASD move   ·   Mouse look   ·   Shift sprint\n" +
                "Ctrl crouch   ·   E interact   ·   F flashlight\n" +
                "Esc release cursor   ·   F3 debug",
                _small);
        }

        /// <summary>
        /// Single player. Starts a host on a no-op transport so every NetworkObject
        /// still spawns and the full simulation runs, with no networking at all.
        /// This is the only mode that works in a browser.
        /// </summary>
        async void StartOffline()
        {
            SessionDriver driver = Services.TryGet<SessionDriver>();
            if (driver == null) { _statusMessage = "No session driver."; return; }

            _busy = true;
            _statusMessage = "Starting...";

            SessionOptions options = SessionOptions.Default;
            options.SessionName = "Vigil (offline)";
            options.MaxPlayers = 1;
            options.SessionSeed = (uint)Random.Range(1, int.MaxValue);

            bool ok = await driver.StartOfflineAsync(options);

            _busy = false;
            _statusMessage = ok ? "" : "Failed to start.";

            if (!ok) return;

            LoadLevel();
        }

        void LoadLevel()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.SceneManager != null)
            {
                nm.SceneManager.LoadScene(_levelSceneName, UnityEngine.SceneManagement.LoadSceneMode.Single);
            }
            else
            {
                VLog.Error(LogCat.Session, "No NetworkManager.SceneManager - cannot load the level.");
            }
        }

        async void StartHost()
        {
            if (_session == null) { _statusMessage = "No session driver."; return; }

            _busy = true;
            _statusMessage = "Starting host...";

            SessionOptions options = SessionOptions.Default;
            options.SessionName = "Vigil";
            options.MaxPlayers = 4;
            options.SessionSeed = (uint)Random.Range(1, int.MaxValue);

            bool ok = await _session.StartHostAsync(options);

            _busy = false;
            _statusMessage = ok ? "" : "Failed to host.";

            if (!ok) return;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.SceneManager != null)
            {
                nm.SceneManager.LoadScene(_levelSceneName, UnityEngine.SceneManagement.LoadSceneMode.Single);
            }
            else
            {
                VLog.Error(LogCat.Session, "No NetworkManager.SceneManager - cannot load the level.");
            }
        }

        async void StartClient()
        {
            if (_session == null) { _statusMessage = "No session driver."; return; }

            _busy = true;
            _statusMessage = "Connecting...";

            SessionOptions options = SessionOptions.Default;
            options.JoinCodeOrAddress = _joinAddress;

            bool ok = await _session.StartClientAsync(options);

            _busy = false;
            _statusMessage = ok ? "" : "Failed to connect.";
        }

        // ----------------------------------------------------------------- hud

        void DrawHud()
        {
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            // --- composure vignette --------------------------------------------
            // Drawn first so everything else sits on top of it.
            if (_localPlayer != null)
            {
                float panic = 1f - Mathf.Clamp01(_localPlayer.Composure / 100f);
                if (panic > 0.02f)
                {
                    float alpha = panic * 0.55f;
                    Color edge = new Color(0.35f, 0f, 0f, alpha);

                    float band = Mathf.Lerp(40f, 190f, panic);
                    Fill(new Rect(0, 0, Screen.width, band), edge);
                    Fill(new Rect(0, Screen.height - band, Screen.width, band), edge);
                    Fill(new Rect(0, 0, band, Screen.height), edge);
                    Fill(new Rect(Screen.width - band, 0, band, Screen.height), edge);
                }
            }

            // --- crosshair -------------------------------------------------------
            Color reticle = _localInteraction != null && _localInteraction.HasFocus
                ? new Color(1f, 0.9f, 0.6f, 0.95f)
                : new Color(1f, 1f, 1f, 0.35f);

            Fill(new Rect(cx - 5f, cy - 1f, 10f, 2f), reticle);
            Fill(new Rect(cx - 1f, cy - 5f, 2f, 10f), reticle);

            // --- interaction prompt ---------------------------------------------
            if (_localInteraction != null && _localInteraction.HasFocus)
            {
                InteractionPrompt p = _localInteraction.CurrentPrompt;

                string text = p.Available
                    ? (p.Kind == InteractionKind.Hold ? $"[Hold E] {p.Verb} {p.Subject}" : $"[E] {p.Verb} {p.Subject}")
                    : $"{p.Verb} {p.Subject} - {p.BlockedReason}";

                GUI.Label(new Rect(cx - 250f, cy + 42f, 500f, 26f), text, _prompt);

                if (p.Kind == InteractionKind.Hold && _localInteraction.HoldProgress > 0.001f)
                {
                    float barW = 220f;
                    Fill(new Rect(cx - barW * 0.5f, cy + 74f, barW, 6f), new Color(0f, 0f, 0f, 0.6f));
                    Fill(new Rect(cx - barW * 0.5f, cy + 74f, barW * _localInteraction.HoldProgress, 6f),
                        new Color(0.95f, 0.8f, 0.35f, 0.95f));
                }
            }

            // --- objective -------------------------------------------------------
            if (_mission != null)
            {
                GUI.Label(new Rect(24f, 20f, 520f, 26f), _mission.ObjectiveText, _big);

                if (_mission.Outcome != MissionOutcome.InProgress)
                {
                    string banner = _mission.Outcome == MissionOutcome.Extracted ? "EXTRACTED" : "ALL PERSONNEL LOST";
                    Fill(new Rect(0, cy - 60f, Screen.width, 120f), new Color(0f, 0f, 0f, 0.75f));
                    GUI.Label(new Rect(0, cy - 30f, Screen.width, 60f), banner, _title);
                }
            }

            // --- player vitals ---------------------------------------------------
            if (_localPlayer == null)
            {
                GUI.Label(new Rect(24f, Screen.height - 40f, 400f, 24f), "Waiting for spawn...", _label);
                return;
            }

            float by = Screen.height - 96f;

            DrawBar(new Rect(24f, by, 220f, 12f),
                Mathf.Clamp01(_localPlayer.Composure / 100f),
                new Color(0.45f, 0.65f, 0.85f), "Composure");

            by += 26f;

            DrawBar(new Rect(24f, by, 220f, 10f),
                Mathf.Clamp01(_localPlayer.Health / Mathf.Max(1f, _localPlayer.MaxHealth)),
                new Color(0.7f, 0.25f, 0.22f), "Health");

            if (_localPlayer.IsDowned)
            {
                GUI.Label(new Rect(0, cy + 120f, Screen.width, 30f), "DOWNED - a teammate can revive you", _big);
            }

            // Gait readout doubles as a noise indicator, which is the information the
            // player actually needs to make stealth decisions.
            string gait = _localPlayer.CurrentGait.ToString().ToUpperInvariant();
            GUI.Label(new Rect(24f, Screen.height - 40f, 300f, 22f), $"{gait}", _label);
        }

        void DrawDebug()
        {
            float x = Screen.width - 300f;
            float y = 20f;

            Fill(new Rect(x - 10f, y - 6f, 300f, 150f), new Color(0f, 0f, 0f, 0.65f));

            GUI.Label(new Rect(x, y, 290f, 20f), "-- VIGIL DEBUG (F3) --", _label); y += 22f;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null)
            {
                string role = nm.IsHost ? "Host" : (nm.IsServer ? "Server" : "Client");
                GUI.Label(new Rect(x, y, 290f, 20f), $"Role: {role}   Clients: {nm.ConnectedClientsIds.Count}", _label);
                y += 20f;
            }

            if (_aiDirector != null)
            {
                GUI.Label(new Rect(x, y, 290f, 20f), $"Dread: {_aiDirector.Phase}  ({_aiDirector.TimeInPhase:F0}s)", _label); y += 20f;
                GUI.Label(new Rect(x, y, 290f, 20f), $"Stress: {_aiDirector.TableStress:F2}", _label); y += 20f;
                GUI.Label(new Rect(x, y, 290f, 20f), $"Engage: {_aiDirector.CurrentIntent.EngagementAuthorised}", _label); y += 20f;
            }

            Vigil.AI.Agents.NpcAgent agent = FindAnyObjectByType<Vigil.AI.Agents.NpcAgent>();
            if (agent != null)
            {
                GUI.Label(new Rect(x, y, 290f, 20f), $"AI: {agent.DebugStatePath}", _label); y += 20f;

                if (agent.DebugAwareness != null)
                {
                    GUI.Label(new Rect(x, y, 290f, 20f),
                        $"Awareness: {agent.DebugAwareness.PeakAwareness:F2} ({agent.DebugAwareness.PeakLevel})", _label);
                }
            }
        }

        void DrawBar(Rect rect, float fill01, Color colour, string caption)
        {
            Fill(rect, new Color(0f, 0f, 0f, 0.55f));
            Fill(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(fill01), rect.height), colour);
            GUI.Label(new Rect(rect.x + rect.width + 8f, rect.y - 4f, 160f, 20f), caption, _label);
        }

        void Fill(Rect rect, Color colour)
        {
            Color previous = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(rect, _white);
            GUI.color = previous;
        }
    }
}
