Shader "VR/StylizedWater"
{
    Properties
    {
        [HideInInspector] _Cull("Cull", Float) = 2.0
        
        _ShallowColor("Shallow Color", Color) = (0.3, 0.8, 0.9, 0.7)
        [HDR] _DeepColor("Deep Color", Color) = (0.0, 0.2, 0.4, 0.8)
        _DepthMaxDistance("Depth Max Distance", Range(0.1, 20.0)) = 5.0
        
        _NormalMap("Water Normal Map", 2D) = "bump" {}
        _NormalTilingA("Normal Tiling A", Float) = 1.0
        _NormalScrollA("Normal Scroll A", Vector) = (0.01, 0.02, 0, 0)
        _NormalTilingB("Normal Tiling B", Float) = 1.5
        _NormalScrollB("Normal Scroll B", Vector) = (-0.015, 0.01, 0, 0)
        _NormalStrength("Normal Strength", Range(0.0, 2.0)) = 1.0
        
        _RefractionStrength("Refraction Strength", Range(0.0, 0.2)) = 0.05
        
        _SurfaceFoamTexture("Surface Foam Texture", 2D) = "white" {}
        _SurfaceFoamTiling("Surface Foam Tiling", Float) = 2.0
        _SurfaceFoamScroll("Surface Foam Scroll", Vector) = (0.02, 0.025, 0, 0)
        _SurfaceFoamCutoff("Surface Foam Cutoff", Range(0.0, 1.0)) = 0.6
        _FoamDistortion("Foam Distortion", Range(0.0, 0.5)) = 0.1
        
        _FoamColor("Intersection Foam Color", Color) = (1.0, 1.0, 1.0, 1.0)
        _FoamIntersectionDepth("Intersection Depth", Range(0.01, 5.0)) = 0.8
        _FoamEdgeSmoothness("Intersection Edge Smoothness", Range(0, 1)) = 0.5
        
        [HDR] _BlingColor("Bling Color", Color) = (2.0, 2.0, 2.0, 1.0)
        _BlingGloss("Bling Gloss", Range(10.0, 1024.0)) = 256.0
        _BlingThreshold("Bling Threshold", Range(0.0, 1.0)) = 0.8
        _BlingIntensity("Bling Intensity", Range(0.0, 10.0)) = 2.0
        
        _WaveAmplitude("Wave Amplitude", Range(0.0, 2.0)) = 0.15
        _WaveFrequency("Wave Frequency", Float) = 1.5
        _WaveSpeed("Wave Speed", Float) = 1.2
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent-100"
            "IgnoreProjector" = "True"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _ShallowColor;
            float4 _DeepColor;
            float4 _FoamColor;
            float4 _BlingColor;
            float4 _NormalScrollA;
            float4 _NormalScrollB;
            float4 _SurfaceFoamScroll;
            float _DepthMaxDistance;
            float _NormalTilingA;
            float _NormalTilingB;
            float _NormalStrength;
            float _RefractionStrength;
            float _SurfaceFoamTiling;
            float _SurfaceFoamCutoff;
            float _FoamDistortion;
            float _FoamIntersectionDepth;
            float _FoamEdgeSmoothness;
            float _BlingGloss;
            float _BlingThreshold;
            float _BlingIntensity;
            float _WaveAmplitude;
            float _WaveFrequency;
            float _WaveSpeed;
        CBUFFER_END

        TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);
        TEXTURE2D(_SurfaceFoamTexture); SAMPLER(sampler_SurfaceFoamTexture);

        float GetVertexWave(float3 positionOS)
        {
            float wavePhase = _Time.y * _WaveSpeed;
            float waveValue = (positionOS.x + positionOS.z) * _WaveFrequency;
            return sin(wavePhase + waveValue) * _WaveAmplitude;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 screenPos  : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 posOS = input.positionOS.xyz;
                posOS.y += GetVertexWave(posOS);

                output.positionWS = TransformObjectToWorld(posOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.screenPos = ComputeScreenPos(output.positionCS);
                
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 screenUV = input.screenPos.xy / input.screenPos.w;
                float rawDepth = SampleSceneDepth(screenUV);
                float sceneDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float surfaceDepth = input.positionCS.w;
                float depthDiff = max(0.001, sceneDepth - surfaceDepth);

                float timeY = _Time.y;
                float2 normalUvA = input.positionWS.xz * _NormalTilingA + timeY * _NormalScrollA.xy;
                float2 normalUvB = input.positionWS.xz * _NormalTilingB + timeY * _NormalScrollB.xy;

                half3 nA = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, normalUvA));
                half3 nB = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, normalUvB));
                half3 normalWS = normalize(half3(nA.xy + nB.xy, nA.z * nB.z));
                normalWS.xy *= _NormalStrength;
                normalWS = normalize(normalWS);

                float2 refractOffset = normalWS.xy * _RefractionStrength * saturate(depthDiff * 0.5);
                float2 refractUV = screenUV + refractOffset;
                half3 sceneColor = SampleSceneColor(refractUV);

                float depthGradient = saturate(depthDiff / _DepthMaxDistance);
                half3 waterColor = lerp(_ShallowColor.rgb, _DeepColor.rgb, depthGradient);
                half3 finalColor = lerp(waterColor, sceneColor, 1.0 - _ShallowColor.a);

                // ── Surface foam ──
                float2 foamUV = input.positionWS.xz * _SurfaceFoamTiling + timeY * _SurfaceFoamScroll.xy;
                foamUV += normalWS.xy * _FoamDistortion;
                
                half foamNoise = SAMPLE_TEXTURE2D(_SurfaceFoamTexture, sampler_SurfaceFoamTexture, foamUV).r;
                half surfaceFoam = step(_SurfaceFoamCutoff, foamNoise);

                // ── Intersection foam with smoothness control ──
                // _FoamEdgeSmoothness: 0 = hard/crispy edge, 1 = soft/wide blend
                float intersectNorm = saturate(depthDiff / _FoamIntersectionDepth);
                
                // Remap smoothness: 0 -> near-step (0.01), 1 -> wide soft blend (full range)
                float softRange = lerp(0.01, _FoamIntersectionDepth, _FoamEdgeSmoothness);
                float intersectFoam = 1.0 - smoothstep(0.0, softRange, depthDiff);

                half combinedFoam = saturate(surfaceFoam * intersectNorm + intersectFoam);
                finalColor = lerp(finalColor, _FoamColor.rgb, combinedFoam);

                // ── Specular bling ──
                Light mainLight = GetMainLight();
                float3 viewDir = normalize(GetCameraPositionWS() - input.positionWS);
                float3 halfVec = normalize(mainLight.direction + viewDir);

                float NdotH = saturate(dot(normalWS, halfVec));
                float specular = pow(NdotH, _BlingGloss);
                float sparkleMask = step(_BlingThreshold, specular) * specular;
                
                half3 blingResult = sparkleMask * _BlingColor.rgb * _BlingIntensity * mainLight.shadowAttenuation;
                finalColor += blingResult;

                return half4(finalColor, _ShallowColor.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 posOS = input.positionOS.xyz;
                posOS.y += GetVertexWave(posOS);
                output.positionCS = TransformObjectToHClip(posOS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return 0;
            }
            ENDHLSL
        }
    }
    CustomEditor "StylizedWaterVRGUI"
}
