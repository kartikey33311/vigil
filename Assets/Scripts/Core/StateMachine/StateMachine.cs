// -----------------------------------------------------------------------------
// Vigil — hierarchical finite state machine.
//
// Generic and gameplay-agnostic: the AI antagonist, the door/lock system and the
// session flow all run on this same engine.
//
// Three features here exist specifically because naive FSMs produce AI that
// players describe as "twitchy" or "broken":
//
//   1. MINIMUM DWELL TIME. Without it, an agent sitting exactly on an awareness
//      threshold flips Investigate<->Chase every single tick. The player sees a
//      monster having a seizure. Every transition can require the source state to
//      have been active for N seconds first.
//
//   2. TRANSITION PRIORITY + DETERMINISTIC ORDER. Conditions overlap in practice.
//      Evaluating in registration order makes behaviour depend on the order
//      someone happened to write the setup code in. Explicit priority, with a
//      stable tiebreak, makes it reviewable.
//
//   3. HIERARCHY VIA COMPOSITION. A StateMachine is itself an IState, so
//      "Hunt" can be a state containing its own Chase/Search/Ambush submachine.
//      Entering Hunt enters its child; exiting Hunt exits the whole subtree. That
//      keeps the top-level graph small enough to reason about — the thing that
//      actually breaks down on flat FSMs once you pass ~10 states.
//
// Zero allocation per tick: all delegates and transition lists are built once
// during setup.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;
using Vigil.Core.Diagnostics;
using Vigil.Core.Simulation;

namespace Vigil.Core.StateMachine
{
    /// <summary>Result of ticking a state.</summary>
    public enum StateStatus : byte
    {
        /// <summary>Still working.</summary>
        Running = 0,

        /// <summary>Finished its job — lets a parent transition fire on completion.</summary>
        Succeeded = 1,

        /// <summary>Could not complete (path failed, target gone).</summary>
        Failed = 2
    }

    public interface IState<TContext>
    {
        /// <summary>Stable identifier. Use an enum cast to int.</summary>
        int Id { get; }

        /// <summary>Debug name, shown in the AI inspector and transition log.</summary>
        string Name { get; }

        void OnEnter(TContext context, in SimTime time);
        void OnExit(TContext context, in SimTime time);
        StateStatus OnTick(TContext context, in SimTime time);
    }

    /// <summary>Convenience base with no-op lifecycle hooks.</summary>
    public abstract class StateBase<TContext> : IState<TContext>
    {
        public abstract int Id { get; }
        public virtual string Name => GetType().Name;

        public virtual void OnEnter(TContext context, in SimTime time) { }
        public virtual void OnExit(TContext context, in SimTime time) { }
        public abstract StateStatus OnTick(TContext context, in SimTime time);
    }

    /// <summary>
    /// One edge in the graph. Immutable after construction.
    /// </summary>
    public sealed class Transition<TContext>
    {
        /// <summary>Source state id, or <see cref="StateMachine{TContext}.AnyStateId"/>.</summary>
        public readonly int From;

        public readonly int To;

        /// <summary>Guard. Evaluated every tick the source state is active. Must be side-effect free.</summary>
        public readonly Func<TContext, bool> Condition;

        /// <summary>Higher wins when several guards pass on the same tick.</summary>
        public readonly int Priority;

        /// <summary>Seconds the source state must have been active before this may fire.</summary>
        public readonly float MinDwellSeconds;

        /// <summary>Seconds after firing before this same edge may fire again.</summary>
        public readonly float CooldownSeconds;

        /// <summary>When set, fires only if the source state returned this status.</summary>
        public readonly StateStatus? RequiredStatus;

        /// <summary>Optional label for the transition log.</summary>
        public readonly string Label;

        internal double LastFiredAt = double.NegativeInfinity;

