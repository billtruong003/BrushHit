#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// ============================================================================
// SCENE MATERIAL & INSTANCE ANALYZER - Modern GPU Rendering Pipeline Advisor
// ============================================================================
// Mục đích: Phân tích toàn bộ scene để xác định hướng render tối ưu
// - Check toàn bộ Material, Shader, Texture
// - Đếm Mesh Instance và phân loại theo batch khả thi
// - Đề xuất GPU Instancing / SRP Batcher / Indirect Draw
// - Giảm phụ thuộc CPU, tối ưu Draw Call cho scene lớn
// ============================================================================

public class SceneMaterialAnalyzer : EditorWindow
{
    // ── Data Models ──────────────────────────────────────────────────────
    
    private class MeshInstanceData
    {
        public Mesh mesh;
        public string meshName;
        public int instanceCount;
        public int vertexCount;
        public int triangleCount;
        public int subMeshCount;
        public long totalVerticesInScene;   // instanceCount * vertexCount
        public long totalTrianglesInScene;  // instanceCount * triangleCount
        public List<Material> usedMaterials = new List<Material>();
        public List<GameObject> gameObjects = new List<GameObject>();
        public bool isGPUInstancingReady;
        public bool isSRPBatcherCompatible;
        public bool isStaticBatched;
        public bool hasLODGroup;
        public int lodLevels;
        public BatchStrategy recommendedStrategy;
    }

    private class MaterialData
    {
        public Material material;
        public string materialName;
        public Shader shader;
        public string shaderName;
        public int usageCount;
        public bool enableGPUInstancing;
        public bool srpBatcherCompatible;
        public int textureCount;
        public long estimatedVRAM; // bytes
        public int passCount;
        public List<string> keywords = new List<string>();
        public List<TextureInfo> textures = new List<TextureInfo>();
        public RenderQueue renderQueue;
        public bool isTransparent;
        public List<GameObject> usedBy = new List<GameObject>();
    }

    private class TextureInfo
    {
        public Texture texture;
        public string propertyName;
        public string textureName;
        public int width, height;
        public TextureFormat format;
        public long estimatedSize;
        public bool isMipMapped;
        public FilterMode filterMode;
    }

    private class ShaderData
    {
        public Shader shader;
        public string shaderName;
        public int materialCount;
        public int passCount;
        public bool supportsInstancing;
        public bool srpBatcherCompatible;
        public List<Material> materials = new List<Material>();
    }

    private enum BatchStrategy
    {
        GPUInstancing,          // Cùng mesh + cùng material → GPU Instance
        SRPBatcher,             // Cùng shader variant → SRP Batcher
        IndirectDraw,           // Số lượng cực lớn → DrawMeshInstancedIndirect
        StaticBatching,         // Tĩnh, ít thay đổi → Static Batch
        DynamicBatching,        // Mesh nhỏ < 300 vert
        ManualCombine,          // Combine mesh thủ công
        LODCrossfade,           // LOD + Instancing hybrid
        None
    }

    private enum RenderQueue
    {
        Background,
        Geometry,
        AlphaTest,
        Transparent,
        Overlay,
        Unknown
    }

    // ── Analysis Results ─────────────────────────────────────────────────

    private List<MeshInstanceData> meshInstances = new List<MeshInstanceData>();
    private List<MaterialData> materialDataList = new List<MaterialData>();
    private Dictionary<Shader, ShaderData> shaderDataMap = new Dictionary<Shader, ShaderData>();

    private int totalGameObjects;
    private int totalRenderers;
    private int totalMeshFilters;
    private int totalSkinnedMeshes;
    private int totalUniqueMeshes;
    private int totalUniqueMaterials;
    private int totalUniqueShaders;
    private int totalUniqueTextures;
    private long totalEstimatedVRAM;
    private long totalVerticesInScene;
    private long totalTrianglesInScene;
    private int estimatedDrawCalls;
    private int estimatedDrawCallsOptimized;
    private int totalLODGroups;
    private int gpuInstancingReadyCount;
    private int srpBatcherReadyCount;
    private int staticBatchedCount;

    // ── UI State ─────────────────────────────────────────────────────────

    private Vector2 scrollPosition;
    private int selectedTab = 0;
    private readonly string[] tabNames = new string[]
    {
        "Overview", "Mesh Instances", "Materials", "Shaders",
        "Draw Call Map", "Optimization Report"
    };

    private bool sortByInstanceCount = true;
    private bool sortDescending = true;
    private string searchFilter = "";
    private bool showOnlyProblems = false;
    private bool hasAnalyzed = false;

    // Foldouts
    private Dictionary<string, bool> foldouts = new Dictionary<string, bool>();

    // Colors
    private static readonly Color COLOR_GOOD = new Color(0.3f, 0.85f, 0.4f);
    private static readonly Color COLOR_WARN = new Color(1f, 0.8f, 0.2f);
    private static readonly Color COLOR_BAD = new Color(1f, 0.35f, 0.3f);
    private static readonly Color COLOR_INFO = new Color(0.4f, 0.7f, 1f);
    private static readonly Color COLOR_HEADER = new Color(0.18f, 0.18f, 0.22f);
    private static readonly Color COLOR_ROW_ALT = new Color(0.22f, 0.22f, 0.26f);
    private static readonly Color COLOR_SECTION = new Color(0.15f, 0.15f, 0.19f);

    // ── Window Setup ─────────────────────────────────────────────────────

    [MenuItem("Tools/Scene Material & Instance Analyzer %#m")]
    public static void ShowWindow()
    {
        var window = GetWindow<SceneMaterialAnalyzer>("Scene Analyzer");
        window.minSize = new Vector2(750, 500);
    }

    // ── Main Analysis ────────────────────────────────────────────────────

    private void AnalyzeScene()
    {
        EditorUtility.DisplayProgressBar("Analyzing Scene", "Gathering renderers...", 0f);

        meshInstances.Clear();
        materialDataList.Clear();
        shaderDataMap.Clear();
        foldouts.Clear();

        var allRenderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        var meshFilterMap = new Dictionary<Mesh, MeshInstanceData>();
        var materialMap = new Dictionary<Material, MaterialData>();
        var textureSet = new HashSet<Texture>();

        totalGameObjects = FindObjectsByType<GameObject>(FindObjectsSortMode.None).Length;
        totalRenderers = allRenderers.Length;
        totalMeshFilters = FindObjectsByType<MeshFilter>(FindObjectsSortMode.None).Length;
        totalSkinnedMeshes = FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None).Length;
        totalLODGroups = FindObjectsByType<LODGroup>(FindObjectsSortMode.None).Length;
        totalVerticesInScene = 0;
        totalTrianglesInScene = 0;
        estimatedDrawCalls = 0;
        staticBatchedCount = 0;
        gpuInstancingReadyCount = 0;
        srpBatcherReadyCount = 0;

        float progress = 0;
        float step = 1f / Mathf.Max(allRenderers.Length, 1);

