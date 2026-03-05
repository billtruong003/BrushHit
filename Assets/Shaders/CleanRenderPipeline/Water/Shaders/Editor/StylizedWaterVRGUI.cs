using UnityEditor;
using UnityEngine;

public class StylizedWaterVRGUI : ShaderGUI
{
    // ── Foldout states ──
    static bool _foldColor = true;
    static bool _foldNormals = false;
    static bool _foldRefraction = false;
    static bool _foldSurfaceFoam = true;
    static bool _foldIntersection = true;
    static bool _foldBling = false;
    static bool _foldWaves = false;

    // ── Styles ──
    static GUIStyle _headerStyle;
    static GUIStyle _sectionBox;
    static bool _stylesInit;

    static readonly Color AccentWater = new Color(0.3f, 0.7f, 1f, 1f);

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
        DrawBanner("STYLIZED WATER", AccentWater);
        EditorGUILayout.Space(4);

        // ━━ Color & Depth ━━
        _foldColor = DrawSection("Color & Depth", _foldColor, () =>
        {
            DrawProp(materialEditor, properties, "_ShallowColor", "Shallow Color");
            DrawProp(materialEditor, properties, "_DeepColor", "Deep Color (HDR)");
            DrawProp(materialEditor, properties, "_DepthMaxDistance", "Depth Max Distance");
        });

        // ━━ Normals ━━
        _foldNormals = DrawSection("Normal Map", _foldNormals, () =>
        {
            DrawTextureSingleLine(materialEditor, properties, "_NormalMap", "Normal Map");
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Layer A", EditorStyles.miniLabel);
            EditorGUI.indentLevel++;
            DrawProp(materialEditor, properties, "_NormalTilingA", "Tiling");
            DrawProp(materialEditor, properties, "_NormalScrollA", "Scroll Speed");
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Layer B", EditorStyles.miniLabel);
            EditorGUI.indentLevel++;
            DrawProp(materialEditor, properties, "_NormalTilingB", "Tiling");
            DrawProp(materialEditor, properties, "_NormalScrollB", "Scroll Speed");
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
            DrawProp(materialEditor, properties, "_NormalStrength", "Strength");
        });

        // ━━ Refraction ━━
        _foldRefraction = DrawSection("Refraction", _foldRefraction, () =>
        {
            DrawProp(materialEditor, properties, "_RefractionStrength", "Strength");
        });

        // ━━ Surface Foam ━━
        _foldSurfaceFoam = DrawSection("Surface Foam", _foldSurfaceFoam, () =>
        {
            DrawTextureSingleLine(materialEditor, properties, "_SurfaceFoamTexture", "Foam Texture");
            DrawProp(materialEditor, properties, "_SurfaceFoamTiling", "Tiling");
            DrawProp(materialEditor, properties, "_SurfaceFoamScroll", "Scroll Speed");
            DrawProp(materialEditor, properties, "_SurfaceFoamCutoff", "Cutoff");
            DrawProp(materialEditor, properties, "_FoamDistortion", "Normal Distortion");
        });

        // ━━ Intersection Foam ━━
        _foldIntersection = DrawSection("Intersection Foam", _foldIntersection, () =>
        {
            DrawProp(materialEditor, properties, "_FoamColor", "Foam Color");
            DrawProp(materialEditor, properties, "_FoamIntersectionDepth", "Intersection Depth");
            DrawProp(materialEditor, properties, "_FoamEdgeSmoothness", "Edge Smoothness");
            DrawHelpBox("0 = hard toon edge  ·  1 = soft gradient");
        });

        // ━━ Bling / Specular ━━
        _foldBling = DrawSection("Specular Bling", _foldBling, () =>
        {
            DrawProp(materialEditor, properties, "_BlingColor", "Bling Color (HDR)");
            DrawProp(materialEditor, properties, "_BlingGloss", "Gloss");
            DrawProp(materialEditor, properties, "_BlingThreshold", "Threshold");
            DrawProp(materialEditor, properties, "_BlingIntensity", "Intensity");
        });

        // ━━ Waves ━━
        _foldWaves = DrawSection("Vertex Waves", _foldWaves, () =>
        {
            DrawProp(materialEditor, properties, "_WaveAmplitude", "Amplitude");
            DrawProp(materialEditor, properties, "_WaveFrequency", "Frequency");
            DrawProp(materialEditor, properties, "_WaveSpeed", "Speed");
        });

        EditorGUILayout.Space(6);

        // ── Cull mode dropdown ──
        MaterialProperty cullProp = FindProperty("_Cull", properties, false);
        if (cullProp != null)
        {
            EditorGUI.BeginChangeCheck();
            var cullMode = (UnityEngine.Rendering.CullMode)cullProp.floatValue;
            cullMode = (UnityEngine.Rendering.CullMode)EditorGUILayout.EnumPopup("Cull Mode", cullMode);
            if (EditorGUI.EndChangeCheck())
                cullProp.floatValue = (float)cullMode;
        }

        materialEditor.RenderQueueField();
    }

    // ════════════════════════════════════════════════════════════════
    // Drawing Helpers
    // ════════════════════════════════════════════════════════════════

    static bool DrawSection(string title, bool foldout, System.Action drawContent)
    {
        EditorGUILayout.Space(2);
        Rect headerRect = GUILayoutUtility.GetRect(1f, 22f, GUILayout.ExpandWidth(true));

        Color bgCol = foldout ? new Color(0.25f, 0.25f, 0.25f, 0.6f) : new Color(0.2f, 0.2f, 0.2f, 0.3f);
        EditorGUI.DrawRect(headerRect, bgCol);

        Rect accentRect = new Rect(headerRect.x, headerRect.y, 3f, headerRect.height);
        EditorGUI.DrawRect(accentRect, AccentWater);

        Event e = Event.current;
        if (e.type == EventType.MouseDown && headerRect.Contains(e.mousePosition))
        {
            foldout = !foldout;
            e.Use();
        }

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