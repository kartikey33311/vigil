using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;

namespace Vigil.Data
{
    /// <summary>
    /// Single aggregation point for the tuning set. The bootstrap resolves this one
    /// asset instead of nine, and sweeps every config once at startup.
    ///
    /// <para>Validation happens at BOOT, loudly, rather than at tick time, quietly.
    /// A curve left null or a radius authored inside-out produces AI that is subtly
    /// wrong for an entire playtest and is nearly impossible to diagnose from the
    /// symptom alone.</para>
    /// </summary>
    [CreateAssetMenu(menuName = "Vigil/Config Registry", fileName = "VigilConfigRegistry")]
    public sealed class VigilConfigRegistry : ScriptableObject
    {
        [SerializeField] PerceptionConfig _perception = null;
        [SerializeField] NavigationConfig _navigation = null;
        [SerializeField] AgentArchetypeConfig _antagonist = null;
        [SerializeField] AgentArchetypeConfig[] _additionalArchetypes = null;
        [SerializeField] DirectorConfig _director = null;
        [SerializeField] MovementConfig _movement = null;
        [SerializeField] ComposureConfig _composure = null;
        [SerializeField] GameplayConfig _gameplay = null;
        [SerializeField] NetworkTuningConfig _network = null;
        [SerializeField] AudioConfig _audio = null;

        public PerceptionConfig Perception => _perception;
        public NavigationConfig Navigation => _navigation;
        public AgentArchetypeConfig Antagonist => _antagonist;
        public AgentArchetypeConfig[] AdditionalArchetypes => _additionalArchetypes;
        public DirectorConfig Director => _director;
        public MovementConfig Movement => _movement;
        public ComposureConfig Composure => _composure;
        public GameplayConfig Gameplay => _gameplay;
        public NetworkTuningConfig Network => _network;
        public AudioConfig Audio => _audio;

        /// <summary>
        /// Sweeps every referenced config. Returns true when the set is usable.
        /// Allocates â€” called once at boot and from the editor, never from a tick.
        /// </summary>
        public bool Validate(List<string> problems)
        {
            if (problems == null) problems = new List<string>();
            int before = problems.Count;

            Check(_perception, nameof(_perception), problems);
            Check(_navigation, nameof(_navigation), problems);
            Check(_antagonist, nameof(_antagonist), problems);
            Check(_director, nameof(_director), problems);
            Check(_movement, nameof(_movement), problems);
            Check(_composure, nameof(_composure), problems);
            Check(_gameplay, nameof(_gameplay), problems);
            Check(_network, nameof(_network), problems);
            Check(_audio, nameof(_audio), problems);

            if (_additionalArchetypes != null)
            {
                for (int i = 0; i < _additionalArchetypes.Length; i++)
                {
                    Check(_additionalArchetypes[i], $"additionalArchetypes[{i}]", problems);
                }
            }

            // Cross-config invariants that no single asset can see on its own.
            if (_antagonist != null && _perception != null && _antagonist.Perception == null)
            {
                problems.Add("Antagonist archetype has no PerceptionConfig; it will be unable to sense anything.");
            }

            bool ok = problems.Count == before;
            if (!ok)
            {
                for (int i = before; i < problems.Count; i++)
                {
                    VLog.Error(LogCat.Core, "Config validation: " + problems[i], this);
                }
            }
            else
            {
                VLog.Info(LogCat.Core, "Config validation passed.", this);
            }

            return ok;
        }

        static void Check(ScriptableObject asset, string field, List<string> problems)
        {
            if (asset == null)
            {
                problems.Add($"VigilConfigRegistry.{field} is not assigned.");
                return;
            }

            if (asset is IValidatableConfig validatable) validatable.Validate(problems);
        }
    }
}
