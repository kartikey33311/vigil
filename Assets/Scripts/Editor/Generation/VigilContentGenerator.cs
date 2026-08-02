// -----------------------------------------------------------------------------
// Vigil â€” procedural project generation.
//
// This project contains NO hand-authored .unity, .prefab or .asset files, and
// that is a deliberate engineering decision rather than a shortcut.
//
// Hand-written Unity YAML carries GUIDs that will not match the ones Unity assigns
// on import. The result is references that resolve to null with no error at import
// time â€” the failure surfaces later, at runtime, far from its cause, and looks
// like a code bug. Generating everything through AssetDatabase means the GUIDs are
// always Unity's own, the repository stays small and diffable, and "regenerate"
// is always a valid recovery action.
//
// Every step is a separate static method so a failure part-way through names the
// step that failed instead of dumping one opaque stack trace.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using Unity.AI.Navigation;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Vigil.AI.Agents;
using Vigil.Bootstrap;
using Vigil.Core.Diagnostics;
using Vigil.Data;
using Vigil.Gameplay.Interaction;
using Vigil.Gameplay.Player;
using Vigil.Gameplay.Systems;
using Vigil.UI;

namespace Vigil.Editor.Generation
{
    public static class VigilContentGenerator
    {
        const string SettingsDir = "Assets/Settings";
        const string ResourcesDir = "Assets/Resources";
        const string MaterialsDir = "Assets/Art/Materials";
        const string PrefabsDir = "Assets/Prefabs";
        const string ScenesDir = "Assets/Scenes";

        const string BootstrapScenePath = ScenesDir + "/Bootstrap.unity";
        const string LevelScenePath = ScenesDir + "/Level_Facility.unity";

        [MenuItem("Vigil/Generate Playable Sample", priority = 0)]
        public static void GenerateAll()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Vigil", "Configuring project settings...", 0.05f);
                VigilProjectSettings.ConfigureAll();

                EditorUtility.DisplayProgressBar("Vigil", "Creating tuning assets...", 0.2f);
                VigilConfigRegistry registry = CreateConfigs();

                EditorUtility.DisplayProgressBar("Vigil", "Creating materials...", 0.35f);
                Dictionary<string, Material> materials = CreateMaterials();

                EditorUtility.DisplayProgressBar("Vigil", "Creating prefabs...", 0.5f);
                GameObject playerPrefab = CreatePlayerPrefab(registry, materials);
                GameObject npcPrefab = CreateNpcPrefab(registry, materials);

                EditorUtility.DisplayProgressBar("Vigil", "Building facility level...", 0.7f);
                BuildLevelScene(registry, materials, playerPrefab, npcPrefab);

                EditorUtility.DisplayProgressBar("Vigil", "Building bootstrap scene...", 0.9f);
                BuildBootstrapScene(registry, playerPrefab, npcPrefab);

                RegisterScenesInBuildSettings();

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                VLog.Info(LogCat.Core, "Vigil sample generated. Open Assets/Scenes/Bootstrap.unity and press Play.");

