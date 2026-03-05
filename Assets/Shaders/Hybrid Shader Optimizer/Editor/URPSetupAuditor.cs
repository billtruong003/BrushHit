// ============================================================================
// URP Setup Auditor — Editor Tool
// Tech Art Department | v1.1 | March 2026
// 
// Tự động kiểm tra URP settings, Lightmap, Bake Map readiness, Quality Tiers
// và đưa ra recommendations dựa trên target platform.
//
// Usage: Window > Tech Art > URP Setup Auditor
// ============================================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace TechArt.Tools
{
    public class URPSetupAuditor : EditorWindow
    {
        // ── Enums ──
        private enum TargetPlatform { PC, Quest3, Mobile }
        private enum Severity { Pass, Info, Warning, Error }

        // ── Data ──
        private struct AuditResult
        {
            public Severity severity;
            public string category;
            public string message;
            public string recommendation;
            public string currentValue;
            public string recommendedValue;
        }

        // ── State ──
        private TargetPlatform _targetPlatform = TargetPlatform.Quest3;
        private List<AuditResult> _results = new List<AuditResult>();
        private Vector2 _scrollPos;
        private bool _hasRun = false;
        private int _passCount, _infoCount, _warnCount, _errorCount;

        // Foldouts
        private bool _foldURPAsset = true;
        private bool _foldShadows = true;
        private bool _foldPostFX = true;
        private bool _foldRenderer = true;
        private bool _foldLightmap = true;
        private bool _foldBakeReady = true;
        private bool _foldQuality = true;
        private bool _foldScene = true;
        private bool _foldShaders = true;

        // Styles (lazy init)
        private GUIStyle _headerStyle;
        private GUIStyle _passStyle;
        private GUIStyle _infoStyle;
        private GUIStyle _warnStyle;
        private GUIStyle _errorStyle;
        private GUIStyle _boxStyle;
        private GUIStyle _boldLabel;
        private GUIStyle _miniLabel;
        private bool _stylesInitialized;

        // ── Menu ──
        [MenuItem("Window/Tech Art/URP Setup Auditor")]
        public static void ShowWindow()
        {
            var wnd = GetWindow<URPSetupAuditor>("URP Auditor");
            wnd.minSize = new Vector2(520, 400);
        }

        // ── Styles ──
        private void InitStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                margin = new RectOffset(4, 4, 8, 4)
            };

            _passStyle = new GUIStyle(EditorStyles.helpBox);
            _infoStyle = new GUIStyle(EditorStyles.helpBox);
            _warnStyle = new GUIStyle(EditorStyles.helpBox);
            _errorStyle = new GUIStyle(EditorStyles.helpBox);

            _boxStyle = new GUIStyle("box")
            {
                padding = new RectOffset(8, 8, 6, 6),
                margin = new RectOffset(4, 4, 2, 2)
            };

            _boldLabel = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold };
            _miniLabel = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };

            _stylesInitialized = true;
        }

        // ══════════════════════════════════════════════
        // GUI
        // ══════════════════════════════════════════════
        private void OnGUI()
        {
            InitStyles();

            // Title
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("URP Setup Auditor", _headerStyle);
            EditorGUILayout.LabelField("Kiểm tra URP settings theo recommendations cho target platform", _miniLabel);
            EditorGUILayout.Space(4);

            // Platform selector
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Target Platform:", GUILayout.Width(110));
            _targetPlatform = (TargetPlatform)EditorGUILayout.EnumPopup(_targetPlatform);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            // Run button
            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = new Color(0.2f, 0.7f, 0.4f);
            if (GUILayout.Button("▶  Run Full Audit", GUILayout.Height(32)))
            {
                RunAudit();
            }
            GUI.backgroundColor = Color.white;

            if (_hasRun && GUILayout.Button("📋 Copy Report", GUILayout.Height(32), GUILayout.Width(120)))
            {
                CopyReportToClipboard();
            }
            EditorGUILayout.EndHorizontal();

            // Summary bar
            if (_hasRun)
            {
                EditorGUILayout.Space(4);
                DrawSummaryBar();
                EditorGUILayout.Space(4);
            }

            // Results
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            if (_hasRun)
            {
                DrawCategoryFoldout("URP Asset Settings", "URPAsset", ref _foldURPAsset);
                DrawCategoryFoldout("Shadows", "Shadows", ref _foldShadows);
                DrawCategoryFoldout("Post-Processing", "PostFX", ref _foldPostFX);
                DrawCategoryFoldout("Renderer Features", "Renderer", ref _foldRenderer);
                DrawCategoryFoldout("Lightmap Settings", "Lightmap", ref _foldLightmap);
                DrawCategoryFoldout("Bake Map Readiness (Low Poly)", "BakeReady", ref _foldBakeReady);
                DrawCategoryFoldout("Quality Tiers", "Quality", ref _foldQuality);
                DrawCategoryFoldout("Scene Objects", "Scene", ref _foldScene);
                DrawCategoryFoldout("Shaders & Variants", "Shaders", ref _foldShaders);
            }
            else
            {
                EditorGUILayout.Space(20);
                EditorGUILayout.LabelField("Chọn target platform rồi nhấn Run Full Audit.", EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSummaryBar()
        {
            EditorGUILayout.BeginHorizontal(_boxStyle);

            string platformLabel = _targetPlatform switch
            {
                TargetPlatform.PC => "🖥 PC",
                TargetPlatform.Quest3 => "🥽 Quest 3",
                TargetPlatform.Mobile => "📱 Mobile",
                _ => ""
            };

            EditorGUILayout.LabelField($"{platformLabel}  |", _boldLabel, GUILayout.Width(100));

            GUI.color = new Color(0.3f, 0.85f, 0.3f);
            EditorGUILayout.LabelField($"✅ {_passCount} Pass", GUILayout.Width(70));
            GUI.color = new Color(0.5f, 0.8f, 1f);
            EditorGUILayout.LabelField($"ℹ {_infoCount} Info", GUILayout.Width(60));
            GUI.color = new Color(1f, 0.85f, 0.2f);
            EditorGUILayout.LabelField($"⚠ {_warnCount} Warn", GUILayout.Width(70));
            GUI.color = new Color(1f, 0.35f, 0.3f);
            EditorGUILayout.LabelField($"❌ {_errorCount} Error", GUILayout.Width(70));
            GUI.color = Color.white;

            int total = _results.Count;
            float score = total > 0 ? (float)_passCount / total * 100f : 0;
            EditorGUILayout.LabelField($"Score: {score:F0}%", _boldLabel);

            EditorGUILayout.EndHorizontal();
        }

        private void DrawCategoryFoldout(string title, string category, ref bool foldout)
        {
            var items = _results.Where(r => r.category == category).ToList();
            if (items.Count == 0) return;

            int errs = items.Count(r => r.severity == Severity.Error);
            int warns = items.Count(r => r.severity == Severity.Warning);
            string badge = "";
            if (errs > 0) badge = $"  ❌{errs}";
            if (warns > 0) badge += $"  ⚠{warns}";
            if (errs == 0 && warns == 0) badge = "  ✅";

            foldout = EditorGUILayout.Foldout(foldout, $"{title}{badge}", true, EditorStyles.foldoutHeader);

            if (foldout)
            {
                EditorGUI.indentLevel++;
                foreach (var r in items)
                {
                    DrawResult(r);
                }
                EditorGUI.indentLevel--;
                EditorGUILayout.Space(4);
            }
        }

        private void DrawResult(AuditResult r)
        {
            Color bgColor = r.severity switch
            {
                Severity.Pass => new Color(0.18f, 0.32f, 0.18f, 0.3f),
                Severity.Info => new Color(0.18f, 0.25f, 0.35f, 0.3f),
                Severity.Warning => new Color(0.4f, 0.35f, 0.1f, 0.3f),
                Severity.Error => new Color(0.4f, 0.15f, 0.12f, 0.3f),
                _ => Color.clear
            };

            string icon = r.severity switch
            {
                Severity.Pass => "✅",
                Severity.Info => "ℹ️",
                Severity.Warning => "⚠️",
                Severity.Error => "❌",
                _ => ""
            };

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = bgColor + Color.white * 0.7f;

            EditorGUILayout.BeginVertical(_boxStyle);
            GUI.backgroundColor = prevBg;

            // Header line
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"{icon} {r.message}", _boldLabel);
            EditorGUILayout.EndHorizontal();

            // Values
            if (!string.IsNullOrEmpty(r.currentValue))
            {
                EditorGUILayout.LabelField($"  Current: {r.currentValue}  →  Recommended: {r.recommendedValue}", _miniLabel);
            }

            // Recommendation
            if (r.severity != Severity.Pass && !string.IsNullOrEmpty(r.recommendation))
            {
                EditorGUILayout.LabelField($"  💡 {r.recommendation}", _miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════════════════
        // AUDIT ENGINE
        // ══════════════════════════════════════════════
        private void RunAudit()
        {
            _results.Clear();
            _hasRun = true;

            // Get current URP asset
            var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset == null)
            {
                _results.Add(new AuditResult
                {
                    severity = Severity.Error,
                    category = "URPAsset",
                    message = "Không tìm thấy URP Asset!",
                    recommendation = "Vào Graphics Settings và assign một UniversalRenderPipelineAsset."
                });
                UpdateCounts();
                return;
            }

            AuditURPAsset(urpAsset);
            AuditShadows(urpAsset);
            AuditPostProcessing(urpAsset);
            AuditRendererFeatures(urpAsset);
            AuditLightmapSettings();
            AuditBakeReadiness();
            AuditQualityTiers();
            AuditSceneObjects();
            AuditShaders();

            UpdateCounts();
            Debug.Log($"[URP Auditor] Audit complete: {_passCount} pass, {_infoCount} info, {_warnCount} warn, {_errorCount} error");
        }

        private void UpdateCounts()
        {
            _passCount = _results.Count(r => r.severity == Severity.Pass);
            _infoCount = _results.Count(r => r.severity == Severity.Info);
            _warnCount = _results.Count(r => r.severity == Severity.Warning);
            _errorCount = _results.Count(r => r.severity == Severity.Error);
        }

        // ── Helpers ──
        private void Pass(string cat, string msg, string cur = "", string rec = "")
            => _results.Add(new AuditResult { severity = Severity.Pass, category = cat, message = msg, currentValue = cur, recommendedValue = rec });
        private void Info(string cat, string msg, string rec = "", string cur = "", string recVal = "")
            => _results.Add(new AuditResult { severity = Severity.Info, category = cat, message = msg, recommendation = rec, currentValue = cur, recommendedValue = recVal });
        private void Warn(string cat, string msg, string rec, string cur = "", string recVal = "")
            => _results.Add(new AuditResult { severity = Severity.Warning, category = cat, message = msg, recommendation = rec, currentValue = cur, recommendedValue = recVal });
        private void Error(string cat, string msg, string rec, string cur = "", string recVal = "")
            => _results.Add(new AuditResult { severity = Severity.Error, category = cat, message = msg, recommendation = rec, currentValue = cur, recommendedValue = recVal });

        private T GetURPField<T>(UniversalRenderPipelineAsset asset, string fieldName, T fallback = default)
        {
            var field = typeof(UniversalRenderPipelineAsset).GetField(fieldName,
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (field != null) return (T)field.GetValue(asset);

            var prop = typeof(UniversalRenderPipelineAsset).GetProperty(fieldName,
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (prop != null) return (T)prop.GetValue(asset);

            return fallback;
        }

        // ══════════════════════════════════════════════
        // 1. URP ASSET
        // ══════════════════════════════════════════════
        private void AuditURPAsset(UniversalRenderPipelineAsset urp)
        {
            string cat = "URPAsset";

            // ── Render Scale ──
            float renderScale = urp.renderScale;
            switch (_targetPlatform)
            {
                case TargetPlatform.PC:
                    if (renderScale >= 0.95f)
                        Pass(cat, "Render Scale phù hợp cho PC", $"{renderScale:F2}", "1.0");
                    else
                        Warn(cat, "Render Scale thấp cho PC", "PC nên dùng 1.0 trừ khi GPU yếu.", $"{renderScale:F2}", "1.0");
                    break;
                case TargetPlatform.Quest3:
                    if (renderScale <= 0.92f && renderScale >= 0.8f)
                        Pass(cat, "Render Scale tốt cho Quest 3", $"{renderScale:F2}", "0.85–0.9");
                    else if (renderScale > 0.92f)
                        Warn(cat, "Render Scale cao cho Quest 3", "Giảm xuống 0.85–0.9. Foveated rendering sẽ bù vùng ngoại vi.", $"{renderScale:F2}", "0.85–0.9");
                    else
                        Warn(cat, "Render Scale quá thấp cho Quest 3", "0.85 là minimum khuyên dùng. Thấp hơn sẽ mờ.", $"{renderScale:F2}", "0.85–0.9");
                    break;
                case TargetPlatform.Mobile:
                    if (renderScale <= 0.88f && renderScale >= 0.7f)
                        Pass(cat, "Render Scale phù hợp cho Mobile", $"{renderScale:F2}", "0.75–0.85");
                    else if (renderScale > 0.88f)
                        Warn(cat, "Render Scale cao cho Mobile", "Giảm xuống 0.75–0.85 để tiết kiệm GPU.", $"{renderScale:F2}", "0.75–0.85");
                    else
                        Warn(cat, "Render Scale rất thấp", "Dưới 0.7 sẽ quá mờ trên hầu hết devices.", $"{renderScale:F2}", "0.75–0.85");
                    break;
            }

            // ── HDR ──
            bool hdr = urp.supportsHDR;
            if (_targetPlatform == TargetPlatform.PC)
            {
                if (hdr) Pass(cat, "HDR bật — OK cho PC", "On", "On");
                else Info(cat, "HDR tắt trên PC", "Bật HDR nếu dùng Bloom, Tone Mapping chất lượng cao.", "Off", "On");
            }
            else
            {
                if (!hdr) Pass(cat, "HDR tắt — tiết kiệm bandwidth cho " + _targetPlatform, "Off", "Off");
                else Error(cat, "HDR đang bật trên " + _targetPlatform, "Tắt HDR. Mobile/Quest chưa hỗ trợ HDR display phổ biến, tốn bandwidth.", "On", "Off");
            }

            // ── MSAA ──
            int msaa = urp.msaaSampleCount;
            switch (_targetPlatform)
            {
                case TargetPlatform.PC:
                    if (msaa >= 4) Pass(cat, "MSAA 4x — OK cho PC", $"{msaa}x", "4x");
                    else Info(cat, "MSAA thấp trên PC", "Cân nhắc 4x hoặc dùng SMAA post-process.", $"{msaa}x", "4x");
                    break;
                case TargetPlatform.Quest3:
                    if (msaa == 4) Pass(cat, "MSAA 4x — tối ưu cho Quest 3 (tile-based GPU)", $"{msaa}x", "4x");
                    else Error(cat, "Quest 3 cần MSAA 4x", "Tile-based GPU render MSAA gần như miễn phí. Luôn bật 4x.", $"{msaa}x", "4x");
                    break;
                case TargetPlatform.Mobile:
                    if (msaa <= 2) Pass(cat, "MSAA " + msaa + "x — phù hợp cho Mobile", $"{msaa}x", "2x hoặc Off");
                    else Warn(cat, "MSAA cao cho Mobile", "Giảm xuống 2x hoặc tắt, dùng FXAA thay.", $"{msaa}x", "2x hoặc Off");
                    break;
            }

            // ── SRP Batcher ──
            bool srpBatcher = urp.useSRPBatcher;
            if (srpBatcher) Pass(cat, "SRP Batcher bật — giảm CPU draw call 30–50%", "On", "On");
            else Error(cat, "SRP Batcher đang TẮT!", "Luôn bật SRP Batcher. Giảm draw call overhead 30–50%.", "Off", "On");

            // ── Depth Texture ──
            bool depthTex = urp.supportsCameraDepthTexture;
            if (_targetPlatform != TargetPlatform.PC && depthTex)
            {
                Warn(cat, "Depth Texture đang bật trên " + _targetPlatform,
                    "Chỉ cần nếu dùng soft particles, SSAO, depth effects. Tắt tiết kiệm 1 render pass.", "On", "Off (nếu không dùng)");
            }
            else if (!depthTex)
            {
                Pass(cat, "Depth Texture tắt — tiết kiệm 1 pass", "Off", "Off");
            }
            else
            {
                Pass(cat, "Depth Texture bật — OK cho PC", "On", "On");
            }

            // ── Opaque Texture ──
            bool opaqueTex = urp.supportsCameraOpaqueTexture;
            if (_targetPlatform != TargetPlatform.PC && opaqueTex)
            {
                Warn(cat, "Opaque Texture bật trên " + _targetPlatform,
                    "Chỉ cần cho refraction/distortion shaders. Tắt giảm 1 copy pass.", "On", "Off");
            }
            else
            {
                Pass(cat, "Opaque Texture — OK", opaqueTex ? "On" : "Off", _targetPlatform == TargetPlatform.PC ? "On" : "Off");
            }
        }

        // ══════════════════════════════════════════════
        // 2. SHADOWS
        // ══════════════════════════════════════════════
        private void AuditShadows(UniversalRenderPipelineAsset urp)
        {
            string cat = "Shadows";

            // Shadow distance
            float shadowDist = urp.shadowDistance;
            float maxDist = _targetPlatform switch
            {
                TargetPlatform.PC => 150f,
                TargetPlatform.Quest3 => 45f,
                TargetPlatform.Mobile => 30f,
                _ => 50f
            };
            float recMin = _targetPlatform switch
            {
                TargetPlatform.PC => 80f,
                TargetPlatform.Quest3 => 25f,
                TargetPlatform.Mobile => 15f,
                _ => 30f
            };
            float recMax = _targetPlatform switch
            {
                TargetPlatform.PC => 150f,
                TargetPlatform.Quest3 => 40f,
                TargetPlatform.Mobile => 25f,
                _ => 50f
            };

            if (shadowDist >= recMin && shadowDist <= recMax)
                Pass(cat, "Shadow Distance phù hợp", $"{shadowDist}m", $"{recMin}–{recMax}m");
            else if (shadowDist > maxDist)
                Error(cat, "Shadow Distance quá xa cho " + _targetPlatform,
                    "Giảm shadow distance. Quá xa = shadow map mất chi tiết gần camera.", $"{shadowDist}m", $"{recMin}–{recMax}m");
            else if (shadowDist < recMin)
                Info(cat, "Shadow Distance ngắn", "Có thể tăng lên nếu visual cần thiết.", $"{shadowDist}m", $"{recMin}–{recMax}m");

            // Shadow cascades
            int cascades = urp.shadowCascadeCount;
            int recCascades = _targetPlatform switch
            {
                TargetPlatform.PC => 4,
                TargetPlatform.Quest3 => 2,
                TargetPlatform.Mobile => 1,
                _ => 2
            };

            if (cascades == recCascades)
                Pass(cat, $"Shadow Cascades = {cascades} — OK", $"{cascades}", $"{recCascades}");
            else if (cascades > recCascades)
                Warn(cat, $"Shadow Cascades cao cho {_targetPlatform}",
                    $"Mỗi cascade = 1 lần render shadow map. Giảm xuống {recCascades}.", $"{cascades}", $"{recCascades}");
            else
                Info(cat, $"Shadow Cascades thấp hơn recommended", "Có thể tăng nếu shadow quality không đủ.", $"{cascades}", $"{recCascades}");

            // Main light shadow resolution
            int shadowRes = (int)GetURPField(urp, "m_MainLightShadowmapResolution", 2048);
            int recRes = _targetPlatform switch
            {
                TargetPlatform.PC => 2048,
                TargetPlatform.Quest3 => 1024,
                TargetPlatform.Mobile => 512,
                _ => 1024
            };

            if (shadowRes <= recRes)
                Pass(cat, "Shadow Resolution phù hợp", $"{shadowRes}", $"{recRes}");
            else
                Warn(cat, $"Shadow Resolution cao cho {_targetPlatform}",
                    "Giảm shadow resolution tiết kiệm fill rate và memory.", $"{shadowRes}", $"{recRes}");

            // Soft shadows
            bool softShadows = urp.supportsSoftShadows;
            if (_targetPlatform == TargetPlatform.Mobile && softShadows)
                Warn(cat, "Soft Shadows bật trên Mobile", "Tắt soft shadows trên mobile, dùng hard shadow.", "On", "Off");
            else if (_targetPlatform == TargetPlatform.Quest3 && softShadows)
                Info(cat, "Soft Shadows bật trên Quest 3", "Cân nhắc dùng Low quality soft shadow hoặc hard shadow.", "On", "Low hoặc Off");
            else
                Pass(cat, "Soft Shadows — OK", softShadows ? "On" : "Off", "");
        }

        // ══════════════════════════════════════════════
        // 3. POST-PROCESSING
        // ══════════════════════════════════════════════
        private void AuditPostProcessing(UniversalRenderPipelineAsset urp)
        {
            string cat = "PostFX";

            // Check volumes in scene
            var volumes = FindObjectsByType<Volume>(FindObjectsSortMode.None);
            if (volumes.Length == 0)
            {
                Info(cat, "Không tìm thấy Volume nào trong scene", "Thêm Global Volume cho base look nếu cần post-processing.");
                return;
            }

            int globalCount = volumes.Count(v => v.isGlobal);
            if (globalCount > 1)
                Warn(cat, $"Có {globalCount} Global Volumes", "Nên chỉ có 1 Global Volume cho base look. Dùng Local Volume cho khu vực cụ thể.");
            else if (globalCount == 1)
                Pass(cat, "1 Global Volume — OK");

            // Check each effect
            foreach (var vol in volumes)
            {
                if (vol.profile == null) continue;

                // Bloom
                if (vol.profile.TryGet<Bloom>(out var bloom) && bloom.active)
                {
                    if (_targetPlatform == TargetPlatform.Mobile)
                        Warn(cat, "Bloom đang bật trên Mobile", "Tắt Bloom hoặc dùng Low quality trên mobile.", "Active", "Off hoặc Low");
                    else
                        Pass(cat, "Bloom — OK cho " + _targetPlatform);
                }

                // Depth of Field
                if (vol.profile.TryGet<DepthOfField>(out var dof) && dof.active)
                {
                    if (_targetPlatform != TargetPlatform.PC)
                        Error(cat, "Depth of Field bật trên " + _targetPlatform,
                            "DoF rất tốn (đặc biệt Bokeh). Chỉ dùng trên PC.", "Active", "Off");
                    else
                        Pass(cat, "Depth of Field — OK cho PC");
                }

                // Motion Blur
                if (vol.profile.TryGet<MotionBlur>(out var mb) && mb.active)
                {
                    if (_targetPlatform == TargetPlatform.Quest3)
                        Error(cat, "Motion Blur bật trên Quest 3!", "Gây motion sickness trên VR. Luôn tắt.", "Active", "Off");
                    else if (_targetPlatform == TargetPlatform.Mobile)
                        Warn(cat, "Motion Blur bật trên Mobile", "Tắt trên mobile, không đủ budget.", "Active", "Off");
                    else
                        Info(cat, "Motion Blur bật trên PC", "Optional. Profile để đảm bảo không tốn quá 1ms.");
                }

                // Color Grading / Tonemapping
                if (vol.profile.TryGet<Tonemapping>(out var tm) && tm.active)
                {
                    if (_targetPlatform != TargetPlatform.PC && tm.mode.value == TonemappingMode.ACES)
                        Warn(cat, "ACES Tonemapping trên " + _targetPlatform, "ACES cần HDR pipeline. Dùng Neutral cho LDR.", "ACES", "Neutral");
                    else
                        Pass(cat, "Tonemapping — OK", tm.mode.value.ToString(), "");
                }

                // Vignette
                if (vol.profile.TryGet<Vignette>(out var vig) && vig.active)
                {
                    if (_targetPlatform == TargetPlatform.Mobile)
                        Info(cat, "Vignette bật trên Mobile", "Rẻ nhưng không cần thiết. Tắt nếu cần tiết kiệm.", "Active", "Off");
                    else
                        Pass(cat, "Vignette — OK, rẻ");
                }

                // Check for SSAO override (if using Volume overrides)
                // Note: In URP, SSAO is a Renderer Feature, not volume-based. Checked in Renderer section.
            }
        }

        // ══════════════════════════════════════════════
        // 4. RENDERER FEATURES
        // ══════════════════════════════════════════════
        private void AuditRendererFeatures(UniversalRenderPipelineAsset urp)
        {
            string cat = "Renderer";

            // Get renderer data via reflection
            var rendererListField = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (rendererListField == null)
            {
                Info(cat, "Không thể đọc Renderer Data qua reflection", "Kiểm tra thủ công Renderer Features trong Inspector.");
                return;
            }

            var rendererList = rendererListField.GetValue(urp) as ScriptableRendererData[];
            if (rendererList == null || rendererList.Length == 0)
            {
                Warn(cat, "Không tìm thấy Renderer Data", "Kiểm tra URP Asset có gán Renderer chưa.");
                return;
            }

            foreach (var rendererData in rendererList)
            {
                if (rendererData == null) continue;

                var features = rendererData.rendererFeatures;
                if (features == null || features.Count == 0)
                {
                    Info(cat, $"Renderer '{rendererData.name}' không có custom features", "Tốt nếu không cần. Mỗi feature = thêm render pass.");
                    continue;
                }

                Info(cat, $"Renderer '{rendererData.name}': {features.Count} feature(s)");

                foreach (var feature in features)
                {
                    if (feature == null) continue;

                    string fname = feature.GetType().Name;
                    bool isActive = feature.isActive;

                    // SSAO
                    if (fname.Contains("ScreenSpaceAmbientOcclusion") || fname.Contains("SSAO"))
                    {
                        if (_targetPlatform != TargetPlatform.PC)
                        {
                            if (isActive)
                                Error(cat, $"SSAO đang bật trên {_targetPlatform}",
                                    "Tắt SSAO. Dùng baked AO trong lightmap hoặc texture thay thế.", "Active", "Off");
                            else
                                Pass(cat, "SSAO đã tắt — OK cho " + _targetPlatform);
                        }
                        else
                        {
                            Pass(cat, "SSAO — OK cho PC", isActive ? "Active" : "Inactive", "");
                        }
                    }
                    // Screen Space Shadows
                    else if (fname.Contains("ScreenSpaceShadows"))
                    {
                        if (_targetPlatform != TargetPlatform.PC && isActive)
                            Warn(cat, "Screen Space Shadows bật trên " + _targetPlatform,
                                "Chỉ nên dùng trên PC. Tắt trên Quest/Mobile.", "Active", "Off");
                        else
                            Pass(cat, "Screen Space Shadows — OK", isActive ? "Active" : "Inactive", "");
                    }
                    // Decals
                    else if (fname.Contains("Decal"))
                    {
                        if (_targetPlatform == TargetPlatform.Mobile && isActive)
                            Warn(cat, "Decal Renderer bật trên Mobile", "Tốn trên mobile. Tắt nếu không dùng decal.", "Active", "Off");
                        else
                            Info(cat, $"Decal Renderer: {(isActive ? "Active" : "Inactive")}", "Chỉ bật nếu project dùng decal.");
                    }
                    // Generic feature
                    else
                    {
                        if (isActive)
                            Info(cat, $"Feature '{feature.name}' ({fname}) — Active",
                                "Kiểm tra xem feature này có cần thiết không. Mỗi feature = thêm cost.");
                        else
                            Pass(cat, $"Feature '{feature.name}' — Inactive (tốt nếu không dùng)");
                    }
                }
            }
        }

        // ══════════════════════════════════════════════
        // 5. LIGHTMAP SETTINGS
        // ══════════════════════════════════════════════
        private void AuditLightmapSettings()
        {
            string cat = "Lightmap";
            var settings = Lightmapping.lightingSettings;

            if (settings == null)
            {
                Warn(cat, "Không tìm thấy Lighting Settings", "Vào Window > Rendering > Lighting và tạo Lighting Settings asset.");
                return;
            }

            // Lightmapper type
            if (settings.lightmapper == LightingSettings.Lightmapper.ProgressiveGPU)
                Pass(cat, "Progressive GPU Lightmapper — nhanh 5–10x", "GPU", "GPU");
            else if (settings.lightmapper == LightingSettings.Lightmapper.ProgressiveCPU)
                Info(cat, "Đang dùng CPU Lightmapper", "Chuyển sang GPU Lightmapper để iterate nhanh hơn. CPU cho final bake.", "CPU", "GPU (iterate) / CPU (final)");
            else
                Warn(cat, "Lightmapper không phải Progressive", "Dùng Progressive GPU cho tốc độ tốt nhất.", settings.lightmapper.ToString(), "ProgressiveGPU");

            // Lightmap resolution
            float resolution = settings.lightmapResolution;
            float recRes = _targetPlatform switch
            {
                TargetPlatform.PC => 30f,
                TargetPlatform.Quest3 => 15f,
                TargetPlatform.Mobile => 8f,
                _ => 15f
            };
            float maxRes = _targetPlatform switch
            {
                TargetPlatform.PC => 40f,
                TargetPlatform.Quest3 => 20f,
                TargetPlatform.Mobile => 12f,
                _ => 20f
            };

            if (resolution <= maxRes)
                Pass(cat, "Lightmap Resolution phù hợp", $"{resolution} texels/unit", $"{recRes} texels/unit");
            else
                Warn(cat, "Lightmap Resolution cao cho " + _targetPlatform,
                    "Low poly không cần resolution cao. Giảm để giảm số lightmap textures.", $"{resolution} texels/unit", $"{recRes} texels/unit");

            // Lightmap padding
            int padding = settings.lightmapPadding;
            if (padding < 2)
                Error(cat, "Lightmap Padding quá nhỏ!",
                    "Low poly có nhiều UV seam. Padding < 2px sẽ gây bleeding nghiêm trọng.", $"{padding}px", "4–6px");
            else if (padding < 4)
                Warn(cat, "Lightmap Padding có thể không đủ cho low poly",
                    "Tăng lên 4–6px để tránh seam bleeding. Low poly = nhiều island nhỏ.", $"{padding}px", "4–6px");
            else
                Pass(cat, "Lightmap Padding — OK", $"{padding}px", "4–6px");

            // Max lightmap size
            int maxSize = settings.lightmapMaxSize;
            int recSize = _targetPlatform switch
            {
                TargetPlatform.PC => 2048,
                TargetPlatform.Quest3 => 1024,
                TargetPlatform.Mobile => 512,
                _ => 1024
            };

            if (maxSize <= recSize)
                Pass(cat, "Max Lightmap Size — OK", $"{maxSize}", $"{recSize}");
            else
                Warn(cat, "Max Lightmap Size lớn cho " + _targetPlatform,
                    "Giảm max size. Nhỏ hơn = nhiều atlas hơn nhưng mỗi cái nhẹ hơn.", $"{maxSize}", $"{recSize}");

            // Directional mode
            if (settings.directionalityMode == LightmapsMode.NonDirectional)
                Pass(cat, "Directional Mode: Non-Directional — tiết kiệm 50% lightmap textures");
            else if (_targetPlatform != TargetPlatform.PC)
                Warn(cat, "Directional Lightmaps trên " + _targetPlatform,
                    "Chuyển sang Non-Directional để giảm 50% số lightmap textures.", "Directional", "Non-Directional");
            else
                Pass(cat, "Directional Lightmaps — OK cho PC (đẹp hơn)");

            // Bounces
            int bounces = settings.indirectResolution > 0 ? (int)settings.indirectResolution : 2;
            // Actually check bounces via reflection or maxBounces
            try
            {
                var bounceProp = typeof(LightingSettings).GetProperty("maxBounces",
                    BindingFlags.Public | BindingFlags.Instance);
                if (bounceProp != null)
                {
                    bounces = (int)bounceProp.GetValue(settings);
                    if (bounces >= 2 && bounces <= 3)
                        Pass(cat, $"Bounces: {bounces} — tốt cho hầu hết scenes", $"{bounces}", "2–3");
                    else if (bounces > 3)
                        Info(cat, $"Bounces: {bounces} — có thể giảm", ">3 bounces hiếm khi cần và tăng bake time.", $"{bounces}", "2–3");
                    else
                        Info(cat, $"Bounces: {bounces}", "Tăng lên 2 nếu indoor scene bị tối.", $"{bounces}", "2–3");
                }
            }
            catch { /* Skip if can't read */ }

            // Ambient Occlusion in lightmap
            if (settings.ao)
                Pass(cat, "Lightmap AO bật — AO miễn phí runtime");
            else
                Warn(cat, "Lightmap AO tắt", "Bật AO trong lightmap settings. Miễn phí runtime cost, thay thế SSAO trên mobile/Quest.");

            // Compress lightmaps
            if (settings.compressLightmaps)
                Pass(cat, "Compress Lightmaps — On");
            else
                Warn(cat, "Lightmaps không nén!", "Bật Compress Lightmaps. Dùng ASTC cho mobile, BC6H cho PC.", "Off", "On");
        }

        // ══════════════════════════════════════════════
        // 6. BAKE READINESS (LOW POLY)
        // ══════════════════════════════════════════════
        private void AuditBakeReadiness()
        {
            string cat = "BakeReady";

            var meshRenderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
            var skinnedRenderers = FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None);

            if (meshRenderers.Length == 0 && skinnedRenderers.Length == 0)
            {
                Info(cat, "Không tìm thấy MeshRenderer/SkinnedMeshRenderer trong scene");
                return;
            }

            // ── Check static flags ──
            int totalMR = meshRenderers.Length;
            int staticLightmap = 0;
            int noUV2 = 0;
            int scaleWarnings = 0;
            int zeroScale = 0;
            int overlappingUV2Suspected = 0;
            List<string> noUV2Names = new List<string>();
            List<string> highScaleNames = new List<string>();

            foreach (var mr in meshRenderers)
            {
                var go = mr.gameObject;

                // Check contribute GI
                var flags = GameObjectUtility.GetStaticEditorFlags(go);
                bool isStatic = flags.HasFlag(StaticEditorFlags.ContributeGI);

                if (!isStatic) continue;

                staticLightmap++;

                // Check if mesh has UV2
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    var mesh = mf.sharedMesh;
                    if (mesh.uv2 == null || mesh.uv2.Length == 0)
                    {
                        // Check if Generate Lightmap UVs is enabled in importer
                        string path = AssetDatabase.GetAssetPath(mesh);
                        if (!string.IsNullOrEmpty(path))
                        {
                            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                            if (importer != null && !importer.generateSecondaryUV)
                            {
                                noUV2++;
                                if (noUV2Names.Count < 10) noUV2Names.Add(go.name);
                            }
                        }
                    }
                    else
                    {
                        // Simple heuristic: if UV2 count != vertex count, might have issues
                        if (mesh.uv2.Length != mesh.vertexCount)
                        {
                            overlappingUV2Suspected++;
                        }
                    }
                }

                // Check Scale In Lightmap
                float scaleInLightmap = mr.scaleInLightmap;
                if (scaleInLightmap > 2.5f)
                {
                    scaleWarnings++;
                    if (highScaleNames.Count < 5) highScaleNames.Add($"{go.name} ({scaleInLightmap:F1})");
                }
                if (scaleInLightmap <= 0.001f) zeroScale++;
            }

            // Report findings
            Info(cat, $"Scene có {totalMR} MeshRenderers, {staticLightmap} đánh dấu ContributeGI");

            if (noUV2 > 0)
            {
                string names = string.Join(", ", noUV2Names);
                if (noUV2 > 10) names += $"... và {noUV2 - 10} nữa";
                Error(cat, $"{noUV2} static mesh KHÔNG có UV2 và chưa bật Generate Lightmap UVs",
                    $"Bật 'Generate Lightmap UVs' trong Model Import Settings, hoặc tạo UV2 thủ công trong DCC. Objects: {names}");
            }
            else if (staticLightmap > 0)
            {
                Pass(cat, "Tất cả static meshes có UV2 hoặc đã bật Generate Lightmap UVs");
            }

            if (overlappingUV2Suspected > 0)
                Info(cat, $"{overlappingUV2Suspected} mesh(es) có UV2 count ≠ vertex count",
                    "Có thể UV2 bị vấn đề. Kiểm tra thủ công trong DCC tool.");

            if (scaleWarnings > 0)
            {
                string names = string.Join(", ", highScaleNames);
                Warn(cat, $"{scaleWarnings} object(s) có Scale In Lightmap > 2.5",
                    $"Scale quá cao tốn lightmap space. Cân nhắc giảm: {names}");
            }

            if (zeroScale > 0)
                Info(cat, $"{zeroScale} object(s) có Scale In Lightmap = 0 (excluded)",
                    "OK nếu là dynamic objects hoặc dùng Light Probes.");

            // ── Check Light Probes ──
            var probeGroups = FindObjectsByType<LightProbeGroup>(FindObjectsSortMode.None);
            if (probeGroups.Length == 0)
            {
                Warn(cat, "Không có Light Probe Group nào trong scene",
                    "Thêm Light Probe Groups cho dynamic objects và small props. Khoảng cách 2–4m indoor, 4–8m outdoor.");
            }
            else
            {
                int totalProbes = probeGroups.Sum(g => g.probePositions.Length);
                Pass(cat, $"{probeGroups.Length} Light Probe Group(s), tổng {totalProbes} probes");

                if (totalProbes < 20)
                    Warn(cat, "Số lượng probes ít", "Thêm probes cho coverage tốt hơn, đặc biệt ở khu vực thay đổi ánh sáng.");
            }

            // ── Check Reflection Probes ──
            var reflProbes = FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None);
            if (reflProbes.Length == 0)
                Info(cat, "Không có Reflection Probe", "Thêm ít nhất 1 Reflection Probe cho environment reflections.");
            else
                Pass(cat, $"{reflProbes.Length} Reflection Probe(s) trong scene");

            // ── Check for common low poly issues ──
            // Check if any materials use Normal Map without proper setup
            int normalMapCount = 0;
            var checkedMats = new HashSet<Material>();
            foreach (var mr in meshRenderers)
            {
                foreach (var mat in mr.sharedMaterials)
                {
                    if (mat == null || checkedMats.Contains(mat)) continue;
                    checkedMats.Add(mat);

                    if (mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") != null)
                    {
                        normalMapCount++;
                    }
                }
            }

            if (normalMapCount > 0)
            {
                Info(cat, $"{normalMapCount} material(s) sử dụng Normal Map",
                    "Đảm bảo Normal Map được bake với MikkTSpace tangent space (match Unity). Check seam trên low poly.");
            }

            // ── Check mesh tangent ──
            int noTangents = 0;
            var checkedMeshes = new HashSet<Mesh>();
            foreach (var mr in meshRenderers)
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mesh = mf.sharedMesh;
                if (checkedMeshes.Contains(mesh)) continue;
                checkedMeshes.Add(mesh);

                if ((mesh.tangents == null || mesh.tangents.Length == 0) && normalMapCount > 0)
                    noTangents++;
            }

            if (noTangents > 0)
                Warn(cat, $"{noTangents} mesh(es) không có tangent data",
                    "Mesh cần tangents để render normal map đúng. Bật 'Tangents: Calculate MikkTSpace' trong Model Import.");
        }

        // ══════════════════════════════════════════════
        // 7. QUALITY TIERS
        // ══════════════════════════════════════════════
        private void AuditQualityTiers()
        {
            string cat = "Quality";

            var qualityNames = QualitySettings.names;
            int qualityCount = qualityNames.Length;

            if (qualityCount < 2)
            {
                Warn(cat, $"Chỉ có {qualityCount} Quality Level",
                    "Nên tạo ít nhất 3 levels: PC High, Quest, Mobile. Mỗi level gán URP Asset riêng.");
            }
            else if (qualityCount < 4)
            {
                Info(cat, $"{qualityCount} Quality Levels: {string.Join(", ", qualityNames)}",
                    "Recommended: 4–6 levels cho multi-platform (Ultra, High, Medium, Quest, Mobile High, Mobile Low).");
            }
            else
            {
                Pass(cat, $"{qualityCount} Quality Levels: {string.Join(", ", qualityNames)}");
            }

            // Check if each quality level has a URP asset
            int currentLevel = QualitySettings.GetQualityLevel();
            for (int i = 0; i < qualityCount; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                var pipeline = QualitySettings.renderPipeline;
                if (pipeline == null)
                {
                    Warn(cat, $"Quality Level '{qualityNames[i]}' không có Render Pipeline Asset",
                        "Gán URP Asset riêng cho mỗi quality level trong Project Settings > Quality.");
                }
                else
                {
                    Pass(cat, $"'{qualityNames[i]}' → {pipeline.name}");
                }
            }
            QualitySettings.SetQualityLevel(currentLevel, false);
        }

        // ══════════════════════════════════════════════
        // 8. SCENE OBJECTS
        // ══════════════════════════════════════════════
        private void AuditSceneObjects()
        {
            string cat = "Scene";

            // Count lights
            var lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            int realtimeLights = lights.Count(l => l.lightmapBakeType == LightmapBakeType.Realtime);
            int mixedLights = lights.Count(l => l.lightmapBakeType == LightmapBakeType.Mixed);
            int bakedLights = lights.Count(l => l.lightmapBakeType == LightmapBakeType.Baked);
            int shadowCastingLights = lights.Count(l => l.shadows != LightShadows.None && l.lightmapBakeType != LightmapBakeType.Baked);

            Info(cat, $"Lights: {lights.Length} total ({realtimeLights} realtime, {mixedLights} mixed, {bakedLights} baked)");

            int maxRealtime = _targetPlatform switch
            {
                TargetPlatform.PC => 8,
                TargetPlatform.Quest3 => 4,
                TargetPlatform.Mobile => 2,
                _ => 4
            };

            if (realtimeLights + mixedLights > maxRealtime)
                Warn(cat, $"{realtimeLights + mixedLights} realtime/mixed lights — nhiều cho {_targetPlatform}",
                    $"Recommend tối đa {maxRealtime} realtime lights. Chuyển còn lại sang Baked.", $"{realtimeLights + mixedLights}", $"≤{maxRealtime}");

            if (shadowCastingLights > 1 && _targetPlatform != TargetPlatform.PC)
                Warn(cat, $"{shadowCastingLights} lights đang cast shadow realtime",
                    $"Trên {_targetPlatform}, chỉ nên 1 light (main directional) cast shadow. Tắt shadow cho additional lights.");

            // Mesh complexity
            var meshFilters = FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
            long totalTris = 0;
            int meshCount = 0;
            foreach (var mf in meshFilters)
            {
                if (mf.sharedMesh != null)
                {
                    totalTris += mf.sharedMesh.triangles.Length / 3;
                    meshCount++;
                }
            }

            long maxTris = _targetPlatform switch
            {
                TargetPlatform.PC => 2_000_000,
                TargetPlatform.Quest3 => 500_000,
                TargetPlatform.Mobile => 200_000,
                _ => 500_000
            };

            string triStr = totalTris > 1_000_000 ? $"{totalTris / 1_000_000f:F1}M" : $"{totalTris / 1000f:F0}K";
            string maxStr = maxTris > 1_000_000 ? $"{maxTris / 1_000_000f:F1}M" : $"{maxTris / 1000f:F0}K";

            if (totalTris <= maxTris)
                Pass(cat, $"Scene triangles: {triStr} ({meshCount} meshes)", triStr, $"≤{maxStr}");
            else
                Warn(cat, $"Scene triangles: {triStr} — cao cho {_targetPlatform}",
                    "Sử dụng LOD, occlusion culling, hoặc giảm mesh complexity.", triStr, $"≤{maxStr}");

            // Draw calls estimate
            int rendererCount = FindObjectsByType<Renderer>(FindObjectsSortMode.None).Length;
            int maxRenderers = _targetPlatform switch
            {
                TargetPlatform.PC => 2000,
                TargetPlatform.Quest3 => 500,
                TargetPlatform.Mobile => 300,
                _ => 500
            };

            if (rendererCount <= maxRenderers)
                Pass(cat, $"Renderers: {rendererCount}", $"{rendererCount}", $"≤{maxRenderers}");
            else
                Warn(cat, $"Renderers: {rendererCount} — nhiều cho {_targetPlatform}",
                    "Cân nhắc mesh combining, static batching, hoặc LOD culling.", $"{rendererCount}", $"≤{maxRenderers}");

            // Particle systems
            var particles = FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
            if (particles.Length > 0)
            {
                int activeParticles = particles.Count(p => p.isPlaying);
                Info(cat, $"{particles.Length} Particle Systems ({activeParticles} playing)",
                    "Mỗi particle system = draw call(s). Profile để đảm bảo trong budget.");
            }
        }

        // ══════════════════════════════════════════════
        // 9. SHADERS & VARIANTS
        // ══════════════════════════════════════════════
        private void AuditShaders()
        {
            string cat = "Shaders";

            // Check shader stripping settings
            var graphicsSettings = GraphicsSettings.currentRenderPipeline;
            if (graphicsSettings != null)
            {
                Info(cat, "Shader Stripping nên được configure trong URP Asset và Graphics Settings",
                    "Tắt unused features trong URP Asset Inspector để tự động strip variants.");
            }

            // Count unique shaders in scene
            var renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            var shaders = new HashSet<Shader>();
            var materials = new HashSet<Material>();
            int shaderErrors = 0;

            foreach (var r in renderers)
            {
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null) continue;
                    materials.Add(mat);

                    if (mat.shader == null || mat.shader.name == "Hidden/InternalErrorShader")
                    {
                        shaderErrors++;
                        continue;
                    }
                    shaders.Add(mat.shader);
                }
            }

            Pass(cat, $"Scene sử dụng {shaders.Count} unique shaders, {materials.Count} unique materials");

            if (shaderErrors > 0)
                Error(cat, $"{shaderErrors} material(s) có shader lỗi!",
                    "Kiểm tra và fix shader errors. Missing shaders sẽ fallback sang error shader (hồng).");

            // Check for non-URP shaders
            int nonURPShaders = 0;
            foreach (var shader in shaders)
            {
                string name = shader.name;
                if (!name.StartsWith("Universal Render Pipeline") &&
                    !name.StartsWith("Shader Graphs") &&
                    !name.StartsWith("Hidden") &&
                    !name.StartsWith("Sprites") &&
                    !name.StartsWith("UI"))
                {
                    nonURPShaders++;
                }
            }

            if (nonURPShaders > 0)
                Warn(cat, $"{nonURPShaders} shader(s) không phải URP standard",
                    "Custom shaders hoặc legacy shaders. Đảm bảo chúng compatible với SRP Batcher.");

            // Keyword check - look for common wasteful keywords
            Info(cat, "Kiểm tra Shader Variant Log",
                "Bật Edit > Project Settings > Graphics > Log Shader Compilation. Build và check console cho số variants.");

            int targetVariants = _targetPlatform switch
            {
                TargetPlatform.PC => 20000,
                TargetPlatform.Quest3 => 8000,
                TargetPlatform.Mobile => 5000,
                _ => 10000
            };

            Info(cat, $"Target variants cho {_targetPlatform}: < {targetVariants:N0}",
                "Dùng IPreprocessShaders script để strip variants không cần thiết.");
        }

        // ══════════════════════════════════════════════
        // EXPORT REPORT
        // ══════════════════════════════════════════════
        private void CopyReportToClipboard()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine($"URP SETUP AUDIT REPORT — {_targetPlatform}");
            sb.AppendLine($"Scene: {SceneManager.GetActiveScene().name}");
            sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine($"Score: {(_results.Count > 0 ? (float)_passCount / _results.Count * 100f : 0):F0}%");
            sb.AppendLine($"Pass: {_passCount} | Info: {_infoCount} | Warn: {_warnCount} | Error: {_errorCount}");
            sb.AppendLine();

            string lastCat = "";
            foreach (var r in _results)
            {
                if (r.category != lastCat)
                {
                    sb.AppendLine($"── {r.category} ──");
                    lastCat = r.category;
                }

                string icon = r.severity switch
                {
                    Severity.Pass => "[PASS]",
                    Severity.Info => "[INFO]",
                    Severity.Warning => "[WARN]",
                    Severity.Error => "[ERROR]",
                    _ => ""
                };

                sb.AppendLine($"  {icon} {r.message}");
                if (!string.IsNullOrEmpty(r.currentValue))
                    sb.AppendLine($"         Current: {r.currentValue} → Recommended: {r.recommendedValue}");
                if (r.severity != Severity.Pass && !string.IsNullOrEmpty(r.recommendation))
                    sb.AppendLine($"         💡 {r.recommendation}");
            }

            GUIUtility.systemCopyBuffer = sb.ToString();
            Debug.Log("[URP Auditor] Report copied to clipboard!");
        }
    }
}
#endif
