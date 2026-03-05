using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace HybridShaderOptimizer.Editor
{
    [Serializable]
    public class ShaderUsageInfo
    {
        public string ShaderName;
        public string AssetPath;
        public List<string> Keywords = new List<string>();
        public List<string> ReferencedInScenes = new List<string>();
        public List<string> ReferencedInPrefabs = new List<string>();
        public bool HasConvertibleMacros;

        public ShaderUsageInfo(string name, string path)
        {
            ShaderName = name;
            AssetPath = path;
        }
    }

    public class HybridShaderProjectData : ScriptableObject
    {
        public List<ShaderUsageInfo> UsedShaders = new List<ShaderUsageInfo>();
        public List<string> UnusedShaderPaths = new List<string>();
        public string LastAnalysisTime;
        public int TotalAssetsScanned;

        public void ClearData()
        {
            UsedShaders.Clear();
            UnusedShaderPaths.Clear();
            LastAnalysisTime = string.Empty;
            TotalAssetsScanned = 0;
        }
    }

    public class HybridShaderConfig : ScriptableObject
    {
        public bool EnableOptimization = true;
        public bool StrictBuildMode = true;
        
        public List<string> GlobalBlacklistKeywords = new List<string>();
        public List<PassType> BlacklistPasses = new List<PassType>();
        public List<string> WhitelistShaders = new List<string>();
        public List<string> GlobalWhitelistKeywords = new List<string>();

        private static string GetDynamicDirectoryPath()
        {
            string[] guids = AssetDatabase.FindAssets("HybridShaderData t:Script");
            if (guids.Length > 0)
            {
                string scriptPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                return Path.GetDirectoryName(scriptPath).Replace("\\", "/");
            }
            return "Assets/Editor/HybridShaderOptimizer";
        }

        private static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                AssetDatabase.Refresh();
            }
        }

        public static HybridShaderConfig GetOrCreate()
        {
            string directoryPath = GetDynamicDirectoryPath();
            EnsureDirectoryExists(directoryPath);
            
            string configPath = $"{directoryPath}/ShaderConfig.asset";
            var config = AssetDatabase.LoadAssetAtPath<HybridShaderConfig>(configPath);
            
            if (config == null)
            {
                config = CreateInstance<HybridShaderConfig>();
                AssetDatabase.CreateAsset(config, configPath);
                AssetDatabase.SaveAssets();
            }
            return config;
        }

        public static HybridShaderProjectData GetOrCreateData()
        {
            string directoryPath = GetDynamicDirectoryPath();
            EnsureDirectoryExists(directoryPath);
            
            string dataPath = $"{directoryPath}/ProjectData.asset";
            var data = AssetDatabase.LoadAssetAtPath<HybridShaderProjectData>(dataPath);
            
            if (data == null)
            {
                data = CreateInstance<HybridShaderProjectData>();
                AssetDatabase.CreateAsset(data, dataPath);
                AssetDatabase.SaveAssets();
            }
            return data;
        }
    }
}