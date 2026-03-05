Shader "CleanRender/ToonFoliage"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white"{}
        [MainColor] _BaseColor("Base Color", Color) = (0.3, 0.65, 0.2, 1)

        [KeywordEnum(LEAF, BARK, GRASS)] _FOLIAGE_TYPE("Foliage Type", Float) = 0

        [Header(Wind)]
        _WindTex("Wind Noise Texture", 2D) = "gray"{}
        _WindScale("Wind Scale", Float) = 0.05
        _WindSpeed("Wind Speed", Float) = 1.0
        _WindStrength("Wind Strength", Range(0, 2)) = 0.5
        [Toggle(_USE_VERTEX_COLOR_WIND)] _UseVertexColorWind("Vertex Color masks wind (R)", Float) = 1
        [Toggle(_STATIC_BATCH)] _StaticBatch("Static Batch (disable wind)", Float) = 0

        [Header(Cel Shading)]
        _ShadowColor("Shadow Color", Color) = (0.15, 0.25, 0.1, 1)
        _Threshold("Shadow Threshold", Range(0, 1)) = 0.45
        _Smoothness("Shadow Smoothness", Range(0.001, 0.5)) = 0.08

        [Header(Subsurface)]
        _SubsurfaceColor("Subsurface Color", Color) = (0.5, 0.8, 0.1, 1)
        _SubsurfaceStrength("Subsurface Strength", Range(0, 1)) = 0.3

        [Header(Alpha)]
        [Toggle(_ALPHATEST_ON)] _AlphaClip("Alpha Clip", Float) = 0
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "AlphaTest"
        }
        LOD 200

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Assets/Shaders/CleanRenderPipeline/Core/Shaders/Includes/InstancingCore.hlsl"
        #include "Assets/Shaders/CleanRenderPipeline/Core/Shaders/Includes/ToonLighting.hlsl"
        #include "Assets/Shaders/CleanRenderPipeline/Core/Shaders/Includes/NoiseLib.hlsl"

        #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
            StructuredBuffer<CompressedInstanceData> _SourceData;
            StructuredBuffer<uint> _VisibleIndices;
        #endif

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            float4 _WindTex_ST;
            float _WindScale;
            float _WindSpeed;
            float _WindStrength;
            half4 _ShadowColor;
            float _Threshold;
            float _Smoothness;
            half4 _SubsurfaceColor;
            float _SubsurfaceStrength;
            float _Cutoff;
        CBUFFER_END

        TEXTURE2D(_BaseMap);    SAMPLER(sampler_BaseMap);
        TEXTURE2D(_WindTex);    SAMPLER(sampler_WindTex);

        void Setup()
        {
            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                SETUP_COMPRESSED_INSTANCE(_SourceData, _VisibleIndices);
            #endif
        }

        // ── Shared wind displacement ──
        // Tách hàm chung để tất cả pass dùng cùng logic, shadow khớp geometry
        float3 ApplyFoliageWind(float3 posWS, float windMask)
        {
            #ifdef _STATIC_BATCH
                return posWS; // Static batch: không dùng wind
            #endif

            #if defined(_FOLIAGE_TYPE_LEAF) || defined(_FOLIAGE_TYPE_GRASS)
                float3 windOffset = SampleWind(
                    TEXTURE2D_ARGS(_WindTex, sampler_WindTex),
                    posWS, _WindScale, _WindSpeed, _WindStrength, _Time.y);
                posWS += windOffset * windMask;
            #elif defined(_FOLIAGE_TYPE_BARK)
                float sway = SimpleWave(posWS, _WindSpeed * 0.3, _WindScale * 0.2, _Time.y);
                posWS.x += sway * _WindStrength * 0.1 * windMask;
            #endif

            return posWS;
        }

        float GetWindMask(float4 color, float posY)
        {
            #ifdef _USE_VERTEX_COLOR_WIND
                return color.r;
            #else
                return saturate(posY);
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
            Cull Off
            AlphaToMask [_ALPHATEST_ON]

            HLSLPROGRAM
            #pragma vertex FoliageVert
            #pragma fragment FoliageFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:Setup

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _USE_VERTEX_COLOR_WIND
            #pragma shader_feature_local _STATIC_BATCH
            #pragma multi_compile_local _FOLIAGE_TYPE_LEAF _FOLIAGE_TYPE_BARK _FOLIAGE_TYPE_GRASS

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float2 lightmapUV : TEXCOORD1;
                float4 color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float3 positionWS   : TEXCOORD1;
                float3 normalWS     : TEXCOORD2;
                float4 uv           : TEXCOORD0; // xy = baseUV, zw = lightmapUV
                float4 vertexColor  : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings FoliageVert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                float windMask = GetWindMask(input.color, input.positionOS.y);
                posWS = ApplyFoliageWind(posWS, windMask);

                o.positionWS = posWS;
                o.positionCS = TransformWorldToHClip(posWS);
                o.normalWS   = TransformObjectToWorldNormal(input.normalOS);
                o.uv.xy      = TRANSFORM_TEX(input.uv, _BaseMap);

                #ifdef LIGHTMAP_ON
                    o.uv.zw = input.lightmapUV * unity_LightmapST.xy + unity_LightmapST.zw;
                #else
                    o.uv.zw = float2(0, 0);
                #endif

                o.vertexColor = input.color;
                return o;
            }

            half4 FoliageFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv.xy) * _BaseColor;

                #ifdef _ALPHATEST_ON
                    clip(albedo.a - _Cutoff);
                #endif

                float3 N = normalize(input.normalWS);
                float3 V = normalize(GetCameraPositionWS() - input.positionWS);

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float NdotL    = saturate(dot(N, mainLight.direction));
                float shadow   = mainLight.shadowAttenuation;
                float intensity = smoothstep(
                    _Threshold - _Smoothness,
                    _Threshold + _Smoothness,
                    NdotL * shadow);

                half3 litColor = albedo.rgb * lerp(_ShadowColor.rgb, mainLight.color, intensity);

                // Subsurface scattering cho lá
                #if defined(_FOLIAGE_TYPE_LEAF)
                    float subsurface = saturate(dot(-N, mainLight.direction))
                                     * _SubsurfaceStrength * shadow;
                    litColor += _SubsurfaceColor.rgb * subsurface * albedo.rgb;
                #endif

                // Vertex color AO (green channel)
                litColor *= lerp(0.6, 1.0, input.vertexColor.g);

                // Indirect lighting
                #ifdef LIGHTMAP_ON
                    litColor += SampleLightmap(input.uv.zw, N) * albedo.rgb;
                #else
                    litColor += SampleSH(N) * albedo.rgb * 0.3;
                #endif

                return half4(litColor, albedo.a);
            }
            ENDHLSL
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 1: Shadow Caster
        // Đảm bảo lá đổ bóng đúng với alpha cutout
        // Wind khớp với Forward pass để shadow không bị lệch
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag

            #pragma multi_compile_instancing
            #pragma instancing_options procedural:Setup
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _USE_VERTEX_COLOR_WIND
            #pragma shader_feature_local _STATIC_BATCH
            #pragma multi_compile_local _FOLIAGE_TYPE_LEAF _FOLIAGE_TYPE_BARK _FOLIAGE_TYPE_GRASS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            ShadowVaryings ShadowVert(ShadowAttributes input)
            {
                ShadowVaryings o;
                UNITY_SETUP_INSTANCE_ID(input);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);

                // Wind phải khớp Forward pass để shadow không lệch
                float windMask = GetWindMask(input.color, input.positionOS.y);
                posWS = ApplyFoliageWind(posWS, windMask);

                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                // Shadow bias để tránh shadow acne
                o.positionCS = TransformWorldToHClip(
                    ApplyShadowBias(posWS, normalWS, _LightDirection));

                #if UNITY_REVERSED_Z
                    o.positionCS.z = min(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    o.positionCS.z = max(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return o;
            }

            half4 ShadowFrag(ShadowVaryings input) : SV_Target
            {
                #ifdef _ALPHATEST_ON
                    half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                    clip(alpha - _Cutoff);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 2: Depth Only
        // Cần cho depth prepass, SSAO, và các effect dựa depth
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            #pragma multi_compile_instancing
            #pragma instancing_options procedural:Setup
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _USE_VERTEX_COLOR_WIND
            #pragma shader_feature_local _STATIC_BATCH
            #pragma multi_compile_local _FOLIAGE_TYPE_LEAF _FOLIAGE_TYPE_BARK _FOLIAGE_TYPE_GRASS

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            DepthVaryings DepthVert(DepthAttributes input)
            {
                DepthVaryings o;
                UNITY_SETUP_INSTANCE_ID(input);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                float windMask = GetWindMask(input.color, input.positionOS.y);
                posWS = ApplyFoliageWind(posWS, windMask);

                o.positionCS = TransformWorldToHClip(posWS);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return o;
            }

            half4 DepthFrag(DepthVaryings input) : SV_Target
            {
                #ifdef _ALPHATEST_ON
                    half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                    clip(alpha - _Cutoff);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 3: Meta (Lightmap Baking)
        // Cần để foliage đóng góp GI khi bake lightmap
        // Không cần wind vì bake là offline
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

            struct MetaAttributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float2 uvLM       : TEXCOORD1;
                float2 uvDLM      : TEXCOORD2;
            };

            struct MetaVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            MetaVaryings MetaVert(MetaAttributes input)
            {
                MetaVaryings o;
                o.positionCS = UnityMetaVertexPosition(
                    input.positionOS.xyz,
                    input.uvLM, input.uvDLM,
                    unity_LightmapST, unity_DynamicLightmapST);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return o;
            }

            half4 MetaFrag(MetaVaryings input) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

                #ifdef _ALPHATEST_ON
                    clip(albedo.a - _Cutoff);
                #endif

                MetaInput metaInput;
                metaInput.Albedo    = albedo.rgb;
                metaInput.Emission  = half3(0, 0, 0);
                #ifdef EDITOR_VISUALIZATION
                    metaInput.VizUV         = 0;
                    metaInput.LightCoord    = 0;
                #endif

                return UnityMetaFragment(metaInput);
            }
            ENDHLSL
        }
    }

    // Fallback cho shadow nếu pass trên lỗi
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
