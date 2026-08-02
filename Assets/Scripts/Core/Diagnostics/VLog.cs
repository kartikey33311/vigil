// -----------------------------------------------------------------------------
// Vigil — categorised, zero-cost-when-disabled logging.
//
// Rationale: UnityEngine.Debug.Log builds a managed string and a stack trace on
// EVERY call, even when the message is filtered out downstream. In a 60 Hz
// simulation with ~40 AI agents that is a measurable GC source. Every method
// here is [Conditional]-gated so the call site is *removed by the compiler* in
// builds that do not define the symbol, and each category can be muted at
// runtime without paying for string construction (see the `Is` guard pattern).
//
// Usage:
//     if (VLog.Is(LogCat.AI)) VLog.Info(LogCat.AI, $"{name} -> {state}");
// -----------------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Vigil.Core.Diagnostics
{
    /// <summary>Log channels. Powers of two — combined into a runtime mask.</summary>
    [Flags]
    public enum LogCat
    {
        None        = 0,
        Core        = 1 << 0,
        Net         = 1 << 1,
        AI          = 1 << 2,
        Perception  = 1 << 3,
        Pathfinding = 1 << 4,
        Fsm         = 1 << 5,
        Director    = 1 << 6,
        Gameplay    = 1 << 7,
        Audio       = 1 << 8,
        UI          = 1 << 9,
        Save        = 1 << 10,
        Session     = 1 << 11,
        All         = ~0
    }

    public static class VLog
    {
        /// <summary>
        /// Categories that are currently emitted. Defaults to everything in the
        /// editor and to warnings/errors only in a player (see static ctor).
        /// Mutable at runtime via the in-game console or `-vigil-log` CLI arg.
        /// </summary>
        public static LogCat Mask { get; set; } = LogCat.All;

        /// <summary>Set false by the dedicated-server bootstrap to strip colour codes.</summary>
        public static bool UseRichText { get; set; } = Application.isEditor;

        static VLog()
        {
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
            Mask = LogCat.None;
#endif
        }

        /// <summary>
        /// Guard for expensive message construction. Always call this before
        /// building an interpolated string:
        /// <c>if (VLog.Is(LogCat.AI)) VLog.Info(LogCat.AI, $"...");</c>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Is(LogCat cat) => (Mask & cat) != 0;

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Info(LogCat cat, string message, UnityEngine.Object context = null)
        {
            if ((Mask & cat) == 0) return;
            UnityEngine.Debug.Log(Format(cat, message), context);
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Warn(LogCat cat, string message, UnityEngine.Object context = null)
        {
            if ((Mask & cat) == 0) return;
            UnityEngine.Debug.LogWarning(Format(cat, message), context);
        }

        /// <summary>
        /// Errors are intentionally NOT [Conditional] — a shipping build must
        /// still surface them to the crash reporter.
        /// </summary>
        public static void Error(LogCat cat, string message, UnityEngine.Object context = null)
        {
            UnityEngine.Debug.LogError(Format(cat, message), context);
        }

        public static void Exception(LogCat cat, Exception ex, UnityEngine.Object context = null)
        {
            UnityEngine.Debug.LogError(Format(cat, ex.Message), context);
            UnityEngine.Debug.LogException(ex, context);
        }

        /// <summary>
        /// Editor-only assertion. Compiled out of players entirely, so it is safe
        /// to use inside tight simulation loops to document invariants.
        /// </summary>
        [Conditional("UNITY_EDITOR")]
        public static void Assert(bool condition, LogCat cat, string message, UnityEngine.Object context = null)
        {
            if (condition) return;
            UnityEngine.Debug.LogError(Format(cat, "ASSERT FAILED: " + message), context);
        }

        static string Format(LogCat cat, string message)
        {
            if (!UseRichText)
            {
                return string.Concat("[", cat.ToString(), "] ", message);
            }

            return string.Concat("<color=", ColourFor(cat), ">[", cat.ToString(), "]</color> ", message);
        }

        static string ColourFor(LogCat cat)
        {
            switch (cat)
            {
                case LogCat.Net:         return "#4EC9B0";
                case LogCat.Session:     return "#4EC9B0";
                case LogCat.AI:          return "#DCDCAA";
                case LogCat.Perception:  return "#C586C0";
                case LogCat.Pathfinding: return "#569CD6";
                case LogCat.Fsm:         return "#CE9178";
                case LogCat.Director:    return "#F44747";
                case LogCat.Gameplay:    return "#9CDCFE";
                case LogCat.Audio:       return "#B5CEA8";
                case LogCat.UI:          return "#808080";
                default:                 return "#D4D4D4";
            }
        }
    }
}
