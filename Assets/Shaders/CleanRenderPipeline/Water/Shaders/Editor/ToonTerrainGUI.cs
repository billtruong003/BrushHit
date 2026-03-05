using UnityEditor;
using UnityEngine;

public class ToonTerrainGUI : ShaderGUI
{
    // ── Foldout states ──
    static bool _foldLayers = true;
    static bool _foldSplatMap = true;
    static bool _foldHoleMap = false;
    static bool _foldHeightBlend = true;
    static bool _foldTriplanar = false;
    static bool _foldTexScale = false;
    static bool _foldCelShading = true;
    static bool _foldShadowRendering = true;

    // ── Styles ──
    static GUIStyle _headerStyle;
    static GUIStyle _sectionBox;
    static bool _stylesInit;

    static readonly Color AccentTerrain = new Color(0.45f, 0.75f, 0.35f, 1f);
    static readonly Color AccentSplat = new Color(0.55f, 0.65f, 0.95f, 1f);
    static readonly Color AccentHole = new Color(0.95f, 0.55f, 0.45f, 1f);
    static readonly Color WarnYellow = new Color(1f, 0.85f, 0.3f, 1f);
    static readonly Color GoodGreen = new Color(0.4f, 0.9f, 0.4f, 1f);

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
        DrawBanner("TOON TERRAIN", AccentTerrain);
        EditorGUILayout.Space(4);

        // ━━ Terrain Layers ━━
        _foldLayers = DrawSection("Terrain Layers", _foldLayers, AccentTerrain, () =>
        {
            DrawLayerRow(materialEditor, properties, "_Layer0", "_Layer0Color", "Layer 0 — Low Ground");
            EditorGUILayout.Space(4);
            DrawLayerRow(materialEditor, properties, "_Layer1", "_Layer1Color", "Layer 1 — Mid Ground");
            EditorGUILayout.Space(4);
            DrawLayerRow(materialEditor, properties, "_Layer2", "_Layer2Color", "Layer 2 — High / Snow");
            EditorGUILayout.Space(4);
            DrawLayerRow(materialEditor, properties, "_Layer3", "_Layer3Color", "Layer 3 — Cliff (Triplanar)");
        });

        // ━━ Splat Map ━━
        _foldSplatMap = DrawSection("Splat Map", _foldSplatMap, AccentSplat, () =>
        {
            DrawSplatMapSection(materialEditor, properties, mat);
        });

        // ━━ Hole Map ━━
        _foldHoleMap = DrawSection("Hole Map", _foldHoleMap, AccentHole, () =>
        {
            DrawHoleMapSection(materialEditor, properties, mat);
        });

        // ━━ Height Blending ━━
        _foldHeightBlend = DrawSection("Height Blending", _foldHeightBlend, AccentTerrain, () =>
        {
            DrawProp(materialEditor, properties, "_HeightLow", "Low → Mid Height");
            DrawProp(materialEditor, properties, "_HeightMid", "Mid → High Height");
            DrawProp(materialEditor, properties, "_BlendSharpness", "Blend Sharpness");
            DrawProp(materialEditor, properties, "_HeightOffset", "Height Offset");
            DrawHelpBox("Adjusts the Y threshold for layer transitions");

            if (mat.IsKeywordEnabled("_USE_SPLATMAP"))
            {
                EditorGUILayout.Space(2);
                DrawInfoBox("Splat Map is active — height weights are blended with splat weights", AccentSplat);
            }
        });

        // ━━ Triplanar Cliff ━━
        _foldTriplanar = DrawSection("Triplanar Cliff", _foldTriplanar, AccentTerrain, () =>
        {
            DrawProp(materialEditor, properties, "_TriplanarScale", "Triplanar Scale");
            DrawProp(materialEditor, properties, "_TriplanarSharpness", "Blend Sharpness");
            DrawProp(materialEditor, properties, "_CliffAngle", "Cliff Angle Threshold");
            DrawHelpBox("Lower = more cliff coverage  ·  Higher = steeper slopes only");

            if (mat.IsKeywordEnabled("_USE_SPLATMAP"))
            {
                EditorGUILayout.Space(2);
                DrawInfoBox("Splat A channel can paint additional cliff areas", AccentSplat);
            }
        });