        public Transition(
            int from,
            int to,
            Func<TContext, bool> condition,
            int priority = 0,
            float minDwellSeconds = 0f,
            float cooldownSeconds = 0f,
            StateStatus? requiredStatus = null,
            string label = null)
        {
            From = from;
            To = to;
            Condition = condition;
            Priority = priority;
            MinDwellSeconds = minDwellSeconds;
            CooldownSeconds = cooldownSeconds;
            RequiredStatus = requiredStatus;
            Label = label;
        }
    }

    /// <summary>
    /// Hierarchical state machine. Implements <see cref="IState{TContext}"/> so it
    /// can be nested inside another machine as a composite state.
    /// </summary>
    public sealed class StateMachine<TContext> : IState<TContext>
    {
        /// <summary>Sentinel source id for transitions valid from every state.</summary>
        public const int AnyStateId = int.MinValue;

        readonly Dictionary<int, IState<TContext>> _states = new Dictionary<int, IState<TContext>>(16);

        /// <summary>Outgoing edges per source state, pre-sorted by descending priority.</summary>
        readonly Dictionary<int, List<Transition<TContext>>> _transitions =
            new Dictionary<int, List<Transition<TContext>>>(16);

        readonly List<Transition<TContext>> _anyTransitions = new List<Transition<TContext>>(8);

        readonly int _id;
        readonly string _name;
        bool _sorted;

        IState<TContext> _current;
        StateStatus _lastStatus = StateStatus.Running;

        double _stateEnteredAt;
        int _stateEnteredTick;

        /// <summary>Guards against a pathological condition set ping-ponging within one tick.</summary>
        public int MaxTransitionsPerTick { get; set; } = 4;

        public int Id => _id;
        public string Name => _name;

        public int InitialStateId { get; private set; }
        public int CurrentStateId => _current?.Id ?? -1;
        public IState<TContext> CurrentState => _current;
        public int PreviousStateId { get; private set; } = -1;
        public StateStatus LastStatus => _lastStatus;

        /// <summary>Seconds the current state has been active.</summary>
        public double TimeInState { get; private set; }

        /// <summary>Tick the current state was entered on.</summary>
        public int StateEnteredTick => _stateEnteredTick;

        /// <summary>Total transitions taken. Useful as a "is this agent thrashing?" metric.</summary>
        public int TransitionCount { get; private set; }

        /// <summary>
        /// Raised after every transition: (fromId, toId, label). Wired to the
        /// debug overlay and to the audio system, which cues stingers off state changes.
        /// </summary>
        public event Action<int, int, string> OnTransition;

        public StateMachine(int id = 0, string name = "FSM")
        {
            _id = id;
            _name = name;
        }

        // ---------------------------------------------------------------- setup

        public StateMachine<TContext> AddState(IState<TContext> state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (_states.ContainsKey(state.Id))
            {
                throw new InvalidOperationException(
                    $"{_name}: duplicate state id {state.Id} ({state.Name}).");
            }
            _states[state.Id] = state;
            if (_states.Count == 1) InitialStateId = state.Id;
            return this;
        }

        public StateMachine<TContext> SetInitialState(int stateId)
        {
            if (!_states.ContainsKey(stateId))
            {
                throw new InvalidOperationException($"{_name}: initial state {stateId} not registered.");
            }
            InitialStateId = stateId;
            return this;
        }

        public StateMachine<TContext> AddTransition(Transition<TContext> transition)
        {
            if (transition == null) throw new ArgumentNullException(nameof(transition));

            if (transition.From == AnyStateId)
            {
                _anyTransitions.Add(transition);
            }
            else
            {
                if (!_states.ContainsKey(transition.From))
                {
                    throw new InvalidOperationException(
                        $"{_name}: transition source {transition.From} not registered.");
                }
                if (!_transitions.TryGetValue(transition.From, out List<Transition<TContext>> list))
                {
                    list = new List<Transition<TContext>>(4);
                    _transitions[transition.From] = list;
                }
                list.Add(transition);
            }

            if (!_states.ContainsKey(transition.To))
            {
                throw new InvalidOperationException(
                    $"{_name}: transition target {transition.To} not registered.");
            }

            _sorted = false;
            return this;
        }

