#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Linq;

namespace CleanRender
{
    /// <summary>
    /// Editor Window: Tools → CleanRender → Benchmark
    /// Điều khiển benchmark bằng nút bấm, hiện kết quả real-time.
    /// </summary>
    public class BenchmarkEditorWindow : EditorWindow
    {
        private PerformanceBenchmark _benchmark;
        private Vector2 _scrollPos;
        private bool _showRenderingDetails = true;
        private bool _showMemoryDetails = true;
        private bool _showGcDetails = true;
        private bool _showStabilityDetails = true;
        private bool _showVrReadiness = true;
        private bool _autoRepaint;

        // Styles
        private GUIStyle _headerStyle;
        private GUIStyle _subHeaderStyle;
        private GUIStyle _passStyle;
        private GUIStyle _failStyle;
        private GUIStyle _warnStyle;
        private GUIStyle _boxStyle;
        private bool _stylesInitialized;

        [MenuItem("Tools/CleanRender/Benchmark %#b")]
        public static void ShowWindow()
        {
            var w = GetWindow<BenchmarkEditorWindow>("Performance Benchmark");
            w.minSize = new Vector2(420, 500);
        }

        private void OnEnable()
        {
            PerformanceBenchmark.OnBenchmarkComplete += OnComplete;
            EditorApplication.update += RepaintIfNeeded;
        }

        private void OnDisable()
        {
            PerformanceBenchmark.OnBenchmarkComplete -= OnComplete;
            EditorApplication.update -= RepaintIfNeeded;
        }

        private void OnComplete(PerformanceBenchmark b)
        {
            _benchmark = b;
            Repaint();
        }

