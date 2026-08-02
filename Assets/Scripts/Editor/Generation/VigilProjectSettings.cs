// -----------------------------------------------------------------------------
// Vigil — project settings configuration.
//
// Layers MUST be created before anything references them. LayerMask.NameToLayer
// returns -1 for a layer that does not exist, and -1 silently becomes a mask that
// matches nothing (or, worse, gets shifted into a mask that matches something
// unintended). The failure is a monster that cannot see through any wall, or one
// that sees through all of them, with no error anywhere. Creating them through the
// TagManager SerializedObject — and validating afterwards — removes that class of
// bug entirely.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Vigil.Core.Diagnostics;

namespace Vigil.Editor.Generation
{
    public static class VigilProjectSettings
    {
        /// <summary>
        /// Layers this project requires. Index 8 is the first user-assignable layer;
        /// 0-7 are reserved by Unity.
        /// </summary>
        public static readonly string[] RequiredLayers =
        {
            "Player",
            "NPC",
            "Occluder",
            "Interactable",
            "Ground",
            "Prop",
            "HidingVolume"
        };

        public static readonly string[] RequiredTags =
        {
            "PlayerSpawn",
            "Objective",
            "RegionVolume"
        };

        [MenuItem("Vigil/Configure Project Settings", priority = 10)]
        public static void ConfigureAll()
        {
            ConfigureSerialization();
            CreateLayers();
            CreateTags();
            ConfigurePhysics();
            ConfigureTime();

            AssetDatabase.SaveAssets();
            VLog.Info(LogCat.Core, "Project settings configured.");
        }

        /// <summary>
        /// Forces text serialization for scenes and prefabs.
        ///
        /// <para>Binary scenes are completely unmergeable: two people touching the
        /// same level produces a conflict that git cannot resolve and a human cannot
        /// read. On a team this converts every level edit into a lock-the-file
        /// negotiation. Text serialization also makes the generated content
        /// reviewable in a pull request, which is the whole point of generating it.</para>
        /// </summary>
        static void ConfigureSerialization()
        {
            if (EditorSettings.serializationMode == SerializationMode.ForceText) return;

            EditorSettings.serializationMode = SerializationMode.ForceText;
            VLog.Info(LogCat.Core, "Asset serialization set to ForceText (scenes and prefabs are now diffable).");
        }

        static SerializedObject GetTagManager()
        {
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            return (assets != null && assets.Length > 0) ? new SerializedObject(assets[0]) : null;
        }

        public static void CreateLayers()
        {
            SerializedObject tagManager = GetTagManager();
            if (tagManager == null)
            {
                VLog.Error(LogCat.Core, "Could not open TagManager.asset — layers not created.");
                return;
            }

            SerializedProperty layers = tagManager.FindProperty("layers");
            if (layers == null) return;

            foreach (string layerName in RequiredLayers)
            {
                if (LayerExists(layers, layerName)) continue;

                bool placed = false;

                // 8..31 are user layers. Take the first empty slot.
                for (int i = 8; i < layers.arraySize; i++)
                {
                    SerializedProperty slot = layers.GetArrayElementAtIndex(i);
                    if (!string.IsNullOrEmpty(slot.stringValue)) continue;

                    slot.stringValue = layerName;
                    placed = true;
                    break;
                }

                if (!placed)
                {
                    VLog.Error(LogCat.Core, $"No free layer slot for '{layerName}' — all 24 user layers are taken.");
                }
            }

            tagManager.ApplyModifiedPropertiesWithoutUndo();
        }

        static bool LayerExists(SerializedProperty layers, string name)
        {
            for (int i = 0; i < layers.arraySize; i++)
            {
                if (layers.GetArrayElementAtIndex(i).stringValue == name) return true;
            }
            return false;
        }

        public static void CreateTags()
        {
            SerializedObject tagManager = GetTagManager();
            if (tagManager == null) return;

            SerializedProperty tags = tagManager.FindProperty("tags");
            if (tags == null) return;

            foreach (string tag in RequiredTags)
            {
                bool exists = false;
                for (int i = 0; i < tags.arraySize; i++)
                {
                    if (tags.GetArrayElementAtIndex(i).stringValue == tag) { exists = true; break; }
                }

                if (exists) continue;

                tags.InsertArrayElementAtIndex(tags.arraySize);
                tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
            }

            tagManager.ApplyModifiedPropertiesWithoutUndo();
        }

        static void ConfigurePhysics()
        {
            // 60Hz physics against a 30Hz simulation: character-controller fidelity
            // where it is visible, AI cognition where it is not.
            Time.fixedDeltaTime = 1f / 60f;

            int player = LayerMask.NameToLayer("Player");
            int npc = LayerMask.NameToLayer("NPC");
            int hiding = LayerMask.NameToLayer("HidingVolume");

            if (player >= 0 && npc >= 0)
            {
                // Players and NPCs collide — the monster must be a physical obstacle.
                Physics.IgnoreLayerCollision(player, npc, false);
            }

            if (player >= 0 && hiding >= 0)
            {
                // Hiding volumes are triggers; physical collision would block entry.
                Physics.IgnoreLayerCollision(player, hiding, true);
            }
        }

        static void ConfigureTime()
        {
            Time.fixedDeltaTime = 1f / 60f;
            Time.maximumDeltaTime = 0.1f;
        }

        /// <summary>
        /// Builds a LayerMask from names, skipping (and reporting) any that do not
        /// exist. Returning a mask that silently omits a missing layer is far safer
        /// than the -1 that NameToLayer produces.
        /// </summary>
        public static int MaskFor(params string[] layerNames)
        {
            int mask = 0;
            List<string> missing = null;

            for (int i = 0; i < layerNames.Length; i++)
            {
                int layer = LayerMask.NameToLayer(layerNames[i]);

                if (layer < 0)
                {
                    (missing ?? (missing = new List<string>())).Add(layerNames[i]);
                    continue;
                }

                mask |= 1 << layer;
            }

            if (missing != null)
            {
                VLog.Warn(LogCat.Core,
                    $"MaskFor: missing layer(s) [{string.Join(", ", missing)}]. " +
                    "Run 'Vigil > Configure Project Settings' first.");
            }

            return mask;
        }
    }
}
