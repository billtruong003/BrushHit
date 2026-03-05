using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// BakeryLightConverter — Batch convert Unity lights to Bakery lights.
///
/// Scans scene for all Light components, adds corresponding Bakery
/// light components, copies settings, and optionally disables the
/// Unity Light to avoid double brightness.
///
/// Menu: Tools → CleanRender → Convert to Bakery Lights
/// </summary>
public class BakeryLightConverter : EditorWindow
{
    // ── Settings ──
    bool disableUnityLights = true;
    bool createSkylight = true;
    bool skipExisting = true;
    bool showPreview = true;
    Color skylightColor = new Color(0.6f, 0.7f, 1f, 1f);
    float skylightIntensity = 1f;

    // ── Preview data ──
    List<LightInfo> foundLights = new List<LightInfo>();
    bool scanned = false;
    Vector2 scrollPos;

    // ── Stats ──
    int convertedDirect = 0;
    int convertedPoint = 0;
    int convertedSpot = 0;
    int skippedCount = 0;
    bool converted = false;

    struct LightInfo
    {
        public Light light;
        public string bakeryType;
        public bool hasBakeryAlready;
        public bool willConvert;
    }

    [MenuItem("Tools/CleanRender/Convert to Bakery Lights")]
    static void Open()
    {
        var win = GetWindow<BakeryLightConverter>("Bakery Light Converter");
        win.minSize = new Vector2(420, 480);
    }

    void OnEnable()
    {
        ScanScene();
    }

