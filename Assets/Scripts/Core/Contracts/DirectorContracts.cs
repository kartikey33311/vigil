// -----------------------------------------------------------------------------
// Vigil — AI Director contracts.
//
// The single most important system in a co-op horror game, and the one most
// often missing from clones. A monster with good pathfinding and a good FSM is
// still exhausting if it applies constant pressure — players acclimatise within
// ~10 minutes and fear collapses into irritation. Fear is a DERIVATIVE: it comes
// from the transition between safety and threat, not from the threat itself.
//
// So the Director owns a pacing curve (Left 4 Dead's insight, adapted for a
// single stalking antagonist rather than hordes):
//
//   Buildup  — the entity closes distance, cues intensify, but no contact.
//   Peak     — contact authorised; the chase happens.
//   Relax    — the entity is FORCED to disengage and withdraw, regardless of how
//              well it was doing. This is the counter-intuitive part, and it is
//              exactly what makes the next Buildup land.
//   Fadeout  — near-silence. Players regroup, loot, talk. Then it starts again.
//
// The Director measures per-player "stress" (recent damage, proximity, time in
// darkness, isolation from teammates) and drives the curve from it, so pacing
// adapts to the actual table rather than a fixed timer.
// -----------------------------------------------------------------------------

using Unity.Mathematics;

namespace Vigil.Core.Contracts
{
    public enum DreadPhase : byte
    {
        /// <summary>Pressure rising. Entity approaches, ambience thickens. No contact.</summary>
        Buildup = 0,

        /// <summary>Contact authorised. The chase.</summary>
        Peak = 1,

        /// <summary>Mandatory disengage. The entity withdraws even if winning.</summary>
        Relax = 2,

        /// <summary>Quiet. Recovery, regrouping, exploration.</summary>
        Fadeout = 3
    }

    /// <summary>Per-player stress readings the Director aggregates.</summary>
    public struct PlayerStressSample
    {
        public ulong PlayerId;
        public float3 Position;

        /// <summary>0..1 composite of recent damage, chases survived and proximity.</summary>
        public float Stress;

        /// <summary>0..1 composure remaining.</summary>
        public float Composure;

        /// <summary>Metres to the nearest teammate. Large values mean isolated.</summary>
        public float IsolationDistance;

        /// <summary>Seconds since this player was last in a chase.</summary>
        public float TimeSinceLastThreat;

        /// <summary>0..1 ambient darkness at the player's position.</summary>
        public float Darkness;

        public bool IsAlive;
        public bool IsDowned;
    }

    /// <summary>
    /// A request from the Director to the antagonist's behaviour layer. The
    /// Director never drives the entity directly — it sets intent and constraints,
    /// and the FSM decides how to satisfy them. Keeping that boundary is what stops
    /// the entity looking puppeteered.
    /// </summary>
    public struct DirectorIntent
    {
        public DreadPhase Phase;

        /// <summary>0..1 overall pressure. Scales speed, hearing acuity and repath rate.</summary>
        public float Pressure;

        /// <summary>False during Relax/Fadeout — the entity may stalk but must not engage.</summary>
        public bool EngagementAuthorised;

        /// <summary>Player the Director wants pressured. 0 = no preference.</summary>
        public ulong PreferredTargetId;

        /// <summary>Region the entity should gravitate toward. 0 = no preference.</summary>
        public int PreferredRegion;

        /// <summary>Metres the entity should keep during Buildup — close enough to be heard, not seen.</summary>
        public float DesiredStandoffDistance;

        /// <summary>Multiplier on movement speed, 0.5..1.5.</summary>
        public float SpeedScale;

        public static DirectorIntent Default => new DirectorIntent
        {
            Phase = DreadPhase.Fadeout,
            Pressure = 0f,
            EngagementAuthorised = false,
            PreferredTargetId = 0UL,
            PreferredRegion = 0,
            DesiredStandoffDistance = 25f,
            SpeedScale = 1f
        };
    }

    /// <summary>Discrete events the Director reacts to.</summary>
    public enum DirectorEvent : byte
    {
        PlayerDowned = 0,
        PlayerRevived = 1,
        PlayerKilled = 2,
        ChaseStarted = 3,
        ChaseEnded = 4,
        ObjectiveCompleted = 5,
        GeneratorStarted = 6,
        PlayerHid = 7,
        EntityStunned = 8,
        LoudNoise = 9
    }

    public interface IAIDirector
    {
        DreadPhase Phase { get; }

        /// <summary>0..1 aggregated table stress.</summary>
        float TableStress { get; }

        /// <summary>Seconds spent in the current phase.</summary>
        float TimeInPhase { get; }

        /// <summary>Current intent for the antagonist.</summary>
        DirectorIntent CurrentIntent { get; }

        /// <summary>Server-only. Reports a discrete gameplay event.</summary>
        void Report(DirectorEvent evt, ulong instigatorId, float3 position);

        /// <summary>Forces a phase. Used by scripted set-pieces and by tests.</summary>
        void ForcePhase(DreadPhase phase);

        /// <summary>Raised on phase change — audio and UI subscribe.</summary>
        event System.Action<DreadPhase, DreadPhase> OnPhaseChanged;
    }
}
