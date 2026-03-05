// ============================================================================
// Lightmap Scale Optimizer — Editor Tool
// Tech Art Department | v1.0 | March 2026
//
// Scan scene tìm static objects, tính kích thước thực tế (bounds),
// hiển thị lightmap texel cost, và cho phép batch chỉnh Scale In Lightmap
// hoặc chuyển sang Light Probes.
//
// Usage: Window > Tech Art > Lightmap Scale Optimizer
// ============================================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TechArt.Tools
{
    public class LightmapScaleOptimizer : EditorWindow
    {
        // ── Data ──
        private enum SizeCategory
        {
            Tiny,       // < 0.3m  — hầu như không cần lightmap
            Small,      // 0.3–1m  — props nhỏ: chai, đĩa, đèn
            Medium,     // 1–3m    — bàn, ghế, thùng
            Large,      // 3–10m   — tường, sàn, xe
            Huge        // > 10m   — terrain, building
        }

        private class ObjectEntry
        {
            public MeshRenderer renderer;
            public GameObject gameObject;
            public string name;
            public Mesh mesh;

            // Metrics
            public Vector3 boundsSize;          // World-space bounds
            public float maxDimension;          // Cạnh dài nhất (meters)
            public float surfaceArea;           // Ước tính diện tích bề mặt (m²)
            public int triangleCount;
            public float currentScale;          // Scale In Lightmap hiện tại
            public float recommendedScale;      // Scale đề xuất
            public SizeCategory category;

            // Lightmap cost estimate
            public float estimatedTexels;       // Số texel ước tính chiếm trong atlas
            public float texelPercentOfAtlas;   // % atlas 1024x1024

            public bool selected = false;
            public bool shouldDisableGI = false; // Recommend chuyển sang Light Probes
        }

        // ── State ──
        private List<ObjectEntry> _entries = new List<ObjectEntry>();
        private Vector2 _scrollPos;
        private bool _hasScanned = false;

        // Settings
        private float _lightmapResolution = 15f; // texels/unit — lấy từ Lighting Settings
        private int _atlasSize = 1024;

        // Filters
        private bool _showTiny = true;
        private bool _showSmall = true;
        private bool _showMedium = true;
        private bool _showLarge = true;
        private bool _showHuge = true;
        private bool _showOnlyOverscaled = false;
        private string _searchFilter = "";

        // Sort
        private enum SortMode { Size, TexelCost, Name, CurrentScale, TriCount }
        private SortMode _sortMode = SortMode.TexelCost;
        private bool _sortDescending = true;

        // Stats
        private float _totalTexelsBefore;
        private float _totalTexelsAfter;
        private int _totalObjects;
        private int _overscaledCount;

        // Foldouts
        private bool _foldStats = true;
        private bool _foldPresets = false;

        // Styles
        private GUIStyle _headerStyle;
        private GUIStyle _boxStyle;
        private GUIStyle _boldLabel;
        private GUIStyle _miniLabel;
        private GUIStyle _richLabel;
        private bool _stylesInit;

        // Category colors
        private static readonly Color TinyColor = new Color(0.4f, 0.85f, 1f);
        private static readonly Color SmallColor = new Color(0.4f, 1f, 0.5f);
        private static readonly Color MediumColor = new Color(1f, 0.9f, 0.3f);
        private static readonly Color LargeColor = new Color(1f, 0.6f, 0.2f);
        private static readonly Color HugeColor = new Color(1f, 0.4f, 0.4f);

        [MenuItem("Window/Tech Art/Lightmap Scale Optimizer")]
        public static void ShowWindow()
        {
            var wnd = GetWindow<LightmapScaleOptimizer>("LM Scale Opt");
            wnd.minSize = new Vector2(680, 450);
        }

        private void InitStyles()
        {
            if (_stylesInit) return;
            _headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14, margin = new RectOffset(4, 4, 8, 4) };
            _boxStyle = new GUIStyle("box") { padding = new RectOffset(8, 8, 6, 6), margin = new RectOffset(4, 4, 2, 2) };
            _boldLabel = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold };
            _miniLabel = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            _richLabel = new GUIStyle(EditorStyles.label) { richText = true, wordWrap = true, fontSize = 11 };
            _stylesInit = true;
        }

        // ══════════════════════════════════════════════
        // GUI
        // ══════════════════════════════════════════════
        private void OnGUI()
        {
            InitStyles();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Lightmap Scale Optimizer", _headerStyle);
            EditorGUILayout.LabelField("Tìm objects nhỏ, xem lightmap cost, batch chỉnh Scale In Lightmap", _miniLabel);
            EditorGUILayout.Space(4);

            // ── Settings ──
            EditorGUILayout.BeginHorizontal();
            _lightmapResolution = EditorGUILayout.FloatField("Lightmap Res (texels/unit):", _lightmapResolution, GUILayout.Width(300));
            _atlasSize = EditorGUILayout.IntPopup("Atlas Size:", _atlasSize, 
                new[] { "512", "1024", "2048", "4096" }, 
                new[] { 512, 1024, 2048, 4096 });
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            // ── Action Buttons ──
            EditorGUILayout.BeginHorizontal();

            GUI.backgroundColor = new Color(0.3f, 0.7f, 0.9f);
            if (GUILayout.Button("🔍  Scan Static Objects", GUILayout.Height(30)))
                ScanScene();

            GUI.backgroundColor = new Color(0.2f, 0.8f, 0.4f);
            GUI.enabled = _hasScanned && _entries.Any(e => e.selected);
            if (GUILayout.Button("✅  Apply Recommended Scale", GUILayout.Height(30)))
                ApplyRecommendedToSelected();
            GUI.enabled = true;

            GUI.backgroundColor = new Color(0.9f, 0.5f, 0.2f);
            GUI.enabled = _hasScanned && _entries.Any(e => e.selected && e.shouldDisableGI);
            if (GUILayout.Button("🔄  Disable GI (→ Probes)", GUILayout.Height(30)))
                DisableGIForSelected();
            GUI.enabled = true;

            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            if (!_hasScanned)
            {
                EditorGUILayout.Space(40);
                EditorGUILayout.LabelField("Nhấn Scan để phân tích static objects trong scene.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            EditorGUILayout.Space(4);

            // ── Stats ──
            _foldStats = EditorGUILayout.Foldout(_foldStats, "📊 Statistics & Savings", true, EditorStyles.foldoutHeader);
            if (_foldStats) DrawStats();

            // ── Presets ──
            _foldPresets = EditorGUILayout.Foldout(_foldPresets, "⚡ Quick Actions", true, EditorStyles.foldoutHeader);
            if (_foldPresets) DrawPresets();

            EditorGUILayout.Space(4);

            // ── Filters & Sort ──
            DrawFiltersAndSort();

            EditorGUILayout.Space(4);

            // ── Table Header ──
            DrawTableHeader();

            // ── Entries ──
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            DrawEntries();
            EditorGUILayout.EndScrollView();
        }

        private void DrawStats()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            float atlasTexels = _atlasSize * _atlasSize;
            float atlasBefore = _totalTexelsBefore / atlasTexels;
            float atlasAfter = _totalTexelsAfter / atlasTexels;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Total static objects: {_totalObjects}", GUILayout.Width(200));
            EditorGUILayout.LabelField($"Overscaled: {_overscaledCount}", GUILayout.Width(130));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            // Before
            GUI.color = new Color(1f, 0.6f, 0.4f);
            EditorGUILayout.LabelField($"Texels (current): {FormatTexels(_totalTexelsBefore)}  ≈ {atlasBefore:F1} atlas(es)", _boldLabel, GUILayout.Width(320));

            // After
            GUI.color = new Color(0.4f, 0.9f, 0.4f);
            EditorGUILayout.LabelField($"Texels (recommended): {FormatTexels(_totalTexelsAfter)}  ≈ {atlasAfter:F1} atlas(es)", _boldLabel);

            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();

            float savings = _totalTexelsBefore > 0 ? (1f - _totalTexelsAfter / _totalTexelsBefore) * 100f : 0;
            EditorGUILayout.LabelField($"Potential savings: {savings:F0}% lightmap space", _miniLabel);

            // Category breakdown
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Breakdown by size:", _boldLabel);
            foreach (SizeCategory cat in Enum.GetValues(typeof(SizeCategory)))
            {
                var catEntries = _entries.Where(e => e.category == cat).ToList();
                if (catEntries.Count == 0) continue;

                int disableCount = catEntries.Count(e => e.shouldDisableGI);
                string catLabel = cat switch
                {
                    SizeCategory.Tiny => $"  🔵 Tiny (<0.3m):  {catEntries.Count} objects — {disableCount} nên tắt GI",
                    SizeCategory.Small => $"  🟢 Small (0.3–1m): {catEntries.Count} objects",
                    SizeCategory.Medium => $"  🟡 Medium (1–3m):  {catEntries.Count} objects",
                    SizeCategory.Large => $"  🟠 Large (3–10m):  {catEntries.Count} objects",
                    SizeCategory.Huge => $"  🔴 Huge (>10m):   {catEntries.Count} objects",
                    _ => ""
                };
                EditorGUILayout.LabelField(catLabel, _miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawPresets()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select ALL Tiny + Small", GUILayout.Height(24)))
            {
                foreach (var e in _entries)
                    e.selected = (e.category == SizeCategory.Tiny || e.category == SizeCategory.Small);
                Repaint();
            }
            if (GUILayout.Button("Select Overscaled Only", GUILayout.Height(24)))
            {
                foreach (var e in _entries)
                    e.selected = (e.currentScale > e.recommendedScale * 1.5f);
                Repaint();
            }
            if (GUILayout.Button("Select 'Should Disable GI'", GUILayout.Height(24)))
            {
                foreach (var e in _entries)
                    e.selected = e.shouldDisableGI;
                Repaint();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Set Selected → Scale 0"))
            {
                SetScaleForSelected(0f);
            }
            if (GUILayout.Button("Set Selected → Scale 0.1"))
            {
                SetScaleForSelected(0.1f);
            }
            if (GUILayout.Button("Set Selected → Scale 0.3"))
            {
                SetScaleForSelected(0.3f);
            }
            if (GUILayout.Button("Set Selected → Scale 0.5"))
            {
                SetScaleForSelected(0.5f);
            }
            if (GUILayout.Button("Set Selected → Scale 1.0"))
            {
                SetScaleForSelected(1.0f);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            // Custom scale
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Custom Scale:", GUILayout.Width(90));
            float customScale = EditorGUILayout.Slider(0.1f, 0f, 3f);
            if (GUILayout.Button("Apply to Selected", GUILayout.Width(120)))
            {
                SetScaleForSelected(customScale);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawFiltersAndSort()
        {
            EditorGUILayout.BeginHorizontal(_boxStyle);

            // Size category toggles
            GUI.color = TinyColor;
            _showTiny = GUILayout.Toggle(_showTiny, $"Tiny({Count(SizeCategory.Tiny)})", "Button", GUILayout.Width(65));
            GUI.color = SmallColor;
            _showSmall = GUILayout.Toggle(_showSmall, $"Small({Count(SizeCategory.Small)})", "Button", GUILayout.Width(72));
            GUI.color = MediumColor;
            _showMedium = GUILayout.Toggle(_showMedium, $"Med({Count(SizeCategory.Medium)})", "Button", GUILayout.Width(60));
            GUI.color = LargeColor;
            _showLarge = GUILayout.Toggle(_showLarge, $"Large({Count(SizeCategory.Large)})", "Button", GUILayout.Width(68));
            GUI.color = HugeColor;
            _showHuge = GUILayout.Toggle(_showHuge, $"Huge({Count(SizeCategory.Huge)})", "Button", GUILayout.Width(64));
            GUI.color = Color.white;

            _showOnlyOverscaled = GUILayout.Toggle(_showOnlyOverscaled, "⚠Over", "Button", GUILayout.Width(52));

            // Sort
            EditorGUILayout.LabelField("Sort:", GUILayout.Width(30));
            _sortMode = (SortMode)EditorGUILayout.EnumPopup(_sortMode, GUILayout.Width(85));
            if (GUILayout.Button(_sortDescending ? "↓" : "↑", GUILayout.Width(22)))
                _sortDescending = !_sortDescending;

            EditorGUILayout.EndHorizontal();

            _searchFilter = EditorGUILayout.TextField("🔍 Search:", _searchFilter);
        }

        private int Count(SizeCategory cat) => _entries.Count(e => e.category == cat);

        private void DrawTableHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("☑", EditorStyles.miniLabel, GUILayout.Width(22));
            EditorGUILayout.LabelField("Cat", EditorStyles.miniLabel, GUILayout.Width(34));
            EditorGUILayout.LabelField("Object Name", EditorStyles.miniLabel, GUILayout.Width(180));
            EditorGUILayout.LabelField("Size (m)", EditorStyles.miniLabel, GUILayout.Width(70));
            EditorGUILayout.LabelField("Tris", EditorStyles.miniLabel, GUILayout.Width(55));
            EditorGUILayout.LabelField("Current", EditorStyles.miniLabel, GUILayout.Width(52));
            EditorGUILayout.LabelField("→ Rec.", EditorStyles.miniLabel, GUILayout.Width(46));
            EditorGUILayout.LabelField("Texel Cost", EditorStyles.miniLabel, GUILayout.Width(70));
            EditorGUILayout.LabelField("Action", EditorStyles.miniLabel, GUILayout.Width(80));
            EditorGUILayout.LabelField("", GUILayout.Width(28)); // ping
            EditorGUILayout.EndHorizontal();
        }

        private void DrawEntries()
        {
            var filtered = GetFilteredAndSorted();

            // Select all/none in filtered
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("☑ All", EditorStyles.miniButtonLeft, GUILayout.Width(50)))
                filtered.ForEach(e => e.selected = true);
            if (GUILayout.Button("☐ None", EditorStyles.miniButtonMid, GUILayout.Width(55)))
                filtered.ForEach(e => e.selected = false);
            if (GUILayout.Button("↔ Invert", EditorStyles.miniButtonRight, GUILayout.Width(55)))
                filtered.ForEach(e => e.selected = !e.selected);
            EditorGUILayout.LabelField($"  Showing {filtered.Count} / {_entries.Count}", _miniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            foreach (var entry in filtered)
            {
                DrawEntryRow(entry);
            }
        }

        private void DrawEntryRow(ObjectEntry e)
        {
            Color catColor = e.category switch
            {
                SizeCategory.Tiny => TinyColor,
                SizeCategory.Small => SmallColor,
                SizeCategory.Medium => MediumColor,
                SizeCategory.Large => LargeColor,
                SizeCategory.Huge => HugeColor,
                _ => Color.white
            };

            // Highlight overscaled
            bool overscaled = e.currentScale > e.recommendedScale * 1.5f && e.recommendedScale < e.currentScale;

            var prevBg = GUI.backgroundColor;
            if (overscaled)
                GUI.backgroundColor = new Color(1f, 0.85f, 0.6f, 0.9f);
            else if (e.shouldDisableGI)
                GUI.backgroundColor = new Color(0.7f, 0.85f, 1f, 0.9f);

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUI.backgroundColor = prevBg;

            // Checkbox
            e.selected = EditorGUILayout.Toggle(e.selected, GUILayout.Width(18));

            // Category dot
            GUI.color = catColor;
            string catIcon = e.category switch
            {
                SizeCategory.Tiny => "●",
                SizeCategory.Small => "●",
                SizeCategory.Medium => "●",
                SizeCategory.Large => "●",
                SizeCategory.Huge => "●",
                _ => "○"
            };
            EditorGUILayout.LabelField(catIcon, _boldLabel, GUILayout.Width(16));
            GUI.color = Color.white;

            // Name
            string displayName = e.name.Length > 24 ? e.name.Substring(0, 22) + ".." : e.name;
            EditorGUILayout.LabelField(displayName, GUILayout.Width(178));

            // Size
            string sizeStr;
            if (e.maxDimension < 0.01f)
                sizeStr = $"{e.maxDimension * 100f:F1}cm";
            else if (e.maxDimension < 1f)
                sizeStr = $"{e.maxDimension:F2}m";
            else
                sizeStr = $"{e.maxDimension:F1}m";
            EditorGUILayout.LabelField(sizeStr, GUILayout.Width(70));

            // Tris
            string triStr = e.triangleCount > 1000 ? $"{e.triangleCount / 1000f:F1}K" : $"{e.triangleCount}";
            EditorGUILayout.LabelField(triStr, GUILayout.Width(55));

            // Current scale
            if (overscaled)
                GUI.color = new Color(1f, 0.5f, 0.2f);
            EditorGUILayout.LabelField($"{e.currentScale:F2}", GUILayout.Width(48));
            GUI.color = Color.white;

            // Recommended
            GUI.color = new Color(0.3f, 0.85f, 0.3f);
            string recStr = e.shouldDisableGI ? "OFF" : $"{e.recommendedScale:F2}";
            EditorGUILayout.LabelField($"→ {recStr}", GUILayout.Width(46));
            GUI.color = Color.white;

            // Texel cost
            string texelStr = FormatTexels(e.estimatedTexels);
            float pct = e.texelPercentOfAtlas;
            string costStr = pct < 0.1f ? $"{texelStr}" : $"{texelStr} ({pct:F1}%)";
            EditorGUILayout.LabelField(costStr, _miniLabel, GUILayout.Width(70));

            // Quick action
            if (e.shouldDisableGI)
            {
                if (GUILayout.Button("→ Probes", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    DisableGISingle(e);
                }
            }
            else if (overscaled)
            {
                if (GUILayout.Button($"→ {e.recommendedScale:F1}", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    ApplyScaleSingle(e, e.recommendedScale);
                }
            }
            else
            {
                EditorGUILayout.LabelField("OK", EditorStyles.miniLabel, GUILayout.Width(60));
            }

            // Ping
            if (GUILayout.Button("📍", GUILayout.Width(26), GUILayout.Height(16)))
            {
                Selection.activeGameObject = e.gameObject;
                EditorGUIUtility.PingObject(e.gameObject);
                SceneView.lastActiveSceneView?.FrameSelected();
            }

            EditorGUILayout.EndHorizontal();
        }

        // ══════════════════════════════════════════════
        // SCAN
        // ══════════════════════════════════════════════
        private void ScanScene()
        {
            _entries.Clear();
            _hasScanned = true;

            // Lấy lightmap resolution từ Lighting Settings
            var lightingSettings = Lightmapping.lightingSettings;
            if (lightingSettings != null)
            {
                _lightmapResolution = lightingSettings.lightmapResolution;
            }

            var meshRenderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
            int progress = 0;

            foreach (var mr in meshRenderers)
            {
                progress++;
                if (progress % 200 == 0)
                    EditorUtility.DisplayProgressBar("Scanning...", $"{progress}/{meshRenderers.Length}", (float)progress / meshRenderers.Length);

                var go = mr.gameObject;
                var flags = GameObjectUtility.GetStaticEditorFlags(go);
                if (!flags.HasFlag(StaticEditorFlags.ContributeGI)) continue;

                var mf = go.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                var mesh = mf.sharedMesh;
                var bounds = mr.bounds;

                var entry = new ObjectEntry
                {
                    renderer = mr,
                    gameObject = go,
                    name = go.name,
                    mesh = mesh,
                    boundsSize = bounds.size,
                    maxDimension = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z),
                    triangleCount = mesh.triangles.Length / 3,
                    currentScale = mr.scaleInLightmap,
                };

                // Ước tính surface area (dùng bounds, không chính xác 100% nhưng đủ)
                var s = bounds.size;
                entry.surfaceArea = 2f * (s.x * s.y + s.y * s.z + s.x * s.z);

                // Classify
                entry.category = ClassifySize(entry.maxDimension);

                // Recommend scale
                entry.recommendedScale = GetRecommendedScale(entry);
                entry.shouldDisableGI = ShouldDisableGI(entry);

                // Estimate texel cost
                // Texels ≈ surfaceArea × lightmapResolution² × scaleInLightmap
                entry.estimatedTexels = entry.surfaceArea * _lightmapResolution * _lightmapResolution * entry.currentScale;
                float atlasTexels = _atlasSize * _atlasSize;
                entry.texelPercentOfAtlas = (entry.estimatedTexels / atlasTexels) * 100f;

                _entries.Add(entry);
            }

            EditorUtility.ClearProgressBar();

            // Calc stats
            _totalObjects = _entries.Count;
            _totalTexelsBefore = _entries.Sum(e => e.estimatedTexels);
            _totalTexelsAfter = _entries.Sum(e =>
            {
                if (e.shouldDisableGI) return 0;
                return e.surfaceArea * _lightmapResolution * _lightmapResolution * e.recommendedScale;
            });
            _overscaledCount = _entries.Count(e => e.currentScale > e.recommendedScale * 1.5f && e.recommendedScale < e.currentScale);

            Debug.Log($"[LM Scale Opt] Scanned {_totalObjects} static objects. " +
                      $"Overscaled: {_overscaledCount}. " +
                      $"Potential savings: {(_totalTexelsBefore > 0 ? (1f - _totalTexelsAfter / _totalTexelsBefore) * 100f : 0):F0}%");
        }

        private SizeCategory ClassifySize(float maxDim)
        {
            if (maxDim < 0.3f) return SizeCategory.Tiny;
            if (maxDim < 1.0f) return SizeCategory.Small;
            if (maxDim < 3.0f) return SizeCategory.Medium;
            if (maxDim < 10.0f) return SizeCategory.Large;
            return SizeCategory.Huge;
        }

        private float GetRecommendedScale(ObjectEntry e)
        {
            // Logic đề xuất dựa trên kích thước và mục đích
            return e.category switch
            {
                // Tiny: hầu như không cần lightmap, dùng probes
                SizeCategory.Tiny => 0f,

                // Small: scale rất thấp, chỉ cần vài texel
                SizeCategory.Small => 0.1f,

                // Medium: props trung bình, scale vừa
                SizeCategory.Medium => 0.3f,

                // Large: tường, sàn — cần scale đàng hoàng
                SizeCategory.Large => 0.8f,

                // Huge: terrain, building lớn — giữ scale cao
                SizeCategory.Huge => 1.0f,

                _ => 1.0f
            };
        }

        private bool ShouldDisableGI(ObjectEntry e)
        {
            // Tiny objects nên chuyển sang Light Probes
            if (e.category == SizeCategory.Tiny) return true;

            // Objects rất nhỏ mà scale đang cao = waste
            if (e.maxDimension < 0.5f && e.currentScale >= 0.5f) return true;

            // Cloned objects (thường là vegetation, particles, decor lặp)
            if (e.name.Contains("(Clone)") && e.category <= SizeCategory.Small) return true;

            return false;
        }

        // ══════════════════════════════════════════════
        // ACTIONS
        // ══════════════════════════════════════════════
        private void ApplyRecommendedToSelected()
        {
            var selected = _entries.Where(e => e.selected).ToList();
            if (selected.Count == 0) return;

            int disableCount = selected.Count(e => e.shouldDisableGI);
            int scaleCount = selected.Count(e => !e.shouldDisableGI);

            if (!EditorUtility.DisplayDialog("Apply Recommended",
                $"Sẽ thay đổi {selected.Count} objects:\n" +
                $"• {scaleCount} set Scale In Lightmap theo recommended\n" +
                $"• {disableCount} tắt ContributeGI (chuyển sang Light Probes)\n\n" +
                "Tiếp tục?", "Apply", "Cancel"))
                return;

            Undo.SetCurrentGroupName("Lightmap Scale Optimize");

            foreach (var e in selected)
            {
                if (e.shouldDisableGI)
                {
                    DisableGISingle(e);
                }
                else
                {
                    ApplyScaleSingle(e, e.recommendedScale);
                }
            }

            RecalcStats();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log($"[LM Scale Opt] Applied recommended settings to {selected.Count} objects.");
        }

        private void DisableGIForSelected()
        {
            var selected = _entries.Where(e => e.selected && e.shouldDisableGI).ToList();
            if (selected.Count == 0) return;

            Undo.SetCurrentGroupName("Disable GI for small objects");

            foreach (var e in selected)
            {
                DisableGISingle(e);
            }

            RecalcStats();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        private void SetScaleForSelected(float scale)
        {
            var selected = _entries.Where(e => e.selected).ToList();
            if (selected.Count == 0) return;

            Undo.SetCurrentGroupName($"Set LM Scale to {scale:F1}");

            foreach (var e in selected)
            {
                ApplyScaleSingle(e, scale);
            }

            RecalcStats();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        private void ApplyScaleSingle(ObjectEntry e, float scale)
        {
            if (e.renderer == null) return;

            Undo.RecordObject(e.renderer, "Change scaleInLightmap");

            // scaleInLightmap is set via SerializedObject
            var so = new SerializedObject(e.renderer);
            var prop = so.FindProperty("m_ScaleInLightmap");
            if (prop != null)
            {
                prop.floatValue = scale;
                so.ApplyModifiedProperties();
            }

            e.currentScale = scale;
            e.estimatedTexels = e.surfaceArea * _lightmapResolution * _lightmapResolution * scale;
            e.texelPercentOfAtlas = (e.estimatedTexels / (_atlasSize * _atlasSize)) * 100f;
        }

        private void DisableGISingle(ObjectEntry e)
        {
            if (e.gameObject == null) return;

            Undo.RecordObject(e.gameObject, "Disable ContributeGI");

            var flags = GameObjectUtility.GetStaticEditorFlags(e.gameObject);
            flags &= ~StaticEditorFlags.ContributeGI;
            GameObjectUtility.SetStaticEditorFlags(e.gameObject, flags);

            // Also set scale to 0
            ApplyScaleSingle(e, 0f);
            e.shouldDisableGI = false; // Đã disable
        }

        private void RecalcStats()
        {
            _totalTexelsBefore = _entries.Sum(e => e.surfaceArea * _lightmapResolution * _lightmapResolution *
                (e.currentScale > 0 ? e.currentScale : 0));
            _totalTexelsAfter = _entries.Sum(e =>
            {
                if (e.shouldDisableGI) return 0;
                return e.surfaceArea * _lightmapResolution * _lightmapResolution * e.recommendedScale;
            });
            _overscaledCount = _entries.Count(e => e.currentScale > e.recommendedScale * 1.5f && e.recommendedScale < e.currentScale);
        }

        // ══════════════════════════════════════════════
        // FILTER & SORT
        // ══════════════════════════════════════════════
        private List<ObjectEntry> GetFilteredAndSorted()
        {
            var list = _entries.Where(e =>
            {
                if (e.category == SizeCategory.Tiny && !_showTiny) return false;
                if (e.category == SizeCategory.Small && !_showSmall) return false;
                if (e.category == SizeCategory.Medium && !_showMedium) return false;
                if (e.category == SizeCategory.Large && !_showLarge) return false;
                if (e.category == SizeCategory.Huge && !_showHuge) return false;

                if (_showOnlyOverscaled && !(e.currentScale > e.recommendedScale * 1.5f)) return false;

                if (!string.IsNullOrEmpty(_searchFilter) &&
                    !e.name.ToLower().Contains(_searchFilter.ToLower()))
                    return false;

                return true;
            }).ToList();

            // Sort
            list.Sort((a, b) =>
            {
                int cmp = _sortMode switch
                {
                    SortMode.Size => a.maxDimension.CompareTo(b.maxDimension),
                    SortMode.TexelCost => a.estimatedTexels.CompareTo(b.estimatedTexels),
                    SortMode.Name => string.Compare(a.name, b.name, StringComparison.Ordinal),
                    SortMode.CurrentScale => a.currentScale.CompareTo(b.currentScale),
                    SortMode.TriCount => a.triangleCount.CompareTo(b.triangleCount),
                    _ => 0
                };
                return _sortDescending ? -cmp : cmp;
            });

            return list;
        }

        // ══════════════════════════════════════════════
        // UTILS
        // ══════════════════════════════════════════════
        private static string FormatTexels(float texels)
        {
            if (texels >= 1_000_000) return $"{texels / 1_000_000f:F1}M";
            if (texels >= 1_000) return $"{texels / 1_000f:F1}K";
            return $"{texels:F0}";
        }
    }
}
#endif
