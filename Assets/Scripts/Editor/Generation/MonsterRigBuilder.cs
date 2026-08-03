// -----------------------------------------------------------------------------
// Vigil — procedural antagonist rig.
//
// The creature is assembled from Unity primitives at edit time rather than
// imported, for the same reason the rest of the project is generated: no binary
// assets, no GUIDs that drift, and "regenerate" is always a valid recovery.
//
// TWO CONVENTIONS GOVERN THIS FILE, AND BOTH ARE LOAD-BEARING FOR THE ANIMATOR:
//
//   1. JOINTS are empty transforms named exactly as the rig contract specifies
//      (Torso, Head, Eye_L, Eye_R, Arm_?_Upper/Lower, Hip, Leg_?_Upper/Lower).
//      These are the ONLY transforms that may be animated.
//
//   2. GEOMETRY is every object whose name ends in "_Mesh". It is pure visual
//      dressing parented under a joint and MUST NOT be animated or renamed —
//      it exists only to give a joint something to show. Limb geometry hangs
//      DOWNWARD along the joint's local -Y, so rotating a joint swings the whole
//      segment about its own origin, which is what makes hand-authored rotation
//      curves behave the way an animator expects.
//
// The single exception to (2) is the Torso, which is not a limb: its pivot sits
// at the base of the spine and its geometry extends UPWARD, because a torso
// leans from the waist. Head and shoulders ride at the far end of that spine.
//
// Every rest angle below is a named constant so the animation pass can read the
// rest pose out of source instead of measuring it in the scene view.
// -----------------------------------------------------------------------------

using UnityEditor;
using UnityEngine;
using Vigil.Core.Diagnostics;

namespace Vigil.Editor.Generation
{
    /// <summary>
    /// Builds the antagonist's visual rig from primitives, and the two materials
    /// it needs. Editor-only: nothing here runs in a player.
    /// </summary>
    public static class MonsterRigBuilder
    {
        const string MaterialsDir = "Assets/Art/Materials";
        const string GhostMaterialPath = MaterialsDir + "/M_GhostBody.mat";
        const string EyeMaterialPath = MaterialsDir + "/M_GhostEye.mat";
        const string AntagonistPrefabPath = "Assets/Prefabs/Antagonist.prefab";

        // ---------------------------------------------------------------------
        // Rest pose.
        //
        // The creature reads wrong on purpose. The three cues doing the work are:
        // a spine that leans 32 degrees so the skull hangs AHEAD of the hips, arms
        // that reach past the ankles, and a digitigrade leg whose three segments
        // zig-zag forward/back/forward so the silhouette has no human knee in it.
        //
        // Signs: the creature faces +Z, so its LEFT is -X (Unity is left-handed).
        // A negative X rotation tilts a downward-hanging segment FORWARD.
        // ---------------------------------------------------------------------

        const float HipHeight = 1.70f;
        const float HipOffsetZ = -0.20f;        // hips sit behind the origin; the head sits ahead of it

        const float LegSpacingX = 0.17f;
        const float UpperLegLength = 0.80f;
        const float UpperLegThickness = 0.145f;
        const float UpperLegPitch = -34f;       // forward and down, out of the hip
        const float LowerLegLength = 0.72f;
        const float LowerLegThickness = 0.115f;
        const float LowerLegPitch = 68f;        // back and down: this is the hock, not a knee
        const float FootLength = 0.46f;
        const float FootThickness = 0.10f;
        const float FootPitch = -80f;           // steeply forward-down; the creature stands on its toes
        const float LegSplay = 4f;

        const float TorsoHeight = 1.66f;
        const float TorsoLean = 32f;            // hunch, measured at the base of the spine
        const float SpineLength = 0.90f;

        const float ShoulderSpacingX = 0.20f;
        const float ShoulderUp = 0.80f;         // along the spine, not along world Y
        const float UpperArmLength = 0.88f;
        const float UpperArmThickness = 0.10f;
        const float UpperArmPitchLeft = -44f;   // deliberately NOT mirrored — see BuildRig
        const float UpperArmPitchRight = -40f;
        const float LowerArmLength = 0.92f;
        const float LowerArmThickness = 0.082f;
        const float LowerArmPitch = 16f;
        const float ArmSplay = 7f;

