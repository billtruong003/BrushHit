using UnityEditor;
using UnityEngine;

public class ToonLavaGUI : ShaderGUI
{
    // ── Foldout states (persisted per-session via EditorPrefs) ──
    static bool _foldTextures = true;
    static bool _foldScrolling = false;
    static bool _foldColors = true;
    static bool _foldEdgeGlow = true;
    static bool _foldTopGlow = false;
    static bool _foldWaves = false;
    static bool _foldCelShading = false;

    // ── Styles (lazy init) ──
    static GUIStyle _headerStyle;
    static GUIStyle _sectionBox;
    static bool _stylesInit;

    static readonly Color SectionBg = new Color(0.22f, 0.22f, 0.22f, 0.4f);
    static readonly Color AccentLava = new Color(1f, 0.45f, 0.1f, 1f);

    static void InitStyles()
    {
        if (_stylesInit) return;
        _stylesInit = true;

        _headerStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 12,
            richText = true
        };

        _sectionBox = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(10, 10, 6, 6),
            margin = new RectOffset(0, 0, 2, 4)
        };
    }

    public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        InitStyles();
        Material mat = materialEditor.target as Material;

        EditorGUILayout.Space(4);

        // ── Header Banner ──
        DrawBanner("TOON LAVA", AccentLava);

        EditorGUILayout.Space(4);

        // ━━ Textures ━━
        _foldTextures = DrawSection("Lava Textures", _foldTextures, () =>
        {
            DrawTextureSingleLine(materialEditor, properties, "_MainTex", "Main Lava Texture");
            DrawTextureSingleLine(materialEditor, properties, "_NoiseTex", "Noise / Distort Texture");
        });

        // ━━ Scrolling & Distortion ━━
        _foldScrolling = DrawSection("Scrolling & Distortion", _foldScrolling, () =>
        {
            DrawProp(materialEditor, properties, "_Scale", "Noise Scale");
            DrawProp(materialEditor, properties, "_MainScale", "Main Texture Scale");
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Distortion Speed", EditorStyles.miniLabel);
            EditorGUI.indentLevel++;
            DrawProp(materialEditor, properties, "_SpeedDistortX", "X");
            DrawProp(materialEditor, properties, "_SpeedDistortY", "Y");
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Main Scroll Speed", EditorStyles.miniLabel);
            EditorGUI.indentLevel++;
            DrawProp(materialEditor, properties, "_SpeedMainX", "X");
            DrawProp(materialEditor, properties, "_SpeedMainY", "Y");
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
            DrawProp(materialEditor, properties, "_DistortionStrength", "Noise Distortion");
            DrawProp(materialEditor, properties, "_VCDistortionStrength", "Vertex Color Distortion");
        });

        // ━━ Colors ━━
        _foldColors = DrawSection("Colors", _foldColors, () =>
        {
            DrawProp(materialEditor, properties, "_TintStart", "Cool Tint");
            DrawProp(materialEditor, properties, "_TintEnd", "Hot Tint");
            DrawProp(materialEditor, properties, "_TintOffset", "Tint Offset");
            DrawProp(materialEditor, properties, "_BrightnessUnder", "Under-Surface Brightness");
        });

        // ━━ Edge Glow (Intersection) ━━
        _foldEdgeGlow = DrawSection("Edge Glow (Intersection)", _foldEdgeGlow, () =>
        {
            DrawProp(materialEditor, properties, "_EdgeThickness", "Thickness");
            DrawProp(materialEditor, properties, "_EdgeSmoothness", "Edge Smoothness");
            DrawHelpBox("0 = hard stylized edge  ·  1 = soft glow blend");
            DrawProp(materialEditor, properties, "_EdgeColor", "Edge Color (HDR)");
            DrawProp(materialEditor, properties, "_EdgeBrightness", "Brightness");
        });

        // ━━ Top Glow ━━
        _foldTopGlow = DrawSection("Top Glow", _foldTopGlow, () =>
        {
            DrawProp(materialEditor, properties, "_CutoffTop", "Cutoff");
            DrawProp(materialEditor, properties, "_TopSmoothness", "Top Smoothness");
            DrawHelpBox("0 = crispy cutoff  ·  1 = soft fade");
            DrawProp(materialEditor, properties, "_TopColor", "Top Color (HDR)");
        });

        // ━━ Waves ━━
        _foldWaves = DrawSection("Vertex Waves", _foldWaves, () =>
        {
            DrawProp(materialEditor, properties, "_WaveAmount", "Amount");
            DrawProp(materialEditor, properties, "_WaveSpeed", "Speed");
            DrawProp(materialEditor, properties, "_WaveHeight", "Height");
        });

        // ━━ Cel Shading ━━
        _foldCelShading = DrawSection("Cel Shading", _foldCelShading, () =>
        {
            DrawProp(materialEditor, properties, "_ShadowColor", "Shadow Color");
            DrawProp(materialEditor, properties, "_Threshold", "Shadow Threshold");
            DrawProp(materialEditor, properties, "_Smoothness", "Shadow Smoothness");
        });

        EditorGUILayout.Space(6);
        materialEditor.RenderQueueField();
    }

    // ════════════════════════════════════════════════════════════════
    // Drawing Helpers
    // ════════════════════════════════════════════════════════════════

    static bool DrawSection(string title, bool foldout, System.Action drawContent)
    {
        EditorGUILayout.Space(2);
        Rect headerRect = GUILayoutUtility.GetRect(1f, 22f, GUILayout.ExpandWidth(true));

        // Background
        Color bgCol = foldout ? new Color(0.25f, 0.25f, 0.25f, 0.6f) : new Color(0.2f, 0.2f, 0.2f, 0.3f);
        EditorGUI.DrawRect(headerRect, bgCol);

        // Left accent bar
        Rect accentRect = new Rect(headerRect.x, headerRect.y, 3f, headerRect.height);
        EditorGUI.DrawRect(accentRect, AccentLava);

        // Foldout
        Event e = Event.current;
        if (e.type == EventType.MouseDown && headerRect.Contains(e.mousePosition))
        {
            foldout = !foldout;
            e.Use();
        }

        // Arrow + label
        Rect labelRect = new Rect(headerRect.x + 16f, headerRect.y + 2f, headerRect.width - 16f, headerRect.height);
        string arrow = foldout ? "▼ " : "► ";
        EditorGUI.LabelField(labelRect, arrow + title, _headerStyle);

        if (foldout)
        {
            EditorGUILayout.BeginVertical(_sectionBox);
            drawContent();
            EditorGUILayout.EndVertical();
        }

        return foldout;
    }

    static void DrawBanner(string text, Color color)
    {
        Rect r = GUILayoutUtility.GetRect(1f, 28f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(r, new Color(color.r * 0.3f, color.g * 0.3f, color.b * 0.3f, 0.8f));

        Rect accent = new Rect(r.x, r.y, r.width, 2f);
        EditorGUI.DrawRect(accent, color);

        GUIStyle bannerStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = color }
        };
        EditorGUI.LabelField(r, text, bannerStyle);
    }

    static void DrawProp(MaterialEditor editor, MaterialProperty[] props, string name, string label)
    {
        MaterialProperty p = FindProperty(name, props, false);
        if (p != null)
            editor.ShaderProperty(p, label);
    }

    static void DrawTextureSingleLine(MaterialEditor editor, MaterialProperty[] props, string name, string label)
    {
        MaterialProperty p = FindProperty(name, props, false);
        if (p != null)
            editor.TexturePropertySingleLine(new GUIContent(label), p);
    }

    static void DrawHelpBox(string msg)
    {
        EditorGUILayout.LabelField(msg, EditorStyles.centeredGreyMiniLabel);
    }
}