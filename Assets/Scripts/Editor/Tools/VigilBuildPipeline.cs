// -----------------------------------------------------------------------------
// Vigil — build entry points.
//
// Callable from the menu and, more importantly, from CI via
// -executeMethod Vigil.Editor.Tools.VigilBuildPipeline.BuildLinuxServer
//
// The single most important behaviour here is EXITING NON-ZERO ON FAILURE. Unity
// in batch mode returns 0 for a failed build unless you explicitly quit with a
// code, so a CI pipeline that trusts the exit status will happily publish a
// container built from a build that never produced a binary. That is the bug this
// file exists to prevent.
// -----------------------------------------------------------------------------

using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Vigil.Core.Diagnostics;

namespace Vigil.Editor.Tools
{
    public static class VigilBuildPipeline
    {
        const string DefaultOutputRoot = "Build";

        static string[] Scenes =>
            EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();

        // =====================================================================
        // Menu entries
        // =====================================================================

        [MenuItem("Vigil/Build/Windows Client", priority = 100)]
        public static void BuildWindowsClientMenu() => BuildWindowsClient();

        [MenuItem("Vigil/Build/Windows Client (development)", priority = 103)]
        public static void BuildWindowsClientDevMenu() => BuildWindowsClientDev();

        /// <summary>
        /// Development build. Essential for diagnosing a player-only failure:
        /// VLog.Info and VLog.Warn are [Conditional] on UNITY_EDITOR/DEVELOPMENT_BUILD,
        /// so a release player logs almost nothing and you are debugging blind.
        /// </summary>
        public static void BuildWindowsClientDev()
        {
            Run(new BuildPlayerOptions
            {
                scenes = Scenes,
                locationPathName = Path.Combine(OutputRoot(), "WindowsClientDev", "Vigil.exe"),
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.Development | BuildOptions.AllowDebugging
            }, "Windows Client (development)");
        }

        [MenuItem("Vigil/Build/Windows Server (headless)", priority = 101)]
        public static void BuildWindowsServerMenu() => BuildWindowsServer();

        [MenuItem("Vigil/Build/Linux Dedicated Server", priority = 102)]
        public static void BuildLinuxServerMenu() => BuildLinuxServer();

        // =====================================================================
        // CI entry points (-executeMethod)
        // =====================================================================

        public static void BuildWindowsClient()
        {
            Run(new BuildPlayerOptions
            {
                scenes = Scenes,
                locationPathName = Path.Combine(OutputRoot(), "WindowsClient", "Vigil.exe"),
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None
            }, "Windows Client");
        }

        /// <summary>
        /// Headless Windows server. Exists because it can be built on a Windows dev
        /// box without the Linux module, which makes it the fastest way to verify the
        /// server actually boots before pushing to CI.
        /// </summary>
        public static void BuildWindowsServer()
        {
            Run(new BuildPlayerOptions
            {
                scenes = Scenes,
                locationPathName = Path.Combine(OutputRoot(), "WindowsServer", "VigilServer.exe"),
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Server,
                options = BuildOptions.None
            }, "Windows Dedicated Server");
        }

        public static void BuildLinuxServer()
        {
            Run(new BuildPlayerOptions
            {
                scenes = Scenes,
                locationPathName = Path.Combine(OutputRoot(), "LinuxServer", "VigilServer"),
                target = BuildTarget.StandaloneLinux64,
                subtarget = (int)StandaloneBuildSubtarget.Server,
                options = BuildOptions.None
            }, "Linux Dedicated Server");
        }

        // =====================================================================
        // Core
        // =====================================================================

        static string OutputRoot()
        {
            // CI overrides the destination; a developer gets ./Build.
            string fromArgs = ReadArg("-buildOutput");
            if (!string.IsNullOrEmpty(fromArgs)) return fromArgs;

            string fromEnv = Environment.GetEnvironmentVariable("VIGIL_BUILD_OUTPUT");
            return string.IsNullOrEmpty(fromEnv) ? DefaultOutputRoot : fromEnv;
        }

        static void Run(BuildPlayerOptions options, string label)
        {
            if (options.scenes == null || options.scenes.Length == 0)
            {
                Fail($"{label}: no scenes in Build Settings. Run 'Vigil > Generate Playable Sample' first.");
                return;
            }

            string directory = Path.GetDirectoryName(options.locationPathName);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            ConfigureDefines(options.subtarget == (int)StandaloneBuildSubtarget.Server);

            VLog.Info(LogCat.Core, $"Building {label} -> {options.locationPathName}");
            VLog.Info(LogCat.Core, $"Scenes: {string.Join(", ", options.scenes)}");

            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(options);
            }
            catch (Exception ex)
            {
                Fail($"{label}: build threw - {ex.Message}");
                return;
            }

            BuildSummary summary = report.summary;

            if (summary.result != BuildResult.Succeeded)
            {
                // Surface the actual errors. Without this the CI log shows only
                // "Build failed", which sends someone hunting through 40k lines.
                foreach (BuildStep step in report.steps)
                {
                    foreach (BuildStepMessage message in step.messages)
                    {
                        if (message.type == LogType.Error || message.type == LogType.Exception)
                        {
                            VLog.Error(LogCat.Core, $"[{step.name}] {message.content}");
                        }
                    }
                }

                Fail($"{label}: {summary.result} ({summary.totalErrors} error(s)).");
                return;
            }

            VLog.Info(LogCat.Core,
                $"{label} SUCCEEDED: {summary.totalSize / (1024f * 1024f):F1} MB in {summary.totalTime.TotalSeconds:F0}s -> {summary.outputPath}");

            Succeed();
        }

        /// <summary>
        /// Server builds get VIGIL_SERVER defined so code can compile out anything
        /// client-only. Client builds must NOT carry it, or a client would take the
        /// headless path and boot straight past the menu.
        /// </summary>
        static void ConfigureDefines(bool isServer)
        {
            NamedBuildTarget target = NamedBuildTarget.Standalone;
            string existing = PlayerSettings.GetScriptingDefineSymbols(target);

            var defines = existing
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(d => d != "VIGIL_SERVER")
                .ToList();

            if (isServer) defines.Add("VIGIL_SERVER");

            PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", defines));
        }

        static string ReadArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name) return args[i + 1];
            }
            return null;
        }

        static bool IsBatch => Application.isBatchMode;

        static void Succeed()
        {
            if (IsBatch) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Logs and exits NON-ZERO in batch mode.
        ///
        /// <para>Unity returns 0 from a failed -executeMethod build unless you quit
        /// explicitly. A CI job that trusts the exit code will otherwise package and
        /// deploy a container around a binary that was never produced.</para>
        /// </summary>
        static void Fail(string reason)
        {
            VLog.Error(LogCat.Core, "BUILD FAILED: " + reason);
            if (IsBatch) EditorApplication.Exit(1);
        }
    }
}
