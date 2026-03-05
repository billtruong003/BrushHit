Shader "CleanRender/SimpleText"
{
    Properties
    {
        [MainTexture] _MainTex("Font Atlas (SDF)", 2D) = "white"{}
        _FaceColor("Face Color", Color) = (1, 1, 1, 1)

        [Header(SDF)]
        _SDFThreshold("SDF Threshold", Range(0, 1)) = 0.5
        _SDFSmoothness("SDF Smoothness", Range(0.001, 0.2)) = 0.05

        [Header(Outline)]
        [Toggle(_OUTLINE)] _UseOutline("Enable Outline", Float) = 0
        _OutlineColor("Outline Color", Color) = (0, 0, 0, 1)
        _OutlineWidth("Outline Width", Range(0, 0.3)) = 0.1

        [Header(Options)]
        [Toggle(_SCREEN_SPACE)] _ScreenSpace("Screen Space (Billboard)", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull [_Cull]

        Pass
        {
            Name "TextForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex TextVert
            #pragma fragment TextFrag
            #pragma multi_compile_instancing
            #pragma shader_feature_local _OUTLINE
            #pragma shader_feature_local _SCREEN_SPACE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _FaceColor;
                float _SDFThreshold;
                float _SDFSmoothness;
                half4 _OutlineColor;
                float _OutlineWidth;
            CBUFFER_END

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR; // per-vertex color from TMP
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings TextVert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);

                #ifdef _SCREEN_SPACE
                    // Billboard: face camera
                    float3 worldCenter = TransformObjectToWorld(float3(0, 0, 0));
                    float3 camRight = UNITY_MATRIX_V[0].xyz;
                    float3 camUp = UNITY_MATRIX_V[1].xyz;
                    float3 worldPos = worldCenter
                        + camRight * input.positionOS.x * unity_ObjectToWorld[0][0]
                        + camUp * input.positionOS.y * unity_ObjectToWorld[1][1];
                    o.positionCS = TransformWorldToHClip(worldPos);
                #else
                    o.positionCS = TransformWorldToHClip(TransformObjectToWorld(input.positionOS.xyz));
                #endif

                o.uv = input.uv;
                o.color = input.color;
                return o;
            }

            half4 TextFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float sdfSample = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a;

                // ── SDF → Alpha ──
                float faceMask = smoothstep(_SDFThreshold - _SDFSmoothness,
                                            _SDFThreshold + _SDFSmoothness, sdfSample);

                half4 result;

                #ifdef _OUTLINE
                    float outlineThreshold = _SDFThreshold - _OutlineWidth;
                    float outlineMask = smoothstep(outlineThreshold - _SDFSmoothness,
                                                    outlineThreshold + _SDFSmoothness, sdfSample);
                    // Color: outline where outlineMask > faceMask
                    half3 faceCol = _FaceColor.rgb * input.color.rgb;
                    half3 col = lerp(_OutlineColor.rgb, faceCol, faceMask);
                    result = half4(col, outlineMask * _FaceColor.a * input.color.a);
                #else
                    result = half4(_FaceColor.rgb * input.color.rgb, faceMask * _FaceColor.a * input.color.a);
                #endif

                clip(result.a - 0.01);
                return result;
            }
            ENDHLSL
        }
    }
}
