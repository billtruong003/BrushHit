Shader "CleanRender/ToonTerrain"
{
    Properties
    {
        [Header(Terrain Layers)]
        _Layer0("Layer 0 (Low)", 2D) = "white"{}
        _Layer0Color("Layer 0 Tint", Color) = (0.6, 0.55, 0.4, 1)
        _Layer1("Layer 1 (Mid)", 2D) = "white"{}
        _Layer1Color("Layer 1 Tint", Color) = (0.3, 0.55, 0.2, 1)
        _Layer2("Layer 2 (High)", 2D) = "white"{}
        _Layer2Color("Layer 2 Tint", Color) = (0.7, 0.7, 0.72, 1)
        _Layer3("Layer 3 (CliffTriplanar)", 2D) = "white"{}
        _Layer3Color("Layer 3 Tint", Color) = (0.45, 0.4, 0.35, 1)

        [Header(Splat Map)]
        [Toggle(_USE_SPLATMAP)] _UseSplatMap("Use Splat Map", Float) = 0
        _SplatMap("Splat Map (RGBA)", 2D) = "white"{}
        _SplatInfluence("Splat Influence", Range(0, 1)) = 1
        _SplatSharpness("Splat Blend Sharpness", Range(0.1, 20)) = 5

        [Header(Hole Map)]
        [Toggle(_USE_HOLEMAP)] _UseHoleMap("Use Hole Map", Float) = 0
        _HoleMap("Hole Map (Alpha)", 2D) = "white"{}
        _HoleThreshold("Hole Threshold", Range(0, 1)) = 0.5
        _HoleEdgeSoftness("Hole Edge Softness", Range(0, 0.2)) = 0.02
        _Cutoff("Cutoff (Baker)", Range(0, 1)) = 0.5

        [Header(Height Blending)]
        _HeightLow("Height Low", Float) = 5
        _HeightMid("Height Mid", Float) = 20
        _BlendSharpness("Blend Sharpness", Range(0.1, 20)) = 5
        _HeightOffset("Height Offset", Float) = 0

        [Header(Triplanar Cliff)]
        _TriplanarScale("Triplanar Scale", Float) = 0.2
        _TriplanarSharpness("Triplanar Blend Sharpness", Range(1, 10)) = 4
        _CliffAngle("Cliff Angle Threshold", Range(0, 1)) = 0.5

        [Header(Texture Scale)]
        _TexScale("Global Texture Scale", Float) = 0.1

        [Header(Cel Shading)]
        _ShadowColor("Shadow Color", Color) = (0.2, 0.22, 0.3, 1)
        _Threshold("Shadow Threshold", Range(0, 1)) = 0.45
        _Smoothness("Shadow Smoothness", Range(0.001, 0.5)) = 0.06
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry-100"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
        #include "Assets/Shaders/CleanRenderPipeline/Core/Shaders/Includes/ToonLighting.hlsl"
        #include "Assets/Shaders/CleanRenderPipeline/Core/Shaders/Includes/NoiseLib.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _Layer0_ST; half4 _Layer0Color;
            float4 _Layer1_ST; half4 _Layer1Color;
            float4 _Layer2_ST; half4 _Layer2Color;
            float4 _Layer3_ST; half4 _Layer3Color;

            float4 _SplatMap_ST;
            half   _SplatInfluence;
            half   _SplatSharpness;

            float4 _HoleMap_ST;
            half   _HoleThreshold;
            half   _HoleEdgeSoftness;
            half   _Cutoff;

            float  _HeightLow;
            float  _HeightMid;
            half   _BlendSharpness;
            float  _HeightOffset;

            half   _TriplanarScale;
            half   _TriplanarSharpness;
            half   _CliffAngle;

            half   _TexScale;

            half4  _ShadowColor;
            half   _Threshold;
            half   _Smoothness;
        CBUFFER_END

        TEXTURE2D(_Layer0); SAMPLER(sampler_Layer0);
        TEXTURE2D(_Layer1); SAMPLER(sampler_Layer1);
        TEXTURE2D(_Layer2); SAMPLER(sampler_Layer2);
        TEXTURE2D(_Layer3); SAMPLER(sampler_Layer3);
        TEXTURE2D(_SplatMap); SAMPLER(sampler_SplatMap);
        TEXTURE2D(_HoleMap); SAMPLER(sampler_HoleMap);

        // ════════════════════════════════════════════════════════════
        // TERRAIN BLENDING — Fixed Normalization Pipeline
        //
        // Stage A: Height bands (soft overlapping)
        // Stage B: Splat blend via lerp (bounded by definition)
        // Stage C: Cliff priority + normalize → guaranteed sum == 1.0
        // ════════════════════════════════════════════════════════════

        half4 NormalizeWeights(half4 w)
        {
            half totalSum = w.x + w.y + w.z + w.w;
            return w * rcp(max(totalSum, 0.001h));
        }

        // Shared hole alpha sampling — used by all passes
        half SampleHoleAlpha(float2 uv)
        {
            float2 holeUV = TRANSFORM_TEX(uv, _HoleMap);
            return SAMPLE_TEXTURE2D(_HoleMap, sampler_HoleMap, holeUV).r;
        }

        ENDHLSL

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 0: Forward Lit
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex TerrainVert
            #pragma fragment TerrainFrag

            #pragma shader_feature_local _USE_SPLATMAP
            #pragma shader_feature_local _USE_HOLEMAP
            #pragma shader_feature_local _ALPHATEST_ON

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

            struct Attributes
            {
                float4 positionOS  : POSITION;
                half3  normalOS    : NORMAL;
                float2 uv          : TEXCOORD0;
                float2 lightmapUV  : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                half3  normalWS    : TEXCOORD1;
                float2 uv          : TEXCOORD2;
                float2 lightmapUV  : TEXCOORD3;
                half   fogFactor   : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings TerrainVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionWS = posWS;
                o.positionCS = TransformWorldToHClip(posWS);
                o.normalWS   = (half3)TransformObjectToWorldNormal(input.normalOS);
                o.uv         = input.uv;
                o.lightmapUV = ToonTransformLightmapUV(input.lightmapUV);
                o.fogFactor  = (half)ComputeFogFactor(o.positionCS.z);

                return o;
            }

            half4 TerrainFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // ── Hole clip ──
                #if defined(_USE_HOLEMAP) || defined(_ALPHATEST_ON)
                {
                    half holeAlpha = SampleHoleAlpha(input.uv);
                    clip(holeAlpha - _HoleThreshold);
                }
                #endif

                half3 N = normalize(input.normalWS);
                float height = input.positionWS.y - _HeightOffset;

                // ── Stage A: Height-based layer weights ──
                half w0 = saturate(1.0h - saturate((height - _HeightLow) * _BlendSharpness * 0.1h));
                half w2 = saturate((height - _HeightMid) * _BlendSharpness * 0.1h);
                half w1 = max(1.0h - w0 - w2, 0.0h);

                half cliffMask = 1.0h - saturate((N.y - _CliffAngle) / (1.0h - _CliffAngle + 0.01h));

                // ── Stage B: Splat Map blend ──
                half splatCliffOverride = 0;

                #ifdef _USE_SPLATMAP
                {
                    float2 splatUV = TRANSFORM_TEX(input.uv, _SplatMap);
                    half4 splat = SAMPLE_TEXTURE2D(_SplatMap, sampler_SplatMap, splatUV);

                    half splatSum = splat.r + splat.g + splat.b + splat.a;
                    splat = (splatSum > 0.001h) ? splat / splatSum : half4(0.25h, 0.25h, 0.25h, 0.25h);

                    half4 sharpSplat = pow(splat, _SplatSharpness);
                    half sharpSum = sharpSplat.r + sharpSplat.g + sharpSplat.b + sharpSplat.a;
                    sharpSplat = (sharpSum > 0.001h) ? sharpSplat / sharpSum : splat;

                    w0 = lerp(w0, sharpSplat.r, _SplatInfluence);
                    w1 = lerp(w1, sharpSplat.g, _SplatInfluence);
                    w2 = lerp(w2, sharpSplat.b, _SplatInfluence);

                    splatCliffOverride = sharpSplat.a * _SplatInfluence;
                }
                #endif

                cliffMask = saturate(cliffMask + splatCliffOverride);

                // ── Stage C: Cliff priority + strict normalization ──
                half nonCliffBudget = 1.0h - cliffMask;
                half flatSum = w0 + w1 + w2;
                half flatScale = (flatSum > 0.001h) ? (nonCliffBudget / flatSum) : 0.0h;

                half4 weights = half4(w0 * flatScale, w1 * flatScale, w2 * flatScale, cliffMask);
                weights = NormalizeWeights(weights);

                // ── Sample all 4 layers ──
                float2 worldUV = input.positionWS.xz * _TexScale;
                half3 c0 = SAMPLE_TEXTURE2D(_Layer0, sampler_Layer0, worldUV).rgb * _Layer0Color.rgb;
                half3 c1 = SAMPLE_TEXTURE2D(_Layer1, sampler_Layer1, worldUV).rgb * _Layer1Color.rgb;
                half3 c2 = SAMPLE_TEXTURE2D(_Layer2, sampler_Layer2, worldUV).rgb * _Layer2Color.rgb;

                TriplanarUV tp = ComputeTriplanarUV(input.positionWS, N, _TriplanarScale, _TriplanarSharpness);
                half3 c3 = SampleTriplanar(TEXTURE2D_ARGS(_Layer3, sampler_Layer3), tp).rgb * _Layer3Color.rgb;

                half3 albedo = c0 * weights.x + c1 * weights.y + c2 * weights.z + c3 * weights.w;

                // ── Toon Lighting ──
                ToonLightingInput lightInput;
                lightInput.albedo      = albedo;
                lightInput.normalWS    = N;
                lightInput.positionWS  = input.positionWS;
                lightInput.positionCS  = input.positionCS;
                lightInput.lightmapUV  = input.lightmapUV;
                lightInput.shadowColor = _ShadowColor.rgb;
                lightInput.threshold   = _Threshold;
                lightInput.smoothness  = _Smoothness;

                ToonLightingOutput lit = ComputeToonLighting(lightInput);

                half3 finalColor = MixFog(lit.color, input.fogFactor);
                return half4(finalColor, 1.0h);
            }
            ENDHLSL
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 1: Shadow Caster
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex TerrainShadowVert
            #pragma fragment TerrainShadowFrag
            #pragma shader_feature_local _USE_HOLEMAP
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttr
            {
                float4 positionOS : POSITION;
                half3  normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVary
            {
                float4 positionCS : SV_POSITION;
                #if defined(_USE_HOLEMAP) || defined(_ALPHATEST_ON)
                    float2 uv     : TEXCOORD0;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            ShadowVary TerrainShadowVert(ShadowAttr input)
            {
                ShadowVary o = (ShadowVary)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 posWS  = TransformObjectToWorld(input.positionOS.xyz);
                float3 normWS = TransformObjectToWorldNormal(input.normalOS);

                o.positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, normWS, _LightDirection));

                #if UNITY_REVERSED_Z
                    o.positionCS.z = min(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    o.positionCS.z = max(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                #if defined(_USE_HOLEMAP) || defined(_ALPHATEST_ON)
                    o.uv = input.uv;
                #endif

                return o;
            }

            half4 TerrainShadowFrag(ShadowVary input) : SV_Target
            {
                #if defined(_USE_HOLEMAP) || defined(_ALPHATEST_ON)
                    half holeAlpha = SampleHoleAlpha(input.uv);
                    clip(holeAlpha - _HoleThreshold);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 2: Depth Only
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex TerrainDepthVert
            #pragma fragment TerrainDepthFrag
            #pragma shader_feature_local _USE_HOLEMAP
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing

            struct DepthAttr
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVary
            {
                float4 positionCS : SV_POSITION;
                #if defined(_USE_HOLEMAP) || defined(_ALPHATEST_ON)
                    float2 uv     : TEXCOORD0;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthVary TerrainDepthVert(DepthAttr input)
            {
                DepthVary o = (DepthVary)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);

                #if defined(_USE_HOLEMAP) || defined(_ALPHATEST_ON)
                    o.uv = input.uv;
                #endif

                return o;
            }

            half4 TerrainDepthFrag(DepthVary input) : SV_Target
            {
                #if defined(_USE_HOLEMAP) || defined(_ALPHATEST_ON)
                    half holeAlpha = SampleHoleAlpha(input.uv);
                    clip(holeAlpha - _HoleThreshold);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 3: Depth Normals (SSAO / screen-space shadows)
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            ColorMask RGBA

            HLSLPROGRAM
            #pragma vertex TerrainDNVert
            #pragma fragment TerrainDNFrag
            #pragma shader_feature_local _USE_HOLEMAP
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing

            struct DNAttr
            {
                float4 positionOS : POSITION;
                half3  normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DNVary
            {
                float4 positionCS : SV_POSITION;
                half3  normalWS   : TEXCOORD0;
                #if defined(_USE_HOLEMAP) || defined(_ALPHATEST_ON)
                    float2 uv     : TEXCOORD1;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DNVary TerrainDNVert(DNAttr input)
            {
                DNVary o = (DNVary)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.normalWS   = (half3)TransformObjectToWorldNormal(input.normalOS);

                #if defined(_USE_HOLEMAP) || defined(_ALPHATEST_ON)
                    o.uv = input.uv;
                #endif

                return o;
            }

            half4 TerrainDNFrag(DNVary input) : SV_Target
            {
                #if defined(_USE_HOLEMAP) || defined(_ALPHATEST_ON)
                    half holeAlpha = SampleHoleAlpha(input.uv);
                    clip(holeAlpha - _HoleThreshold);
                #endif

                half3 n = normalize(input.normalWS);
                return half4(n * 0.5h + 0.5h, 0);
            }
            ENDHLSL
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 4: Meta (lightmap baking)
        //
        // Full terrain blend pipeline + hole alpha for baker occlusion.
        // Baker uses _ALPHATEST_ON + _Cutoff + Meta alpha to decide
        // which texels are transparent for shadow ray tracing.
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "Meta"
            Tags { "LightMode" = "Meta" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex MetaVert
            #pragma fragment MetaFrag
            #pragma shader_feature_local _USE_SPLATMAP
            #pragma shader_feature_local _USE_HOLEMAP
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"

            struct MetaAttr
            {
                float4 positionOS : POSITION;
                half3  normalOS   : NORMAL;
                float2 uv0        : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                float2 uv2        : TEXCOORD2;
            };

            struct MetaVary
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
            };

            MetaVary MetaVert(MetaAttr input)
            {
                MetaVary o = (MetaVary)0;
                o.positionCS = UnityMetaVertexPosition(input.positionOS.xyz, input.uv1, input.uv2);
                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.normalWS   = (half3)TransformObjectToWorldNormal(input.normalOS);
                o.uv         = input.uv0;
                return o;
            }

            half4 MetaFrag(MetaVary input) : SV_Target
            {
                // ── Hole clip ──
                half holeAlpha = 1.0h;

                #if defined(_USE_HOLEMAP) || defined(_ALPHATEST_ON)
                {
                    holeAlpha = SampleHoleAlpha(input.uv);
                    clip(holeAlpha - _HoleThreshold);
                }
                #endif

                half3 N = normalize(input.normalWS);
                float height = input.positionWS.y - _HeightOffset;

                // ── Stage A: Height-based weights ──
                half w0 = saturate(1.0h - saturate((height - _HeightLow) * _BlendSharpness * 0.1h));
                half w2 = saturate((height - _HeightMid) * _BlendSharpness * 0.1h);
                half w1 = max(1.0h - w0 - w2, 0.0h);

                half cliffMask = 1.0h - saturate((N.y - _CliffAngle) / (1.0h - _CliffAngle + 0.01h));

                // ── Stage B: Splat map blend ──
                half splatCliffOverride = 0;

                #ifdef _USE_SPLATMAP
                {
                    float2 splatUV = TRANSFORM_TEX(input.uv, _SplatMap);
                    half4 splat = SAMPLE_TEXTURE2D(_SplatMap, sampler_SplatMap, splatUV);

                    half splatSum = splat.r + splat.g + splat.b + splat.a;
                    splat = (splatSum > 0.001h) ? splat / splatSum : half4(0.25h, 0.25h, 0.25h, 0.25h);

                    half4 sharpSplat = pow(splat, _SplatSharpness);
                    half sharpSum = sharpSplat.r + sharpSplat.g + sharpSplat.b + sharpSplat.a;
                    sharpSplat = (sharpSum > 0.001h) ? sharpSplat / sharpSum : splat;

                    w0 = lerp(w0, sharpSplat.r, _SplatInfluence);
                    w1 = lerp(w1, sharpSplat.g, _SplatInfluence);
                    w2 = lerp(w2, sharpSplat.b, _SplatInfluence);

                    splatCliffOverride = sharpSplat.a * _SplatInfluence;
                }
                #endif

                cliffMask = saturate(cliffMask + splatCliffOverride);

                // ── Stage C: Cliff priority + normalize ──
                half nonCliffBudget = 1.0h - cliffMask;
                half flatSum = w0 + w1 + w2;
                half flatScale = (flatSum > 0.001h) ? (nonCliffBudget / flatSum) : 0.0h;

                half4 weights = half4(w0 * flatScale, w1 * flatScale, w2 * flatScale, cliffMask);
                weights = NormalizeWeights(weights);

                // ── Sample all 4 layers (world-space UVs) ──
                float2 worldUV = input.positionWS.xz * _TexScale;
                half3 c0 = SAMPLE_TEXTURE2D(_Layer0, sampler_Layer0, worldUV).rgb * _Layer0Color.rgb;
                half3 c1 = SAMPLE_TEXTURE2D(_Layer1, sampler_Layer1, worldUV).rgb * _Layer1Color.rgb;
                half3 c2 = SAMPLE_TEXTURE2D(_Layer2, sampler_Layer2, worldUV).rgb * _Layer2Color.rgb;

                TriplanarUV tp = ComputeTriplanarUV(input.positionWS, N, _TriplanarScale, _TriplanarSharpness);
                half3 c3 = SampleTriplanar(TEXTURE2D_ARGS(_Layer3, sampler_Layer3), tp).rgb * _Layer3Color.rgb;

                half3 albedo = c0 * weights.x + c1 * weights.y + c2 * weights.z + c3 * weights.w;

                // ── Output to baker ──
                MetaInput metaInput = (MetaInput)0;
                metaInput.Albedo = albedo;
                metaInput.Emission = half3(0, 0, 0);

                half4 metaOut = UnityMetaFragment(metaInput);
                metaOut.a = holeAlpha;
                return metaOut;
            }
            ENDHLSL
        }
    }
    CustomEditor "ToonTerrainGUI"
}
