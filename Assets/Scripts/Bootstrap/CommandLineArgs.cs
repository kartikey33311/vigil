// -----------------------------------------------------------------------------
// Vigil — command line parsing.
//
// Used by the dedicated server entry point, by automated test launches, and by
// anyone debugging a specific seed. Parsed once and cached: Environment's argument
// array is allocated fresh on every call, so reading it per-frame (which is
// tempting for a log-level toggle) is a quiet source of garbage.
//
//   -server                run headless as an authoritative server
//   -port <n>              listen / connect port
//   -join <address>        connect to a host on startup
//   -seed <n>              fix the session seed for reproducible AI
//   -vigil-log <cats>      comma-separated LogCat names, or "all" / "none"
// -----------------------------------------------------------------------------

using System;
using UnityEngine;
using Vigil.Core.Diagnostics;

namespace Vigil.Bootstrap
{
    public static class CommandLineArgs
    {
        static bool _parsed;

        static bool _isDedicatedServer;
        static ushort _port;
        static string _joinAddress;
        static string _bindAddress;
        static uint _seed;
        static bool _hasSeed;
        static int _maxPlayers;
        static string _levelScene;

        public static bool IsDedicatedServer { get { EnsureParsed(); return _isDedicatedServer; } }
        public static ushort Port { get { EnsureParsed(); return _port; } }
        public static string JoinAddress { get { EnsureParsed(); return _joinAddress; } }
        public static string BindAddress { get { EnsureParsed(); return _bindAddress; } }
        public static bool HasSeed { get { EnsureParsed(); return _hasSeed; } }
        public static uint Seed { get { EnsureParsed(); return _seed; } }
        public static int MaxPlayers { get { EnsureParsed(); return _maxPlayers; } }
        public static string LevelScene { get { EnsureParsed(); return _levelScene; } }

        static void EnsureParsed()
        {
            if (_parsed) return;
            _parsed = true;

            // Environment variables first, command line second — a flag beats an env
            // var, which is what an operator debugging a running container expects.
            // Container platforms configure through env, so this is the primary path.
            _port = ReadEnvUShort("VIGIL_PORT", 7777);
            _bindAddress = ReadEnv("VIGIL_BIND", "0.0.0.0");
            _maxPlayers = (int)ReadEnvUShort("VIGIL_MAX_PLAYERS", 4);
            _levelScene = ReadEnv("VIGIL_LEVEL", "Level_Facility");
            _joinAddress = string.Empty;

            if (!string.IsNullOrEmpty(ReadEnv("VIGIL_SERVER", string.Empty))) _isDedicatedServer = true;

            string envLog = ReadEnv("VIGIL_LOG", string.Empty);
            if (!string.IsNullOrEmpty(envLog)) ApplyLogMask(envLog);

            string envSeed = ReadEnv("VIGIL_SEED", string.Empty);
            if (!string.IsNullOrEmpty(envSeed) && uint.TryParse(envSeed, out uint envSeedValue))
            {
                _seed = envSeedValue;
                _hasSeed = true;
            }

            string[] args;
            try
            {
                args = Environment.GetCommandLineArgs();
            }
            catch (Exception)
            {
                // Some platforms deny access. Defaults are a fine outcome.
                return;
            }

            if (args == null) return;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-server":
                        _isDedicatedServer = true;
                        break;

                    case "-port":
                        if (i + 1 < args.Length && ushort.TryParse(args[i + 1], out ushort port)) _port = port;
                        break;

                    case "-join":
                        if (i + 1 < args.Length) _joinAddress = args[i + 1];
                        break;

                    case "-bind":
                        if (i + 1 < args.Length) _bindAddress = args[i + 1];
                        break;

                    case "-maxplayers":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int maxPlayers)) _maxPlayers = maxPlayers;
                        break;

                    case "-level":
                        if (i + 1 < args.Length) _levelScene = args[i + 1];
                        break;

                    case "-seed":
                        if (i + 1 < args.Length && uint.TryParse(args[i + 1], out uint seed))
                        {
                            _seed = seed;
                            _hasSeed = true;
                        }
                        break;

                    case "-vigil-log":
                        if (i + 1 < args.Length) ApplyLogMask(args[i + 1]);
                        break;
                }
            }

            // A headless PLAYER with no graphics device is a server whether or not
            // anyone passed the flag — treating it as a client would install an audio
            // stack and a camera for an audience of nobody.
            //
            // Explicitly NOT applied in the editor. The test runner runs batchmode
            // + nographics, so this heuristic made every CI test run look like a
            // dedicated server: DedicatedServerBootstrap auto-installed, tried to
            // bind a socket the tests were not expecting, and hung the suite until
            // it timed out. An editor session is only a server when someone actually
            // passed -server.
            if (!_isDedicatedServer
                && !Application.isEditor
                && Application.isBatchMode
                && SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                _isDedicatedServer = true;
            }
        }

        static string ReadEnv(string key, string fallback)
        {
            try
            {
                string value = Environment.GetEnvironmentVariable(key);
                return string.IsNullOrEmpty(value) ? fallback : value;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        static ushort ReadEnvUShort(string key, ushort fallback)
        {
            string raw = ReadEnv(key, string.Empty);
            return ushort.TryParse(raw, out ushort parsed) ? parsed : fallback;
        }

        static void ApplyLogMask(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return;

            if (string.Equals(spec, "all", StringComparison.OrdinalIgnoreCase)) { VLog.Mask = LogCat.All; return; }
            if (string.Equals(spec, "none", StringComparison.OrdinalIgnoreCase)) { VLog.Mask = LogCat.None; return; }

            LogCat mask = LogCat.None;
            string[] parts = spec.Split(',');

            for (int i = 0; i < parts.Length; i++)
            {
                if (Enum.TryParse(parts[i].Trim(), true, out LogCat parsed)) mask |= parsed;
            }

            VLog.Mask = mask;
        }

        /// <summary>Test hook: forces a re-parse after the environment changes.</summary>
        public static void Invalidate() => _parsed = false;
    }
}
