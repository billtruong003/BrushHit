using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.Linq;

namespace CleanRender
{
    public class CleanRenderMaterialManager : EditorWindow
    {
        // ════════════════════════════════════════════════════════════════
        // Data Structures
        // ════════════════════════════════════════════════════════════════

        private class MaterialEntry
        {
            public Material material;
            public Shader shader;
            public string shaderName;
            public List<Renderer> renderers = new List<Renderer>();
            public bool selected;
            public bool expanded; // inline edit foldout
        }

        private class ShaderGroup
        {
            public Shader shader;
            public string shaderName;
            public List<MaterialEntry> entries = new List<MaterialEntry>();
            public bool foldout = true;
            public bool selectAll;
            public int totalRenderers;
        }

        // ════════════════════════════════════════════════════════════════
        // State
        // ════════════════════════════════════════════════════════════════

        private List<ShaderGroup> _shaderGroups = new List<ShaderGroup>();
        private Dictionary<Material, MaterialEntry> _materialMap = new Dictionary<Material, MaterialEntry>();
        private Vector2 _scrollPos;
        private Vector2 _detailScroll;

        // Filter / Search
        private string _searchFilter = "";
        private bool _showOnlyCleanRender = false;

        // Batch operations
        private Shader _targetShader;
        private int _selectedCount;
        private bool _showBatchPanel = true;
        private bool _showStatsPanel = true;

        // Stats
        private int _totalMaterials;
        private int _totalRenderers;
        private int _totalShaders;
        private Dictionary<string, int> _shaderUsageCount = new Dictionary<string, int>();

        // Known CleanRender shaders for quick switch
        private static readonly string[] KnownShaders = new string[]
        {
            "CleanRender/ToonLit",
            "CleanRender/ToonMetal",
            "CleanRender/ToonFoliage",
            "CleanRender/ToonBark",
            "CleanRender/ToonTerrain",
            "CleanRender/ToonGrass",
            "CleanRender/ToonLava",
            "CleanRender/CaveFog",
            "CleanRender/SimpleText",
            "VR/StylizedWater",
        };

        // Styles
        private static GUIStyle _headerStyle;
        private static GUIStyle _groupBoxStyle;
        private static GUIStyle _miniButtonStyle;
        private static GUIStyle _statsLabelStyle;
        private static GUIStyle _richLabel;
        private static bool _stylesInit;

        // Colors
        private static readonly Color AccentBlue = new Color(0.3f, 0.7f, 1f, 1f);
        private static readonly Color AccentGreen = new Color(0.4f, 0.85f, 0.4f, 1f);
        private static readonly Color AccentOrange = new Color(1f, 0.6f, 0.2f, 1f);
        private static readonly Color AccentRed = new Color(1f, 0.4f, 0.35f, 1f);
        private static readonly Color BgDark = new Color(0.18f, 0.18f, 0.18f, 1f);
        private static readonly Color BgMid = new Color(0.22f, 0.22f, 0.22f, 1f);
        private static readonly Color BgLight = new Color(0.28f, 0.28f, 0.28f, 1f);

        // ════════════════════════════════════════════════════════════════
        // Window Setup
        // ════════════════════════════════════════════════════════════════

        [MenuItem("Tools/CleanRender/Material Manager %#m")]
        public static void ShowWindow()
        {
            var window = GetWindow<CleanRenderMaterialManager>("Material Manager");
            window.minSize = new Vector2(480, 600);
            window.Show();
        }

        private void OnEnable()
        {
            RefreshMaterialList();
        }

        private static void InitStyles()
        {
            if (_stylesInit) return;
            _stylesInit = true;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                richText = true
            };

            _groupBoxStyle = new GUIStyle("box")
            {
                padding = new RectOffset(8, 8, 4, 4),
                margin = new RectOffset(2, 2, 2, 2)
            };

            _miniButtonStyle = new GUIStyle(EditorStyles.miniButton)
            {
                fixedHeight = 20,
                fontSize = 10
            };