        const float EyeSpacingX = 0.055f;
        const float EyeDiameter = 0.08f;

        // =====================================================================
        // Rig
        // =====================================================================

        /// <summary>
        /// Builds the full creature under <paramref name="parent"/> and returns the
        /// "Rig" container. Safe to call with a null parent (produces a loose rig).
        /// </summary>
        public static GameObject BuildRig(Transform parent, Material body, Material eye)
        {
            GameObject rig = new GameObject("Rig");

            if (parent != null)
            {
                rig.transform.SetParent(parent, false);
            }

            rig.transform.localPosition = Vector3.zero;
            rig.transform.localRotation = Quaternion.identity;
            rig.transform.localScale = Vector3.one;

            // Torso and Hip are SIBLINGS, not a chain — that is the rig contract.
            // Rotating Hip swings the legs and nothing else; rotating Torso swings
            // the upper body and nothing else. Any animation that wants the whole
            // body to lean has to drive both.
            Transform torso = BuildTorso(rig.transform, body, eye);
            BuildHip(rig.transform, body);

            // Left and right are NOT exact mirrors. A perfectly symmetrical rest
            // pose is the single strongest cue that something is a costume; four
            // degrees of difference in the shoulders is invisible as a detail and
            // unmistakable as a feeling.
            BuildArm(torso, "L", -1f, UpperArmPitchLeft, body);
            BuildArm(torso, "R", 1f, UpperArmPitchRight, body);

            if (parent != null)
            {
                SetLayerRecursive(rig.transform, parent.gameObject.layer);
            }

            return rig;
        }

        static Transform BuildTorso(Transform rig, Material body, Material eye)
        {
            GameObject torso = CreateJoint(rig, "Torso",
                new Vector3(0f, TorsoHeight, HipOffsetZ),
                new Vector3(TorsoLean, 0f, 0f));

            // Three stacked sections instead of one capsule: a capsule cannot taper,
            // and the taper is the whole point. Narrow waist, distended ribcage,
            // narrow again at the shoulders — long, thin, and wrong at both ends.
            CreateMeshPart(torso.transform, "Torso_Lower_Mesh", PrimitiveType.Capsule,
                new Vector3(0f, 0.18f, 0f), Vector3.zero, new Vector3(0.26f, 0.17f, 0.24f), body);

            CreateMeshPart(torso.transform, "Torso_Mid_Mesh", PrimitiveType.Capsule,
                new Vector3(0f, 0.46f, 0.01f), Vector3.zero, new Vector3(0.32f, 0.19f, 0.28f), body);

            CreateMeshPart(torso.transform, "Torso_Upper_Mesh", PrimitiveType.Capsule,
                new Vector3(0f, 0.74f, 0f), Vector3.zero, new Vector3(0.24f, 0.17f, 0.22f), body);

            // The hump behind the shoulders. Without it the lean reads as "leaning
            // over" rather than "built wrong", because a straight spine at an angle
            // is still a human spine.
            CreateMeshPart(torso.transform, "Torso_Hump_Mesh", PrimitiveType.Sphere,
                new Vector3(0f, 0.84f, -0.09f), Vector3.zero, new Vector3(0.30f, 0.24f, 0.26f), body);

            BuildHead(torso.transform, body, eye);
            return torso.transform;
        }

        static void BuildHead(Transform torso, Material body, Material eye)
        {
            // Head is a bare pivot at the top of the spine with the skull parented
            // under it. If the skull WERE the Head transform, its non-uniform scale
            // would deform the eyes, which are its children.
            //
            // The extra yaw and roll are the asymmetry mentioned in BuildRig: the
            // head is very slightly cocked, permanently.
            GameObject head = CreateJoint(torso, "Head",
                new Vector3(0f, SpineLength, 0f),
                new Vector3(14f, 5f, -4f));

            // One featureless egg. No jaw, no brow, no mouth — a face gives the
            // player something to read as an expression, and there is nothing to
            // express. The eyes have to carry the whole read.
            CreateMeshPart(head.transform, "Head_Mesh", PrimitiveType.Sphere,
                new Vector3(0f, 0.15f, 0.015f), Vector3.zero, new Vector3(0.22f, 0.28f, 0.25f), body);

            // Eyes are contract-named leaves, so they ARE the spheres rather than
            // pivots with geometry under them.
            //
            // Z is tuned so a little over half of each sphere clears the skull
            // surface. Sunk further they vanish entirely in profile; pushed further
            // out they bulge and read as googly rather than deep. Set close together
            // because narrow-set eyes on a wide skull is a predator cue.
            CreateEye(head.transform, "Eye_L", -EyeSpacingX, eye);
            CreateEye(head.transform, "Eye_R", EyeSpacingX, eye);
        }

