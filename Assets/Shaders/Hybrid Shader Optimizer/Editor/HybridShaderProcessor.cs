using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace HybridShaderOptimizer.Editor
{
    public static class ShaderAutoSuggestEngine
    {
        public static void AutoDetectAndFillRules(HybridShaderConfig config)
        {
            var suggestedKeywords = new HashSet<string>(config.GlobalBlacklistKeywords);
            var suggestedPasses = new HashSet<PassType>(config.BlacklistPasses);

            if (!RenderSettings.fog)
            {
                suggestedKeywords.Add("FOG_LINEAR");
                suggestedKeywords.Add("FOG_EXP");
                suggestedKeywords.Add("FOG_EXP2");
            }

            if (QualitySettings.shadows == ShadowQuality.Disable)
            {
                suggestedKeywords.Add("SHADOWS_DEPTH");
                suggestedKeywords.Add("SHADOWS_SOFT");
                suggestedKeywords.Add("SHADOWS_SCREEN");
                suggestedKeywords.Add("SHADOWS_CUBE");
                suggestedKeywords.Add("_SHADOWS_SOFT");
                suggestedPasses.Add(PassType.ShadowCaster);
            }

            bool usesURP = GraphicsSettings.currentRenderPipeline != null || GraphicsSettings.defaultRenderPipeline != null;
            if (usesURP)
            {
                suggestedPasses.Add(PassType.ForwardBase);
                suggestedPasses.Add(PassType.ForwardAdd);
                suggestedPasses.Add(PassType.Deferred);
                suggestedPasses.Add(PassType.Vertex);
                suggestedPasses.Add(PassType.VertexLM);
                suggestedPasses.Add(PassType.VertexLMRGBM);
            }
            else
            {
                suggestedPasses.Add(PassType.ScriptableRenderPipeline);
                suggestedPasses.Add(PassType.ScriptableRenderPipelineDefaultUnlit);
            }

            if (LightmapSettings.lightmaps.Length == 0)
            {
                suggestedKeywords.Add("LIGHTMAP_ON");
                suggestedKeywords.Add("DIRLIGHTMAP_COMBINED");
                suggestedKeywords.Add("DYNAMICLIGHTMAP_ON");
                suggestedKeywords.Add("LIGHTMAP_SHADOW_MIXING");
                suggestedKeywords.Add("SHADOWS_SHADOWMASK");
            }

            config.GlobalBlacklistKeywords = suggestedKeywords.ToList();
            config.BlacklistPasses = suggestedPasses.ToList();
            
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
        }
    }

    public static class ShaderDeepAnalyzer
    {
        private static readonly string[] IgnoredMacros = 
        { 
            "multi_compile_fwdbase", "multi_compile_fwdadd", "multi_compile_fog", 
            "multi_compile_instancing", "multi_compile_particles", "multi_compile_shadowcaster" 
        };

        public static void RunDeepScan()
        {
            var data = HybridShaderConfig.GetOrCreateData();
            data.ClearData();

            string[] allScenes = AssetDatabase.FindAssets("t:Scene").Select(AssetDatabase.GUIDToAssetPath).ToArray();
            string[] allPrefabs = AssetDatabase.FindAssets("t:Prefab").Select(AssetDatabase.GUIDToAssetPath).ToArray();
            string[] allMaterials = AssetDatabase.FindAssets("t:Material").Select(AssetDatabase.GUIDToAssetPath).ToArray();
            string[] allShaders = AssetDatabase.FindAssets("t:Shader").Select(AssetDatabase.GUIDToAssetPath).ToArray();

            var shaderUsageDict = new Dictionary<string, ShaderUsageInfo>();
            var materialToShader = new Dictionary<string, string>();

            foreach (string matPath in allMaterials)
            {
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat != null && mat.shader != null)
                {
                    string shaderName = mat.shader.name;
                    string shaderPath = AssetDatabase.GetAssetPath(mat.shader);
                    materialToShader[matPath] = shaderName;

                    if (!shaderUsageDict.ContainsKey(shaderName))
                    {
                        shaderUsageDict[shaderName] = new ShaderUsageInfo(shaderName, shaderPath)
                        {
                            HasConvertibleMacros = CheckConvertibleMacros(shaderPath)
                        };
                    }

                    foreach (string kw in mat.shaderKeywords)
                    {
                        if (!shaderUsageDict[shaderName].Keywords.Contains(kw))
                        {
                            shaderUsageDict[shaderName].Keywords.Add(kw);
                        }
                    }
                }
            }

            ProcessDependencies(allScenes, materialToShader, shaderUsageDict, true);
            ProcessDependencies(allPrefabs, materialToShader, shaderUsageDict, false);

            data.UsedShaders = shaderUsageDict.Values.Where(s => s.ReferencedInScenes.Count > 0 || s.ReferencedInPrefabs.Count > 0).ToList();

            var usedShaderPaths = new HashSet<string>(data.UsedShaders.Select(s => s.AssetPath));
            foreach (string shaderPath in allShaders)
            {
                if (!usedShaderPaths.Contains(shaderPath) && !shaderPath.StartsWith("Packages/"))
                {
                    data.UnusedShaderPaths.Add(shaderPath);
                }
            }

            data.TotalAssetsScanned = allScenes.Length + allPrefabs.Length + allMaterials.Length;
            data.LastAnalysisTime = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
        }

        private static void ProcessDependencies(string[] rootAssets, Dictionary<string, string> materialToShader, Dictionary<string, ShaderUsageInfo> usageDict, bool isScene)
        {
            foreach (string root in rootAssets)
            {
                string[] deps = AssetDatabase.GetDependencies(root, false);
                foreach (string dep in deps)
                {
                    if (materialToShader.TryGetValue(dep, out string shaderName))
                    {
                        if (isScene)
                        {
                            if (!usageDict[shaderName].ReferencedInScenes.Contains(root))
                                usageDict[shaderName].ReferencedInScenes.Add(root);
                        }
                        else
                        {
                            if (!usageDict[shaderName].ReferencedInPrefabs.Contains(root))
                                usageDict[shaderName].ReferencedInPrefabs.Add(root);
                        }
                    }
                }
            }
        }

        private static bool CheckConvertibleMacros(string shaderPath)
        {
            if (string.IsNullOrEmpty(shaderPath) || !File.Exists(shaderPath)) return false;
            string content = File.ReadAllText(shaderPath);
            return content.Contains("#pragma multi_compile ") && !IgnoredMacros.Any(content.Contains);
        }

        public static void ConvertMacros(string shaderPath)
        {
            if (string.IsNullOrEmpty(shaderPath) || !File.Exists(shaderPath)) return;
            string[] lines = File.ReadAllLines(shaderPath);
            bool changed = false;

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("#pragma multi_compile ") || lines[i].Contains("#pragma multi_compile_local "))
                {
                    bool isIgnored = IgnoredMacros.Any(ignored => lines[i].Contains(ignored));
                    if (!isIgnored)
                    {
                        lines[i] = lines[i].Replace("multi_compile_local", "shader_feature_local")
                                           .Replace("multi_compile", "shader_feature");
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                File.WriteAllLines(shaderPath, lines);
                AssetDatabase.ImportAsset(shaderPath);
            }
        }
    }

    public class ShaderBuildInterceptor : IPreprocessShaders
    {
        public int callbackOrder => 0;

        private HybridShaderConfig _config;
        private HybridShaderProjectData _data;
        private HashSet<string> _fastBlacklistKeywords;
        private HashSet<PassType> _fastBlacklistPasses;
        private HashSet<string> _fastWhitelistShaders;
        private HashSet<string> _fastUsedShaders;

        public ShaderBuildInterceptor()
        {
            _config = HybridShaderConfig.GetOrCreate();
            _data = HybridShaderConfig.GetOrCreateData();

            _fastBlacklistKeywords = new HashSet<string>(_config.GlobalBlacklistKeywords);
            _fastBlacklistPasses = new HashSet<PassType>(_config.BlacklistPasses);
            _fastWhitelistShaders = new HashSet<string>(_config.WhitelistShaders);
            _fastUsedShaders = new HashSet<string>(_data.UsedShaders.Select(s => s.ShaderName));
        }

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            if (!_config.EnableOptimization) return;
            if (_fastWhitelistShaders.Contains(shader.name)) return;

            if (_config.StrictBuildMode && !_fastUsedShaders.Contains(shader.name))
            {
                data.Clear();
                return;
            }

            if (_fastBlacklistPasses.Contains(snippet.passType))
            {
                data.Clear();
                return;
            }

            for (int i = data.Count - 1; i >= 0; --i)
            {
                var keywords = data[i].shaderKeywordSet.GetShaderKeywords();
                foreach (var kw in keywords)
                {
                    if (_fastBlacklistKeywords.Contains(kw.name))
                    {
                        data.RemoveAt(i);
                        break;
                    }
                }
            }
        }
    }
}