        foreach (var renderer in allRenderers)
        {
            progress += step;
            if (progress % 0.1f < step)
                EditorUtility.DisplayProgressBar("Analyzing Scene",
                    $"Processing: {renderer.gameObject.name}", progress * 0.7f);

            Mesh mesh = null;
            var meshFilter = renderer.GetComponent<MeshFilter>();
            var skinnedMesh = renderer as SkinnedMeshRenderer;

            if (meshFilter != null && meshFilter.sharedMesh != null)
                mesh = meshFilter.sharedMesh;
            else if (skinnedMesh != null && skinnedMesh.sharedMesh != null)
                mesh = skinnedMesh.sharedMesh;

            if (mesh == null) continue;

            // ── Mesh Instance Data ──
            if (!meshFilterMap.ContainsKey(mesh))
            {
                var mid = new MeshInstanceData
                {
                    mesh = mesh,
                    meshName = mesh.name,
                    instanceCount = 0,
                    vertexCount = mesh.vertexCount,
                    triangleCount = mesh.triangles.Length / 3,
                    subMeshCount = mesh.subMeshCount
                };
                meshFilterMap[mesh] = mid;
            }

            var meshData = meshFilterMap[mesh];
            meshData.instanceCount++;
            meshData.gameObjects.Add(renderer.gameObject);

            bool isStatic = renderer.gameObject.isStatic ||
                            GameObjectUtility.GetStaticEditorFlags(renderer.gameObject)
                                .HasFlag(StaticEditorFlags.BatchingStatic);
            if (isStatic) meshData.isStaticBatched = true;

            var lodGroup = renderer.GetComponentInParent<LODGroup>();
            if (lodGroup != null)
            {
                meshData.hasLODGroup = true;
                meshData.lodLevels = lodGroup.lodCount;
            }

            // ── Material Data ──
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat == null) continue;

                estimatedDrawCalls++;

                if (!materialMap.ContainsKey(mat))
                {
                    var md = new MaterialData
                    {
                        material = mat,
                        materialName = mat.name,
                        shader = mat.shader,
                        shaderName = mat.shader != null ? mat.shader.name : "NULL",
                        usageCount = 0,
                        enableGPUInstancing = mat.enableInstancing,
                        renderQueue = ClassifyRenderQueue(mat.renderQueue),
                        isTransparent = mat.renderQueue >= 2500
                    };

                    // Shader info
                    if (mat.shader != null)
                    {
                        md.passCount = mat.shader.passCount;
                        md.srpBatcherCompatible = IsSRPBatcherCompatible(mat);
                        md.keywords = mat.shaderKeywords.ToList();

                        if (!shaderDataMap.ContainsKey(mat.shader))
                        {
                            shaderDataMap[mat.shader] = new ShaderData
                            {
                                shader = mat.shader,
                                shaderName = mat.shader.name,
                                passCount = mat.shader.passCount,
                                supportsInstancing = mat.enableInstancing,
                                srpBatcherCompatible = md.srpBatcherCompatible
                            };
                        }
                        shaderDataMap[mat.shader].materials.Add(mat);
                    }

                    // Textures
                    var texPropIds = mat.GetTexturePropertyNameIDs();
                    var texPropNames = mat.GetTexturePropertyNames();
                    for (int i = 0; i < texPropIds.Length; i++)
                    {
                        var tex = mat.GetTexture(texPropIds[i]);
                        if (tex != null)
                        {
                            textureSet.Add(tex);
                            var texInfo = new TextureInfo
                            {
                                texture = tex,
                                propertyName = texPropNames[i],
                                textureName = tex.name,
                                width = tex.width,
                                height = tex.height,
                                filterMode = tex.filterMode
                            };

                            if (tex is Texture2D tex2d)
                            {
                                texInfo.format = tex2d.format;
                                texInfo.isMipMapped = tex2d.mipmapCount > 1;
                                texInfo.estimatedSize = EstimateTextureSize(tex2d);
                            }

                            md.textures.Add(texInfo);
                            md.estimatedVRAM += texInfo.estimatedSize;
                        }
                    }
                    md.textureCount = md.textures.Count;
                    materialMap[mat] = md;
                }

                var matData = materialMap[mat];
                matData.usageCount++;
                matData.usedBy.Add(renderer.gameObject);

