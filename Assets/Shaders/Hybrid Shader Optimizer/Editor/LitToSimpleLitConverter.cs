// ============================================================================
// Lit → Simple Lit Converter — Editor Tool
// Tech Art Department | v1.0 | March 2026
//
// Batch chuyển materials từ URP Lit sang Simple Lit để tối ưu cho Quest/Mobile.
// Simple Lit dùng Blinn-Phong thay vì PBR → rẻ hơn đáng kể trên mobile GPU.
//
// Tự động map properties: Albedo, Normal, Emission, Alpha, Smoothness, Specular.
// Metallic workflow → Specular workflow conversion.
//
// Usage: Window > Tech Art > Lit to Simple Lit
// ============================================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace TechArt.Tools
{
    public class LitToSimpleLitConverter : EditorWindow
    {
        // ── Data ──
        private class MaterialEntry
        {
            public Material material;
            public string name;
            public string assetPath;
            public string currentShader;
            public bool selected = true;
            public bool converted = false;
            public string note = "";

            // Preview info
            public int usageCount;          // Bao nhiêu renderers dùng material này
            public bool hasNormalMap;
            public bool hasEmission;
            public bool hasMetallicMap;
            public bool hasSpecularMap;
            public float metallic;
            public float smoothness;
            public Color baseColor;
            public RenderMode renderMode;

            // Backup
            public bool backupCreated = false;
            public string backupPath = "";
        }

        private enum RenderMode { Opaque, Cutout, Transparent, Unknown }
        private enum ScanScope { Scene, Project, Selection }
        private enum SpecularSource { FromMetallic, CustomColor, White }

        // ── State ──
        private List<MaterialEntry> _entries = new List<MaterialEntry>();
        private Vector2 _scrollPos;
        private bool _hasScanned = false;
        private int _convertedCount = 0;
        private int _failedCount = 0;

        // Options
        private ScanScope _scanScope = ScanScope.Scene;
        private bool _createBackup = true;
        private SpecularSource _specularSource = SpecularSource.FromMetallic;
        private Color _customSpecularColor = new Color(0.5f, 0.5f, 0.5f, 1f);
        private float _specularStrength = 0.5f;
        private bool _keepNormalMaps = true;
        private bool _keepEmission = true;
        private bool _showAdvanced = false;
        private bool _showPreview = true;

        // Filters
        private string _searchFilter = "";
        private bool _showConverted = true;

        // Target shader reference
        private Shader _simpleLitShader;
        private Shader _litShader;

        // Styles
        private GUIStyle _headerStyle, _boxStyle, _boldLabel, _miniLabel;
        private bool _stylesInit;

        [MenuItem("Window/Tech Art/Lit to Simple Lit")]
        public static void ShowWindow()
        {
            var wnd = GetWindow<LitToSimpleLitConverter>("Lit→SimpleLit");
            wnd.minSize = new Vector2(600, 420);
        }

        private void OnEnable()
        {
            _simpleLitShader = Shader.Find("Universal Render Pipeline/Simple Lit");
            _litShader = Shader.Find("Universal Render Pipeline/Lit");
        }

        private void InitStyles()
        {
            if (_stylesInit) return;
            _headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14, margin = new RectOffset(4, 4, 8, 4) };
            _boxStyle = new GUIStyle("box") { padding = new RectOffset(8, 8, 6, 6), margin = new RectOffset(4, 4, 2, 2) };
            _boldLabel = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold };
            _miniLabel = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            _stylesInit = true;
        }

        // ══════════════════════════════════════════════
        // GUI
        // ══════════════════════════════════════════════
        private void OnGUI()
        {
            InitStyles();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Lit → Simple Lit Converter", _headerStyle);
            EditorGUILayout.LabelField("Batch chuyển URP Lit sang Simple Lit (Blinn-Phong). Tiết kiệm ~30% fragment shader cost trên Quest/Mobile.", _miniLabel);
            EditorGUILayout.Space(4);

            // Shader status
            if (_simpleLitShader == null || _litShader == null)
            {
                EditorGUILayout.HelpBox("Không tìm thấy URP Lit / Simple Lit shader. Đảm bảo project đang dùng URP.", MessageType.Error);
                return;
            }

            // ── Scan scope ──
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Scan:", GUILayout.Width(40));
            _scanScope = (ScanScope)EditorGUILayout.EnumPopup(_scanScope, GUILayout.Width(100));

            GUI.backgroundColor = new Color(0.3f, 0.7f, 0.9f);
            if (GUILayout.Button("🔍 Scan", GUILayout.Height(24), GUILayout.Width(80)))
                ScanMaterials();

            GUI.backgroundColor = new Color(0.2f, 0.8f, 0.4f);
            GUI.enabled = _hasScanned && _entries.Any(e => e.selected && !e.converted);
            if (GUILayout.Button("🔧 Convert Selected", GUILayout.Height(24)))
                ConvertSelected();
            GUI.enabled = true;

            GUI.backgroundColor = new Color(0.9f, 0.5f, 0.2f);
            GUI.enabled = _hasScanned && _entries.Any(e => e.converted && e.backupCreated);
            if (GUILayout.Button("↩ Revert All", GUILayout.Height(24), GUILayout.Width(90)))
                RevertAll();
            GUI.enabled = true;

            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            // ── Options ──
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "⚙ Conversion Options", true);
            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                DrawOptions();
                EditorGUI.indentLevel--;
            }

            // ── Summary ──
            if (_hasScanned)
            {
                EditorGUILayout.Space(4);
                DrawSummary();
            }

            // ── Material List ──
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            if (_hasScanned && _entries.Count > 0)
            {
                // Search
                _searchFilter = EditorGUILayout.TextField("🔍 Search:", _searchFilter);

                EditorGUILayout.Space(2);

                // Select buttons
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("☑ All", EditorStyles.miniButtonLeft, GUILayout.Width(45)))
                    _entries.ForEach(e => e.selected = !e.converted);
                if (GUILayout.Button("☐ None", EditorStyles.miniButtonMid, GUILayout.Width(50)))
                    _entries.ForEach(e => e.selected = false);
                if (GUILayout.Button("No Normal", EditorStyles.miniButtonMid, GUILayout.Width(70)))
                    _entries.ForEach(e => e.selected = !e.converted && !e.hasNormalMap);
                if (GUILayout.Button("No Metal", EditorStyles.miniButtonRight, GUILayout.Width(65)))
                    _entries.ForEach(e => e.selected = !e.converted && !e.hasMetallicMap);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(2);

                // Table header
                DrawTableHeader();

                // Entries
                foreach (var entry in GetFiltered())
                {
                    DrawEntry(entry);
                }
            }
            else if (_hasScanned)
            {
                EditorGUILayout.LabelField("Không tìm thấy material URP Lit nào.", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                EditorGUILayout.Space(30);
                EditorGUILayout.LabelField("Chọn scope rồi nhấn Scan.", EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawOptions()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            _createBackup = EditorGUILayout.Toggle("Tạo backup (.bak.mat)", _createBackup);
            _keepNormalMaps = EditorGUILayout.Toggle("Giữ Normal Maps", _keepNormalMaps);
            _keepEmission = EditorGUILayout.Toggle("Giữ Emission", _keepEmission);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Specular Conversion:", _boldLabel);
            _specularSource = (SpecularSource)EditorGUILayout.EnumPopup("Source", _specularSource);

            switch (_specularSource)
            {
                case SpecularSource.FromMetallic:
                    EditorGUILayout.HelpBox(
                        "Tự tính specular color từ metallic + albedo:\n" +
                        "• Metallic cao → specular = albedo color (kim loại phản chiếu màu)\n" +
                        "• Metallic thấp → specular = trắng nhạt (dielectric)", MessageType.Info);
                    _specularStrength = EditorGUILayout.Slider("Specular Strength", _specularStrength, 0f, 1f);
                    break;
                case SpecularSource.CustomColor:
                    _customSpecularColor = EditorGUILayout.ColorField("Specular Color", _customSpecularColor);
                    break;
                case SpecularSource.White:
                    EditorGUILayout.HelpBox("Dùng specular trắng nhạt cho tất cả materials. Đơn giản nhất.", MessageType.Info);
                    break;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSummary()
        {
            EditorGUILayout.BeginVertical(_boxStyle);

            int total = _entries.Count;
            int converted = _entries.Count(e => e.converted);
            int selected = _entries.Count(e => e.selected && !e.converted);
            int withNormal = _entries.Count(e => e.hasNormalMap);
            int withMetallic = _entries.Count(e => e.hasMetallicMap);
            int withEmission = _entries.Count(e => e.hasEmission);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Total Lit materials: {total}", _boldLabel, GUILayout.Width(170));
            GUI.color = new Color(0.3f, 0.9f, 0.3f);
            EditorGUILayout.LabelField($"✅ Converted: {converted}", GUILayout.Width(130));
            GUI.color = new Color(0.4f, 0.75f, 1f);
            EditorGUILayout.LabelField($"☑ Selected: {selected}", GUILayout.Width(110));
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField($"Properties: {withNormal} Normal Map  |  {withMetallic} Metallic Map  |  {withEmission} Emission", _miniLabel);

            if (_convertedCount > 0 || _failedCount > 0)
                EditorGUILayout.LabelField($"Last run: {_convertedCount} converted, {_failedCount} failed", _miniLabel);

            EditorGUILayout.EndVertical();
        }

        private void DrawTableHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("☑", EditorStyles.miniLabel, GUILayout.Width(20));
            EditorGUILayout.LabelField("Material", EditorStyles.miniLabel, GUILayout.Width(170));
            EditorGUILayout.LabelField("Used", EditorStyles.miniLabel, GUILayout.Width(35));
            EditorGUILayout.LabelField("Color", EditorStyles.miniLabel, GUILayout.Width(36));
            EditorGUILayout.LabelField("N", EditorStyles.miniLabel, GUILayout.Width(16));
            EditorGUILayout.LabelField("M", EditorStyles.miniLabel, GUILayout.Width(16));
            EditorGUILayout.LabelField("E", EditorStyles.miniLabel, GUILayout.Width(16));
            EditorGUILayout.LabelField("Metal", EditorStyles.miniLabel, GUILayout.Width(40));
            EditorGUILayout.LabelField("Smooth", EditorStyles.miniLabel, GUILayout.Width(48));
            EditorGUILayout.LabelField("Mode", EditorStyles.miniLabel, GUILayout.Width(58));
            EditorGUILayout.LabelField("Status", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawEntry(MaterialEntry e)
        {
            var prevBg = GUI.backgroundColor;
            if (e.converted)
                GUI.backgroundColor = new Color(0.6f, 0.9f, 0.6f, 0.8f);
            else if (e.selected)
                GUI.backgroundColor = new Color(0.8f, 0.85f, 1f, 0.8f);

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUI.backgroundColor = prevBg;

            // Checkbox
            GUI.enabled = !e.converted;
            e.selected = EditorGUILayout.Toggle(e.selected, GUILayout.Width(18));
            GUI.enabled = true;

            // Name
            string displayName = e.name.Length > 22 ? e.name.Substring(0, 20) + ".." : e.name;
            EditorGUILayout.LabelField(displayName, GUILayout.Width(168));

            // Usage count
            EditorGUILayout.LabelField($"{e.usageCount}", EditorStyles.miniLabel, GUILayout.Width(32));

            // Base color preview
            var rect = GUILayoutUtility.GetRect(20, 16, GUILayout.Width(32));
            EditorGUI.DrawRect(rect, e.baseColor);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1), Color.gray);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), Color.gray);

            // Normal map indicator
            GUI.color = e.hasNormalMap ? new Color(0.4f, 0.7f, 1f) : new Color(0.5f, 0.5f, 0.5f);
            EditorGUILayout.LabelField(e.hasNormalMap ? "✓" : "–", EditorStyles.miniLabel, GUILayout.Width(14));

            // Metallic map indicator
            GUI.color = e.hasMetallicMap ? new Color(0.7f, 0.7f, 0.9f) : new Color(0.5f, 0.5f, 0.5f);
            EditorGUILayout.LabelField(e.hasMetallicMap ? "✓" : "–", EditorStyles.miniLabel, GUILayout.Width(14));

            // Emission indicator
            GUI.color = e.hasEmission ? new Color(1f, 0.9f, 0.3f) : new Color(0.5f, 0.5f, 0.5f);
            EditorGUILayout.LabelField(e.hasEmission ? "✓" : "–", EditorStyles.miniLabel, GUILayout.Width(14));
            GUI.color = Color.white;

            // Metallic value
            EditorGUILayout.LabelField($"{e.metallic:F2}", EditorStyles.miniLabel, GUILayout.Width(38));

            // Smoothness value
            EditorGUILayout.LabelField($"{e.smoothness:F2}", EditorStyles.miniLabel, GUILayout.Width(46));

            // Render mode
            EditorGUILayout.LabelField(e.renderMode.ToString(), EditorStyles.miniLabel, GUILayout.Width(56));

            // Status
            if (e.converted)
            {
                GUI.color = new Color(0.2f, 0.8f, 0.2f);
                EditorGUILayout.LabelField("✅ Done", EditorStyles.miniLabel);
            }
            else if (!string.IsNullOrEmpty(e.note))
            {
                EditorGUILayout.LabelField(e.note, _miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("", EditorStyles.miniLabel);
            }
            GUI.color = Color.white;

            // Ping
            if (GUILayout.Button("📍", GUILayout.Width(24), GUILayout.Height(16)))
            {
                Selection.activeObject = e.material;
                EditorGUIUtility.PingObject(e.material);
            }

            EditorGUILayout.EndHorizontal();
        }

        private List<MaterialEntry> GetFiltered()
        {
            return _entries.Where(e =>
            {
                if (!_showConverted && e.converted) return false;
                if (!string.IsNullOrEmpty(_searchFilter) &&
                    !e.name.ToLower().Contains(_searchFilter.ToLower()))
                    return false;
                return true;
            }).ToList();
        }

        // ══════════════════════════════════════════════
        // SCAN
        // ══════════════════════════════════════════════
        private void ScanMaterials()
        {
            _entries.Clear();
            _hasScanned = true;
            _convertedCount = 0;
            _failedCount = 0;

            var materials = new Dictionary<Material, MaterialEntry>();

            switch (_scanScope)
            {
                case ScanScope.Scene:
                    ScanSceneMaterials(materials);
                    break;
                case ScanScope.Project:
                    ScanProjectMaterials(materials);
                    break;
                case ScanScope.Selection:
                    ScanSelectionMaterials(materials);
                    break;
            }

            _entries = materials.Values.OrderByDescending(e => e.usageCount).ToList();

            Debug.Log($"[Lit→SimpleLit] Scanned: {_entries.Count} URP Lit materials found. " +
                      $"{_entries.Count(e => e.hasNormalMap)} with Normal Map, " +
                      $"{_entries.Count(e => e.hasMetallicMap)} with Metallic Map.");
        }

        private void ScanSceneMaterials(Dictionary<Material, MaterialEntry> materials)
        {
            var renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);

            foreach (var renderer in renderers)
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null || mat.shader == null) continue;
                    if (!IsLitShader(mat.shader)) continue;

                    if (materials.TryGetValue(mat, out var existing))
                    {
                        existing.usageCount++;
                        continue;
                    }

                    materials[mat] = CreateEntry(mat, 1);
                }
            }
        }

        private void ScanProjectMaterials(Dictionary<Material, MaterialEntry> materials)
        {
            var guids = AssetDatabase.FindAssets("t:Material");

            for (int i = 0; i < guids.Length; i++)
            {
                if (i % 50 == 0)
                    EditorUtility.DisplayProgressBar("Scanning...", $"{i}/{guids.Length}", (float)i / guids.Length);

                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null) continue;
                if (!IsLitShader(mat.shader)) continue;

                materials[mat] = CreateEntry(mat, 0);
            }

            EditorUtility.ClearProgressBar();
        }

        private void ScanSelectionMaterials(Dictionary<Material, MaterialEntry> materials)
        {
            // Materials from selected objects
            foreach (var obj in Selection.gameObjects)
            {
                var renderers = obj.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in renderers)
                {
                    foreach (var mat in renderer.sharedMaterials)
                    {
                        if (mat == null || mat.shader == null) continue;
                        if (!IsLitShader(mat.shader)) continue;

                        if (materials.TryGetValue(mat, out var existing))
                        {
                            existing.usageCount++;
                            continue;
                        }

                        materials[mat] = CreateEntry(mat, 1);
                    }
                }
            }

            // Direct material selection
            foreach (var obj in Selection.objects)
            {
                if (obj is Material mat && IsLitShader(mat.shader) && !materials.ContainsKey(mat))
                {
                    materials[mat] = CreateEntry(mat, 0);
                }
            }
        }

        private bool IsLitShader(Shader shader)
        {
            string name = shader.name;
            return name == "Universal Render Pipeline/Lit" ||
                   name == "Universal Render Pipeline/Complex Lit";
        }

        private MaterialEntry CreateEntry(Material mat, int usageCount)
        {
            var entry = new MaterialEntry
            {
                material = mat,
                name = mat.name,
                assetPath = AssetDatabase.GetAssetPath(mat),
                currentShader = mat.shader.name,
                usageCount = usageCount,
            };

            // Read properties
            entry.baseColor = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
            entry.metallic = mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic") : 0f;
            entry.smoothness = mat.HasProperty("_Smoothness") ? mat.GetFloat("_Smoothness") : 0.5f;
            entry.hasNormalMap = mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") != null;
            entry.hasMetallicMap = mat.HasProperty("_MetallicGlossMap") && mat.GetTexture("_MetallicGlossMap") != null;
            entry.hasEmission = mat.HasProperty("_EmissionColor") && mat.IsKeywordEnabled("_EMISSION");

            if (mat.HasProperty("_SpecGlossMap") && mat.GetTexture("_SpecGlossMap") != null)
                entry.hasSpecularMap = true;

            // Detect render mode
            if (mat.HasProperty("_Surface"))
            {
                float surface = mat.GetFloat("_Surface");
                if (surface == 0) // Opaque
                {
                    if (mat.HasProperty("_AlphaClip") && mat.GetFloat("_AlphaClip") == 1)
                        entry.renderMode = RenderMode.Cutout;
                    else
                        entry.renderMode = RenderMode.Opaque;
                }
                else
                {
                    entry.renderMode = RenderMode.Transparent;
                }
            }
            else
            {
                entry.renderMode = RenderMode.Unknown;
            }

            return entry;
        }

        // ══════════════════════════════════════════════
        // CONVERT
        // ══════════════════════════════════════════════
        private void ConvertSelected()
        {
            var toConvert = _entries.Where(e => e.selected && !e.converted).ToList();
            if (toConvert.Count == 0) return;

            int normalCount = toConvert.Count(e => e.hasNormalMap);
            int metallicCount = toConvert.Count(e => e.hasMetallicMap);

            if (!EditorUtility.DisplayDialog("Convert to Simple Lit",
                $"Chuyển {toConvert.Count} material(s) sang Simple Lit:\n\n" +
                $"• {normalCount} có Normal Map → {(_keepNormalMaps ? "giữ" : "bỏ")}\n" +
                $"• {metallicCount} có Metallic Map → chuyển sang Specular\n" +
                $"• Backup: {(_createBackup ? "Có" : "Không")}\n\n" +
                "Tiếp tục?", "Convert", "Cancel"))
                return;

            _convertedCount = 0;
            _failedCount = 0;

            for (int i = 0; i < toConvert.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Converting...",
                    $"[{i + 1}/{toConvert.Count}] {toConvert[i].name}",
                    (float)i / toConvert.Count);

                ConvertMaterial(toConvert[i]);
            }

            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[Lit→SimpleLit] Done: {_convertedCount} converted, {_failedCount} failed.");
            EditorUtility.DisplayDialog("Conversion Complete",
                $"✅ Converted: {_convertedCount}\n❌ Failed: {_failedCount}", "OK");
        }

        private void ConvertMaterial(MaterialEntry entry)
        {
            try
            {
                var mat = entry.material;

                // ── Backup ──
                if (_createBackup && !string.IsNullOrEmpty(entry.assetPath))
                {
                    string backupPath = entry.assetPath.Replace(".mat", "_LitBackup.mat");
                    backupPath = AssetDatabase.GenerateUniqueAssetPath(backupPath);

                    if (AssetDatabase.CopyAsset(entry.assetPath, backupPath))
                    {
                        entry.backupCreated = true;
                        entry.backupPath = backupPath;
                    }
                }

                // ── Read current properties BEFORE switching shader ──
                Color baseColor = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
                Texture baseMap = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : null;
                Vector2 tiling = mat.HasProperty("_BaseMap") ? mat.GetTextureScale("_BaseMap") : Vector2.one;
                Vector2 offset = mat.HasProperty("_BaseMap") ? mat.GetTextureOffset("_BaseMap") : Vector2.zero;

                float metallic = mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic") : 0f;
                float smoothness = mat.HasProperty("_Smoothness") ? mat.GetFloat("_Smoothness") : 0.5f;
                Texture metallicMap = mat.HasProperty("_MetallicGlossMap") ? mat.GetTexture("_MetallicGlossMap") : null;

                Texture normalMap = mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
                float normalScale = mat.HasProperty("_BumpScale") ? mat.GetFloat("_BumpScale") : 1f;

                Color emissionColor = Color.black;
                Texture emissionMap = null;
                bool hasEmission = mat.IsKeywordEnabled("_EMISSION");
                if (hasEmission)
                {
                    emissionColor = mat.HasProperty("_EmissionColor") ? mat.GetColor("_EmissionColor") : Color.black;
                    emissionMap = mat.HasProperty("_EmissionMap") ? mat.GetTexture("_EmissionMap") : null;
                }

                // Surface type
                float surfaceType = mat.HasProperty("_Surface") ? mat.GetFloat("_Surface") : 0;
                float alphaClip = mat.HasProperty("_AlphaClip") ? mat.GetFloat("_AlphaClip") : 0;
                float cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f;
                float blend = mat.HasProperty("_Blend") ? mat.GetFloat("_Blend") : 0;

                // Occlusion
                Texture occlusionMap = mat.HasProperty("_OcclusionMap") ? mat.GetTexture("_OcclusionMap") : null;
                float occlusionStrength = mat.HasProperty("_OcclusionStrength") ? mat.GetFloat("_OcclusionStrength") : 1f;

                // ── Switch shader ──
                Undo.RecordObject(mat, $"Convert {mat.name} to Simple Lit");
                mat.shader = _simpleLitShader;

                // ── Apply properties ──

                // Base color + texture
                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", baseColor);
                if (mat.HasProperty("_BaseMap") && baseMap != null)
                {
                    mat.SetTexture("_BaseMap", baseMap);
                    mat.SetTextureScale("_BaseMap", tiling);
                    mat.SetTextureOffset("_BaseMap", offset);
                }

                // Specular (convert from metallic)
                Color specularColor = CalculateSpecularColor(baseColor, metallic, smoothness);
                if (mat.HasProperty("_SpecColor"))
                    mat.SetColor("_SpecColor", specularColor);

                // Smoothness
                if (mat.HasProperty("_Smoothness"))
                    mat.SetFloat("_Smoothness", smoothness);

                // Specular map (from metallic map if exists)
                if (mat.HasProperty("_SpecGlossMap") && metallicMap != null)
                {
                    // Simple Lit can use specular map in _SpecGlossMap
                    mat.SetTexture("_SpecGlossMap", metallicMap);
                    mat.EnableKeyword("_SPECGLOSSMAP");
                }

                // Normal map
                if (_keepNormalMaps && normalMap != null)
                {
                    if (mat.HasProperty("_BumpMap"))
                    {
                        mat.SetTexture("_BumpMap", normalMap);
                        mat.EnableKeyword("_NORMALMAP");
                    }
                    if (mat.HasProperty("_BumpScale"))
                        mat.SetFloat("_BumpScale", normalScale);
                }

                // Emission
                if (_keepEmission && hasEmission)
                {
                    if (mat.HasProperty("_EmissionColor"))
                        mat.SetColor("_EmissionColor", emissionColor);
                    if (mat.HasProperty("_EmissionMap") && emissionMap != null)
                        mat.SetTexture("_EmissionMap", emissionMap);
                    mat.EnableKeyword("_EMISSION");
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
                }

                // Surface type
                if (mat.HasProperty("_Surface"))
                    mat.SetFloat("_Surface", surfaceType);
                if (mat.HasProperty("_AlphaClip"))
                    mat.SetFloat("_AlphaClip", alphaClip);
                if (mat.HasProperty("_Cutoff"))
                    mat.SetFloat("_Cutoff", cutoff);

                // Render queue
                if (surfaceType == 0) // Opaque
                {
                    if (alphaClip == 1)
                    {
                        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                        mat.SetOverrideTag("RenderType", "TransparentCutout");
                    }
                    else
                    {
                        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
                        mat.SetOverrideTag("RenderType", "Opaque");
                    }
                    mat.SetFloat("_ZWrite", 1);
                    mat.SetFloat("_SrcBlend", (float)BlendMode.One);
                    mat.SetFloat("_DstBlend", (float)BlendMode.Zero);
                }
                else // Transparent
                {
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    mat.SetOverrideTag("RenderType", "Transparent");
                    mat.SetFloat("_ZWrite", 0);
                    mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                }

                EditorUtility.SetDirty(mat);

                entry.converted = true;
                entry.note = "✅ Converted";
                entry.currentShader = "Simple Lit";
                _convertedCount++;
            }
            catch (Exception ex)
            {
                entry.note = $"❌ {ex.Message}";
                _failedCount++;
                Debug.LogError($"[Lit→SimpleLit] Failed: {entry.name}: {ex}");
            }
        }

        private Color CalculateSpecularColor(Color baseColor, float metallic, float smoothness)
        {
            switch (_specularSource)
            {
                case SpecularSource.FromMetallic:
                    // PBR metallic → specular conversion
                    // Metallic surfaces: specular = base color
                    // Dielectric surfaces: specular = 0.04 (white-ish, low)
                    Color dielectric = new Color(0.04f, 0.04f, 0.04f, 1f);
                    Color spec = Color.Lerp(dielectric, baseColor, metallic) * _specularStrength;
                    spec.a = smoothness; // Alpha = smoothness in Simple Lit
                    return spec;

                case SpecularSource.CustomColor:
                    var c = _customSpecularColor;
                    c.a = smoothness;
                    return c;

                case SpecularSource.White:
                    return new Color(0.2f, 0.2f, 0.2f, smoothness);

                default:
                    return new Color(0.2f, 0.2f, 0.2f, smoothness);
            }
        }

        // ══════════════════════════════════════════════
        // REVERT
        // ══════════════════════════════════════════════
        private void RevertAll()
        {
            var toRevert = _entries.Where(e => e.converted && e.backupCreated).ToList();
            if (toRevert.Count == 0) return;

            if (!EditorUtility.DisplayDialog("Revert All",
                $"Revert {toRevert.Count} material(s) về Lit shader từ backup?", "Revert", "Cancel"))
                return;

            int reverted = 0;
            foreach (var entry in toRevert)
            {
                try
                {
                    if (string.IsNullOrEmpty(entry.backupPath)) continue;

                    // Copy backup over original
                    var backupMat = AssetDatabase.LoadAssetAtPath<Material>(entry.backupPath);
                    if (backupMat == null) continue;

                    EditorUtility.CopySerialized(backupMat, entry.material);
                    EditorUtility.SetDirty(entry.material);

                    // Delete backup
                    AssetDatabase.DeleteAsset(entry.backupPath);

                    entry.converted = false;
                    entry.backupCreated = false;
                    entry.backupPath = "";
                    entry.note = "↩ Reverted";
                    entry.currentShader = "Lit";
                    reverted++;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Lit→SimpleLit] Revert failed: {entry.name}: {ex}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[Lit→SimpleLit] Reverted {reverted} materials.");
        }
    }
}
#endif
