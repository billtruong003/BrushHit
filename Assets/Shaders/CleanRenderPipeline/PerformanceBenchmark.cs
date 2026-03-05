using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Unity.Profiling;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System;
using System.Linq;

namespace CleanRender
{
    /// <summary>
    /// Performance Benchmark Tool v2 — Unity 6 Compatible
    /// 
    /// Accurate performance measurement using Unity ProfilerRecorder API.
    /// Captures: FPS, Frame Time, CPU/GPU Time, Draw Calls, Batches,
    /// SetPass Calls, Triangles, Vertices, Shadow Casters, GC Alloc,
    /// Memory, Stutter Analysis, Frame Time Distribution.
    ///
    /// Usage:
    ///   Gắn vào GameObject → Play Mode → Inspector bấm Start
    ///   Hoặc dùng Editor Window: Tools → CleanRender → Benchmark
    ///
    /// Hotkeys:
    ///   F8  = Start/Stop benchmark
    ///   F9  = Quick 5s benchmark
    /// </summary>
    [AddComponentMenu("CleanRender/Performance Benchmark v2")]
    public class PerformanceBenchmark : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════
        // Settings
        // ════════════════════════════════════════════════════════════════

        [Header("━━━ Benchmark Settings ━━━")]
        [Tooltip("Thời gian benchmark (giây)")]
        public float benchmarkDuration = 10f;

        [Tooltip("Bỏ qua N frame đầu (warmup để GC/shader compile ổn định)")]
        public int warmupFrames = 60;

        [Tooltip("Tên config hiện tại (ghi vào report)")]
        public string configLabel = "Default";

        [Tooltip("Đường dẫn xuất report (relative to project root)")]
        public string outputFolder = "BenchmarkReports";

        [Header("━━━ Camera Path (Optional) ━━━")]
        [Tooltip("Camera tự di chuyển theo path để benchmark consistent")]
        public Transform[] cameraPath;
        public float pathSpeed = 5f;

        [Header("━━━ Runtime State (Read Only) ━━━")]
        [SerializeField] private bool _isBenchmarking;
        [SerializeField] private bool _isWarmingUp;
        [SerializeField] private int _warmupRemaining;
        [SerializeField] private float _progress;
        [SerializeField] private float _currentFPS;
        [SerializeField] private int _recordedFrames;

        public bool IsBenchmarking => _isBenchmarking;
        public bool IsWarmingUp => _isWarmingUp;
        public float Progress => _progress;
        public float CurrentFPS => _currentFPS;
        public int RecordedFrames => _recordedFrames;
        public BenchmarkResult LastResult { get; private set; }

        // ════════════════════════════════════════════════════════════════
        // ProfilerRecorders — Unity 6 accurate stats
        // ════════════════════════════════════════════════════════════════

        private ProfilerRecorder _drawCallsRecorder;
        private ProfilerRecorder _batchesRecorder;
        private ProfilerRecorder _setPassRecorder;
        private ProfilerRecorder _trianglesRecorder;
        private ProfilerRecorder _verticesRecorder;
        private ProfilerRecorder _shadowCastersRecorder;
        private ProfilerRecorder _gcAllocRecorder;
        private ProfilerRecorder _mainThreadRecorder;
        private ProfilerRecorder _renderThreadRecorder;
        private ProfilerRecorder _totalMemoryRecorder;
        private ProfilerRecorder _gcMemoryRecorder;
        private ProfilerRecorder _gfxMemoryRecorder;
        private ProfilerRecorder _meshMemoryRecorder;
        private ProfilerRecorder _textureMemoryRecorder;
        private ProfilerRecorder _visibleSkinnedMeshesRecorder;
        private ProfilerRecorder _renderTexturesCountRecorder;
        private ProfilerRecorder _renderTexturesBytesRecorder;
        private ProfilerRecorder _usedTexturesCountRecorder;
        private ProfilerRecorder _usedTexturesBytesRecorder;

        // GPU timing via FrameTimingManager
        private FrameTiming[] _frameTimings = new FrameTiming[3];

        // ════════════════════════════════════════════════════════════════
        // Frame Data Storage
        // ════════════════════════════════════════════════════════════════

        private struct FrameData
        {
            public float deltaTime;      // seconds
            public float cpuMainMs;       // main thread ms
            public float cpuRenderMs;     // render thread ms
            public float gpuMs;           // gpu ms
            public int drawCalls;
            public int batches;
            public int setPassCalls;
            public long triangles;
            public long vertices;
            public int shadowCasters;
            public long gcAllocBytes;     // GC alloc THIS frame
            public int visibleSkinnedMeshes;
        }

        private List<FrameData> _frames = new List<FrameData>(8192);
        private float _benchmarkStartTime;
        private int _cameraPathIndex;
        private float _cameraPathT;

        // Comparison snapshots
        private BenchmarkResult _beforeResult;

        // Event for editor window
        public static event Action<PerformanceBenchmark> OnBenchmarkComplete;

        // ════════════════════════════════════════════════════════════════
        // Lifecycle
        // ════════════════════════════════════════════════════════════════

        private void OnEnable()
        {
            EnableRecorders();
        }

        private void OnDisable()
        {
            if (_isBenchmarking) StopBenchmark();
            DisposeRecorders();
        }