                // A modal dialog in batch mode would hang CI forever waiting for a
                // click nobody can make.
                if (!Application.isBatchMode)
                {
                    EditorUtility.DisplayDialog("Vigil",
                        "Playable sample generated.\n\nOpen Assets/Scenes/Bootstrap.unity and press Play.",
                        "OK");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // =====================================================================
        // Configs
        // =====================================================================

        [MenuItem("Vigil/Regenerate Config Assets", priority = 20)]
        public static VigilConfigRegistry CreateConfigs()
        {
            EnsureFolder(SettingsDir);
            EnsureFolder(ResourcesDir);

            // Every config's C# field initialisers already hold shipping-quality
            // defaults, so CreateInstance alone produces a usable asset. Only the
            // cross-references need wiring.
            PerceptionConfig perception = CreateOrLoad<PerceptionConfig>(SettingsDir + "/PerceptionConfig.asset");
            NavigationConfig navigation = CreateOrLoad<NavigationConfig>(SettingsDir + "/NavigationConfig.asset");
            DirectorConfig director = CreateOrLoad<DirectorConfig>(SettingsDir + "/DirectorConfig.asset");
            MovementConfig movement = CreateOrLoad<MovementConfig>(SettingsDir + "/MovementConfig.asset");
            ComposureConfig composure = CreateOrLoad<ComposureConfig>(SettingsDir + "/ComposureConfig.asset");
            GameplayConfig gameplay = CreateOrLoad<GameplayConfig>(SettingsDir + "/GameplayConfig.asset");
            NetworkTuningConfig network = CreateOrLoad<NetworkTuningConfig>(SettingsDir + "/NetworkTuningConfig.asset");
            AudioConfig audio = CreateOrLoad<AudioConfig>(SettingsDir + "/AudioConfig.asset");
            AgentArchetypeConfig antagonist = CreateOrLoad<AgentArchetypeConfig>(SettingsDir + "/Archetype_Occupant.asset");

            // Layer masks cannot be sensible defaults in a field initialiser, because
            // the layers do not exist until VigilProjectSettings has run.
            SetMask(perception, "_occlusionMask", VigilProjectSettings.MaskFor("Default", "Ground", "Occluder", "Prop"));
            SetMask(navigation, "_concealmentMask", VigilProjectSettings.MaskFor("Default", "Ground", "Occluder", "Prop"));
            SetMask(movement, "_collisionMask", VigilProjectSettings.MaskFor("Default", "Ground", "Occluder", "Prop"));
            SetMask(gameplay, "_interactableMask", VigilProjectSettings.MaskFor("Interactable"));

            SetObjectRef(antagonist, "_perception", perception);
            SetObjectRef(antagonist, "_navigation", navigation);

            // The registry lives in Resources so GameBootstrap can find it without a
            // scene reference â€” which is what lets Play-from-any-scene work.
            VigilConfigRegistry registry = CreateOrLoad<VigilConfigRegistry>(ResourcesDir + "/VigilConfigRegistry.asset");

            // Written through ONE SerializedObject rather than nine. Constructing a
            // fresh SerializedObject per field and applying each in turn is both
            // wasteful and unreliable â€” each instance snapshots the target on
            // construction, so interleaved applies can clobber each other's writes
            // and silently leave fields null.
            SerializedObject registrySo = new SerializedObject(registry);

            AssignRef(registrySo, "_perception", perception);
            AssignRef(registrySo, "_navigation", navigation);
            AssignRef(registrySo, "_antagonist", antagonist);
            AssignRef(registrySo, "_director", director);
            AssignRef(registrySo, "_movement", movement);
            AssignRef(registrySo, "_composure", composure);
            AssignRef(registrySo, "_gameplay", gameplay);
            AssignRef(registrySo, "_network", network);
            AssignRef(registrySo, "_audio", audio);

            registrySo.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssets();

            List<string> problems = new List<string>();
            registry.Validate(problems);

            return registry;
        }

        static T CreateOrLoad<T>(string path) where T : ScriptableObject
        {
            T existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null) return existing;

            T created = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(created, path);
            return created;
        }

        /// <summary>Assigns one reference on an already-open SerializedObject.</summary>
        static void AssignRef(SerializedObject so, string propertyName, Object value)
        {
            SerializedProperty prop = so.FindProperty(propertyName);

            if (prop == null)
            {
                VLog.Warn(LogCat.Core, $"{so.targetObject.name}: serialized property '{propertyName}' not found.");
                return;
            }

            prop.objectReferenceValue = value;
        }

