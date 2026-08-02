// -----------------------------------------------------------------------------
// Vigil — the AI Director.
//
// Owns pacing. It never drives the antagonist directly — it publishes intent and
// constraints, and the FSM decides how to satisfy them. Keeping that boundary is
// what stops the monster looking puppeteered: the player sees an animal making
// decisions, not a spawner obeying a script.
//
// The load-bearing rule in this file is the MAXIMUM phase duration. It forces
// Relax even while the entity is winning and the table is at peak stress. That is
// counter-intuitive and it will look, to anyone reading this later, like a bug or
// a missed optimisation. It is neither. Fear is a derivative: it comes from the
// transition between safety and threat, not from threat itself. Remove the lull
// and players acclimatise within about ten minutes, after which the monster stops
// being frightening and becomes tedious. DirectorTests asserts this clamp
// explicitly so it cannot be quietly "fixed".
// -----------------------------------------------------------------------------

using System;
using Unity.Mathematics;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;
using Vigil.Core.Events;
using Vigil.Core.Simulation;
using Vigil.Data;

namespace Vigil.AI.Director
{
    /// <summary>
    /// Supplies the Director with per-player state each tick. Implemented in
    /// Vigil.Gameplay — the Director itself must not know what a player IS.
    /// </summary>
    public interface IDirectorPlayerSource
    {
        /// <summary>Fills the buffer with live player samples; returns the count written.</summary>
        int GetPlayerSamples(PlayerStressSample[] buffer);

        /// <summary>Current antagonist position, for proximity stress. False if none is spawned.</summary>
        bool TryGetAntagonistPosition(out float3 position);
    }

    public sealed class AIDirector : IAIDirector, ITickable
    {
        public const int MaxPlayers = 8;

        readonly DirectorConfig _config;
        readonly IEventBus _events;
        readonly IRegionGraph _regions;

        readonly PlayerStressSample[] _samples = new PlayerStressSample[MaxPlayers];
        int _sampleCount;

        IDirectorPlayerSource _source;

        DreadPhase _phase = DreadPhase.Fadeout;
        double _phaseEnteredAt;
        double _lastRelaxEndedAt = double.NegativeInfinity;

        float _tableStress;
        float _eventStressAccumulator;
        double _now;

        ulong _preferredTargetId;
        float _preferredTargetScore;

        public DreadPhase Phase => _phase;
        public float TableStress => _tableStress;
        public float TimeInPhase => (float)(_now - _phaseEnteredAt);
        public DirectorIntent CurrentIntent { get; private set; } = DirectorIntent.Default;

        public event Action<DreadPhase, DreadPhase> OnPhaseChanged;

        /// <summary>Live player samples from the most recent tick. Read-only view for the debug overlay.</summary>
        public int SampleCount => _sampleCount;
        public PlayerStressSample GetSample(int i) => _samples[math.clamp(i, 0, MaxPlayers - 1)];

        public AIDirector(DirectorConfig config, IEventBus events, IRegionGraph regions)
        {
            _config = config;
            _events = events;
            _regions = regions;
        }

        public void SetPlayerSource(IDirectorPlayerSource source) => _source = source;

        // ------------------------------------------------------------------- tick

        public void OnSimTick(in SimTime time)
        {
            _now = time.Elapsed;

            if (_source != null)
            {
                _sampleCount = math.min(_source.GetPlayerSamples(_samples), MaxPlayers);
            }

            AggregateStress(time.DeltaTime);
            EvaluatePhase();
            SelectPreferredTarget();
            BuildIntent();
        }

