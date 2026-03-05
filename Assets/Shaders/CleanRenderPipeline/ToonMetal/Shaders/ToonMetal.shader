Shader "CleanRender/ToonMetal"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white"{}
        [MainColor] _BaseColor("Base Color", Color) = (0.8, 0.75, 0.65, 1)

        [Header(Metal)]
        _MetalColor("Metal Highlight Color", Color) = (1, 0.95, 0.85, 1)
        _MetalCutoff("Metal Fresnel Cutoff", Range(0, 1)) = 0.5
        _MetalSmoothness("Metal Fresnel Smoothness", Range(0, 0.3)) = 0.05
        _MetalMask("Metal Mask (R=metal)", 2D) = "white"{}

        [Header(Specular Highlight)]
        _SpecColor("Specular Color", Color) = (1, 1, 1, 1)
        _SpecCutoff("Specular Cutoff", Range(0, 1)) = 0.85
        _SpecSmoothness("Specular Smoothness", Range(0, 0.2)) = 0.03

        [Header(Cel Shading)]
        _ShadowColor("Shadow Color", Color) = (0.25, 0.2, 0.35, 1)
        _Threshold("Shadow Threshold", Range(0, 1)) = 0.45
        _Smoothness("Shadow Smoothness", Range(0, 0.5)) = 0.04

        [Header(Rim)]
        _RimColor("Rim Color", Color) = (1, 0.95, 0.9, 0.6)
        _RimPower("Rim Power", Range(0.1, 10)) = 4

        [Header(Alpha)]
        [Toggle(_ALPHATEST_ON)] _AlphaClip("Alpha Clip", Float) = 0
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "StaticInstancing" = "True"
        }
        LOD 200

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Assets/Shaders/CleanRenderPipeline/Core/Shaders/Includes/InstancingCore.hlsl"
        #include "Assets/Shaders/CleanRenderPipeline/Core/Shaders/Includes/ToonLighting.hlsl"

        #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
            StructuredBuffer<CompressedInstanceData> _SourceData;
            StructuredBuffer<uint> _VisibleIndices;
        #endif

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _MetalColor;
            float _MetalCutoff;
            float _MetalSmoothness;
            half4 _SpecColor;
            float _SpecCutoff;
            float _SpecSmoothness;
            half4 _ShadowColor;
            half4 _RimColor;
            float _Threshold;
            float _Smoothness;
            float _RimPower;
            float _Cutoff;
        CBUFFER_END

        TEXTURE2D(_BaseMap);    SAMPLER(sampler_BaseMap);
        TEXTURE2D(_MetalMask);  SAMPLER(sampler_MetalMask);

        void Setup()
        {
            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                SETUP_COMPRESSED_INSTANCE(_SourceData, _VisibleIndices);
            #endif
        }
        ENDHLSL

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 0: Forward Lit
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex MetalVert
            #pragma fragment MetalFrag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:Setup
            #pragma shader_feature_local _ALPHATEST_ON

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 lightmapUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float2 uv : TEXCOORD0;
                float2 lightmapUV : TEXCOORD3;
                half   fogFactor : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings MetalVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                o.lightmapUV = ToonTransformLightmapUV(input.lightmapUV);
                o.fogFactor = (half)ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 MetalFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 albedoTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half metalMask = SAMPLE_TEXTURE2D(_MetalMask, sampler_MetalMask, input.uv).r;

                #ifdef _ALPHATEST_ON
                    clip(albedoTex.a * _BaseColor.a - _Cutoff);
                #endif

                float3 N = normalize(input.normalWS);
                float3 V = normalize(GetCameraPositionWS() - input.positionWS);

                // ── Main Light ──
                float4 shadowCoord = ToonGetShadowCoordSimple(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float3 L = mainLight.direction;

                float NdotL = dot(N, L);
                float shadow = mainLight.shadowAttenuation;
                float intensity = smoothstep(_Threshold - _Smoothness, _Threshold + _Smoothness, NdotL * shadow);

                // ── Base Cel Shading ──
                half3 baseAlbedo = albedoTex.rgb * _BaseColor.rgb;
                half3 diffuse = baseAlbedo * lerp(_ShadowColor.rgb, mainLight.color, intensity);

                // ── Metallic Fresnel ──
                float NdotV = dot(N, V);
                float fresnel = smoothstep(_MetalCutoff - _MetalSmoothness,
                                           _MetalCutoff + _MetalSmoothness, NdotV);
                half3 metalHighlight = lerp(baseAlbedo * _ShadowColor.rgb, _MetalColor.rgb, fresnel);

                // ── Specular ──
                float3 H = normalize(L + V);
                float NdotH = saturate(dot(N, H));
                float spec = smoothstep(_SpecCutoff - _SpecSmoothness,
                                        _SpecCutoff + _SpecSmoothness, NdotH) * shadow;
                half3 specular = _SpecColor.rgb * spec;

                // ── Combine ──
                half3 nonMetal = diffuse;
                half3 metal = metalHighlight * lerp(_ShadowColor.rgb, mainLight.color, intensity) + specular;
                half3 finalColor = lerp(nonMetal, metal, metalMask);

                // ── Rim ──
                float rimVal = 1.0 - saturate(NdotV);
                float rim = smoothstep(1.0 - (1.0 / _RimPower), 1.0, rimVal * intensity);
                finalColor += _RimColor.rgb * rim * _RimColor.a;

                // ── Baked GI ──
                half3 bakedGI = ToonSampleBakedGI(input.lightmapUV, (half3)N);
                finalColor += bakedGI * baseAlbedo;

                // ── Additional Lights ──
                #ifdef _ADDITIONAL_LIGHTS
                    finalColor += ComputeToonAdditionalLightsGlobal(input.positionWS, N, baseAlbedo);
                #endif

                finalColor = MixFog(finalColor, input.fogFactor);

                return half4(finalColor, albedoTex.a * _BaseColor.a);
            }
            ENDHLSL
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 1: Shadow Caster
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:Setup
            #pragma shader_feature_local _ALPHATEST_ON

            float3 _LightDirection;
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO };

            Varyings ShadowVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normWS = TransformObjectToWorldNormal(input.normalOS);
                o.positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, normWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    o.positionCS.z = min(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    o.positionCS.z = max(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                #ifdef _ALPHATEST_ON
                    o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                #else
                    o.uv = 0;
                #endif
                return o;
            }

            half4 ShadowFrag(Varyings i) : SV_Target
            {
                #ifdef _ALPHATEST_ON
                    clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).a * _BaseColor.a - _Cutoff);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 2: Depth Only
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R

            HLSLPROGRAM
            #pragma vertex DOVert
            #pragma fragment DOFrag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:Setup
            #pragma shader_feature_local _ALPHATEST_ON

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO };

            Varyings DOVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformWorldToHClip(TransformObjectToWorld(input.positionOS.xyz));
                #ifdef _ALPHATEST_ON
                    o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                #else
                    o.uv = 0;
                #endif
                return o;
            }

            half4 DOFrag(Varyings i) : SV_Target
            {
                #ifdef _ALPHATEST_ON
                    clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).a * _BaseColor.a - _Cutoff);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 3: Depth Normals (NEW — was missing → no SSAO)
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex DNVert
            #pragma fragment DNFrag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:Setup
            #pragma shader_feature_local _ALPHATEST_ON

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3  normalWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DNVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformWorldToHClip(TransformObjectToWorld(input.positionOS.xyz));
                o.normalWS = (half3)TransformObjectToWorldNormal(input.normalOS);
                #ifdef _ALPHATEST_ON
                    o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                #else
                    o.uv = 0;
                #endif
                return o;
            }

            half4 DNFrag(Varyings input) : SV_Target
            {
                #ifdef _ALPHATEST_ON
                    clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a - _Cutoff);
                #endif
                half3 n = normalize(input.normalWS);
                return half4(n * 0.5h + 0.5h, 0);
            }
            ENDHLSL
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 4: Meta (NEW — was missing → no GI bounce)
        //
        // Albedo = lerp(diffuse, metalAvg, metalMask) so baker
        // gets correct apparent surface color for GI bounce.
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "Meta"
            Tags { "LightMode" = "Meta" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex MetaVert
            #pragma fragment MetaFrag
            #pragma shader_feature_local _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                float2 uv2 : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings MetaVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                o.positionCS = UnityMetaVertexPosition(input.positionOS.xyz, input.uv1, input.uv2);
                o.uv = TRANSFORM_TEX(input.uv0, _BaseMap);
                return o;
            }

            half4 MetaFrag(Varyings input) : SV_Target
            {
                half4 base = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);

                #ifdef _ALPHATEST_ON
                    clip(base.a * _BaseColor.a - _Cutoff);
                #endif

                half metalMask = SAMPLE_TEXTURE2D(_MetalMask, sampler_MetalMask, input.uv).r;

                half3 diffuseAlbedo = base.rgb * _BaseColor.rgb;

                // Metal regions: average of base and metal highlight color
                // (approximates view-dependent fresnel at NdotV ≈ 0.5)
                half3 metalAlbedo = lerp(diffuseAlbedo, _MetalColor.rgb, 0.5h);

                half3 albedo = lerp(diffuseAlbedo, metalAlbedo, metalMask);

                MetaInput metaInput = (MetaInput)0;
                metaInput.Albedo = albedo;
                metaInput.Emission = half3(0, 0, 0);
                return UnityMetaFragment(metaInput);
            }
            ENDHLSL
        }
    }
}
