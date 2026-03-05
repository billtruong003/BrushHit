// ============================================================================
// STYLIZED SKYBOX SHADER - VR OPTIMIZED v3
// Based on MinionsArt's Stylized Skybox
//
// v3 FIX: Precision artifacts ("lỗ rổ vỡ hình")
// ────────────────────────────────────────────────
// Nguyên nhân: v2 dùng half cho MỌI THỨ kể cả geometry calculations.
//
// half = float16: range ±65504, precision ~3 decimal digits
// float = float32: range ±3.4e38, precision ~7 decimal digits
//
// Skybox mesh là CUBE với vertices ở corners như (1,1,1), (1,-1,1)...
// Khi normalize(half3(1,1,1)):
//   magnitude = sqrt(3) = 1.732...
//   half precision sqrt(3) ≈ 1.732 (OK)
//   NHƯNG 1.0/1.732 = 0.57735... → half rounds → 0.5771 hoặc 0.5776
//   → Sai số ~0.0003 mỗi component
//   → Ở VIỀN giữa 2 cube faces, 2 vertices kề nhau cho kết quả
//     normalize khác nhau → GPU interpolate ra giá trị NHẢY/GIÁN ĐOẠN
//     → Hiện lên thành đường nứt, lỗ rổ
//
// Tương tự, skyUV = worldPos.xz / abs(worldPos.y):
//   Gần horizon, worldPos.y → 0 → 1/y → cực lớn
//   half max = 65504 → UV overflow → texture wrap sai → lỗ rổ
//
// GIẢI PHÁP: Quy tắc vàng cho VR shaders:
//   ✓ float cho GEOMETRY: positions, directions, normals, UVs, normalize()
//   ✓ half cho COLORS: lerp factors, color values, alpha, cutoffs
//   Trên PC GPU: half thường auto-promote lên float → không mất gì
//   Trên Mobile GPU (Quest): chỉ color ALU được lợi từ half, geometry
//   pipeline luôn chạy float anyway
// ============================================================================

