// -----------------------------------------------------------------------------
// Vigil — sensory modalities.
//
// Sensors WRITE into the awareness model. They never make decisions; the FSM reads
// the model and decides. Keeping that boundary is what lets perception be retuned
// without touching behaviour, and lets behaviour be unit-tested by writing directly
// into a model with no scene present.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Mathx;
using Vigil.Core.Simulation;
using Vigil.Data;

namespace Vigil.AI.Perception
{
    /// <summary>
    /// What a sensor needs to know about the body it is attached to. Narrow on
    /// purpose: sensors must remain testable without a MonoBehaviour.
    /// </summary>
    public interface ISensorHost
    {
        ulong OwnerId { get; }
        float3 EyePosition { get; }
        float3 EyeForward { get; }

        /// <summary>Ambient darkness at a world position, 0 (lit) .. 1 (pitch black).</summary>
        float DarknessAt(float3 position);
    }

    // =========================================================================
    // Vision — the PULL sensor
    // =========================================================================

    public sealed class VisionSensor : ISensor
    {
        readonly ISensorHost _host;
        readonly PerceptionConfig _config;
        readonly PerceptionBus _bus;

        readonly float3[] _sampleBuffer = new float3[8];
        int _roundRobinCursor;

        public StimulusChannel Channel => StimulusChannel.Sight;
        public bool Enabled { get; set; } = true;

        /// <summary>Perceivables actually evaluated on the most recent tick. Telemetry.</summary>
        public int LastEvaluatedCount { get; private set; }

        public VisionSensor(ISensorHost host, PerceptionConfig config, PerceptionBus bus)
        {
            _host = host;
            _config = config;
            _bus = bus;
        }

        public void Sense(in SimTime time, IAwarenessModel model)
        {
            if (!Enabled || _config == null || _bus == null || model == null) return;

            IReadOnlyList<IPerceivable> all = _bus.Perceivables;
            int total = all.Count;
            if (total == 0) { LastEvaluatedCount = 0; return; }

            int budget = math.min(total, _config.MaxVisionCandidatesPerTick);
            LastEvaluatedCount = budget;

            float3 eye = _host.EyePosition;
            float3 forward = _host.EyeForward;
            float maxRange = _config.MaxSightRange;
            float maxRangeSq = maxRange * maxRange;
            int occlusionMask = _config.OcclusionMask.value;

            // Gain scaled so that ideal conditions reach Confirmed in MinTimeToDetect.
            float baseGain = 1f / math.max(0.05f, _config.MinTimeToDetect);

            for (int n = 0; n < budget; n++)
            {
                int i = (_roundRobinCursor + n) % total;
                IPerceivable p = all[i];

                if (p == null || !p.IsPerceivable) continue;
                if (p.EntityId == _host.OwnerId) continue;

                float3 targetPos = p.PerceivablePosition;
                float3 toTarget = targetPos - eye;

                float distSq = math.lengthsq(toTarget);
                if (distSq > maxRangeSq || distSq < 1e-5f) continue;

                float distance = math.sqrt(distSq);
                float3 dir = toTarget / distance;

                // ---- band selection ------------------------------------------------
                // Three bands rather than one cone: a single cone has a hard edge that
                // players learn to hug, and a blind spot directly behind that becomes
                // an exploit.
                float cosAngle = math.dot(forward, dir);
                float angleDeg = math.degrees(math.acos(math.clamp(cosAngle, -1f, 1f)));

                float bandFactor;
                if (distance <= _config.NearRange)
                {
                    bandFactor = 1f;                                  // near-omnidirectional
                }
                else if (angleDeg <= _config.FocusHalfAngle && distance <= _config.FocusRange)
                {
                    bandFactor = 1f;
                }
                else if (angleDeg <= _config.MidHalfAngle && distance <= _config.MidRange)
                {
                    bandFactor = 0.75f;
                }
                else
                {
                    continue;
                }

                // ---- line of sight -------------------------------------------------
                int sampleCount = p.GetVisibilitySamples(ConvertBuffer());
                if (sampleCount <= 0) continue;

                int visible = 0;
                for (int s = 0; s < sampleCount && s < _sampleBuffer.Length; s++)
                {
                    Vector3 samplePoint = _sampleBuffer[s];
                    if (!Physics.Linecast(eye, samplePoint, occlusionMask, QueryTriggerInteraction.Ignore))
                    {
                        visible++;
                    }
                }

                if (visible == 0) continue;

                float exposure = (float)visible / sampleCount;

                // ---- response ------------------------------------------------------
                float normalisedDist = math.saturate(distance / maxRange);
                float normalisedAngle = math.saturate(angleDeg / math.max(1f, _config.MidHalfAngle));

                float darkness = _host.DarknessAt(targetPos);
                float lightFactor = math.lerp(1f, _config.DarknessVisibility, math.saturate(darkness));

                float gain = baseGain
                             * bandFactor
                             * exposure
                             * lightFactor
                             * p.VisibilityScale
                             * _config.SightGainByDistance(normalisedDist)
                             * _config.SightGainByAngle(normalisedAngle)
                             * _config.ChannelGain(StimulusChannel.Sight)
                             * time.DeltaTime;

                if (gain <= 0f) continue;

                Stimulus stimulus = new Stimulus(
                    StimulusChannel.Sight,
                    StimulusTag.None,
                    targetPos,
                    exposure,
                    maxRange,
                    p.EntityId,
                    time.Tick);

                model.Contribute(p.EntityId, gain, in stimulus, lineOfSight: true);
            }

            _roundRobinCursor = (_roundRobinCursor + budget) % total;
        }

