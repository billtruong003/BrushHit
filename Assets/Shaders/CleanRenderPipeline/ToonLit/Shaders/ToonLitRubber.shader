Shader "CleanRender/ToonLitRubber"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white"{}
        [MainColor]  _BaseColor("Base Color", Color) = (1, 1, 1, 1)

        [Header(Toon Lighting)]
        _ShadowColor("Shadow Color", Color) = (0.3, 0.3, 0.4, 1)
        _Threshold("Shadow Threshold", Range(0, 1)) = 0.5
        _Smoothness("Shadow Smoothness", Range(0.001, 0.5)) = 0.05
        _RimColor("Rim Color", Color) = (1, 1, 1, 0.5)
        _RimPower("Rim Power", Range(0.1, 10)) = 3

        [Header(Player Interaction)]
        _InteractRadius("Interact Radius", Float) = 1.5
        _PushStrength("Push Strength (XZ)", Float) = 0.4
        _SquishStrength("Squish Strength (Y)", Float) = 0.3
        _BendFalloff("Bend Falloff", Range(0.5, 5)) = 2.0

        [Header(Touched Color)]
        _TouchedColor("Touched Color", Color) = (0, 1, 0, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        LOD 200

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 0: Forward Lit
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex RubberVert
            #pragma fragment RubberFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/Shaders/CleanRenderPipeline/Core/Shaders/Includes/ToonLighting.hlsl"

            // ── Per-instance data via MaterialPropertyBlock (WebGL safe) ──
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _InstPosTouch)    // xyz=pos, w=touched
                UNITY_DEFINE_INSTANCED_PROP(float4, _InstDataSpring)  // xyz=scale, w=spring
            UNITY_INSTANCING_BUFFER_END(Props)

            // ── Player positions (global) ──
            float4 _PlayerPos0;
            float4 _PlayerPos1;
            float4 _PlayerPos2;
            float  _PlayerPartCount;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half4  _ShadowColor;
                half4  _RimColor;
                float  _Threshold;
                float  _Smoothness;
                float  _RimPower;
                float  _InteractRadius;
                float  _PushStrength;
                float  _SquishStrength;
                float  _BendFalloff;
                half4  _TouchedColor;
            CBUFFER_END

            TEXTURE2D(_BaseMap);  SAMPLER(sampler_BaseMap);

            // ── XZ Push helpers ──
            void CalcPointPush(float3 center, float3 partPos, float hRatio, inout float2 pushXZ)
            {
                float2 delta = center.xz - partPos.xz;
                float dist = length(delta);
                if (dist >= _InteractRadius) return;
                float t = pow(1.0 - saturate(dist / _InteractRadius), _BendFalloff);
                float2 dir = (dist > 0.001) ? normalize(delta) : float2(1, 0);
                pushXZ += dir * t * _PushStrength * hRatio;
            }

            void CalcSegmentPush(float3 center, float3 segA, float3 segB, float hRatio, inout float2 pushXZ)
            {
                float2 ab = segB.xz - segA.xz;
                float abLenSq = dot(ab, ab);
                float tProj = (abLenSq > 0.0001) ? saturate(dot(center.xz - segA.xz, ab) / abLenSq) : 0.0;
                float2 closest = segA.xz + tProj * ab;
                float2 delta = center.xz - closest;
                float dist = length(delta);
                if (dist >= _InteractRadius) return;
                float t = pow(1.0 - saturate(dist / _InteractRadius), _BendFalloff);
                float2 dir = (dist > 0.001) ? normalize(delta) : float2(1, 0);
                pushXZ += dir * t * _PushStrength * hRatio;
            }

            float3 ApplyInteraction(float3 worldPos, float3 instCenter, float3 localPos, float meshH, float springVal)
            {
                float hRatio = saturate(localPos.y / max(meshH, 0.001));

                float2 pushXZ = float2(0, 0);
                if (_PlayerPartCount >= 1) CalcPointPush(instCenter, _PlayerPos0.xyz, hRatio, pushXZ);
                if (_PlayerPartCount >= 2) CalcPointPush(instCenter, _PlayerPos1.xyz, hRatio, pushXZ);
                if (_PlayerPartCount >= 3) CalcSegmentPush(instCenter, _PlayerPos0.xyz, _PlayerPos1.xyz, hRatio, pushXZ);
                worldPos.xz += pushXZ;

                worldPos.y -= springVal * _SquishStrength * hRatio;
                return worldPos;
            }

            struct Attributes
            {
                float4 positionOS  : POSITION;
                float3 normalOS    : NORMAL;
                float2 uv          : TEXCOORD0;
                float2 lightmapUV  : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD1;
                half3  normalWS    : TEXCOORD2;
                float4 uv          : TEXCOORD0;
                half   fogFactor   : TEXCOORD3;
                float  touched     : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings RubberVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float4 posTouch = UNITY_ACCESS_INSTANCED_PROP(Props, _InstPosTouch);
                float4 dataSpring = UNITY_ACCESS_INSTANCED_PROP(Props, _InstDataSpring);

                float3 instCenter = posTouch.xyz;
                float springVal = dataSpring.w;

                float3 worldPos = TransformObjectToWorld(input.positionOS.xyz);
                worldPos = ApplyInteraction(worldPos, instCenter, input.positionOS.xyz, 1.0, springVal);

                o.positionWS = worldPos;
                o.positionCS = TransformWorldToHClip(worldPos);
                o.normalWS   = (half3)TransformObjectToWorldNormal(input.normalOS);
                o.uv.xy      = TRANSFORM_TEX(input.uv, _BaseMap);
                o.uv.zw      = ToonTransformLightmapUV(input.lightmapUV);
                o.fogFactor   = (half)ComputeFogFactor(o.positionCS.z);
                o.touched     = posTouch.w;

                return o;
            }

            half4 RubberFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv.xy);
                half3 colorTint = lerp(_BaseColor.rgb, _TouchedColor.rgb, input.touched);
                half3 albedo = baseTex.rgb * colorTint;

                half3  normalWS  = normalize(input.normalWS);
                float3 viewDirWS = normalize(GetCameraPositionWS() - input.positionWS);

                ToonLightResult lit = ComputeToonMainLight(
                    input.positionWS, normalWS, viewDirWS, albedo, input.uv.zw,
                    _Threshold, _Smoothness, _ShadowColor.rgb,
                    _RimColor.rgb, _RimPower, _RimColor.a, 1.0h);

                half3 finalColor = lit.diffuse + lit.rim + lit.globalIllumination;

                #if defined(_ADDITIONAL_LIGHTS)
                    finalColor += ComputeToonAdditionalLights(
                        input.positionWS, normalWS, albedo, _Threshold, _Smoothness);
                #endif

                finalColor = MixFog(finalColor, input.fogFactor);
                return half4(finalColor, 1.0h);
            }
            ENDHLSL
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 1: Shadow Caster
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _InstPosTouch)
                UNITY_DEFINE_INSTANCED_PROP(float4, _InstDataSpring)
            UNITY_INSTANCING_BUFFER_END(Props)

            float4 _PlayerPos0;
            float4 _PlayerPos1;
            float4 _PlayerPos2;
            float  _PlayerPartCount;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half4  _ShadowColor;
                half4  _RimColor;
                float  _Threshold;
                float  _Smoothness;
                float  _RimPower;
                float  _InteractRadius;
                float  _PushStrength;
                float  _SquishStrength;
                float  _BendFalloff;
                half4  _TouchedColor;
            CBUFFER_END

            void CalcPointPush(float3 center, float3 partPos, float hRatio, inout float2 pushXZ)
            {
                float2 delta = center.xz - partPos.xz;
                float dist = length(delta);
                if (dist >= _InteractRadius) return;
                float t = pow(1.0 - saturate(dist / _InteractRadius), _BendFalloff);
                float2 dir = (dist > 0.001) ? normalize(delta) : float2(1, 0);
                pushXZ += dir * t * _PushStrength * hRatio;
            }

            void CalcSegmentPush(float3 center, float3 segA, float3 segB, float hRatio, inout float2 pushXZ)
            {
                float2 ab = segB.xz - segA.xz;
                float abLenSq = dot(ab, ab);
                float tProj = (abLenSq > 0.0001) ? saturate(dot(center.xz - segA.xz, ab) / abLenSq) : 0.0;
                float2 closest = segA.xz + tProj * ab;
                float2 delta = center.xz - closest;
                float dist = length(delta);
                if (dist >= _InteractRadius) return;
                float t = pow(1.0 - saturate(dist / _InteractRadius), _BendFalloff);
                float2 dir = (dist > 0.001) ? normalize(delta) : float2(1, 0);
                pushXZ += dir * t * _PushStrength * hRatio;
            }

            float3 ApplyInteraction(float3 worldPos, float3 instCenter, float3 localPos, float meshH, float springVal)
            {
                float hRatio = saturate(localPos.y / max(meshH, 0.001));
                float2 pushXZ = float2(0, 0);
                if (_PlayerPartCount >= 1) CalcPointPush(instCenter, _PlayerPos0.xyz, hRatio, pushXZ);
                if (_PlayerPartCount >= 2) CalcPointPush(instCenter, _PlayerPos1.xyz, hRatio, pushXZ);
                if (_PlayerPartCount >= 3) CalcSegmentPush(instCenter, _PlayerPos0.xyz, _PlayerPos1.xyz, hRatio, pushXZ);
                worldPos.xz += pushXZ;
                worldPos.y -= springVal * _SquishStrength * hRatio;
                return worldPos;
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float3 _LightDirection;

            Varyings ShadowVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 instCenter = UNITY_ACCESS_INSTANCED_PROP(Props, _InstPosTouch).xyz;
                float springVal = UNITY_ACCESS_INSTANCED_PROP(Props, _InstDataSpring).w;

                float3 worldPos = TransformObjectToWorld(input.positionOS.xyz);
                worldPos = ApplyInteraction(worldPos, instCenter, input.positionOS.xyz, 1.0, springVal);

                float3 normWS = TransformObjectToWorldNormal(input.normalOS);
                o.positionCS = TransformWorldToHClip(ApplyShadowBias(worldPos, normWS, _LightDirection));

                #if UNITY_REVERSED_Z
                    o.positionCS.z = min(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    o.positionCS.z = max(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return o;
            }

            half4 ShadowFrag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // PASS 2: Depth Only
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _InstPosTouch)
                UNITY_DEFINE_INSTANCED_PROP(float4, _InstDataSpring)
            UNITY_INSTANCING_BUFFER_END(Props)

            float4 _PlayerPos0;
            float4 _PlayerPos1;
            float4 _PlayerPos2;
            float  _PlayerPartCount;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half4  _ShadowColor;
                half4  _RimColor;
                float  _Threshold;
                float  _Smoothness;
                float  _RimPower;
                float  _InteractRadius;
                float  _PushStrength;
                float  _SquishStrength;
                float  _BendFalloff;
                half4  _TouchedColor;
            CBUFFER_END

            void CalcPointPush(float3 center, float3 partPos, float hRatio, inout float2 pushXZ)
            {
                float2 delta = center.xz - partPos.xz;
                float dist = length(delta);
                if (dist >= _InteractRadius) return;
                float t = pow(1.0 - saturate(dist / _InteractRadius), _BendFalloff);
                float2 dir = (dist > 0.001) ? normalize(delta) : float2(1, 0);
                pushXZ += dir * t * _PushStrength * hRatio;
            }

            void CalcSegmentPush(float3 center, float3 segA, float3 segB, float hRatio, inout float2 pushXZ)
            {
                float2 ab = segB.xz - segA.xz;
                float abLenSq = dot(ab, ab);
                float tProj = (abLenSq > 0.0001) ? saturate(dot(center.xz - segA.xz, ab) / abLenSq) : 0.0;
                float2 closest = segA.xz + tProj * ab;
                float2 delta = center.xz - closest;
                float dist = length(delta);
                if (dist >= _InteractRadius) return;
                float t = pow(1.0 - saturate(dist / _InteractRadius), _BendFalloff);
                float2 dir = (dist > 0.001) ? normalize(delta) : float2(1, 0);
                pushXZ += dir * t * _PushStrength * hRatio;
            }

            float3 ApplyInteraction(float3 worldPos, float3 instCenter, float3 localPos, float meshH, float springVal)
            {
                float hRatio = saturate(localPos.y / max(meshH, 0.001));
                float2 pushXZ = float2(0, 0);
                if (_PlayerPartCount >= 1) CalcPointPush(instCenter, _PlayerPos0.xyz, hRatio, pushXZ);
                if (_PlayerPartCount >= 2) CalcPointPush(instCenter, _PlayerPos1.xyz, hRatio, pushXZ);
                if (_PlayerPartCount >= 3) CalcSegmentPush(instCenter, _PlayerPos0.xyz, _PlayerPos1.xyz, hRatio, pushXZ);
                worldPos.xz += pushXZ;
                worldPos.y -= springVal * _SquishStrength * hRatio;
                return worldPos;
            }

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

            Varyings DepthVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 instCenter = UNITY_ACCESS_INSTANCED_PROP(Props, _InstPosTouch).xyz;
                float springVal = UNITY_ACCESS_INSTANCED_PROP(Props, _InstDataSpring).w;

                float3 worldPos = TransformObjectToWorld(input.positionOS.xyz);
                worldPos = ApplyInteraction(worldPos, instCenter, input.positionOS.xyz, 1.0, springVal);
                o.positionCS = TransformWorldToHClip(worldPos);
                return o;
            }

            half4 DepthFrag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