Shader "VR/StylizedSkybox"
{
    Properties
    {
        // ── SUN ──
        [HDR]_SunColor("Sun Color", Color) = (1, 1, 1, 1)
        _SunRadius("Sun Radius", Range(0.001, 0.5)) = 0.1
        _SunSharpness("Sun Edge Sharpness", Range(1, 100)) = 50

        // ── MOON ──
        [HDR]_MoonColor("Moon Color", Color) = (0.9, 0.95, 1, 1)
        _MoonRadius("Moon Radius", Range(0.001, 0.5)) = 0.15
        _MoonSharpness("Moon Edge Sharpness", Range(1, 100)) = 50
        _MoonOffset("Moon Crescent Offset", Vector) = (0.25, 0.5, 0.5, 0)

        // ── SKY ──
        [HDR]_DayTopColor("Day Sky Top", Color) = (0.4, 1, 1, 1)
        [HDR]_DayBottomColor("Day Sky Bottom", Color) = (0, 0.8, 1, 1)
        [HDR]_NightTopColor("Night Sky Top", Color) = (0, 0, 0, 1)
        [HDR]_NightBottomColor("Night Sky Bottom", Color) = (0, 0, 0.2, 1)

        // ── HORIZON ──
        _OffsetHorizon("Horizon Offset", Range(-1, 1)) = 0
        _HorizonWidth("Horizon Width", Range(0.1, 10)) = 3.3
        [HDR]_HorizonColorDay("Day Horizon Color", Color) = (1, 0.7, 0.3, 1)
        [HDR]_HorizonColorNight("Night Horizon Color", Color) = (0, 0.1, 0.2, 1)

        // ── STARS ──
        [Toggle(_STARS_ON)] _EnableStars("Enable Stars", Float) = 1
        _Stars("Stars Texture", 2D) = "black" {}
        _StarsCutoff("Stars Cutoff", Range(0, 1)) = 0.08
        _StarsSpeed("Stars UV Scale", Range(0.01, 2)) = 0.3
        [HDR]_StarsSkyColor("Stars Tint", Color) = (1, 1, 1, 1)

        // ── CLOUDS MAIN ──
        [Toggle(_CLOUDS_ON)] _EnableClouds("Enable Clouds", Float) = 1
        _BaseNoise("Base Noise", 2D) = "black" {}
        _Distort("Distort Noise", 2D) = "black" {}
        _BaseNoiseScale("Base Noise Scale", Range(0.001, 1)) = 0.2
        _DistortScale("Cloud Detail Scale", Range(0.001, 1)) = 0.06
        _Distortion("Distortion Amount", Range(0, 1)) = 0.1
        _BaseNoiseSpeed("Base Noise Scroll", Vector) = (0.25, 0.5, 0, 0)
        _CloudsLayerSpeed("Cloud Scroll Speed", Vector) = (0.25, 0.5, 0, 0)
        _CloudCutoff("Cloud Cutoff", Range(0, 1)) = 0.3
        _Fuzziness("Cloud Softness", Range(0.001, 0.5)) = 0.04
        _HorizonCloudsFade("Horizon Fade (Min, Max)", Vector) = (0.25, 0.5, 0, 0)

        // ── CLOUDS SECONDARY ──
        [Toggle(_CLOUDS2_ON)] _EnableClouds2("Enable Secondary Layer", Float) = 1
        _SecNoise("Secondary Noise", 2D) = "black" {}
        _SecNoiseScale("Secondary Scale", Range(0.001, 1)) = 0.05
        _CloudCutoff2("Secondary Cutoff", Range(0, 1)) = 0.3
        _Fuzziness2("Secondary Softness", Range(0.001, 0.5)) = 0.04
        _OpacitySec("Secondary Opacity", Range(0, 1)) = 0.5

        // ── CLOUD COLORS ──
        _ColorStretch("Color Stretch", Range(-10, 10)) = 0.01
        _ColorOffset("Color Offset", Range(-10, 10)) = 0.04
        [HDR]_CloudColorDayEdge("Day Cloud Edge", Color) = (1, 1, 1, 1)
        [HDR]_CloudColorDayMain("Day Cloud Core", Color) = (0.8, 0.9, 0.8, 1)
        [HDR]_CloudColorNightEdge("Night Cloud Edge", Color) = (0, 0.5, 0.8, 1)
        [HDR]_CloudColorNightMain("Night Cloud Core", Color) = (0, 0.1, 0.3, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Background"
            "Queue" = "Background"
            "PreviewType" = "Skybox"
        }

        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // VR: Single Pass Instanced Stereo
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer

            // Feature toggles
            #pragma shader_feature_local _STARS_ON
            #pragma shader_feature_local _CLOUDS_ON
            #pragma shader_feature_local _CLOUDS2_ON

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // ══════════════════════════════════════════════════
            // v2f STRUCT - PRECISION STRATEGY:
            //
            //   float → geometry data (directions, UVs)
            //     Tại sao: Cube mesh interpolation cần 7 digits precision
            //     ở edges. half chỉ có 3 digits → seam cracks.
            //
            //   half → color data (pre-computed colors, blend factors)
            //     Tại sao: Màu sắc 0-1 (hoặc HDR 0-16) chỉ cần ~3 digits.
            //     Sai 0.001 trong color = invisible. Sai 0.001 trong UV = lỗ rổ.
            //
            //   Trên Adreno (Quest): half ALU = 2x throughput vs float
            //     → Dùng half cho colors vẫn được lợi performance
            //     → Geometry pipeline luôn float32 bất kể khai báo
            //     → Khai báo float cho geometry = đúng + không mất perf
            // ══════════════════════════════════════════════════
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 uv : TEXCOORD0;           // FLOAT: skybox view direction
                float4 skyUV_data : TEXCOORD1;   // FLOAT: xy=skyUV, z=fadeH, w=dnLerp
                half4 skyColor : TEXCOORD2;      // half OK: pre-blended color
                half4 cloudEdge : TEXCOORD3;     // half OK: color
                half4 cloudMain : TEXCOORD4;     // half OK: color
                float4 starData : TEXCOORD5;     // FLOAT: xy=UV, z=nightFactor

                UNITY_VERTEX_OUTPUT_STEREO
            };

            // ══════════════════════════════════════════════
            // UNIFORMS - float cho geometry, half cho colors
            // ══════════════════════════════════════════════

            sampler2D _Stars, _BaseNoise, _Distort, _SecNoise;

            half4 _SunColor, _MoonColor;
            float _SunRadius, _SunSharpness;
            float _MoonRadius, _MoonSharpness;
            float3 _MoonOffset;

            half4 _DayTopColor, _DayBottomColor;
            half4 _NightTopColor, _NightBottomColor;
            half4 _HorizonColorDay, _HorizonColorNight;
            float _HorizonWidth, _OffsetHorizon;
            float2 _HorizonCloudsFade;

            half _StarsCutoff;
            float _StarsSpeed;
            half4 _StarsSkyColor;

            float _BaseNoiseScale, _DistortScale, _SecNoiseScale;
            float _Distortion;
            half _CloudCutoff, _Fuzziness;
            half _CloudCutoff2, _Fuzziness2, _OpacitySec;
            half _ColorStretch, _ColorOffset;
            float2 _BaseNoiseSpeed, _CloudsLayerSpeed;

            half4 _CloudColorDayEdge, _CloudColorDayMain;
            half4 _CloudColorNightEdge, _CloudColorNightMain;

            // ══════════════════════════════════════════════
            // VERTEX SHADER
            // ══════════════════════════════════════════════
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);

                // View direction - FLOAT, sẽ normalize ở fragment
                o.uv = v.uv;

                // Sky UV - FLOAT precision cho division
                float3 wPos = mul(unity_ObjectToWorld, v.vertex).xyz;

                // Epsilon 0.01 thay vì 0.001:
                // 0.001 trong half = có thể round về 0 → div by zero → NaN → lỗ rổ
                // 0.01 trong float = an toàn, horizon vẫn smooth
                float rcpY = 1.0 / (abs(wPos.y) + 0.01);
                float2 skyUV = wPos.xz * rcpY;

                // Day/night + horizon fade
                float dnLerp = saturate(_WorldSpaceLightPos0.y + 0.5);
                float fadeH = saturate((abs(v.uv.y) - _HorizonCloudsFade.x)
                            / max(_HorizonCloudsFade.y - _HorizonCloudsFade.x, 0.001));

                o.skyUV_data = float4(skyUV, fadeH, dnLerp);

                // Pre-compute colors (half precision OK cho lerp kết quả)
                half hdn = (half)dnLerp;
                half t = saturate(v.uv.y);

                half4 nightSky = lerp(_NightBottomColor, _NightTopColor, t);
                half4 daySky = lerp(_DayBottomColor, _DayTopColor, t);
                half4 sky = lerp(nightSky, daySky, hdn);

                half horizon = saturate(1.0 - abs(v.uv.y * _HorizonWidth - _OffsetHorizon));
                half4 horizonCol = lerp(_HorizonColorNight, _HorizonColorDay, hdn);
                o.skyColor = lerp(sky, horizonCol, horizon);

                o.cloudEdge = lerp(_CloudColorNightEdge, _CloudColorDayEdge, hdn);
                o.cloudMain = lerp(_CloudColorNightMain, _CloudColorDayMain, hdn);

                #if defined(_STARS_ON)
                    o.starData = float4(skyUV, saturate(-_WorldSpaceLightPos0.y * 5.0), 0);
                #else
                    o.starData = float4(0, 0, 0, 0);
                #endif

                return o;
            }

            // ══════════════════════════════════════════════
            // FRAGMENT SHADER
            // ══════════════════════════════════════════════
            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float2 skyUV = i.skyUV_data.xy;     // FLOAT UV
                half fadeHorizon = i.skyUV_data.z;   // half blend OK
                half dnLerp = i.skyUV_data.w;        // half blend OK

                // ══════════════════════════════════════════════
                // SUN - normalize() PHẢI dùng FLOAT
                //
                // i.uv được interpolate giữa cube vertices:
                //   Face center: (0, 0, 1) → |v| = 1.0
                //   Edge midpoint: (1, 0, 1) → |v| = 1.414
                //   Corner: (1, 1, 1) → |v| = 1.732
                //
                // normalize cần rsqrt(dot(v,v)):
                //   rsqrt(3.0) = 0.577350...
                //   half rsqrt(3.0) ≈ 0.5771 hoặc 0.5776 (tùy rounding)
                //
                // Ở seam giữa 2 faces, pixel A lấy rsqrt từ vertex face1,
                // pixel B lấy từ vertex face2 → kết quả nhảy 0.0005
                // → viewDir bị discontinuous → dot product nhảy
                // → sunDisc flicker/crack → "lỗ rổ"
                //
                // float rsqrt(3.0) = 0.5773503 (7 digits) → seamless
                // ══════════════════════════════════════════════
                float3 viewDir = normalize(i.uv);
                float3 sunDir = _WorldSpaceLightPos0.xyz;

                // Sun disc
                float sunAngle = 1.0 - dot(viewDir, sunDir);
                half sunDisc = saturate((1.0 - sunAngle / _SunRadius) * _SunSharpness);

                // Moon disc
                float moonAngle = 1.0 - dot(viewDir, -sunDir);
                half moonDisc = saturate((1.0 - moonAngle / _MoonRadius) * _MoonSharpness);

                // Crescent
                float3 crescentDir = normalize(i.uv + _MoonOffset);
                float crescentAngle = 1.0 - dot(crescentDir, -sunDir);
                half crescentDisc = saturate((1.0 - crescentAngle / _MoonRadius) * _MoonSharpness);
                half crescent = saturate(moonDisc - crescentDisc);

                // ══════════════════════════════════════════════
                // SKY + SUN + MOON
                // lerp = replace sky with sun/moon color
                // ══════════════════════════════════════════════
                half4 fullSky = i.skyColor;
                fullSky = lerp(fullSky, _SunColor, sunDisc);
                fullSky = lerp(fullSky, _MoonColor, crescent);

                // ══════════════════════════════════════════════
                // CLOUDS (float UVs, half colors)
                // ══════════════════════════════════════════════
                half cloudsLerp = 0;
                half4 cloudsColor = 0;

                #if defined(_CLOUDS_ON)
                    float timeX = _Time.x;

                    float2 baseUV = (skyUV + timeX * _BaseNoiseSpeed) * _BaseNoiseScale;
                    half baseN = tex2D(_BaseNoise, baseUV).r;

                    float2 c1UV = (skyUV + (float)baseN * _Distortion + timeX * _CloudsLayerSpeed) * _DistortScale;
                    half c1 = tex2D(_Distort, c1UV).r;

                    half cut1 = saturate(smoothstep(_CloudCutoff, _CloudCutoff + _Fuzziness, c1));

                    half cStretch = saturate(c1 * _ColorStretch + _ColorOffset);
                    cloudsColor = lerp(i.cloudEdge, i.cloudMain, cStretch);

                    half cut2 = 0;
                    #if defined(_CLOUDS2_ON)
                        float2 c2UV = (skyUV + (float)c1 + timeX * _CloudsLayerSpeed * 0.5) * _SecNoiseScale;
                        half c2 = tex2D(_SecNoise, c2UV).r;
                        cut2 = saturate(smoothstep(_CloudCutoff2, _CloudCutoff2 + _Fuzziness2, c2)) * _OpacitySec;
                    #endif

                    cloudsLerp = saturate(cut2 * fadeHorizon + cut1 * fadeHorizon * 2.0h);
                #endif

                // ══════════════════════════════════════════════
                // STARS
                // ══════════════════════════════════════════════
                half4 starsCol = 0;
                #if defined(_STARS_ON)
                    float2 sUV = (i.starData.xy + sunDir.xz * 0.5) * _StarsSpeed;
                    half4 sTex = tex2D(_Stars, sUV);
                    sTex *= (half)i.starData.z;
                    starsCol = step(_StarsCutoff, sTex) * _StarsSkyColor * (1.0h - moonDisc);
                #endif

                // ══════════════════════════════════════════════
                // FINAL
                // ══════════════════════════════════════════════
                return lerp(fullSky + starsCol, cloudsColor, cloudsLerp);
            }
            ENDCG
        }
    }

    CustomEditor "StylizedSkyboxGUI"
    Fallback Off
}
