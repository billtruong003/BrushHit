// ProfessionalShaderProfiler.cs
// This script must be placed in an "Editor" folder.
#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.IO;
using Unity.EditorCoroutines.Editor;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;

#region Data Models

[Serializable]
public class ProfilerSettings
{
    public enum TargetProfileType { Default, LowEndMobile, HighEndPC, Custom }
    public TargetProfileType CurrentProfile = TargetProfileType.Default;

    public Color LowComplexityColor = new Color(0.2f, 0.8f, 0.2f, 1f);
    public Color MediumComplexityColor = new Color(1f, 0.8f, 0.2f, 1f);
    public Color HighComplexityColor = new Color(1f, 0.2f, 0.2f, 1f);
    public Color GradientStartColor = Color.green;
    public Color GradientEndColor = Color.red;

    public float MediumComplexityThreshold = 100f;
    public float HighComplexityThreshold = 300f;
    public bool UseGradient = true;

    public float PassCostWeight = 30f;
    public float VariantCountWeight = 40f;
    public float TextureMemoryWeight = 2f;
    public float InstructionCountWeight = 1f;
    public float TransparencyWeight = 75f;
}

public class ShaderPassProfile
{
    public int PassIndex { get; }
    public string PassName { get; }
    public string LightMode { get; }

    public ShaderPassProfile(int index, string name, string lightMode)
    {
        PassIndex = index;
        PassName = name;
        LightMode = lightMode;
    }
}

public class MaterialVariantProfile
{
    public string VariantKeywordsKey { get; }
    public Material Material { get; }
    public readonly List<Component> Components = new List<Component>();

    public MaterialVariantProfile(string keywordsKey, Material material)
    {
        VariantKeywordsKey = keywordsKey;
        Material = material;
    }
}

public class ShaderProfile
{
    public Shader Shader { get; }
    public float ComplexityScore { get; set; }
    public int PassCount => Passes.Count;
    public bool IsTransparent { get; }
    public float TextureMemoryUsageMB { get; set; }
    public int InstructionCount { get; set; }
    public int UniqueVariantCount => MaterialVariants.Count;
    public int TotalGameObjectCount { get; private set; }
    public int TextureSampleCount { get; set; }

    public readonly List<ShaderPassProfile> Passes = new List<ShaderPassProfile>();
    public readonly Dictionary<string, MaterialVariantProfile> MaterialVariants = new Dictionary<string, MaterialVariantProfile>();

    public ShaderProfile(Shader shader)
    {
        Shader = shader;
        IsTransparent = IsShaderConsideredTransparent(shader);
    }

    private static bool IsShaderConsideredTransparent(Shader shader)
    {
        string queueTag = "Geometry";
        var getTagValueMethod = typeof(ShaderUtil).GetMethod("GetTagValue", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (getTagValueMethod != null)
        {
            var foundTag = (string)getTagValueMethod.Invoke(null, new object[] { shader, "Queue" });
            if (!string.IsNullOrEmpty(foundTag))
            {
                queueTag = foundTag;
            }
        }
        return queueTag.Equals("Transparent", StringComparison.OrdinalIgnoreCase) ||
               shader.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    public void FinalizeComponentCounts()
    {
        TotalGameObjectCount = MaterialVariants.Values.Sum(variant => variant.Components.Count);
    }
}

#endregion

public class ProfessionalShaderProfiler : EditorWindow
{
    #region Private Fields

    private List<ShaderProfile> _shaderProfiles = new List<ShaderProfile>();
    private readonly Dictionary<Component, ShaderProfile> _componentToProfileMap = new Dictionary<Component, ShaderProfile>();
    private float _minScore, _maxScore;

    private Vector2 _bottomPanelScrollPos;
    private bool _settingsFoldout = false;
    private bool _isCapturing = false;
    private bool _isHeatmapVisible = true;
    private EditorCoroutine _captureCoroutine;

    [SerializeField] private TreeViewState _treeViewState;
    [SerializeField] private MultiColumnHeaderState _multiColumnHeaderState;
    private ShaderProfilerTreeView _treeView;
    private TreeViewItem _selectedItem;

    private ProfilerSettings _settings;
    private const string SETTINGS_PREF_KEY = "ProfessionalShaderProfiler_Settings_V5";
    private const int ANALYSIS_BATCH_SIZE = 50;

    private static readonly Dictionary<string, float> COMPLEX_INSTRUCTION_COSTS = new Dictionary<string, float>
    {
        { "noise", 12.0f }, { "inverse", 10.0f }, { "ddx", 6.0f }, { "ddy", 6.0f }, { "fwidth", 6.0f },
        { "pow", 4.0f }, { "exp", 3.5f }, { "exp2", 3.5f }, { "log", 3.5f }, { "log2", 3.5f },
        { "sin", 3.0f }, { "cos", 3.0f }, { "tan", 3.8f }, { "asin", 4.5f }, { "acos", 4.5f }, { "atan", 4.0f },
        { "atan2", 4.5f }, { "reflect", 2.5f }, { "refract", 4.0f }, { "sqrt", 2.0f }, { "rsqrt", 1.8f },
        { "normalize", 3.0f }, { "length", 2.5f }, { "distance", 2.8f }, { "determinant", 3.0f },
        { "lerp", 1.2f }, { "smoothstep", 2.5f }, { "step", 1.1f }, { "clamp", 1.2f }, { "saturate", 1.1f },
        { "frac", 1.0f }, { "floor", 1.0f }, { "ceil", 1.0f }, { "sign", 1.0f }, { "abs", 1.0f },
        { "dot", 1.5f }, { "cross", 1.8f }
    };

    private static readonly Dictionary<string, float> TEXTURE_SAMPLER_COSTS = new Dictionary<string, float>
    {
        { "tex2Dgrad", 5.0f }, { "texCUBEgrad", 5.5f }, { "tex2Dlod", 4.0f }, { "texCUBElod", 4.5f },
        { "tex3Dlod", 4.5f }, { "tex2Dproj", 2.0f }, { "tex3Dproj", 2.2f }, { "texCUBEproj", 2.5f },
        { "tex2D", 1.0f }, { "texCUBE", 1.2f }, { "tex3D", 1.2f }, { "tex2DMS", 1.5f }
    };

    private static MethodInfo _getShaderPassNameMethod;
    private static MethodInfo _getPassLightModeMethod;
    private static MethodInfo _getShaderActiveSubshaderIndexMethod;

    #endregion

    #region Window Management

    [MenuItem("Window/Analysis/Shader Profiler")]
    public static void ShowWindow()
    {
        GetWindow<ProfessionalShaderProfiler>("Pro Shader Profiler");
    }

    private void OnEnable()
    {
        LoadSettings();
        InitializeShaderUtilReflection();
        InitializeTreeView();
        SceneView.duringSceneGui += OnSceneGUI;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        StopCaptureCoroutine();
        SaveSettings();
    }

    #endregion

    #region GUI Drawing

    private void OnGUI()
    {
        DrawToolbar();
        DrawTreeView();
        DrawBottomPanel();
    }

    private void InitializeTreeView()
    {
        _treeViewState ??= new TreeViewState();

        var headerState = ShaderProfilerTreeView.CreateDefaultMultiColumnHeaderState();
        if (MultiColumnHeaderState.CanOverwriteSerializedFields(_multiColumnHeaderState, headerState))
        {
            MultiColumnHeaderState.OverwriteSerializedFields(_multiColumnHeaderState, headerState);
        }
        _multiColumnHeaderState = headerState;

        var multiColumnHeader = new MultiColumnHeader(headerState);
        multiColumnHeader.sortingChanged += SortTreeView;

        _treeView = new ShaderProfilerTreeView(_treeViewState, multiColumnHeader, _shaderProfiles, GetColorForScore);
        _treeView.OnSelectionChanged += OnTreeViewSelectionChanged;
    }

    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        using (new EditorGUI.DisabledScope(_isCapturing))
        {
            if (GUILayout.Button(new GUIContent("Capture Scene", EditorGUIUtility.IconContent("d_Refresh").image), EditorStyles.toolbarButton))
            {
                StartCaptureCoroutine();
            }
        }

        bool newHeatmapValue = GUILayout.Toggle(_isHeatmapVisible, "Show Heatmap", EditorStyles.toolbarButton);
        if (newHeatmapValue != _isHeatmapVisible)
        {
            _isHeatmapVisible = newHeatmapValue;
            SceneView.RepaintAll();
        }

        if (GUILayout.Button("Expand All", EditorStyles.toolbarButton)) _treeView.ExpandAll();
        if (GUILayout.Button("Collapse All", EditorStyles.toolbarButton)) _treeView.CollapseAll();

        GUILayout.FlexibleSpace();

        if (GUILayout.Button(new GUIContent("Export CSV", EditorGUIUtility.IconContent("d_SaveAs").image), EditorStyles.toolbarButton))
        {
            ExportToCsv();
        }

        _settingsFoldout = GUILayout.Toggle(_settingsFoldout, new GUIContent(" Settings", EditorGUIUtility.IconContent("d_Settings").image), EditorStyles.toolbarButton);

        EditorGUILayout.EndHorizontal();
    }

    private void DrawTreeView()
    {
        Rect treeViewRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandHeight(true));
        _treeView.OnGUI(treeViewRect);
    }

