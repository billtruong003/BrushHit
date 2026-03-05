using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace HybridShaderOptimizer.Editor
{
    public enum ShaderCategory
    {
        SimpleLit, Lit, Unlit, ToonLit, ToonFoliage, ToonGrass, ToonTerrain,
        ToonWater, ToonLava, ToonMetal, Skybox, TextShader, Unknown
    }

    [Serializable]
    public class MaterialConvertEntry
    {
        public Material Material;
        public string OriginalShaderName;
        public ShaderCategory Category;
        public string TargetShaderPath;
        public bool Selected;
        public bool Converted;
        public string Notes;
        public bool IsAutoConvertible => Category == ShaderCategory.SimpleLit || Category == ShaderCategory.Lit || Category == ShaderCategory.Unlit;
    }[Serializable]
    public class DuplicateMaterialGroup
    {
        public string Fingerprint;
        public Material MasterMaterial;
        public List<Material> Duplicates = new List<Material>();
        public bool IsExpanded;
    }

    public static class ShaderPropertyMap
    {
        public static readonly (string src, string dst)[] TextureMap = {
            ("_BaseMap", "_BaseMap"), ("_MainTex", "_BaseMap"), ("_BumpMap", "_BumpMap"),
            ("_NormalMap", "_BumpMap"), ("_EmissionMap", "_EmissionMap"), ("_OcclusionMap", "_OcclusionMap"),
            ("_SpecGlossMap", "_SpecGlossMap"), ("_MetallicGlossMap", "_SpecGlossMap")
        };

        public static readonly (string src, string dst)[] ColorMap = {
            ("_BaseColor", "_BaseColor"), ("_Color", "_BaseColor"),
            ("_EmissionColor", "_EmissionColor"), ("_SpecColor", "_SpecColor")
        };

        public static readonly (string src, string dst)[] FloatMap = {
            ("_Cutoff", "_Cutoff"), ("_AlphaClip", "_AlphaClip"), ("_BumpScale", "_BumpScale"),
            ("_Surface", "_Surface"), ("_Blend", "_Blend"), ("_Cull", "_Cull"),
            ("_ZWrite", "_ZWrite"), ("_SrcBlend", "_SrcBlend"), ("_DstBlend", "_DstBlend"),
            ("_Smoothness", "_Smoothness")
        };

        public static readonly (string prop, float value)[] ToonDefaults = {
            ("_ToonRamp", 0.5f), ("_ShadowStep", 0.5f), ("_ShadowSmooth", 0.05f),
            ("_SpecularSize", 0.1f), ("_RimPower", 3.0f)
        };
    }

    public static class ShaderCategorizer
    {
        private static readonly Dictionary<string, ShaderCategory> ExactMatch = new Dictionary<string, ShaderCategory>
        {
            { "Universal Render Pipeline/Simple Lit", ShaderCategory.SimpleLit },
            { "Universal Render Pipeline/Lit", ShaderCategory.Lit },
            { "Universal Render Pipeline/Unlit", ShaderCategory.Unlit }
        };

        private static readonly (string contains, ShaderCategory cat)[] PatternMatch = {
            ("SimpleLit", ShaderCategory.SimpleLit), ("Simple Lit", ShaderCategory.SimpleLit),
            ("ToonLit", ShaderCategory.ToonLit), ("Toon Lit", ShaderCategory.ToonLit),
            ("ToonFoliage", ShaderCategory.ToonFoliage), ("Toon Foliage", ShaderCategory.ToonFoliage),
            ("ToonGrass", ShaderCategory.ToonGrass), ("Toon Grass", ShaderCategory.ToonGrass),
            ("ToonTerrain", ShaderCategory.ToonTerrain), ("Toon Terrain", ShaderCategory.ToonTerrain),
            ("ToonWater", ShaderCategory.ToonWater), ("Toon Water", ShaderCategory.ToonWater),
            ("ToonLava", ShaderCategory.ToonLava), ("Toon Lava", ShaderCategory.ToonLava),
            ("ToonMetal", ShaderCategory.ToonMetal), ("Toon Metal", ShaderCategory.ToonMetal),
            ("Skybox", ShaderCategory.Skybox), ("SimpleText", ShaderCategory.TextShader),
            ("Unlit", ShaderCategory.Unlit)
        };

        public static ShaderCategory Categorize(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return ShaderCategory.Unknown;
            if (ExactMatch.TryGetValue(shaderName, out var exact)) return exact;
            foreach (var (pattern, cat) in PatternMatch)
            {
                if (shaderName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) return cat;
            }
            return ShaderCategory.Unknown;
        }

        public static string GetTargetShaderName(ShaderCategory category)
        {
            return category switch
            {
                ShaderCategory.SimpleLit or ShaderCategory.Lit or ShaderCategory.Unlit => "CleanRenderPipeline/ToonLit",
                _ => null
            };
        }

        public static Shader FindTargetShader(string shaderName)
        {
            Shader s = Shader.Find(shaderName);
            if (s != null) return s;
            string[] variations = { shaderName, "Shader Graphs/" + shaderName, "Custom/" + shaderName, shaderName.Replace("/", " / ") };
            foreach (string v in variations)
            {
                s = Shader.Find(v);
                if (s != null) return s;
            }
            string[] guids = AssetDatabase.FindAssets("t:Shader " + shaderName.Split('/').Last());
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Shader found = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (found != null && found.name.IndexOf("ToonLit", StringComparison.OrdinalIgnoreCase) >= 0) return found;
            }
            return null;
        }
    }

    [Serializable]
    public class FoliageAnalysisEntry
    {
        public GameObject GameObject;
        public Renderer Renderer;
        public Material Material;
        public string ShaderName;
        public bool HasShadowCasterPass;
        public bool HasAlphaClipInShadow;
        public bool ShadowsEnabled;
        public float AlphaCutoff;
        public bool IsStatic;
        public bool HasVertexAnimation;
        public bool CanStaticBatch;
        public bool UsesGPUInstancing;
    }

    public enum AuditSeverity { Critical, Warning, Info, Pass }[Serializable]
    public class AuditItem
    {
        public AuditSeverity Severity;
        public string Category;
        public string Title;
        public string Description;
        public string Fix;
    }

    public class VRToonOptimizationStudio : EditorWindow
    {
        private int currentTab = 0;
        private string[] tabs = { "Material Converter", "Duplicate Consolidator", "Foliage Optimizer", "Keyword Stripper", "VR Auditor" };

        private List<MaterialConvertEntry> convertEntries = new List<MaterialConvertEntry>();
        private List<DuplicateMaterialGroup> duplicateGroups = new List<DuplicateMaterialGroup>();
        private List<FoliageAnalysisEntry> foliageEntries = new List<FoliageAnalysisEntry>();
        private List<AuditItem> auditItems = new List<AuditItem>();
        private List<string> bloatShaders = new List<string>();

        private Vector2 scrollPosConverter;
        private Vector2 scrollPosConsolidator;
        private Vector2 scrollPosFoliage;
        private Vector2 scrollPosAudit;
        private Vector2 scrollPosStripper;

        [MenuItem("Tools/VR Toon Optimization Studio")]
        public static void ShowWindow()
        {
            GetWindow<VRToonOptimizationStudio>("VR Optimization Studio").minSize = new Vector2(900, 600);
        }

        private void OnGUI()
        {
            currentTab = GUILayout.Toolbar(currentTab, tabs);
            EditorGUILayout.Space();

            switch (currentTab)
            {
                case 0: DrawConverterTab(); break;
                case 1: DrawConsolidatorTab(); break;
                case 2: DrawFoliageTab(); break;
                case 3: DrawStripperTab(); break;
                case 4: DrawAuditorTab(); break;
            }
        }

        private void DrawConverterTab()
        {
            if (GUILayout.Button("Scan Materials in Project", GUILayout.Height(30))) convertEntries = ScanMaterials();
            if (convertEntries.Count == 0) return;
            if (GUILayout.Button("Batch Convert Selected to ToonLit", GUILayout.Height(30))) BatchConvertMaterials(convertEntries);

            scrollPosConverter = EditorGUILayout.BeginScrollView(scrollPosConverter);
            var grouped = convertEntries.GroupBy(x => x.Category).OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                EditorGUILayout.LabelField(group.Key.ToString(), EditorStyles.boldLabel);
                foreach (var entry in group)
                {
                    EditorGUILayout.BeginHorizontal("box");
                    if (entry.IsAutoConvertible) entry.Selected = EditorGUILayout.Toggle(entry.Selected, GUILayout.Width(20));
                    else GUILayout.Space(24);
                    
                    EditorGUILayout.ObjectField(entry.Material, typeof(Material), false, GUILayout.Width(200));
                    EditorGUILayout.LabelField(entry.OriginalShaderName, GUILayout.Width(250));
                    EditorGUILayout.LabelField(entry.Notes);
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawConsolidatorTab()
        {
            if (GUILayout.Button("Scan For Duplicate Materials", GUILayout.Height(30))) duplicateGroups = ScanDuplicateMaterials();
            if (duplicateGroups.Count == 0) return;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Consolidate All Renderers in Scene", GUILayout.Height(30))) ConsolidateAllMaterialsInScene();
            if (GUILayout.Button("Delete Unused Duplicates in Project", GUILayout.Height(30))) DeleteUnusedDuplicates();
            EditorGUILayout.EndHorizontal();

            scrollPosConsolidator = EditorGUILayout.BeginScrollView(scrollPosConsolidator);
            foreach (var group in duplicateGroups)
            {
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();
                group.IsExpanded = EditorGUILayout.Foldout(group.IsExpanded, $"Group: {group.MasterMaterial.name} ({group.Duplicates.Count} Duplicates)", true);
                EditorGUILayout.EndHorizontal();

                if (group.IsExpanded)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Master Material:", GUILayout.Width(100));
                    group.MasterMaterial = (Material)EditorGUILayout.ObjectField(group.MasterMaterial, typeof(Material), false);
                    EditorGUILayout.EndHorizontal();

                    EditorGUI.indentLevel++;
                    foreach (var dup in group.Duplicates)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("Duplicate:", GUILayout.Width(100));
                        EditorGUILayout.ObjectField(dup, typeof(Material), false);
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawFoliageTab()
        {
            if (GUILayout.Button("Scan Scene Foliage", GUILayout.Height(30))) foliageEntries = ScanFoliage();
            if (foliageEntries.Count == 0) return;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Force Ultimate VR Static Batching (Strip Anim & GPU Instancing)", GUILayout.Height(30))) ForceUltimateStaticBatching(foliageEntries);
            if (GUILayout.Button("Inject Missing ShadowCasters", GUILayout.Height(30))) InjectShadowCasters(foliageEntries);
            EditorGUILayout.EndHorizontal();

            scrollPosFoliage = EditorGUILayout.BeginScrollView(scrollPosFoliage);
            foreach (var entry in foliageEntries)
            {
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.ObjectField(entry.GameObject, typeof(GameObject), true, GUILayout.Width(200));
                EditorGUILayout.ObjectField(entry.Material, typeof(Material), false, GUILayout.Width(200));
                EditorGUILayout.LabelField(entry.ShaderName);
                EditorGUILayout.EndHorizontal();
                
                string shadowStat = entry.HasShadowCasterPass ? (entry.HasAlphaClipInShadow ? "Perfect Shadow" : "Shadow Missing Clip") : "NO SHADOW CASTER";
                string batchStat = entry.HasVertexAnimation ? "Vertex Anim Active" : "Static Ready";
                
                EditorGUILayout.LabelField($"Shadow: {shadowStat} | Batching: {batchStat} | Static: {entry.IsStatic}");
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawStripperTab()
        {
            if (GUILayout.Button("Scan Shader Bloat (multi_compile)", GUILayout.Height(30))) bloatShaders = ScanBloatedShaders();
            if (bloatShaders.Count == 0) return;

            if (GUILayout.Button("Optimize Keywords (multi_compile -> shader_feature_local)", GUILayout.Height(30)))
            {
                OptimizeShaderKeywords(bloatShaders);
                bloatShaders = ScanBloatedShaders();
            }

            scrollPosStripper = EditorGUILayout.BeginScrollView(scrollPosStripper);
            foreach (var path in bloatShaders)
            {
                EditorGUILayout.LabelField(path);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawAuditorTab()
        {
            if (GUILayout.Button("Run VR Performance Audit", GUILayout.Height(30))) auditItems = RunVRAudit();

            scrollPosAudit = EditorGUILayout.BeginScrollView(scrollPosAudit);
            foreach (var item in auditItems)
            {
                GUI.color = item.Severity == AuditSeverity.Critical ? Color.red : item.Severity == AuditSeverity.Warning ? Color.yellow : Color.white;
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField($"[{item.Severity}] {item.Category} - {item.Title}", EditorStyles.boldLabel);
                GUI.color = Color.white;
                EditorGUILayout.LabelField(item.Description, EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("Fix: " + item.Fix, EditorStyles.wordWrappedLabel);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }

        private List<MaterialConvertEntry> ScanMaterials()
        {
            var results = new List<MaterialConvertEntry>();
            string[] guids = AssetDatabase.FindAssets("t:Material");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith("Packages/")) continue;
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null) continue;
                ShaderCategory cat = ShaderCategorizer.Categorize(mat.shader.name);
                results.Add(new MaterialConvertEntry
                {
                    Material = mat,
                    OriginalShaderName = mat.shader.name,
                    Category = cat,
                    TargetShaderPath = ShaderCategorizer.GetTargetShaderName(cat),
                    Selected = cat == ShaderCategory.SimpleLit || cat == ShaderCategory.Lit || cat == ShaderCategory.Unlit,
                    Notes = cat.ToString()
                });
            }
            return results;
        }

        private void BatchConvertMaterials(List<MaterialConvertEntry> entries)
        {
            var targets = entries.Where(e => e.Selected && e.IsAutoConvertible).ToList();
            for (int i = 0; i < targets.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Converting", targets[i].Material.name, (float)i / targets.Count);
                ConvertSingleMaterial(targets[i]);
            }
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
        }

        private void ConvertSingleMaterial(MaterialConvertEntry entry)
        {
            Shader targetShader = ShaderCategorizer.FindTargetShader(entry.TargetShaderPath);
            if (targetShader == null) return;
            Material mat = entry.Material;
            var texBackup = new Dictionary<string, (Texture, Vector2, Vector2)>();
            var colBackup = new Dictionary<string, Color>();
            var floatBackup = new Dictionary<string, float>();
            var kwBackup = new List<string>(mat.shaderKeywords);

            foreach (var (src, _) in ShaderPropertyMap.TextureMap)
                if (mat.HasProperty(src) && mat.GetTexture(src) != null)
                    texBackup[src] = (mat.GetTexture(src), mat.GetTextureOffset(src), mat.GetTextureScale(src));

            foreach (var (src, _) in ShaderPropertyMap.ColorMap)
                if (mat.HasProperty(src)) colBackup[src] = mat.GetColor(src);

            foreach (var (src, _) in ShaderPropertyMap.FloatMap)
                if (mat.HasProperty(src)) floatBackup[src] = mat.GetFloat(src);

            Undo.RecordObject(mat, "Convert Material");
            mat.shader = targetShader;

            foreach (var (src, dst) in ShaderPropertyMap.TextureMap)
                if (texBackup.TryGetValue(src, out var d) && mat.HasProperty(dst))
                {
                    mat.SetTexture(dst, d.Item1);
                    mat.SetTextureOffset(dst, d.Item2);
                    mat.SetTextureScale(dst, d.Item3);
                }

            foreach (var (src, dst) in ShaderPropertyMap.ColorMap)
                if (colBackup.TryGetValue(src, out var c) && mat.HasProperty(dst)) mat.SetColor(dst, c);

            foreach (var (src, dst) in ShaderPropertyMap.FloatMap)
                if (floatBackup.TryGetValue(src, out var v) && mat.HasProperty(dst)) mat.SetFloat(dst, v);

            foreach (var (prop, val) in ShaderPropertyMap.ToonDefaults)
                if (mat.HasProperty(prop)) mat.SetFloat(prop, val);

            foreach (string kw in kwBackup)
                if (!kw.StartsWith("_SPECULAR_COLOR") && !kw.StartsWith("_ENVIRONMENTREFLECTIONS")) mat.EnableKeyword(kw);

            if (floatBackup.TryGetValue("_Surface", out float surface) && surface >= 1.0f)
            {
                mat.renderQueue = (int)RenderQueue.Transparent;
                mat.SetOverrideTag("RenderType", "Transparent");
            }
            else if (floatBackup.TryGetValue("_AlphaClip", out float clip) && clip >= 1.0f)
            {
                mat.renderQueue = (int)RenderQueue.AlphaTest;
                mat.SetOverrideTag("RenderType", "TransparentCutout");
                mat.EnableKeyword("_ALPHATEST_ON");
            }
            else
            {
                mat.renderQueue = (int)RenderQueue.Geometry;
                mat.SetOverrideTag("RenderType", "Opaque");
            }
            EditorUtility.SetDirty(mat);
            entry.Converted = true;
        }

        private List<DuplicateMaterialGroup> ScanDuplicateMaterials()
        {
            var dict = new Dictionary<string, List<Material>>();
            string[] guids = AssetDatabase.FindAssets("t:Material");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith("Packages/")) continue;
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null) continue;

                Texture tex = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : (mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null);
                string texGuid = tex != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(tex)) : "none";
                Color col = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : (mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white);
                float cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0f;
                
                string hash = $"{mat.shader.name}_{texGuid}_{ColorUtility.ToHtmlStringRGBA(col)}_{cutoff:F2}";

                if (!dict.ContainsKey(hash)) dict[hash] = new List<Material>();
                dict[hash].Add(mat);
            }

            var results = new List<DuplicateMaterialGroup>();
            foreach (var kvp in dict)
            {
                if (kvp.Value.Count > 1)
                {
                    results.Add(new DuplicateMaterialGroup
                    {
                        Fingerprint = kvp.Key,
                        MasterMaterial = kvp.Value.OrderBy(m => m.name.Length).First(),
                        Duplicates = kvp.Value.Where(m => m != kvp.Value.OrderBy(n => n.name.Length).First()).ToList()
                    });
                }
            }
            return results.OrderByDescending(g => g.Duplicates.Count).ToList();
        }

        private void ConsolidateAllMaterialsInScene()
        {
            var renderers = FindObjectsOfType<Renderer>(true);
            int replacedCount = 0;

            foreach (var r in renderers)
            {
                var mats = r.sharedMaterials;
                bool changed = false;

                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    foreach (var group in duplicateGroups)
                    {
                        if (group.Duplicates.Contains(mats[i]))
                        {
                            mats[i] = group.MasterMaterial;
                            changed = true;
                            replacedCount++;
                            break;
                        }
                    }
                }

                if (changed)
                {
                    Undo.RecordObject(r, "Consolidate Duplicate Materials");
                    r.sharedMaterials = mats;
                    EditorUtility.SetDirty(r);
                }
            }
            Debug.Log($"[Consolidator] Successfully replaced {replacedCount} material instances in the scene.");
        }

        private void DeleteUnusedDuplicates()
        {
            int deleted = 0;
            foreach (var group in duplicateGroups)
            {
                foreach (var dup in group.Duplicates)
                {
                    string path = AssetDatabase.GetAssetPath(dup);
                    if (AssetDatabase.DeleteAsset(path)) deleted++;
                }
            }
            AssetDatabase.Refresh();
            duplicateGroups = ScanDuplicateMaterials();
            Debug.Log($"[Consolidator] Deleted {deleted} duplicate material assets from the project.");
        }

        private List<FoliageAnalysisEntry> ScanFoliage()
        {
            var results = new List<FoliageAnalysisEntry>();
            var renderers = FindObjectsOfType<Renderer>();
            foreach (var r in renderers)
            {
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null || mat.shader == null) continue;
                    string n = r.gameObject.name.ToLower();
                    bool isFol = n.Contains("tree") || n.Contains("leaf") || n.Contains("grass") || n.Contains("foliage") || r.gameObject.CompareTag("Foliage");
                    if (!isFol && !mat.shader.name.Contains("Foliage") && !mat.shader.name.Contains("Grass")) continue;

                    string spath = AssetDatabase.GetAssetPath(mat.shader);
                    string content = File.Exists(spath) ? File.ReadAllText(spath) : "";
                    
                    results.Add(new FoliageAnalysisEntry
                    {
                        GameObject = r.gameObject,
                        Renderer = r,
                        Material = mat,
                        ShaderName = mat.shader.name,
                        ShadowsEnabled = r.shadowCastingMode != ShadowCastingMode.Off,
                        IsStatic = r.gameObject.isStatic,
                        HasShadowCasterPass = content.Contains("ShadowCaster"),
                        HasAlphaClipInShadow = content.Contains("ShadowCaster") && (content.Contains("clip(") || content.Contains("_ALPHATEST") || content.Contains("ALPHA_CLIP")),
                        HasVertexAnimation = mat.IsKeywordEnabled("_WIND_ON") || mat.IsKeywordEnabled("_VERTEX_ANIM") || content.Contains("_Wind"),
                        UsesGPUInstancing = mat.enableInstancing
                    });
                }
            }
            return results;
        }

        private void ForceUltimateStaticBatching(List<FoliageAnalysisEntry> entries)
        {
            foreach (var e in entries)
            {
                if (e.Material != null)
                {
                    Undo.RecordObject(e.Material, "Strip Foliage Anim");
                    e.Material.DisableKeyword("_WIND_ON");
                    e.Material.DisableKeyword("_VERTEX_ANIM");
                    e.Material.SetFloat("_Wind", 0);
                    e.Material.enableInstancing = false; 
                    EditorUtility.SetDirty(e.Material);
                }
                if (e.GameObject != null)
                {
                    Undo.RecordObject(e.GameObject, "Force Static");
                    GameObjectUtility.SetStaticEditorFlags(e.GameObject, StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.OccluderStatic);
                    EditorUtility.SetDirty(e.GameObject);
                }
            }
            AssetDatabase.SaveAssets();
        }

        private void InjectShadowCasters(List<FoliageAnalysisEntry> entries)
        {
            var paths = entries.Where(e => !e.HasShadowCasterPass).Select(e => AssetDatabase.GetAssetPath(e.Material.shader)).Distinct().ToList();
            string snippet = "Pass{Name\"ShadowCaster\"Tags{\"LightMode\"=\"ShadowCaster\"}ZWrite On ZTest LEqual ColorMask 0 Cull Off HLSLPROGRAM #pragma vertex ShadowPassVertex\n#pragma fragment ShadowPassFragment\n#pragma multi_compile_instancing\n#pragma shader_feature_local _ALPHATEST_ON\n#include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl\"\n#include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl\"\n#include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl\"\nTEXTURE2D(_BaseMap);SAMPLER(sampler_BaseMap);CBUFFER_START(UnityPerMaterial)float4 _BaseMap_ST;half4 _BaseColor;half _Cutoff;CBUFFER_END struct Attributes{float4 positionOS:POSITION;float3 normalOS:NORMAL;float2 texcoord:TEXCOORD0;UNITY_VERTEX_INPUT_INSTANCE_ID};struct Varyings{float4 positionCS:SV_POSITION;float2 uv:TEXCOORD0;UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO};float3 _LightDirection;float4 GetShadowPositionHClip(Attributes input){float3 positionWS=TransformObjectToWorld(input.positionOS.xyz);float3 normalWS=TransformObjectToWorldNormal(input.normalOS);float4 positionCS=TransformWorldToHClip(ApplyShadowBias(positionWS,normalWS,_LightDirection));#if UNITY_REVERSED_Z\npositionCS.z=min(positionCS.z,UNITY_NEAR_CLIP_VALUE);#else\npositionCS.z=max(positionCS.z,UNITY_NEAR_CLIP_VALUE);#endif\nreturn positionCS;}Varyings ShadowPassVertex(Attributes input){Varyings output;UNITY_SETUP_INSTANCE_ID(input);UNITY_TRANSFER_INSTANCE_ID(input,output);UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);output.uv=TRANSFORM_TEX(input.texcoord,_BaseMap);output.positionCS=GetShadowPositionHClip(input);return output;}half4 ShadowPassFragment(Varyings input):SV_TARGET{UNITY_SETUP_INSTANCE_ID(input);UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);half4 albedo=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,input.uv);albedo*=_BaseColor;#ifdef _ALPHATEST_ON\nclip(albedo.a-_Cutoff);#endif\nreturn 0;}ENDHLSL}";
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
                string content = File.ReadAllText(p);
                int lastIdx = content.LastIndexOf("SubShader");
                if (lastIdx < 0) continue;
                int depth = 0, insert = -1;
                for (int i = lastIdx; i < content.Length; i++)
                {
                    if (content[i] == '{') depth++;
                    if (content[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { insert = i; break; }
                    }
                }
                if (insert > 0)
                {
                    content = content.Insert(insert, "\n" + snippet + "\n");
                    File.WriteAllText(p, content);
                }
            }
            AssetDatabase.Refresh();
        }

        private List<string> ScanBloatedShaders()
        {
            var list = new List<string>();
            string[] paths = AssetDatabase.FindAssets("t:Shader").Select(AssetDatabase.GUIDToAssetPath).Where(p => !p.StartsWith("Packages/") && File.Exists(p)).ToArray();
            foreach (string p in paths)
            {
                string c = File.ReadAllText(p);
                if (c.Split(new[] { "#pragma multi_compile " }, StringSplitOptions.None).Length - 1 > 2) list.Add(p);
            }
            return list;
        }

        private void OptimizeShaderKeywords(List<string> paths)
        {
            foreach (string p in paths)
            {
                string c = File.ReadAllText(p);
                c = c.Replace("#pragma multi_compile _ALPHATEST_ON", "#pragma shader_feature_local _ALPHATEST_ON");
                c = c.Replace("#pragma multi_compile _WIND_ON", "#pragma shader_feature_local _WIND_ON");
                c = c.Replace("#pragma multi_compile _RECEIVE_SHADOWS_OFF", "#pragma shader_feature_local _RECEIVE_SHADOWS_OFF");
                File.WriteAllText(p, c);
            }
            AssetDatabase.Refresh();
        }

        private List<AuditItem> RunVRAudit()
        {
            var res = new List<AuditItem>();
            string[] paths = AssetDatabase.FindAssets("t:Shader").Select(AssetDatabase.GUIDToAssetPath).Where(p => !p.StartsWith("Packages/") && File.Exists(p)).ToArray();
            int missingVR = paths.Count(p => { string c = File.ReadAllText(p); return !c.Contains("UNITY_VERTEX_OUTPUT_STEREO"); });
            if (missingVR > 0) res.Add(new AuditItem { Severity = AuditSeverity.Critical, Category = "VR Stereo", Title = $"{missingVR} shaders lack VR SPI", Description = "Missing UNITY_VERTEX_OUTPUT_STEREO", Fix = "Add VR Instancing macros." });
            int badTex = AssetDatabase.FindAssets("t:Texture2D").Select(AssetDatabase.GUIDToAssetPath).Where(p => !p.StartsWith("Packages/")).Count(p => (AssetImporter.GetAtPath(p) as TextureImporter)?.maxTextureSize > 2048);
            if (badTex > 0) res.Add(new AuditItem { Severity = AuditSeverity.Warning, Category = "Memory", Title = $"{badTex} Oversized Textures", Description = ">2048px textures kill VR VRAM.", Fix = "Limit max size to 2048 or 1024." });
            return res.OrderBy(r => r.Severity).ToList();
        }
    }

    class VRShaderVariantStripper : IPreprocessShaders
    {
        public int callbackOrder => 0;
        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            if (shader.name.Contains("Universal Render Pipeline")) return;
            for (int i = data.Count - 1; i >= 0; --i)
            {
                if (data[i].shaderKeywordSet.IsEnabled(new ShaderKeyword("_WIND_ON")) || data[i].shaderKeywordSet.IsEnabled(new ShaderKeyword("DIRLIGHTMAP_COMBINED")))
                {
                    data.RemoveAt(i);
                }
            }
        }
    }
}