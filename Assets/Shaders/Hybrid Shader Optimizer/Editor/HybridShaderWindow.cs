using System.IO;
using UnityEditor;
using UnityEngine;

namespace HybridShaderOptimizer.Editor
{
    public class HybridShaderWindow : EditorWindow
    {
        private HybridShaderConfig _config;
        private HybridShaderProjectData _data;
        private int _selectedTab = 0;
        private readonly string[] _tabs = { "Deep Scan", "Active Dependencies", "Garbage Collection", "Rules Engine" };
        private Vector2 _scrollPosition;[MenuItem("Tools/Hybrid Shader Optimizer")]
        public static void ShowWindow()
        {
            var window = GetWindow<HybridShaderWindow>("Shader Optimizer");
            window.minSize = new Vector2(800, 600);
            window.Show();
        }

        private void OnEnable()
        {
            _config = HybridShaderConfig.GetOrCreate();
            _data = HybridShaderConfig.GetOrCreateData();
        }

        private void OnGUI()
        {
            if (_config == null || _data == null) return;

            DrawHeader();
            _selectedTab = GUILayout.Toolbar(_selectedTab, _tabs, GUILayout.Height(30));
            EditorGUILayout.Space();

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            switch (_selectedTab)
            {
                case 0: DrawScannerTab(); break;
                case 1: DrawDependenciesTab(); break;
                case 2: DrawGarbageCollectionTab(); break;
                case 3: DrawRulesEngineTab(); break;
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            GUILayout.Label("Hybrid Shader Architecture", new GUIStyle(EditorStyles.boldLabel) { fontSize = 18, alignment = TextAnchor.MiddleCenter, margin = new RectOffset(0, 0, 10, 10) });
            EditorGUILayout.HelpBox("Reverse Dependency Mapping & Automated Rules Engine", MessageType.Info);
        }

        private void DrawScannerTab()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (GUILayout.Button("Run Deep Reverse Dependency Scan", GUILayout.Height(50)))
            {
                ShaderDeepAnalyzer.RunDeepScan();
                Repaint();
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Assets Scanned:", _data.TotalAssetsScanned.ToString());
            EditorGUILayout.LabelField("Active Shaders Identified:", _data.UsedShaders.Count.ToString());
            EditorGUILayout.LabelField("Unused Shaders Found:", _data.UnusedShaderPaths.Count.ToString());
            EditorGUILayout.LabelField("Last Scan Time:", string.IsNullOrEmpty(_data.LastAnalysisTime) ? "Never" : _data.LastAnalysisTime);
        }

        private void DrawDependenciesTab()
        {
            if (_data.UsedShaders.Count == 0)
            {
                EditorGUILayout.HelpBox("Run the Deep Scan first.", MessageType.Warning);
                return;
            }

            foreach (var info in _data.UsedShaders)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(info.ShaderName, EditorStyles.boldLabel);
                
                if (info.HasConvertibleMacros)
                {
                    GUI.backgroundColor = Color.green;
                    if (GUILayout.Button("Optimize multi_compile", GUILayout.Width(180)))
                    {
                        ShaderDeepAnalyzer.ConvertMacros(info.AssetPath);
                        info.HasConvertibleMacros = false;
                    }
                    GUI.backgroundColor = Color.white;
                }
                EditorGUILayout.EndHorizontal();

                EditorGUI.indentLevel++;
                
                if (info.ReferencedInScenes.Count > 0)
                {
                    GUILayout.Label("Scenes:", EditorStyles.miniBoldLabel);
                    foreach (var sc in info.ReferencedInScenes) EditorGUILayout.LabelField(sc, EditorStyles.miniLabel);
                }

                if (info.ReferencedInPrefabs.Count > 0)
                {
                    GUILayout.Label("Prefabs:", EditorStyles.miniBoldLabel);
                    foreach (var pf in info.ReferencedInPrefabs) EditorGUILayout.LabelField(pf, EditorStyles.miniLabel);
                }

                EditorGUI.indentLevel--;
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawGarbageCollectionTab()
        {
            if (_data.UnusedShaderPaths.Count == 0)
            {
                EditorGUILayout.HelpBox("No unused shaders found or scan not executed.", MessageType.Info);
                return;
            }

            GUI.backgroundColor = Color.red;
            if (GUILayout.Button("Delete All Unused Shaders", GUILayout.Height(40)))
            {
                foreach (var path in _data.UnusedShaderPaths)
                {
                    AssetDatabase.DeleteAsset(path);
                }
                _data.UnusedShaderPaths.Clear();
                AssetDatabase.Refresh();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space();

            foreach (var path in _data.UnusedShaderPaths)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField(Path.GetFileName(path), GUILayout.Width(200));
                EditorGUILayout.LabelField(path, EditorStyles.miniLabel);
                if (GUILayout.Button("X", GUILayout.Width(30)))
                {
                    AssetDatabase.DeleteAsset(path);
                    _data.UnusedShaderPaths.Remove(path);
                    AssetDatabase.Refresh();
                    break; 
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawRulesEngineTab()
        {
            SerializedObject serializedConfig = new SerializedObject(_config);
            serializedConfig.Update();

            GUI.backgroundColor = Color.cyan;
            if (GUILayout.Button("⚡ Auto-Detect Project Settings & Suggest Rules", GUILayout.Height(40)))
            {
                ShaderAutoSuggestEngine.AutoDetectAndFillRules(_config);
                Repaint();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space();

            _config.EnableOptimization = EditorGUILayout.Toggle("Enable Global Optimizer", _config.EnableOptimization);
            _config.StrictBuildMode = EditorGUILayout.Toggle("Strict Build Cull", _config.StrictBuildMode);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedConfig.FindProperty("GlobalBlacklistKeywords"), true);
            EditorGUILayout.PropertyField(serializedConfig.FindProperty("BlacklistPasses"), true);
            EditorGUILayout.PropertyField(serializedConfig.FindProperty("WhitelistShaders"), true);

            serializedConfig.ApplyModifiedProperties();
        }
    }
}