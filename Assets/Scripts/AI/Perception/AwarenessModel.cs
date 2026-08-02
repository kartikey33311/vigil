// -----------------------------------------------------------------------------
// Vigil — the agent's belief store.
//
// This is the agent's MODEL of the world, not ground truth. It is deliberately
// allowed to be stale, imprecise and wrong, because every interesting search
// behaviour in the game is downstream of the monster believing something slightly
// incorrect and acting on it with total conviction.
//
// Two properties are load-bearing:
//
//   HYSTERESIS. Decay is damped just BELOW a band threshold, so a target parked
//   exactly on the Alerted/Confirmed line does not flip bands every tick. Without
//   it the FSM oscillates and the monster reads as broken. AwarenessModelTests
//   asserts a bound on band changes specifically to stop this regressing.
//
//   POSITION CONFIDENCE. Separate from awareness, and decaying faster. The monster
//   can be certain you exist (high awareness) while having no idea where you are
//   (low confidence) — which is exactly the state that produces a wide, frantic
//   search rather than a beeline.
//
// Fixed-capacity arrays throughout. No Dictionary, no allocation, no GC.
// -----------------------------------------------------------------------------

using Unity.Mathematics;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Events;
using Vigil.Core.Simulation;
using Vigil.Data;

namespace Vigil.AI.Perception
{
    public sealed class AwarenessModel : IAwarenessModel
    {
        public const int MaxTargets = 8;
        public const int MaxInvestigationPoints = 6;

        struct InvestigationPoint
        {
            public float3 Position;
            public float Priority;
            public int Tick;
            public bool Active;
        }

        readonly PerceivedTarget[] _targets = new PerceivedTarget[MaxTargets];
        readonly bool[] _occupied = new bool[MaxTargets];
        readonly AwarenessLevel[] _lastPublishedLevel = new AwarenessLevel[MaxTargets];

        readonly InvestigationPoint[] _points = new InvestigationPoint[MaxInvestigationPoints];

        readonly PerceptionConfig _config;
        readonly IEventBus _events;
        readonly ulong _ownerId;

        float3 _ownerPosition;
        int _tick;

        public float PeakAwareness { get; private set; }
        public AwarenessLevel PeakLevel { get; private set; } = AwarenessLevel.Unaware;