        // GetVisibilitySamples takes float3[]; kept as a method so the buffer is
        // never exposed as a mutable field to callers.
        float3[] ConvertBuffer() => _sampleBuffer;
    }

    // =========================================================================
    // Hearing — PUSH, with deliberate localisation error
    // =========================================================================

    public sealed class HearingSensor : ISensor
    {
        const int QueueCapacity = 32;

        readonly ISensorHost _host;
        readonly PerceptionConfig _config;

        readonly Stimulus[] _queue = new Stimulus[QueueCapacity];
        int _head, _count;

        DeterministicRandom _rng;

        public StimulusChannel Channel => StimulusChannel.Sound;
        public bool Enabled { get; set; } = true;

        /// <summary>Stimuli dropped because the queue was full. Non-zero means the level is very loud.</summary>
        public int DroppedCount { get; private set; }

        public HearingSensor(ISensorHost host, PerceptionConfig config, uint seed)
        {
            _host = host;
            _config = config;
            _rng = new DeterministicRandom(seed);
        }

        /// <summary>Called from the owning agent's IStimulusReceiver.OnStimulus. Must be cheap.</summary>
        public void Enqueue(in Stimulus stimulus)
        {
            if (stimulus.Channel != StimulusChannel.Sound && stimulus.Channel != StimulusChannel.Environmental) return;

            if (_count >= QueueCapacity)
            {
                // Drop the oldest rather than the newest: recent noise is more
                // actionable than a stale one, and a full queue means the situation
                // is loud enough that precision no longer matters.
                _head = (_head + 1) % QueueCapacity;
                _count--;
                DroppedCount++;
            }

            _queue[(_head + _count) % QueueCapacity] = stimulus;
            _count++;
        }

