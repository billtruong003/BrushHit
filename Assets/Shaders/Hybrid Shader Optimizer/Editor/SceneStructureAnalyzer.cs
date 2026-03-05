// ============================================================================
// Scene Structure Analyzer — Editor Tool
// Tech Art Department | v1.0 | March 2026
//
// Phân tích scene xem objects nào đã Mesh Baker combine, objects nào riêng lẻ,
// phân loại theo tên/tag/size để quyết định optimize strategy.
//
// Usage: Window > Tech Art > Scene Analyzer
// ============================================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace TechArt.Tools
{
    public class SceneStructureAnalyzer : EditorWindow
    {
        // ── Data ──
        private class ObjectGroup
        {
            public string groupName;
            public string matchPattern;      // Tên chứa pattern này
            public Color color;
            public List<RendererInfo> renderers = new List<RendererInfo>();
            public bool foldout = false;

            // Stats
            public int totalRenderers;
            public long totalTris;
            public int staticGICount;
            public int activeCount;
            public bool isCombinedMesh;     // Là kết quả Mesh Baker
        }

        private class RendererInfo
        {
            public GameObject gameObject;
            public MeshRenderer meshRenderer;
            public string name;
            public int triCount;
            public float maxDimension;
            public bool isStatic;
            public bool isContributeGI;
            public bool isActive;
            public bool isCombinedMesh;
            public float scaleInLightmap;
        }

        // ── State ──
        private List<ObjectGroup> _groups = new List<ObjectGroup>();
        private List<RendererInfo> _ungrouped = new List<RendererInfo>();
        private Vector2 _scrollPos;
        private bool _hasScanned = false;

        // Totals
        private int _totalRenderers;
        private long _totalTris;
        private int _totalStaticGI;
        private int _meshBakerCombined;
        private int _meshBakerSources;
        private int _individualObjects;

        // Foldouts
        private bool _foldSummary = true;
        private bool _foldMeshBaker = true;
        private bool _foldGroups = true;
        private bool _foldUngrouped = false;
        private bool _foldRecommendations = true;

        // Styles
        private GUIStyle _headerStyle, _boxStyle, _boldLabel, _miniLabel, _richLabel;
        private bool _stylesInit;

        [MenuItem("Window/Tech Art/Scene Analyzer")]
        public static void ShowWindow()
        {
            var wnd = GetWindow<SceneStructureAnalyzer>("Scene Analyzer");
            wnd.minSize = new Vector2(620, 450);
        }

        private void InitStyles()
        {
            if (_stylesInit) return;
            _headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14, margin = new RectOffset(4, 4, 8, 4) };
            _boxStyle = new GUIStyle("box") { padding = new RectOffset(8, 8, 6, 6), margin = new RectOffset(4, 4, 2, 2) };
            _boldLabel = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold };
            _miniLabel = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            _richLabel = new GUIStyle(EditorStyles.label) { richText = true, wordWrap = true };
            _stylesInit = true;
        }

        // ══════════════════════════════════════════════
        // GUI
        // ══════════════════════════════════════════════
        private void OnGUI()
        {
            InitStyles();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Scene Structure Analyzer", _headerStyle);
            EditorGUILayout.LabelField("Phân tích Mesh Baker + objects riêng lẻ, đề xuất optimize strategy", _miniLabel);
            EditorGUILayout.Space(4);

            // Actions
            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = new Color(0.3f, 0.7f, 0.9f);
            if (GUILayout.Button("🔍  Analyze Scene", GUILayout.Height(30)))
                AnalyzeScene();

            GUI.backgroundColor = new Color(0.8f, 0.7f, 0.3f);
            GUI.enabled = _hasScanned;
            if (GUILayout.Button("📋  Copy Report", GUILayout.Height(30), GUILayout.Width(120)))
                CopyReport();
            GUI.enabled = true;
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            if (!_hasScanned)
            {
                EditorGUILayout.Space(40);
                EditorGUILayout.LabelField("Nhấn Analyze để scan scene.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            // Summary
            _foldSummary = EditorGUILayout.Foldout(_foldSummary, "📊 Tổng Quan", true, EditorStyles.foldoutHeader);
            if (_foldSummary) DrawSummary();

            // Mesh Baker section
            _foldMeshBaker = EditorGUILayout.Foldout(_foldMeshBaker, "🔧 Mesh Baker Analysis", true, EditorStyles.foldoutHeader);
            if (_foldMeshBaker) DrawMeshBakerSection();

            // Groups
            _foldGroups = EditorGUILayout.Foldout(_foldGroups, "📁 Object Groups (by name pattern)", true, EditorStyles.foldoutHeader);
            if (_foldGroups) DrawGroups();

            // Recommendations
            _foldRecommendations = EditorGUILayout.Foldout(_foldRecommendations, "💡 Recommendations", true, EditorStyles.foldoutHeader);
            if (_foldRecommendations) DrawRecommendations();

            EditorGUILayout.EndScrollView();
        }

        private void DrawSummary()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            EditorGUILayout.LabelField($"Total Renderers (active): {_totalRenderers}", _boldLabel);

            string triStr = _totalTris > 1_000_000 ? $"{_totalTris / 1_000_000f:F2}M" : $"{_totalTris / 1000f:F0}K";
            EditorGUILayout.LabelField($"Total Triangles: {triStr}");
            EditorGUILayout.LabelField($"ContributeGI Objects: {_totalStaticGI}");

            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            GUI.color = new Color(0.4f, 0.85f, 1f);
            EditorGUILayout.LabelField($"🔧 Mesh Baker Combined: {_meshBakerCombined}", GUILayout.Width(220));
            GUI.color = new Color(1f, 0.8f, 0.4f);
            EditorGUILayout.LabelField($"📦 MB Source (disabled): {_meshBakerSources}", GUILayout.Width(210));
            GUI.color = new Color(0.5f, 1f, 0.5f);
            EditorGUILayout.LabelField($"🟢 Individual: {_individualObjects}");
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();

            // Quest budget comparison
            EditorGUILayout.Space(4);
            float triRatio = _totalTris / 500000f;
            float rendererRatio = _totalRenderers / 500f;
            GUI.color = triRatio > 1 ? new Color(1f, 0.4f, 0.3f) : new Color(0.3f, 0.9f, 0.3f);
            EditorGUILayout.LabelField($"Quest 3 Tri Budget:      {triStr} / 500K  ({triRatio:F1}x)", _boldLabel);
            GUI.color = rendererRatio > 1 ? new Color(1f, 0.4f, 0.3f) : new Color(0.3f, 0.9f, 0.3f);
            EditorGUILayout.LabelField($"Quest 3 Renderer Budget: {_totalRenderers} / 500   ({rendererRatio:F1}x)", _boldLabel);
            GUI.color = Color.white;

            EditorGUILayout.EndVertical();
        }

        private void DrawMeshBakerSection()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            if (_meshBakerCombined == 0)
            {
                EditorGUILayout.LabelField("Không tìm thấy Mesh Baker combined objects rõ ràng.", _miniLabel);
                EditorGUILayout.LabelField("Có thể đã rename hoặc dùng workflow khác. Check Hierarchy thủ công.", _miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField($"Tìm thấy {_meshBakerCombined} combined mesh(es) từ Mesh Baker.", _boldLabel);

                // List combined meshes
                var combined = _groups.Where(g => g.isCombinedMesh).ToList();
                foreach (var g in combined)
                {
                    EditorGUILayout.BeginHorizontal();
                    string triLabel = g.totalTris > 1000 ? $"{g.totalTris / 1000f:F1}K" : $"{g.totalTris}";
                    EditorGUILayout.LabelField($"  📦 {g.groupName}: {g.totalRenderers} mesh(es), {triLabel} tris, " +
                        $"GI: {g.staticGICount}");

                    if (g.renderers.Count > 0 && GUILayout.Button("📍", GUILayout.Width(26)))
                    {
                        Selection.activeGameObject = g.renderers[0].gameObject;
                        EditorGUIUtility.PingObject(g.renderers[0].gameObject);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            // Source objects (disabled by Mesh Baker)
            if (_meshBakerSources > 0)
            {
                EditorGUILayout.Space(4);
                GUI.color = new Color(1f, 0.8f, 0.4f);
                EditorGUILayout.LabelField($"⚠ {_meshBakerSources} Mesh Baker source objects (disabled) vẫn đang trong scene.", _boldLabel);
                GUI.color = Color.white;
                EditorGUILayout.LabelField("  Những objects này đã bị disable nhưng vẫn tồn tại. Nếu ContributeGI " +
                    "vẫn bật trên chúng, lightmapper có thể vẫn xử lý.", _miniLabel);

                if (GUILayout.Button("Select All MB Source Objects (disabled)"))
                {
                    var sources = FindAllMeshBakerSources();
                    Selection.objects = sources.Select(s => (UnityEngine.Object)s).ToArray();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawGroups()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            var nonMBGroups = _groups.Where(g => !g.isCombinedMesh).OrderByDescending(g => g.totalTris).ToList();

            foreach (var g in nonMBGroups)
            {
                string triLabel = g.totalTris > 1000 ? $"{g.totalTris / 1000f:F1}K" : $"{g.totalTris}";
                string giLabel = g.staticGICount > 0 ? $"  GI:{g.staticGICount}" : "";

                EditorGUILayout.BeginHorizontal();

                GUI.color = g.color;
                g.foldout = EditorGUILayout.Foldout(g.foldout,
                    $"● {g.groupName}:  {g.totalRenderers} obj,  {triLabel} tris{giLabel}",
                    true);
                GUI.color = Color.white;

                // Select all in group
                if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(50)))
                {
                    Selection.objects = g.renderers
                        .Where(r => r.gameObject != null)
                        .Select(r => (UnityEngine.Object)r.gameObject)
                        .ToArray();
                }

                // Batch disable GI
                if (g.staticGICount > 0 && GUILayout.Button("Disable GI", EditorStyles.miniButton, GUILayout.Width(72)))
                {
                    if (EditorUtility.DisplayDialog("Disable GI",
                        $"Tắt ContributeGI cho {g.staticGICount} objects trong group '{g.groupName}'?", "OK", "Cancel"))
                    {
                        BatchDisableGI(g);
                    }
                }

                EditorGUILayout.EndHorizontal();

                if (g.foldout)
                {
                    EditorGUI.indentLevel++;
                    int showCount = Mathf.Min(g.renderers.Count, 20);
                    for (int i = 0; i < showCount; i++)
                    {
                        var r = g.renderers[i];
                        EditorGUILayout.BeginHorizontal();
                        string status = r.isActive ? "" : " [disabled]";
                        string gi = r.isContributeGI ? " 🌞GI" : "";
                        string dim = r.maxDimension < 1 ? $"{r.maxDimension:F2}m" : $"{r.maxDimension:F1}m";
                        EditorGUILayout.LabelField($"  {r.name}{status}{gi}  |  {dim}  |  {r.triCount} tris  |  LM:{r.scaleInLightmap:F1}", _miniLabel);

                        if (GUILayout.Button("📍", GUILayout.Width(24), GUILayout.Height(16)))
                        {
                            Selection.activeGameObject = r.gameObject;
                            EditorGUIUtility.PingObject(r.gameObject);
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    if (g.renderers.Count > showCount)
                        EditorGUILayout.LabelField($"  ... và {g.renderers.Count - showCount} objects nữa", _miniLabel);

                    EditorGUI.indentLevel--;
                }
            }

            // Ungrouped
            if (_ungrouped.Count > 0)
            {
                _foldUngrouped = EditorGUILayout.Foldout(_foldUngrouped,
                    $"❓ Ungrouped: {_ungrouped.Count} objects", true);
                if (_foldUngrouped)
                {
                    EditorGUI.indentLevel++;
                    int show = Mathf.Min(_ungrouped.Count, 30);
                    for (int i = 0; i < show; i++)
                    {
                        var r = _ungrouped[i];
                        EditorGUILayout.BeginHorizontal();
                        string gi = r.isContributeGI ? " 🌞" : "";
                        EditorGUILayout.LabelField($"  {r.name}{gi}  |  {r.triCount} tris", _miniLabel);
                        if (GUILayout.Button("📍", GUILayout.Width(24), GUILayout.Height(16)))
                        {
                            Selection.activeGameObject = r.gameObject;
                            EditorGUIUtility.PingObject(r.gameObject);
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawRecommendations()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            // Occlusion Culling
            GUI.color = new Color(1f, 0.4f, 0.3f);
            EditorGUILayout.LabelField("❌ CRITICAL: Bật Occlusion Culling ngay!", _boldLabel);
            GUI.color = Color.white;
            EditorGUILayout.LabelField("  Canyon map như này cực kỳ phù hợp Occlusion Culling. Vách đá sẽ che khuất 40–60% scene.\n" +
                "  → Window > Rendering > Occlusion Culling > Bake", _miniLabel);
            EditorGUILayout.Space(4);

            // Per-group recommendations
            foreach (var g in _groups.Where(g => !g.isCombinedMesh).OrderByDescending(g => g.totalTris))
            {
                string name = g.groupName.ToLower();
                bool isVegetation = name.Contains("tree") || name.Contains("grass") || name.Contains("bush") ||
                                   name.Contains("plant") || name.Contains("flower") || name.Contains("leaf") ||
                                   name.Contains("seagrass") || name.Contains("cactus") || name.Contains("fern");
                bool isSmallProp = name.Contains("barrel") || name.Contains("box") || name.Contains("crate") ||
                                   name.Contains("plate") || name.Contains("bottle") || name.Contains("cup") ||
                                   name.Contains("light") || name.Contains("bulb") || name.Contains("lamp");
                bool isWater = name.Contains("water") || name.Contains("pool") || name.Contains("river") ||
                              name.Contains("sea") || name.Contains("ocean");
                bool isRock = name.Contains("rock") || name.Contains("stone") || name.Contains("cliff") ||
                             name.Contains("mountain") || name.Contains("canyon");

                string rec = "";
                if (isVegetation)
                    rec = $"🌿 '{g.groupName}' ({g.totalRenderers} obj): Tắt ContributeGI, dùng Light Probes + GPU Instancing. LOD nếu chưa có.";
                else if (isSmallProp)
                    rec = $"📦 '{g.groupName}' ({g.totalRenderers} obj): Tắt ContributeGI, dùng Light Probes. Scale In Lightmap = 0.";
                else if (isWater)
                    rec = $"🌊 '{g.groupName}' ({g.totalRenderers} obj): Tắt ContributeGI. Water shader tự handle.";
                else if (isRock)
                    rec = $"🪨 '{g.groupName}' ({g.totalRenderers} obj): Giữ GI, Scale In Lightmap = 0.5–0.8.";
                else if (g.totalRenderers > 50)
                    rec = $"❓ '{g.groupName}' ({g.totalRenderers} obj): Nhiều objects. Check xem có cần lightmap không.";

                if (!string.IsNullOrEmpty(rec))
                    EditorGUILayout.LabelField(rec, _miniLabel);
            }

            // Mesh Baker sources
            if (_meshBakerSources > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField($"⚠ {_meshBakerSources} Mesh Baker source objects disabled nhưng vẫn tồn tại. " +
                    "Kiểm tra ContributeGI flag trên chúng.", _miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════════════════
        // ANALYZE
        // ══════════════════════════════════════════════
        private void AnalyzeScene()
        {
            _groups.Clear();
            _ungrouped.Clear();
            _hasScanned = true;
            _totalRenderers = 0;
            _totalTris = 0;
            _totalStaticGI = 0;
            _meshBakerCombined = 0;
            _meshBakerSources = 0;
            _individualObjects = 0;

            // Collect all renderers
            var allRenderers = FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var allInfos = new List<RendererInfo>();

            foreach (var mr in allRenderers)
            {
                var go = mr.gameObject;
                var mf = go.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                var mesh = mf.sharedMesh;
                var bounds = mr.bounds;
                var flags = GameObjectUtility.GetStaticEditorFlags(go);

                var info = new RendererInfo
                {
                    gameObject = go,
                    meshRenderer = mr,
                    name = go.name,
                    triCount = mesh.triangles.Length / 3,
                    maxDimension = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z),
                    isStatic = go.isStatic,
                    isContributeGI = flags.HasFlag(StaticEditorFlags.ContributeGI),
                    isActive = go.activeInHierarchy,
                    scaleInLightmap = mr.scaleInLightmap,
                };

                // Detect Mesh Baker combined meshes
                info.isCombinedMesh = IsMeshBakerCombined(go, mesh);

                allInfos.Add(info);

                if (info.isActive)
                {
                    _totalRenderers++;
                    _totalTris += info.triCount;
                    if (info.isContributeGI) _totalStaticGI++;
                }

                if (info.isCombinedMesh)
                    _meshBakerCombined++;
                else if (IsMeshBakerSource(go))
                    _meshBakerSources++;
                else
                    _individualObjects++;
            }

            // ── Auto-group by name patterns ──
            var patterns = DetectNamePatterns(allInfos);

            foreach (var pattern in patterns)
            {
                var matching = allInfos.Where(i => MatchesPattern(i.name, pattern.Key)).ToList();
                if (matching.Count < 2) continue; // Skip unique objects

                var group = new ObjectGroup
                {
                    groupName = pattern.Key,
                    matchPattern = pattern.Key,
                    color = GetGroupColor(pattern.Key),
                    renderers = matching,
                    isCombinedMesh = matching.Any(m => m.isCombinedMesh),
                    totalRenderers = matching.Count,
                    totalTris = matching.Sum(m => (long)m.triCount),
                    staticGICount = matching.Count(m => m.isContributeGI),
                    activeCount = matching.Count(m => m.isActive),
                };

                _groups.Add(group);

                // Remove from allInfos to avoid double-counting
                foreach (var m in matching)
                    allInfos.Remove(m);
            }

            // Sort groups by tri count
            _groups.Sort((a, b) => b.totalTris.CompareTo(a.totalTris));

            // Remaining ungrouped
            _ungrouped = allInfos.Where(i => i.isActive).OrderByDescending(i => i.triCount).ToList();

            Debug.Log($"[Scene Analyzer] Done: {_totalRenderers} active renderers, " +
                      $"{_groups.Count} groups, {_meshBakerCombined} MB combined, {_meshBakerSources} MB sources");
        }

        // ── Pattern Detection ──
        private Dictionary<string, int> DetectNamePatterns(List<RendererInfo> infos)
        {
            var nameCounts = new Dictionary<string, int>();

            foreach (var info in infos)
            {
                string baseName = GetBaseName(info.name);
                if (string.IsNullOrEmpty(baseName)) continue;

                if (!nameCounts.ContainsKey(baseName))
                    nameCounts[baseName] = 0;
                nameCounts[baseName]++;
            }

            // Only keep patterns with 2+ matches, sorted by count
            return nameCounts
                .Where(kv => kv.Value >= 2)
                .OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        private string GetBaseName(string name)
        {
            // Remove common suffixes: (1), (Clone), _LOD0, numbers
            string clean = name;

            // Remove (N) suffix
            int parenIdx = clean.LastIndexOf('(');
            if (parenIdx > 0 && clean.EndsWith(")"))
            {
                string inside = clean.Substring(parenIdx + 1, clean.Length - parenIdx - 2).Trim();
                if (int.TryParse(inside, out _) || inside == "Clone")
                    clean = clean.Substring(0, parenIdx).Trim();
            }

            // Remove _LOD0, _LOD1 etc
            for (int i = 0; i < 5; i++)
            {
                clean = clean.Replace($"_LOD{i}", "");
                clean = clean.Replace($"_lod{i}", "");
            }

            // Remove trailing numbers and spaces
            clean = clean.TrimEnd(' ', '_', '-');
            while (clean.Length > 1 && char.IsDigit(clean[clean.Length - 1]))
                clean = clean.Substring(0, clean.Length - 1);
            clean = clean.TrimEnd(' ', '_', '-');

            return clean.Length >= 2 ? clean : null;
        }

        private bool MatchesPattern(string name, string pattern)
        {
            string baseName = GetBaseName(name);
            return baseName == pattern;
        }

        private Color GetGroupColor(string name)
        {
            string lower = name.ToLower();
            if (lower.Contains("tree") || lower.Contains("grass") || lower.Contains("bush") ||
                lower.Contains("plant") || lower.Contains("leaf") || lower.Contains("fern"))
                return new Color(0.3f, 0.85f, 0.3f);
            if (lower.Contains("rock") || lower.Contains("stone") || lower.Contains("cliff"))
                return new Color(0.6f, 0.5f, 0.4f);
            if (lower.Contains("water") || lower.Contains("pool") || lower.Contains("sea"))
                return new Color(0.3f, 0.6f, 1f);
            if (lower.Contains("barrel") || lower.Contains("box") || lower.Contains("crate"))
                return new Color(0.9f, 0.7f, 0.3f);
            if (lower.Contains("bridge") || lower.Contains("wall") || lower.Contains("floor"))
                return new Color(0.8f, 0.5f, 0.3f);

            // Hash-based color for others
            int hash = name.GetHashCode();
            float h = Mathf.Abs(hash % 360) / 360f;
            return Color.HSVToRGB(h, 0.5f, 0.85f);
        }

        // ── Mesh Baker Detection ──
        private bool IsMeshBakerCombined(GameObject go, Mesh mesh)
        {
            // Mesh Baker combined objects detection heuristics
            string name = go.name.ToLower();
            string meshName = mesh.name.ToLower();

            // Common Mesh Baker naming patterns
            if (name.Contains("meshbaker") || name.Contains("mb3_") || name.Contains("mesh baker"))
                return true;
            if (meshName.Contains("combinedmesh") || meshName.Contains("mb_") || meshName.Contains("meshbaker"))
                return true;

            // Check for MB3 components
            var components = go.GetComponents<Component>();
            foreach (var comp in components)
            {
                if (comp == null) continue;
                string typeName = comp.GetType().Name;
                if (typeName.Contains("MB3") || typeName.Contains("MeshBaker") || typeName.Contains("TextureBaker"))
                    return true;
            }

            // Check parent
            if (go.transform.parent != null)
            {
                string parentName = go.transform.parent.name.ToLower();
                if (parentName.Contains("meshbaker") || parentName.Contains("mb3_") || parentName.Contains("mesh baker"))
                    return true;
            }

            return false;
        }

        private bool IsMeshBakerSource(GameObject go)
        {
            // Disabled objects that were likely Mesh Baker sources
            if (go.activeInHierarchy) return false;

            // Check if parent has MB3 component
            Transform parent = go.transform.parent;
            while (parent != null)
            {
                var components = parent.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;
                    if (typeName.Contains("MB3") || typeName.Contains("MeshBaker"))
                        return true;
                }
                parent = parent.parent;
            }

            return false;
        }

        private List<GameObject> FindAllMeshBakerSources()
        {
            var all = FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            return all.Where(mr => IsMeshBakerSource(mr.gameObject))
                      .Select(mr => mr.gameObject)
                      .ToList();
        }

        // ── Actions ──
        private void BatchDisableGI(ObjectGroup group)
        {
            Undo.SetCurrentGroupName($"Disable GI: {group.groupName}");

            int count = 0;
            foreach (var r in group.renderers)
            {
                if (r.gameObject == null || !r.isContributeGI) continue;

                Undo.RecordObject(r.gameObject, "Disable ContributeGI");
                var flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                flags &= ~StaticEditorFlags.ContributeGI;
                GameObjectUtility.SetStaticEditorFlags(r.gameObject, flags);
                r.isContributeGI = false;
                count++;
            }

            group.staticGICount = 0;
            _totalStaticGI = _groups.Sum(g => g.staticGICount) + _ungrouped.Count(u => u.isContributeGI);

            Debug.Log($"[Scene Analyzer] Disabled GI for {count} objects in '{group.groupName}'");
        }

        // ── Report ──
        private void CopyReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══════════════════════════════════════");
            sb.AppendLine("SCENE STRUCTURE ANALYSIS REPORT");
            sb.AppendLine($"Scene: {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
            sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine("═══════════════════════════════════════");
            sb.AppendLine($"Renderers: {_totalRenderers}  |  Tris: {_totalTris:N0}  |  GI: {_totalStaticGI}");
            sb.AppendLine($"Mesh Baker Combined: {_meshBakerCombined}  |  MB Sources: {_meshBakerSources}  |  Individual: {_individualObjects}");
            sb.AppendLine();

            sb.AppendLine("── Object Groups ──");
            foreach (var g in _groups.OrderByDescending(g => g.totalTris))
            {
                string tri = g.totalTris > 1000 ? $"{g.totalTris / 1000f:F1}K" : $"{g.totalTris}";
                sb.AppendLine($"  {g.groupName}: {g.totalRenderers} obj, {tri} tris, GI: {g.staticGICount}" +
                    (g.isCombinedMesh ? " [MB Combined]" : ""));
            }

            GUIUtility.systemCopyBuffer = sb.ToString();
            Debug.Log("[Scene Analyzer] Report copied to clipboard!");
        }
    }
}
#endif
