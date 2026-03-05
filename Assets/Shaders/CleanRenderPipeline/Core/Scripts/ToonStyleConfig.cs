using UnityEngine;

namespace CleanRender
{
    [CreateAssetMenu(fileName = "ToonStyleConfig", menuName = "CleanRender/Toon Style Config")]
    public class ToonStyleConfig : ScriptableObject
    {
        [Header("━━━ GLOBAL CEL SHADING ━━━")]
        [Tooltip("Ngưỡng sáng/tối cho cel shading")]
        [Range(0f, 1f)] public float shadowThreshold = 0.5f;

        [Tooltip("Độ mềm của ranh giới sáng/tối")]
        [Range(0.001f, 0.5f)] public float shadowSmoothness = 0.05f;

        [Tooltip("Màu vùng tối")]
        public Color shadowColor = new Color(0.3f, 0.3f, 0.4f, 1f);

        [Header("━━━ RIM LIGHT ━━━")]
        public Color rimColor = new Color(1f, 1f, 1f, 0.5f);
        [Range(0.1f, 10f)] public float rimPower = 3f;
        [Range(0f, 1f)] public float rimIntensity = 0.5f;

        [Header("━━━ AMBIENT ━━━")]
        [Range(0f, 1f)] public float ambientStrength = 0.3f;
        public Color ambientOverride = Color.clear; // clear = use SH

        [Header("━━━ SPECULAR (ToonMetal) ━━━")]
        [Range(0f, 1f)] public float specularCutoff = 0.7f;
        [Range(0.001f, 0.3f)] public float specularSmoothness = 0.05f;
        public Color specularColor = new Color(1f, 0.95f, 0.9f, 1f);

        [Header("━━━ ENVIRONMENT ━━━")]
        public Color fogColor = new Color(0.7f, 0.8f, 0.9f, 1f);
        [Range(0f, 500f)] public float fogStartDistance = 50f;
        [Range(0f, 1000f)] public float fogEndDistance = 300f;

        [Header("━━━ RENDERING ━━━")]
        [Range(10f, 2000f)] public float cullDistance = 500f;
        [Range(10f, 500f)] public float shadowDistance = 150f;
        [Range(0.5f, 3f)] public float lodBias = 1f;

        private static ToonStyleConfig _instance;

        public static ToonStyleConfig Instance
        {
            get
            {
                if (_instance == null)
                    _instance = Resources.Load<ToonStyleConfig>("ToonStyleConfig");
                return _instance;
            }
        }

        private static readonly int _ShadowThreshold = Shader.PropertyToID("_GlobalShadowThreshold");
        private static readonly int _ShadowSmoothness = Shader.PropertyToID("_GlobalShadowSmoothness");
        private static readonly int _ShadowColor = Shader.PropertyToID("_GlobalShadowColor");
        private static readonly int _RimColor = Shader.PropertyToID("_GlobalRimColor");
        private static readonly int _RimPower = Shader.PropertyToID("_GlobalRimPower");
        private static readonly int _RimIntensity = Shader.PropertyToID("_GlobalRimIntensity");
        private static readonly int _AmbientStrength = Shader.PropertyToID("_GlobalAmbientStrength");
        private static readonly int _SpecCutoff = Shader.PropertyToID("_GlobalSpecularCutoff");
        private static readonly int _SpecSmooth = Shader.PropertyToID("_GlobalSpecularSmoothness");
        private static readonly int _SpecColor = Shader.PropertyToID("_GlobalSpecularColor");

        public void Apply()
        {
            Shader.SetGlobalFloat(_ShadowThreshold, shadowThreshold);
            Shader.SetGlobalFloat(_ShadowSmoothness, shadowSmoothness);
            Shader.SetGlobalColor(_ShadowColor, shadowColor);
            Shader.SetGlobalColor(_RimColor, rimColor);
            Shader.SetGlobalFloat(_RimPower, rimPower);
            Shader.SetGlobalFloat(_RimIntensity, rimIntensity);
            Shader.SetGlobalFloat(_AmbientStrength, ambientStrength);
            Shader.SetGlobalFloat(_SpecCutoff, specularCutoff);
            Shader.SetGlobalFloat(_SpecSmooth, specularSmoothness);
            Shader.SetGlobalColor(_SpecColor, specularColor);
        }

        private void OnValidate() => Apply();
    }
}