        static void CreateEye(Transform head, string name, float x, Material eye)
        {
            GameObject go = CreatePrimitiveNoCollider(PrimitiveType.Sphere, name, head);
            go.transform.localPosition = new Vector3(x, 0.185f, 0.10f);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(EyeDiameter, EyeDiameter, EyeDiameter);
            ApplyMaterial(go, eye);
        }

        static void BuildHip(Transform rig, Material body)
        {
            GameObject hip = CreateJoint(rig, "Hip",
                new Vector3(0f, HipHeight, HipOffsetZ), Vector3.zero);

            CreateMeshPart(hip.transform, "Hip_Mesh", PrimitiveType.Sphere,
                new Vector3(0f, -0.06f, 0f), Vector3.zero, new Vector3(0.30f, 0.26f, 0.24f), body);

            BuildLeg(hip.transform, "L", -1f, body);
            BuildLeg(hip.transform, "R", 1f, body);
        }

        static void BuildLeg(Transform hip, string side, float sideSign, Material body)
        {
            GameObject upper = CreateJoint(hip, "Leg_" + side + "_Upper",
                new Vector3(sideSign * LegSpacingX, -0.08f, 0.02f),
                new Vector3(UpperLegPitch, 0f, sideSign * LegSplay));

            AddSegmentMesh(upper.transform, "Leg_" + side + "_Upper_Mesh", PrimitiveType.Capsule,
                UpperLegLength, UpperLegThickness, 0f, body);

            // The lower joint sits at the far END of the parent segment, which in
            // the parent's own space is straight down by its length. Getting this
            // wrong is how limbs end up detached the moment anything rotates.
            GameObject lower = CreateJoint(upper.transform, "Leg_" + side + "_Lower",
                new Vector3(0f, -UpperLegLength, 0f),
                new Vector3(LowerLegPitch, 0f, 0f));

            AddSegmentMesh(lower.transform, "Leg_" + side + "_Lower_Mesh", PrimitiveType.Capsule,
                LowerLegLength, LowerLegThickness, 0f, body);

            // The foot is geometry, not a joint — the contract has no foot bone.
            // It is pitched hard forward out of the hock so the creature carries its
            // weight on the toes; that third direction change is what turns two
            // straight segments into a recognisably non-human leg.
            AddSegmentMesh(lower.transform, "Leg_" + side + "_Foot_Mesh", PrimitiveType.Capsule,
                FootLength, FootThickness, FootPitch, body,
                new Vector3(0f, -LowerLegLength, 0f));
        }

        static void BuildArm(Transform torso, string side, float sideSign, float upperPitch, Material body)
        {
            GameObject upper = CreateJoint(torso, "Arm_" + side + "_Upper",
                new Vector3(sideSign * ShoulderSpacingX, ShoulderUp, 0f),
                new Vector3(upperPitch, 0f, sideSign * ArmSplay));

            AddSegmentMesh(upper.transform, "Arm_" + side + "_Upper_Mesh", PrimitiveType.Capsule,
                UpperArmLength, UpperArmThickness, 0f, body);

            GameObject lower = CreateJoint(upper.transform, "Arm_" + side + "_Lower",
                new Vector3(0f, -UpperArmLength, 0f),
                new Vector3(LowerArmPitch, 0f, sideSign * 3f));

            AddSegmentMesh(lower.transform, "Arm_" + side + "_Lower_Mesh", PrimitiveType.Capsule,
                LowerArmLength, LowerArmThickness, 0f, body);

            BuildHand(lower.transform, side, body);
        }

