Shader "CleanRender/CaveFog"
{
    Properties
    {
        [Header(Fog Appearance)]
        _FogColor("Fog Color", Color) = (0.15, 0.12, 0.2, 0.9)
        _FogDensity("Fog Density", Range(0, 3)) = 1.0
        _NoiseTex("Noise Texture", 2D) = "gray"{}
        _NoiseScale("Noise Scale", Float) = 0.3
        _NoiseSpeed("Noise Scroll Speed", Vector) = (0.02, 0.01, 0, 0)
        _NoiseStrength("Noise Distortion", Range(0, 0.5)) = 0.15

        [Header(Distance Fade)]
        _FadeStart("Fade Start Distance", Float) = 15
        _FadeEnd("Fade End Distance (disappear)", Float) = 5
        _SoftEdge("Soft Edge Width", Range(0, 5)) = 1.5

        [Header(Depth)]
        _DepthFade("Depth Softness", Range(0, 5)) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+100"
            "RenderPipeline" = "UniversalPipeline"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            Name "CaveFog"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex FogVert
            #pragma fragment FogFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _FogColor;
                float _FogDensity;
                float4 _NoiseTex_ST;
                float _NoiseScale;
                float4 _NoiseSpeed;
                float _NoiseStrength;
                float _FadeStart;
                float _FadeEnd;
                float _SoftEdge;
                float _DepthFade;
            CBUFFER_END

            TEXTURE2D(_NoiseTex); SAMPLER(sampler_NoiseTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD1;
                float2 uv : TEXCOORD0;
                float4 screenPos : TEXCOORD2;
                float distToCamera : TEXCOORD3;
            };

            Varyings FogVert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);

                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.uv = input.uv;
                o.screenPos = ComputeScreenPos(o.positionCS);
                o.distToCamera = distance(o.positionWS, GetCameraPositionWS());
                return o;
            }

            half4 FogFrag(Varyings input) : SV_Target
            {
                // ── Distance Fade ──
                // Fog fades as camera approaches, disappears completely at FadeEnd
                float distFade = saturate((input.distToCamera - _FadeEnd) / max(_FadeStart - _FadeEnd, 0.01));

                // If completely faded out, discard early
                if (distFade < 0.001) discard;

                // ── Noise Animation ──
                float2 noiseUV = input.positionWS.xz * _NoiseScale + _NoiseSpeed.xy * _Time.y;
                float noise = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, noiseUV).r;

                // Second layer
                float2 noiseUV2 = input.positionWS.xz * _NoiseScale * 1.7 - _NoiseSpeed.xy * _Time.y * 0.6;
                float noise2 = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, noiseUV2).r;

                float noiseFinal = (noise + noise2) * 0.5;

                // ── Depth Soft Edge ──
                float2 screenUV = input.screenPos.xy / input.screenPos.w;
                float sceneDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float fragDepth = input.screenPos.w;
                float depthDiff = sceneDepth - fragDepth;
                float depthFade = saturate(depthDiff / _DepthFade);

                // ── Combine Alpha ──
                float alpha = _FogColor.a * _FogDensity;
                alpha *= distFade;           // Distance fade
                alpha *= depthFade;          // Soft edge against geometry
                alpha *= lerp(1.0 - _NoiseStrength, 1.0, noiseFinal); // Noise variation

                // Soft edge on UV borders (for box/plane meshes)
                float2 edgeUV = abs(input.uv * 2.0 - 1.0);
                float edgeFade = saturate((1.0 - max(edgeUV.x, edgeUV.y)) / (_SoftEdge * 0.1 + 0.01));
                alpha *= edgeFade;

                return half4(_FogColor.rgb, saturate(alpha));
            }
            ENDHLSL
        }
    }
}
