// -----------------------------------------------------------------------------
// Vigil — gameplay-facing contracts shared by Gameplay, AI, UI and Audio.
//
// These live in Core so that (for example) the AI can damage an IDamageable and
// the UI can render an IInteractable prompt without either taking a dependency
// on the other's assembly.
// -----------------------------------------------------------------------------

using Unity.Mathematics;

namespace Vigil.Core.Contracts
{
    // -------------------------------------------------------------------------
    // Interaction
    // -------------------------------------------------------------------------

    public enum InteractionKind : byte
    {
        Instant = 0,

        /// <summary>Requires the key held for a duration — doors being barricaded, etc.</summary>
        Hold = 1,

        /// <summary>Repeated taps. Used for struggle/escape events.</summary>
        Mash = 2,

        /// <summary>Opens a sub-UI (keypad, note, inventory container).</summary>
        Open = 3
    }

    public enum InteractionResult : byte
    {
        Success = 0,
        Blocked = 1,
        Locked = 2,
        RequiresItem = 3,
        Busy = 4,
        Failed = 5
    }

    /// <summary>What the HUD renders when the player looks at an interactable.</summary>
    public struct InteractionPrompt
    {
        /// <summary>e.g. "Open", "Barricade", "Start Generator".</summary>
        public string Verb;

        /// <summary>e.g. "Steel Door". May be empty.</summary>
        public string Subject;

        public InteractionKind Kind;

        /// <summary>Seconds for Hold interactions; ignored otherwise.</summary>
        public float Duration;

        /// <summary>False renders the prompt greyed out with <see cref="BlockedReason"/>.</summary>
        public bool Available;

        /// <summary>e.g. "Requires Maintenance Key".</summary>
        public string BlockedReason;

        public static InteractionPrompt None => new InteractionPrompt { Available = false };
    }

    /// <summary>
    /// Anything the player can interact with. Server-authoritative: clients raise a
    /// request, the server validates range and state, then replicates the result.
    /// </summary>
    public interface IInteractable
    {
        /// <summary>Networked entity id of the interactable.</summary>
        ulong EntityId { get; }

        /// <summary>Point the interaction ray should aim at.</summary>
        float3 InteractionPoint { get; }

        /// <summary>Maximum interaction distance in metres.</summary>
        float InteractionRange { get; }

        /// <summary>Prompt for a specific requester (may differ by held items).</summary>
        InteractionPrompt GetPrompt(ulong requesterId);

        /// <summary>Server-side validation. Must be side-effect free.</summary>
        bool CanInteract(ulong requesterId, out InteractionResult reason);

        /// <summary>Server-side execution. Only called after CanInteract passed.</summary>
        InteractionResult Interact(ulong requesterId);
    }

    // -------------------------------------------------------------------------
    // Damage / health
    // -------------------------------------------------------------------------

    public enum DamageType : byte
    {
        Generic = 0,
        Melee = 1,
        Grapple = 2,
        Environmental = 3,
        Fall = 4,

        /// <summary>Non-lethal psychological pressure. Drains composure, not health.</summary>
        Psychological = 5
    }

    public struct DamageInfo
    {
        public float Amount;
        public DamageType Type;

        /// <summary>Entity that caused it. 0 = world.</summary>
        public ulong InstigatorId;

        public float3 HitPoint;
        public float3 HitNormal;

        /// <summary>Simulation tick applied on — used for network reconciliation.</summary>
        public int Tick;

        /// <summary>Downs the target outright regardless of remaining health.</summary>
        public bool IsExecution;
    }

    public interface IDamageable
    {
        ulong EntityId { get; }
        float Health { get; }
        float MaxHealth { get; }
        bool IsAlive { get; }

        /// <summary>Server-only. Returns damage actually applied after mitigation.</summary>
        float ApplyDamage(in DamageInfo info);
    }

    // -------------------------------------------------------------------------
    // Composure (the horror resource)
    // -------------------------------------------------------------------------

    /// <summary>
    /// "Sanity" as a mechanic rather than a meter: composure drains in darkness and
    /// proximity to the antagonist, and recovers in light and near teammates. Low
    /// composure feeds back into perception — a panicking player breathes harder and
    /// is literally easier for the monster to hear, which closes the loop between
    /// the player's emotional state and the AI's behaviour.
    /// </summary>
    public interface IComposureSource
    {
        /// <summary>Composure delta per second contributed to a listener at a position.</summary>
        float GetComposureDelta(float3 listenerPosition, ulong listenerId);

        /// <summary>Beyond this distance the source contributes nothing.</summary>
        float InfluenceRadius { get; }

        float3 SourcePosition { get; }
    }

    public interface IComposureHolder
    {
        ulong EntityId { get; }

        /// <summary>0 = broken, 1 = calm.</summary>
        float Composure { get; }

        /// <summary>Multiplier applied to the noise this entity emits. Rises as composure falls.</summary>
        float NoiseMultiplier { get; }
    }

    // -------------------------------------------------------------------------
    // Team / faction
    // -------------------------------------------------------------------------

    public enum Faction : byte
    {
        Neutral = 0,
        Survivors = 1,
        Entity = 2,

        /// <summary>Ambient NPCs — hostile to nobody, useful as red herrings and noise sources.</summary>
        Wildlife = 3
    }

    public interface IFactionMember
    {
        Faction Faction { get; }
    }
}