        private void RepaintIfNeeded()
        {
            if (_benchmark != null && _benchmark.IsBenchmarking)
                Repaint();
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                margin = new RectOffset(0, 0, 8, 4)
            };
            _subHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                margin = new RectOffset(0, 0, 6, 2)
            };
            _passStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = new Color(0.2f, 0.8f, 0.2f) },
                fontStyle = FontStyle.Bold
            };
            _failStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = new Color(0.9f, 0.2f, 0.2f) },
                fontStyle = FontStyle.Bold
            };
            _warnStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = new Color(0.9f, 0.7f, 0.1f) },
                fontStyle = FontStyle.Bold
            };
            _boxStyle = new GUIStyle("helpbox")
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(4, 4, 4, 4)
            };
        }

        private void OnGUI()
        {
            InitStyles();
            FindBenchmark();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            DrawToolbar();
            EditorGUILayout.Space(4);

            if (_benchmark == null)
            {
                EditorGUILayout.HelpBox(
                    "Không tìm thấy PerformanceBenchmark trong scene.\n" +
                    "Bấm 'Create Benchmark Object' hoặc gắn PerformanceBenchmark vào bất kỳ GameObject.",
                    MessageType.Info);

                if (GUILayout.Button("Create Benchmark Object", GUILayout.Height(30)))
                {
                    var go = new GameObject("_PerformanceBenchmark");
                    _benchmark = go.AddComponent<PerformanceBenchmark>();
                    Selection.activeGameObject = go;
                }
            }
            else if (_benchmark.IsBenchmarking)
            {
                DrawLiveStatus();
            }
            else if (_benchmark.LastResult != null)
            {
                DrawResults(_benchmark.LastResult);
            }
            else
            {
                EditorGUILayout.HelpBox("Bấm Start Benchmark để bắt đầu đo.", MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        private void FindBenchmark()
        {
            if (_benchmark != null) return;
            _benchmark = FindAnyObjectByType<PerformanceBenchmark>();
        }

        // ════════════════════════════════════════════════════════════════
        // Toolbar
        // ════════════════════════════════════════════════════════════════

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            bool isPlaying = Application.isPlaying;
            bool isBenchmarking = _benchmark != null && _benchmark.IsBenchmarking;

            GUI.enabled = isPlaying && _benchmark != null && !isBenchmarking;
            if (GUILayout.Button("▶ Start", EditorStyles.toolbarButton, GUILayout.Width(60)))
                _benchmark.StartBenchmark();

            GUI.enabled = isPlaying && isBenchmarking;
            if (GUILayout.Button("■ Stop", EditorStyles.toolbarButton, GUILayout.Width(60)))
                _benchmark.StopBenchmark();

            GUI.enabled = isPlaying && _benchmark != null && !isBenchmarking;
            if (GUILayout.Button("5s Quick", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                _benchmark.benchmarkDuration = 5f;
                _benchmark.StartBenchmark();
            }

            GUILayout.FlexibleSpace();

            GUI.enabled = _benchmark != null && _benchmark.LastResult != null && !isBenchmarking;
            if (GUILayout.Button("Set BEFORE", EditorStyles.toolbarButton, GUILayout.Width(80)))
                _benchmark.SetAsBefore();

            if (GUILayout.Button("Compare", EditorStyles.toolbarButton, GUILayout.Width(65)))
                _benchmark.GenerateComparison();

            GUI.enabled = true;

            if (!isPlaying)
            {
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.HelpBox("Enter Play Mode to benchmark.", MessageType.Warning);
                return;
            }

            EditorGUILayout.EndHorizontal();

            // Settings row
            if (_benchmark != null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Duration:", GUILayout.Width(60));
                _benchmark.benchmarkDuration = EditorGUILayout.FloatField(_benchmark.benchmarkDuration, GUILayout.Width(40));
                EditorGUILayout.LabelField("s", GUILayout.Width(15));

                EditorGUILayout.LabelField("Warmup:", GUILayout.Width(55));
                _benchmark.warmupFrames = EditorGUILayout.IntField(_benchmark.warmupFrames, GUILayout.Width(40));

                EditorGUILayout.LabelField("Label:", GUILayout.Width(40));
                _benchmark.configLabel = EditorGUILayout.TextField(_benchmark.configLabel);
                EditorGUILayout.EndHorizontal();
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Live Status
        // ════════════════════════════════════════════════════════════════

        private void DrawLiveStatus()
        {
            EditorGUILayout.LabelField(_benchmark.IsWarmingUp ? "⏳ Warming Up..." : "🔴 Recording...", _headerStyle);

            // Progress bar
            var rect = EditorGUILayout.GetControlRect(false, 22);
            EditorGUI.ProgressBar(rect, _benchmark.Progress,
                $"{_benchmark.Progress * 100:F0}%  —  {_benchmark.RecordedFrames} frames  —  {_benchmark.CurrentFPS:F0} FPS");

            EditorGUILayout.Space(4);

            if (GUILayout.Button("■ Stop Now", GUILayout.Height(30)))
                _benchmark.StopBenchmark();
        }

        // ════════════════════════════════════════════════════════════════
        // Results Display
        // ════════════════════════════════════════════════════════════════

        private void DrawResults(PerformanceBenchmark.BenchmarkResult r)
        {
            // Header
            EditorGUILayout.LabelField($"Results: {r.label}", _headerStyle);
            EditorGUILayout.LabelField($"{r.sceneName} | {r.resolution} | {r.totalFrames} frames | {r.durationSeconds:F1}s | {r.bottleneck}", EditorStyles.miniLabel);
            EditorGUILayout.Space(4);

            // ── FPS Summary ──
            EditorGUILayout.BeginVertical(_boxStyle);
            EditorGUILayout.LabelField("FPS", _subHeaderStyle);
            ThreeCol("Average", $"{r.avgFPS:F1}", GetFpsColor(r.avgFPS));
            ThreeCol("1% Low", $"{r.fps1Low:F1}", GetFpsColor(r.fps1Low));
            ThreeCol("0.1% Low", $"{r.fps01Low:F1}", GetFpsColor(r.fps01Low));
            ThreeCol("Min / Max", $"{r.minFPS:F1} / {r.maxFPS:F1}", null);
            ThreeCol("Stability", r.fpsStdDev < r.avgFPS * 0.05f ? "STABLE" : r.fpsStdDev < r.avgFPS * 0.15f ? "MODERATE" : "UNSTABLE",
                r.fpsStdDev < r.avgFPS * 0.05f ? _passStyle : r.fpsStdDev < r.avgFPS * 0.15f ? _warnStyle : _failStyle);
            EditorGUILayout.EndVertical();

            // ── Timing ──
            EditorGUILayout.BeginVertical(_boxStyle);
            EditorGUILayout.LabelField("Timing (ms)", _subHeaderStyle);
            ThreeCol("Frame Avg / 99th", $"{r.avgFrameTime:F2} / {r.frameTime99th:F2}", null);
            ThreeCol("CPU Main", $"{r.avgCpuMain:F2} (max {r.maxCpuMain:F2})", null);
            ThreeCol("CPU Render", $"{r.avgCpuRender:F2} (max {r.maxCpuRender:F2})", null);
            if (r.gpuDataAvailable)
                ThreeCol("GPU", $"{r.avgGpu:F2} (max {r.maxGpu:F2})", null);
            else
                ThreeCol("GPU", "N/A", _warnStyle);
            EditorGUILayout.EndVertical();

            // ── Rendering Stats ──
            _showRenderingDetails = EditorGUILayout.BeginFoldoutHeaderGroup(_showRenderingDetails, "Rendering Stats");
            if (_showRenderingDetails)
            {
                EditorGUILayout.BeginVertical(_boxStyle);
                FourCol("", "Avg", "Min", "Max");
                FourCol("Batches", F0(r.avgBatches), F0(r.minBatches), F0(r.maxBatches));
                FourCol("Draw Calls", F0(r.avgDrawCalls), F0(r.minDrawCalls), F0(r.maxDrawCalls));
                FourCol("SetPass", F0(r.avgSetPass), "", F0(r.maxSetPass));
                FourCol("Triangles", FormatK(r.avgTriangles), "", FormatK(r.maxTriangles));
                FourCol("Vertices", FormatK(r.avgVertices), "", FormatK(r.maxVertices));
                FourCol("Shadow Casters", F0(r.avgShadowCasters), "", F0(r.maxShadowCasters));
                FourCol("Skinned Meshes", F0(r.avgVisibleSkinned), "", "");
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ── Memory ──
            _showMemoryDetails = EditorGUILayout.BeginFoldoutHeaderGroup(_showMemoryDetails, "Memory");
            if (_showMemoryDetails)
            {
                EditorGUILayout.BeginVertical(_boxStyle);
                ThreeCol("Total Used", $"{r.totalMemoryMB} MB", null);
                ThreeCol("GC Heap", $"{r.gcMemoryMB} MB", null);
                ThreeCol("Graphics", $"{r.gfxMemoryMB} MB", null);
                ThreeCol("Textures", $"{r.textureMemoryMB} MB ({r.usedTextureCount})", null);
                ThreeCol("Meshes", $"{r.meshMemoryMB} MB", null);
                ThreeCol("Render Textures", $"{r.renderTexturesMB} MB ({r.renderTextureCount})", null);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ── GC ──
            _showGcDetails = EditorGUILayout.BeginFoldoutHeaderGroup(_showGcDetails, "GC Allocation");
            if (_showGcDetails)
            {
                EditorGUILayout.BeginVertical(_boxStyle);
                ThreeCol("Avg/Frame", FormatBytes(r.avgGcAllocPerFrame),
                    r.avgGcAllocPerFrame < 1024 ? _passStyle : r.avgGcAllocPerFrame < 4096 ? _warnStyle : _failStyle);
                ThreeCol("Max/Frame", FormatBytes(r.maxGcAllocPerFrame), null);
                ThreeCol("Total", FormatBytes(r.totalGcAlloc), null);
                ThreeCol("Spike Frames (>1KB)", $"{r.gcSpikeFrames} ({(float)r.gcSpikeFrames / r.totalFrames * 100:F1}%)",
                    r.gcSpikeFrames == 0 ? _passStyle : _warnStyle);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ── Stability ──
            _showStabilityDetails = EditorGUILayout.BeginFoldoutHeaderGroup(_showStabilityDetails, "Frame Stability");
            if (_showStabilityDetails)
            {
                EditorGUILayout.BeginVertical(_boxStyle);
                ThreeCol("Stutter (>2x avg)", $"{r.stutterFrames} ({r.stutterPercent:F1}%)",
                    r.stutterPercent < 1 ? _passStyle : r.stutterPercent < 3 ? _warnStyle : _failStyle);
                ThreeCol("Severe (>3x avg)", $"{r.severeStutterFrames}", null);
                ThreeCol("Longest Spike", $"{r.longestStutterMs:F1} ms", null);

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Frame Time Distribution:", EditorStyles.miniLabel);

                string[] labels = { "<8ms", "8-11ms", "11-17ms", "17-22ms", "22-33ms", "33-50ms", "50-100ms", ">100ms" };
                string[] fpsLabels = { ">120", "90-120", "60-90", "45-60", "30-45", "20-30", "10-20", "<10" };
                Color[] colors = {
                    new Color(0.2f, 0.9f, 0.2f), new Color(0.4f, 0.85f, 0.2f),
                    new Color(0.7f, 0.8f, 0.1f), new Color(0.9f, 0.7f, 0.1f),
                    new Color(0.9f, 0.5f, 0.1f), new Color(0.9f, 0.3f, 0.1f),
                    new Color(0.9f, 0.15f, 0.1f), new Color(0.8f, 0.05f, 0.05f)
                };

                for (int i = 0; i < 8; i++)
                {
                    float pct = r.totalFrames > 0 ? (float)r.frameTimeBuckets[i] / r.totalFrames : 0;
                    var barRect = EditorGUILayout.GetControlRect(false, 16);

                    // Label
                    var labelRect = new Rect(barRect.x, barRect.y, 65, barRect.height);
                    EditorGUI.LabelField(labelRect, labels[i], EditorStyles.miniLabel);

                    // Bar
                    var barArea = new Rect(barRect.x + 68, barRect.y, barRect.width - 150, barRect.height);
                    EditorGUI.DrawRect(new Rect(barArea.x, barArea.y, barArea.width, barArea.height),
                        new Color(0.15f, 0.15f, 0.15f));
                    if (pct > 0)
                    {
                        EditorGUI.DrawRect(new Rect(barArea.x, barArea.y,
                            barArea.width * pct, barArea.height), colors[i]);
                    }

                    // Pct + count
                    var pctRect = new Rect(barArea.xMax + 4, barRect.y, 80, barRect.height);
                    EditorGUI.LabelField(pctRect,
                        $"{pct * 100:F1}% ({r.frameTimeBuckets[i]})", EditorStyles.miniLabel);
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ── VR Readiness ──
            _showVrReadiness = EditorGUILayout.BeginFoldoutHeaderGroup(_showVrReadiness, "VR Readiness (Quest)");
            if (_showVrReadiness)
            {
                EditorGUILayout.BeginVertical(_boxStyle);
                VrCheck("Batches ≤ 100", r.avgBatches <= 100, $"avg: {r.avgBatches:F0}");
                VrCheck("Triangles ≤ 200K", r.avgTriangles <= 200000, $"avg: {FormatK(r.avgTriangles)}");
                VrCheck("1% Low FPS ≥ 72", r.fps1Low >= 72, $"1% low: {r.fps1Low:F1}");
                VrCheck("GPU ≤ 11ms", !r.gpuDataAvailable || r.avgGpu <= 11,
                    r.gpuDataAvailable ? $"avg: {r.avgGpu:F1}ms" : "no GPU data");
                VrCheck("GC ≤ 4KB/frame", r.avgGcAllocPerFrame < 4096, $"avg: {FormatBytes(r.avgGcAllocPerFrame)}");
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ════════════════════════════════════════════════════════════════
        // Layout Helpers
        // ════════════════════════════════════════════════════════════════

        private void ThreeCol(string label, string value, GUIStyle style)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(160));
            EditorGUILayout.LabelField(value, style ?? EditorStyles.label);
            EditorGUILayout.EndHorizontal();
        }

        private void FourCol(string label, string a, string b, string c)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(130));
            EditorGUILayout.LabelField(a, GUILayout.Width(80));
            EditorGUILayout.LabelField(b, GUILayout.Width(80));
            EditorGUILayout.LabelField(c, GUILayout.Width(80));
            EditorGUILayout.EndHorizontal();
        }

        private void VrCheck(string label, bool pass, string detail)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(pass ? "✓" : "✗", pass ? _passStyle : _failStyle, GUILayout.Width(20));
            EditorGUILayout.LabelField(label, GUILayout.Width(160));
            EditorGUILayout.LabelField(detail, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private GUIStyle GetFpsColor(float fps)
        {
            if (fps >= 90) return _passStyle;
            if (fps >= 60) return _warnStyle;
            return _failStyle;
        }

        private static string F0(float v) => $"{v:F0}";
        private static string FormatK(float v) => v < 1000 ? $"{v:F0}" : v < 1000000 ? $"{v / 1000:F1}K" : $"{v / 1000000:F2}M";
        private static string FormatBytes(float b) => b < 1024 ? $"{b:F0} B" : b < 1048576 ? $"{b / 1024:F1} KB" : $"{b / 1048576:F1} MB";
    }
}
#endif