        public StateMachine<TContext> AddTransition(
            int from, int to, Func<TContext, bool> condition,
            int priority = 0, float minDwellSeconds = 0f, float cooldownSeconds = 0f,
            StateStatus? requiredStatus = null, string label = null)
            => AddTransition(new Transition<TContext>(
                from, to, condition, priority, minDwellSeconds, cooldownSeconds, requiredStatus, label));

        /// <summary>
        /// Edge valid from any state. Reserve for genuine interrupts — stun, death,
        /// despawn. Overusing these recreates the flat-FSM spaghetti hierarchy is
        /// meant to avoid.
        /// </summary>
        public StateMachine<TContext> AddAnyTransition(
            int to, Func<TContext, bool> condition,
            int priority = 100, float minDwellSeconds = 0f, float cooldownSeconds = 0f, string label = null)
            => AddTransition(new Transition<TContext>(
                AnyStateId, to, condition, priority, minDwellSeconds, cooldownSeconds, null, label));

        void EnsureSorted()
        {
            if (_sorted) return;
            foreach (KeyValuePair<int, List<Transition<TContext>>> kv in _transitions)
            {
                // Descending priority; OrderBy is a stable sort so registration
                // order remains the tiebreak, which keeps behaviour reproducible.
                kv.Value.Sort(CompareByPriorityDescending);
            }
            _anyTransitions.Sort(CompareByPriorityDescending);
            _sorted = true;
        }

        static int CompareByPriorityDescending(Transition<TContext> a, Transition<TContext> b)
            => b.Priority.CompareTo(a.Priority);

        // ------------------------------------------------------------- lifecycle

        public void OnEnter(TContext context, in SimTime time)
        {
            EnsureSorted();
            _lastStatus = StateStatus.Running;
            TransitionCount = 0;
            PreviousStateId = -1;

            if (_states.TryGetValue(InitialStateId, out IState<TContext> initial))
            {
                EnterState(initial, context, in time);
            }
            else
            {
                VLog.Error(LogCat.Fsm, $"{_name}: no initial state registered.");
            }
        }

        public void OnExit(TContext context, in SimTime time)
        {
            if (_current == null) return;
            _current.OnExit(context, in time);
            _current = null;
        }

        public StateStatus OnTick(TContext context, in SimTime time)
        {
            if (_current == null)
            {
                EnsureSorted();
                if (!_states.TryGetValue(InitialStateId, out IState<TContext> initial)) return StateStatus.Failed;
                EnterState(initial, context, in time);
            }

            TimeInState = time.Elapsed - _stateEnteredAt;

            // Tick first, then evaluate transitions, so guards see this tick's results.
            _lastStatus = _current.OnTick(context, in time);

            int guard = 0;
            while (guard++ < MaxTransitionsPerTick)
            {
                Transition<TContext> chosen = SelectTransition(context, in time);
                if (chosen == null) break;

                chosen.LastFiredAt = time.Elapsed;
                int fromId = _current.Id;

                _current.OnExit(context, in time);
                PreviousStateId = fromId;

                if (!_states.TryGetValue(chosen.To, out IState<TContext> next))
                {
                    VLog.Error(LogCat.Fsm, $"{_name}: transition target {chosen.To} missing at runtime.");
                    break;
                }

                EnterState(next, context, in time);
                TransitionCount++;

                if (VLog.Is(LogCat.Fsm))
                {
                    VLog.Info(LogCat.Fsm, $"{_name}: {fromId} -> {chosen.To} ({chosen.Label ?? "cond"})");
                }

                OnTransition?.Invoke(fromId, chosen.To, chosen.Label);

                // Run the new state immediately so it does not idle for a tick.
                _lastStatus = _current.OnTick(context, in time);
            }

            if (guard >= MaxTransitionsPerTick)
            {
                VLog.Warn(LogCat.Fsm,
                    $"{_name}: hit MaxTransitionsPerTick ({MaxTransitionsPerTick}) — check for a transition cycle.");
            }

            return _lastStatus;
        }

