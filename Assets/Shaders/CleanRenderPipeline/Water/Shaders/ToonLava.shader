Shader "CleanRender/ToonLava"
{
    Properties
    {
        [Header(Lava Textures)]
        _MainTex("Main Lava Texture", 2D) = "white"{}
        _NoiseTex("Noise / Distort Texture", 2D) = "gray"{}

        [Header(Scrolling)]
        _Scale("Noise Scale", Float) = 1.0
        _MainScale("Main Texture Scale", Float) = 0.8
        _SpeedDistortX("Speed Distort X", Float) = 0.03
        _SpeedDistortY("Speed Distort Y", Float) = 0.02
        _SpeedMainX("Speed Main X", Float) = 0.01
        _SpeedMainY("Speed Main Y", Float) = 0.015
        _DistortionStrength("Distortion Strength", Range(0, 1)) = 0.2
        _VCDistortionStrength("Vertex Color Distortion", Range(0, 1)) = 0.3

        [Header(Colors)]
        _TintStart("Main Tint Start (cool)", Color) = (0.15, 0.05, 0.02, 1)
        _TintEnd("Main Tint End (hot)", Color) = (1, 0.35, 0.05, 1)
        _TintOffset("Tint Offset", Range(0, 3)) = 1.0
        _BrightnessUnder("Brightness Under Lava", Range(0, 5)) = 1.5

        [Header(Edge Glow)]
        _EdgeThickness("Edge Thickness", Range(0, 5)) = 1.0
        _EdgeSmoothness("Edge Smoothness", Range(0, 1)) = 0.1
        [HDR] _EdgeColor("Edge Color", Color) = (5, 1.5, 0.2, 1)
        _EdgeBrightness("Edge/Top Brightness", Range(0, 10)) = 3.0

        [Header(Top Glow)]
        _CutoffTop("Cutoff Top", Range(0, 1)) = 0.6
        _TopSmoothness("Top Smoothness", Range(0, 1)) = 0.05
        [HDR] _TopColor("Top Layer Tint", Color) = (3, 0.8, 0.1, 1)

        [Header(Waves)]
        _WaveAmount("Wave Amount", Range(0, 10)) = 2.0
        _WaveSpeed("Wave Speed", Range(0, 5)) = 1.0
        _WaveHeight("Wave Height", Range(0, 0.5)) = 0.05

        [Header(Cel Shading)]
        _ShadowColor("Shadow Color", Color) = (0.08, 0.02, 0.02, 1)
        _Threshold("Shadow Threshold", Range(0, 1)) = 0.3
        _Smoothness("Shadow Smoothness", Range(0.001, 0.5)) = 0.08
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            Name "LavaForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex LavaVert
            #pragma fragment LavaFrag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _NoiseTex_ST;
                float _Scale;
                float _MainScale;
                float _SpeedDistortX;
                float _SpeedDistortY;
                float _SpeedMainX;
                float _SpeedMainY;
                float _DistortionStrength;
                float _VCDistortionStrength;
                half4 _TintStart;
                half4 _TintEnd;
                float _TintOffset;
                float _BrightnessUnder;
                float _EdgeThickness;
                float _EdgeSmoothness;
                half4 _EdgeColor;
                float _EdgeBrightness;
                float _CutoffTop;
                float _TopSmoothness;
                half4 _TopColor;
                float _WaveAmount;
                float _WaveSpeed;
                float _WaveHeight;
                half4 _ShadowColor;
                float _Threshold;
                float _Smoothness;
            CBUFFER_END

            TEXTURE2D(_MainTex);    SAMPLER(sampler_MainTex);
            TEXTURE2D(_NoiseTex);   SAMPLER(sampler_NoiseTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
                float4 color : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings LavaVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);

                // Vertex wave
                float waveBase = (input.positionOS.x + input.positionOS.z) * _WaveAmount;
                float sineWave = sin(_Time.y * _WaveSpeed + waveBase) * _WaveHeight;
                sineWave *= input.color.r;
                input.positionOS.y += sineWave;

                posWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionWS = posWS;
                o.positionCS = TransformWorldToHClip(posWS);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.screenPos = ComputeScreenPos(o.positionCS);
                o.color = input.color;
                return o;
            }

            half4 LavaFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float time = _Time.y;
                float2 worldUV = input.positionWS.xz;

                // ── Noise layers ──
                float2 noiseUV1 = worldUV * _Scale + float2(_SpeedDistortX, _SpeedDistortY) * time;
                half noise1 = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, noiseUV1).r;

                float2 noiseUV2 = worldUV * _Scale * 0.5 + float2(_SpeedDistortX, _SpeedDistortY) * time * 0.7;
                half noise2 = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, noiseUV2).r;

                half combinedNoise = (noise1 + noise2) * 0.5;

                // ── Main texture with distortion ──
                float2 mainUV = worldUV * _MainScale + float2(_SpeedMainX, _SpeedMainY) * time;
                mainUV += combinedNoise * _DistortionStrength;
                mainUV += input.color.r * _VCDistortionStrength;

                half mainTex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, mainUV).r;

                half lavaValue = saturate(mainTex + combinedNoise);

                // ── Color ──
                half3 lavaColor = lerp(_TintStart.rgb, _TintEnd.rgb, lavaValue * _TintOffset) * _BrightnessUnder;

                // ── Depth intersection edge ──
                float2 screenUV = input.screenPos.xy / input.screenPos.w;
                #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                    screenUV = UnityStereoTransformScreenSpaceTex(screenUV);
                #endif

                float rawDepth = SampleSceneDepth(screenUV);
                float sceneDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float surfaceDepth = input.screenPos.w;

                float depthEdgeRaw = sceneDepth - surfaceDepth;
                float depthEdge = 1.0 - saturate(depthEdgeRaw / _EdgeThickness);

                // ── Edge glow with smoothness control ──
                // _EdgeSmoothness: 0 = hard/crispy step, 1 = soft/wide blend
                float invertedMain = 1.0 - mainTex;
                float edgeBlurAmount = _EdgeSmoothness * 0.5; // remap 0-1 to 0-0.5 range for smoothstep
                float texturedEdge = smoothstep(invertedMain - edgeBlurAmount, invertedMain + edgeBlurAmount + 0.001, depthEdge);

                half3 coloredEdge = texturedEdge * _EdgeColor.rgb * _EdgeBrightness;

                // ── Top glow with smoothness control ──
                // _TopSmoothness: 0 = hard cutoff, 1 = soft blend
                float topBlurAmount = _TopSmoothness * 0.25;
                float topMask = smoothstep(_CutoffTop - topBlurAmount, _CutoffTop + topBlurAmount + 0.001, mainTex);
                half3 topGlow = topMask * _TopColor.rgb * _EdgeBrightness;

                // ── Subtract edge+top from base to prevent overlap ──
                float subtractMask = saturate(1.0 - texturedEdge - topMask);
                lavaColor *= subtractMask;

                // ── Add glowing parts ──
                lavaColor += coloredEdge + topGlow;

                // ── Vertex color fade ──
                lavaColor *= lerp(0.3, 1.0, input.color.r);

                // ── Cel shading ──
                float3 N = normalize(input.normalWS);
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float NdotL = dot(N, mainLight.direction);
                float celI = smoothstep(_Threshold - _Smoothness, _Threshold + _Smoothness,
                    NdotL * mainLight.shadowAttenuation);
                lavaColor *= lerp(_ShadowColor.rgb, mainLight.color, celI);

                float alpha = saturate(0.95 + texturedEdge * 0.05 + topMask * 0.05);

                return half4(lavaColor, alpha);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma vertex SV
            #pragma fragment SF
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            struct A { float4 p : POSITION; float3 n : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 p : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            V SV(A i)
            {
                V o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 ws = TransformObjectToWorld(i.p.xyz);
                float3 wn = TransformObjectToWorldNormal(i.n);
                o.p = TransformWorldToHClip(ApplyShadowBias(ws, wn, _LightDirection));
                return o;
            }
            half4 SF(V i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    CustomEditor "ToonLavaGUI"
}
