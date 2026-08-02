// -----------------------------------------------------------------------------
// Vigil — shared vocabulary for the tuning layer.
//
// Everything in Vigil.Data is authored by designers and read by simulation code,
// which imposes two constraints that shape this file:
//
//   1. AUTHORING SHAPE != RUNTIME SHAPE. Designers want named, labelled entries
//      they can reorder and comment on. Simulation wants an O(1) array lookup with
//      no branching and no allocation. Configs therefore serialise an authored
//      list and bake a flat LUT on OnEnable/OnValidate. The counts below are the
//      contract between those two representations — if a Core enum ever gains a
//      member, exactly one constant here needs updating and Validate() will shout
//      about the mismatch rather than silently reading garbage.
//
//   2. CONFIG ERRORS MUST FAIL LOUDLY AT BOOT, NEVER QUIETLY AT RUNTIME. A curve
//      left null or a radius authored inside-out produces AI that is subtly wrong
//      for an entire playtest and is almost impossible to diagnose from the
//      symptom. IValidatableConfig exists so the registry can sweep every asset
//      once during bootstrap and refuse to proceed on a bad combination.
//
// Nested [Serializable] structs in this assembly expose PUBLIC FIELDS rather than
// properties. That is a deliberate exception to the project style rule: Unity's
// serialiser cannot serialise properties, so an authoring row has no other option.
// The enclosing ScriptableObject still hands them out read-only, by value, through
// TryGet-style accessors so no caller can mutate a config asset at runtime.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Contracts;

namespace Vigil.Data
{
    /// <summary>
    /// Player locomotion gaits. Ordered quietest-to-loudest, which several configs
    /// rely on when validating that noise output is monotonic — a crouch that emits
    /// more noise than a sprint is always an authoring mistake, never a design.
    /// </summary>
    public enum Gait : byte
    {
        /// <summary>Standing still. Emits nothing but breathing (see ComposureConfig).</summary>
        Idle = 0,

        Crouch = 1,
        Walk = 2,
        Sprint = 3
    }

    /// <summary>
    /// Acoustic material classes for occlusion. Deliberately coarse: a per-material
    /// table with fifty entries is unmaintainable and inaudible. Eight classes is
    /// the point where players can still hear the difference between hiding behind
    /// drywall and hiding behind a steel door.
    /// </summary>
    public enum SurfaceMaterialClass : byte
    {
        /// <summary>No occluder — the free-air reference case.</summary>
        Open = 0,

        Drywall = 1,
        Wood = 2,
        Glass = 3,
        Metal = 4,
        Concrete = 5,
        Fabric = 6,
        Foliage = 7
    }

    /// <summary>
    /// Locomotion intent bands for an AI agent. These are NOT FSM state ids — the
    /// FSM owns those and may have many more states. This enum exists purely so a
    /// designer can author five movement speeds without the archetype asset needing
    /// to know the antagonist's state graph.
    /// </summary>
    public enum AgentMoveState : byte
    {
        Patrol = 0,
        Investigate = 1,
        Search = 2,

        /// <summary>Deliberately slow. Stalking is about being heard, not about closing.</summary>
        Stalk = 3,

        Chase = 4
    }

    /// <summary>
    /// Effect tiers derived from composure. Presentation systems key off the tier,
    /// not the raw float, so a designer can retune the thresholds without every
    /// post-process and audio hookup needing to be re-authored.
    /// </summary>
    public enum ComposureTier : byte
    {
        Calm = 0,
        Uneasy = 1,
        Shaken = 2,
        Panicked = 3,

        /// <summary>Composure floor. The player is actively betraying their own position.</summary>
        Broken = 4
    }

    /// <summary>One-shot musical/impact cues. Each kind carries its own cooldown.</summary>
    public enum StingerKind : byte
    {
        Sighting = 0,
        ChaseStart = 1,
        ChaseEnd = 2,
        Attack = 3,
        Downed = 4,
        Death = 5,

        /// <summary>Scripted reveal of the antagonist. Highest priority, longest cooldown.</summary>
        Reveal = 6,

        /// <summary>Deliberate misdirection — a scare with nothing behind it.</summary>
        FalseScare = 7
    }

    /// <summary>
    /// Cardinalities of every enum a config bakes into a flat array. Kept in one
    /// place so a Core enum gaining a member is a single-line fix plus a loud
    /// validation failure, rather than an out-of-range read at tick time.
    /// </summary>
    public static class ConfigCounts
    {
        public const int Gaits = 4;
        public const int SurfaceMaterialClasses = 8;
        public const int AgentMoveStates = 5;
        public const int ComposureTiers = 5;
        public const int StingerKinds = 8;

        /// <summary>Unaware, Suspicious, Alerted, Confirmed, Lost.</summary>
        public const int AwarenessLevels = 5;

        /// <summary>Buildup, Peak, Relax, Fadeout.</summary>
        public const int DreadPhases = 4;