        static void SetObjectRef(Object target, string propertyName, Object value)
        {
            SerializedObject so = new SerializedObject(target);
            SerializedProperty prop = so.FindProperty(propertyName);

            if (prop == null)
            {
                VLog.Warn(LogCat.Core, $"{target.name}: serialized property '{propertyName}' not found.");
                return;
            }

            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetMask(Object target, string propertyName, int mask)
        {
            SerializedObject so = new SerializedObject(target);
            SerializedProperty prop = so.FindProperty(propertyName);
            if (prop == null) return;

            prop.intValue = mask;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // =====================================================================
        // Materials
        // =====================================================================

        static Dictionary<string, Material> CreateMaterials()
        {
            EnsureFolder("Assets/Art");
            EnsureFolder(MaterialsDir);

            Dictionary<string, Material> result = new Dictionary<string, Material>();

            // URP is assigned by VigilProjectSettings before this runs, so URP/Lit is
            // the shader that will actually ship. The Standard fallback exists only
            // so a misconfigured project renders SOMETHING rather than magenta.
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

            if (shader == null)
            {
                VLog.Error(LogCat.Core, "No usable lit shader found â€” the level will render magenta.");
            }

            // Smoothness carries most of the mood here. Wet-looking concrete and
            // scuffed metal catch the flashlight and give the beam something to do;
            // fully matte surfaces make a torch-lit corridor read as flat grey paper.
            result["Floor"] = CreateMaterial(shader, "M_Floor", new Color(0.14f, 0.145f, 0.16f), smoothness: 0.34f, metallic: 0.0f);
            result["Wall"] = CreateMaterial(shader, "M_Wall", new Color(0.19f, 0.185f, 0.175f), smoothness: 0.18f, metallic: 0.0f);
            result["Prop"] = CreateMaterial(shader, "M_Prop", new Color(0.31f, 0.25f, 0.16f), smoothness: 0.25f, metallic: 0.15f);
            result["Metal"] = CreateMaterial(shader, "M_Metal", new Color(0.26f, 0.27f, 0.29f), smoothness: 0.62f, metallic: 0.85f);
            result["Player"] = CreateMaterial(shader, "M_Player", new Color(0.22f, 0.48f, 0.66f), smoothness: 0.30f, metallic: 0.0f);

            // The entity is deliberately dark and matte: it should read as an absence
            // rather than an object, and a shiny monster catches light that gives its
            // position away before the player has earned it.
            result["Entity"] = CreateMaterial(shader, "M_Entity", new Color(0.10f, 0.035f, 0.04f), smoothness: 0.08f, metallic: 0.0f);

            // Cold green, and deliberately NOT bright enough to clip. At 2.2 it blew
            // out to flat white, which loses the panel's shape entirely — and a
            // featureless white rectangle reads as a rendering bug, not an exit sign.
            // Cold also separates it from the warm generator lights at a glance.
            result["Emissive"] = CreateMaterial(shader, "M_Emissive", new Color(0.30f, 0.62f, 0.45f),
                smoothness: 0.55f, metallic: 0.0f, emission: new Color(0.25f, 0.85f, 0.55f) * 0.85f);

            return result;
        }

        static Material CreateMaterial(
            Shader shader, string name, Color color,
            float smoothness = 0.3f, float metallic = 0f, Color? emission = null)
        {
            string path = $"{MaterialsDir}/{name}.mat";

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool isNew = mat == null;

            if (isNew) mat = new Material(shader);
            else if (shader != null && mat.shader != shader) mat.shader = shader;

            // URP uses _BaseColor; the built-in Standard shader uses _Color. Setting
            // whichever exists keeps the fallback path looking right too.
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);

            if (emission.HasValue)
            {
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", emission.Value);
            }

            if (isNew) AssetDatabase.CreateAsset(mat, path);
            else EditorUtility.SetDirty(mat);

            return mat;
        }

        // =====================================================================
        // Prefabs
        // =====================================================================

        static GameObject CreatePlayerPrefab(VigilConfigRegistry registry, Dictionary<string, Material> materials)
        {
            EnsureFolder(PrefabsDir);
            string path = PrefabsDir + "/Player.prefab";

            GameObject root = new GameObject("Player");
            root.layer = LayerMask.NameToLayer("Player");

            CharacterController controller = root.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.35f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            controller.slopeLimit = 50f;
            controller.stepOffset = 0.35f;

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            body.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.GetComponent<Renderer>().sharedMaterial = materials["Player"];
            body.layer = root.layer;

            GameObject head = new GameObject("Head");
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = new Vector3(0f, 1.65f, 0f);

            // The camera lives in the prefab rather than being created at runtime so
            // the owner sees through it on the very first frame â€” spawning it later
            // produces a visible black frame on join.
            GameObject camGo = new GameObject("PlayerCamera");
            camGo.transform.SetParent(head.transform, false);
            Camera cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 68f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300f;
            camGo.AddComponent<AudioListener>();

            GameObject lightGo = new GameObject("Flashlight");
            lightGo.transform.SetParent(camGo.transform, false);
            Light flashlight = lightGo.AddComponent<Light>();
            flashlight.type = LightType.Spot;
            flashlight.range = 26f;
            flashlight.spotAngle = 48f;
            flashlight.intensity = 3.2f;
            flashlight.color = new Color(1f, 0.96f, 0.85f);
            flashlight.enabled = false;

            root.AddComponent<NetworkObject>();

            PlayerCharacter player = root.AddComponent<PlayerCharacter>();
            SetObjectRef(player, "_movement", registry.Movement);
            SetObjectRef(player, "_composureConfig", registry.Composure);
            SetObjectRef(player, "_gameplay", registry.Gameplay);
            SetObjectRef(player, "_perception", registry.Perception);
            SetObjectRef(player, "_head", head.transform);
            SetObjectRef(player, "_flashlight", flashlight);

            root.AddComponent<PlayerCameraRig>();

            InteractionSystem interaction = root.AddComponent<InteractionSystem>();
            SetObjectRef(interaction, "_gameplay", registry.Gameplay);
            SetMask(interaction, "_interactableMask", VigilProjectSettings.MaskFor("Interactable"));

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);

            RefreshNetworkIds(prefab);
            return prefab;
        }

