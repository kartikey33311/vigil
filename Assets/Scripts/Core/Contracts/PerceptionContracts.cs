// -----------------------------------------------------------------------------
// Vigil — perception contracts.
//
// The design goal is that the antagonist NEVER has binary knowledge of a player.
// Binary "can see / cannot see" AI reads as either omniscient or blind, and both
// kill tension. Instead every agent maintains a continuous *awareness* value per
// target that accrues from stimuli and decays over time, crossing named
// thresholds (Suspicious -> Alerted -> Confirmed). That single change is what
// produces the "it heard something and is coming to look" beat that horror runs on.
//
// Two delivery models, chosen per channel for cost reasons:
//   PUSH (sound, scent, damage, environmental) — sparse, event-driven, broadcast
//        through PerceptionBus to receivers in range. Costs nothing when quiet.
//   PULL (sight) — polled by the agent on its own tick budget, because vision
//        must be evaluated against the agent's current head transform.
//
// All positions are float3 world-space. All ids are the networked entity id so
// that server and client agree on who was perceived.
// -----------------------------------------------------------------------------

using Unity.Mathematics;
using Vigil.Core.Simulation;

namespace Vigil.Core.Contracts
{
    /// <summary>Sensory modality that produced a stimulus.</summary>
    public enum StimulusChannel : byte
    {
        Sight = 0,
        Sound = 1,
        Scent = 2,
        Touch = 3,
        Environmental = 4,
        Damage = 5
    }

    /// <summary>
    /// Semantic label on a stimulus. Agents weight tags differently: a Gunshot is
    /// worth far more awareness than a Footstep, and a Decoy is deliberately
    /// over-weighted so players can exploit it.
    /// </summary>
    [System.Flags]
    public enum StimulusTag : uint
    {
        None        = 0,
        Footstep    = 1u << 0,
        Sprint      = 1u << 1,
        Voice       = 1u << 2,
        Door        = 1u << 3,
        Breakage    = 1u << 4,
        Impact      = 1u << 5,
        Flashlight  = 1u << 6,
        Machinery   = 1u << 7,
        Radio       = 1u << 8,
        Decoy       = 1u << 9,
        Scream      = 1u << 10,
        Heartbeat   = 1u << 11,
        Corpse      = 1u << 12,
        Blood       = 1u << 13
    }

    /// <summary>Named awareness bands. Behaviour selection keys off these, not raw floats.</summary>
    public enum AwarenessLevel : byte
    {
        /// <summary>No knowledge of the target.</summary>
        Unaware = 0,

        /// <summary>Something registered. Agent orients / pauses, does not commit.</summary>
        Suspicious = 1,

        /// <summary>Committed to investigating a position. Moves to it.</summary>
        Alerted = 2,

        /// <summary>Target identified and localised. Pursuit is authorised.</summary>
        Confirmed = 3,

        /// <summary>Was Confirmed, contact broken. Drives search behaviour.</summary>
        Lost = 4
    }

    /// <summary>Awareness thresholds. Shared so UI, AI and audio agree on the bands.</summary>
    public static class AwarenessThresholds
    {
        public const float Suspicious = 0.25f;
        public const float Alerted    = 0.55f;
        public const float Confirmed  = 0.85f;

        public static AwarenessLevel Classify(float awareness, bool hadContact)
        {
            if (awareness >= Confirmed) return AwarenessLevel.Confirmed;
            if (awareness >= Alerted)   return AwarenessLevel.Alerted;
            if (awareness >= Suspicious) return AwarenessLevel.Suspicious;
            return hadContact ? AwarenessLevel.Lost : AwarenessLevel.Unaware;
        }
    }

    /// <summary>
    /// A single sensory event. Immutable, blittable, cheap to copy — safe to queue
    /// and to pass to Burst jobs.
    /// </summary>
    public readonly struct Stimulus
    {
        public readonly StimulusChannel Channel;
        public readonly StimulusTag Tag;

        /// <summary>World position the stimulus originated from.</summary>
        public readonly float3 Position;

        /// <summary>Source velocity when known — lets an agent lead a moving target.</summary>
        public readonly float3 Velocity;

        /// <summary>Normalised strength at the source, 0..1.</summary>
        public readonly float Intensity;

        /// <summary>World distance at which intensity falls to zero.</summary>
        public readonly float Radius;

        /// <summary>Networked entity id of the emitter. 0 = world / no owner.</summary>
        public readonly ulong SourceId;

        /// <summary>Simulation tick the stimulus was emitted on.</summary>
        public readonly int Tick;

        public Stimulus(
            StimulusChannel channel,
            StimulusTag tag,
            float3 position,
            float intensity,
            float radius,
            ulong sourceId,
            int tick,
            float3 velocity = default)
        {
            Channel = channel;
            Tag = tag;
            Position = position;
            Velocity = velocity;
            Intensity = math.saturate(intensity);
            Radius = math.max(0.01f, radius);
            SourceId = sourceId;
            Tick = tick;
        }

        /// <summary>
        /// Intensity as experienced at <paramref name="listener"/>, before occlusion.
        /// Inverse-square-ish falloff, clamped, so distant sounds fade smoothly to
        /// nothing instead of popping at the radius boundary.
        /// </summary>
        public float IntensityAt(float3 listener)
        {
            float distSq = math.distancesq(Position, listener);
            float radiusSq = Radius * Radius;
            if (distSq >= radiusSq) return 0f;
            float t = 1f - (distSq / radiusSq);
            return Intensity * t * t;
        }

        public override string ToString() => $"{Channel}/{Tag} i={Intensity:F2} r={Radius:F1} @{Position}";
    }

