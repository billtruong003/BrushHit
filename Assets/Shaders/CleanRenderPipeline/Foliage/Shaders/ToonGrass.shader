Shader "CleanRender/ToonGrass"
{
    Properties
    {
        [MainTexture] _BaseMap("Grass Texture", 2D) = "white"{}
        _BaseColor("Base Color (Bottom)", Color) = (0.2, 0.4, 0.1, 1)
        _TipColor("Tip Color (Top)", Color) = (0.5, 0.85, 0.2, 1)

        [Header(Wind)]
        _WindTex("Wind Noise Tex", 2D) = "gray"{}
        _WindScale("Wind Scale", Float) = 0.08
        _WindSpeed("Wind Speed", Float) = 1.5
        _WindStrength("Wind Strength", Range(0, 1.5)) = 0.4

        [Header(Interaction)]
        [Toggle(_INTERACTIVE)] _Interactive("Enable Player Interaction", Float) = 1
        _InteractRadius("Interact Radius", Range(0, 5)) = 1.5
        _InteractStrength("Interact Bend Strength", Range(0, 2)) = 1.0

        [Header(Geometry)]
        _GrassHeight("Grass Height", Range(0.1, 3)) = 0.8
        _GrassWidth("Grass Width", Range(0.01, 0.5)) = 0.1

        [Header(Cel Shading)]
        _ShadowColor("Shadow Color", Color) = (0.1, 0.2, 0.05, 1)
        _Threshold("Shadow Threshold", Range(0, 1)) = 0.4

        _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.3
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "AlphaTest"
        }
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Assets/Shaders/CleanRenderPipeline/Core/Shaders/Includes/InstancingCore.hlsl"
        #include "Assets/Shaders/CleanRenderPipeline/Core/Shaders/Includes/NoiseLib.hlsl"

        // ── Shared Data for All Passes ──

        #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
            StructuredBuffer<CompressedInstanceData> _SourceData;
            StructuredBuffer<uint> _VisibleIndices;
        #endif

        // Interaction: up to 8 interactors
        float4 _InteractorPositions[8]; // xyz = position, w = radius
        int _InteractorCount;

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _TipColor;
            float _WindScale;
            float _WindSpeed;
            float _WindStrength;
            float _InteractRadius;
            float _InteractStrength;
            float _GrassHeight;
            float _GrassWidth;
            half4 _ShadowColor;
            float _Threshold;
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
        ENDHLSL

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 0: Forward Lit
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "GrassForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex GrassVert
            #pragma fragment GrassFrag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:Setup
            #pragma shader_feature_local _INTERACTIVE

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
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float2 uv : TEXCOORD0;
                float heightGradient : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings GrassVert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                float heightMask = saturate(input.positionOS.y / _GrassHeight);
                o.heightGradient = heightMask;

                // ── Wind ──
                float3 wind = SampleWind(TEXTURE2D_ARGS(_WindTex, sampler_WindTex),
                    posWS, _WindScale, _WindSpeed, _WindStrength, _Time.y);
                posWS += wind * heightMask;

                // ── Player Interaction ──
                #ifdef _INTERACTIVE
                for (int i = 0; i < _InteractorCount; i++)
                {
                    float3 interactPos = _InteractorPositions[i].xyz;
                    float radius = _InteractorPositions[i].w;
                    if (radius <= 0) continue;

                    float3 diff = posWS - interactPos;
                    float dist = length(diff.xz);
                    float influence = saturate(1.0 - dist / radius) * heightMask * _InteractStrength;

                    // Bend away from interactor
                    float2 bendDir = normalize(diff.xz + 0.001);
                    posWS.xz += bendDir * influence * 0.5;
                    posWS.y -= influence * 0.3; // press down
                }
                #endif

                o.positionWS = posWS;
                o.positionCS = TransformWorldToHClip(posWS);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return o;
            }

            half4 GrassFrag(Varyings input) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                clip(tex.a - _Cutoff);

                // Height gradient color
                half3 color = lerp(_BaseColor.rgb, _TipColor.rgb, input.heightGradient) * tex.rgb;

                // Simple cel shading
                float3 N = normalize(input.normalWS);
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float NdotL = dot(N, mainLight.direction);
                float intensity = smoothstep(_Threshold - 0.05, _Threshold + 0.05,
                                             NdotL * mainLight.shadowAttenuation);
                color *= lerp(_ShadowColor.rgb, mainLight.color, intensity);

                // Ambient
                color += SampleSH(N) * color * 0.3;

                return half4(color, 1);
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
            ZWrite On ZTest LEqual Cull Off

            HLSLPROGRAM
            #pragma vertex SV
            #pragma fragment SF
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:Setup

            float3 _LightDirection;

            struct A { float4 p:POSITION; float3 n:NORMAL; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };

            V SV(A i)
            {
                V o;
                UNITY_SETUP_INSTANCE_ID(i);
                
                // Recalculate wind/bend for shadow to match geometry
                // (Simplified for performance: basic wind only)
                float3 ws = TransformObjectToWorld(i.p.xyz);
                float heightMask = saturate(i.p.y / _GrassHeight);

                float3 wind = SampleWind(TEXTURE2D_ARGS(_WindTex, sampler_WindTex),
                    ws, _WindScale, _WindSpeed, _WindStrength, _Time.y);
                ws += wind * heightMask;

                float3 wn = TransformObjectToWorldNormal(i.n);
                o.p = TransformWorldToHClip(ApplyShadowBias(ws, wn, _LightDirection));
                o.uv = TRANSFORM_TEX(i.uv, _BaseMap);
                return o;
            }

            half4 SF(V i):SV_Target
            {
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).a - _Cutoff);
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
            ZWrite On ColorMask R Cull Off

            HLSLPROGRAM
            #pragma vertex DOV
            #pragma fragment DOF
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:Setup

            struct A { float4 p:POSITION; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };

            V DOV(A i)
            {
                V o;
                UNITY_SETUP_INSTANCE_ID(i);
                float3 ws = TransformObjectToWorld(i.p.xyz);
                // Apply wind here too!
                float heightMask = saturate(i.p.y / _GrassHeight);
                float3 wind = SampleWind(TEXTURE2D_ARGS(_WindTex, sampler_WindTex),
                    ws, _WindScale, _WindSpeed, _WindStrength, _Time.y);
                ws += wind * heightMask;

                o.p = TransformWorldToHClip(ws);
                o.uv = TRANSFORM_TEX(i.uv, _BaseMap);
                return o;
            }

            half4 DOF(V i):SV_Target
            {
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}