                if (!meshData.usedMaterials.Contains(mat))
                    meshData.usedMaterials.Add(mat);
            }
        }

        EditorUtility.DisplayProgressBar("Analyzing Scene", "Computing strategies...", 0.8f);

        // ── Finalize Mesh Data ──
        foreach (var kvp in meshFilterMap)
        {
            var md = kvp.Value;
            md.totalVerticesInScene = (long)md.instanceCount * md.vertexCount;
            md.totalTrianglesInScene = (long)md.instanceCount * md.triangleCount;
            totalVerticesInScene += md.totalVerticesInScene;
            totalTrianglesInScene += md.totalTrianglesInScene;

            md.isGPUInstancingReady = md.usedMaterials.All(m => m != null && m.enableInstancing);
            md.isSRPBatcherCompatible = md.usedMaterials.All(m => IsSRPBatcherCompatible(m));
            md.recommendedStrategy = RecommendStrategy(md);

            if (md.isGPUInstancingReady) gpuInstancingReadyCount++;
            if (md.isSRPBatcherCompatible) srpBatcherReadyCount++;
            if (md.isStaticBatched) staticBatchedCount++;
        }

        meshInstances = meshFilterMap.Values.ToList();
        materialDataList = materialMap.Values.ToList();

        totalUniqueMeshes = meshInstances.Count;
        totalUniqueMaterials = materialDataList.Count;
        totalUniqueShaders = shaderDataMap.Count;
        totalUniqueTextures = textureSet.Count;
        totalEstimatedVRAM = materialDataList.Sum(m => m.estimatedVRAM);

        // Shader material counts
        foreach (var kvp in shaderDataMap)
            kvp.Value.materialCount = kvp.Value.materials.Distinct().Count();

        // Estimate optimized draw calls
        estimatedDrawCallsOptimized = EstimateOptimizedDrawCalls();

        hasAnalyzed = true;
        EditorUtility.ClearProgressBar();
    }

    // ── Strategy Recommendation Engine ───────────────────────────────────

    private BatchStrategy RecommendStrategy(MeshInstanceData data)
    {
        // Số lượng cực lớn (> 500 instances) → Indirect Draw (GPU driven)
        if (data.instanceCount > 500)
            return BatchStrategy.IndirectDraw;

        // Nhiều instance (> 10) + cùng material → GPU Instancing
        if (data.instanceCount > 10 && data.usedMaterials.Count <= 2)
            return BatchStrategy.GPUInstancing;

        // Có LOD → LOD + Instancing hybrid
        if (data.hasLODGroup && data.instanceCount > 20)
            return BatchStrategy.LODCrossfade;

        // Mesh nhỏ < 300 vertices → Dynamic Batching
        if (data.vertexCount < 300 && data.instanceCount > 1)
            return BatchStrategy.DynamicBatching;

        // Static object → Static Batching hoặc SRP Batcher
        if (data.isStaticBatched)
        {
            if (data.isSRPBatcherCompatible)
                return BatchStrategy.SRPBatcher;
            return BatchStrategy.StaticBatching;
        }

        // Ít instance nhưng nhiều material variant → SRP Batcher
        if (data.isSRPBatcherCompatible)
            return BatchStrategy.SRPBatcher;

        // Instance trung bình → GPU Instancing
        if (data.instanceCount > 3)
            return BatchStrategy.GPUInstancing;

        return BatchStrategy.None;
    }

    private int EstimateOptimizedDrawCalls()
    {
        int optimized = 0;
        foreach (var md in meshInstances)
        {
            switch (md.recommendedStrategy)
            {
                case BatchStrategy.GPUInstancing:
                    // 1 draw call per material per mesh (instanced)
                    optimized += md.usedMaterials.Count;
                    break;
                case BatchStrategy.IndirectDraw:
                    // 1 draw call per mesh type (indirect)
                    optimized += 1;
                    break;
                case BatchStrategy.SRPBatcher:
                    // SRP Batcher groups by shader variant
                    optimized += md.usedMaterials.Select(m => m.shader).Distinct().Count();
                    break;
                case BatchStrategy.StaticBatching:
                    // Batched into fewer calls
                    optimized += Mathf.CeilToInt(md.instanceCount / 32f) * md.usedMaterials.Count;
                    break;
                case BatchStrategy.DynamicBatching:
                    optimized += Mathf.CeilToInt(md.instanceCount / 16f);
                    break;
                case BatchStrategy.LODCrossfade:
                    optimized += md.usedMaterials.Count * md.lodLevels;
                    break;
                default:
                    optimized += md.instanceCount * md.usedMaterials.Count;
                    break;
            }
        }
        return optimized;
    }

    // ── Helper Functions ─────────────────────────────────────────────────

    private bool IsSRPBatcherCompatible(Material mat)
    {
        if (mat == null || mat.shader == null) return false;
        // Heuristic: URP/HDRP Lit shaders thường compatible
        string sn = mat.shader.name.ToLower();
        return sn.Contains("universal") || sn.Contains("urp") ||
               sn.Contains("hdrp") || sn.Contains("lit") ||
               sn.Contains("shader graph");
    }

    private RenderQueue ClassifyRenderQueue(int queue)
    {
        if (queue < 1500) return RenderQueue.Background;
        if (queue < 2400) return RenderQueue.Geometry;
        if (queue < 2500) return RenderQueue.AlphaTest;
        if (queue < 3500) return RenderQueue.Transparent;
        if (queue < 5000) return RenderQueue.Overlay;
        return RenderQueue.Unknown;
    }

    private long EstimateTextureSize(Texture2D tex)
    {
        int bpp = 8; // default
        switch (tex.format)
        {
            case TextureFormat.RGBA32: bpp = 32; break;
            case TextureFormat.RGB24: bpp = 24; break;
            case TextureFormat.ARGB32: bpp = 32; break;
            case TextureFormat.DXT1: bpp = 4; break;
            case TextureFormat.DXT5: bpp = 8; break;
            case TextureFormat.BC7: bpp = 8; break;
            case TextureFormat.ASTC_4x4: bpp = 8; break;
            case TextureFormat.ASTC_6x6: bpp = 4; break;
            case TextureFormat.ETC2_RGB: bpp = 4; break;
            case TextureFormat.ETC2_RGBA8: bpp = 8; break;
            case TextureFormat.R8: bpp = 8; break;
            case TextureFormat.R16: bpp = 16; break;
            case TextureFormat.RGBAHalf: bpp = 64; break;
            case TextureFormat.RGBAFloat: bpp = 128; break;
        }
        long baseSize = (long)tex.width * tex.height * bpp / 8;
        // Mipmap adds ~33%
        return tex.mipmapCount > 1 ? (long)(baseSize * 1.33f) : baseSize;
    }

    private string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024f * 1024f):F1} MB";
        return $"{bytes / (1024f * 1024f * 1024f):F2} GB";
    }

    private string FormatNumber(long num)
    {
        if (num < 1000) return num.ToString();
        if (num < 1000000) return $"{num / 1000f:F1}K";
        return $"{num / 1000000f:F2}M";
    }

    private Color GetStrategyColor(BatchStrategy strategy)
    {
        switch (strategy)
        {
            case BatchStrategy.GPUInstancing: return COLOR_GOOD;
            case BatchStrategy.IndirectDraw: return new Color(0.5f, 0.9f, 1f);
            case BatchStrategy.SRPBatcher: return new Color(0.6f, 0.8f, 0.5f);
            case BatchStrategy.LODCrossfade: return COLOR_INFO;
            case BatchStrategy.StaticBatching: return COLOR_WARN;
            case BatchStrategy.DynamicBatching: return COLOR_WARN;
            case BatchStrategy.ManualCombine: return new Color(1f, 0.6f, 0.3f);
            case BatchStrategy.None: return COLOR_BAD;
            default: return Color.gray;
        }
    }

    private string GetStrategyLabel(BatchStrategy strategy)
    {
        switch (strategy)
        {
            case BatchStrategy.GPUInstancing: return "GPU INSTANCING";
            case BatchStrategy.IndirectDraw: return "INDIRECT DRAW (GPU DRIVEN)";
            case BatchStrategy.SRPBatcher: return "SRP BATCHER";
            case BatchStrategy.LODCrossfade: return "LOD + INSTANCING";
            case BatchStrategy.StaticBatching: return "STATIC BATCHING";
            case BatchStrategy.DynamicBatching: return "DYNAMIC BATCHING";
            case BatchStrategy.ManualCombine: return "MANUAL COMBINE";
            case BatchStrategy.None: return "NO OPTIMIZATION";
            default: return "UNKNOWN";
        }
    }

    // ── GUI Drawing ──────────────────────────────────────────────────────

    private void OnGUI()
    {
        DrawHeader();

        if (!hasAnalyzed)
        {
            DrawWelcomeScreen();
            return;
        }

        selectedTab = GUILayout.Toolbar(selectedTab, tabNames, GUILayout.Height(28));
        EditorGUILayout.Space(4);

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        switch (selectedTab)
        {
            case 0: DrawOverviewTab(); break;
            case 1: DrawMeshInstancesTab(); break;
            case 2: DrawMaterialsTab(); break;
            case 3: DrawShadersTab(); break;
            case 4: DrawDrawCallMapTab(); break;
            case 5: DrawOptimizationReportTab(); break;
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawHeader()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        GUILayout.Label("SCENE MATERIAL & INSTANCE ANALYZER", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();

        if (GUILayout.Button("Analyze Scene", EditorStyles.toolbarButton, GUILayout.Width(110)))
            AnalyzeScene();

        if (hasAnalyzed && GUILayout.Button("Export Report", EditorStyles.toolbarButton, GUILayout.Width(100)))
            ExportReport();

        EditorGUILayout.EndHorizontal();
    }

    private void DrawWelcomeScreen()
    {
        GUILayout.FlexibleSpace();
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        EditorGUILayout.BeginVertical(GUILayout.Width(500));

        var style = new GUIStyle(EditorStyles.label)
        {
            fontSize = 16, alignment = TextAnchor.MiddleCenter, wordWrap = true
        };
        GUILayout.Label("Scene Material & Instance Analyzer", style);
        EditorGUILayout.Space(10);

        var descStyle = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleCenter, wordWrap = true
        };
        GUILayout.Label(
            "Phân tích toàn bộ Material, Mesh Instance, Shader trong scene.\n" +
            "Xác định hướng render tối ưu: GPU Instancing, SRP Batcher, Indirect Draw.\n" +
            "Giảm Draw Call, giảm phụ thuộc CPU, tối ưu cho scene lớn.",
            descStyle);

        EditorGUILayout.Space(20);

        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("  ANALYZE SCENE  ", GUILayout.Height(36), GUILayout.Width(200)))
            AnalyzeScene();
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndVertical();
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
        GUILayout.FlexibleSpace();
    }

    // ── TAB 0: Overview ──────────────────────────────────────────────────

    private void DrawOverviewTab()
    {
        DrawSectionHeader("SCENE STATISTICS");

        EditorGUILayout.BeginHorizontal();

        // Column 1: Objects
        EditorGUILayout.BeginVertical("box", GUILayout.Width(240));
        GUILayout.Label("Objects", EditorStyles.boldLabel);
        DrawStatRow("GameObjects", totalGameObjects.ToString());
        DrawStatRow("Renderers", totalRenderers.ToString());
        DrawStatRow("MeshFilters", totalMeshFilters.ToString());
        DrawStatRow("SkinnedMeshes", totalSkinnedMeshes.ToString());
        DrawStatRow("LOD Groups", totalLODGroups.ToString());
        EditorGUILayout.EndVertical();

        // Column 2: Assets
        EditorGUILayout.BeginVertical("box", GUILayout.Width(240));
        GUILayout.Label("Unique Assets", EditorStyles.boldLabel);
        DrawStatRow("Meshes", totalUniqueMeshes.ToString());
        DrawStatRow("Materials", totalUniqueMaterials.ToString());
        DrawStatRow("Shaders", totalUniqueShaders.ToString());
        DrawStatRow("Textures", totalUniqueTextures.ToString());
        DrawStatRow("Est. VRAM", FormatBytes(totalEstimatedVRAM));
        EditorGUILayout.EndVertical();

        // Column 3: Geometry
        EditorGUILayout.BeginVertical("box", GUILayout.Width(240));
        GUILayout.Label("Geometry", EditorStyles.boldLabel);
        DrawStatRow("Total Vertices", FormatNumber(totalVerticesInScene));
        DrawStatRow("Total Triangles", FormatNumber(totalTrianglesInScene));
        DrawStatRowColored("Est. Draw Calls", estimatedDrawCalls.ToString(),
            estimatedDrawCalls > 2000 ? COLOR_BAD : estimatedDrawCalls > 500 ? COLOR_WARN : COLOR_GOOD);
        DrawStatRowColored("Optimized Draw Calls", estimatedDrawCallsOptimized.ToString(),
            estimatedDrawCallsOptimized > 1000 ? COLOR_BAD : estimatedDrawCallsOptimized > 300 ? COLOR_WARN : COLOR_GOOD);
        DrawStatRow("Reduction", $"{(1f - (float)estimatedDrawCallsOptimized / Mathf.Max(estimatedDrawCalls, 1)) * 100f:F0}%");
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(10);

        // ── Render Readiness ──
        DrawSectionHeader("RENDER PIPELINE READINESS");

        EditorGUILayout.BeginHorizontal();
        DrawReadinessBox("GPU Instancing Ready", gpuInstancingReadyCount, totalUniqueMeshes, COLOR_GOOD);
        DrawReadinessBox("SRP Batcher Compatible", srpBatcherReadyCount, totalUniqueMeshes, COLOR_INFO);
        DrawReadinessBox("Static Batched", staticBatchedCount, totalUniqueMeshes, COLOR_WARN);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(10);

        // ── Strategy Distribution ──
        DrawSectionHeader("RECOMMENDED STRATEGY DISTRIBUTION");

        var strategyGroups = meshInstances
            .GroupBy(m => m.recommendedStrategy)
            .OrderByDescending(g => g.Sum(m => m.instanceCount))
            .ToList();

        foreach (var group in strategyGroups)
        {
            int totalInstances = group.Sum(m => m.instanceCount);
            int meshCount = group.Count();
            float pct = (float)totalInstances / Mathf.Max(totalRenderers, 1);

            EditorGUILayout.BeginHorizontal();
            var prevColor = GUI.color;
            GUI.color = GetStrategyColor(group.Key);
            GUILayout.Label("■", GUILayout.Width(14));
            GUI.color = prevColor;

            GUILayout.Label(GetStrategyLabel(group.Key), GUILayout.Width(200));
            GUILayout.Label($"{meshCount} meshes, {totalInstances} instances", GUILayout.Width(200));

            // Progress bar
            var rect = GUILayoutUtility.GetRect(200, 18);
            EditorGUI.ProgressBar(rect, pct, $"{pct * 100:F0}%");
            EditorGUILayout.EndHorizontal();
        }
    }

    // ── TAB 1: Mesh Instances ────────────────────────────────────────────

    private void DrawMeshInstancesTab()
    {
        DrawSectionHeader("MESH INSTANCE ANALYSIS");

        EditorGUILayout.BeginHorizontal();
        searchFilter = EditorGUILayout.TextField("Search", searchFilter, GUILayout.Width(300));
        GUILayout.Space(20);
        showOnlyProblems = EditorGUILayout.Toggle("Show Problems Only", showOnlyProblems);
        GUILayout.FlexibleSpace();

        if (GUILayout.Button(sortDescending ? "▼ Instances" : "▲ Instances", GUILayout.Width(100)))
        {
            if (sortByInstanceCount)
                sortDescending = !sortDescending;
            sortByInstanceCount = true;
        }
        if (GUILayout.Button("▼ Triangles", GUILayout.Width(100)))
        {
            sortByInstanceCount = false;
            sortDescending = true;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        // Header
        EditorGUILayout.BeginHorizontal("box");
        GUILayout.Label("Mesh", EditorStyles.miniLabel, GUILayout.Width(200));
        GUILayout.Label("Instances", EditorStyles.miniLabel, GUILayout.Width(70));
        GUILayout.Label("Verts", EditorStyles.miniLabel, GUILayout.Width(70));
        GUILayout.Label("Tris", EditorStyles.miniLabel, GUILayout.Width(70));
        GUILayout.Label("Total Tris", EditorStyles.miniLabel, GUILayout.Width(80));
        GUILayout.Label("Materials", EditorStyles.miniLabel, GUILayout.Width(70));
        GUILayout.Label("Strategy", EditorStyles.miniLabel, GUILayout.Width(180));
        GUILayout.Label("Status", EditorStyles.miniLabel, GUILayout.Width(60));
        EditorGUILayout.EndHorizontal();

        var sortedList = sortByInstanceCount
            ? (sortDescending
                ? meshInstances.OrderByDescending(m => m.instanceCount)
                : meshInstances.OrderBy(m => m.instanceCount))
            : meshInstances.OrderByDescending(m => m.totalTrianglesInScene);

        var filtered = sortedList.Where(m =>
        {
            if (!string.IsNullOrEmpty(searchFilter) &&
                !m.meshName.ToLower().Contains(searchFilter.ToLower()))
                return false;
            if (showOnlyProblems && m.recommendedStrategy != BatchStrategy.None &&
                m.isGPUInstancingReady)
                return false;
            return true;
        });

        int row = 0;
        foreach (var md in filtered)
        {
            var bgColor = row % 2 == 0 ? COLOR_ROW_ALT : Color.clear;
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = bgColor;

            EditorGUILayout.BeginHorizontal("box");
            GUI.backgroundColor = prevBg;

            // Mesh name
            if (GUILayout.Button(md.meshName, EditorStyles.linkLabel, GUILayout.Width(200)))
            {
                if (md.gameObjects.Count > 0)
                    Selection.activeGameObject = md.gameObjects[0];
            }

            // Stats
            var countColor = md.instanceCount > 100 ? COLOR_WARN :
                             md.instanceCount > 500 ? COLOR_BAD : Color.white;
            DrawColoredLabel(md.instanceCount.ToString(), countColor, 70);
            GUILayout.Label(FormatNumber(md.vertexCount), GUILayout.Width(70));
            GUILayout.Label(FormatNumber(md.triangleCount), GUILayout.Width(70));

            var triColor = md.totalTrianglesInScene > 1000000 ? COLOR_BAD :
                           md.totalTrianglesInScene > 100000 ? COLOR_WARN : Color.white;
            DrawColoredLabel(FormatNumber(md.totalTrianglesInScene), triColor, 80);

            GUILayout.Label(md.usedMaterials.Count.ToString(), GUILayout.Width(70));

            // Strategy
            var stratColor = GetStrategyColor(md.recommendedStrategy);
            DrawColoredLabel(GetStrategyLabel(md.recommendedStrategy), stratColor, 180);

            // Status icons
            string status = "";
            if (md.isGPUInstancingReady) status += "✓I ";
            else status += "✗I ";
            if (md.isSRPBatcherCompatible) status += "✓S ";
            if (md.hasLODGroup) status += $"L{md.lodLevels}";

            GUILayout.Label(status, GUILayout.Width(60));

            EditorGUILayout.EndHorizontal();

            // Expandable detail
            string foldKey = $"mesh_{md.meshName}_{md.mesh.GetInstanceID()}";
            if (!foldouts.ContainsKey(foldKey)) foldouts[foldKey] = false;

            if (Event.current.type == EventType.MouseDown &&
                GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition) &&
                Event.current.button == 1)
            {
                foldouts[foldKey] = !foldouts[foldKey];
                Event.current.Use();
                Repaint();
            }

            if (foldouts.ContainsKey(foldKey) && foldouts[foldKey])
            {
                DrawMeshDetail(md);
            }

            row++;
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.HelpBox("Right-click a row to expand details. Click mesh name to select in scene.", MessageType.Info);
    }

    private void DrawMeshDetail(MeshInstanceData md)
    {
        EditorGUI.indentLevel += 2;
        EditorGUILayout.BeginVertical("helpbox");

        EditorGUILayout.LabelField("SubMeshes", md.subMeshCount.ToString());
        EditorGUILayout.LabelField("GPU Instancing Ready", md.isGPUInstancingReady ? "YES" : "NO");
        EditorGUILayout.LabelField("SRP Batcher Compatible", md.isSRPBatcherCompatible ? "YES" : "NO");
        EditorGUILayout.LabelField("Has LOD", md.hasLODGroup ? $"YES ({md.lodLevels} levels)" : "NO");

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Materials:", EditorStyles.boldLabel);
        foreach (var mat in md.usedMaterials)
        {
            if (mat == null) continue;
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(30);
            EditorGUILayout.ObjectField(mat, typeof(Material), false, GUILayout.Width(300));
            GUILayout.Label($"Instancing: {(mat.enableInstancing ? "ON" : "OFF")}");
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField($"Instances ({md.instanceCount}):", EditorStyles.boldLabel);
        int shown = 0;
        foreach (var go in md.gameObjects)
        {
            if (shown >= 10) { GUILayout.Label($"  ... and {md.instanceCount - 10} more"); break; }
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(30);
            if (GUILayout.Button(go.name, EditorStyles.linkLabel, GUILayout.Width(300)))
                Selection.activeGameObject = go;
            EditorGUILayout.EndHorizontal();
            shown++;
        }

        EditorGUILayout.EndVertical();
        EditorGUI.indentLevel -= 2;
    }

    // ── TAB 2: Materials ─────────────────────────────────────────────────

    private void DrawMaterialsTab()
    {
        DrawSectionHeader("MATERIAL ANALYSIS");

        EditorGUILayout.BeginHorizontal();
        searchFilter = EditorGUILayout.TextField("Search", searchFilter, GUILayout.Width(300));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        // Header
        EditorGUILayout.BeginHorizontal("box");
        GUILayout.Label("Material", EditorStyles.miniLabel, GUILayout.Width(200));
        GUILayout.Label("Shader", EditorStyles.miniLabel, GUILayout.Width(200));
        GUILayout.Label("Used", EditorStyles.miniLabel, GUILayout.Width(50));
        GUILayout.Label("Queue", EditorStyles.miniLabel, GUILayout.Width(80));
        GUILayout.Label("Textures", EditorStyles.miniLabel, GUILayout.Width(60));
        GUILayout.Label("VRAM", EditorStyles.miniLabel, GUILayout.Width(70));
        GUILayout.Label("Instancing", EditorStyles.miniLabel, GUILayout.Width(70));
        GUILayout.Label("SRP", EditorStyles.miniLabel, GUILayout.Width(40));
        EditorGUILayout.EndHorizontal();

        var materials = materialDataList
            .OrderByDescending(m => m.usageCount)
            .Where(m => string.IsNullOrEmpty(searchFilter) ||
                        m.materialName.ToLower().Contains(searchFilter.ToLower()) ||
                        m.shaderName.ToLower().Contains(searchFilter.ToLower()));

        int row = 0;
        foreach (var md in materials)
        {
            EditorGUILayout.BeginHorizontal(row % 2 == 0 ? "box" : EditorStyles.helpBox);

            // Material (clickable)
            if (GUILayout.Button(md.materialName, EditorStyles.linkLabel, GUILayout.Width(200)))
                EditorGUIUtility.PingObject(md.material);

            GUILayout.Label(TruncateString(md.shaderName, 30), GUILayout.Width(200));
            GUILayout.Label(md.usageCount.ToString(), GUILayout.Width(50));

            var queueColor = md.isTransparent ? COLOR_WARN : Color.white;
            DrawColoredLabel(md.renderQueue.ToString(), queueColor, 80);

            GUILayout.Label(md.textureCount.ToString(), GUILayout.Width(60));
            GUILayout.Label(FormatBytes(md.estimatedVRAM), GUILayout.Width(70));

            DrawColoredLabel(md.enableGPUInstancing ? "ON" : "OFF",
                md.enableGPUInstancing ? COLOR_GOOD : COLOR_BAD, 70);

            DrawColoredLabel(md.srpBatcherCompatible ? "✓" : "✗",
                md.srpBatcherCompatible ? COLOR_GOOD : COLOR_BAD, 40);

            EditorGUILayout.EndHorizontal();

            // Expandable texture detail
            string foldKey = $"mat_{md.material.GetInstanceID()}";
            if (!foldouts.ContainsKey(foldKey)) foldouts[foldKey] = false;

            if (Event.current.type == EventType.MouseDown &&
                GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition) &&
                Event.current.button == 1)
            {
                foldouts[foldKey] = !foldouts[foldKey];
                Event.current.Use();
                Repaint();
            }

            if (foldouts.ContainsKey(foldKey) && foldouts[foldKey])
            {
                DrawMaterialDetail(md);
            }

            row++;
        }
    }

    private void DrawMaterialDetail(MaterialData md)
    {
        EditorGUI.indentLevel += 2;
        EditorGUILayout.BeginVertical("helpbox");

        EditorGUILayout.LabelField("Shader", md.shaderName);
        EditorGUILayout.LabelField("Pass Count", md.passCount.ToString());
        EditorGUILayout.LabelField("Render Queue", $"{md.renderQueue} ({md.material.renderQueue})");

        if (md.keywords.Count > 0)
        {
            EditorGUILayout.LabelField("Keywords:", EditorStyles.boldLabel);
            foreach (var kw in md.keywords)
                EditorGUILayout.LabelField($"  • {kw}");
        }

        if (md.textures.Count > 0)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Textures:", EditorStyles.boldLabel);
            foreach (var tex in md.textures)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(30);
                EditorGUILayout.ObjectField(tex.texture, typeof(Texture), false, GUILayout.Width(200));
                GUILayout.Label($"{tex.propertyName} | {tex.width}x{tex.height} | {tex.format} | {FormatBytes(tex.estimatedSize)}");
                EditorGUILayout.EndHorizontal();
            }
        }

        EditorGUILayout.EndVertical();
        EditorGUI.indentLevel -= 2;
    }

    // ── TAB 3: Shaders ───────────────────────────────────────────────────

    private void DrawShadersTab()
    {
        DrawSectionHeader("SHADER ANALYSIS");

        EditorGUILayout.BeginHorizontal("box");
        GUILayout.Label("Shader", EditorStyles.miniLabel, GUILayout.Width(300));
        GUILayout.Label("Materials", EditorStyles.miniLabel, GUILayout.Width(70));
        GUILayout.Label("Passes", EditorStyles.miniLabel, GUILayout.Width(60));
        GUILayout.Label("Instancing", EditorStyles.miniLabel, GUILayout.Width(80));
        GUILayout.Label("SRP Batcher", EditorStyles.miniLabel, GUILayout.Width(80));
        EditorGUILayout.EndHorizontal();

        int row = 0;
        foreach (var kvp in shaderDataMap.OrderByDescending(s => s.Value.materialCount))
        {
            var sd = kvp.Value;
            EditorGUILayout.BeginHorizontal(row % 2 == 0 ? "box" : EditorStyles.helpBox);

            if (GUILayout.Button(sd.shaderName, EditorStyles.linkLabel, GUILayout.Width(300)))
                EditorGUIUtility.PingObject(sd.shader);

            GUILayout.Label(sd.materialCount.ToString(), GUILayout.Width(70));

            var passColor = sd.passCount > 2 ? COLOR_WARN : Color.white;
            DrawColoredLabel(sd.passCount.ToString(), passColor, 60);

            DrawColoredLabel(sd.supportsInstancing ? "YES" : "NO",
                sd.supportsInstancing ? COLOR_GOOD : COLOR_BAD, 80);

            DrawColoredLabel(sd.srpBatcherCompatible ? "YES" : "NO",
                sd.srpBatcherCompatible ? COLOR_GOOD : COLOR_BAD, 80);

            EditorGUILayout.EndHorizontal();
            row++;
        }
    }

    // ── TAB 4: Draw Call Map ─────────────────────────────────────────────

    private void DrawDrawCallMapTab()
    {
        DrawSectionHeader("DRAW CALL FLOW MAP");

        EditorGUILayout.HelpBox(
            "Phân tích luồng Draw Call: Từ Mesh → Material → Shader → GPU\n" +
            "Mục tiêu: Giảm số lượng state change giữa CPU → GPU",
            MessageType.Info);

        EditorGUILayout.Space(8);

        // ── Render Queue Breakdown ──
        DrawSectionHeader("BY RENDER QUEUE");

        var queueGroups = materialDataList
            .GroupBy(m => m.renderQueue)
            .OrderBy(g => g.Key);

        foreach (var group in queueGroups)
        {
            int drawCalls = group.Sum(m => m.usageCount);
            float pct = (float)drawCalls / Mathf.Max(estimatedDrawCalls, 1);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(group.Key.ToString(), EditorStyles.boldLabel, GUILayout.Width(120));
            GUILayout.Label($"{group.Count()} materials, {drawCalls} draw calls", GUILayout.Width(250));

            var rect = GUILayoutUtility.GetRect(250, 18);
            EditorGUI.ProgressBar(rect, pct, $"{drawCalls} ({pct * 100:F0}%)");
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(12);

        // ── Bottleneck Analysis ──
        DrawSectionHeader("BOTTLENECK ANALYSIS");

        // Identify materials with most draw calls
        var topMaterials = materialDataList
            .OrderByDescending(m => m.usageCount)
            .Take(10);

        GUILayout.Label("Top 10 Materials by Draw Call Impact:", EditorStyles.boldLabel);
        foreach (var md in topMaterials)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(10);

            var color = md.usageCount > 50 ? COLOR_BAD :
                        md.usageCount > 20 ? COLOR_WARN : Color.white;
            DrawColoredLabel($"[{md.usageCount} calls]", color, 80);

            if (GUILayout.Button(md.materialName, EditorStyles.linkLabel, GUILayout.Width(200)))
                EditorGUIUtility.PingObject(md.material);

            GUILayout.Label($"Shader: {TruncateString(md.shaderName, 30)}");

            string fix = "";
            if (!md.enableGPUInstancing) fix += "Enable Instancing | ";
            if (md.isTransparent) fix += "Transparent (costly sort) | ";
            if (md.passCount > 2) fix += $"{md.passCount} passes (multi-pass) | ";
            if (!string.IsNullOrEmpty(fix))
            {
                GUI.color = COLOR_WARN;
                GUILayout.Label($"FIX: {fix.TrimEnd(' ', '|')}", GUILayout.Width(300));
                GUI.color = Color.white;
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(12);

        // ── Instance Batching Opportunity ──
        DrawSectionHeader("GPU INSTANCE BATCHING OPPORTUNITIES");

        var batchable = meshInstances
            .Where(m => m.instanceCount > 5 && !m.isGPUInstancingReady)
            .OrderByDescending(m => m.instanceCount);

        if (!batchable.Any())
        {
            EditorGUILayout.HelpBox("All high-instance meshes already have GPU Instancing enabled!", MessageType.Info);
        }
        else
        {
            foreach (var md in batchable)
            {
                EditorGUILayout.BeginHorizontal("box");
                GUI.color = COLOR_BAD;
                GUILayout.Label("✗", GUILayout.Width(16));
                GUI.color = Color.white;

                GUILayout.Label(md.meshName, GUILayout.Width(200));
                GUILayout.Label($"{md.instanceCount} instances", GUILayout.Width(100));
                GUILayout.Label($"Potential savings: ~{md.instanceCount - 1} draw calls", GUILayout.Width(250));

                if (GUILayout.Button("Fix Materials", GUILayout.Width(100)))
                {
                    foreach (var mat in md.usedMaterials)
                    {
                        if (mat != null && !mat.enableInstancing)
                        {
                            Undo.RecordObject(mat, "Enable GPU Instancing");
                            mat.enableInstancing = true;
                            EditorUtility.SetDirty(mat);
                        }
                    }
                    AnalyzeScene();
                }

                EditorGUILayout.EndHorizontal();
            }
        }
    }

    // ── TAB 5: Optimization Report ───────────────────────────────────────

    private void DrawOptimizationReportTab()
    {
        DrawSectionHeader("MODERN RENDER OPTIMIZATION REPORT");

        // ── Section 1: GPU Instancing ──
        DrawSectionHeader("1. GPU INSTANCING (Recommended for 10-500 instances)");
        DrawReportBox(
            "GPU Instancing cho phép render hàng trăm mesh giống nhau trong 1 draw call.\n" +
            "GPU xử lý toàn bộ transform, không cần CPU iterate từng object.\n\n" +
            "Yêu cầu:\n" +
            "• Cùng Mesh + cùng Material (có thể khác per-instance property via MaterialPropertyBlock)\n" +
            "• Material phải enable 'GPU Instancing'\n" +
            "• Shader phải support instancing (#pragma multi_compile_instancing)\n" +
            "• KHÔNG dùng chung với Static Batching (conflict)\n\n" +
            "Khi nào dùng: Cây cối, đá, props lặp lại nhiều, particles custom",
            MessageType.None);

        int notInstanced = meshInstances.Count(m => m.instanceCount > 10 && !m.isGPUInstancingReady);
        if (notInstanced > 0)
        {
            EditorGUILayout.HelpBox(
                $"⚠ {notInstanced} mesh(es) có > 10 instances nhưng CHƯA enable GPU Instancing!\n" +
                "→ Chuyển sang tab 'Draw Call Map' và click 'Fix Materials' để auto-enable.",
                MessageType.Warning);
        }
        else
        {
            EditorGUILayout.HelpBox("✓ Tất cả mesh có nhiều instance đều đã GPU Instancing ready.", MessageType.Info);
        }

        EditorGUILayout.Space(8);

        // ── Section 2: DrawMeshInstancedIndirect ──
        DrawSectionHeader("2. INDIRECT DRAW - GPU DRIVEN RENDERING (500+ instances)");
        DrawReportBox(
            "DrawMeshInstancedIndirect / Graphics.RenderMeshIndirect:\n" +
            "• GPU tự quyết định render bao nhiêu instance (compute buffer)\n" +
            "• CPU chỉ cần 1 draw call duy nhất, bất kể số lượng\n" +
            "• Kết hợp với Compute Shader để culling trên GPU (frustum + occlusion)\n" +
            "• Hỗ trợ LOD trên GPU (distance-based LOD không qua CPU)\n\n" +
            "Pipeline:\n" +
            "  ComputeBuffer (positions) → Compute Shader (cull) → AppendBuffer → IndirectDraw\n\n" +
            "Khi nào dùng: Vegetation system, crowd rendering, particle-like objects (>500)\n" +
            "Framework: Unity DOTS/ECS, custom compute pipeline, hoặc GPU Resident Drawer (Unity 6+)",
            MessageType.None);

        var indirectCandidates = meshInstances
            .Where(m => m.instanceCount > 500)
            .OrderByDescending(m => m.instanceCount)
            .ToList();

        if (indirectCandidates.Any())
        {
            EditorGUILayout.HelpBox(
                $"🎯 {indirectCandidates.Count} mesh(es) nên chuyển sang Indirect Draw:",
                MessageType.Warning);

            foreach (var md in indirectCandidates)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(20);
                GUI.color = new Color(0.5f, 0.9f, 1f);
                GUILayout.Label($"→ {md.meshName}: {md.instanceCount} instances, " +
                    $"{FormatNumber(md.totalTrianglesInScene)} total tris", EditorStyles.boldLabel);
                GUI.color = Color.white;
                EditorGUILayout.EndHorizontal();
            }
        }

        EditorGUILayout.Space(8);

        // ── Section 3: SRP Batcher ──
        DrawSectionHeader("3. SRP BATCHER (URP/HDRP)");
        DrawReportBox(
            "SRP Batcher giảm CPU cost bằng cách cache material properties trên GPU.\n" +
            "Không cần cùng mesh hay cùng material, chỉ cần cùng shader variant.\n\n" +
            "Ưu điểm:\n" +
            "• Không giảm draw call number nhưng giảm CPU cost/draw call rất nhiều\n" +
            "• Hoạt động tự động với URP/HDRP compatible shaders\n" +
            "• Kết hợp được với GPU Instancing\n\n" +
            "Lưu ý: Phải dùng URP/HDRP, shader phải declare CBUFFER correctly",
            MessageType.None);

        int notSRPReady = materialDataList.Count(m => !m.srpBatcherCompatible);
        if (notSRPReady > 0)
        {
            EditorGUILayout.HelpBox(
                $"⚠ {notSRPReady}/{totalUniqueMaterials} materials không SRP Batcher compatible.\n" +
                "→ Kiểm tra shader có dùng CBUFFER_START/END đúng cách không.\n" +
                "→ Chuyển từ Built-in shader sang URP/HDRP Lit shader.",
                MessageType.Warning);
        }

        EditorGUILayout.Space(8);

        // ── Section 4: LOD Strategy ──
        DrawSectionHeader("4. LOD STRATEGY - GPU vs CPU");
        DrawReportBox(
            "❌ Unity LODGroup (CPU-based):\n" +
            "• CPU tính distance cho TỪNG object mỗi frame\n" +
            "• Với 10,000 objects = 10,000 distance calculations trên CPU\n" +
            "• Gây CPU bottleneck nghiêm trọng\n\n" +
            "✅ GPU LOD (Modern approach):\n" +
            "• Compute Shader tính distance trên GPU song song\n" +
            "• Chọn LOD level trong compute buffer\n" +
            "• Append vào IndirectArgs buffer tương ứng\n" +
            "• CPU cost = 0 cho LOD selection\n\n" +
            "Implementation:\n" +
            "  1. Store all instance transforms in ComputeBuffer\n" +
            "  2. Compute Shader: frustum cull + distance → LOD level\n" +
            "  3. AppendStructuredBuffer per LOD level\n" +
            "  4. DrawMeshInstancedIndirect per LOD mesh\n\n" +
            "Hoặc dùng Unity 6 GPU Resident Drawer + GPU Occlusion Culling",
            MessageType.None);

        int noLODHighInstance = meshInstances.Count(m => m.instanceCount > 50 && !m.hasLODGroup);
        if (noLODHighInstance > 0)
        {
            EditorGUILayout.HelpBox(
                $"⚠ {noLODHighInstance} mesh(es) có > 50 instances mà KHÔNG có LOD!\n" +
                "→ Thêm LOD hoặc implement GPU-based distance culling.",
                MessageType.Warning);
        }

        EditorGUILayout.Space(8);

        // ── Section 5: Quick Actions ──
        DrawSectionHeader("5. QUICK FIX ACTIONS");

        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Enable GPU Instancing\non ALL Materials", GUILayout.Height(40)))
        {
            int count = 0;
            foreach (var md in materialDataList)
            {
                if (md.material != null && !md.enableGPUInstancing)
                {
                    Undo.RecordObject(md.material, "Enable GPU Instancing");
                    md.material.enableInstancing = true;
                    EditorUtility.SetDirty(md.material);
                    count++;
                }
            }
            EditorUtility.DisplayDialog("Done",
                $"Enabled GPU Instancing on {count} materials.", "OK");
            AnalyzeScene();
        }

        if (GUILayout.Button("Select All Non-Instanced\nHigh-Count Objects", GUILayout.Height(40)))
        {
            var objects = meshInstances
                .Where(m => m.instanceCount > 10 && !m.isGPUInstancingReady)
                .SelectMany(m => m.gameObjects)
                .Select(go => go as Object)
                .ToArray();
            Selection.objects = objects;
        }

        if (GUILayout.Button("Select All\nTransparent Renderers", GUILayout.Height(40)))
        {
            var objects = materialDataList
                .Where(m => m.isTransparent)
                .SelectMany(m => m.usedBy)
                .Distinct()
                .Select(go => go as Object)
                .ToArray();
            Selection.objects = objects;
        }

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(8);

        // ── Section 6: Code Snippets ──
        DrawSectionHeader("6. CODE REFERENCE - INDIRECT DRAW SETUP");

        EditorGUILayout.HelpBox(
            "// ═══ Minimal GPU Instanced Indirect Draw Setup ═══\n\n" +
            "// 1. Create ComputeBuffer with instance transforms\n" +
            "ComputeBuffer posBuffer = new ComputeBuffer(count, sizeof(float) * 16);\n" +
            "posBuffer.SetData(matrices); // Matrix4x4[]\n\n" +
            "// 2. Create IndirectArgs buffer\n" +
            "uint[] args = { mesh.GetIndexCount(0), (uint)count, 0, 0, 0 };\n" +
            "ComputeBuffer argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint),\n" +
            "    ComputeBufferType.IndirectArguments);\n" +
            "argsBuffer.SetData(args);\n\n" +
            "// 3. Set buffer on material\n" +
            "material.SetBuffer(\"_InstanceBuffer\", posBuffer);\n\n" +
            "// 4. Draw (1 draw call for ALL instances)\n" +
            "Graphics.DrawMeshInstancedIndirect(mesh, 0, material, bounds, argsBuffer);\n\n" +
            "// ═══ GPU Culling via Compute Shader ═══\n" +
            "// cullShader.Dispatch(kernel, threadGroups, 1, 1);\n" +
            "// Compute shader writes visible count → argsBuffer[1]",
            MessageType.None);
    }

    // ── UI Helpers ───────────────────────────────────────────────────────

    private void DrawSectionHeader(string title)
    {
        EditorGUILayout.Space(4);
        var style = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 12,
            normal = { textColor = COLOR_INFO }
        };
        GUILayout.Label($"━━━ {title} ━━━", style);
        EditorGUILayout.Space(2);
    }

    private void DrawStatRow(string label, string value)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(130));
        GUILayout.Label(value, EditorStyles.boldLabel);
        EditorGUILayout.EndHorizontal();
    }

    private void DrawStatRowColored(string label, string value, Color color)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(130));
        var prev = GUI.color;
        GUI.color = color;
        GUILayout.Label(value, EditorStyles.boldLabel);
        GUI.color = prev;
        EditorGUILayout.EndHorizontal();
    }

    private void DrawColoredLabel(string text, Color color, float width)
    {
        var prev = GUI.color;
        GUI.color = color;
        GUILayout.Label(text, GUILayout.Width(width));
        GUI.color = prev;
    }

    private void DrawReadinessBox(string label, int ready, int total, Color color)
    {
        EditorGUILayout.BeginVertical("box", GUILayout.MinWidth(200));
        GUILayout.Label(label, EditorStyles.boldLabel);

        float pct = (float)ready / Mathf.Max(total, 1);
        var prev = GUI.color;
        GUI.color = color;

        var rect = GUILayoutUtility.GetRect(180, 22);
        EditorGUI.ProgressBar(rect, pct, $"{ready}/{total} ({pct * 100:F0}%)");

        GUI.color = prev;
        EditorGUILayout.EndVertical();
    }

    private void DrawReportBox(string content, MessageType type)
    {
        var style = new GUIStyle(EditorStyles.helpBox)
        {
            fontSize = 11,
            richText = true,
            padding = new RectOffset(10, 10, 8, 8)
        };
        EditorGUILayout.LabelField(content, style);
    }

    private string TruncateString(string str, int maxLen)
    {
        if (string.IsNullOrEmpty(str)) return "";
        return str.Length > maxLen ? str.Substring(0, maxLen) + "..." : str;
    }

    // ── Export ────────────────────────────────────────────────────────────

    private void ExportReport()
    {
        var path = EditorUtility.SaveFilePanel("Export Analysis Report",
            "", "SceneAnalysis_Report", "txt");
        if (string.IsNullOrEmpty(path)) return;

        var sb = new StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════════════════════");
        sb.AppendLine("  SCENE MATERIAL & INSTANCE ANALYSIS REPORT");
        sb.AppendLine($"  Generated: {System.DateTime.Now}");
        sb.AppendLine($"  Scene: {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
        sb.AppendLine("═══════════════════════════════════════════════════════════");
        sb.AppendLine();

        sb.AppendLine("── OVERVIEW ──────────────────────────────────────────────");
        sb.AppendLine($"  GameObjects: {totalGameObjects}");
        sb.AppendLine($"  Renderers: {totalRenderers}");
        sb.AppendLine($"  Unique Meshes: {totalUniqueMeshes}");
        sb.AppendLine($"  Unique Materials: {totalUniqueMaterials}");
        sb.AppendLine($"  Unique Shaders: {totalUniqueShaders}");
        sb.AppendLine($"  Total Vertices: {FormatNumber(totalVerticesInScene)}");
        sb.AppendLine($"  Total Triangles: {FormatNumber(totalTrianglesInScene)}");
        sb.AppendLine($"  Est. VRAM: {FormatBytes(totalEstimatedVRAM)}");
        sb.AppendLine($"  Est. Draw Calls: {estimatedDrawCalls}");
        sb.AppendLine($"  Optimized Draw Calls: {estimatedDrawCallsOptimized}");
        sb.AppendLine($"  Reduction: {(1f - (float)estimatedDrawCallsOptimized / Mathf.Max(estimatedDrawCalls, 1)) * 100f:F0}%");
        sb.AppendLine();

        sb.AppendLine("── TOP MESH INSTANCES ────────────────────────────────────");
        foreach (var md in meshInstances.OrderByDescending(m => m.instanceCount).Take(30))
        {
            sb.AppendLine($"  {md.meshName,-30} | {md.instanceCount,5} inst | {FormatNumber(md.vertexCount),6} verts | " +
                $"{FormatNumber(md.totalTrianglesInScene),8} total tris | Strategy: {GetStrategyLabel(md.recommendedStrategy)}");
        }
        sb.AppendLine();

        sb.AppendLine("── MATERIALS WITHOUT GPU INSTANCING ──────────────────────");
        foreach (var md in materialDataList.Where(m => !m.enableGPUInstancing).OrderByDescending(m => m.usageCount))
        {
            sb.AppendLine($"  {md.materialName,-30} | {md.usageCount,4} uses | Shader: {md.shaderName}");
        }
        sb.AppendLine();

        sb.AppendLine("── INDIRECT DRAW CANDIDATES ──────────────────────────────");
        foreach (var md in meshInstances.Where(m => m.recommendedStrategy == BatchStrategy.IndirectDraw))
        {
            sb.AppendLine($"  {md.meshName}: {md.instanceCount} instances → " +
                $"Saves ~{md.instanceCount - 1} draw calls");
        }

        System.IO.File.WriteAllText(path, sb.ToString());
        EditorUtility.DisplayDialog("Exported", $"Report saved to:\n{path}", "OK");
        EditorUtility.RevealInFinder(path);
    }
}
#endif