    /// <summary>
    /// What an agent believes about one target. This is the agent's *model* of the
    /// world, not ground truth — deliberately allowed to be stale or wrong, which
    /// is where believable search behaviour comes from.
    /// </summary>
    public struct PerceivedTarget
    {
        public ulong TargetId;

        /// <summary>Where the agent thinks the target is. May be badly out of date.</summary>
        public float3 LastKnownPosition;

        /// <summary>Velocity at last contact, used to extrapolate a search origin.</summary>
        public float3 LastKnownVelocity;

        /// <summary>Tick of the most recent stimulus attributed to this target.</summary>
        public int LastSensedTick;

        /// <summary>Tick the agent first became aware of this target this encounter.</summary>
        public int FirstSensedTick;

        /// <summary>Tick line of sight was last actually confirmed.</summary>
        public int LastSeenTick;

        /// <summary>Accumulated awareness, 0..1.</summary>
        public float Awareness;

        public AwarenessLevel Level;

        /// <summary>Channel that most recently contributed awareness.</summary>
        public StimulusChannel LastChannel;

        /// <summary>True if line of sight is established right now.</summary>
        public bool HasLineOfSight;

        /// <summary>
        /// Positional confidence 0..1, decaying with time since last contact.
        /// Search radius is derived from this — low confidence means search wide.
        /// </summary>
        public float PositionConfidence;

        /// <summary>Composite threat/priority score used to pick a target among several.</summary>
        public float ThreatScore;

        public bool IsValid => TargetId != 0UL;
    }

    /// <summary>Something that can be seen. Implemented by players and by decoys.</summary>
    public interface IPerceivable
    {
        /// <summary>Networked entity id. Stable across the session.</summary>
        ulong EntityId { get; }

        /// <summary>World position of the body centre.</summary>
        float3 PerceivablePosition { get; }

        /// <summary>Sample points used for line-of-sight tests (head, torso, feet).</summary>
        int GetVisibilitySamples(float3[] buffer);

        /// <summary>
        /// Multiplier on how easily this is seen: crouching, standing still, being
        /// in shadow, or actively hiding all push this toward 0. A lit flashlight
        /// pushes it above 1.
        /// </summary>
        float VisibilityScale { get; }

        /// <summary>False while dead, despawned, or inside a hiding volume that grants immunity.</summary>
        bool IsPerceivable { get; }
    }

    /// <summary>Receives pushed stimuli (sound / scent / damage / environmental).</summary>
    public interface IStimulusReceiver
    {
        /// <summary>Where this receiver listens from.</summary>
        float3 ListenerPosition { get; }

        /// <summary>Maximum range at which this receiver can register anything.</summary>
        float ListenerRange { get; }

        /// <summary>False while stunned, dormant or despawned.</summary>
        bool IsListening { get; }

        /// <summary>
        /// Called on the simulation thread. Must be cheap and must not allocate —
        /// implementations should enqueue and process on their own tick.
        /// </summary>
        void OnStimulus(in Stimulus stimulus);
    }

    /// <summary>
    /// Global routing for pushed stimuli. Implemented in Vigil.AI, resolved from
    /// the ServiceContext. Server-authoritative: clients never broadcast directly.
    /// </summary>
    public interface IPerceptionBus
    {
        void Register(IStimulusReceiver receiver);
        void Unregister(IStimulusReceiver receiver);

        /// <summary>Routes a stimulus to every receiver in range. Allocation-free.</summary>
        void Broadcast(in Stimulus stimulus);

        /// <summary>Registered receiver count (telemetry / tests).</summary>
        int ReceiverCount { get; }
    }

    /// <summary>
    /// One sensory modality attached to an agent. Sight is the only PULL sensor;
    /// the rest consume the receiver queue. Sensors write into the agent's
    /// awareness model rather than making decisions themselves.
    /// </summary>
    public interface ISensor
    {
        StimulusChannel Channel { get; }
        bool Enabled { get; set; }

        /// <summary>
        /// Evaluate this modality for one tick and push results into the model.
        /// </summary>
        void Sense(in SimTime time, IAwarenessModel model);
    }

    /// <summary>
    /// The agent's belief store. Sensors write, behaviours read.
    /// </summary>
    public interface IAwarenessModel
    {
        /// <summary>Contributes awareness about a target from a sensory event.</summary>
        void Contribute(ulong targetId, float amount, in Stimulus stimulus, bool lineOfSight);

        /// <summary>Records a positional cue with no attributable target (a noise in the dark).</summary>
        void ContributeAnonymous(in Stimulus stimulus, float amount);

        /// <summary>Reads the current belief about a target.</summary>
        bool TryGetTarget(ulong targetId, out PerceivedTarget target);

        /// <summary>Highest-scoring target, or false when the agent knows of none.</summary>
        bool TryGetPrimaryTarget(out PerceivedTarget target);

        /// <summary>Best unresolved point of interest to investigate, if any.</summary>
        bool TryGetInvestigationPoint(out float3 position, out float priority);

        /// <summary>Clears belief about a target (used on despawn / player death).</summary>
        void Forget(ulong targetId);

        /// <summary>Peak awareness across all known targets — drives music and UI.</summary>
        float PeakAwareness { get; }

        AwarenessLevel PeakLevel { get; }
    }
}
