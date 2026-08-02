using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;

namespace Vigil.Data
{
    [CreateAssetMenu(menuName = "Vigil/Gameplay/Gameplay Config", fileName = "GameplayConfig")]
    public sealed class GameplayConfig : ScriptableObject, IValidatableConfig
    {
        [Header("Interaction")]
        [SerializeField, Min(0.5f)] float _interactionRange = 2.6f;
        [SerializeField, Min(0.05f)] float _defaultHoldDuration = 1.1f;
        [SerializeField, Tooltip("Layers raycast for interactables.")]
        LayerMask _interactableMask = ~0;

        [Header("Doors")]
        [SerializeField, Min(0.05f)] float _doorSwingSeconds = 0.55f;
        [SerializeField, Min(0f)] float _doorBarricadeSeconds = 3.5f;
        [SerializeField, Min(1f), Tooltip("Damage a barricaded door absorbs before the entity breaks through.")]
        float _doorHealth = 160f;

        [SerializeField, Min(0f)] float _doorNoiseRadius = 15f;

        [Header("Objectives")]
        [SerializeField, Min(1)] int _generatorsRequired = 3;
        [SerializeField, Min(1f)] float _generatorRepairSeconds = 26f;
        [SerializeField, Min(1), Tooltip("Discrete repair stages. Each completed stage persists, so a player driven off a generator does not lose all progress â€” losing everything to one scare is punishing without being interesting.")]
        int _generatorStages = 4;

        [SerializeField, Min(0f)] float _generatorNoiseRadius = 34f;

        [Header("Downed / revive")]
        [SerializeField, Min(1f)] float _bleedOutSeconds = 55f;
        [SerializeField, Min(0.1f)] float _reviveSeconds = 5.5f;
        [SerializeField, Min(0f)] float _maxHealth = 100f;

        [Header("Extraction")]
        [SerializeField, Min(1f), Tooltip("Seconds the extraction window stays open. Director pressure is unclamped during this â€” it is the only time the game is allowed to be unfair.")]
        float _extractionWindowSeconds = 70f;

        [Header("Inventory")]
        [SerializeField, Range(1, 8)] int _inventorySlots = 4;

        public float InteractionRange => _interactionRange;
        public float DefaultHoldDuration => _defaultHoldDuration;
        public LayerMask InteractableMask => _interactableMask;
        public float DoorSwingSeconds => _doorSwingSeconds;
        public float DoorBarricadeSeconds => _doorBarricadeSeconds;
        public float DoorHealth => _doorHealth;
        public float DoorNoiseRadius => _doorNoiseRadius;
        public int GeneratorsRequired => _generatorsRequired;
        public float GeneratorRepairSeconds => _generatorRepairSeconds;
        public int GeneratorStages => _generatorStages;
        public float GeneratorNoiseRadius => _generatorNoiseRadius;
        public float BleedOutSeconds => _bleedOutSeconds;
        public float ReviveSeconds => _reviveSeconds;
        public float MaxHealth => _maxHealth;
        public float ExtractionWindowSeconds => _extractionWindowSeconds;
        public int InventorySlots => _inventorySlots;

        public void Validate(IList<string> problems)
        {
            if (_interactableMask == 0) problems.Add($"{name}: interactableMask is empty â€” nothing will ever be interactable.");
            if (_reviveSeconds >= _bleedOutSeconds) problems.Add($"{name}: reviveSeconds >= bleedOutSeconds; revives can never complete.");
        }
    }
}