        // ━━ Texture Scale ━━
        _foldTexScale = DrawSection("Texture Scale", _foldTexScale, AccentTerrain, () =>
        {
            DrawProp(materialEditor, properties, "_TexScale", "Global Texture Scale");
            DrawHelpBox("Scales UV for layers 0–2 (world XZ projection)");
        });

        // ━━ Cel Shading ━━
        _foldCelShading = DrawSection("Cel Shading", _foldCelShading, AccentTerrain, () =>
        {
            DrawProp(materialEditor, properties, "_ShadowColor", "Shadow Color");
            DrawProp(materialEditor, properties, "_Threshold", "Shadow Threshold");
            DrawProp(materialEditor, properties, "_Smoothness", "Shadow Smoothness");
        });

        // ━━ Shadow & Rendering ━━
        _foldShadowRendering = DrawSection("Shadow & Rendering", _foldShadowRendering, AccentTerrain, () =>
        {
            DrawShadowStatus(materialEditor);
        });

        EditorGUILayout.Space(6);
        materialEditor.RenderQueueField();

        // ── Sync keywords on every repaint (catches undo, multi-edit, etc.) ──
        foreach (var obj in materialEditor.targets)
        {
            SyncHoleKeywords((Material)obj);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Keyword Sync — Bridge _USE_HOLEMAP → _ALPHATEST_ON for baker
    //
    // Unity's Progressive Lightmapper (CPU mode) does NOT execute
    // ShadowCaster clip() during ray tracing. It only respects the
    // standard _ALPHATEST_ON + _Cutoff pipeline.
    // Without this bridge, baked shadows treat terrain as fully
    // solid over hole regions → no light reaches caves below.
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by Unity when material is first assigned or shader changes.
    /// </summary>
    public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
    {
        base.AssignNewShaderToMaterial(material, oldShader, newShader);
        SyncHoleKeywords(material);
    }

    /// <summary>
    /// Called by Unity when a material property changes via script or undo.
    /// </summary>
    public override void ValidateMaterial(Material material)
    {
        base.ValidateMaterial(material);
        SyncHoleKeywords(material);
    }

    static void SyncHoleKeywords(Material material)
    {
        bool useHoleMap = material.IsKeywordEnabled("_USE_HOLEMAP");

        if (useHoleMap)
        {
            // Sync _Cutoff to match _HoleThreshold — baker reads _Cutoff by name
            if (material.HasProperty("_HoleThreshold") && material.HasProperty("_Cutoff"))
            {
                material.SetFloat("_Cutoff", material.GetFloat("_HoleThreshold"));
            }

            // Tag as TransparentCutout — baker uses RenderType for internal classification
            material.SetOverrideTag("RenderType", "TransparentCutout");

            // _ALPHATEST_ON is only needed for the lightmap baker.
            // At runtime _USE_HOLEMAP already handles clip() in every pass.
            // Forcing _ALPHATEST_ON at runtime wastes Early-Z on some GPUs.
            //
            // Strategy: enable in editor (baker needs it), strip at play time.
            if (!Application.isPlaying)
            {
                material.EnableKeyword("_ALPHATEST_ON");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            }
            else
            {
                material.DisableKeyword("_ALPHATEST_ON");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry - 100;
            }
        }
        else
        {
            material.DisableKeyword("_ALPHATEST_ON");
            material.SetOverrideTag("RenderType", "Opaque");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry - 100;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Splat Map Section
    // ════════════════════════════════════════════════════════════════

    static void DrawSplatMapSection(MaterialEditor materialEditor, MaterialProperty[] properties, Material mat)
    {
        MaterialProperty useSplat = FindProperty("_UseSplatMap", properties, false);
        if (useSplat != null)
        {
            EditorGUI.BeginChangeCheck();
            materialEditor.ShaderProperty(useSplat, "Enable Splat Map");
            if (EditorGUI.EndChangeCheck())
            {
                if (useSplat.floatValue > 0.5f)
                    mat.EnableKeyword("_USE_SPLATMAP");
                else
                    mat.DisableKeyword("_USE_SPLATMAP");
            }
        }

        bool splatEnabled = mat.IsKeywordEnabled("_USE_SPLATMAP");

        if (splatEnabled)
        {
            EditorGUILayout.Space(4);

            MaterialProperty splatTex = FindProperty("_SplatMap", properties, false);
            if (splatTex != null)
            {
                materialEditor.TexturePropertySingleLine(new GUIContent("Splat Texture (RGBA)"), splatTex);
                materialEditor.TextureScaleOffsetProperty(splatTex);
            }

            EditorGUILayout.Space(4);
            DrawProp(materialEditor, properties, "_SplatInfluence", "Splat Influence");
            DrawProp(materialEditor, properties, "_SplatSharpness", "Splat Blend Sharpness");

            EditorGUILayout.Space(4);

            EditorGUILayout.BeginVertical(_sectionBox);
            EditorGUILayout.LabelField("Channel Mapping", EditorStyles.miniLabel);
            DrawChannelRow("R", "Layer 0 — Low Ground", new Color(1f, 0.3f, 0.3f));
            DrawChannelRow("G", "Layer 1 — Mid Ground", new Color(0.3f, 1f, 0.3f));
            DrawChannelRow("B", "Layer 2 — High / Snow", new Color(0.3f, 0.5f, 1f));
            DrawChannelRow("A", "Layer 3 — Cliff Override", new Color(0.8f, 0.8f, 0.8f));
            EditorGUILayout.EndVertical();

            DrawHelpBox("Splat Influence: 0 = pure height-based · 1 = pure splat-based");

            if (splatTex != null && splatTex.textureValue == null)
            {
                EditorGUILayout.Space(2);
                DrawInfoBox("⚠ No splat texture assigned — using uniform white (equal weights)", WarnYellow);
            }
        }
        else
        {
            DrawHelpBox("Enable to paint terrain layers with an RGBA splat texture");
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Hole Map Section
    // ════════════════════════════════════════════════════════════════

    static void DrawHoleMapSection(MaterialEditor materialEditor, MaterialProperty[] properties, Material mat)
    {
        MaterialProperty useHole = FindProperty("_UseHoleMap", properties, false);
        if (useHole != null)
        {
            EditorGUI.BeginChangeCheck();
            materialEditor.ShaderProperty(useHole, "Enable Hole Map");
            if (EditorGUI.EndChangeCheck())
            {
                // Toggle _USE_HOLEMAP keyword
                if (useHole.floatValue > 0.5f)
                    mat.EnableKeyword("_USE_HOLEMAP");
                else
                    mat.DisableKeyword("_USE_HOLEMAP");

                // Immediately sync baker keywords
                SyncHoleKeywords(mat);
            }
        }

        bool holeEnabled = mat.IsKeywordEnabled("_USE_HOLEMAP");

        if (holeEnabled)
        {
            EditorGUILayout.Space(4);

            MaterialProperty holeTex = FindProperty("_HoleMap", properties, false);
            if (holeTex != null)
            {
                materialEditor.TexturePropertySingleLine(new GUIContent("Hole Texture (R channel)"), holeTex);
                materialEditor.TextureScaleOffsetProperty(holeTex);
            }

            EditorGUILayout.Space(4);

            // When threshold changes, also sync _Cutoff for baker
            EditorGUI.BeginChangeCheck();
            DrawProp(materialEditor, properties, "_HoleThreshold", "Hole Threshold");
            if (EditorGUI.EndChangeCheck())
            {
                SyncHoleKeywords(mat);
            }

            DrawProp(materialEditor, properties, "_HoleEdgeSoftness", "Edge Softness");

            EditorGUILayout.Space(4);
            DrawHelpBox("Black (0) = hole · White (1) = solid terrain");
            DrawHelpBox("Holes are respected in all passes: lit, shadows, depth, bake");

            // Baker status indicator
            EditorGUILayout.Space(2);
            bool bakerReady = mat.IsKeywordEnabled("_ALPHATEST_ON");
            if (bakerReady)
            {
                DrawInfoBox("✓ Baker alpha test active — lightmap will respect holes", GoodGreen);
            }
            else
            {
                DrawInfoBox("⚠ Baker keywords out of sync — re-toggle Hole Map to fix", WarnYellow);
            }

            if (holeTex != null && holeTex.textureValue == null)
            {
                EditorGUILayout.Space(2);
                DrawInfoBox("⚠ No hole texture assigned — terrain is fully solid", WarnYellow);
            }
        }
        else
        {
            DrawHelpBox("Enable to cut holes in terrain using a mask texture");
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Shadow Status — live diagnostic of shadow pipeline
    // ════════════════════════════════════════════════════════════════

    static void DrawShadowStatus(MaterialEditor materialEditor)
    {
        Material mat = materialEditor.target as Material;
        bool hasShadowPass = mat.FindPass("ShadowCaster") >= 0;
        bool hasDepthNormals = mat.FindPass("DepthNormals") >= 0;
        bool hasMetaPass = mat.FindPass("Meta") >= 0;

        DrawStatusRow("ShadowCaster Pass", hasShadowPass, "Terrain can cast shadows");
        DrawStatusRow("DepthNormals Pass", hasDepthNormals, "Required for SSAO / screen-space shadows");
        DrawStatusRow("Meta Pass", hasMetaPass, "Required for lightmap baking (GI bounce)");

        bool holeEnabled = mat.IsKeywordEnabled("_USE_HOLEMAP");
        if (holeEnabled)
        {
            DrawStatusRow("Hole Map in Shadows", true, "Shadows respect hole cutouts");

            bool alphaTestOn = mat.IsKeywordEnabled("_ALPHATEST_ON");
            DrawStatusRow("Baker Alpha Test", alphaTestOn,
                alphaTestOn ? "Lightmap baker will respect holes" : "⚠ _ALPHATEST_ON not set — bake may ignore holes");
        }

        EditorGUILayout.Space(4);

        EditorGUILayout.LabelField("Shadow Receive Keywords", EditorStyles.miniLabel);
        EditorGUI.indentLevel++;
        DrawHelpBox("Variants compiled: Cascade + Screen + Soft");
        DrawHelpBox("Keywords are activated by URP at runtime");
        EditorGUI.indentLevel--;

        EditorGUILayout.Space(4);

        // ── Check scene light ──
        var mainLight = FindMainDirectionalLight();
        if (mainLight != null)
        {
            bool lightCastsShadow = mainLight.shadows != LightShadows.None;
            DrawStatusRow("Directional Light Shadows", lightCastsShadow,
                lightCastsShadow ? $"Type: {mainLight.shadows}" : "Enable shadows on your Directional Light!");

            if (!lightCastsShadow)
            {
                if (GUILayout.Button("Fix: Enable Soft Shadows on Main Light", EditorStyles.miniButton))
                {
                    Undo.RecordObject(mainLight, "Enable Light Shadows");
                    mainLight.shadows = LightShadows.Soft;
                    EditorUtility.SetDirty(mainLight);
                }
            }
        }
        else
        {
            DrawStatusRow("Directional Light", false, "No directional light found in scene!");
        }

        // ── Check URP asset ──
        var urpAsset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
        if (urpAsset != null)
        {
            var so = new SerializedObject(urpAsset);
            var shadowProp = so.FindProperty("m_MainLightShadowsSupported");

            if (shadowProp != null)
            {
                bool urpShadows = shadowProp.boolValue;
                DrawStatusRow("URP Shadow Support", urpShadows,
                    urpShadows ? "Main light shadows enabled in URP Asset" : "Enable in URP Asset → Shadows");

                if (!urpShadows)
                {
                    if (GUILayout.Button("Fix: Enable Shadows in URP Asset", EditorStyles.miniButton))
                    {
                        shadowProp.boolValue = true;
                        so.ApplyModifiedProperties();
                    }
                }

                var shadowDistProp = so.FindProperty("m_ShadowDistance");
                if (shadowDistProp != null)
                {
                    float dist = shadowDistProp.floatValue;
                    DrawStatusRow("Shadow Distance", dist > 0, $"{dist:F0}m");
                }
            }

            so.Dispose();
        }

        EditorGUILayout.Space(4);

        // ── Check renderers ──
        EditorGUILayout.LabelField("Renderer Check", EditorStyles.miniLabel);
        EditorGUI.indentLevel++;

        int rendererCount = 0;
        int castShadowCount = 0;
        var renderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

        foreach (var r in renderers)
        {
            if (r.sharedMaterials == null) continue;
            foreach (var m in r.sharedMaterials)
            {
                if (m == mat)
                {
                    rendererCount++;
                    if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                        castShadowCount++;
                    break;
                }
            }
        }

        DrawStatusRow($"Renderers using this material", rendererCount > 0, $"{rendererCount} found");
        DrawStatusRow($"Cast Shadows enabled", castShadowCount > 0, $"{castShadowCount}/{rendererCount} renderers");

        if (castShadowCount < rendererCount && rendererCount > 0)
        {
            if (GUILayout.Button($"Fix: Enable Cast Shadows on all {rendererCount} renderers", EditorStyles.miniButton))
            {
                foreach (var r in renderers)
                {
                    if (r.sharedMaterials == null) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == mat)
                        {
                            Undo.RecordObject(r, "Enable Cast Shadows");
                            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                            EditorUtility.SetDirty(r);
                            break;
                        }
                    }
                }
            }
        }

        EditorGUI.indentLevel--;
    }

    // ════════════════════════════════════════════════════════════════
    // Layer Row: texture + tint on same line
    // ════════════════════════════════════════════════════════════════

    static void DrawLayerRow(MaterialEditor editor, MaterialProperty[] props,
        string texName, string colorName, string label)
    {
        MaterialProperty tex = FindProperty(texName, props, false);
        MaterialProperty col = FindProperty(colorName, props, false);

        if (tex != null && col != null)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
            editor.TexturePropertySingleLine(new GUIContent("Texture"), tex, col);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Drawing Helpers
    // ════════════════════════════════════════════════════════════════

    static bool DrawSection(string title, bool foldout, Color accentColor, System.Action drawContent)
    {
        EditorGUILayout.Space(2);
        Rect headerRect = GUILayoutUtility.GetRect(1f, 22f, GUILayout.ExpandWidth(true));

        Color bgCol = foldout ? new Color(0.25f, 0.25f, 0.25f, 0.6f) : new Color(0.2f, 0.2f, 0.2f, 0.3f);
        EditorGUI.DrawRect(headerRect, bgCol);

        Rect accentRect = new Rect(headerRect.x, headerRect.y, 3f, headerRect.height);
        EditorGUI.DrawRect(accentRect, accentColor);

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

    static void DrawHelpBox(string msg)
    {
        EditorGUILayout.LabelField(msg, EditorStyles.centeredGreyMiniLabel);
    }

    static void DrawInfoBox(string msg, Color color)
    {
        GUIStyle style = new GUIStyle(EditorStyles.helpBox)
        {
            richText = true,
            fontSize = 10,
            normal = { textColor = color }
        };
        EditorGUILayout.LabelField(msg, style);
    }

    static void DrawChannelRow(string channel, string layerName, Color channelColor)
    {
        EditorGUILayout.BeginHorizontal();
        GUIStyle channelStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            normal = { textColor = channelColor },
            fixedWidth = 20
        };
        EditorGUILayout.LabelField(channel, channelStyle, GUILayout.Width(20));
        EditorGUILayout.LabelField("→ " + layerName, EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();
    }

    static void DrawStatusRow(string label, bool ok, string detail)
    {
        EditorGUILayout.BeginHorizontal();

        Color iconColor = ok ? GoodGreen : WarnYellow;
        string icon = ok ? "✓" : "⚠";
        GUIStyle iconStyle = new GUIStyle(EditorStyles.label)
        {
            normal = { textColor = iconColor },
            fontStyle = FontStyle.Bold,
            fixedWidth = 20
        };
        EditorGUILayout.LabelField(icon, iconStyle, GUILayout.Width(20));
        EditorGUILayout.LabelField(label, GUILayout.Width(180));

        GUIStyle detailStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            normal = { textColor = ok ? Color.gray : WarnYellow }
        };
        EditorGUILayout.LabelField(detail, detailStyle);

        EditorGUILayout.EndHorizontal();
    }

    static Light FindMainDirectionalLight()
    {
        var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var light in lights)
        {
            if (light.type == LightType.Directional)
                return light;
        }
        return null;
    }
}