        private void EnableRecorders()
        {
            // Rendering stats
            _drawCallsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _batchesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _setPassRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _trianglesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _verticesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
            _shadowCastersRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Shadow Casters Count");
            _visibleSkinnedMeshesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Visible Skinned Meshes Count");

            // Memory stats
            _gcAllocRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            _totalMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Used Memory");
            _gcMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Used Memory");
            _gfxMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Gfx Used Memory");
            _textureMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Texture Memory");
            _meshMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Mesh Memory");
            _renderTexturesCountRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Render Textures Count");
            _renderTexturesBytesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Render Textures Bytes");
            _usedTexturesCountRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Used Textures Count");
            _usedTexturesBytesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Used Textures Bytes");

            // CPU timing
            _mainThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 15);
            _renderThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Render Thread", 15);
        }

        private void DisposeRecorders()
        {
            _drawCallsRecorder.Dispose();
            _batchesRecorder.Dispose();
            _setPassRecorder.Dispose();
            _trianglesRecorder.Dispose();
            _verticesRecorder.Dispose();
            _shadowCastersRecorder.Dispose();
            _visibleSkinnedMeshesRecorder.Dispose();
            _gcAllocRecorder.Dispose();
            _totalMemoryRecorder.Dispose();
            _gcMemoryRecorder.Dispose();
            _gfxMemoryRecorder.Dispose();
            _textureMemoryRecorder.Dispose();
            _meshMemoryRecorder.Dispose();
            _renderTexturesCountRecorder.Dispose();
            _renderTexturesBytesRecorder.Dispose();
            _usedTexturesCountRecorder.Dispose();
            _usedTexturesBytesRecorder.Dispose();
            _mainThreadRecorder.Dispose();
            _renderThreadRecorder.Dispose();
        }

        // ════════════════════════════════════════════════════════════════
        // Update Loop
        // ════════════════════════════════════════════════════════════════

        private void Update()
        {
            // Hotkeys
            if (Input.GetKeyDown(KeyCode.F8))
            {
                if (_isBenchmarking) StopBenchmark();
                else StartBenchmark();
            }
            if (Input.GetKeyDown(KeyCode.F9))
            {
                benchmarkDuration = 5f;
                StartBenchmark();
            }

            if (!_isBenchmarking) return;

            // Always capture GPU timing
            FrameTimingManager.CaptureFrameTimings();

            // Warmup phase — still count frames, just don't record
            if (_isWarmingUp)
            {
                _warmupRemaining--;
                _currentFPS = 1f / Time.unscaledDeltaTime;
                if (_warmupRemaining <= 0)
                {
                    _isWarmingUp = false;
                    _benchmarkStartTime = Time.realtimeSinceStartup;
                    _frames.Clear();
                    Debug.Log("[Benchmark] Warmup complete. Recording...");
                }
                return;
            }

            // ── Record this frame ──
            RecordFrame();

            // Camera path
            if (cameraPath != null && cameraPath.Length >= 2)
                UpdateCameraPath();

            // Progress & display
            float elapsed = Time.realtimeSinceStartup - _benchmarkStartTime;
            _progress = Mathf.Clamp01(elapsed / benchmarkDuration);
            _currentFPS = 1f / Time.unscaledDeltaTime;
            _recordedFrames = _frames.Count;

            // Auto stop
            if (elapsed >= benchmarkDuration)
                StopBenchmark();
        }

        private void RecordFrame()
        {
            var data = new FrameData();

            // Delta time
            data.deltaTime = Time.unscaledDeltaTime;

            // CPU timing from ProfilerRecorder
            data.cpuMainMs = GetRecorderMs(_mainThreadRecorder);
            data.cpuRenderMs = GetRecorderMs(_renderThreadRecorder);

            // GPU timing from FrameTimingManager
            uint timingCount = FrameTimingManager.GetLatestTimings(1, _frameTimings);
            if (timingCount > 0)
            {
                data.gpuMs = (float)_frameTimings[0].gpuFrameTime;
                // Also get more accurate CPU if available
                if (_frameTimings[0].cpuFrameTime > 0)
                    data.cpuMainMs = (float)_frameTimings[0].cpuFrameTime;
            }

            // Rendering stats
            data.drawCalls = GetRecorderValue(_drawCallsRecorder);
            data.batches = GetRecorderValue(_batchesRecorder);
            data.setPassCalls = GetRecorderValue(_setPassRecorder);
            data.triangles = GetRecorderLong(_trianglesRecorder);
            data.vertices = GetRecorderLong(_verticesRecorder);
            data.shadowCasters = GetRecorderValue(_shadowCastersRecorder);
            data.visibleSkinnedMeshes = GetRecorderValue(_visibleSkinnedMeshesRecorder);

            // GC alloc this frame
            data.gcAllocBytes = GetRecorderLong(_gcAllocRecorder);

            _frames.Add(data);
        }

        private static int GetRecorderValue(ProfilerRecorder recorder)
        {
            return recorder.Valid && recorder.Count > 0 ? (int)recorder.LastValue : 0;
        }

        private static long GetRecorderLong(ProfilerRecorder recorder)
        {
            return recorder.Valid && recorder.Count > 0 ? recorder.LastValue : 0;
        }

        private static float GetRecorderMs(ProfilerRecorder recorder)
        {
            return recorder.Valid && recorder.Count > 0
                ? recorder.LastValue * 1e-6f  // nanoseconds → ms
                : 0f;
        }

        // ════════════════════════════════════════════════════════════════
        // Public API
        // ════════════════════════════════════════════════════════════════

        public void StartBenchmark()
        {
            if (_isBenchmarking) return;

            _frames.Clear();
            _warmupRemaining = warmupFrames;
            _isWarmingUp = warmupFrames > 0;
            _isBenchmarking = true;
            _progress = 0;
            _recordedFrames = 0;
            _cameraPathIndex = 0;
            _cameraPathT = 0;

            if (!_isWarmingUp)
                _benchmarkStartTime = Time.realtimeSinceStartup;

            Debug.Log($"[Benchmark] Started — duration={benchmarkDuration}s, " +
                $"warmup={warmupFrames} frames, label='{configLabel}'");
        }

        public void StopBenchmark()
        {
            if (!_isBenchmarking) return;
            _isBenchmarking = false;
            _isWarmingUp = false;

            if (_frames.Count < 2)
            {
                Debug.LogWarning("[Benchmark] Not enough frames recorded. Try longer duration.");
                return;
            }

            // Build result
            LastResult = BuildResult();

            // Export
            string path = ExportReport(LastResult);

            Debug.Log($"[Benchmark] Complete — {LastResult.totalFrames} frames, " +
                $"Avg FPS: {LastResult.avgFPS:F1}, " +
                $"1% Low: {LastResult.fps1Low:F1}, " +
                $"Batches: {LastResult.avgBatches:F0}, " +
                $"Report: {path}");

            OnBenchmarkComplete?.Invoke(this);
        }

        public void SetAsBefore()
        {
            _beforeResult = LastResult;
            Debug.Log("[Benchmark] Saved as BEFORE snapshot.");
        }

        public void GenerateComparison()
        {
            if (_beforeResult == null || LastResult == null)
            {
                Debug.LogWarning("[Benchmark] Need both BEFORE and AFTER results.");
                return;
            }
            ExportComparisonReport(_beforeResult, LastResult);
        }

        // ════════════════════════════════════════════════════════════════
        // Result Builder
        // ════════════════════════════════════════════════════════════════

        [Serializable]
        public class BenchmarkResult
        {
            // Meta
            public string label;
            public string timestamp;
            public string sceneName;
            public float durationSeconds;
            public int totalFrames;
            public string resolution;

            // FPS
            public float avgFPS, medianFPS, minFPS, maxFPS;
            public float fps1Low, fps01Low;
            public float fpsStdDev;

            // Frame Time (ms)
            public float avgFrameTime, maxFrameTime, minFrameTime;
            public float frameTime95th, frameTime99th, frameTime999th;
            public float frameTimeStdDev;

            // CPU (ms)
            public float avgCpuMain, maxCpuMain, minCpuMain;
            public float avgCpuRender, maxCpuRender;
            public float cpuMainStdDev;

            // GPU (ms)
            public float avgGpu, maxGpu, minGpu;
            public float gpuStdDev;
            public bool gpuDataAvailable;

            // Rendering
            public float avgDrawCalls, maxDrawCalls, minDrawCalls;
            public float avgBatches, maxBatches, minBatches;
            public float avgSetPass, maxSetPass;
            public float avgTriangles, maxTriangles;
            public float avgVertices, maxVertices;
            public float avgShadowCasters, maxShadowCasters;
            public float avgVisibleSkinned;

            // Memory (captured at end of benchmark)
            public long totalMemoryMB;
            public long gcMemoryMB;
            public long gfxMemoryMB;
            public long textureMemoryMB;
            public long meshMemoryMB;
            public int renderTextureCount;
            public long renderTexturesMB;
            public int usedTextureCount;
            public long usedTexturesMB;

            // GC
            public float avgGcAllocPerFrame;    // bytes
            public long maxGcAllocPerFrame;
            public long totalGcAlloc;
            public int gcSpikeFrames;           // frames with >1KB GC alloc

            // Stutter
            public int stutterFrames;           // >2x avg
            public float stutterPercent;
            public int severeStutterFrames;     // >3x avg
            public float longestStutterMs;

            // Bottleneck
            public string bottleneck;

            // Frame time buckets
            public int[] frameTimeBuckets;      // [<8ms, 8-11, 11-16, 16-22, 22-33, 33-50, 50-100, >100]
        }

        private BenchmarkResult BuildResult()
        {
            var r = new BenchmarkResult();
            int n = _frames.Count;

            r.label = configLabel;
            r.timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            r.sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            r.durationSeconds = Time.realtimeSinceStartup - _benchmarkStartTime;
            r.totalFrames = n;
            r.resolution = $"{Screen.width}x{Screen.height}";

            // ── FPS ──
            var fpsList = new float[n];
            var frameTimes = new float[n];
            for (int i = 0; i < n; i++)
            {
                frameTimes[i] = _frames[i].deltaTime * 1000f; // ms
                fpsList[i] = 1f / Mathf.Max(_frames[i].deltaTime, 0.00001f);
            }

            Array.Sort(fpsList);
            Array.Sort(frameTimes);

            float fpsSum = 0;
            for (int i = 0; i < n; i++) fpsSum += fpsList[i];
            r.avgFPS = fpsSum / n;
            r.medianFPS = fpsList[n / 2];
            r.minFPS = fpsList[0];
            r.maxFPS = fpsList[n - 1];
            r.fps1Low = AverageBottom(fpsList, 0.01f);
            r.fps01Low = AverageBottom(fpsList, 0.001f);
            r.fpsStdDev = StdDev(fpsList, r.avgFPS);

            // ── Frame Time ──
            float ftSum = 0;
            for (int i = 0; i < n; i++) ftSum += frameTimes[i];
            r.avgFrameTime = ftSum / n;
            r.minFrameTime = frameTimes[0];
            r.maxFrameTime = frameTimes[n - 1];
            r.frameTime95th = Percentile(frameTimes, 0.95f);
            r.frameTime99th = Percentile(frameTimes, 0.99f);
            r.frameTime999th = Percentile(frameTimes, 0.999f);
            r.frameTimeStdDev = StdDev(frameTimes, r.avgFrameTime);

            // ── CPU ──
            r.avgCpuMain = Average(_frames, f => f.cpuMainMs);
            r.maxCpuMain = Max(_frames, f => f.cpuMainMs);
            r.minCpuMain = Min(_frames, f => f.cpuMainMs);
            r.avgCpuRender = Average(_frames, f => f.cpuRenderMs);
            r.maxCpuRender = Max(_frames, f => f.cpuRenderMs);
            r.cpuMainStdDev = StdDev(_frames.Select(f => f.cpuMainMs).ToArray(), r.avgCpuMain);

            // ── GPU ──
            var gpuFrames = _frames.Where(f => f.gpuMs > 0).ToList();
            r.gpuDataAvailable = gpuFrames.Count > n * 0.5f;
            if (gpuFrames.Count > 0)
            {
                r.avgGpu = gpuFrames.Average(f => f.gpuMs);
                r.maxGpu = gpuFrames.Max(f => f.gpuMs);
                r.minGpu = gpuFrames.Min(f => f.gpuMs);
                r.gpuStdDev = StdDev(gpuFrames.Select(f => f.gpuMs).ToArray(), r.avgGpu);
            }

            // ── Rendering Stats ──
            r.avgDrawCalls = Average(_frames, f => f.drawCalls);
            r.maxDrawCalls = Max(_frames, f => f.drawCalls);
            r.minDrawCalls = Min(_frames, f => f.drawCalls);
            r.avgBatches = Average(_frames, f => f.batches);
            r.maxBatches = Max(_frames, f => f.batches);
            r.minBatches = Min(_frames, f => f.batches);
            r.avgSetPass = Average(_frames, f => f.setPassCalls);
            r.maxSetPass = Max(_frames, f => f.setPassCalls);
            r.avgTriangles = Average(_frames, f => (float)f.triangles);
            r.maxTriangles = Max(_frames, f => (float)f.triangles);
            r.avgVertices = Average(_frames, f => (float)f.vertices);
            r.maxVertices = Max(_frames, f => (float)f.vertices);
            r.avgShadowCasters = Average(_frames, f => f.shadowCasters);
            r.maxShadowCasters = Max(_frames, f => f.shadowCasters);
            r.avgVisibleSkinned = Average(_frames, f => f.visibleSkinnedMeshes);

            // ── Memory (snapshot at end) ──
            r.totalMemoryMB = GetRecorderLong(_totalMemoryRecorder) / (1024 * 1024);
            r.gcMemoryMB = GetRecorderLong(_gcMemoryRecorder) / (1024 * 1024);
            r.gfxMemoryMB = GetRecorderLong(_gfxMemoryRecorder) / (1024 * 1024);
            r.textureMemoryMB = GetRecorderLong(_textureMemoryRecorder) / (1024 * 1024);
            r.meshMemoryMB = GetRecorderLong(_meshMemoryRecorder) / (1024 * 1024);
            r.renderTextureCount = GetRecorderValue(_renderTexturesCountRecorder);
            r.renderTexturesMB = GetRecorderLong(_renderTexturesBytesRecorder) / (1024 * 1024);
            r.usedTextureCount = GetRecorderValue(_usedTexturesCountRecorder);
            r.usedTexturesMB = GetRecorderLong(_usedTexturesBytesRecorder) / (1024 * 1024);

            // Fallback memory if recorders didn't work
            if (r.totalMemoryMB == 0)
            {
                r.totalMemoryMB = Profiler.GetTotalReservedMemoryLong() / (1024 * 1024);
                r.gcMemoryMB = Profiler.GetMonoUsedSizeLong() / (1024 * 1024);
                r.gfxMemoryMB = Profiler.GetAllocatedMemoryForGraphicsDriver() / (1024 * 1024);
            }

            // ── GC ──
            long totalGc = 0;
            long maxGc = 0;
            int gcSpikes = 0;
            for (int i = 0; i < n; i++)
            {
                long gc = _frames[i].gcAllocBytes;
                totalGc += gc;
                if (gc > maxGc) maxGc = gc;
                if (gc > 1024) gcSpikes++; // >1KB = spike
            }
            r.totalGcAlloc = totalGc;
            r.avgGcAllocPerFrame = (float)totalGc / n;
            r.maxGcAllocPerFrame = maxGc;
            r.gcSpikeFrames = gcSpikes;

            // ── Stutter ──
            float avgDt = r.avgFrameTime;
            int stutters = 0, severe = 0;
            float longestStutter = 0;
            for (int i = 0; i < n; i++)
            {
                float ms = _frames[i].deltaTime * 1000f;
                if (ms > avgDt * 2f) stutters++;
                if (ms > avgDt * 3f) severe++;
                if (ms > longestStutter) longestStutter = ms;
            }
            r.stutterFrames = stutters;
            r.stutterPercent = (float)stutters / n * 100f;
            r.severeStutterFrames = severe;
            r.longestStutterMs = longestStutter;

            // ── Bottleneck ──
            if (r.gpuDataAvailable)
            {
                if (r.avgCpuMain > r.avgGpu * 1.3f) r.bottleneck = "CPU BOUND";
                else if (r.avgGpu > r.avgCpuMain * 1.3f) r.bottleneck = "GPU BOUND";
                else r.bottleneck = "BALANCED";
            }
            else
            {
                r.bottleneck = "CPU BOUND (no GPU data)";
            }

            // ── Frame Time Distribution ──
            // [<8ms, 8-11, 11-16, 16-22, 22-33, 33-50, 50-100, >100]
            r.frameTimeBuckets = new int[8];
            for (int i = 0; i < n; i++)
            {
                float ms = _frames[i].deltaTime * 1000f;
                if (ms < 8) r.frameTimeBuckets[0]++;
                else if (ms < 11.1f) r.frameTimeBuckets[1]++;
                else if (ms < 16.7f) r.frameTimeBuckets[2]++;
                else if (ms < 22.2f) r.frameTimeBuckets[3]++;
                else if (ms < 33.3f) r.frameTimeBuckets[4]++;
                else if (ms < 50f) r.frameTimeBuckets[5]++;
                else if (ms < 100f) r.frameTimeBuckets[6]++;
                else r.frameTimeBuckets[7]++;
            }

            return r;
        }

        // ════════════════════════════════════════════════════════════════
        // Report Export
        // ════════════════════════════════════════════════════════════════

        private string ExportReport(BenchmarkResult r)
        {
            var sb = new StringBuilder(4096);
            string sep = new string('═', 75);
            string thin = new string('─', 75);

            sb.AppendLine(sep);
            sb.AppendLine("  PERFORMANCE BENCHMARK REPORT v2");
            sb.AppendLine($"  Label:      {r.label}");
            sb.AppendLine($"  Scene:      {r.sceneName}");
            sb.AppendLine($"  Time:       {r.timestamp}");
            sb.AppendLine($"  Duration:   {r.durationSeconds:F1}s ({r.totalFrames} frames)");
            sb.AppendLine($"  Resolution: {r.resolution}");
            sb.AppendLine($"  Bottleneck: {r.bottleneck}");
            sb.AppendLine(sep);
            sb.AppendLine();

            // ── FPS ──
            sb.AppendLine($"── FPS {thin.Substring(6)}");
            sb.AppendLine($"  Average:           {r.avgFPS,10:F1}");
            sb.AppendLine($"  Median:            {r.medianFPS,10:F1}");
            sb.AppendLine($"  Min:               {r.minFPS,10:F1}");
            sb.AppendLine($"  Max:               {r.maxFPS,10:F1}");
            sb.AppendLine($"  1% Low:            {r.fps1Low,10:F1}");
            sb.AppendLine($"  0.1% Low:          {r.fps01Low,10:F1}");
            sb.AppendLine($"  Std Dev:           {r.fpsStdDev,10:F1}");
            sb.AppendLine($"  Stability:         {(r.fpsStdDev < r.avgFPS * 0.05f ? "STABLE" : r.fpsStdDev < r.avgFPS * 0.15f ? "MODERATE" : "UNSTABLE"),10}");
            sb.AppendLine();

            // ── Frame Time ──
            sb.AppendLine($"── FRAME TIME (ms) {thin.Substring(18)}");
            sb.AppendLine($"  Average:           {r.avgFrameTime,10:F2}");
            sb.AppendLine($"  Min:               {r.minFrameTime,10:F2}");
            sb.AppendLine($"  Max:               {r.maxFrameTime,10:F2}");
            sb.AppendLine($"  95th Percentile:   {r.frameTime95th,10:F2}");
            sb.AppendLine($"  99th Percentile:   {r.frameTime99th,10:F2}");
            sb.AppendLine($"  99.9th Percentile: {r.frameTime999th,10:F2}");
            sb.AppendLine($"  Std Dev:           {r.frameTimeStdDev,10:F2}");
            sb.AppendLine();

            // ── CPU ──
            sb.AppendLine($"── CPU TIMING (ms) {thin.Substring(18)}");
            sb.AppendLine($"  Main Thread Avg:   {r.avgCpuMain,10:F2}");
            sb.AppendLine($"  Main Thread Max:   {r.maxCpuMain,10:F2}");
            sb.AppendLine($"  Main Thread Min:   {r.minCpuMain,10:F2}");
            sb.AppendLine($"  Main Thread σ:     {r.cpuMainStdDev,10:F2}");
            sb.AppendLine($"  Render Thread Avg: {r.avgCpuRender,10:F2}");
            sb.AppendLine($"  Render Thread Max: {r.maxCpuRender,10:F2}");
            sb.AppendLine();

            // ── GPU ──
            sb.AppendLine($"── GPU TIMING (ms) {thin.Substring(18)}");
            if (r.gpuDataAvailable)
            {
                sb.AppendLine($"  Average:           {r.avgGpu,10:F2}");
                sb.AppendLine($"  Max:               {r.maxGpu,10:F2}");
                sb.AppendLine($"  Min:               {r.minGpu,10:F2}");
                sb.AppendLine($"  Std Dev:           {r.gpuStdDev,10:F2}");
            }
            else
            {
                sb.AppendLine("  (GPU timing not available — enable FrameTimingManager in Player Settings)");
            }
            sb.AppendLine();

            // ── Rendering ──
            sb.AppendLine($"── RENDERING STATS {thin.Substring(18)}");
            sb.AppendLine($"  {"",25} {"Avg",10} {"Min",10} {"Max",10}");
            sb.AppendLine($"  {"Draw Calls",-25} {r.avgDrawCalls,10:F0} {r.minDrawCalls,10:F0} {r.maxDrawCalls,10:F0}");
            sb.AppendLine($"  {"Batches",-25} {r.avgBatches,10:F0} {r.minBatches,10:F0} {r.maxBatches,10:F0}");
            sb.AppendLine($"  {"SetPass Calls",-25} {r.avgSetPass,10:F0} {"",10} {r.maxSetPass,10:F0}");
            sb.AppendLine($"  {"Triangles",-25} {FormatK(r.avgTriangles),10} {"",10} {FormatK(r.maxTriangles),10}");
            sb.AppendLine($"  {"Vertices",-25} {FormatK(r.avgVertices),10} {"",10} {FormatK(r.maxVertices),10}");
            sb.AppendLine($"  {"Shadow Casters",-25} {r.avgShadowCasters,10:F0} {"",10} {r.maxShadowCasters,10:F0}");
            sb.AppendLine($"  {"Visible Skinned Meshes",-25} {r.avgVisibleSkinned,10:F0}");
            sb.AppendLine();

            // ── Memory ──
            sb.AppendLine($"── MEMORY {thin.Substring(9)}");
            sb.AppendLine($"  Total Used:        {r.totalMemoryMB,10} MB");
            sb.AppendLine($"  GC Heap:           {r.gcMemoryMB,10} MB");
            sb.AppendLine($"  Graphics:          {r.gfxMemoryMB,10} MB");
            sb.AppendLine($"  Textures:          {r.textureMemoryMB,10} MB  ({r.usedTextureCount} textures)");
            sb.AppendLine($"  Meshes:            {r.meshMemoryMB,10} MB");
            sb.AppendLine($"  Render Textures:   {r.renderTexturesMB,10} MB  ({r.renderTextureCount} RTs)");
            sb.AppendLine();

            // ── GC Allocation ──
            sb.AppendLine($"── GC ALLOCATION {thin.Substring(16)}");
            sb.AppendLine($"  Avg Per Frame:     {FormatBytes(r.avgGcAllocPerFrame),10}");
            sb.AppendLine($"  Max Per Frame:     {FormatBytes(r.maxGcAllocPerFrame),10}");
            sb.AppendLine($"  Total During Test: {FormatBytes(r.totalGcAlloc),10}");
            sb.AppendLine($"  Spike Frames:      {r.gcSpikeFrames,10} ({(float)r.gcSpikeFrames / r.totalFrames * 100f:F1}% of frames > 1KB)");
            sb.AppendLine($"  Health:            {(r.avgGcAllocPerFrame < 1024 ? "CLEAN" : r.avgGcAllocPerFrame < 4096 ? "ACCEPTABLE" : r.avgGcAllocPerFrame < 32768 ? "WARNING" : "CRITICAL"),10}");
            sb.AppendLine();

            // ── Stutter ──
            sb.AppendLine($"── FRAME STABILITY {thin.Substring(18)}");
            sb.AppendLine($"  Stutter (>2x avg): {r.stutterFrames,10} frames ({r.stutterPercent:F1}%)");
            sb.AppendLine($"  Severe  (>3x avg): {r.severeStutterFrames,10} frames");
            sb.AppendLine($"  Longest Spike:     {r.longestStutterMs,10:F1} ms");
            sb.AppendLine($"  Rating:            {(r.stutterPercent < 0.5f ? "SMOOTH" : r.stutterPercent < 2f ? "ACCEPTABLE" : r.stutterPercent < 5f ? "NOTICEABLE" : "POOR"),10}");
            sb.AppendLine();

            // ── Frame Time Distribution ──
            string[] bucketLabels = {
                "<8ms (>120fps)", "8-11ms (90-120)", "11-17ms (60-90)",
                "17-22ms (45-60)", "22-33ms (30-45)", "33-50ms (20-30)",
                "50-100ms (10-20)", ">100ms (<10fps)"
            };
            sb.AppendLine($"── FRAME TIME DISTRIBUTION {thin.Substring(25)}");
            for (int i = 0; i < 8; i++)
            {
                float pct = (float)r.frameTimeBuckets[i] / r.totalFrames * 100f;
                int barLen = Mathf.Min(40, (int)(pct / 2.5f));
                string bar = new string('█', barLen) + new string('░', 40 - barLen);
                sb.AppendLine($"  {bucketLabels[i],-22} {bar} {pct,5:F1}% ({r.frameTimeBuckets[i]})");
            }
            sb.AppendLine();

            // ── VR Readiness ──
            sb.AppendLine($"── VR READINESS (Quest Target) {thin.Substring(30)}");
            bool batchOk = r.avgBatches <= 100;
            bool triOk = r.avgTriangles <= 200000;
            bool fpsOk = r.fps1Low >= 72;
            bool gpuOk = !r.gpuDataAvailable || r.avgGpu <= 11;
            bool gcOk = r.avgGcAllocPerFrame < 4096;
            sb.AppendLine($"  Batches ≤100:      {(batchOk ? "✓ PASS" : "✗ FAIL"),10}  (avg: {r.avgBatches:F0})");
            sb.AppendLine($"  Triangles ≤200K:   {(triOk ? "✓ PASS" : "✗ FAIL"),10}  (avg: {FormatK(r.avgTriangles)})");
            sb.AppendLine($"  1% Low FPS ≥72:    {(fpsOk ? "✓ PASS" : "✗ FAIL"),10}  (1% low: {r.fps1Low:F1})");
            sb.AppendLine($"  GPU ≤11ms:         {(gpuOk ? "✓ PASS" : "? N/A"),10}  (avg: {(r.gpuDataAvailable ? $"{r.avgGpu:F1}ms" : "no data")})");
            sb.AppendLine($"  GC ≤4KB/frame:     {(gcOk ? "✓ PASS" : "✗ FAIL"),10}  (avg: {FormatBytes(r.avgGcAllocPerFrame)})");
            int passed = (batchOk ? 1 : 0) + (triOk ? 1 : 0) + (fpsOk ? 1 : 0) + (gpuOk ? 1 : 0) + (gcOk ? 1 : 0);
            sb.AppendLine($"  Overall:           {passed}/5 checks passed {(passed >= 4 ? "— READY" : passed >= 3 ? "— ALMOST" : "— NOT READY")}");
            sb.AppendLine();

            // Write file
            string filename = $"Benchmark_{r.label}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string path = GetOutputPath(filename);
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        private void ExportComparisonReport(BenchmarkResult before, BenchmarkResult after)
        {
            var sb = new StringBuilder(4096);
            string sep = new string('═', 75);
            string thin = new string('─', 75);

            sb.AppendLine(sep);
            sb.AppendLine("  PERFORMANCE COMPARISON REPORT");
            sb.AppendLine($"  Before: {before.label} ({before.timestamp})");
            sb.AppendLine($"  After:  {after.label} ({after.timestamp})");
            sb.AppendLine($"  Scene:  {before.sceneName}");
            sb.AppendLine(sep);
            sb.AppendLine();

            // Summary box
            float fpsChange = Pct(before.avgFPS, after.avgFPS, true);
            float cpuChange = Pct(before.avgCpuMain, after.avgCpuMain, false);
            float gpuChange = Pct(before.avgGpu, after.avgGpu, false);
            float batchChange = Pct(before.avgBatches, after.avgBatches, false);
            float triChange = Pct(before.avgTriangles, after.avgTriangles, false);
            float gcChange = Pct(before.avgGcAllocPerFrame, after.avgGcAllocPerFrame, false);

            sb.AppendLine("┌───────────────────────────────────────────────────────┐");
            sb.AppendLine("│               IMPROVEMENT SUMMARY                     │");
            sb.AppendLine("├───────────────────────────────────────────────────────┤");
            sb.AppendLine($"│  FPS:          {FmtPct(fpsChange, true),40} │");
            sb.AppendLine($"│  CPU Time:     {FmtPct(cpuChange, false),40} │");
            sb.AppendLine($"│  GPU Time:     {FmtPct(gpuChange, false),40} │");
            sb.AppendLine($"│  Batches:      {FmtPct(batchChange, false),40} │");
            sb.AppendLine($"│  Triangles:    {FmtPct(triChange, false),40} │");
            sb.AppendLine($"│  GC Alloc:     {FmtPct(gcChange, false),40} │");
            sb.AppendLine("└───────────────────────────────────────────────────────┘");
            sb.AppendLine();

            // Detailed table
            sb.AppendLine($"  {"Metric",-28} {"BEFORE",12} {"AFTER",12} {"CHANGE",12}");
            sb.AppendLine($"  {thin.Substring(2)}");
            Row(sb, "Avg FPS", F1(before.avgFPS), F1(after.avgFPS), Delta(before.avgFPS, after.avgFPS, true));
            Row(sb, "1% Low FPS", F1(before.fps1Low), F1(after.fps1Low), Delta(before.fps1Low, after.fps1Low, true));
            Row(sb, "Min FPS", F1(before.minFPS), F1(after.minFPS), Delta(before.minFPS, after.minFPS, true));
            Row(sb, "FPS Std Dev", F1(before.fpsStdDev), F1(after.fpsStdDev), Delta(before.fpsStdDev, after.fpsStdDev, false));
            sb.AppendLine();
            Row(sb, "Avg Frame Time (ms)", F2(before.avgFrameTime), F2(after.avgFrameTime), Delta(before.avgFrameTime, after.avgFrameTime, false));
            Row(sb, "99th Pctile (ms)", F2(before.frameTime99th), F2(after.frameTime99th), Delta(before.frameTime99th, after.frameTime99th, false));
            sb.AppendLine();
            Row(sb, "CPU Main (ms)", F2(before.avgCpuMain), F2(after.avgCpuMain), Delta(before.avgCpuMain, after.avgCpuMain, false));
            Row(sb, "CPU Render (ms)", F2(before.avgCpuRender), F2(after.avgCpuRender), Delta(before.avgCpuRender, after.avgCpuRender, false));
            Row(sb, "GPU (ms)", F2(before.avgGpu), F2(after.avgGpu), Delta(before.avgGpu, after.avgGpu, false));
            sb.AppendLine();
            Row(sb, "Avg Batches", F0(before.avgBatches), F0(after.avgBatches), Delta(before.avgBatches, after.avgBatches, false));
            Row(sb, "Avg Draw Calls", F0(before.avgDrawCalls), F0(after.avgDrawCalls), Delta(before.avgDrawCalls, after.avgDrawCalls, false));
            Row(sb, "Avg SetPass", F0(before.avgSetPass), F0(after.avgSetPass), Delta(before.avgSetPass, after.avgSetPass, false));
            Row(sb, "Avg Triangles", FormatK(before.avgTriangles), FormatK(after.avgTriangles), Delta(before.avgTriangles, after.avgTriangles, false));
            Row(sb, "Avg Vertices", FormatK(before.avgVertices), FormatK(after.avgVertices), Delta(before.avgVertices, after.avgVertices, false));
            sb.AppendLine();
            Row(sb, "GC Alloc/Frame", FormatBytes(before.avgGcAllocPerFrame), FormatBytes(after.avgGcAllocPerFrame), Delta(before.avgGcAllocPerFrame, after.avgGcAllocPerFrame, false));
            Row(sb, "GC Spikes", $"{before.gcSpikeFrames}", $"{after.gcSpikeFrames}", Delta(before.gcSpikeFrames, after.gcSpikeFrames, false));
            Row(sb, "Stutter %", $"{before.stutterPercent:F1}%", $"{after.stutterPercent:F1}%", Delta(before.stutterPercent, after.stutterPercent, false));
            Row(sb, "Total Memory (MB)", $"{before.totalMemoryMB}", $"{after.totalMemoryMB}", Delta(before.totalMemoryMB, after.totalMemoryMB, false));
            Row(sb, "GFX Memory (MB)", $"{before.gfxMemoryMB}", $"{after.gfxMemoryMB}", Delta(before.gfxMemoryMB, after.gfxMemoryMB, false));
            sb.AppendLine();

            // Verdict
            sb.AppendLine(sep);
            sb.AppendLine("  VERDICT");
            sb.AppendLine(sep);
            if (fpsChange > 5) sb.AppendLine($"  ✓ FPS improved {fpsChange:F1}%");
            else if (fpsChange < -5) sb.AppendLine($"  ✗ FPS regressed {-fpsChange:F1}%");
            else sb.AppendLine($"  ─ FPS unchanged ({fpsChange:+0.0;-0.0}%)");

            if (batchChange < -10) sb.AppendLine($"  ✓ Batches reduced {-batchChange:F0}%");
            if (cpuChange < -10) sb.AppendLine($"  ✓ CPU time reduced {-cpuChange:F1}%");
            if (gpuChange < -10) sb.AppendLine($"  ✓ GPU time reduced {-gpuChange:F1}%");
            if (gcChange < -50) sb.AppendLine($"  ✓ GC alloc reduced {-gcChange:F0}%");

            sb.AppendLine();

            string filename = $"Comparison_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string path = GetOutputPath(filename);
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[Benchmark] Comparison saved: {path}");

#if UNITY_EDITOR
            UnityEditor.EditorUtility.RevealInFinder(path);
#endif
        }

        // ════════════════════════════════════════════════════════════════
        // Camera Path
        // ════════════════════════════════════════════════════════════════

        private void UpdateCameraPath()
        {
            if (Camera.main == null) return;
            _cameraPathT += Time.deltaTime * pathSpeed /
                Mathf.Max(0.01f, Vector3.Distance(
                    cameraPath[_cameraPathIndex].position,
                    cameraPath[(_cameraPathIndex + 1) % cameraPath.Length].position));

            if (_cameraPathT >= 1f)
            {
                _cameraPathT = 0f;
                _cameraPathIndex = (_cameraPathIndex + 1) % cameraPath.Length;
            }

            int next = (_cameraPathIndex + 1) % cameraPath.Length;
            Camera.main.transform.position = Vector3.Lerp(
                cameraPath[_cameraPathIndex].position,
                cameraPath[next].position, _cameraPathT);
            Camera.main.transform.rotation = Quaternion.Slerp(
                cameraPath[_cameraPathIndex].rotation,
                cameraPath[next].rotation, _cameraPathT);
        }

        // ════════════════════════════════════════════════════════════════
        // Math Helpers
        // ════════════════════════════════════════════════════════════════

        private static float Average(List<FrameData> data, Func<FrameData, float> selector)
        {
            if (data.Count == 0) return 0;
            float sum = 0;
            for (int i = 0; i < data.Count; i++) sum += selector(data[i]);
            return sum / data.Count;
        }

        private static float Max(List<FrameData> data, Func<FrameData, float> selector)
        {
            float max = float.MinValue;
            for (int i = 0; i < data.Count; i++) { float v = selector(data[i]); if (v > max) max = v; }
            return max;
        }

        private static float Min(List<FrameData> data, Func<FrameData, float> selector)
        {
            float min = float.MaxValue;
            for (int i = 0; i < data.Count; i++) { float v = selector(data[i]); if (v < min) min = v; }
            return min;
        }

        private static float AverageBottom(float[] sorted, float fraction)
        {
            int count = Mathf.Max(1, (int)(sorted.Length * fraction));
            float sum = 0;
            for (int i = 0; i < count; i++) sum += sorted[i];
            return sum / count;
        }

        private static float Percentile(float[] sorted, float p)
        {
            int idx = Mathf.Min(sorted.Length - 1, (int)(sorted.Length * p));
            return sorted[idx];
        }

        private static float StdDev(float[] values, float mean)
        {
            if (values.Length < 2) return 0;
            float sumSq = 0;
            for (int i = 0; i < values.Length; i++)
            {
                float d = values[i] - mean;
                sumSq += d * d;
            }
            return Mathf.Sqrt(sumSq / (values.Length - 1));
        }

        private static float Pct(float before, float after, bool higherBetter)
        {
            if (Mathf.Abs(before) < 0.001f) return 0;
            return (after - before) / before * 100f;
        }

        // ════════════════════════════════════════════════════════════════
        // Format Helpers
        // ════════════════════════════════════════════════════════════════

        private static string FormatBytes(float bytes)
        {
            if (bytes < 1024) return $"{bytes:F0} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
            return $"{bytes / (1024f * 1024f):F1} MB";
        }

        private static string FormatBytes(long bytes)
        {
            return FormatBytes((float)bytes);
        }

        private static string FormatK(float value)
        {
            if (value < 1000) return $"{value:F0}";
            if (value < 1000000) return $"{value / 1000f:F1}K";
            return $"{value / 1000000f:F2}M";
        }

        private static string F0(float v) => $"{v:F0}";
        private static string F1(float v) => $"{v:F1}";
        private static string F2(float v) => $"{v:F2}";

        private static string FmtPct(float pct, bool higherBetter)
        {
            string arrow = pct > 1 ? (higherBetter ? "▲" : "▼") :
                           pct < -1 ? (higherBetter ? "▼" : "▲") : "─";
            return $"{arrow} {(pct > 0 ? "+" : "")}{pct:F1}%";
        }

        private static string Delta(float before, float after, bool higherBetter)
        {
            float diff = after - before;
            if (Mathf.Abs(diff) < 0.01f) return "  =";
            string icon = (diff > 0 && higherBetter) || (diff < 0 && !higherBetter) ? "✓" : "✗";
            return $"{icon} {(diff > 0 ? "+" : "")}{diff:F1}";
        }

        private static string Delta(long before, long after, bool higherBetter)
        {
            return Delta((float)before, (float)after, higherBetter);
        }

        private static void Row(StringBuilder sb, string metric, string before, string after, string change)
        {
            sb.AppendLine($"  {metric,-28} {before,12} {after,12} {change,12}");
        }

        private string GetOutputPath(string filename)
        {
            string dir = Path.Combine(Application.dataPath, "..", outputFolder);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, filename);
        }

        // ════════════════════════════════════════════════════════════════
        // Runtime GUI Overlay
        // ════════════════════════════════════════════════════════════════

        private GUIStyle _guiStyle;
        private GUIStyle _guiBoldStyle;
        private Texture2D _bgTexture;

        private void OnGUI()
        {
            if (!_isBenchmarking) return;

            if (_guiStyle == null)
            {
                _bgTexture = new Texture2D(1, 1);
                _bgTexture.SetPixel(0, 0, new Color(0, 0, 0, 0.8f));
                _bgTexture.Apply();

                _guiStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    normal = { textColor = Color.white }
                };
                _guiBoldStyle = new GUIStyle(_guiStyle)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize = 14
                };
            }

            float x = 10, y = 10, w = 280;

            GUI.DrawTexture(new Rect(x - 5, y - 5, w + 10, _isWarmingUp ? 50 : 110), _bgTexture);

            if (_isWarmingUp)
            {
                GUI.Label(new Rect(x, y, w, 20), $"WARMING UP... ({_warmupRemaining})", _guiBoldStyle);
                y += 22;
                GUI.Label(new Rect(x, y, w, 20), $"FPS: {_currentFPS:F0}", _guiStyle);
            }
            else
            {
                GUI.Label(new Rect(x, y, w, 20), $"RECORDING: {configLabel}", _guiBoldStyle);
                y += 22;
                GUI.Label(new Rect(x, y, w, 20), $"FPS: {_currentFPS:F0}  |  Frames: {_recordedFrames}", _guiStyle);
                y += 20;

                float elapsed = Time.realtimeSinceStartup - _benchmarkStartTime;
                GUI.Label(new Rect(x, y, w, 20),
                    $"Time: {elapsed:F1}s / {benchmarkDuration:F0}s", _guiStyle);
                y += 22;

                // Progress bar
                GUI.DrawTexture(new Rect(x, y, w, 14), _bgTexture);
                var greenTex = Texture2D.whiteTexture;
                GUI.color = new Color(0.2f, 0.85f, 0.3f);
                GUI.DrawTexture(new Rect(x, y, w * _progress, 14), greenTex);
                GUI.color = Color.white;
                GUI.Label(new Rect(x, y, w, 14), $"  {_progress * 100:F0}%",
                    new GUIStyle(_guiStyle) { fontSize = 10, alignment = TextAnchor.MiddleCenter });
            }
        }
    }
}