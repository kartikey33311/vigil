using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Simulation;

namespace Vigil.Data
{
    [CreateAssetMenu(menuName = "Vigil/AI/Agent Archetype", fileName = "AgentArchetype")]
    public sealed class AgentArchetypeConfig : ScriptableObject, IValidatableConfig
    {
        [Header("Identity")]
        [SerializeField] string _displayName = "The Occupant";
        [SerializeField] Faction _faction = Faction.Entity;

        [Header("Referenced tuning")]
        [SerializeField] PerceptionConfig _perception = null;
        [SerializeField] NavigationConfig _navigation = null;

        [Header("Locomotion â€” metres/second per intent band")]
        [SerializeField, Tooltip("Indexed by AgentMoveState: Patrol, Investigate, Search, Stalk, Chase. Stalk is deliberately slow â€” stalking is about being heard, not about closing.")]
        float[] _moveSpeed = new float[ConfigCounts.AgentMoveStates] { 1.5f, 2.1f, 2.4f, 1.7f, 4.6f };

        [SerializeField, Min(0.1f)] float _acceleration = 9f;
        [SerializeField, Min(1f), Tooltip("Degrees per second. Low values make the monster commit to turns, which players can exploit â€” that is intended.")]
        float _turnRateDegrees = 240f;

        [Header("Combat")]
        [SerializeField, Min(0.1f)] float _attackRange = 2.1f;
        [SerializeField, Min(0f)] float _attackDamage = 55f;
        [SerializeField, Min(0f), Tooltip("Seconds of windup before the strike lands. This is the player's reaction window; below ~0.35s the attack is undodgeable.")]
        float _attackWindup = 0.45f;

        [SerializeField, Min(0f), Tooltip("Seconds the strike is committed. The agent CANNOT turn during this, which is what makes dodging a real skill.")]
        float _attackCommit = 0.30f;

        [SerializeField, Min(0f)] float _attackRecover = 0.6f;
        [SerializeField, Min(0f)] float _attackCooldown = 2.2f;

        [Header("Grapple")]
        [SerializeField, Min(0f)] float _grappleDuration = 3.5f;
        [SerializeField, Min(0f), Tooltip("Damage per second while grappling. Teammates can interrupt.")]
        float _grappleDps = 22f;

        [Header("Resilience")]
        [SerializeField, Min(1f)] float _maxHealth = 1000f;
        [SerializeField, Min(0.1f), Tooltip("Seconds stunned by a flare or equivalent. The only real counterplay the players have.")]
        float _stunDuration = 4.5f;

        [Header("Behaviour tuning")]
        [SerializeField, Min(1), Tooltip("Regions swept before SearchState gives up and degrades to Patrol. The monster deciding to stop looking is as important a beat as any chase.")]
        int _maxSearchRegions = 5;

        [SerializeField, Min(0f), Tooltip("Seconds spent looking around at an investigation point before abandoning it.")]
        float _investigateDwell = 3.2f;

        [SerializeField, Min(0f), Tooltip("Metres from territory centre before the leash pulls the agent home.")]
        float _leashRadius = 220f;

        [SerializeField, Tooltip("Coarsest tick bucket this archetype may be demoted to. Cap at Normal for a boss that must never look asleep.")]
        TickBudget _minimumBudget = TickBudget.Dormant;

        public string DisplayName => _displayName;
        public Faction Faction => _faction;
        public PerceptionConfig Perception => _perception;
        public NavigationConfig Navigation => _navigation;
        public float Acceleration => _acceleration;
        public float TurnRateDegrees => _turnRateDegrees;
        public float AttackRange => _attackRange;
        public float AttackDamage => _attackDamage;
        public float AttackWindup => _attackWindup;
        public float AttackCommit => _attackCommit;
        public float AttackRecover => _attackRecover;
        public float AttackCooldown => _attackCooldown;
        public float GrappleDuration => _grappleDuration;
        public float GrappleDps => _grappleDps;
        public float MaxHealth => _maxHealth;
        public float StunDuration => _stunDuration;
        public int MaxSearchRegions => _maxSearchRegions;
        public float InvestigateDwell => _investigateDwell;
        public float LeashRadius => _leashRadius;
        public TickBudget MinimumBudget => _minimumBudget;

        public float MoveSpeed(AgentMoveState state)
        {
            int i = (int)state;
            return (_moveSpeed != null && i >= 0 && i < _moveSpeed.Length) ? _moveSpeed[i] : 2f;
        }

        /// <summary>Fastest authored speed â€” used to size lookahead and prediction windows.</summary>
        public float MaxMoveSpeed
        {
            get
            {
                float max = 0f;
                if (_moveSpeed == null) return 4f;
                for (int i = 0; i < _moveSpeed.Length; i++) max = math.max(max, _moveSpeed[i]);
                return max <= 0f ? 4f : max;
            }
        }

        public void Validate(IList<string> problems)
        {
            if (_perception == null) problems.Add($"{name}: PerceptionConfig is not assigned.");
            if (_navigation == null) problems.Add($"{name}: NavigationConfig is not assigned.");

            if (_moveSpeed == null || _moveSpeed.Length != ConfigCounts.AgentMoveStates)
            {
                problems.Add($"{name}: moveSpeed must have exactly {ConfigCounts.AgentMoveStates} entries.");
            }
            else if (_moveSpeed[(int)AgentMoveState.Stalk] > _moveSpeed[(int)AgentMoveState.Chase])
            {
                problems.Add($"{name}: stalk speed exceeds chase speed â€” stalking should be slower than pursuing.");
            }

            if (_attackWindup < 0.35f)
            {
                problems.Add($"{name}: attackWindup {_attackWindup:F2}s leaves no reaction window; the attack is effectively undodgeable.");
            }
            if (_attackCommit <= 0f)
            {
                problems.Add($"{name}: attackCommit is 0 â€” the agent can track through its own strike, which removes all counterplay.");
            }
        }
    }
}
