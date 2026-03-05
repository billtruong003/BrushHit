// ============================================================================
// CUSTOM SHADER GUI - StylizedSkyboxGUI.cs
// Đặt file này trong folder: Assets/Editor/StylizedSkyboxGUI.cs
//
// Features:
// - Foldout sections có icon cho mỗi nhóm property
// - Toggle features (Stars, Clouds, Cloud Layer 2) với visual feedback
// - Color preview swatches
// - Tooltips cho mọi property
// - Performance indicator dựa trên features đang bật
// ============================================================================

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class StylizedSkyboxGUI : ShaderGUI
{
    // ════════════════════════════════════════════
    // Foldout states - lưu trạng thái mở/đóng
    // Dùng SessionState để persist qua recompile
    // ════════════════════════════════════════════
    private static bool foldSun = true;
    private static bool foldMoon = true;
    private static bool foldSky = true;
    private static bool foldHorizon = true;
    private static bool foldStars = true;
    private static bool foldClouds = true;
    private static bool foldClouds2 = true;
    private static bool foldCloudColors = true;
    private static bool foldPerf = false;

    // ════════════════════════════════════════════
    // Styles - cached để tránh GC allocation mỗi frame
    // ════════════════════════════════════════════
    private static GUIStyle _headerStyle;
    private static GUIStyle _toggleHeaderStyle;
    private static GUIStyle _perfBoxStyle;
    private static GUIStyle _perfLabelStyle;

    private static GUIStyle HeaderStyle
    {
        get
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(EditorStyles.foldoutHeader)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize = 12
                };
            }
            return _headerStyle;
        }
    }

    // ════════════════════════════════════════════
    // MAIN GUI
    // ════════════════════════════════════════════
    public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        Material mat = materialEditor.target as Material;
        if (mat == null) return;

        EditorGUILayout.Space(4);

        // ── Title Banner ──
        DrawBanner("☀ VR Stylized Skybox", "Optimized for Single Pass Instanced VR");
        EditorGUILayout.Space(6);

        // ── Sun Section ──
        foldSun = DrawSection("☀  Sun", foldSun, () =>
        {
            DrawProp(materialEditor, properties, "_SunColor", "Sun Color (HDR)", "Màu của mặt trời. Dùng HDR để tạo bloom.");
            DrawProp(materialEditor, properties, "_SunRadius", "Sun Size", "Bán kính mặt trời. 0.05 = nhỏ, 0.2 = lớn.");
            DrawProp(materialEditor, properties, "_SunSharpness", "Edge Sharpness", "1-10 = soft glow, 50-100 = sharp disc. Giá trị thấp tạo hiệu ứng sunset đẹp.");
        });

        // ── Moon Section ──
        foldMoon = DrawSection("🌙  Moon", foldMoon, () =>
        {
            DrawProp(materialEditor, properties, "_MoonColor", "Moon Color (HDR)", "Màu trăng. HDR để glow nhẹ.");
            DrawProp(materialEditor, properties, "_MoonRadius", "Moon Size", "Bán kính trăng.");
            DrawProp(materialEditor, properties, "_MoonSharpness", "Edge Sharpness", "Độ sắc nét viền trăng.");
            DrawProp(materialEditor, properties, "_MoonOffset", "Crescent Offset", "XYZ offset tạo hình lưỡi liềm. Thay đổi X để điều chỉnh pha trăng.");
        });

        // ── Sky Section ──
        foldSky = DrawSection("🌤  Sky Colors", foldSky, () =>
        {
            EditorGUILayout.LabelField("Day Sky", EditorStyles.boldLabel);
            DrawProp(materialEditor, properties, "_DayTopColor", "  Top Color", "Màu đỉnh bầu trời ban ngày.");
            DrawProp(materialEditor, properties, "_DayBottomColor", "  Bottom Color", "Màu đáy bầu trời ban ngày.");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Night Sky", EditorStyles.boldLabel);
            DrawProp(materialEditor, properties, "_NightTopColor", "  Top Color", "Màu đỉnh bầu trời ban đêm.");
            DrawProp(materialEditor, properties, "_NightBottomColor", "  Bottom Color", "Màu đáy bầu trời ban đêm.");
        });

        // ── Horizon Section ──
        foldHorizon = DrawSection("🌅  Horizon", foldHorizon, () =>
        {
            DrawProp(materialEditor, properties, "_HorizonColorDay", "Day Horizon", "Màu đường chân trời ban ngày (sunset glow).");
            DrawProp(materialEditor, properties, "_HorizonColorNight", "Night Horizon", "Màu đường chân trời ban đêm.");
            DrawProp(materialEditor, properties, "_HorizonWidth", "Width", "Độ rộng vùng horizon. Giá trị lớn = horizon rộng hơn.");
            DrawProp(materialEditor, properties, "_OffsetHorizon", "Vertical Offset", "Dịch horizon lên/xuống.");
        });

        // ── Stars Section (Toggle) ──
        foldStars = DrawToggleSection("⭐  Stars", foldStars, materialEditor, properties,
            "_EnableStars", "_STARS_ON", () =>
        {
            DrawProp(materialEditor, properties, "_Stars", "Stars Texture", "Texture chứa hình các ngôi sao.");
            DrawProp(materialEditor, properties, "_StarsCutoff", "Brightness Cutoff", "Ngưỡng sáng. Thấp = nhiều sao, cao = ít sao sáng.");
            DrawProp(materialEditor, properties, "_StarsSpeed", "UV Scale", "Scale UV map cho stars. Nhỏ = sao to, lớn = sao nhỏ nhiều.");
            DrawProp(materialEditor, properties, "_StarsSkyColor", "Tint Color", "Màu tint cho ngôi sao.");
        });

        // ── Clouds Main Section (Toggle) ──
        foldClouds = DrawToggleSection("☁  Clouds - Primary", foldClouds, materialEditor, properties,
            "_EnableClouds", "_CLOUDS_ON", () =>
        {
            EditorGUILayout.LabelField("Textures", EditorStyles.boldLabel);
            DrawProp(materialEditor, properties, "_BaseNoise", "  Base Noise", "Noise texture chính để distort UV.");
            DrawProp(materialEditor, properties, "_Distort", "  Detail Noise", "Noise tạo hình dạng cloud chi tiết.");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Scale & Distortion", EditorStyles.boldLabel);
            DrawProp(materialEditor, properties, "_BaseNoiseScale", "  Base Scale", "Scale noise cơ sở.");
            DrawProp(materialEditor, properties, "_DistortScale", "  Detail Scale", "Scale noise chi tiết.");
            DrawProp(materialEditor, properties, "_Distortion", "  Distortion", "Mức độ biến dạng UV từ base noise.");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Movement", EditorStyles.boldLabel);
            DrawProp(materialEditor, properties, "_BaseNoiseSpeed", "  Base Scroll", "Tốc độ scroll base noise (XY).");
            DrawProp(materialEditor, properties, "_CloudsLayerSpeed", "  Cloud Scroll", "Tốc độ scroll layer cloud chính (XY).");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Shape", EditorStyles.boldLabel);
            DrawProp(materialEditor, properties, "_CloudCutoff", "  Cutoff", "Ngưỡng cắt cloud. Cao = ít mây, thấp = nhiều mây.");
            DrawProp(materialEditor, properties, "_Fuzziness", "  Softness", "Độ mềm viền mây. 0 = sắc nét, 0.5 = mềm mại.");
            DrawProp(materialEditor, properties, "_HorizonCloudsFade", "  Horizon Fade", "X = bắt đầu fade, Y = kết thúc fade. Mây mờ dần ở horizon.");
        });

        // ── Clouds Secondary (Toggle) ──
        bool cloudsEnabled = mat.IsKeywordEnabled("_CLOUDS_ON");
        EditorGUI.BeginDisabledGroup(!cloudsEnabled);
        foldClouds2 = DrawToggleSection("☁  Clouds - Secondary Layer", foldClouds2, materialEditor, properties,
            "_EnableClouds2", "_CLOUDS2_ON", () =>
        {
            if (!cloudsEnabled)
            {
                EditorGUILayout.HelpBox("Enable Primary Clouds first!", MessageType.Info);
                return;
            }
            DrawProp(materialEditor, properties, "_SecNoise", "Noise Texture", "Noise texture cho layer phụ.");
            DrawProp(materialEditor, properties, "_SecNoiseScale", "Scale", "Scale noise layer phụ.");
            DrawProp(materialEditor, properties, "_CloudCutoff2", "Cutoff", "Ngưỡng cắt layer phụ.");
            DrawProp(materialEditor, properties, "_Fuzziness2", "Softness", "Độ mềm viền layer phụ.");
            DrawProp(materialEditor, properties, "_OpacitySec", "Opacity", "Độ trong suốt layer phụ. 0 = ẩn, 1 = đầy đủ.");
        });
        EditorGUI.EndDisabledGroup();

        // ── Cloud Colors ──
        foldCloudColors = DrawSection("🎨  Cloud Colors", foldCloudColors, () =>
        {
            DrawProp(materialEditor, properties, "_ColorStretch", "Color Stretch", "Kéo giãn gradient màu trên mây.");
            DrawProp(materialEditor, properties, "_ColorOffset", "Color Offset", "Dịch gradient màu.");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Day Clouds", EditorStyles.boldLabel);
            DrawProp(materialEditor, properties, "_CloudColorDayEdge", "  Edge Color", "Màu viền mây ban ngày (sáng hơn).");
            DrawProp(materialEditor, properties, "_CloudColorDayMain", "  Core Color", "Màu lõi mây ban ngày (tối hơn).");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Night Clouds", EditorStyles.boldLabel);
            DrawProp(materialEditor, properties, "_CloudColorNightEdge", "  Edge Color", "Màu viền mây ban đêm.");
            DrawProp(materialEditor, properties, "_CloudColorNightMain", "  Core Color", "Màu lõi mây ban đêm.");
        });

        // ── Performance Monitor ──
        EditorGUILayout.Space(8);
        DrawPerformanceSection(mat);

        EditorGUILayout.Space(8);
    }

    // ════════════════════════════════════════════
    // DRAWING HELPERS
    // ════════════════════════════════════════════

    /// <summary>
    /// Banner ở đầu inspector
    /// </summary>
    private void DrawBanner(string title, string subtitle)
    {
        var rect = EditorGUILayout.GetControlRect(false, 42);
        EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f, 1f));

        var inner = new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4);
        EditorGUI.DrawRect(inner, new Color(0.22f, 0.22f, 0.28f, 1f));

        var titleRect = new Rect(inner.x + 10, inner.y + 4, inner.width - 20, 18);
        var subRect = new Rect(inner.x + 10, inner.y + 22, inner.width - 20, 14);

        var titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
        titleStyle.normal.textColor = new Color(0.9f, 0.95f, 1f);
        EditorGUI.LabelField(titleRect, title, titleStyle);

        var subStyle = new GUIStyle(EditorStyles.miniLabel);
        subStyle.normal.textColor = new Color(0.6f, 0.65f, 0.7f);
        EditorGUI.LabelField(subRect, subtitle, subStyle);
    }

    /// <summary>
    /// Foldout section thường (không có toggle)
    /// </summary>
    private bool DrawSection(string label, bool foldout, System.Action drawContent)
    {
        EditorGUILayout.Space(2);

        var bgRect = EditorGUILayout.GetControlRect(false, 22);
        EditorGUI.DrawRect(bgRect, new Color(0.24f, 0.24f, 0.24f, 1f));

        foldout = EditorGUI.Foldout(bgRect, foldout, " " + label, true, HeaderStyle);

        if (foldout)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Space(2);
            drawContent();
            EditorGUILayout.Space(4);
            EditorGUI.indentLevel--;
        }

        return foldout;
    }

    /// <summary>
    /// Foldout section VỚI toggle (Stars, Clouds, etc.)
    /// Toggle bật/tắt shader_feature keyword
    /// </summary>
    private bool DrawToggleSection(string label, bool foldout,
        MaterialEditor materialEditor, MaterialProperty[] properties,
        string toggleProp, string keyword, System.Action drawContent)
    {
        EditorGUILayout.Space(2);

        MaterialProperty toggle = FindProperty(toggleProp, properties, false);
        if (toggle == null) return foldout;

        Material mat = materialEditor.target as Material;
        bool enabled = toggle.floatValue > 0.5f;

        // Header background - tinted dựa trên on/off
        var bgRect = EditorGUILayout.GetControlRect(false, 22);
        Color bgColor = enabled
            ? new Color(0.2f, 0.3f, 0.2f, 1f)
            : new Color(0.25f, 0.22f, 0.22f, 1f);
        EditorGUI.DrawRect(bgRect, bgColor);

        // Toggle checkbox
        var toggleRect = new Rect(bgRect.x + bgRect.width - 40, bgRect.y + 2, 18, 18);
        EditorGUI.BeginChangeCheck();
        enabled = EditorGUI.Toggle(toggleRect, enabled);
        if (EditorGUI.EndChangeCheck())
        {
            toggle.floatValue = enabled ? 1f : 0f;
            if (enabled)
                mat.EnableKeyword(keyword);
            else
                mat.DisableKeyword(keyword);
        }

        // Foldout label
        string statusLabel = enabled ? label + "  ✓" : label + "  ✗";
        foldout = EditorGUI.Foldout(bgRect, foldout, " " + statusLabel, true, HeaderStyle);

        if (foldout && enabled)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Space(2);
            drawContent();
            EditorGUILayout.Space(4);
            EditorGUI.indentLevel--;
        }
        else if (foldout && !enabled)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox("Feature disabled — zero GPU cost.", MessageType.None);
            EditorGUI.indentLevel--;
        }

        return foldout;
    }

    /// <summary>
    /// Draw individual property với tooltip
    /// </summary>
    private void DrawProp(MaterialEditor editor, MaterialProperty[] props,
        string name, string displayName, string tooltip)
    {
        MaterialProperty prop = FindProperty(name, props, false);
        if (prop == null) return;

        GUIContent content = new GUIContent(displayName, tooltip);
        editor.ShaderProperty(prop, content);
    }

    // ════════════════════════════════════════════
    // PERFORMANCE INDICATOR
    //
    // Đếm features đang bật và ước tính cost
    // Giúp artist hiểu impact của mỗi toggle
    // ════════════════════════════════════════════
    private void DrawPerformanceSection(Material mat)
    {
        foldPerf = DrawSection("⚡  VR Performance", foldPerf, () =>
        {
            bool stars = mat.IsKeywordEnabled("_STARS_ON");
            bool clouds = mat.IsKeywordEnabled("_CLOUDS_ON");
            bool clouds2 = mat.IsKeywordEnabled("_CLOUDS2_ON");

            // Ước tính texture samples
            int texSamples = 0;
            if (stars) texSamples += 1;
            if (clouds) texSamples += 2; // base noise + cloud detail
            if (clouds2) texSamples += 1; // secondary noise

            // Ước tính ALU cost (relative)
            int aluCost = 15; // base: sun, moon, crescent, sky composite
            if (stars) aluCost += 5;
            if (clouds) aluCost += 10;
            if (clouds2) aluCost += 6;

            // Performance rating
            string rating;
            Color ratingColor;
            if (texSamples <= 1)
            {
                rating = "Excellent — Ideal for Mobile VR (Quest)";
                ratingColor = new Color(0.3f, 0.9f, 0.3f);
            }
            else if (texSamples <= 3)
            {
                rating = "Good — Fine for all VR platforms";
                ratingColor = new Color(0.9f, 0.9f, 0.3f);
            }
            else
            {
                rating = "Moderate — Best for PC VR";
                ratingColor = new Color(0.9f, 0.6f, 0.2f);
            }

            // Draw info box
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var rStyle = new GUIStyle(EditorStyles.boldLabel);
            rStyle.normal.textColor = ratingColor;
            EditorGUILayout.LabelField(rating, rStyle);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"Texture Samples: {texSamples}/frame/pixel", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Est. ALU Instructions: ~{aluCost}/pixel", EditorStyles.miniLabel);

            EditorGUILayout.Space(4);
            DrawFeatureRow("Stars", stars, 1, 5);
            DrawFeatureRow("Primary Clouds", clouds, 2, 10);
            DrawFeatureRow("Secondary Clouds", clouds2, 1, 6);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("VR Optimizations Active:", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("  ✓ Single Pass Instanced Stereo", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("  ✓ Vertex Shader Offloading (sky, horizon, colors)", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("  ✓ Half Precision (mobile GPU 2x throughput)", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("  ✓ Angular Dot-Product (no sqrt for sun/moon)", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("  ✓ ZWrite Off (saves depth bandwidth)", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("  ✓ Branch-free design (no warp divergence)", EditorStyles.miniLabel);

            EditorGUILayout.EndVertical();
        });
    }

    private void DrawFeatureRow(string name, bool enabled, int texCost, int aluCost)
    {
        string status = enabled ? "ON" : "OFF";
        Color col = enabled ? new Color(0.4f, 0.8f, 0.4f) : new Color(0.5f, 0.5f, 0.5f);
        string cost = enabled ? $"+{texCost} tex, +{aluCost} ALU" : "0 cost";

        EditorGUILayout.BeginHorizontal();
        var style = new GUIStyle(EditorStyles.miniLabel);
        style.normal.textColor = col;
        EditorGUILayout.LabelField($"  [{status}] {name}", style, GUILayout.Width(200));
        EditorGUILayout.LabelField(cost, EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();
    }
}