        /// <summary>
        /// Palm and three fingers, all geometry. Grouped under one container so the
        /// whole hand can be hidden or swapped without touching the arm chain.
        /// </summary>
        static void BuildHand(Transform lowerArm, string side, Material body)
        {
            GameObject hand = CreateJoint(lowerArm, "Arm_" + side + "_Hand_Mesh",
                new Vector3(0f, -LowerArmLength, 0f), new Vector3(8f, 0f, 0f));

            CreateMeshPart(hand.transform, "Palm_Mesh", PrimitiveType.Cube,
                new Vector3(0f, -0.06f, 0f), Vector3.zero, new Vector3(0.09f, 0.12f, 0.045f), body);

            // Three fingers, not five. An odd count with obvious gaps between them
            // reads as a hand that was never a hand, where four thin fingers just
            // read as a hand with a missing digit.
            for (int i = 0; i < 3; i++)
            {
                float spread = (i - 1) * 0.032f;

                AddSegmentMesh(hand.transform, "Finger_" + i + "_Mesh", PrimitiveType.Capsule,
                    0.24f, 0.035f, 0f, body,
                    new Vector3(spread, -0.12f, 0.005f),
                    (i - 1) * 8f);
            }
        }

        // =====================================================================
        // Primitive helpers
        // =====================================================================

        static GameObject CreateJoint(Transform parent, string name, Vector3 localPosition, Vector3 localEuler)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.Euler(localEuler);
            go.transform.localScale = Vector3.one;
            return go;
        }

        /// <summary>
        /// Places a segment's geometry so it hangs down from its pivot's origin.
        /// The mesh centre is half a length along the (optionally pitched) axis,
        /// which is what makes a rotation of the pivot swing the segment instead of
        /// spinning it about its own middle.
        /// </summary>
        static GameObject AddSegmentMesh(
            Transform pivot, string name, PrimitiveType type,
            float length, float thickness, float pitch, Material material,
            Vector3 origin = default, float roll = 0f)
        {
            Quaternion rotation = Quaternion.Euler(pitch, 0f, roll);
            Vector3 centre = origin + rotation * new Vector3(0f, -length * 0.5f, 0f);

            return CreateMeshPart(pivot, name, type, centre, new Vector3(pitch, 0f, roll),
                SegmentScale(type, length, thickness), material);
        }

        static GameObject CreateMeshPart(
            Transform parent, string name, PrimitiveType type,
            Vector3 localPosition, Vector3 localEuler, Vector3 localScale, Material material)
        {
            GameObject go = CreatePrimitiveNoCollider(type, name, parent);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.Euler(localEuler);
            go.transform.localScale = localScale;
            ApplyMaterial(go, material);
            return go;
        }

        static GameObject CreatePrimitiveNoCollider(PrimitiveType type, string name, Transform parent)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;

