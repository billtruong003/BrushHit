#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace CleanRender.Editor
{
    /// <summary>
    /// One-click scene scanner: finds all renderers using instancing-compatible shaders,
    /// groups by Mesh+Material, creates runtime managers for GPU Indirect Draw.
    /// 
    /// Usage: Tools → CleanRender → Setup Static Instancing
    /// 
    /// What it does:
    /// 1. Scans all MeshRenderers in the scene
    /// 2. Filters for shaders tagged "StaticInstancing" = "True" (ToonLit, ToonMetal, ToonFoliage, etc.)
    /// 3. Groups instances by (Mesh, Material) pair
    /// 4. For each group: creates a manager GameObject with StaticInstanceManager component
    /// 5. Disables original renderers (GPU indirect takes over)
    /// 6. At runtime, StaticInstanceManager feeds CompressedInstanceData → Compute Cull → IndirectDraw
    /// </summary>
    public class StaticInstanceSetup : EditorWindow
    {
        // ── Settings ──
        private int minInstancesForIndirect = 10;
        private float defaultCullDistance = 500f;
        private float defaultShadowDistance = 150f;
        private ComputeShader cullingShader;
        private bool disableOriginalRenderers = true;
        private bool skipAlreadySetup = true;
        private bool includeInactive = false;
        private LayerMask targetLayers = ~0;
        private string containerName = "[CleanRender_Instances]";

        // ── Results ──
        private int lastGroupCount;
        private int lastInstanceCount;
        private int lastSkippedCount;
        private string lastLog = "";
        private Vector2 scrollPos;

        // ── Compatible shader tags ──
        private static readonly HashSet<string> CompatibleShaderNames = new HashSet<string>
        {
            "CleanRender/ToonLit",
            "CleanRender/ToonMetal",
            "CleanRender/ToonFoliage",
            "CleanRender/ToonGrass",
            "CleanRender/ToonTerrain"
        };

        [MenuItem("Tools/CleanRender/Setup Static Instancing %#i")]
        public static void ShowWindow()
        {
            var w = GetWindow<StaticInstanceSetup>("Static Instance Setup");
            w.minSize = new Vector2(500, 600);
        }

        private void OnEnable()
        {
            // Auto-find culling shader
            if (cullingShader == null)
            {
                var guids = AssetDatabase.FindAssets("ImprovedStaticCulling t:ComputeShader");
                if (guids.Length > 0)
                    cullingShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                        AssetDatabase.GUIDToAssetPath(guids[0]));
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(5);
            GUILayout.Label("━━━ STATIC INSTANCE SETUP ━━━", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Scans the scene for instancing-compatible shaders (ToonLit, ToonMetal, ToonFoliage, etc.),\n" +
                "groups by Mesh+Material, and creates GPU Indirect Draw managers.\n" +
                "One click = entire scene optimized.",
                MessageType.Info);

            EditorGUILayout.Space(10);

            // ── Settings ──
            GUILayout.Label("Settings", EditorStyles.boldLabel);
            cullingShader = (ComputeShader)EditorGUILayout.ObjectField(
                "Culling Compute Shader", cullingShader, typeof(ComputeShader), false);
            minInstancesForIndirect = EditorGUILayout.IntSlider(
                "Min Instances for Indirect", minInstancesForIndirect, 2, 100);
            defaultCullDistance = EditorGUILayout.FloatField("Default Cull Distance", defaultCullDistance);
            defaultShadowDistance = EditorGUILayout.FloatField("Default Shadow Distance", defaultShadowDistance);
            disableOriginalRenderers = EditorGUILayout.Toggle("Disable Original Renderers", disableOriginalRenderers);
            skipAlreadySetup = EditorGUILayout.Toggle("Skip Already Setup", skipAlreadySetup);
            includeInactive = EditorGUILayout.Toggle("Include Inactive Objects", includeInactive);
            containerName = EditorGUILayout.TextField("Container Name", containerName);

            EditorGUILayout.Space(10);

            // ── Actions ──
            EditorGUILayout.BeginHorizontal();

            GUI.color = new Color(0.3f, 1f, 0.5f);
            if (GUILayout.Button("SCAN & SETUP", GUILayout.Height(40)))
            {
                ScanAndSetup();
            }
            GUI.color = Color.white;

            GUI.color = new Color(1f, 0.8f, 0.3f);
            if (GUILayout.Button("DRY RUN\n(Preview Only)", GUILayout.Height(40)))
            {
                DryRun();
            }
            GUI.color = Color.white;

            GUI.color = new Color(1f, 0.4f, 0.3f);
            if (GUILayout.Button("REMOVE ALL\nInstance Managers", GUILayout.Height(40)))
            {
                RemoveAll();
            }
            GUI.color = Color.white;

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            // ── Results ──
            if (!string.IsNullOrEmpty(lastLog))
            {
                GUILayout.Label("Last Result", EditorStyles.boldLabel);
                scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.Height(250));
                EditorGUILayout.HelpBox(lastLog, MessageType.None);
                EditorGUILayout.EndScrollView();
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Core: Scan & Setup
        // ════════════════════════════════════════════════════════════════

        private struct MeshMatKey
        {
            public Mesh mesh;
            public Material material;

            public override int GetHashCode()
            {
                int h1 = mesh != null ? mesh.GetHashCode() : 0;
                int h2 = material != null ? material.GetHashCode() : 0;
                return h1 ^ (h2 << 16);
            }

            public override bool Equals(object obj)
            {
                if (obj is MeshMatKey other)
                    return mesh == other.mesh && material == other.material;
                return false;
            }
        }

        private struct InstanceInfo
        {
            public GameObject gameObject;
            public MeshRenderer renderer;
            public MeshFilter filter;
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 scale;
        }

        private Dictionary<MeshMatKey, List<InstanceInfo>> GatherInstances()
        {
            var groups = new Dictionary<MeshMatKey, List<InstanceInfo>>();
            int skipped = 0;

            MeshRenderer[] allRenderers;
            if (includeInactive)
                allRenderers = Resources.FindObjectsOfTypeAll<MeshRenderer>()
                    .Where(r => r.gameObject.scene.isLoaded).ToArray();
            else
                allRenderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);

            foreach (var renderer in allRenderers)
            {
                // Skip if already managed
                if (skipAlreadySetup && renderer.GetComponentInParent<StaticInstanceManager>() != null)
                {
                    skipped++;
                    continue;
                }

                // Layer check
                if (((1 << renderer.gameObject.layer) & targetLayers) == 0)
                    continue;

                // Get mesh
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;

                // Check each material
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null || mat.shader == null) continue;

                    // Check if shader is compatible
                    if (!IsCompatibleShader(mat.shader)) continue;

                    // Ensure material has instancing enabled
                    if (!mat.enableInstancing)
                    {
                        // Auto-enable it
                        mat.enableInstancing = true;
                        EditorUtility.SetDirty(mat);
                    }

                    var key = new MeshMatKey { mesh = filter.sharedMesh, material = mat };
                    if (!groups.ContainsKey(key))
                        groups[key] = new List<InstanceInfo>();

                    var t = renderer.transform;
                    groups[key].Add(new InstanceInfo
                    {
                        gameObject = renderer.gameObject,
                        renderer = renderer,
                        filter = filter,
                        position = t.position,
                        rotation = t.rotation,
                        scale = t.lossyScale
                    });
                }
            }

            lastSkippedCount = skipped;
            return groups;
        }

        private bool IsCompatibleShader(Shader shader)
        {
            // Check by name
            if (CompatibleShaderNames.Contains(shader.name))
                return true;

            // Check by tag in SubShader — we look for shaders that have
            // "StaticInstancing" = "True" in their tags
            // Since we can't read tags from C#, we rely on the name list above
            // plus any shader that has instancing_options procedural
            // Heuristic: if the shader name starts with "CleanRender/"
            return shader.name.StartsWith("CleanRender/");
        }

        private void ScanAndSetup()
        {
            if (cullingShader == null)
            {
                EditorUtility.DisplayDialog("Error",
                    "Assign the ImprovedStaticCulling compute shader first!", "OK");
                return;
            }

            Undo.SetCurrentGroupName("Setup Static Instancing");
            int undoGroup = Undo.GetCurrentGroup();

            EditorUtility.DisplayProgressBar("Static Instance Setup", "Scanning scene...", 0f);

            var groups = GatherInstances();
            var log = new System.Text.StringBuilder();
            int totalInstances = 0;
            int totalGroups = 0;

            // Find or create container
            var container = GameObject.Find(containerName);
            if (container == null)
            {
                container = new GameObject(containerName);
                Undo.RegisterCreatedObjectUndo(container, "Create Instance Container");
            }

            float progress = 0;
            float step = 1f / Mathf.Max(groups.Count, 1);

            foreach (var kvp in groups)
            {
                var key = kvp.Key;
                var instances = kvp.Value;

                progress += step;
                EditorUtility.DisplayProgressBar("Static Instance Setup",
                    $"Processing: {key.mesh.name} ({instances.Count} instances)", progress);

                if (instances.Count < minInstancesForIndirect)
                {
                    log.AppendLine($"  SKIP: {key.mesh.name} + {key.material.name} " +
                        $"({instances.Count} instances < {minInstancesForIndirect} min)");
                    continue;
                }

                // Create manager GameObject
                string managerName = $"InstanceGroup_{key.mesh.name}_{key.material.name}";
                var managerGO = new GameObject(managerName);
                managerGO.transform.SetParent(container.transform);
                Undo.RegisterCreatedObjectUndo(managerGO, "Create Instance Manager");

                // Add StaticInstanceManager
                var manager = managerGO.AddComponent<StaticInstanceManager>();
                manager.instanceMesh = key.mesh;
                manager.instanceMaterial = key.material;
                manager.cullingShader = cullingShader;
                manager.cullDistance = defaultCullDistance;
                manager.shadowDistance = defaultShadowDistance;

                // Build instance data
                var dataList = new StaticInstanceManager.SerializedInstanceData[instances.Count];
                for (int i = 0; i < instances.Count; i++)
                {
                    var inst = instances[i];
                    dataList[i] = new StaticInstanceManager.SerializedInstanceData
                    {
                        position = inst.position,
                        rotation = new Vector4(inst.rotation.x, inst.rotation.y, inst.rotation.z, inst.rotation.w),
                        scale = inst.scale
                    };
                }
                manager.instanceData = dataList;

                // Track source GameObjects for disable/re-enable
                manager.sourceObjects = instances.Select(i => i.gameObject).ToArray();

                // Disable original renderers
                if (disableOriginalRenderers)
                {
                    foreach (var inst in instances)
                    {
                        if (inst.renderer != null)
                        {
                            Undo.RecordObject(inst.renderer, "Disable Renderer");
                            inst.renderer.enabled = false;
                        }
                    }
                }

                EditorUtility.SetDirty(managerGO);

                totalGroups++;
                totalInstances += instances.Count;

                log.AppendLine($"  ✓ {key.mesh.name} + {key.material.name}: " +
                    $"{instances.Count} instances → GPU Indirect Draw");
            }

            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.ClearProgressBar();

            lastGroupCount = totalGroups;
            lastInstanceCount = totalInstances;
            lastLog = $"═══ SETUP COMPLETE ═══\n" +
                $"Groups created: {totalGroups}\n" +
                $"Total instances: {totalInstances}\n" +
                $"Skipped (already setup): {lastSkippedCount}\n" +
                $"Estimated draw calls: {totalGroups} (down from {totalInstances})\n\n" +
                $"── Details ──\n{log}";

            Debug.Log($"[CleanRender] Static Instance Setup: {totalGroups} groups, " +
                $"{totalInstances} instances configured for GPU Indirect Draw.");
        }

        private void DryRun()
        {
            var groups = GatherInstances();
            var log = new System.Text.StringBuilder();
            int totalInstances = 0;
            int totalGroups = 0;

            foreach (var kvp in groups)
            {
                var instances = kvp.Value;
                if (instances.Count < minInstancesForIndirect)
                {
                    log.AppendLine($"  SKIP: {kvp.Key.mesh.name} + {kvp.Key.material.name} " +
                        $"({instances.Count} < {minInstancesForIndirect})");
                    continue;
                }

                totalGroups++;
                totalInstances += instances.Count;
                log.AppendLine($"  → {kvp.Key.mesh.name} + {kvp.Key.material.name}: " +
                    $"{instances.Count} instances");
            }

            lastLog = $"═══ DRY RUN (no changes made) ═══\n" +
                $"Would create: {totalGroups} groups\n" +
                $"Total instances: {totalInstances}\n" +
                $"Skipped: {lastSkippedCount}\n" +
                $"Estimated draw call reduction: {totalInstances} → {totalGroups}\n\n" +
                $"── Groups ──\n{log}";
        }

        private void RemoveAll()
        {
            var container = GameObject.Find(containerName);
            if (container == null)
            {
                EditorUtility.DisplayDialog("Nothing to Remove",
                    $"No '{containerName}' found in scene.", "OK");
                return;
            }

            // Re-enable source renderers
            var managers = container.GetComponentsInChildren<StaticInstanceManager>();
            foreach (var mgr in managers)
            {
                if (mgr.sourceObjects == null) continue;
                foreach (var go in mgr.sourceObjects)
                {
                    if (go == null) continue;
                    var renderer = go.GetComponent<MeshRenderer>();
                    if (renderer != null)
                    {
                        Undo.RecordObject(renderer, "Re-enable Renderer");
                        renderer.enabled = true;
                    }
                }
            }

            Undo.DestroyObjectImmediate(container);

            lastLog = $"═══ REMOVED ═══\n" +
                $"Destroyed {managers.Length} instance managers.\n" +
                $"Re-enabled original renderers.";

            Debug.Log("[CleanRender] All static instance managers removed.");
        }
    }
}
#endif