        /// <summary>PlayerDowned .. LoudNoise.</summary>
        public const int DirectorEvents = 10;

        /// <summary>Sight, Sound, Scent, Touch, Environmental, Damage.</summary>
        public const int StimulusChannels = 6;

        /// <summary>Number of defined bits in <see cref="StimulusTag"/> (Footstep .. Blood).</summary>
        public const int StimulusTagBits = 14;

        /// <summary>Unity's NavMesh supports exactly 32 area indices.</summary>
        public const int NavMeshAreas = 32;
    }

    /// <summary>
    /// Bit-index helpers for the <see cref="StimulusTag"/> flags enum. A flags enum
    /// cannot index an array directly, and a Dictionary lookup per stimulus would
    /// allocate on the boxing path — so tags are collapsed to a dense 0..13 index
    /// and weights live in a plain float array.
    /// </summary>
    public static class StimulusTagIndex
    {
        /// <summary>Lowest set bit as a dense index, or -1 for <see cref="StimulusTag.None"/>.</summary>
        public static int IndexOf(StimulusTag tag)
        {
            uint bits = (uint)tag;
            if (bits == 0u) return -1;

            for (int i = 0; i < ConfigCounts.StimulusTagBits; i++)
            {
                if ((bits & (1u << i)) != 0u) return i;
            }

            return -1;
        }

        /// <summary>Single-bit tag for a dense index, or <see cref="StimulusTag.None"/>.</summary>
        public static StimulusTag FromIndex(int index)
        {
            if (index < 0 || index >= ConfigCounts.StimulusTagBits) return StimulusTag.None;
            return (StimulusTag)(1u << index);
        }
    }

    /// <summary>
    /// Implemented by every config asset so <see cref="VigilConfigRegistry"/> can
    /// sweep the whole tuning set in one pass at bootstrap.
    /// </summary>
    public interface IValidatableConfig
    {
        /// <summary>
        /// Appends a human-readable description of every problem found. Called from
        /// the editor and from bootstrap — never from a tick, so string allocation
        /// here is intentional and acceptable.
        /// </summary>
        void Validate(IList<string> problems);
    }

    /// <summary>
    /// Curve and clamping helpers shared by the configs.
    ///
    /// <para>The null guards matter more than they look: a designer duplicating a
    /// config asset in the project window, or a serialised curve added after an
    /// asset was authored, both yield a null AnimationCurve. Without a guard that
    /// is a NullReferenceException thrown from inside the AI tick on one specific
    /// machine, which is the worst possible place to discover an authoring gap.
    /// Validate() reports the null; Evaluate() keeps the game running meanwhile.</para>
    /// </summary>
    public static class ConfigCurve
    {
        /// <summary>Evaluates a curve, substituting <paramref name="fallback"/> when unusable.</summary>
        public static float Evaluate(AnimationCurve curve, float t, float fallback)
        {
            if (curve == null || curve.length == 0) return fallback;
            return curve.Evaluate(t);
        }

        /// <summary>Evaluates with the input clamped to 0..1 — the domain every response curve here uses.</summary>
        public static float Evaluate01(AnimationCurve curve, float t, float fallback)
        {
            if (curve == null || curve.length == 0) return fallback;
            return curve.Evaluate(math.saturate(t));
        }

        /// <summary>Evaluates with the input normalised by <paramref name="range"/> and clamped to 0..1.</summary>
        public static float EvaluateNormalised(AnimationCurve curve, float value, float range, float fallback)
        {
            if (curve == null || curve.length == 0) return fallback;
            float denominator = math.max(1e-4f, range);
            return curve.Evaluate(math.saturate(value / denominator));
        }

        /// <summary>Straight line from (0,<paramref name="a"/>) to (1,<paramref name="b"/>).</summary>
        public static AnimationCurve Linear01(float a, float b) => AnimationCurve.Linear(0f, a, 1f, b);

        /// <summary>Smoothstep-ish ramp from (0,<paramref name="a"/>) to (1,<paramref name="b"/>).</summary>
        public static AnimationCurve Ease01(float a, float b) => AnimationCurve.EaseInOut(0f, a, 1f, b);

        /// <summary>Three-key curve over the 0..1 domain, for curves with a deliberate knee.</summary>
        public static AnimationCurve Knee01(float a, float midT, float mid, float b)
        {
            return new AnimationCurve(
                new Keyframe(0f, a),
                new Keyframe(math.clamp(midT, 0.01f, 0.99f), mid),
                new Keyframe(1f, b));
        }

        /// <summary>Orders a min/max pair packed into a float2 and clamps it to sane bounds.</summary>
        public static float2 SanitiseRange(float2 range, float lowerBound, float upperBound)
        {
            float min = math.clamp(range.x, lowerBound, upperBound);
            float max = math.clamp(range.y, lowerBound, upperBound);
            if (max < min) max = min;
            return new float2(min, max);
        }
    }
}