            _statsLabelStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                richText = true,
                wordWrap = true
            };

            _richLabel = new GUIStyle(EditorStyles.label)
            {
                richText = true,
                wordWrap = false
            };
        }

        // ════════════════════════════════════════════════════════════════
        // Scan Scene
        // ════════════════════════════════════════════════════════════════

        private void RefreshMaterialList()
        {
            _shaderGroups.Clear();
            _materialMap.Clear();
            _shaderUsageCount.Clear();
            _selectedCount = 0;

            // Find all renderers in scene (including inactive)
            var allRenderers = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            Dictionary<Shader, ShaderGroup> groupMap = new Dictionary<Shader, ShaderGroup>();

            foreach (var renderer in allRenderers)
            {
                if (renderer == null) continue;
                var sharedMats = renderer.sharedMaterials;
                if (sharedMats == null) continue;

                foreach (var mat in sharedMats)
                {
                    if (mat == null) continue;

                    if (!_materialMap.TryGetValue(mat, out MaterialEntry entry))
                    {
                        entry = new MaterialEntry
                        {
                            material = mat,
                            shader = mat.shader,
                            shaderName = mat.shader != null ? mat.shader.name : "(null)"
                        };
                        _materialMap[mat] = entry;

                        // Add to shader group
                        if (mat.shader != null)
                        {
                            if (!groupMap.TryGetValue(mat.shader, out ShaderGroup group))
                            {
                                group = new ShaderGroup
                                {
                                    shader = mat.shader,
                                    shaderName = mat.shader.name
                                };
                                groupMap[mat.shader] = group;
                            }
                            group.entries.Add(entry);
                        }
                    }

                    entry.renderers.Add(renderer);
                }
            }

            // Sort groups: CleanRender first, then alphabetical
            _shaderGroups = groupMap.Values
                .OrderByDescending(g => g.shaderName.StartsWith("CleanRender") || g.shaderName.StartsWith("VR/"))
                .ThenBy(g => g.shaderName)
                .ToList();

            // Compute stats
            foreach (var group in _shaderGroups)
            {
                group.totalRenderers = group.entries.Sum(e => e.renderers.Count);
                _shaderUsageCount[group.shaderName] = group.totalRenderers;
            }

            _totalMaterials = _materialMap.Count;
            _totalRenderers = allRenderers.Length;
            _totalShaders = _shaderGroups.Count;
        }

        // ════════════════════════════════════════════════════════════════
        // GUI
        // ════════════════════════════════════════════════════════════════

        private void OnGUI()
        {
            InitStyles();

            // ── Top Banner ──
            DrawBanner();

            // ── Toolbar ──
            DrawToolbar();

            // ── Stats Panel ──
            if (_showStatsPanel)
                DrawStatsPanel();

            // ── Batch Operations Panel ──
            if (_showBatchPanel)
                DrawBatchPanel();

            // ── Material List ──
            EditorGUILayout.Space(4);
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            DrawMaterialList();
            EditorGUILayout.EndScrollView();
        }

        // ── Banner ──────────────────────────────────────────────────────

        private void DrawBanner()
        {
            Rect r = GUILayoutUtility.GetRect(1f, 32f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(0.12f, 0.12f, 0.15f, 1f));

            Rect accentLine = new Rect(r.x, r.yMax - 2f, r.width, 2f);
            EditorGUI.DrawRect(accentLine, AccentBlue);

            GUIStyle bannerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = AccentBlue }
            };
            EditorGUI.LabelField(r, "CLEANRENDER — MATERIAL MANAGER", bannerStyle);
        }

        // ── Toolbar ─────────────────────────────────────────────────────

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
                RefreshMaterialList();

            GUILayout.Space(4);

            _showOnlyCleanRender = GUILayout.Toggle(_showOnlyCleanRender, "CleanRender Only",
                EditorStyles.toolbarButton, GUILayout.Width(110));

            GUILayout.Space(4);

            _showStatsPanel = GUILayout.Toggle(_showStatsPanel, "Stats",
                EditorStyles.toolbarButton, GUILayout.Width(50));

            _showBatchPanel = GUILayout.Toggle(_showBatchPanel, "Batch Ops",
                EditorStyles.toolbarButton, GUILayout.Width(70));

            GUILayout.FlexibleSpace();

            // Search
            EditorGUILayout.LabelField("Search:", GUILayout.Width(48));
            _searchFilter = EditorGUILayout.TextField(_searchFilter, EditorStyles.toolbarSearchField,
                GUILayout.Width(180));
            if (GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(20)))
                _searchFilter = "";

            EditorGUILayout.EndHorizontal();
        }

        // ── Stats Panel ─────────────────────────────────────────────────

        private void DrawStatsPanel()
        {
            EditorGUILayout.BeginVertical(_groupBoxStyle);

            EditorGUILayout.LabelField("Scene Overview", _headerStyle);
            EditorGUILayout.BeginHorizontal();

            DrawStatBox("Materials", _totalMaterials.ToString(), AccentBlue);
            DrawStatBox("Shaders", _totalShaders.ToString(), AccentGreen);
            DrawStatBox("Renderers", _totalRenderers.ToString(), AccentOrange);
            DrawStatBox("Selected", _selectedCount.ToString(), AccentRed);

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawStatBox(string label, string value, Color color)
        {
            EditorGUILayout.BeginVertical(_groupBoxStyle, GUILayout.MinWidth(80));
            var oldColor = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField(value, new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = color }
            });
            GUI.color = oldColor;
            EditorGUILayout.LabelField(label, new GUIStyle(EditorStyles.centeredGreyMiniLabel));
            EditorGUILayout.EndVertical();
        }

        // ── Batch Operations Panel ──────────────────────────────────────

        private void DrawBatchPanel()
        {
            EditorGUILayout.BeginVertical(_groupBoxStyle);

            EditorGUILayout.LabelField("Batch Shader Switch", _headerStyle);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Target Shader:", GUILayout.Width(100));
            _targetShader = (Shader)EditorGUILayout.ObjectField(_targetShader, typeof(Shader), false);
            EditorGUILayout.EndHorizontal();

            // Quick shader buttons
            EditorGUILayout.LabelField("Quick Select:", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            int btnCount = 0;
            foreach (var shaderName in KnownShaders)
            {
                var shader = Shader.Find(shaderName);
                if (shader == null) continue;

                string shortName = shaderName.Contains("/") ?
                    shaderName.Substring(shaderName.LastIndexOf('/') + 1) : shaderName;

                bool isCurrent = _targetShader == shader;
                var oldBg = GUI.backgroundColor;
                if (isCurrent) GUI.backgroundColor = AccentBlue;

                if (GUILayout.Button(shortName, _miniButtonStyle, GUILayout.MinWidth(70)))
                    _targetShader = shader;

                GUI.backgroundColor = oldBg;
                btnCount++;
                if (btnCount % 5 == 0)
                {
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();

            // Select All / None
            if (GUILayout.Button("Select All", _miniButtonStyle))
                SetAllSelected(true);
            if (GUILayout.Button("Select None", _miniButtonStyle))
                SetAllSelected(false);
            if (GUILayout.Button("Invert Selection", _miniButtonStyle))
                InvertSelection();

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            // Apply button
            GUI.enabled = _targetShader != null && _selectedCount > 0;
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = AccentOrange;

            string applyLabel = $"Switch {_selectedCount} Material(s) → {(_targetShader != null ? _targetShader.name : "?")}";
            if (GUILayout.Button(applyLabel, GUILayout.Height(28)))
            {
                if (EditorUtility.DisplayDialog("Batch Shader Switch",
                    $"Switch {_selectedCount} material(s) to \"{_targetShader.name}\"?\n\nThis action supports Undo.",
                    "Switch", "Cancel"))
                {
                    BatchSwitchShader();
                }
            }

            GUI.backgroundColor = prevBg;
            GUI.enabled = true;

            EditorGUILayout.EndVertical();
        }

        // ── Material List ───────────────────────────────────────────────

        private void DrawMaterialList()
        {
            _selectedCount = 0;
            string filter = _searchFilter.ToLowerInvariant();

            foreach (var group in _shaderGroups)
            {
                // Filter: CleanRender only
                if (_showOnlyCleanRender &&
                    !group.shaderName.StartsWith("CleanRender") &&
                    !group.shaderName.StartsWith("VR/"))
                    continue;

                // Filter: search
                bool groupMatchesSearch = string.IsNullOrEmpty(filter) ||
                    group.shaderName.ToLowerInvariant().Contains(filter);

                var visibleEntries = group.entries;
                if (!string.IsNullOrEmpty(filter))
                {
                    visibleEntries = group.entries.Where(e =>
                        e.material.name.ToLowerInvariant().Contains(filter) ||
                        e.shaderName.ToLowerInvariant().Contains(filter)
                    ).ToList();

                    if (visibleEntries.Count == 0 && !groupMatchesSearch) continue;
                    if (visibleEntries.Count == 0) visibleEntries = group.entries;
                }

                // Count selected in this group
                int groupSelected = group.entries.Count(e => e.selected);
                _selectedCount += groupSelected;

                // ── Group Header ──
                DrawShaderGroupHeader(group, visibleEntries.Count, groupSelected);

                if (!group.foldout) continue;

                EditorGUI.indentLevel++;
                foreach (var entry in visibleEntries)
                {
                    DrawMaterialEntry(entry);
                }
                EditorGUI.indentLevel--;

                EditorGUILayout.Space(2);
            }
        }

        private void DrawShaderGroupHeader(ShaderGroup group, int visibleCount, int selectedCount)
        {
            EditorGUILayout.Space(2);
            Rect headerRect = GUILayoutUtility.GetRect(1f, 24f, GUILayout.ExpandWidth(true));

            // Background
            bool isCleanRender = group.shaderName.StartsWith("CleanRender") ||
                                 group.shaderName.StartsWith("VR/");
            Color bgCol = group.foldout ? BgLight : BgMid;
            EditorGUI.DrawRect(headerRect, bgCol);

            // Left accent
            Color accentCol = isCleanRender ? AccentBlue : new Color(0.5f, 0.5f, 0.5f);
            Rect accentRect = new Rect(headerRect.x, headerRect.y, 3f, headerRect.height);
            EditorGUI.DrawRect(accentRect, accentCol);

            // Click to toggle
            Event e = Event.current;
            if (e.type == EventType.MouseDown && headerRect.Contains(e.mousePosition))
            {
                if (e.button == 0)
                {
                    group.foldout = !group.foldout;
                    e.Use();
                    Repaint();
                }
            }

            // Arrow + Shader name
            string arrow = group.foldout ? "▼" : "►";
            string countStr = $"<color=#888888>({visibleCount} mat, {group.totalRenderers} obj)</color>";
            string selectedStr = selectedCount > 0 ? $" <color=#FF9944>[{selectedCount} sel]</color>" : "";

            Rect labelRect = new Rect(headerRect.x + 8, headerRect.y + 2, headerRect.width - 100, headerRect.height);
            EditorGUI.LabelField(labelRect, $"{arrow} {group.shaderName} {countStr}{selectedStr}", _richLabel);

            // Select all toggle for group
            Rect toggleRect = new Rect(headerRect.xMax - 90, headerRect.y + 3, 85, 18);
            EditorGUI.BeginChangeCheck();
            bool allSelected = group.entries.All(en => en.selected);
            bool newAll = EditorGUI.ToggleLeft(toggleRect, "Select All", allSelected);
            if (EditorGUI.EndChangeCheck())
            {
                foreach (var entry in group.entries)
                    entry.selected = newAll;
                Repaint();
            }
        }

        private void DrawMaterialEntry(MaterialEntry entry)
        {
            EditorGUILayout.BeginVertical(_groupBoxStyle);

            EditorGUILayout.BeginHorizontal();

            // Checkbox
            entry.selected = EditorGUILayout.Toggle(entry.selected, GUILayout.Width(18));

            // Material preview
            Rect previewRect = GUILayoutUtility.GetRect(32, 32, GUILayout.Width(32), GUILayout.Height(32));
            var preview = AssetPreview.GetAssetPreview(entry.material);
            if (preview != null)
                GUI.DrawTexture(previewRect, preview, ScaleMode.ScaleToFit);
            else
                EditorGUI.DrawRect(previewRect, new Color(0.3f, 0.3f, 0.3f));

            // Material name + info
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(entry.material.name, EditorStyles.boldLabel);

            string info = $"Shader: {entry.shaderName}  |  Used by {entry.renderers.Count} renderer(s)";
            EditorGUILayout.LabelField(info, EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();

            // Buttons
            EditorGUILayout.BeginVertical(GUILayout.Width(80));

            if (GUILayout.Button("Ping", _miniButtonStyle))
            {
                EditorGUIUtility.PingObject(entry.material);
                Selection.activeObject = entry.material;
            }

            if (GUILayout.Button(entry.expanded ? "▲ Close" : "▼ Edit", _miniButtonStyle))
                entry.expanded = !entry.expanded;

            if (GUILayout.Button("Select Objs", _miniButtonStyle))
            {
                Selection.objects = entry.renderers
                    .Where(r => r != null)
                    .Select(r => r.gameObject)
                    .Distinct()
                    .ToArray();
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            // ── Inline Material Editor ──
            if (entry.expanded)
            {
                EditorGUILayout.Space(4);
                DrawInlineMaterialEditor(entry);
            }

            EditorGUILayout.EndVertical();
        }

        // ── Inline Material Editor ──────────────────────────────────────

        private Dictionary<Material, MaterialEditor> _cachedEditors = new Dictionary<Material, MaterialEditor>();

        private void DrawInlineMaterialEditor(MaterialEntry entry)
        {
            if (entry.material == null) return;

            // Separator
            Rect sep = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(sep, AccentBlue * 0.6f);
            EditorGUILayout.Space(2);

            // Shader field (allows changing shader per-material)
            EditorGUI.BeginChangeCheck();
            Shader newShader = (Shader)EditorGUILayout.ObjectField("Shader", entry.material.shader, typeof(Shader), false);
            if (EditorGUI.EndChangeCheck() && newShader != null)
            {
                Undo.RecordObject(entry.material, "Change Shader");
                entry.material.shader = newShader;
                entry.shader = newShader;
                entry.shaderName = newShader.name;

                // Clear cached editor
                if (_cachedEditors.ContainsKey(entry.material))
                {
                    DestroyImmediate(_cachedEditors[entry.material]);
                    _cachedEditors.Remove(entry.material);
                }

                EditorUtility.SetDirty(entry.material);
            }

            EditorGUILayout.Space(2);

            // Draw all material properties using MaterialEditor
            if (!_cachedEditors.TryGetValue(entry.material, out MaterialEditor matEditor) || matEditor == null)
            {
                matEditor = (MaterialEditor)UnityEditor.Editor.CreateEditor(entry.material, typeof(MaterialEditor));
                _cachedEditors[entry.material] = matEditor;
            }

            if (matEditor != null)
            {
                EditorGUI.BeginChangeCheck();

                // Get all properties
                var shader = entry.material.shader;
                int propCount = ShaderUtil.GetPropertyCount(shader);

                for (int i = 0; i < propCount; i++)
                {
                    string propName = ShaderUtil.GetPropertyName(shader, i);
                    string propDesc = ShaderUtil.GetPropertyDescription(shader, i);

                    // Skip hidden
                    if (shader.GetPropertyFlags(i).HasFlag(UnityEngine.Rendering.ShaderPropertyFlags.HideInInspector)) continue;

                    var propType = ShaderUtil.GetPropertyType(shader, i);

                    switch (propType)
                    {
                        case ShaderUtil.ShaderPropertyType.Color:
                            Color col = entry.material.GetColor(propName);
                            Color newCol = EditorGUILayout.ColorField(new GUIContent(propDesc), col, true, true, true);
                            if (col != newCol) { Undo.RecordObject(entry.material, "Edit Material"); entry.material.SetColor(propName, newCol); }
                            break;

                        case ShaderUtil.ShaderPropertyType.Float:
                        case ShaderUtil.ShaderPropertyType.Range:
                            float val = entry.material.GetFloat(propName);
                            float newVal;
                            if (propType == ShaderUtil.ShaderPropertyType.Range)
                            {
                                float min = ShaderUtil.GetRangeLimits(shader, i, 1);
                                float max = ShaderUtil.GetRangeLimits(shader, i, 2);
                                newVal = EditorGUILayout.Slider(propDesc, val, min, max);
                            }
                            else
                            {
                                newVal = EditorGUILayout.FloatField(propDesc, val);
                            }
                            if (!Mathf.Approximately(val, newVal)) { Undo.RecordObject(entry.material, "Edit Material"); entry.material.SetFloat(propName, newVal); }
                            break;

                        case ShaderUtil.ShaderPropertyType.Vector:
                            Vector4 vec = entry.material.GetVector(propName);
                            Vector4 newVec = EditorGUILayout.Vector4Field(propDesc, vec);
                            if (vec != newVec) { Undo.RecordObject(entry.material, "Edit Material"); entry.material.SetVector(propName, newVec); }
                            break;

                        case ShaderUtil.ShaderPropertyType.TexEnv:
                            Texture tex = entry.material.GetTexture(propName);
                            Texture newTex = (Texture)EditorGUILayout.ObjectField(propDesc, tex, typeof(Texture), false);
                            if (tex != newTex) { Undo.RecordObject(entry.material, "Edit Material"); entry.material.SetTexture(propName, newTex); }
                            break;
                    }
                }

                if (EditorGUI.EndChangeCheck())
                {
                    EditorUtility.SetDirty(entry.material);
                }
            }

            EditorGUILayout.Space(4);

            // Render queue
            EditorGUI.BeginChangeCheck();
            int rq = EditorGUILayout.IntField("Render Queue", entry.material.renderQueue);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(entry.material, "Change Render Queue");
                entry.material.renderQueue = rq;
            }

            // GPU Instancing toggle
            EditorGUI.BeginChangeCheck();
            bool gpuInst = EditorGUILayout.Toggle("GPU Instancing", entry.material.enableInstancing);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(entry.material, "Toggle GPU Instancing");
                entry.material.enableInstancing = gpuInst;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Batch Operations
        // ════════════════════════════════════════════════════════════════

        private void BatchSwitchShader()
        {
            if (_targetShader == null) return;

            var selected = _materialMap.Values.Where(e => e.selected).ToList();
            if (selected.Count == 0) return;

            Undo.SetCurrentGroupName($"Batch Switch to {_targetShader.name}");
            int undoGroup = Undo.GetCurrentGroup();

            foreach (var entry in selected)
            {
                if (entry.material == null) continue;
                if (entry.material.shader == _targetShader) continue;

                Undo.RecordObject(entry.material, "Switch Shader");

                // Try to preserve common properties
                var oldProps = CaptureCommonProperties(entry.material);

                entry.material.shader = _targetShader;

                // Re-apply preserved properties
                ApplyCommonProperties(entry.material, oldProps);

                // Enable GPU instancing for opaque CleanRender shaders
                if (_targetShader.name.StartsWith("CleanRender/Toon"))
                    entry.material.enableInstancing = true;

                EditorUtility.SetDirty(entry.material);

                // Update entry
                entry.shader = _targetShader;
                entry.shaderName = _targetShader.name;

                // Clear cached editor
                if (_cachedEditors.ContainsKey(entry.material))
                {
                    DestroyImmediate(_cachedEditors[entry.material]);
                    _cachedEditors.Remove(entry.material);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);

            // Refresh grouping
            RefreshMaterialList();
        }

        // ── Property Preservation ───────────────────────────────────────

        private struct CommonProps
        {
            public Color? baseColor;
            public Texture baseMap;
            public Color? shadowColor;
            public float? threshold;
            public float? smoothness;
            public float? cutoff;
            public Color? rimColor;
            public float? rimPower;
            public Color? emissionColor;
            public Texture emissionMap;
        }

        private CommonProps CaptureCommonProperties(Material mat)
        {
            var props = new CommonProps();
            var shader = mat.shader;

            // Try common property names across CleanRender shaders
            if (mat.HasProperty("_BaseColor")) props.baseColor = mat.GetColor("_BaseColor");
            if (mat.HasProperty("_BaseMap")) props.baseMap = mat.GetTexture("_BaseMap");
            if (mat.HasProperty("_ShadowColor")) props.shadowColor = mat.GetColor("_ShadowColor");
            if (mat.HasProperty("_Threshold")) props.threshold = mat.GetFloat("_Threshold");
            if (mat.HasProperty("_Smoothness")) props.smoothness = mat.GetFloat("_Smoothness");
            if (mat.HasProperty("_Cutoff")) props.cutoff = mat.GetFloat("_Cutoff");
            if (mat.HasProperty("_RimColor")) props.rimColor = mat.GetColor("_RimColor");
            if (mat.HasProperty("_RimPower")) props.rimPower = mat.GetFloat("_RimPower");
            if (mat.HasProperty("_EmissionColor")) props.emissionColor = mat.GetColor("_EmissionColor");
            if (mat.HasProperty("_EmissionMap")) props.emissionMap = mat.GetTexture("_EmissionMap");

            return props;
        }

        private void ApplyCommonProperties(Material mat, CommonProps props)
        {
            if (props.baseColor.HasValue && mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", props.baseColor.Value);
            if (props.baseMap != null && mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", props.baseMap);
            if (props.shadowColor.HasValue && mat.HasProperty("_ShadowColor"))
                mat.SetColor("_ShadowColor", props.shadowColor.Value);
            if (props.threshold.HasValue && mat.HasProperty("_Threshold"))
                mat.SetFloat("_Threshold", props.threshold.Value);
            if (props.smoothness.HasValue && mat.HasProperty("_Smoothness"))
                mat.SetFloat("_Smoothness", props.smoothness.Value);
            if (props.cutoff.HasValue && mat.HasProperty("_Cutoff"))
                mat.SetFloat("_Cutoff", props.cutoff.Value);
            if (props.rimColor.HasValue && mat.HasProperty("_RimColor"))
                mat.SetColor("_RimColor", props.rimColor.Value);
            if (props.rimPower.HasValue && mat.HasProperty("_RimPower"))
                mat.SetFloat("_RimPower", props.rimPower.Value);
            if (props.emissionColor.HasValue && mat.HasProperty("_EmissionColor"))
                mat.SetColor("_EmissionColor", props.emissionColor.Value);
            if (props.emissionMap != null && mat.HasProperty("_EmissionMap"))
                mat.SetTexture("_EmissionMap", props.emissionMap);
        }

        // ════════════════════════════════════════════════════════════════
        // Selection Helpers
        // ════════════════════════════════════════════════════════════════

        private void SetAllSelected(bool value)
        {
            foreach (var entry in _materialMap.Values)
                entry.selected = value;
            Repaint();
        }

        private void InvertSelection()
        {
            foreach (var entry in _materialMap.Values)
                entry.selected = !entry.selected;
            Repaint();
        }

        // ════════════════════════════════════════════════════════════════
        // Cleanup
        // ════════════════════════════════════════════════════════════════

        private void OnDisable()
        {
            foreach (var editor in _cachedEditors.Values)
            {
                if (editor != null)
                    DestroyImmediate(editor);
            }
            _cachedEditors.Clear();
        }
    }
}