    void OnGUI()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Bakery Light Converter", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Converts all Unity Light components to Bakery equivalents.\n" +
            "Directional → Bakery Direct Light\n" +
            "Point → Bakery Point Light\n" +
            "Spot → Bakery Point Light (Projected)\n" +
            "Also creates a Bakery Skylight for ambient.",
            MessageType.Info);

        EditorGUILayout.Space(8);

        // ── Settings ──
        disableUnityLights = EditorGUILayout.Toggle(
            new GUIContent("Disable Unity Lights",
                "Disable original Unity Light components to prevent double brightness"),
            disableUnityLights);

        createSkylight = EditorGUILayout.Toggle(
            new GUIContent("Create Skylight",
                "Create a Bakery Skylight for ambient/sky bounce"),
            createSkylight);

        if (createSkylight)
        {
            EditorGUI.indentLevel++;
            skylightColor = EditorGUILayout.ColorField("Skylight Color", skylightColor);
            skylightIntensity = EditorGUILayout.FloatField("Skylight Intensity", skylightIntensity);
            EditorGUI.indentLevel--;
        }

        skipExisting = EditorGUILayout.Toggle(
            new GUIContent("Skip Already Converted",
                "Skip lights that already have a Bakery component"),
            skipExisting);

        EditorGUILayout.Space(8);

        // ── Scan ──
        if (GUILayout.Button("Scan Scene", GUILayout.Height(24)))
        {
            ScanScene();
        }

        // ── Preview ──
        if (scanned && foundLights.Count > 0)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"Found {foundLights.Count} lights:", EditorStyles.boldLabel);

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.Height(200));

            foreach (var info in foundLights)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                // Status icon
                if (info.hasBakeryAlready && skipExisting)
                {
                    GUIStyle skipStyle = new GUIStyle(EditorStyles.label)
                    { normal = { textColor = Color.gray } };
                    EditorGUILayout.LabelField("—", skipStyle, GUILayout.Width(16));
                }
                else
                {
                    GUIStyle okStyle = new GUIStyle(EditorStyles.label)
                    { normal = { textColor = new Color(0.4f, 0.9f, 0.4f) } };
                    EditorGUILayout.LabelField("→", okStyle, GUILayout.Width(16));
                }

                // Light name
                if (info.light != null)
                {
                    EditorGUILayout.ObjectField(info.light.gameObject, typeof(GameObject), true,
                        GUILayout.Width(160));
                }

                // Type
                EditorGUILayout.LabelField(info.bakeryType, GUILayout.Width(120));

                // Already has bakery?
                if (info.hasBakeryAlready)
                {
                    GUIStyle existStyle = new GUIStyle(EditorStyles.miniLabel)
                    { normal = { textColor = new Color(1f, 0.85f, 0.3f) } };
                    EditorGUILayout.LabelField("(has Bakery)", existStyle, GUILayout.Width(80));
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }
        else if (scanned)
        {
            EditorGUILayout.HelpBox("No lights found in scene.", MessageType.Warning);
        }

        EditorGUILayout.Space(8);

        // ── Convert button ──
        int toConvert = 0;
        foreach (var info in foundLights)
        {
            if (!info.hasBakeryAlready || !skipExisting) toConvert++;
        }

        GUI.enabled = toConvert > 0 && HasBakeryInstalled();

        if (GUILayout.Button($"Convert {toConvert} Lights", GUILayout.Height(32)))
        {
            ConvertAll();
        }

        GUI.enabled = true;

        if (!HasBakeryInstalled())
        {
            EditorGUILayout.HelpBox(
                "Bakery components not found in project.\n" +
                "Install Bakery GPU Lightmapper from Asset Store first.",
                MessageType.Error);
        }

        // ── Results ──
        if (converted)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                $"Converted:\n" +
                $"  Directional → Bakery Direct Light: {convertedDirect}\n" +
                $"  Point → Bakery Point Light: {convertedPoint}\n" +
                $"  Spot → Bakery Point Light (Projected): {convertedSpot}\n" +
                $"  Skipped (already had Bakery): {skippedCount}\n" +
                (createSkylight ? "  Skylight: created" : ""),
                MessageType.Info);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Scene Scan
    // ════════════════════════════════════════════════════════════════

    void ScanScene()
    {
        foundLights.Clear();
        scanned = true;
        converted = false;

        var lights = Object.FindObjectsByType<Light>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (var light in lights)
        {
            var info = new LightInfo
            {
                light = light,
                hasBakeryAlready = false,
                willConvert = true,
            };

            switch (light.type)
            {
                case LightType.Directional:
                    info.bakeryType = "→ Bakery Direct Light";
                    info.hasBakeryAlready = HasComponent(light.gameObject, "BakeryDirectLight");
                    break;

                case LightType.Point:
                    info.bakeryType = "→ Bakery Point Light";
                    info.hasBakeryAlready = HasComponent(light.gameObject, "BakeryPointLight");
                    break;

                case LightType.Spot:
                    info.bakeryType = "→ Bakery Point (Projected)";
                    info.hasBakeryAlready = HasComponent(light.gameObject, "BakeryPointLight");
                    break;
                case LightType.Rectangle:
                    info.bakeryType = "→ Bakery Light Mesh";
                    info.hasBakeryAlready = HasComponent(light.gameObject, "BakeryLightMesh");
                    break;

                default:
                    info.bakeryType = "(unsupported type)";
                    info.willConvert = false;
                    break;
            }

            foundLights.Add(info);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Convert All
    // ════════════════════════════════════════════════════════════════

    void ConvertAll()
    {
        convertedDirect = 0;
        convertedPoint = 0;
        convertedSpot = 0;
        skippedCount = 0;

        Undo.SetCurrentGroupName("Convert to Bakery Lights");
        int undoGroup = Undo.GetCurrentGroup();

        foreach (var info in foundLights)
        {
            if (info.light == null) continue;
            if (!info.willConvert) continue;

            if (info.hasBakeryAlready && skipExisting)
            {
                skippedCount++;
                continue;
            }

            switch (info.light.type)
            {
                case LightType.Directional:
                    ConvertDirectional(info.light);
                    convertedDirect++;
                    break;

                case LightType.Point:
                    ConvertPoint(info.light, false);
                    convertedPoint++;
                    break;

                case LightType.Spot:
                    ConvertPoint(info.light, true);
                    convertedSpot++;
                    break;
                case LightType.Rectangle:
                    ConvertArea(info.light);
                    break;
            }

            // Disable Unity Light
            if (disableUnityLights)
            {
                Undo.RecordObject(info.light, "Disable Unity Light");
                info.light.enabled = false;
            }
        }

        // Create Skylight
        if (createSkylight && !SceneHasSkylight())
        {
            CreateBakerySkylight();
        }

        Undo.CollapseUndoOperations(undoGroup);
        converted = true;
        ScanScene(); // refresh preview
        converted = true; // re-set after scan clears it
    }

    // ════════════════════════════════════════════════════════════════
    // Individual Converters (using reflection for Bakery types)
    // ════════════════════════════════════════════════════════════════

    void ConvertDirectional(Light light)
    {
        var type = GetBakeryType("BakeryDirectLight");
        if (type == null) return;

        var comp = Undo.AddComponent(light.gameObject, type);

        // Copy settings via reflection
        SetField(comp, "color", light.color);
        SetField(comp, "intensity", light.intensity);
        SetField(comp, "shadowSpread", 0.05f); // nice soft shadow default
        SetField(comp, "samples", 16);

        // Try to copy shadow angle for soft shadows
        SetField(comp, "shadowAngle", light.shadowAngle > 0 ? light.shadowAngle : 1f);

        EditorUtility.SetDirty(comp);
    }

    void ConvertPoint(Light light, bool isSpot)
    {
        var type = GetBakeryType("BakeryPointLight");
        if (type == null) return;

        var comp = Undo.AddComponent(light.gameObject, type);

        SetField(comp, "color", light.color);
        SetField(comp, "intensity", light.intensity);
        SetField(comp, "range", light.range);
        SetField(comp, "samples", 8);

        if (isSpot)
        {
            SetField(comp, "projected", true);
            SetField(comp, "angle", light.spotAngle);
        }

        // Copy shadow settings
        bool castShadow = light.shadows != LightShadows.None;
        SetField(comp, "shadowmask", castShadow);

        EditorUtility.SetDirty(comp);
    }

    void ConvertArea(Light light)
    {
        var type = GetBakeryType("BakeryLightMesh");
        if (type == null) return;

        var comp = Undo.AddComponent(light.gameObject, type);

        SetField(comp, "color", light.color);
        SetField(comp, "intensity", light.intensity);
        SetField(comp, "samples", 16);

        EditorUtility.SetDirty(comp);
    }

    void CreateBakerySkylight()
    {
        var type = GetBakeryType("BakerySkyLight");
        if (type == null) return;

        var go = new GameObject("Bakery Skylight");
        Undo.RegisterCreatedObjectUndo(go, "Create Bakery Skylight");

        var comp = go.AddComponent(type);

        SetField(comp, "color", skylightColor);
        SetField(comp, "intensity", skylightIntensity);
        SetField(comp, "samples", 32);

        EditorUtility.SetDirty(comp);
        Selection.activeGameObject = go;
    }

    // ════════════════════════════════════════════════════════════════
    // Reflection Helpers (Bakery types resolved at runtime)
    // ════════════════════════════════════════════════════════════════

    static System.Type GetBakeryType(string typeName)
    {
        // Search all assemblies for Bakery type
        foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(typeName);
            if (type != null) return type;
        }
        return null;
    }

    static bool HasBakeryInstalled()
    {
        return GetBakeryType("BakeryDirectLight") != null;
    }

    static bool HasComponent(GameObject go, string typeName)
    {
        var type = GetBakeryType(typeName);
        if (type == null) return false;
        return go.GetComponent(type) != null;
    }

    static bool SceneHasSkylight()
    {
        var type = GetBakeryType("BakerySkyLight");
        if (type == null) return false;
        return Object.FindAnyObjectByType(type) != null;
    }

    static void SetField(Component comp, string fieldName, object value)
    {
        if (comp == null) return;
        var type = comp.GetType();

        // Try field first
        var field = type.GetField(fieldName,
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);

        if (field != null)
        {
            try { field.SetValue(comp, System.Convert.ChangeType(value, field.FieldType)); }
            catch { /* type mismatch, skip */ }
            return;
        }

        // Try property
        var prop = type.GetProperty(fieldName,
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);

        if (prop != null && prop.CanWrite)
        {
            try { prop.SetValue(comp, System.Convert.ChangeType(value, prop.PropertyType)); }
            catch { /* type mismatch, skip */ }
        }
    }
}