        static GameObject CreateNpcPrefab(VigilConfigRegistry registry, Dictionary<string, Material> materials)
        {
            string path = PrefabsDir + "/Antagonist.prefab";

            GameObject root = new GameObject("Antagonist");
            root.layer = LayerMask.NameToLayer("NPC");

            CapsuleCollider collider = root.AddComponent<CapsuleCollider>();
            collider.height = 2.1f;
            collider.radius = 0.45f;
            collider.center = new Vector3(0f, 1.05f, 0f);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 1.05f, 0f);
            body.transform.localScale = new Vector3(0.9f, 1.05f, 0.9f);
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.GetComponent<Renderer>().sharedMaterial = materials["Entity"];
            body.layer = root.layer;

            GameObject eye = new GameObject("Eye");
            eye.transform.SetParent(root.transform, false);
            eye.transform.localPosition = new Vector3(0f, 1.75f, 0.2f);

            root.AddComponent<NetworkObject>();

            NpcAgent agent = root.AddComponent<NpcAgent>();
            SetObjectRef(agent, "_archetype", registry.Antagonist);
            SetObjectRef(agent, "_eye", eye.transform);
            SetMask(agent, "_obstacleMask", VigilProjectSettings.MaskFor("Default", "Ground", "Occluder", "Prop"));
            SetMask(agent, "_agentMask", VigilProjectSettings.MaskFor("NPC"));
            SetMask(agent, "_strikeMask", VigilProjectSettings.MaskFor("Player"));

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);