        Transition<TContext> SelectTransition(TContext context, in SimTime time)
        {
            // AnyState edges outrank local ones at equal priority: interrupts win.
            Transition<TContext> best = Evaluate(_anyTransitions, context, in time, null);

            if (_transitions.TryGetValue(_current.Id, out List<Transition<TContext>> local))
            {
                Transition<TContext> localBest = Evaluate(local, context, in time, best);
                if (localBest != null) best = localBest;
            }

            return best;
        }

        Transition<TContext> Evaluate(
            List<Transition<TContext>> list, TContext context, in SimTime time, Transition<TContext> incumbent)
        {
            int incumbentPriority = incumbent?.Priority ?? int.MinValue;
            Transition<TContext> best = null;

            for (int i = 0; i < list.Count; i++)
            {
                Transition<TContext> t = list[i];

                // List is priority-sorted; once we drop to or below the incumbent
                // nothing later can beat it.
                if (t.Priority <= incumbentPriority) break;

                if (t.To == _current.Id) continue;
                if (t.MinDwellSeconds > 0f && TimeInState < t.MinDwellSeconds) continue;
                if (t.CooldownSeconds > 0f && (time.Elapsed - t.LastFiredAt) < t.CooldownSeconds) continue;
                if (t.RequiredStatus.HasValue && t.RequiredStatus.Value != _lastStatus) continue;

                bool passed;
                try
                {
                    passed = t.Condition == null || t.Condition(context);
                }
                catch (Exception ex)
                {
                    VLog.Exception(LogCat.Fsm, ex);
                    continue;
                }

                if (!passed) continue;

                best = t;
                break; // sorted, so the first pass is the highest-priority pass
            }

            return best;
        }

        void EnterState(IState<TContext> state, TContext context, in SimTime time)
        {
            _current = state;
            _stateEnteredAt = time.Elapsed;
            _stateEnteredTick = time.Tick;
            TimeInState = 0d;
            _lastStatus = StateStatus.Running;
            state.OnEnter(context, in time);
        }

        // ------------------------------------------------------------------ misc

        /// <summary>Forces a state change, bypassing all guards. For scripted beats and tests.</summary>
        public void ForceState(int stateId, TContext context, in SimTime time)
        {
            if (!_states.TryGetValue(stateId, out IState<TContext> next))
            {
                VLog.Error(LogCat.Fsm, $"{_name}: ForceState to unregistered id {stateId}.");
                return;
            }

            int fromId = -1;
            if (_current != null)
            {
                fromId = _current.Id;
                _current.OnExit(context, in time);
                PreviousStateId = fromId;
            }

            EnterState(next, context, in time);
            TransitionCount++;
            OnTransition?.Invoke(fromId, stateId, "forced");
        }

        public bool TryGetState(int stateId, out IState<TContext> state) => _states.TryGetValue(stateId, out state);

        public IEnumerable<IState<TContext>> States => _states.Values;

        /// <summary>
        /// Full active path including nested machines, e.g. "Root/Hunt/Search".
        /// Allocates — debug and tooling only, never call from a tick.
        /// </summary>
        public string GetActivePath()
        {
            StringBuilder sb = new StringBuilder(64);
            AppendPath(sb);
            return sb.ToString();
        }

        void AppendPath(StringBuilder sb)
        {
            sb.Append(_name);
            if (_current == null) { sb.Append("/<none>"); return; }
            sb.Append('/');
            if (_current is StateMachine<TContext> nested) nested.AppendPath(sb);
            else sb.Append(_current.Name);
        }
    }
}