            // Every primitive arrives with a collider. Left in place they fight the
            // root CapsuleCollider, and because the NavMesh bake collects physics
            // colliders they would also carve the creature's own body out of the
            // mesh it has to walk on.
            Collider collider = go.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);

            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }

        static Vector3 SegmentScale(PrimitiveType type, float length, float thickness)
        {
            // A Unity capsule is TWO units tall at unit scale (a 1-unit cylinder
            // plus two 0.5 hemispheres), so half the length is the right Y scale.
            // Cubes and spheres are one unit and take the length directly.
            float y = type == PrimitiveType.Capsule ? length * 0.5f : length;
            return new Vector3(thickness, y, thickness);
        }

        static void ApplyMaterial(GameObject go, Material material)
        {
            if (material == null) return;

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer == null) return;

            renderer.sharedMaterial = material;
        }

        static void SetLayerRecursive(Transform root, int layer)
        {
            root.gameObject.layer = layer;

            for (int i = 0; i < root.childCount; i++)
            {
                SetLayerRecursive(root.GetChild(i), layer);
            }
        }

        // =====================================================================
        // Materials
        // =====================================================================

        /// <summary>
        /// Near-black, matte, faintly translucent body material. Returns the
        /// existing asset unchanged if one is already at <paramref name="assetPath"/>.
        /// </summary>
        public static Material CreateGhostMaterial(string assetPath)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null) return existing;

            Shader shader = FindLitShader();
            if (shader == null) return null;

            Material mat = new Material(shader);

            // Darker than the level's darkest surface, and almost entirely matte.
            // A shiny creature catches the flashlight and gives away its position
            // from across a room; this one has to be found, not spotted.
            Color colour = new Color(0.028f, 0.026f, 0.032f, 0.85f);

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", colour);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", colour);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.06f);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.06f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);

            ApplyTransparency(mat);

            EnsureFolderFor(assetPath);
            AssetDatabase.CreateAsset(mat, assetPath);

            VLog.Info(LogCat.Core, $"Created ghost body material at '{assetPath}'.");
            return mat;
        }

        /// <summary>
        /// Strongly emissive eye material. Returns the existing asset unchanged if
        /// one is already at <paramref name="assetPath"/>.
        /// </summary>
        public static Material CreateEyeMaterial(string assetPath)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null) return existing;

            Shader shader = FindLitShader();
            if (shader == null) return null;

            Material mat = new Material(shader);

            // The lit base is nearly black on purpose. If the eyes had a bright
            // albedo they would also be visible under the flashlight as two grey
            // dots, and the whole effect depends on them appearing out of nothing.
            Color baseColour = new Color(0.09f, 0.02f, 0.012f, 1f);

            // Dim red-orange at intensity 4. Above the level's bloom threshold of
            // 1.1, so they bleed slightly into the fog and read at distance — the
            // silhouette the player actually sees is two smears, not two spheres.
            // Pushed much higher they tonemap to white and stop looking organic.
            Color emission = new Color(1f, 0.30f, 0.11f) * 4f;

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseColour);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", baseColour);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.5f);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.5f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);

            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", emission);

            EnsureFolderFor(assetPath);
            AssetDatabase.CreateAsset(mat, assetPath);

            VLog.Info(LogCat.Core, $"Created emissive eye material at '{assetPath}'.");
            return mat;
        }

        static Shader FindLitShader()
        {
            // URP/Lit is what ships. Standard exists only so a misconfigured project
            // renders something instead of magenta.
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

            if (shader == null)
            {
                VLog.Error(LogCat.Core, "MonsterRigBuilder: no usable lit shader found — the creature will render magenta.");
            }

            return shader;
        }

        /// <summary>
        /// Switches a material to alpha-blended transparency, handling URP/Lit and
        /// the Standard fallback separately because they use different property
        /// names for the same concept. No-ops on any shader that has neither.
        /// </summary>
        static void ApplyTransparency(Material mat)
        {
            const int srcAlpha = (int)UnityEngine.Rendering.BlendMode.SrcAlpha;
            const int oneMinusSrcAlpha = (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;
            const int transparentQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);           // 0 opaque, 1 transparent
                mat.SetFloat("_Blend", 0f);             // alpha blend
                mat.SetFloat("_AlphaClip", 0f);
                mat.SetFloat("_SrcBlend", srcAlpha);
                mat.SetFloat("_DstBlend", oneMinusSrcAlpha);
                mat.SetFloat("_ZWrite", 0f);

                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");

                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = transparentQueue;
                return;
            }

            if (mat.HasProperty("_Mode"))
            {
                mat.SetFloat("_Mode", 2f);              // Standard shader "Fade"
                mat.SetInt("_SrcBlend", srcAlpha);
                mat.SetInt("_DstBlend", oneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);

                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");

                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = transparentQueue;
                return;
            }

            VLog.Warn(LogCat.Core, "MonsterRigBuilder: shader exposes no surface-type property — ghost body stays opaque.");
        }

        // =====================================================================
        // Prefab integration
        // =====================================================================

        [MenuItem("Vigil/Rebuild Creature Rig", priority = 30)]
        static void RebuildCreatureRigMenu()
        {
            AddToAntagonistPrefab(AntagonistPrefabPath);
        }

        /// <summary>
        /// Replaces the antagonist prefab's placeholder capsule with the full rig.
        /// Idempotent: an existing "Body" or "Rig" child is removed first, so this
        /// can be re-run after any tuning change. The pre-existing "Eye" sensor
        /// transform is left exactly where it is — the AI reads it.
        /// </summary>
        /// <summary>
        /// Drops the rig so its lowest rendered point rests on the root's origin.
        ///
        /// <para>The creature is assembled from segment lengths and joint offsets, so
        /// its actual standing height is an emergent result of a dozen numbers rather
        /// than something authored directly. Getting it wrong by even 40cm leaves the
        /// monster visibly hovering — which it was. Measuring the finished bounds and
        /// correcting once is far more robust than hand-tuning a magic hip height
        /// that breaks the moment any limb length changes.</para>
        /// </summary>
        static void GroundRig(GameObject rig)
        {
            if (rig == null) return;

            Renderer[] renderers = rig.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return;

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            // Inside a prefab preview scene the root sits at the world origin, so the
            // world-space minimum is already the offset from the character's feet.
            float lowest = bounds.min.y;

            rig.transform.position -= new Vector3(0f, lowest, 0f);

            VLog.Info(LogCat.Core,
                $"Grounded creature rig: lowered {lowest:F2}m, standing height {bounds.size.y:F2}m.");
        }

        public static void AddToAntagonistPrefab(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath))
            {
                VLog.Error(LogCat.Core, "MonsterRigBuilder: no prefab path supplied.");
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                VLog.Error(LogCat.Core, $"MonsterRigBuilder: no prefab at '{prefabPath}'. Generate the sample content first.");
                return;
            }

            Material body = CreateGhostMaterial(GhostMaterialPath);
            Material eye = CreateEyeMaterial(EyeMaterialPath);

            if (body == null || eye == null)
            {
                VLog.Error(LogCat.Core, "MonsterRigBuilder: material creation failed — aborting before touching the prefab.");
                return;
            }

            // Prefab ASSET transforms reject SetParent outright ("Setting the parent
            // of a transform which resides in a Prefab Asset is disabled"), so the
            // rig cannot be built directly on the object returned by
            // LoadAssetAtPath and flushed with PrefabUtility.SavePrefabAsset.
            // LoadPrefabContents opens the same prefab in a throwaway preview scene
            // where the hierarchy is fully editable; SaveAsPrefabAsset writes it
            // back to the same path, preserving the asset's GUID and every existing
            // reference to it.
            GameObject contents = null;

            try
            {
                contents = PrefabUtility.LoadPrefabContents(prefabPath);

                if (contents == null)
                {
                    VLog.Error(LogCat.Core, $"MonsterRigBuilder: could not open prefab contents for '{prefabPath}'.");
                    return;
                }

                int removed = RemoveChild(contents.transform, "Body") + RemoveChild(contents.transform, "Rig");

                GameObject rig = BuildRig(contents.transform, body, eye);
                GroundRig(rig);

                GameObject saved = PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);

                if (saved == null)
                {
                    VLog.Error(LogCat.Core, $"MonsterRigBuilder: failed to save '{prefabPath}'.");
                    return;
                }

                VLog.Info(LogCat.Core,
                    $"Built creature rig on '{prefabPath}' (removed {removed} old child object(s)).");
            }
            catch (System.Exception ex)
            {
                VLog.Exception(LogCat.Core, ex);
            }
            finally
            {
                // Leaking the preview scene leaves an invisible, un-closeable scene
                // open for the rest of the editor session.
                if (contents != null) PrefabUtility.UnloadPrefabContents(contents);
            }

            AssetDatabase.SaveAssets();
        }

        /// <summary>Destroys every direct child with the given name. Returns the count.</summary>
        static int RemoveChild(Transform root, string name)
        {
            int removed = 0;

            // Backwards, because destroying a child re-indexes the ones after it.
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                if (child == null || child.name != name) continue;

                Object.DestroyImmediate(child.gameObject);
                removed++;
            }

            return removed;
        }

        // =====================================================================
        // Asset folders
        // =====================================================================

        static void EnsureFolderFor(string assetPath)
        {
            string dir = System.IO.Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(dir)) return;

            EnsureFolder(dir.Replace('\\', '/'));
        }

        static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;

            string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(path);

            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return;

            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);

            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