        void AggregateStress(float dt)
        {
            if (_config == null || _sampleCount == 0)
            {
                _tableStress = math.saturate(_eventStressAccumulator);
                DecayEventStress(dt);
                return;
            }

            float3 entity = default;
            bool hasEntity = _source != null && _source.TryGetAntagonistPosition(out entity);

            float total = 0f;
            int alive = 0;

            for (int i = 0; i < _sampleCount; i++)
            {
                ref PlayerStressSample s = ref _samples[i];
                if (!s.IsAlive) continue;

                alive++;

                float proximityStress = 0f;
                if (hasEntity)
                {
                    // Normalised so "touching" is 1 and ~40m away is ~0.
                    float d = math.distance(s.Position, entity);
                    float n = math.saturate(1f - d / 40f);
                    proximityStress = _config.StressByProximity(n);
                }

                float isolationN = math.saturate(s.IsolationDistance / math.max(1f, _config.IsolationDistance));
                float isolationStress = _config.StressByIsolation(isolationN);

                float composureStress = 1f - math.saturate(s.Composure);
                float darknessStress = s.Darkness * _config.DarknessWeight;
                float downedStress = s.IsDowned ? 0.6f : 0f;

                float playerStress = math.saturate(
                    proximityStress * 0.40f +
                    isolationStress * 0.20f +
                    composureStress * 0.25f +
                    darknessStress +
                    downedStress);

                total += playerStress;
            }

            float perPlayer = alive > 0 ? total / alive : 0f;

            // Blend toward the sampled value rather than snapping, so a single tick
            // of proximity does not spike the whole pacing curve.
            float blend = 1f - math.exp(-dt * 0.8f);
            _tableStress = math.saturate(math.lerp(_tableStress, perPlayer, blend) + _eventStressAccumulator);

            DecayEventStress(dt);
        }

        void DecayEventStress(float dt)
        {
            if (_config == null) { _eventStressAccumulator = 0f; return; }

            float halfLife = math.max(0.1f, _config.StressHalfLife);
            _eventStressAccumulator *= math.exp(-dt * 0.6931f / halfLife);
            if (math.abs(_eventStressAccumulator) < 0.001f) _eventStressAccumulator = 0f;
        }

        void EvaluatePhase()
        {
            if (_config == null) return;

            float elapsed = TimeInPhase;
            float min = _config.MinPhaseSeconds(_phase);
            float max = _config.MaxPhaseSeconds(_phase);

            // The hard clamp. Nothing below this line may override it.
            if (elapsed >= max)
            {
                TransitionTo(NextPhaseOnTimeout(_phase), "max duration");
                return;
            }

            if (elapsed < min) return;

            switch (_phase)
            {
                case DreadPhase.Fadeout:
                    // Enforced quiet after a Relax before pressure may rebuild.
                    if (_now - _lastRelaxEndedAt < _config.RelaxCooldown) return;
                    TransitionTo(DreadPhase.Buildup, "cooldown elapsed");
                    break;

                case DreadPhase.Buildup:
                    if (_tableStress >= _config.PeakEntryStress) TransitionTo(DreadPhase.Peak, "stress threshold");
                    break;

                case DreadPhase.Peak:
                    // Peak only ends early if the table has genuinely de-escalated
                    // (everyone escaped and calmed down). Otherwise the max clamp
                    // above is what ends it.
                    if (_tableStress <= _config.RelaxExitStress) TransitionTo(DreadPhase.Relax, "table de-escalated");
                    break;

                case DreadPhase.Relax:
                    if (_tableStress <= _config.RelaxExitStress) TransitionTo(DreadPhase.Fadeout, "recovered");
                    break;
            }
        }

        static DreadPhase NextPhaseOnTimeout(DreadPhase current)
        {
            switch (current)
            {
                case DreadPhase.Buildup: return DreadPhase.Peak;
                case DreadPhase.Peak: return DreadPhase.Relax;
                case DreadPhase.Relax: return DreadPhase.Fadeout;
                default: return DreadPhase.Buildup;
            }
        }

        void TransitionTo(DreadPhase next, string reason)
        {
            if (next == _phase) return;

            DreadPhase from = _phase;
            _phase = next;
            _phaseEnteredAt = _now;

            if (from == DreadPhase.Relax) _lastRelaxEndedAt = _now;

            VLog.Info(LogCat.Director, $"Dread {from} -> {next} ({reason}, stress {_tableStress:F2})");

            OnPhaseChanged?.Invoke(from, next);
            _events?.Publish(new DreadPhaseChangedEvent { From = from, To = next });
        }