        public void Sense(in SimTime time, IAwarenessModel model)
        {
            if (!Enabled || _config == null || model == null) { _count = 0; return; }

            float3 ear = _host.EyePosition;
            int occlusionMask = _config.OcclusionMask.value;

            while (_count > 0)
            {
                Stimulus s = _queue[_head];
                _head = (_head + 1) % QueueCapacity;
                _count--;

                float intensity = s.IntensityAt(ear);
                if (intensity <= 0.001f) continue;

                // ---- occlusion ------------------------------------------------------
                int blockers = CountBlockers(s.Position, ear, occlusionMask);
                float attenuation = math.pow(_config.OcclusionTransmission, blockers);
                float heard = intensity * attenuation;

                if (heard <= 0.01f) continue;

                // ---- localisation error --------------------------------------------
                // THE tension knob. A sound through two walls localises badly, so the
                // monster investigates the wrong place and the player watches it do so.
                // Modelling this as a feature rather than an inaccuracy is the single
                // best decision in the perception system.
                float errorMagnitude = _config.SoundLocalisationError * (1f - attenuation);
                float3 perceivedPosition = s.Position;
                if (errorMagnitude > 0.01f)
                {
                    perceivedPosition += _rng.NextPointInDiscXZ(errorMagnitude);
                }

                float gain = heard
                             * _config.ChannelGain(s.Channel)
                             * _config.TagWeightFor(s.Tag);

                if (gain <= 0f) continue;

                Stimulus perceived = new Stimulus(
                    s.Channel, s.Tag, perceivedPosition, heard, s.Radius, s.SourceId, time.Tick, s.Velocity);

                if (s.SourceId != 0UL)
                {
                    model.Contribute(s.SourceId, gain, in perceived, lineOfSight: false);
                }
                else
                {
                    model.ContributeAnonymous(in perceived, gain);
                }
            }
        }

        int CountBlockers(float3 from, float3 to, int mask)
        {
            // A single Linecast tells us blocked/not; counting surfaces needs the
            // ray to continue. RaycastNonAlloc with a shared buffer keeps this
            // allocation-free.
            float3 delta = to - from;
            float distance = math.length(delta);
            if (distance < 1e-3f) return 0;

            float3 dir = delta / distance;
            int hits = Physics.RaycastNonAlloc(
                new Ray(from, dir), _blockerBuffer, distance, mask, QueryTriggerInteraction.Ignore);

            // Clamped to the buffer: past a few surfaces the sound is inaudible
            // anyway, so counting more precisely buys nothing.
            return math.clamp(hits, 0, _blockerBuffer.Length);
        }

        static readonly RaycastHit[] _blockerBuffer = new RaycastHit[8];
    }

    // =========================================================================
    // Scent — trail following
    // =========================================================================

    /// <summary>
    /// Decaying breadcrumb trail dropped by players. Lets the monster follow where
    /// you WENT rather than where you are, which is what makes doubling back a real
    /// tactic and running in a straight line a mistake.
    /// </summary>
    public sealed class ScentTrail
    {
        public struct Marker
        {
            public float3 Position;
            public ulong OwnerId;
            public float Age;
            public bool Active;
        }

        readonly Marker[] _markers;
        int _cursor;

        public ScentTrail(int capacity = 128)
        {
            _markers = new Marker[capacity];
        }

        public void Drop(float3 position, ulong ownerId)
        {
            // Ring buffer: oldest marker is naturally overwritten, so the trail has a
            // bounded length with no bookkeeping.
            _markers[_cursor] = new Marker { Position = position, OwnerId = ownerId, Age = 0f, Active = true };
            _cursor = (_cursor + 1) % _markers.Length;
        }

        public void Tick(float dt, float lifetime)
        {
            for (int i = 0; i < _markers.Length; i++)
            {
                if (!_markers[i].Active) continue;
                _markers[i].Age += dt;
                if (_markers[i].Age >= lifetime) _markers[i].Active = false;
            }
        }

        /// <summary>Freshest marker within range, or false.</summary>
        public bool TryGetFreshest(float3 near, float range, out Marker result)
        {
            float rangeSq = range * range;
            float bestAge = float.MaxValue;
            int best = -1;

            for (int i = 0; i < _markers.Length; i++)
            {
                if (!_markers[i].Active) continue;
                if (math.distancesq(_markers[i].Position, near) > rangeSq) continue;
                if (_markers[i].Age < bestAge)
                {
                    bestAge = _markers[i].Age;
                    best = i;
                }
            }

            if (best < 0) { result = default; return false; }
            result = _markers[best];
            return true;
        }

        public void Clear()
        {
            for (int i = 0; i < _markers.Length; i++) _markers[i].Active = false;
            _cursor = 0;
        }
    }

    public sealed class ScentSensor : ISensor
    {
        readonly ISensorHost _host;
        readonly PerceptionConfig _config;
        readonly ScentTrail _trail;

