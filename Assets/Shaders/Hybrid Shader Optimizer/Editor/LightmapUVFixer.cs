// ============================================================================
// URP Lightmap UV Fixer — Editor Tool
// Tech Art Department | v1.0 | March 2026
//
// Tự động bật Generate Lightmap UVs cho tất cả static mesh thiếu UV2.
// Hỗ trợ cả mesh có ModelImporter lẫn mesh lẻ (procedural, embedded, 
// prefab nested, v.v.) bằng cách generate UV2 runtime qua Unwrapping API.
//
// Usage: Window > Tech Art > Lightmap UV Fixer
//        Hoặc gọi từ URP Setup Auditor khi có lỗi UV2.
// ============================================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TechArt.Tools
{
    public class LightmapUVFixer : EditorWindow
    {
        // ── Data Structures ──
        private enum MeshFixType
        {
            ModelImporter,      // Có file FBX/OBJ/GLTF → bật generateSecondaryUV
            RuntimeGenerate,    // Mesh lẻ, procedural, embedded → dùng Unwrapping API
            ReadOnlySkipped     // Mesh không thể sửa (package, immutable)
        }

        private class MeshEntry
        {
            public Mesh mesh;
            public string meshName;
            public string assetPath;
            public MeshFixType fixType;
            public bool selected = true;
            public bool fixed_ = false;
            public string fixNote = "";
            public List<GameObject> referencedBy = new List<GameObject>();

            // Unwrapping params (cho RuntimeGenerate)
            public float packMargin = 0.03f;  // Tăng cho low poly
            public float angleError = 0.08f;
            public float areaError = 0.15f;
            public float hardAngle = 88f;
        }

        // ── State ──
        private List<MeshEntry> _entries = new List<MeshEntry>();
        private Vector2 _scrollPos;
        private bool _hasScanned = false;
        private bool _showAdvanced = false;
        private int _totalFixed = 0;
        private int _totalFailed = 0;

        // Global unwrapping params
        private float _globalPackMargin = 0.03f;
        private float _globalAngleError = 0.08f;
        private float _globalAreaError = 0.15f;
        private float _globalHardAngle = 88f;

        // Filter
        private bool _showModelImporter = true;
        private bool _showRuntimeGenerate = true;
        private bool _showReadOnly = false;
        private string _searchFilter = "";

        // Styles
        private GUIStyle _headerStyle;
        private GUIStyle _boxStyle;
        private GUIStyle _boldLabel;
        private GUIStyle _miniLabel;
        private GUIStyle _richLabel;
        private bool _stylesInit;

        // ── Menu ──
        [MenuItem("Window/Tech Art/Lightmap UV Fixer")]
        public static void ShowWindow()
        {
            var wnd = GetWindow<LightmapUVFixer>("UV Fixer");
            wnd.minSize = new Vector2(560, 400);
        }

        // Có thể gọi từ code khác (e.g. URP Auditor)
        public static void ShowAndScan()
        {
            var wnd = ShowWindow_Internal();
            wnd.ScanScene();
        }

        private static LightmapUVFixer ShowWindow_Internal()
        {
            var wnd = GetWindow<LightmapUVFixer>("UV Fixer");
            wnd.minSize = new Vector2(560, 400);
            return wnd;
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
            EditorGUILayout.LabelField("Lightmap UV Fixer", _headerStyle);
            EditorGUILayout.LabelField("Auto bật Generate Lightmap UVs + generate UV2 cho mesh lẻ không có file", _miniLabel);
            EditorGUILayout.Space(4);

            // ── Action Buttons ──
            EditorGUILayout.BeginHorizontal();

            GUI.backgroundColor = new Color(0.3f, 0.7f, 0.9f);
            if (GUILayout.Button("🔍  Scan Scene", GUILayout.Height(30)))
                ScanScene();

            GUI.backgroundColor = new Color(0.2f, 0.8f, 0.4f);
            GUI.enabled = _hasScanned && _entries.Any(e => e.selected && !e.fixed_ && e.fixType != MeshFixType.ReadOnlySkipped);
            if (GUILayout.Button("🔧  Fix Selected", GUILayout.Height(30)))
                FixSelected();
            GUI.enabled = true;

            GUI.backgroundColor = new Color(0.9f, 0.7f, 0.2f);
            GUI.enabled = _hasScanned && _entries.Count > 0;
            if (GUILayout.Button("⚡  Fix All", GUILayout.Height(30)))
                FixAll();
            GUI.enabled = true;

            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            // ── Summary ──
            if (_hasScanned)
            {
                EditorGUILayout.Space(4);
                DrawSummary();
                EditorGUILayout.Space(4);

                // ── Filters ──
                EditorGUILayout.BeginHorizontal(_boxStyle);
                EditorGUILayout.LabelField("Filter:", GUILayout.Width(40));
                _showModelImporter = GUILayout.Toggle(_showModelImporter, $"ModelImporter ({_entries.Count(e => e.fixType == MeshFixType.ModelImporter)})", "Button", GUILayout.Height(20));
                _showRuntimeGenerate = GUILayout.Toggle(_showRuntimeGenerate, $"Runtime Gen ({_entries.Count(e => e.fixType == MeshFixType.RuntimeGenerate)})", "Button", GUILayout.Height(20));
                _showReadOnly = GUILayout.Toggle(_showReadOnly, $"Read-Only ({_entries.Count(e => e.fixType == MeshFixType.ReadOnlySkipped)})", "Button", GUILayout.Height(20));
                EditorGUILayout.EndHorizontal();

                _searchFilter = EditorGUILayout.TextField("Search:", _searchFilter);

                // ── Advanced Settings ──
                _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "⚙ UV2 Generation Settings (cho mesh lẻ)", true);
                if (_showAdvanced)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.HelpBox(
                        "Settings này áp dụng cho mesh KHÔNG có ModelImporter (procedural, embedded, clone).\n" +
                        "Low poly cần Pack Margin cao hơn (0.03–0.05) để tránh seam bleeding.",
                        MessageType.Info);

                    _globalPackMargin = EditorGUILayout.Slider("Pack Margin", _globalPackMargin, 0.005f, 0.1f);
                    _globalAngleError = EditorGUILayout.Slider("Angle Error", _globalAngleError, 0.01f, 0.2f);
                    _globalAreaError = EditorGUILayout.Slider("Area Error", _globalAreaError, 0.01f, 0.3f);
                    _globalHardAngle = EditorGUILayout.Slider("Hard Angle", _globalHardAngle, 60f, 180f);

                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Low Poly Preset"))
                    {
                        _globalPackMargin = 0.04f;
                        _globalAngleError = 0.08f;
                        _globalAreaError = 0.15f;
                        _globalHardAngle = 88f;
                    }
                    if (GUILayout.Button("Default Preset"))
                    {
                        _globalPackMargin = 0.02f;
                        _globalAngleError = 0.08f;
                        _globalAreaError = 0.15f;
                        _globalHardAngle = 88f;
                    }
                    if (GUILayout.Button("Aggressive Pack"))
                    {
                        _globalPackMargin = 0.05f;
                        _globalAngleError = 0.12f;
                        _globalAreaError = 0.2f;
                        _globalHardAngle = 78f;
                    }
                    EditorGUILayout.EndHorizontal();

                    if (GUILayout.Button("Apply Settings to All Runtime Generate Entries"))
                    {
                        foreach (var e in _entries.Where(x => x.fixType == MeshFixType.RuntimeGenerate))
                        {
                            e.packMargin = _globalPackMargin;
                            e.angleError = _globalAngleError;
                            e.areaError = _globalAreaError;
                            e.hardAngle = _globalHardAngle;
                        }
                    }

                    EditorGUI.indentLevel--;
                    EditorGUILayout.Space(4);
                }
            }

            // ── Entry List ──
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            if (_hasScanned && _entries.Count > 0)
            {
                // Select all / none
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Select All", EditorStyles.miniButtonLeft, GUILayout.Width(80)))
                    _entries.ForEach(e => e.selected = true);
                if (GUILayout.Button("Select None", EditorStyles.miniButtonMid, GUILayout.Width(80)))
                    _entries.ForEach(e => e.selected = false);
                if (GUILayout.Button("Select Unfixed", EditorStyles.miniButtonRight, GUILayout.Width(100)))
                    _entries.ForEach(e => e.selected = !e.fixed_);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(4);

                foreach (var entry in _entries)
                {
                    // Apply filters
                    if (entry.fixType == MeshFixType.ModelImporter && !_showModelImporter) continue;
                    if (entry.fixType == MeshFixType.RuntimeGenerate && !_showRuntimeGenerate) continue;
                    if (entry.fixType == MeshFixType.ReadOnlySkipped && !_showReadOnly) continue;
                    if (!string.IsNullOrEmpty(_searchFilter) &&
                        !entry.meshName.ToLower().Contains(_searchFilter.ToLower()) &&
                        !entry.assetPath.ToLower().Contains(_searchFilter.ToLower()))
                        continue;

                    DrawEntry(entry);
                }
            }
            else if (_hasScanned)
            {
                EditorGUILayout.LabelField("✅ Tất cả static meshes đã có UV2!", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                EditorGUILayout.Space(20);
                EditorGUILayout.LabelField("Nhấn Scan Scene để tìm meshes thiếu UV2.", EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSummary()
        {
            int modelImporter = _entries.Count(e => e.fixType == MeshFixType.ModelImporter);
            int runtimeGen = _entries.Count(e => e.fixType == MeshFixType.RuntimeGenerate);
            int readOnly = _entries.Count(e => e.fixType == MeshFixType.ReadOnlySkipped);
            int alreadyFixed = _entries.Count(e => e.fixed_);

            EditorGUILayout.BeginVertical(_boxStyle);

            EditorGUILayout.LabelField($"Tìm thấy {_entries.Count} mesh(es) thiếu UV2:", _boldLabel);

            EditorGUILayout.BeginHorizontal();
            GUI.color = new Color(0.4f, 0.8f, 1f);
            EditorGUILayout.LabelField($"📦 {modelImporter} ModelImporter", GUILayout.Width(160));
            GUI.color = new Color(1f, 0.8f, 0.3f);
            EditorGUILayout.LabelField($"⚙ {runtimeGen} Runtime Generate", GUILayout.Width(170));
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            EditorGUILayout.LabelField($"🔒 {readOnly} Read-Only", GUILayout.Width(130));
            GUI.color = new Color(0.3f, 0.9f, 0.3f);
            EditorGUILayout.LabelField($"✅ {alreadyFixed} Fixed", GUILayout.Width(100));
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();

            if (_totalFixed > 0 || _totalFailed > 0)
            {
                EditorGUILayout.LabelField($"Last run: {_totalFixed} fixed, {_totalFailed} failed", _miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawEntry(MeshEntry entry)
        {
            Color bgColor;
            if (entry.fixed_)
                bgColor = new Color(0.18f, 0.32f, 0.18f, 0.3f);
            else if (entry.fixType == MeshFixType.ReadOnlySkipped)
                bgColor = new Color(0.3f, 0.3f, 0.3f, 0.3f);
            else
                bgColor = new Color(0.35f, 0.25f, 0.1f, 0.3f);

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = bgColor + Color.white * 0.7f;
            EditorGUILayout.BeginHorizontal(_boxStyle);
            GUI.backgroundColor = prevBg;

            // Checkbox
            GUI.enabled = !entry.fixed_ && entry.fixType != MeshFixType.ReadOnlySkipped;
            entry.selected = EditorGUILayout.Toggle(entry.selected, GUILayout.Width(18));
            GUI.enabled = true;

            // Icon + Name
            string icon = entry.fixType switch
            {
                MeshFixType.ModelImporter => "📦",
                MeshFixType.RuntimeGenerate => "⚙",
                MeshFixType.ReadOnlySkipped => "🔒",
                _ => ""
            };

            if (entry.fixed_)
                icon = "✅";

            EditorGUILayout.LabelField($"{icon} {entry.meshName}", _boldLabel, GUILayout.Width(220));

            // Type badge
            string badge = entry.fixType switch
            {
                MeshFixType.ModelImporter => "Importer",
                MeshFixType.RuntimeGenerate => "Generate",
                MeshFixType.ReadOnlySkipped => "Read-Only",
                _ => ""
            };
            EditorGUILayout.LabelField(badge, EditorStyles.miniLabel, GUILayout.Width(65));

            // Referenced by count
            EditorGUILayout.LabelField($"{entry.referencedBy.Count} obj(s)", EditorStyles.miniLabel, GUILayout.Width(55));

            // Path (truncated)
            string pathDisplay = entry.assetPath.Length > 45
                ? "..." + entry.assetPath.Substring(entry.assetPath.Length - 42)
                : entry.assetPath;
            EditorGUILayout.LabelField(pathDisplay, _miniLabel);

            // Ping button
            if (entry.referencedBy.Count > 0 && GUILayout.Button("📍", GUILayout.Width(28), GUILayout.Height(18)))
            {
                Selection.activeGameObject = entry.referencedBy[0];
                EditorGUIUtility.PingObject(entry.referencedBy[0]);
            }

            EditorGUILayout.EndHorizontal();

            // Fix note
            if (!string.IsNullOrEmpty(entry.fixNote))
            {
                EditorGUI.indentLevel += 2;
                EditorGUILayout.LabelField($"    {entry.fixNote}", _miniLabel);
                EditorGUI.indentLevel -= 2;
            }
        }

        // ══════════════════════════════════════════════
        // SCAN
        // ══════════════════════════════════════════════
        private void ScanScene()
        {
            _entries.Clear();
            _hasScanned = true;
            _totalFixed = 0;
            _totalFailed = 0;

            var meshRenderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
            var processedMeshes = new Dictionary<Mesh, MeshEntry>();

            int progress = 0;
            int total = meshRenderers.Length;

            foreach (var mr in meshRenderers)
            {
                progress++;
                if (progress % 100 == 0)
                {
                    EditorUtility.DisplayProgressBar("Scanning...",
                        $"Checking {progress}/{total} renderers", (float)progress / total);
                }

                var go = mr.gameObject;

                // Chỉ check static objects
                var flags = GameObjectUtility.GetStaticEditorFlags(go);
                if (!flags.HasFlag(StaticEditorFlags.ContributeGI)) continue;

                var mf = go.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                var mesh = mf.sharedMesh;

                // Đã có UV2 → skip
                if (mesh.uv2 != null && mesh.uv2.Length > 0) continue;

                // Đã track mesh này → chỉ thêm reference
                if (processedMeshes.TryGetValue(mesh, out var existing))
                {
                    existing.referencedBy.Add(go);
                    continue;
                }

                // Classify mesh
                var entry = ClassifyMesh(mesh, go);
                processedMeshes[mesh] = entry;
                _entries.Add(entry);
            }

            EditorUtility.ClearProgressBar();

            // Sort: errors first, then by reference count
            _entries.Sort((a, b) =>
            {
                int typeCmp = ((int)a.fixType).CompareTo((int)b.fixType);
                if (typeCmp != 0) return typeCmp;
                return b.referencedBy.Count.CompareTo(a.referencedBy.Count);
            });

            Debug.Log($"[UV Fixer] Scan complete: {_entries.Count} unique meshes thiếu UV2 " +
                      $"({_entries.Count(e => e.fixType == MeshFixType.ModelImporter)} importer, " +
                      $"{_entries.Count(e => e.fixType == MeshFixType.RuntimeGenerate)} runtime, " +
                      $"{_entries.Count(e => e.fixType == MeshFixType.ReadOnlySkipped)} read-only)");
        }

        private MeshEntry ClassifyMesh(Mesh mesh, GameObject firstRef)
        {
            var entry = new MeshEntry
            {
                mesh = mesh,
                meshName = mesh.name,
                packMargin = _globalPackMargin,
                angleError = _globalAngleError,
                areaError = _globalAreaError,
                hardAngle = _globalHardAngle,
                referencedBy = new List<GameObject> { firstRef }
            };

            string assetPath = AssetDatabase.GetAssetPath(mesh);
            entry.assetPath = string.IsNullOrEmpty(assetPath) ? "(no asset path — scene/procedural)" : assetPath;

            if (string.IsNullOrEmpty(assetPath))
            {
                // Mesh procedural, tạo trong code, hoặc là instance
                entry.fixType = MeshFixType.RuntimeGenerate;
                entry.fixNote = "Mesh không có asset path. Sẽ generate UV2 và save thành asset mới.";
                return entry;
            }

            // Check if it's in a package (read-only)
            if (assetPath.StartsWith("Packages/"))
            {
                entry.fixType = MeshFixType.ReadOnlySkipped;
                entry.fixNote = "Mesh trong Package (read-only). Cần copy ra Assets/ trước.";
                return entry;
            }

            // Check if it's a model file with ModelImporter
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer != null)
            {
                if (importer.generateSecondaryUV)
                {
                    // Đã bật nhưng chưa reimport?
                    entry.fixType = MeshFixType.ModelImporter;
                    entry.fixNote = "generateSecondaryUV đã bật nhưng mesh chưa có UV2. Cần reimport.";
                }
                else
                {
                    entry.fixType = MeshFixType.ModelImporter;
                    entry.fixNote = "Sẽ bật generateSecondaryUV và reimport.";
                }
                return entry;
            }

            // Mesh asset nhưng không phải model file (ScriptableObject mesh, .asset, etc.)
            // Check if writable
            if (!AssetDatabase.IsOpenForEdit(assetPath))
            {
                entry.fixType = MeshFixType.ReadOnlySkipped;
                entry.fixNote = "Asset không writable (locked hoặc version control).";
                return entry;
            }

            // Mesh asset file → generate runtime
            entry.fixType = MeshFixType.RuntimeGenerate;
            entry.fixNote = "Mesh asset (không phải model file). Sẽ generate UV2 trực tiếp.";
            return entry;
        }

        // ══════════════════════════════════════════════
        // FIX
        // ══════════════════════════════════════════════
        private void FixAll()
        {
            foreach (var e in _entries)
            {
                if (e.fixType != MeshFixType.ReadOnlySkipped)
                    e.selected = true;
            }
            FixSelected();
        }

        private void FixSelected()
        {
            var toFix = _entries.Where(e => e.selected && !e.fixed_ && e.fixType != MeshFixType.ReadOnlySkipped).ToList();

            if (toFix.Count == 0)
            {
                EditorUtility.DisplayDialog("UV Fixer", "Không có mesh nào cần fix.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("UV Fixer",
                $"Sẽ fix {toFix.Count} mesh(es):\n" +
                $"• {toFix.Count(e => e.fixType == MeshFixType.ModelImporter)} bật Generate Lightmap UVs + reimport\n" +
                $"• {toFix.Count(e => e.fixType == MeshFixType.RuntimeGenerate)} generate UV2 runtime\n\n" +
                "Quá trình này sẽ modify assets. Tiếp tục?",
                "Fix", "Cancel"))
                return;

            _totalFixed = 0;
            _totalFailed = 0;

            // ── Batch ModelImporter fixes ──
            var importerEntries = toFix.Where(e => e.fixType == MeshFixType.ModelImporter).ToList();
            if (importerEntries.Count > 0)
            {
                FixModelImporterBatch(importerEntries);
            }

            // ── Runtime Generate fixes ──
            var runtimeEntries = toFix.Where(e => e.fixType == MeshFixType.RuntimeGenerate).ToList();
            if (runtimeEntries.Count > 0)
            {
                FixRuntimeGenerateBatch(runtimeEntries);
            }

            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[UV Fixer] Complete: {_totalFixed} fixed, {_totalFailed} failed");
            EditorUtility.DisplayDialog("UV Fixer",
                $"Hoàn tất!\n✅ Fixed: {_totalFixed}\n❌ Failed: {_totalFailed}", "OK");
        }

        // ── ModelImporter Batch ──
        private void FixModelImporterBatch(List<MeshEntry> entries)
        {
            // Group by asset path (multiple meshes có thể từ cùng 1 file)
            var byAsset = entries.GroupBy(e => AssetDatabase.GetAssetPath(e.mesh)).ToList();

            for (int i = 0; i < byAsset.Count; i++)
            {
                var group = byAsset[i];
                string assetPath = group.Key;

                EditorUtility.DisplayProgressBar("Fixing ModelImporter...",
                    $"[{i + 1}/{byAsset.Count}] {Path.GetFileName(assetPath)}",
                    (float)i / byAsset.Count);

                try
                {
                    var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
                    if (importer == null)
                    {
                        foreach (var e in group)
                        {
                            e.fixNote = "❌ Không tìm thấy ModelImporter!";
                            _totalFailed++;
                        }
                        continue;
                    }

                    // Bật generate secondary UV với settings tốt cho low poly
                    importer.generateSecondaryUV = true;
                    importer.secondaryUVPackMargin = _globalPackMargin;
                    importer.secondaryUVAngleDistortion = _globalAngleError;
                    importer.secondaryUVAreaDistortion = _globalAreaError;
                    importer.secondaryUVHardAngle = _globalHardAngle;

                    // Đảm bảo tangent space đúng
                    if (importer.importTangents == ModelImporterTangents.None)
                    {
                        importer.importTangents = ModelImporterTangents.CalculateMikk;
                    }

                    importer.SaveAndReimport();

                    foreach (var e in group)
                    {
                        e.fixed_ = true;
                        e.fixNote = $"✅ generateSecondaryUV = true, PackMargin = {_globalPackMargin}, Tangents = MikkTSpace, reimported.";
                        _totalFixed++;
                    }
                }
                catch (Exception ex)
                {
                    foreach (var e in group)
                    {
                        e.fixNote = $"❌ Error: {ex.Message}";
                        _totalFailed++;
                    }
                    Debug.LogError($"[UV Fixer] Failed to fix {assetPath}: {ex}");
                }
            }
        }

        // ── Runtime Generate Batch ──
        private void FixRuntimeGenerateBatch(List<MeshEntry> entries)
        {
            // Tạo folder cho generated UV2 meshes
            string outputFolder = "Assets/Generated/LightmapUV";
            EnsureFolderExists(outputFolder);

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];

                EditorUtility.DisplayProgressBar("Generating UV2...",
                    $"[{i + 1}/{entries.Count}] {entry.meshName}",
                    (float)i / entries.Count);

                try
                {
                    Mesh sourceMesh = entry.mesh;
                    string assetPath = AssetDatabase.GetAssetPath(sourceMesh);

                    // Nếu mesh đã là asset và writable → generate trực tiếp
                    if (!string.IsNullOrEmpty(assetPath) && !assetPath.StartsWith("Packages/") && assetPath.EndsWith(".asset"))
                    {
                        GenerateUV2OnMesh(sourceMesh, entry);
                        EditorUtility.SetDirty(sourceMesh);
                        entry.fixed_ = true;
                        entry.fixNote = $"✅ UV2 generated trực tiếp trên asset. PackMargin = {entry.packMargin}";
                        _totalFixed++;
                        continue;
                    }

                    // Mesh không có asset path hoặc embedded → clone và save thành asset mới
                    Mesh clonedMesh = UnityEngine.Object.Instantiate(sourceMesh);
                    clonedMesh.name = sourceMesh.name + "_UV2";

                    GenerateUV2OnMesh(clonedMesh, entry);

                    // Verify UV2 was generated
                    if (clonedMesh.uv2 == null || clonedMesh.uv2.Length == 0)
                    {
                        UnityEngine.Object.DestroyImmediate(clonedMesh);
                        entry.fixNote = "❌ Unwrapping API không tạo được UV2 cho mesh này.";
                        _totalFailed++;
                        continue;
                    }

                    // Save as asset
                    string safeName = SanitizeFileName(clonedMesh.name);
                    string meshAssetPath = AssetDatabase.GenerateUniqueAssetPath($"{outputFolder}/{safeName}.asset");
                    AssetDatabase.CreateAsset(clonedMesh, meshAssetPath);

                    // Replace mesh references in scene
                    Mesh savedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath);
                    int replacedCount = 0;
                    foreach (var go in entry.referencedBy)
                    {
                        if (go == null) continue;
                        var mf = go.GetComponent<MeshFilter>();
                        if (mf != null)
                        {
                            Undo.RecordObject(mf, "Replace mesh with UV2 version");
                            mf.sharedMesh = savedMesh;
                            EditorUtility.SetDirty(mf);
                            replacedCount++;
                        }
                    }

                    entry.mesh = savedMesh;
                    entry.fixed_ = true;
                    entry.fixNote = $"✅ UV2 generated, saved tại {meshAssetPath}. Replaced {replacedCount} reference(s).";
                    _totalFixed++;
                }
                catch (Exception ex)
                {
                    entry.fixNote = $"❌ Error: {ex.Message}";
                    _totalFailed++;
                    Debug.LogError($"[UV Fixer] Failed to generate UV2 for {entry.meshName}: {ex}");
                }
            }
        }

        // ── Core UV2 Generation ──
        private void GenerateUV2OnMesh(Mesh mesh, MeshEntry entry)
        {
            var settings = new UnwrapParam();
            UnwrapParam.SetDefaults(out settings);

            settings.packMargin = entry.packMargin;
            settings.angleError = entry.angleError;
            settings.areaError = entry.areaError;
            settings.hardAngle = entry.hardAngle;

            Unwrapping.GenerateSecondaryUVSet(mesh, settings);
        }

        // ══════════════════════════════════════════════
        // UTILS
        // ══════════════════════════════════════════════
        private static void EnsureFolderExists(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0]; // "Assets"

            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (var c in invalid)
                name = name.Replace(c, '_');

            // Remove Unity clone suffix
            name = name.Replace("(Clone)", "").Trim();

            if (string.IsNullOrEmpty(name))
                name = "mesh";

            return name;
        }
    }
}
#endif