        /// <summary>Number of targets currently believed in. Telemetry / debug overlay.</summary>
        public int KnownTargetCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < MaxTargets; i++) if (_occupied[i]) n++;
                return n;
            }
        }

        public AwarenessModel(ulong ownerId, PerceptionConfig config, IEventBus events)
        {
            _ownerId = ownerId;
            _config = config;
            _events = events;
        }

        /// <summary>Owner position, refreshed each tick before sensing so scoring can use it.</summary>
        public void SetOwnerPosition(float3 position) => _ownerPosition = position;

        // ----------------------------------------------------------- contribution

        public void Contribute(ulong targetId, float amount, in Stimulus stimulus, bool lineOfSight)
        {
            if (targetId == 0UL || amount <= 0f) return;

            int slot = FindOrCreate(targetId);
            if (slot < 0) return;

            ref PerceivedTarget t = ref _targets[slot];

            if (t.FirstSensedTick == 0) t.FirstSensedTick = _tick;

            t.Awareness = math.saturate(t.Awareness + amount);
            t.LastSensedTick = _tick;
            t.LastChannel = stimulus.Channel;
            t.HasLineOfSight = lineOfSight;

            if (lineOfSight)
            {
                // Direct sight is the only channel that fully refreshes belief. Sound
                // and scent update position with error applied by the sensor.
                t.LastKnownPosition = stimulus.Position;
                t.LastKnownVelocity = stimulus.Velocity;
                t.LastSeenTick = _tick;
                t.PositionConfidence = 1f;
            }
            else
            {
                // Blend toward the cue rather than snapping. A monster that teleports
                // its belief to every noise looks twitchy; one that gradually converges
                // looks like it is triangulating.
                float weight = math.saturate(amount * 2.5f);
                t.LastKnownPosition = math.lerp(t.LastKnownPosition, stimulus.Position, weight);
                t.PositionConfidence = math.max(t.PositionConfidence, weight * 0.7f);
            }

            UpdateLevel(slot);
        }

        public void ContributeAnonymous(in Stimulus stimulus, float amount)
        {
            if (amount <= 0f) return;

            // Merge into a nearby existing point rather than accumulating dozens of
            // near-identical positions — otherwise a running player fills the buffer
            // in a second and evicts everything genuinely interesting.
            const float MergeRadiusSq = 9f;

            int freeSlot = -1;
            int weakest = -1;
            float weakestPriority = float.MaxValue;

            for (int i = 0; i < MaxInvestigationPoints; i++)
            {
                if (!_points[i].Active)
                {
                    if (freeSlot < 0) freeSlot = i;
                    continue;
                }

                if (math.distancesq(_points[i].Position, stimulus.Position) < MergeRadiusSq)
                {
                    _points[i].Priority = math.max(_points[i].Priority, amount);
                    _points[i].Position = math.lerp(_points[i].Position, stimulus.Position, 0.5f);
                    _points[i].Tick = _tick;
                    return;
                }

                if (_points[i].Priority < weakestPriority)
                {
                    weakestPriority = _points[i].Priority;
                    weakest = i;
                }
            }

            int target = freeSlot >= 0 ? freeSlot : (amount > weakestPriority ? weakest : -1);
            if (target < 0) return;

            _points[target] = new InvestigationPoint
            {
                Position = stimulus.Position,
                Priority = amount,
                Tick = _tick,
                Active = true
            };
        }

        // ------------------------------------------------------------------- tick

        /// <summary>Applies decay. Must run once per agent tick, after sensing.</summary>
        public void Tick(in SimTime time)
        {
            _tick = time.Tick;
            float dt = time.DeltaTime;

            float peak = 0f;
            AwarenessLevel peakLevel = AwarenessLevel.Unaware;

            for (int i = 0; i < MaxTargets; i++)
            {
                if (!_occupied[i]) continue;

                ref PerceivedTarget t = ref _targets[i];

                float decay = _config != null ? _config.DecayPerSecond(t.Level) : 0.12f;

                // --- hysteresis ---------------------------------------------------
                // Damp decay while sitting just below a threshold we recently crossed.
                // This makes falling back OUT of a band harder than entering it, which
                // is the whole anti-oscillation mechanism.
                if (_config != null && IsJustBelowThreshold(t.Awareness, _config.HysteresisBand))
                {
                    decay *= _config.HysteresisDamping;
                }

                t.Awareness = math.max(0f, t.Awareness - decay * dt);

                // Confidence decays on its own, faster half-life. Exponential so it
                // never quite reaches zero — the monster keeps a vague idea forever.
                float halfLife = _config != null ? _config.ConfidenceHalfLife : 9f;
                t.PositionConfidence *= math.exp(-dt * 0.6931f / math.max(0.1f, halfLife));

                // Extrapolate belief along last known velocity while confidence holds.
                // This is why the monster cuts you off instead of always trailing you.
                if (t.PositionConfidence > 0.35f && !t.HasLineOfSight)
                {
                    t.LastKnownPosition += t.LastKnownVelocity * dt * t.PositionConfidence;
                }

                t.HasLineOfSight = false;   // sensors re-assert this every tick
                t.ThreatScore = ScoreThreat(in t);

                UpdateLevel(i);

                if (t.Awareness <= 0.001f && t.Level == AwarenessLevel.Unaware)
                {
                    Release(i);
                    continue;
                }

                if (t.Awareness > peak)
                {
                    peak = t.Awareness;
                    peakLevel = t.Level;
                }
            }

            PeakAwareness = peak;
            PeakLevel = peakLevel;

            ExpireInvestigationPoints(dt);
        }

        void ExpireInvestigationPoints(float dt)
        {
            for (int i = 0; i < MaxInvestigationPoints; i++)
            {
                if (!_points[i].Active) continue;

                _points[i].Priority -= dt * 0.08f;
                if (_points[i].Priority <= 0.02f) _points[i].Active = false;
            }
        }

        static bool IsJustBelowThreshold(float awareness, float band)
        {
            return (awareness < AwarenessThresholds.Suspicious && awareness > AwarenessThresholds.Suspicious - band)
                || (awareness < AwarenessThresholds.Alerted && awareness > AwarenessThresholds.Alerted - band)
                || (awareness < AwarenessThresholds.Confirmed && awareness > AwarenessThresholds.Confirmed - band);
        }

        void UpdateLevel(int slot)
        {
            ref PerceivedTarget t = ref _targets[slot];

            bool hadContact = t.LastSeenTick != 0;
            AwarenessLevel level = AwarenessThresholds.Classify(t.Awareness, hadContact);
            t.Level = level;

            if (_lastPublishedLevel[slot] == level) return;

            _lastPublishedLevel[slot] = level;

            if (VLog.Is(LogCat.Perception))
            {
                VLog.Info(LogCat.Perception, $"Agent {_ownerId}: target {t.TargetId} -> {level} ({t.Awareness:F2})");
            }

            _events?.Publish(new AwarenessChangedEvent
            {
                EntityId = _ownerId,
                TargetId = t.TargetId,
                Level = level
            });
        }

        float ScoreThreat(in PerceivedTarget t)
        {
            float distance = math.distance(_ownerPosition, t.LastKnownPosition);
            float proximity = 1f / (1f + distance * 0.05f);
            float recency = 1f / (1f + math.max(0, _tick - t.LastSensedTick) * 0.02f);

            return t.Awareness * 2f + proximity + recency * 0.5f + t.PositionConfidence * 0.5f;
        }

        // ---------------------------------------------------------------- queries

        public bool TryGetTarget(ulong targetId, out PerceivedTarget target)
        {
            for (int i = 0; i < MaxTargets; i++)
            {
                if (_occupied[i] && _targets[i].TargetId == targetId)
                {
                    target = _targets[i];
                    return true;
                }
            }

            target = default;
            return false;
        }

        public bool TryGetPrimaryTarget(out PerceivedTarget target)
        {
            float best = float.NegativeInfinity;
            int bestSlot = -1;

            for (int i = 0; i < MaxTargets; i++)
            {
                if (!_occupied[i]) continue;
                if (_targets[i].ThreatScore > best)
                {
                    best = _targets[i].ThreatScore;
                    bestSlot = i;
                }
            }

            if (bestSlot < 0) { target = default; return false; }

            target = _targets[bestSlot];
            return true;
        }

        public bool TryGetInvestigationPoint(out float3 position, out float priority)
        {
            float best = 0f;
            int bestSlot = -1;

            for (int i = 0; i < MaxInvestigationPoints; i++)
            {
                if (!_points[i].Active) continue;
                if (_points[i].Priority > best)
                {
                    best = _points[i].Priority;
                    bestSlot = i;
                }
            }

            if (bestSlot < 0) { position = default; priority = 0f; return false; }

            position = _points[bestSlot].Position;
            priority = best;
            return true;
        }

        /// <summary>Clears the investigation point nearest a position — called once it has been checked.</summary>
        public void ClearInvestigationPointNear(float3 position, float radius)
        {
            float radiusSq = radius * radius;
            for (int i = 0; i < MaxInvestigationPoints; i++)
            {
                if (!_points[i].Active) continue;
                if (math.distancesq(_points[i].Position, position) <= radiusSq) _points[i].Active = false;
            }
        }

        public void Forget(ulong targetId)
        {
            for (int i = 0; i < MaxTargets; i++)
            {
                if (_occupied[i] && _targets[i].TargetId == targetId) Release(i);
            }
        }

        // ------------------------------------------------------------------ slots

        int FindOrCreate(ulong targetId)
        {
            int free = -1;
            int weakest = -1;
            float weakestScore = float.MaxValue;

            for (int i = 0; i < MaxTargets; i++)
            {
                if (!_occupied[i])
                {
                    if (free < 0) free = i;
                    continue;
                }

                if (_targets[i].TargetId == targetId) return i;

                if (_targets[i].ThreatScore < weakestScore)
                {
                    weakestScore = _targets[i].ThreatScore;
                    weakest = i;
                }
            }

            int slot = free >= 0 ? free : weakest;
            if (slot < 0) return -1;

            _targets[slot] = new PerceivedTarget
            {
                TargetId = targetId,
                Awareness = 0f,
                Level = AwarenessLevel.Unaware,
                PositionConfidence = 0f,
                FirstSensedTick = _tick,
                LastSensedTick = _tick
            };

            _occupied[slot] = true;
            _lastPublishedLevel[slot] = AwarenessLevel.Unaware;
            return slot;
        }

        void Release(int slot)
        {
            _occupied[slot] = false;
            _targets[slot] = default;
            _lastPublishedLevel[slot] = AwarenessLevel.Unaware;
        }

        /// <summary>Wipes all belief. Used on despawn and between test cases.</summary>
        public void Reset()
        {
            for (int i = 0; i < MaxTargets; i++) Release(i);
            for (int i = 0; i < MaxInvestigationPoints; i++) _points[i].Active = false;
            PeakAwareness = 0f;
            PeakLevel = AwarenessLevel.Unaware;
        }
    }
}