        public StimulusChannel Channel => StimulusChannel.Scent;
        public bool Enabled { get; set; } = true;

        public ScentSensor(ISensorHost host, PerceptionConfig config, ScentTrail trail)
        {
            _host = host;
            _config = config;
            _trail = trail;
        }

        public void Sense(in SimTime time, IAwarenessModel model)
        {
            if (!Enabled || _config == null || _trail == null || model == null) return;
            if (_config.ScentLifetime <= 0f) return;

            if (!_trail.TryGetFreshest(_host.EyePosition, _config.ScentRange, out ScentTrail.Marker marker)) return;

            // Fresher trail = stronger signal. An old trail still registers, but only
            // enough to nudge the monster in a direction, not to commit it.
            float freshness = 1f - math.saturate(marker.Age / _config.ScentLifetime);
            float gain = freshness * _config.ChannelGain(StimulusChannel.Scent) * time.DeltaTime;

            if (gain <= 0f) return;

            Stimulus stimulus = new Stimulus(
                StimulusChannel.Scent, StimulusTag.None, marker.Position,
                freshness, _config.ScentRange, marker.OwnerId, time.Tick);

            model.Contribute(marker.OwnerId, gain, in stimulus, lineOfSight: false);
        }
    }

    // =========================================================================
    // Touch
    // =========================================================================

    public sealed class TouchSensor : ISensor
    {
        readonly PerceptionConfig _config;

        ulong _pendingContactId;
        float3 _pendingPosition;
        bool _hasContact;

        public StimulusChannel Channel => StimulusChannel.Touch;
        public bool Enabled { get; set; } = true;

        public TouchSensor(PerceptionConfig config)
        {
            _config = config;
        }

        /// <summary>Reported by the agent's collision handling.</summary>
        public void ReportContact(ulong entityId, float3 position)
        {
            _pendingContactId = entityId;
            _pendingPosition = position;
            _hasContact = true;
        }

        public void Sense(in SimTime time, IAwarenessModel model)
        {
            if (!Enabled || !_hasContact || model == null) { _hasContact = false; return; }

            _hasContact = false;
            if (_pendingContactId == 0UL) return;

            // Physical contact is unambiguous — it jumps straight to Confirmed.
            // There is no plausible reading in which the monster touches you and is
            // merely "suspicious".
            Stimulus stimulus = new Stimulus(
                StimulusChannel.Touch, StimulusTag.Impact, _pendingPosition,
                1f, 2f, _pendingContactId, time.Tick);

            model.Contribute(_pendingContactId, 1f, in stimulus, lineOfSight: true);
        }
    }

    // =========================================================================
    // Rig
    // =========================================================================

    /// <summary>
    /// An agent's complete sensory apparatus. Plain class, no MonoBehaviour, so the
    /// whole perception stack can be exercised in an EditMode test.
    /// </summary>
    public sealed class PerceptionRig
    {
        readonly ISensor[] _sensors;

        public AwarenessModel Model { get; }
        public VisionSensor Vision { get; }
        public HearingSensor Hearing { get; }
        public ScentSensor Scent { get; }
        public TouchSensor Touch { get; }

        public ISensor[] Sensors => _sensors;

        public PerceptionRig(
            ISensorHost host,
            PerceptionConfig config,
            PerceptionBus bus,
            ScentTrail trail,
            AwarenessModel model,
            uint seed)
        {
            Model = model;
            Vision = new VisionSensor(host, config, bus);
            Hearing = new HearingSensor(host, config, seed);
            Scent = new ScentSensor(host, config, trail);
            Touch = new TouchSensor(config);

            _sensors = new ISensor[] { Vision, Hearing, Scent, Touch };
        }

        /// <summary>Runs every sensor then applies decay. Order matters: sense, then decay.</summary>
        public void Tick(in SimTime time, IAwarenessModel model)
        {
            for (int i = 0; i < _sensors.Length; i++)
            {
                if (_sensors[i].Enabled) _sensors[i].Sense(in time, model);
            }

            Model.Tick(in time);
        }
    }
}