    private void DrawBottomPanel()
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MinHeight(120), GUILayout.MaxHeight(300));
        _bottomPanelScrollPos = EditorGUILayout.BeginScrollView(_bottomPanelScrollPos);

        if (_settingsFoldout)
        {
            DrawSettingsPanel();
        }
        else
        {
            DrawDetailedInformationPanel();
        }
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();
    }

    private void DrawSettingsPanel()
    {
        EditorGUILayout.LabelField("Profiler Settings", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        _settings.CurrentProfile = (ProfilerSettings.TargetProfileType)EditorGUILayout.EnumPopup("Target Profile", _settings.CurrentProfile);
        if (EditorGUI.EndChangeCheck())
        {
            ApplyProfileSettings(_settings.CurrentProfile);
        }
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Heatmap Colors", EditorStyles.boldLabel);
        _settings.UseGradient = EditorGUILayout.Toggle("Use Gradient", _settings.UseGradient);
        if (_settings.UseGradient)
        {
            _settings.GradientStartColor = EditorGUILayout.ColorField("Low Color (Gradient)", _settings.GradientStartColor);
            _settings.GradientEndColor = EditorGUILayout.ColorField("High Color (Gradient)", _settings.GradientEndColor);
        }
        else
        {
            _settings.LowComplexityColor = EditorGUILayout.ColorField("Low Color", _settings.LowComplexityColor);
            _settings.MediumComplexityColor = EditorGUILayout.ColorField("Medium Color", _settings.MediumComplexityColor);
            _settings.HighComplexityColor = EditorGUILayout.ColorField("High Color", _settings.HighComplexityColor);
            _settings.MediumComplexityThreshold = EditorGUILayout.FloatField("Medium Threshold", _settings.MediumComplexityThreshold);
            _settings.HighComplexityThreshold = EditorGUILayout.FloatField("High Threshold", _settings.HighComplexityThreshold);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Score Calculation Weights (Custom)", EditorStyles.boldLabel);
        _settings.PassCostWeight = EditorGUILayout.FloatField("Pass Cost Weight", _settings.PassCostWeight);
        _settings.VariantCountWeight = EditorGUILayout.FloatField("Variant Count Weight", _settings.VariantCountWeight);
        _settings.TextureMemoryWeight = EditorGUILayout.FloatField("Tex Memory Weight (per MB)", _settings.TextureMemoryWeight);
        _settings.InstructionCountWeight = EditorGUILayout.FloatField("Instruction Weight", _settings.InstructionCountWeight);
        _settings.TransparencyWeight = EditorGUILayout.FloatField("Transparency Penalty", _settings.TransparencyWeight);
    }

    private void DrawDetailedInformationPanel()
    {
        EditorGUILayout.LabelField("Detailed Information & Suggestions", EditorStyles.boldLabel);
        if (_selectedItem is not ProfilerTreeViewItem item || item.ShaderProfile == null)
        {
            EditorGUILayout.LabelField("Chọn một mục trong danh sách trên để xem chi tiết và mẹo tối ưu hóa.", EditorStyles.wordWrappedLabel);
            return;
        }

        switch (item.ItemType)
        {
            case ProfilerTreeViewItem.Type.Pass:
                DrawPassDetails(item.PassProfile);
                break;
            case ProfilerTreeViewItem.Type.Variant:
                DrawVariantDetails(item.VariantProfile);
                break;
            case ProfilerTreeViewItem.Type.Shader:
                DrawShaderDetails(item.ShaderProfile);
                break;
            case ProfilerTreeViewItem.Type.Component:
                EditorGUILayout.LabelField("Đối tượng được chọn:", EditorStyles.boldLabel);
                EditorGUILayout.ObjectField(item.Component.gameObject, typeof(GameObject), true);
                break;
        }
    }

    private void DrawShaderDetails(ShaderProfile profile)
    {
        EditorGUILayout.LabelField("Shader được chọn:", EditorStyles.boldLabel);
        EditorGUILayout.ObjectField(profile.Shader, typeof(Shader), false);
        EditorGUILayout.LabelField("Điểm phức tạp tổng thể", profile.ComplexityScore.ToString("F0"));
        EditorGUILayout.LabelField("Tổng số đối tượng sử dụng", profile.TotalGameObjectCount.ToString());
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Gợi ý Tối ưu hóa:", EditorStyles.boldLabel);

        bool hasTips = false;

        bool isLikelyALUBound = profile.InstructionCount > 30 && profile.TextureSampleCount < profile.InstructionCount / 4;
        bool isLikelyBandwidthBound = profile.TextureMemoryUsageMB > 8 && profile.TextureSampleCount > 10;
        bool isLikelyTextureBound = profile.TextureSampleCount > 15 && profile.InstructionCount < profile.TextureSampleCount;

        if (isLikelyBandwidthBound)
        {
            EditorGUILayout.HelpBox($"Thắt cổ chai tiềm năng: Băng thông bộ nhớ (Memory Bandwidth). Shader này sử dụng nhiều bộ nhớ texture ({profile.TextureMemoryUsageMB:F2} MB) và lấy mẫu texture nhiều lần ({profile.TextureSampleCount} lần). Hãy xem xét việc giảm độ phân giải, sử dụng nén texture (ASTC/DXT), và đảm bảo mipmap được bật.", MessageType.Warning);
            hasTips = true;
        }
        else if (isLikelyALUBound)
        {
            EditorGUILayout.HelpBox($"Thắt cổ chai tiềm năng: Giới hạn tính toán (ALU-bound). Shader có nhiều lệnh phức tạp (điểm lệnh: {profile.InstructionCount}) nhưng ít lấy mẫu texture. Cố gắng đơn giản hóa các phép toán, thay thế các hàm đắt đỏ (pow, sin) bằng các phép tính xấp xỉ hoặc lookup table nếu có thể.", MessageType.Warning);
            hasTips = true;
        }
        else if (isLikelyTextureBound)
        {
            EditorGUILayout.HelpBox($"Thắt cổ chai tiềm năng: Giới hạn Texture (Texture-bound). Shader có rất nhiều lệnh lấy mẫu texture ({profile.TextureSampleCount} lần). Hãy xem xét việc gộp các texture lại (channel packing) để giảm số lần lấy mẫu trong một shader.", MessageType.Warning);
            hasTips = true;
        }

        if (profile.PassCount > 2)
        {
            EditorGUILayout.HelpBox($"Nhiều Pass ({profile.PassCount}): Nhiều pass làm tăng đáng kể số draw call. Hãy mở rộng mục shader để kiểm tra từng pass. Mỗi 'ForwardAdd' pass sẽ chạy cho mỗi pixel light.", MessageType.Info);
            hasTips = true;
        }
        if (profile.IsTransparent)
        {
            EditorGUILayout.HelpBox($"Trong suốt (Transparency): Shader trong suốt gây ra overdraw, rất tốn kém. Đảm bảo các bề mặt trong suốt có diện tích nhỏ nhất có thể và kiểm tra Render Queue.", MessageType.Info);
            hasTips = true;
        }
        if (profile.UniqueVariantCount > 10)
        {
            EditorGUILayout.HelpBox($"Nhiều Variant ({profile.UniqueVariantCount}): Nhiều variant làm tăng kích thước build và thời gian load. Hãy kiểm tra lại việc sử dụng `#pragma multi_compile` và chuyển sang `#pragma shader_feature` cho các tính năng có thể tắt.", MessageType.Info);
            hasTips = true;
        }

        if (!hasTips)
        {
            EditorGUILayout.HelpBox("Shader này có vẻ đã được tối ưu hợp lý. Hãy mở rộng để phân tích sâu hơn từng pass và variant.", MessageType.Info);
        }
    }

    private void DrawPassDetails(ShaderPassProfile pass)
    {
        EditorGUILayout.LabelField("Pass được chọn:", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Tên Pass", pass.PassName);
        EditorGUILayout.LabelField("Thẻ LightMode", pass.LightMode);
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Gợi ý:", EditorStyles.boldLabel);

        bool hasTips = false;
        if (pass.LightMode == "ForwardAdd")
        {
            EditorGUILayout.HelpBox("Đây là một additive forward pass. Nó chạy cho MỖI pixel light chiếu vào đối tượng, rất tốn kém. Cân nhắc dùng Deferred Shading nếu cần nhiều đèn.", MessageType.Warning);
            hasTips = true;
        }
        if (pass.LightMode == "ShadowCaster")
        {
            EditorGUILayout.HelpBox("Pass này render đối tượng vào shadow map. Vertex shader phức tạp ở đây sẽ làm chậm việc render bóng đổ. Hãy đơn giản hóa các phép biến đổi vertex cho pass này nếu có thể.", MessageType.Info);
            hasTips = true;
        }

        if (!hasTips)
        {
            EditorGUILayout.HelpBox("Phân tích mục đích của pass này. Các pass không cần thiết nên được loại bỏ bằng cách sử dụng shader keywords hoặc các chỉ thị tiền xử lý (#if).", MessageType.Info);
        }
    }

    private void DrawVariantDetails(MaterialVariantProfile variant)
    {
        EditorGUILayout.LabelField("Variant của Material được chọn:", EditorStyles.boldLabel);
        EditorGUILayout.SelectableLabel($"Keywords: {variant.VariantKeywordsKey}", EditorStyles.wordWrappedLabel, GUILayout.Height(30));
        EditorGUILayout.LabelField("Số đối tượng sử dụng", variant.Components.Count.ToString());
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Gợi ý:", EditorStyles.boldLabel);

        if (string.IsNullOrEmpty(variant.VariantKeywordsKey) || variant.VariantKeywordsKey == "[default]")
        {
            EditorGUILayout.HelpBox("Đây là variant mặc định không có keyword nào được bật.", MessageType.Info);
            return;
        }

        if (variant.VariantKeywordsKey.Contains("MULTI_COMPILE", StringComparison.OrdinalIgnoreCase))
        {
            EditorGUILayout.HelpBox("Variant này có thể được tạo từ `#pragma multi_compile`. Chúng luôn được đưa vào bản build. Đối với các tính năng tùy chọn, hãy đổi sang `#pragma shader_feature` để trình biên dịch có thể loại bỏ chúng khi không dùng đến.", MessageType.Warning);
        }
        else
        {
            EditorGUILayout.HelpBox("Variant này được điều khiển bởi `#pragma shader_feature`. Đây là một thực hành tốt vì nó sẽ bị loại bỏ khỏi các bản build nếu không có material nào sử dụng.", MessageType.Info);
        }
    }

    #endregion

    #region Core Logic: Analysis & Scoring

    private void StartCaptureCoroutine()
    {
        StopCaptureCoroutine();
        _captureCoroutine = EditorCoroutineUtility.StartCoroutine(CaptureAndAnalyzeSceneCoroutine(), this);
    }

    private void StopCaptureCoroutine()
    {
        if (_captureCoroutine != null)
        {
            EditorCoroutineUtility.StopCoroutine(_captureCoroutine);
            _captureCoroutine = null;
        }
        EditorUtility.ClearProgressBar();
    }

    private IEnumerator CaptureAndAnalyzeSceneCoroutine()
    {
        _isCapturing = true;
        Repaint();

        var shaderToProfileMap = new Dictionary<Shader, ShaderProfile>();
        _componentToProfileMap.Clear();

        var allRenderers = FindObjectsOfType<Renderer>();
        var allCanvasRenderers = FindObjectsOfType<CanvasRenderer>();
        int totalComponents = allRenderers.Length + allCanvasRenderers.Length;
        int processedCount = 0;

        try
        {
            for (int i = 0; i < allRenderers.Length; i++)
            {
                var renderer = allRenderers[i];
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                {
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material != null && material.shader != null)
                        {
                            ProcessComponentMaterial(material, renderer, shaderToProfileMap);
                        }
                    }
                }
                if (++processedCount % ANALYSIS_BATCH_SIZE == 0)
                {
                    EditorUtility.DisplayProgressBar("Analyzing Scene...", $"Processing Component {processedCount}/{totalComponents}", (float)processedCount / totalComponents);
                    yield return null;
                }
            }

            for (int i = 0; i < allCanvasRenderers.Length; i++)
            {
                var canvasRenderer = allCanvasRenderers[i];
                if (canvasRenderer != null && canvasRenderer.gameObject.activeInHierarchy)
                {
                    var material = canvasRenderer.GetMaterial();
                    if (material != null && material.shader != null)
                    {
                        ProcessComponentMaterial(material, canvasRenderer, shaderToProfileMap);
                    }
                }
                if (++processedCount % ANALYSIS_BATCH_SIZE == 0)
                {
                    EditorUtility.DisplayProgressBar("Analyzing Scene...", $"Processing Component {processedCount}/{totalComponents}", (float)processedCount / totalComponents);
                    yield return null;
                }
            }

            _shaderProfiles = shaderToProfileMap.Values.ToList();

            for (int i = 0; i < _shaderProfiles.Count; i++)
            {
                var profile = _shaderProfiles[i];
                EditorUtility.DisplayProgressBar("Calculating Metrics...", $"Analyzing Shader {i + 1}/{_shaderProfiles.Count}", (float)(i + 1) / _shaderProfiles.Count);
                CalculateShaderMetrics(profile);
                profile.FinalizeComponentCounts();
                yield return null;
            }

            if (_shaderProfiles.Any())
            {
                _minScore = _shaderProfiles.Min(p => p.ComplexityScore);
                _maxScore = _shaderProfiles.Max(p => p.ComplexityScore);
            }

            SortAndReloadTreeView();
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            _isCapturing = false;
            _captureCoroutine = null;
            Repaint();
        }
    }

    private void ProcessComponentMaterial(Material material, Component sourceComponent, Dictionary<Shader, ShaderProfile> shaderProfileMap)
    {
        Shader shader = material.shader;
        if (shader.name.StartsWith("Hidden/")) return;

        if (!shaderProfileMap.TryGetValue(shader, out var profile))
        {
            profile = new ShaderProfile(shader);
            shaderProfileMap[shader] = profile;
        }

        _componentToProfileMap[sourceComponent] = profile;

        string keywordsKey = string.Join(" ", material.shaderKeywords.OrderBy(k => k));
        if (string.IsNullOrWhiteSpace(keywordsKey)) keywordsKey = "[default]";

        if (!profile.MaterialVariants.TryGetValue(keywordsKey, out var variantProfile))
        {
            variantProfile = new MaterialVariantProfile(keywordsKey, material);
            profile.MaterialVariants[keywordsKey] = variantProfile;
        }
        if (!variantProfile.Components.Contains(sourceComponent))
        {
            variantProfile.Components.Add(sourceComponent);
        }
    }

    private void CalculateShaderMetrics(ShaderProfile profile)
    {
        AnalyzeShaderPasses(profile);
        profile.TextureMemoryUsageMB = CalculateTextureMemory(profile);

        string fullShaderCode = GetFullShaderSource(profile.Shader);

        if (!string.IsNullOrEmpty(fullShaderCode))
        {
            string lowerCaseCode = fullShaderCode.ToLowerInvariant();
            profile.InstructionCount = EstimateComplexInstructionCost(lowerCaseCode);
            profile.TextureSampleCount = EstimateTextureSampleCount(lowerCaseCode);
        }
        else
        {
            profile.InstructionCount = 0;
            profile.TextureSampleCount = 0;
        }

        profile.ComplexityScore = CalculateComplexityScore(profile);
    }

    private string GetFullShaderSource(Shader shader)
    {
        string path = AssetDatabase.GetAssetPath(shader);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return GetFullShaderSourceRecursive(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Could not fully analyze shader source for '{shader.name}' at {path}. Reason: {ex.Message}");
            return null;
        }
    }

    private string GetFullShaderSourceRecursive(string assetPath, HashSet<string> visitedPaths)
    {
        if (!visitedPaths.Add(assetPath))
        {
            return string.Empty;
        }

        string fileContent;
        try
        {
            fileContent = File.ReadAllText(assetPath);
        }
        catch (Exception)
        {
            return string.Empty;
        }

        var sourceBuilder = new StringBuilder(fileContent);
        string currentDirectory = Path.GetDirectoryName(assetPath);

        // Find #include "..." directives
        var includeRegex = new Regex(@"^\s*#include\s+""([^""]+)""", RegexOptions.Multiline);
        foreach (Match match in includeRegex.Matches(fileContent))
        {
            string dependencyRelativePath = match.Groups[1].Value;
            string dependencyFullPath = Path.GetFullPath(Path.Combine(currentDirectory, dependencyRelativePath));
            string dependencyAssetPath = "Assets" + dependencyFullPath.Substring(Application.dataPath.Length);

            if (File.Exists(dependencyAssetPath))
            {
                sourceBuilder.Append(GetFullShaderSourceRecursive(dependencyAssetPath, visitedPaths));
            }
        }

        // Find dependencies in shadergraph/subgraph files
        string extension = Path.GetExtension(assetPath).ToLowerInvariant();
        if (extension == ".shadergraph" || extension == ".subgraph")
        {
            var pathRegex = new Regex(@"\""path\"":\s*\""([^\""]+)\""");
            foreach (Match match in pathRegex.Matches(fileContent))
            {
                string foundPath = match.Groups[1].Value;
                if (foundPath.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase) ||
                    foundPath.EndsWith(".subgraph", StringComparison.OrdinalIgnoreCase))
                {
                    string dependencyAssetPath = foundPath.StartsWith("Assets/") ? foundPath : Path.Combine(currentDirectory, foundPath);
                    if (File.Exists(dependencyAssetPath))
                    {
                        sourceBuilder.Append(GetFullShaderSourceRecursive(dependencyAssetPath, visitedPaths));
                    }
                }
            }
        }

        return sourceBuilder.ToString();
    }

    private void AnalyzeShaderPasses(ShaderProfile profile)
    {
        profile.Passes.Clear();
        if (_getShaderPassNameMethod == null || _getShaderActiveSubshaderIndexMethod == null) return;
        int activeSubShaderIndex = (int)_getShaderActiveSubshaderIndexMethod.Invoke(null, new object[] { profile.Shader });

        for (int i = 0; i < profile.Shader.passCount; i++)
        {
            string passName = (string)_getShaderPassNameMethod.Invoke(null, new object[] { profile.Shader, activeSubShaderIndex, i });

            string lightMode = "N/A";
            if (_getPassLightModeMethod != null)
            {
                lightMode = (string)_getPassLightModeMethod.Invoke(null, new object[] { profile.Shader, i });
            }

            profile.Passes.Add(new ShaderPassProfile(i, passName, lightMode));
        }
    }

    private float CalculateTextureMemory(ShaderProfile profile)
    {
        var uniqueTextures = new HashSet<Texture>();
        foreach (var variant in profile.MaterialVariants.Values)
        {
            int propertyCount = ShaderUtil.GetPropertyCount(variant.Material.shader);
            for (int i = 0; i < propertyCount; i++)
            {
                if (ShaderUtil.GetPropertyType(variant.Material.shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    string propertyName = ShaderUtil.GetPropertyName(variant.Material.shader, i);
                    Texture tex = variant.Material.GetTexture(propertyName);
                    if (tex != null)
                    {
                        uniqueTextures.Add(tex);
                    }
                }
            }
        }
        return uniqueTextures.Sum(t => (float)UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t)) / (1024f * 1024f);
    }

    private float CalculateComplexityScore(ShaderProfile profile)
    {
        float score = 0;
        score += profile.PassCount * _settings.PassCostWeight;
        score += profile.UniqueVariantCount * _settings.VariantCountWeight;
        score += profile.TextureMemoryUsageMB * _settings.TextureMemoryWeight;
        score += profile.InstructionCount * _settings.InstructionCountWeight;
        if (profile.IsTransparent)
        {
            score += _settings.TransparencyWeight;
        }
        return score;
    }

    private int EstimateComplexInstructionCost(string shaderCode)
    {
        float totalCost = 0;
        foreach (var instructionPair in COMPLEX_INSTRUCTION_COSTS)
        {
            int occurrences = Regex.Matches(shaderCode, @"\b" + instructionPair.Key + @"\b").Count;
            totalCost += occurrences * instructionPair.Value;
        }
        return Mathf.RoundToInt(totalCost);
    }

    private int EstimateTextureSampleCount(string shaderCode)
    {
        int count = 0;
        foreach (var sampler in TEXTURE_SAMPLER_COSTS.Keys)
        {
            count += Regex.Matches(shaderCode, @"\b" + sampler + @"\b").Count;
        }
        return count;
    }

    #endregion

    #region Scene Drawing: Heatmap

    private void OnSceneGUI(SceneView sceneView)
    {
        if (!_isHeatmapVisible || !_shaderProfiles.Any() || _componentToProfileMap.Count == 0) return;

        Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
        Plane[] frustumPlanes = GeometryUtility.CalculateFrustumPlanes(sceneView.camera);

        foreach (var pair in _componentToProfileMap)
        {
            Component component = pair.Key;
            if (component == null || !component.gameObject.activeInHierarchy) continue;

            Color heatColor = GetColorForScore(pair.Value.ComplexityScore);

            switch (component)
            {
                case Renderer renderer:
                    DrawHeatmapFor3DObject(renderer, frustumPlanes, heatColor);
                    break;
                case CanvasRenderer canvasRenderer:
                    DrawHeatmapForUIObject(canvasRenderer, heatColor);
                    break;
            }
        }
    }

    private void DrawHeatmapFor3DObject(Renderer renderer, Plane[] frustumPlanes, Color heatColor)
    {
        Bounds bounds = renderer.bounds;
        if (!GeometryUtility.TestPlanesAABB(frustumPlanes, bounds)) return;

        Color faceColor = heatColor;
        faceColor.a = 0.2f;

        Color outlineColor = heatColor;
        outlineColor.a = 0.9f;

        DrawSolidBox(bounds, faceColor, outlineColor);
    }

    private void DrawHeatmapForUIObject(CanvasRenderer canvasRenderer, Color heatColor)
    {
        if (canvasRenderer.GetAlpha() < 0.001f) return;

        RectTransform rectTransform = canvasRenderer.GetComponent<RectTransform>();
        Vector3[] corners = new Vector3[4];
        rectTransform.GetWorldCorners(corners);

        Color faceColor = heatColor;
        faceColor.a = 0.3f;
        Color outlineColor = heatColor;
        outlineColor.a = 0.9f;

        Handles.DrawSolidRectangleWithOutline(corners, faceColor, outlineColor);
    }

    private void DrawSolidBox(Bounds bounds, Color faceColor, Color outlineColor)
    {
        Vector3 center = bounds.center;
        Vector3 halfSize = bounds.extents;

        Vector3[] points = {
            center + new Vector3(-halfSize.x, -halfSize.y, -halfSize.z),
            center + new Vector3( halfSize.x, -halfSize.y, -halfSize.z),
            center + new Vector3( halfSize.x,  halfSize.y, -halfSize.z),
            center + new Vector3(-halfSize.x,  halfSize.y, -halfSize.z),
            center + new Vector3(-halfSize.x, -halfSize.y,  halfSize.z),
            center + new Vector3( halfSize.x, -halfSize.y,  halfSize.z),
            center + new Vector3( halfSize.x,  halfSize.y,  halfSize.z),
            center + new Vector3(-halfSize.x,  halfSize.y,  halfSize.z)
        };

        Handles.color = outlineColor;
        Handles.DrawWireCube(center, bounds.size);

        Handles.color = faceColor;
        Handles.DrawSolidRectangleWithOutline(new[] { points[0], points[1], points[2], points[3] }, faceColor, Color.clear);
        Handles.DrawSolidRectangleWithOutline(new[] { points[4], points[5], points[6], points[7] }, faceColor, Color.clear);
        Handles.DrawSolidRectangleWithOutline(new[] { points[0], points[4], points[7], points[3] }, faceColor, Color.clear);
        Handles.DrawSolidRectangleWithOutline(new[] { points[1], points[5], points[6], points[2] }, faceColor, Color.clear);
        Handles.DrawSolidRectangleWithOutline(new[] { points[3], points[2], points[6], points[7] }, faceColor, Color.clear);
        Handles.DrawSolidRectangleWithOutline(new[] { points[0], points[1], points[5], points[4] }, faceColor, Color.clear);
    }

    private Color GetColorForScore(float score)
    {
        if (_settings.UseGradient)
        {
            if (_maxScore <= _minScore) return _settings.GradientStartColor;
            float normalizedScore = Mathf.InverseLerp(_minScore, _maxScore, score);
            return Color.Lerp(_settings.GradientStartColor, _settings.GradientEndColor, normalizedScore);
        }

        if (score < _settings.MediumComplexityThreshold) return _settings.LowComplexityColor;
        if (score < _settings.HighComplexityThreshold) return _settings.MediumComplexityColor;
        return _settings.HighComplexityColor;
    }

    #endregion

    #region Data Export & Sorting

    private void ExportToCsv()
    {
        if (!_shaderProfiles.Any())
        {
            EditorUtility.DisplayDialog("Export Error", "No analysis data to export. Please run 'Capture Scene' first.", "OK");
            return;
        }

        string path = EditorUtility.SaveFilePanel("Save Shader Report", "", $"ShaderReport_{DateTime.Now:yyyyMMdd_HHmm}.csv", "csv");
        if (string.IsNullOrEmpty(path)) return;

        var sb = new StringBuilder();
        sb.AppendLine("Shader Name,Complexity Score,Passes,Variants,Instructions Cost,Texture Samples,Texture Memory (MB),Total GameObjects,Is Transparent");

        foreach (var profile in _treeView.GetSortedData())
        {
            sb.AppendFormat("\"{0}\",{1:F0},{2},{3},{4},{5},{6:F2},{7},{8}\n",
                profile.Shader.name.Replace("\"", "\"\""), profile.ComplexityScore, profile.PassCount,
                profile.UniqueVariantCount, profile.InstructionCount, profile.TextureSampleCount, profile.TextureMemoryUsageMB,
                profile.TotalGameObjectCount, profile.IsTransparent);
        }

        File.WriteAllText(path, sb.ToString());
        EditorUtility.DisplayDialog("Export Successful", $"Report saved to:\n{path}", "OK");
    }

    private void SortTreeView(MultiColumnHeader multiColumnHeader)
    {
        SortAndReloadTreeView();
    }

    private void SortAndReloadTreeView()
    {
        var sortedColumns = _treeView.multiColumnHeader.state.sortedColumns;
        if (sortedColumns.Length == 0)
        {
            _treeView.SetAnalysisResults(_shaderProfiles);
            _treeView.Reload();
            return;
        }

        int sortedColumnIndex = sortedColumns[0];
        bool isAscending = _treeView.multiColumnHeader.IsSortedAscending(sortedColumnIndex);
        var column = (ShaderProfilerTreeView.Columns)sortedColumnIndex;

        IOrderedEnumerable<ShaderProfile> orderedData = column switch
        {
            ShaderProfilerTreeView.Columns.ShaderName => _shaderProfiles.OrderBy(p => p.Shader.name, isAscending),
            ShaderProfilerTreeView.Columns.Score => _shaderProfiles.OrderBy(p => p.ComplexityScore, isAscending),
            ShaderProfilerTreeView.Columns.Passes => _shaderProfiles.OrderBy(p => p.PassCount, isAscending),
            ShaderProfilerTreeView.Columns.Variants => _shaderProfiles.OrderBy(p => p.UniqueVariantCount, isAscending),
            ShaderProfilerTreeView.Columns.Instructions => _shaderProfiles.OrderBy(p => p.InstructionCount, isAscending),
            ShaderProfilerTreeView.Columns.Memory => _shaderProfiles.OrderBy(p => p.TextureMemoryUsageMB, isAscending),
            ShaderProfilerTreeView.Columns.Objects => _shaderProfiles.OrderBy(p => p.TotalGameObjectCount, isAscending),
            _ => _shaderProfiles.OrderByDescending(p => p.ComplexityScore),
        };

        _treeView.SetAnalysisResults(orderedData.ToList());
        _treeView.Reload();
    }

    private void OnTreeViewSelectionChanged(IList<int> selectedIds)
    {
        _selectedItem = selectedIds.Count > 0 ? _treeView.PublicFindRows(selectedIds).FirstOrDefault() : null;
        Repaint();

        if (_selectedItem is not ProfilerTreeViewItem profilerItem) return;

        UnityEngine.Object objectToPing = profilerItem.ItemType switch
        {
            ProfilerTreeViewItem.Type.Component => profilerItem.Component.gameObject,
            ProfilerTreeViewItem.Type.Variant => profilerItem.VariantProfile.Material,
            ProfilerTreeViewItem.Type.Shader => profilerItem.ShaderProfile.Shader,
            _ => null
        };

        if (objectToPing != null)
        {
            EditorGUIUtility.PingObject(objectToPing);
        }
    }

    #endregion

    #region Settings & Reflection

    private void ApplyProfileSettings(ProfilerSettings.TargetProfileType profile)
    {
        _settings.CurrentProfile = profile;
        if (profile == ProfilerSettings.TargetProfileType.Custom) return;

        switch (profile)
        {
            case ProfilerSettings.TargetProfileType.LowEndMobile:
                _settings.PassCostWeight = 60f;
                _settings.VariantCountWeight = 30f;
                _settings.TextureMemoryWeight = 4f;
                _settings.InstructionCountWeight = 1.5f;
                _settings.TransparencyWeight = 100f;
                break;
            case ProfilerSettings.TargetProfileType.HighEndPC:
                _settings.PassCostWeight = 25f;
                _settings.VariantCountWeight = 40f;
                _settings.TextureMemoryWeight = 1.5f;
                _settings.InstructionCountWeight = 0.8f;
                _settings.TransparencyWeight = 50f;
                break;
            case ProfilerSettings.TargetProfileType.Default:
                _settings.PassCostWeight = 30f;
                _settings.VariantCountWeight = 40f;
                _settings.TextureMemoryWeight = 2f;
                _settings.InstructionCountWeight = 1f;
                _settings.TransparencyWeight = 75f;
                break;
        }
        Repaint();
    }

    private void SaveSettings()
    {
        string json = EditorJsonUtility.ToJson(_settings);
        EditorPrefs.SetString(SETTINGS_PREF_KEY, json);
    }

    private void LoadSettings()
    {
        _settings = new ProfilerSettings();
        if (EditorPrefs.HasKey(SETTINGS_PREF_KEY))
        {
            string json = EditorPrefs.GetString(SETTINGS_PREF_KEY);
            EditorJsonUtility.FromJsonOverwrite(json, _settings);
        }
    }

    private static void InitializeShaderUtilReflection()
    {
        if (_getShaderPassNameMethod != null) return;

        Type shaderUtilType = typeof(ShaderUtil);

        _getShaderPassNameMethod = shaderUtilType.GetMethod(
            "GetShaderPassName",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            null,
            new Type[] { typeof(Shader), typeof(int), typeof(int) },
            null
        );

        _getPassLightModeMethod = shaderUtilType.GetMethod(
            "GetPassLightMode",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
        );

        _getShaderActiveSubshaderIndexMethod = shaderUtilType.GetMethod(
            "GetShaderActiveSubshaderIndex",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
        );

        if (_getShaderPassNameMethod == null || _getShaderActiveSubshaderIndexMethod == null)
        {
            Debug.LogError("ProfessionalShaderProfiler: Could not find critical internal ShaderUtil methods (GetShaderPassName/GetShaderActiveSubshaderIndex) via reflection. Pass name analysis will be disabled. This may be due to a Unity version update.");
        }

        if (_getPassLightModeMethod == null)
        {
            Debug.LogWarning("ProfessionalShaderProfiler: 'GetPassLightMode' not found. This is expected on newer Unity versions. LightMode tag will be unavailable.");
        }
    }
    #endregion
}

public static class EnumerableExtensions
{
    public static IOrderedEnumerable<TSource> OrderBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, bool ascending)
    {
        return ascending ? source.OrderBy(keySelector) : source.OrderByDescending(keySelector);
    }
}

#region TreeView Implementation

public class ShaderProfilerTreeView : TreeView
{
    private List<ShaderProfile> _data;
    private readonly Func<float, Color> _colorProvider;

    public Action<IList<int>> OnSelectionChanged;
    public enum Columns { ShaderName, Score, Passes, Variants, Instructions, Memory, Objects }

    public ShaderProfilerTreeView(TreeViewState state, MultiColumnHeader multiColumnHeader, List<ShaderProfile> data, Func<float, Color> colorProvider) : base(state, multiColumnHeader)
    {
        _data = data;
        _colorProvider = colorProvider;
        showAlternatingRowBackgrounds = true;
        Reload();
    }

    public static MultiColumnHeaderState CreateDefaultMultiColumnHeaderState()
    {
        var columns = new MultiColumnHeaderState.Column[]
        {
            new() { headerContent = new GUIContent("Shader / Pass / Material / Object", "Hierarchy of shaders, passes, materials, and game objects."), width = 400, minWidth = 250, autoResize = true, canSort = true },
            new() { headerContent = new GUIContent("Score", "Overall complexity score."), width = 80, minWidth = 60, autoResize = false, canSort = true, sortedAscending = false },
            new() { headerContent = new GUIContent("Passes", "Number of passes in the shader."), width = 50, minWidth = 50, autoResize = false, canSort = true },
            new() { headerContent = new GUIContent("Variants", "Number of unique material variants."), width = 60, minWidth = 60, autoResize = false, canSort = true },
            new() { headerContent = new GUIContent("Instr. Cost", "Estimated weighted cost of complex instructions."), width = 80, minWidth = 70, autoResize = false, canSort = true },
            new() { headerContent = new GUIContent("Mem (MB)", "Estimated total texture memory used."), width = 80, minWidth = 70, autoResize = false, canSort = true },
            new() { headerContent = new GUIContent("Objects", "Number of objects using this item."), width = 60, minWidth = 60, autoResize = false, canSort = true },
        };
        return new MultiColumnHeaderState(columns) { sortedColumns = new int[] { (int)Columns.Score } };
    }

    public void SetAnalysisResults(List<ShaderProfile> data) => _data = data;
    public IEnumerable<ShaderProfile> GetSortedData() => _data;
    public IList<TreeViewItem> PublicFindRows(IList<int> itemIDs) => FindRows(itemIDs);

    protected override TreeViewItem BuildRoot()
    {
        var root = new TreeViewItem { id = 0, depth = -1, displayName = "Root" };
        if (_data == null || !_data.Any())
        {
            root.AddChild(new TreeViewItem { id = 1, displayName = "No data. Please run 'Capture Scene'." });
            return root;
        }

        int currentId = 0;
        foreach (var shaderProfile in _data)
        {
            var shaderItem = new ProfilerTreeViewItem(++currentId, 0, shaderProfile);
            root.AddChild(shaderItem);

            var passesHeader = new ProfilerTreeViewItem(++currentId, 1, shaderProfile, specialNodeType: ProfilerTreeViewItem.SpecialNodeType.PassesHeader);
            shaderItem.AddChild(passesHeader);
            foreach (var passProfile in shaderProfile.Passes)
            {
                var passItem = new ProfilerTreeViewItem(++currentId, 2, shaderProfile, passProfile);
                passesHeader.AddChild(passItem);
            }

            var variantsHeader = new ProfilerTreeViewItem(++currentId, 1, shaderProfile, specialNodeType: ProfilerTreeViewItem.SpecialNodeType.VariantsHeader);
            shaderItem.AddChild(variantsHeader);
            foreach (var variant in shaderProfile.MaterialVariants.Values)
            {
                var variantItem = new ProfilerTreeViewItem(++currentId, 2, shaderProfile, variantProfile: variant);
                variantsHeader.AddChild(variantItem);

                foreach (var component in variant.Components)
                {
                    var goItem = new ProfilerTreeViewItem(++currentId, 3, shaderProfile, variantProfile: variant, component: component);
                    variantItem.AddChild(goItem);
                }
            }
        }
        SetupDepthsFromParentsAndChildren(root);
        return root;
    }

    protected override void RowGUI(RowGUIArgs args)
    {
        if (args.item is not ProfilerTreeViewItem item)
        {
            base.RowGUI(args);
            return;
        }

        for (int i = 0; i < args.GetNumVisibleColumns(); ++i)
        {
            Rect cellRect = args.GetCellRect(i);
            var column = (Columns)args.GetColumn(i);

            if (column == Columns.ShaderName)
            {
                if (item.ItemType == ProfilerTreeViewItem.Type.Shader)
                {
                    Rect colorRect = new Rect(cellRect.x + GetContentIndent(item) - 16, cellRect.y, 4, cellRect.height);
                    EditorGUI.DrawRect(colorRect, _colorProvider(item.ShaderProfile.ComplexityScore));
                }
                base.RowGUI(args);
            }
            else
            {
                CenterRect(ref cellRect);
                DrawCell(cellRect, item, column);
            }
        }
    }

    private void DrawCell(Rect rect, ProfilerTreeViewItem item, Columns column)
    {
        string label = column switch
        {
            Columns.Score when item.ItemType == ProfilerTreeViewItem.Type.Shader => item.ShaderProfile.ComplexityScore.ToString("F0"),
            Columns.Passes when item.ItemType == ProfilerTreeViewItem.Type.Shader => item.ShaderProfile.PassCount.ToString(),
            Columns.Variants when item.ItemType == ProfilerTreeViewItem.Type.Shader => item.ShaderProfile.UniqueVariantCount.ToString(),
            Columns.Instructions when item.ItemType == ProfilerTreeViewItem.Type.Shader => item.ShaderProfile.InstructionCount.ToString(),
            Columns.Memory when item.ItemType == ProfilerTreeViewItem.Type.Shader => item.ShaderProfile.TextureMemoryUsageMB.ToString("F2"),
            Columns.Objects when item.ItemType == ProfilerTreeViewItem.Type.Shader => item.ShaderProfile.TotalGameObjectCount.ToString(),
            Columns.Objects when item.ItemType == ProfilerTreeViewItem.Type.Variant => item.VariantProfile.Components.Count.ToString(),
            Columns.Objects when item.ItemType == ProfilerTreeViewItem.Type.Component => "1",
            _ => ""
        };
        EditorGUI.LabelField(rect, label);
    }

    private void CenterRect(ref Rect rect)
    {
        rect.x += 4;
        rect.width -= 4;
    }

    protected override void SelectionChanged(IList<int> selectedIds)
    {
        base.SelectionChanged(selectedIds);
        OnSelectionChanged?.Invoke(selectedIds);
    }
}

public class ProfilerTreeViewItem : TreeViewItem
{
    public enum Type { Shader, Pass, Variant, Component, SpecialNode }
    public enum SpecialNodeType { None, PassesHeader, VariantsHeader }

    public Type ItemType { get; }
    public SpecialNodeType NodeType { get; }
    public ShaderProfile ShaderProfile { get; }
    public ShaderPassProfile PassProfile { get; }
    public MaterialVariantProfile VariantProfile { get; }
    public Component Component { get; }

    public ProfilerTreeViewItem(int id, int depth, ShaderProfile shader, ShaderPassProfile pass = null, MaterialVariantProfile variantProfile = null, Component component = null, SpecialNodeType specialNodeType = SpecialNodeType.None) : base(id, depth)
    {
        ShaderProfile = shader;
        PassProfile = pass;
        VariantProfile = variantProfile;
        Component = component;
        NodeType = specialNodeType;

        if (specialNodeType != SpecialNodeType.None) ItemType = Type.SpecialNode;
        else if (component != null) ItemType = Type.Component;
        else if (variantProfile != null) ItemType = Type.Variant;
        else if (pass != null) ItemType = Type.Pass;
        else ItemType = Type.Shader;

        displayName = GetDisplayNameForType();
        icon = GetIconForType();
    }

    private string GetDisplayNameForType() => ItemType switch
    {
        Type.Component => Component.gameObject.name,
        Type.Variant => string.IsNullOrEmpty(VariantProfile.VariantKeywordsKey) ? "[Default Variant]" : VariantProfile.VariantKeywordsKey,
        Type.Pass => $"{PassProfile.PassName} (LightMode: {PassProfile.LightMode})",
        Type.Shader => ShaderProfile.Shader.name,
        Type.SpecialNode => NodeType switch
        {
            SpecialNodeType.PassesHeader => "Passes",
            SpecialNodeType.VariantsHeader => "Material Variants",
            _ => "Header"
        },
        _ => "Unknown Item"
    };

    private Texture2D GetIconForType()
    {
        string iconName = ItemType switch
        {
            Type.Component => Component is CanvasRenderer ? "d_Canvas Icon" : "d_PrefabVariant Icon",
            Type.Variant => "d_Material Icon",
            Type.Pass => "d_ForwardPass",
            Type.Shader => "d_Shader Icon",
            Type.SpecialNode => "d_Folder Icon",
            _ => "d_console.erroricon.sml"
        };
        return EditorGUIUtility.IconContent(iconName).image as Texture2D;
    }
}

#endregion

#endif