        // -------------------------------------------------------- target selection

        void SelectPreferredTarget()
        {
            if (_config == null || _sampleCount == 0)
            {
                _preferredTargetId = 0UL;
                return;
            }

            ulong best = 0UL;
            float bestScore = float.NegativeInfinity;

            for (int i = 0; i < _sampleCount; i++)
            {
                ref PlayerStressSample s = ref _samples[i];
                if (!s.IsAlive || s.IsDowned) continue;

                float isolationN = math.saturate(s.IsolationDistance / math.max(1f, _config.IsolationDistance));

                // Deliberately NOT "pick the weakest". Pressuring an already-broken
                // player produces a rout: they die, the team collapses, the session
                // ends flat. Pressuring the CONFIDENT, ISOLATED player produces the
                // run people talk about afterwards.
                float score = isolationN * _config.IsolationPreference
                              + math.saturate(s.Composure) * _config.ComposurePreference
                              + (1f - math.saturate(s.Composure)) * _config.LowHealthPenalty;

                // Stickiness so preference does not thrash between two similar players.
                if (s.PlayerId == _preferredTargetId) score += _config.TargetStickiness;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = s.PlayerId;
                }
            }

            _preferredTargetId = best;
            _preferredTargetScore = bestScore;
        }

        void BuildIntent()
        {
            DirectorIntent intent = new DirectorIntent
            {
                Phase = _phase,
                Pressure = _config != null ? _config.Pressure(_phase) : 0.5f,

                // Contact is authorised ONLY at Peak. This single flag is what makes
                // Buildup work: the monster knows exactly where you are and is
                // forbidden from acting on it.
                EngagementAuthorised = _phase == DreadPhase.Peak,

                PreferredTargetId = _preferredTargetId,
                DesiredStandoffDistance = _config != null ? _config.StandoffDistance(_phase) : 25f,
                SpeedScale = _config != null ? _config.SpeedScale(_phase) : 1f,
                PreferredRegion = ResolvePreferredRegion()
            };

            CurrentIntent = intent;
        }

        int ResolvePreferredRegion()
        {
            if (_regions == null || _preferredTargetId == 0UL) return 0;

            for (int i = 0; i < _sampleCount; i++)
            {
                if (_samples[i].PlayerId == _preferredTargetId)
                {
                    return _regions.GetRegionAt(_samples[i].Position);
                }
            }

            return 0;
        }

        // ------------------------------------------------------------------ events

        public void Report(DirectorEvent evt, ulong instigatorId, float3 position)
        {
            if (_config == null) return;

            _eventStressAccumulator = math.clamp(_eventStressAccumulator + _config.EventStress(evt), -1f, 1f);

            VLog.Info(LogCat.Director, $"Event {evt} from {instigatorId} (stress accumulator {_eventStressAccumulator:F2})");

            switch (evt)
            {
                case DirectorEvent.PlayerKilled:
                    // A death ends the beat regardless of the curve. Continuing to
                    // press a team that just lost someone reads as piling on, and the
                    // survivors need the space to process it.
                    ForcePhase(DreadPhase.Relax);
                    break;

                case DirectorEvent.PlayerDowned:
                    if (_phase == DreadPhase.Peak && _tableStress > 0.8f) ForcePhase(DreadPhase.Relax);
                    break;

                case DirectorEvent.ObjectiveCompleted:
                    // Reward progress with a beat of pressure — completing a generator
                    // should feel like it cost something.
                    if (_phase == DreadPhase.Fadeout) ForcePhase(DreadPhase.Buildup);
                    break;
            }
        }

        public void ForcePhase(DreadPhase phase) => TransitionTo(phase, "forced");

        /// <summary>Test/tooling hook: injects a sample set without a live player source.</summary>
        public void InjectSamples(PlayerStressSample[] samples, int count)
        {
            _sampleCount = math.min(count, MaxPlayers);
            for (int i = 0; i < _sampleCount; i++) _samples[i] = samples[i];
        }
    }
}