            RefreshNetworkIds(prefab);
            return prefab;
        }

        // =====================================================================
        // Network identity
        // =====================================================================

        /// <summary>
        /// Forces Netcode to (re)generate GlobalObjectIdHash on every NetworkObject.
        ///
        /// <para>NGO derives that hash in <c>OnValidate</c>, which the editor calls
        /// during normal inspector-driven authoring. Objects built purely from code
        /// never receive it, so every one of them ships with a hash of 0 â€” and NGO
        /// then throws "already contains the same GlobalObjectIdHash value 0"
        /// the moment a second scene-placed NetworkObject registers. The failure
        /// appears at runtime as a spawn exception with no obvious link to the
        /// generator that caused it.</para>
        ///
        /// <para>The hash is derived from the object's persisted GlobalObjectId, so
        /// this must run AFTER the scene or prefab has been written to disk.</para>
        /// </summary>
        static void RefreshNetworkIds(GameObject root)
        {
            if (root == null) return;

            NetworkObject[] objects = root.GetComponentsInChildren<NetworkObject>(true);
            for (int i = 0; i < objects.Length; i++) InvokeHashGeneration(objects[i]);

            if (objects.Length > 0) EditorUtility.SetDirty(root);
        }

        static void RefreshNetworkIds(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            int touched = 0;

            for (int r = 0; r < roots.Length; r++)
            {
                NetworkObject[] objects = roots[r].GetComponentsInChildren<NetworkObject>(true);
                for (int i = 0; i < objects.Length; i++)
                {
                    InvokeHashGeneration(objects[i]);
                    EditorUtility.SetDirty(objects[i]);
                    touched++;
                }
            }

            VLog.Info(LogCat.Net, $"Refreshed GlobalObjectIdHash on {touched} scene NetworkObject(s) in '{scene.name}'.");
        }

        static void InvokeHashGeneration(NetworkObject netObj)
        {
            if (netObj == null) return;

            System.Type type = typeof(NetworkObject);
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public;

            // NGO has moved this between versions, so try the known names in order
            // rather than binding to one and breaking on the next package bump.
            System.Reflection.MethodInfo method =
                type.GetMethod("GenerateGlobalObjectIdHash", flags) ??
                type.GetMethod("OnValidate", flags);

            if (method == null)
            {
                VLog.Warn(LogCat.Net,
                    "Could not find NGO's hash generator by reflection â€” scene NetworkObjects may collide on hash 0.");
                return;
            }

            try
            {
                method.Invoke(netObj, null);
            }
            catch (System.Exception ex)
            {
                VLog.Warn(LogCat.Net, $"GlobalObjectIdHash generation failed for {netObj.name}: {ex.Message}");
            }
        }

        // =====================================================================
        // Scenes
        // =====================================================================

        static void BuildBootstrapScene(VigilConfigRegistry registry, GameObject playerPrefab, GameObject npcPrefab)
        {
            EnsureFolder(ScenesDir);

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            GameObject bootstrapGo = new GameObject("[Vigil Bootstrap]");
            GameBootstrap bootstrap = bootstrapGo.AddComponent<GameBootstrap>();
            SetObjectRef(bootstrap, "_configs", registry);

            // The HUD survives the scene change into the level, so it is created
            // here alongside the bootstrap rather than in the level scene.
            GameObject hudGo = new GameObject("[Vigil HUD]");
            hudGo.AddComponent<VigilHud>();

            CreateNetworkManager(playerPrefab, npcPrefab);

            SaveSceneWithNetworkIds(scene, BootstrapScenePath);
        }

        /// <summary>
        /// Saves, reopens, regenerates network ids, and saves again.
        ///
        /// <para>The reopen is not redundant: GlobalObjectIdHash is derived from the
        /// object's PERSISTED GlobalObjectId, so it cannot be computed correctly
        /// until the scene has been written to disk at least once.</para>
        /// </summary>
        /// <summary>
        /// Kept as a switch because it is the first thing to bisect if a scene ever
        /// fails to serialize into a player build. Ruled out as the cause of the
        /// "level1 is corrupted" crash â€” disabling it changed the scene bytes but
        /// not the outcome â€” and it fixes a genuine hash-collision bug, so it stays on.
        /// </summary>
        const bool RefreshSceneNetworkIds = true;

        static void SaveSceneWithNetworkIds(Scene scene, string path)
        {
            EditorSceneManager.SaveScene(scene, path);

            Scene reopened = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            if (RefreshSceneNetworkIds) RefreshNetworkIds(reopened);

            EditorSceneManager.MarkSceneDirty(reopened);
            EditorSceneManager.SaveScene(reopened, path);

            // Force THIS scene to the project's serialization mode.
            //
            // Setting EditorSettings.serializationMode earlier in the same session
            // is not reliably applied to scenes written immediately afterwards â€”
            // Level_Facility kept coming out binary while Bootstrap, saved a moment
            // later by this same method, came out text. A scene left in the wrong
            // format produced a player build whose 'level1' was corrupt: it loaded
            // the menu, then crashed with "Position out of bounds!".
            //
            // Reserializing the specific path is deterministic and costs milliseconds.
            AssetDatabase.ForceReserializeAssets(new[] { path });
            AssetDatabase.SaveAssets();

            VLog.Info(LogCat.Core, $"Saved and reserialized scene '{path}'.");
        }

        static void CreateNetworkManager(GameObject playerPrefab, GameObject npcPrefab)
        {
            GameObject nmGo = new GameObject("NetworkManager");

            // The transport component is added but deliberately NOT configured here.
            // Endpoint configuration belongs to SessionDriver at runtime, which knows
            // whether this process is hosting, joining by IP, or brokering through
            // Relay. Baking an address into the prefab would also mean the editor
            // assembly has to reference Unity.Networking.Transport for NetworkEndpoint,
            // which is a dependency the content generator has no business carrying.
            UnityTransport transport = nmGo.AddComponent<UnityTransport>();

            NetworkManager nm = nmGo.AddComponent<NetworkManager>();

            SerializedObject so = new SerializedObject(nm);

            SerializedProperty transportProp = so.FindProperty("NetworkConfig.NetworkTransport");
            if (transportProp != null) transportProp.objectReferenceValue = transport;

            SerializedProperty playerPrefabProp = so.FindProperty("NetworkConfig.PlayerPrefab");
            if (playerPrefabProp != null) playerPrefabProp.objectReferenceValue = playerPrefab;

            SerializedProperty tickRateProp = so.FindProperty("NetworkConfig.TickRate");
            if (tickRateProp != null) tickRateProp.uintValue = 30u;

            // The antagonist must be in the network prefab list or the server cannot
            // spawn it â€” a missing entry produces a runtime error that reads as an
            // unrelated null reference.
            SerializedProperty prefabsList = so.FindProperty("NetworkConfig.Prefabs.NetworkPrefabsLists");
            if (prefabsList == null)
            {
                VLog.Warn(LogCat.Net,
                    "Could not locate NetworkConfig.Prefabs.NetworkPrefabsLists â€” add the Antagonist prefab " +
                    "to the NetworkManager prefab list manually.");
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void BuildLevelScene(
            VigilConfigRegistry registry,
            Dictionary<string, Material> materials,
            GameObject playerPrefab,
            GameObject npcPrefab)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            GameObject root = new GameObject("Facility");

            int groundLayer = LayerMask.NameToLayer("Ground");
            int occluderLayer = LayerMask.NameToLayer("Occluder");

            // --- floor ---
            GameObject floor = CreateBox(root.transform, "Floor", Vector3.zero, new Vector3(70f, 1f, 70f), materials["Floor"], groundLayer);
            floor.transform.position = new Vector3(0f, -0.5f, 0f);

            // --- a simple but genuinely navigable facility -----------------------
            // Rooms are boxes of walls with doorway gaps, which is enough for the
            // region graph, the NavMesh and line-of-sight to all behave meaningfully.
            BuildRoom(root.transform, materials["Wall"], occluderLayer, new Vector3(-18f, 0f, -18f), new Vector2(20f, 20f), "BoilerRoom");
            BuildRoom(root.transform, materials["Wall"], occluderLayer, new Vector3(18f, 0f, -18f), new Vector2(20f, 20f), "Offices");
            BuildRoom(root.transform, materials["Wall"], occluderLayer, new Vector3(-18f, 0f, 18f), new Vector2(20f, 20f), "Maintenance");
            BuildRoom(root.transform, materials["Wall"], occluderLayer, new Vector3(18f, 0f, 18f), new Vector2(20f, 20f), "Storage");

            // Scatter cover so concealment scoring and steering avoidance have
            // something to actually work with.
            for (int i = 0; i < 24; i++)
            {
                float x = Mathf.Lerp(-30f, 30f, (i * 7919 % 100) / 100f);
                float z = Mathf.Lerp(-30f, 30f, (i * 6271 % 100) / 100f);

                CreateBox(root.transform, $"Crate_{i}",
                    new Vector3(x, 0.6f, z), new Vector3(1.6f, 1.2f, 1.6f), materials["Prop"], occluderLayer);
            }

            // --- lighting ---
            GameObject lightGo = new GameObject("Ambient");
            Light fill = lightGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.12f;                 // near-dark: this is a horror game
            fill.color = new Color(0.6f, 0.65f, 0.8f);
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // --- objectives, doors, extraction ----------------------------------
            // Built BEFORE the NavMesh bake so the static geometry is included, but
            // doors carve dynamically via NavMeshObstacle so their state matters at
            // runtime rather than at bake time.
            Vector3[] roomCentres =
            {
                new Vector3(-18f, 0f, -18f),
                new Vector3(18f, 0f, -18f),
                new Vector3(-18f, 0f, 18f),
                new Vector3(18f, 0f, 18f)
            };

            // One door on each room's doorway facing the central hub.
            CreateDoor(root.transform, registry, materials, new Vector3(-18f, 0f, -8f), 0f, "Door_Boiler");
            CreateDoor(root.transform, registry, materials, new Vector3(8f, 0f, -18f), 90f, "Door_Offices");
            CreateDoor(root.transform, registry, materials, new Vector3(-18f, 0f, 8f), 0f, "Door_Maintenance");
            CreateDoor(root.transform, registry, materials, new Vector3(8f, 0f, 18f), 90f, "Door_Storage");

            // Three generators, one per room, each with the light it restores.
            for (int i = 0; i < 3; i++)
            {
                CreateGenerator(root.transform, registry, materials, roomCentres[i] + new Vector3(4f, 0f, 4f), i + 1, $"Generator_{i + 1}");
            }

            CreateExtraction(root.transform, registry, materials, new Vector3(0f, 0f, 0f));

            // A couple of always-on lights so the hub is navigable before power is
            // restored â€” a level that starts at zero visibility is unplayable, not scary.
            CreatePointLight(root.transform, new Vector3(0f, 3.2f, -12f), 14f, 1.1f, true, "Light_Hub_A");
            CreatePointLight(root.transform, new Vector3(0f, 3.2f, 12f), 14f, 1.1f, true, "Light_Hub_B");

            // --- navmesh ---
            NavMeshSurface surface = root.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = VigilProjectSettings.MaskFor("Default", "Ground", "Occluder", "Prop");

            // DELIBERATELY NOT BAKED HERE.
            //
            // Calling surface.BuildNavMesh() at edit time attaches a NavMeshData
            // object that exists only in memory â€” it is not saved as a project asset.
            // The editor is happy with that, which is why every PlayMode test passed,
            // but serializing it into a player build produced a corrupt 'level1':
            // the build loaded the menu and then died with "Position out of bounds!".
            //
            // VigilLevelSpawner.BakeNavMesh() bakes at level load instead (~30ms),
            // which is the correct approach for a procedurally assembled level anyway.

            // --- spawns ---
            GameObject playerSpawn = new GameObject("PlayerSpawn");
            playerSpawn.transform.position = new Vector3(0f, 0.1f, 0f);

            GameObject npcInstance = (GameObject)PrefabUtility.InstantiatePrefab(npcPrefab);
            npcInstance.transform.position = new Vector3(-18f, 0.1f, -18f);

            GameObject spawnerGo = new GameObject("[Vigil Level]");
            VigilLevelSpawner spawner = spawnerGo.AddComponent<VigilLevelSpawner>();
            SetObjectRef(spawner, "_antagonist", npcInstance.GetComponent<NpcAgent>());
            SetObjectRef(spawner, "_navMeshSurface", surface);

            // Mission state is a scene-placed NetworkObject, so NGO spawns it
            // automatically with the scene rather than needing a prefab entry.
            GameObject missionGo = new GameObject("[Mission]");
            missionGo.AddComponent<NetworkObject>();
            MissionDirector mission = missionGo.AddComponent<MissionDirector>();
            SetObjectRef(mission, "_gameplay", registry.Gameplay);

            SaveSceneWithNetworkIds(scene, LevelScenePath);
        }

        static void BuildRoom(Transform parent, Material wall, int layer, Vector3 center, Vector2 size, string name)
        {
            GameObject room = new GameObject(name);
            room.transform.SetParent(parent, false);
            room.transform.position = center;

            float hx = size.x * 0.5f;
            float hz = size.y * 0.5f;
            const float wallHeight = 4f;
            const float thickness = 0.5f;
            const float doorWidth = 3.5f;

            // Each wall is split into two segments with a gap in the middle. That gap
            // is the doorway, and it is what makes the level a connected graph rather
            // than four sealed boxes the monster can never leave.
            float segment = (size.x - doorWidth) * 0.5f;

            CreateBox(room.transform, name + "_N_a", new Vector3(-(doorWidth * 0.5f + segment * 0.5f), wallHeight * 0.5f, hz), new Vector3(segment, wallHeight, thickness), wall, layer);
            CreateBox(room.transform, name + "_N_b", new Vector3(doorWidth * 0.5f + segment * 0.5f, wallHeight * 0.5f, hz), new Vector3(segment, wallHeight, thickness), wall, layer);

            CreateBox(room.transform, name + "_S_a", new Vector3(-(doorWidth * 0.5f + segment * 0.5f), wallHeight * 0.5f, -hz), new Vector3(segment, wallHeight, thickness), wall, layer);
            CreateBox(room.transform, name + "_S_b", new Vector3(doorWidth * 0.5f + segment * 0.5f, wallHeight * 0.5f, -hz), new Vector3(segment, wallHeight, thickness), wall, layer);

            CreateBox(room.transform, name + "_W_a", new Vector3(-hx, wallHeight * 0.5f, -(doorWidth * 0.5f + segment * 0.5f)), new Vector3(thickness, wallHeight, segment), wall, layer);
            CreateBox(room.transform, name + "_W_b", new Vector3(-hx, wallHeight * 0.5f, doorWidth * 0.5f + segment * 0.5f), new Vector3(thickness, wallHeight, segment), wall, layer);

            CreateBox(room.transform, name + "_E_a", new Vector3(hx, wallHeight * 0.5f, -(doorWidth * 0.5f + segment * 0.5f)), new Vector3(thickness, wallHeight, segment), wall, layer);
            CreateBox(room.transform, name + "_E_b", new Vector3(hx, wallHeight * 0.5f, doorWidth * 0.5f + segment * 0.5f), new Vector3(thickness, wallHeight, segment), wall, layer);
        }

        // ---------------------------------------------------------- interactables

        static GameObject CreateDoor(
            Transform parent, VigilConfigRegistry registry, Dictionary<string, Material> materials,
            Vector3 position, float yaw, string name)
        {
            int interactableLayer = LayerMask.NameToLayer("Interactable");

            GameObject root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            root.layer = interactableLayer;

            // Hinge at the frame edge so the leaf swings rather than spinning about
            // its own centre, which reads immediately as wrong.
            GameObject hinge = new GameObject("Hinge");
            hinge.transform.SetParent(root.transform, false);
            hinge.transform.localPosition = new Vector3(-1.7f, 0f, 0f);

            GameObject leaf = GameObject.CreatePrimitive(PrimitiveType.Cube);
            leaf.name = "Leaf";
            leaf.transform.SetParent(hinge.transform, false);
            leaf.transform.localPosition = new Vector3(1.7f, 1.8f, 0f);
            leaf.transform.localScale = new Vector3(3.4f, 3.6f, 0.16f);
            leaf.GetComponent<Renderer>().sharedMaterial = materials["Metal"];
            leaf.layer = interactableLayer;

            NavMeshObstacle obstacle = root.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.center = new Vector3(0f, 1.8f, 0f);
            obstacle.size = new Vector3(3.5f, 3.6f, 0.5f);
            obstacle.carving = true;
            obstacle.carveOnlyStationary = false;

            root.AddComponent<NetworkObject>();

            Door door = root.AddComponent<Door>();
            SetObjectRef(door, "_gameplay", registry.Gameplay);
            SetObjectRef(door, "_leaf", hinge.transform);
            SetObjectRef(door, "_obstacle", obstacle);
            SetObjectRef(door, "_interactionAnchor", leaf.transform);

            return root;
        }

        static GameObject CreateGenerator(
            Transform parent, VigilConfigRegistry registry, Dictionary<string, Material> materials,
            Vector3 position, int regionId, string name)
        {
            int interactableLayer = LayerMask.NameToLayer("Interactable");

            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            root.name = name;
            root.transform.SetParent(parent, false);
            root.transform.position = position + new Vector3(0f, 0.9f, 0f);
            root.transform.localScale = new Vector3(1.5f, 1.8f, 1.1f);
            root.GetComponent<Renderer>().sharedMaterial = materials["Metal"];
            root.layer = interactableLayer;

            // The light this generator restores. Off until the repair completes â€”
            // that transition is the reward for taking the risk.
            GameObject lightGo = new GameObject(name + "_Light");
            lightGo.transform.SetParent(parent, false);
            lightGo.transform.position = position + new Vector3(0f, 3.4f, 0f);

            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 20f;
            light.intensity = 2.4f;
            light.color = new Color(1f, 0.93f, 0.78f);
            light.enabled = false;

            root.AddComponent<NetworkObject>();

            Generator generator = root.AddComponent<Generator>();
            SetObjectRef(generator, "_gameplay", registry.Gameplay);
            SetInt(generator, "_regionId", regionId);
            SetObjectArray(generator, "_poweredLights", new Object[] { light });

            return root;
        }

        static GameObject CreateExtraction(
            Transform parent, VigilConfigRegistry registry, Dictionary<string, Material> materials,
            Vector3 position)
        {
            int interactableLayer = LayerMask.NameToLayer("Interactable");

            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            root.name = "ExtractionPoint";
            root.transform.SetParent(parent, false);
            root.transform.position = position + new Vector3(0f, 1.5f, 0f);
            root.transform.localScale = new Vector3(3f, 3f, 0.6f);
            root.GetComponent<Renderer>().sharedMaterial = materials["Emissive"];
            root.layer = interactableLayer;

            root.AddComponent<NetworkObject>();

            ExtractionPoint extraction = root.AddComponent<ExtractionPoint>();
            SetObjectRef(extraction, "_gameplay", registry.Gameplay);

            return root;
        }

        static GameObject CreatePointLight(
            Transform parent, Vector3 position, float range, float intensity, bool enabled, string name)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = position;

            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = range;
            light.intensity = intensity;
            light.color = new Color(0.85f, 0.87f, 1f);
            light.enabled = enabled;

            return go;
        }

        static void SetInt(Object target, string propertyName, int value)
        {
            SerializedObject so = new SerializedObject(target);
            SerializedProperty prop = so.FindProperty(propertyName);
            if (prop == null) return;
            prop.intValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetObjectArray(Object target, string propertyName, Object[] values)
        {
            SerializedObject so = new SerializedObject(target);
            SerializedProperty prop = so.FindProperty(propertyName);
            if (prop == null || !prop.isArray) return;

            prop.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                prop.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static GameObject CreateBox(Transform parent, string name, Vector3 localPosition, Vector3 scale, Material material, int layer)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
            if (layer >= 0) go.layer = layer;
            return go;
        }

        static void RegisterScenesInBuildSettings()
        {
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(BootstrapScenePath, true),
                new EditorBuildSettingsScene(LevelScenePath, true)
            };
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;

            string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(path);

            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);

            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}

