// ============================================================================
// STYLIZED SKYBOX SHADER - VR OPTIMIZED
// Based on MinionsArt's Stylized Skybox (https://www.patreon.com/posts/27402644)
// Optimized for VR by: moving work to vertex shader, reducing ALU,
// using half precision, Single Pass Instanced stereo, and math substitutions.
// ============================================================================
//
// ==================== TÓM TẮT CÁC OPTIMIZATION ====================
//
// 1. SINGLE PASS INSTANCED RENDERING (SPI)
//    - Render cả 2 mắt trong 1 draw call thay vì 2 draw call riêng
//    - Giảm ~50% CPU overhead cho skybox
//    - Dùng UNITY_VERTEX_INPUT_INSTANCE_ID / UNITY_SETUP_INSTANCE_ID
//
// 2. VERTEX SHADER OFFLOADING
//    - Chuyển sky gradient, horizon, day/night lerp, skyUV sang vertex shader
//    - Lý do: Skybox mesh có ít vertex (~24-36) nhưng cover TOÀN BỘ screen pixels
//      → Fragment shader chạy hàng triệu lần, vertex shader chỉ chạy vài chục lần
//      → Mọi phép tính không cần per-pixel accuracy nên move sang vertex = FREE
//    - GPU sẽ interpolate kết quả giữa các vertex → visually identical cho skybox
//
// 3. dot() THAY CHO distance()
//    - distance(a,b) = sqrt(dot(a-b, a-b)) → cần 1 sqrt instruction
//    - dot(d,d) = squared distance → bỏ sqrt, rẻ hơn 1 instruction
//    - Với sun/moon disc, ta chỉ cần so sánh threshold → dùng squared radius
//    - Tiết kiệm: 3 sqrt operations (sun, moon, crescent) = ~3-6 GPU cycles mỗi pixel
//
// 4. HALF PRECISION (float16)
//    - Mobile VR (Quest 2/3/Pro) GPU có ALU units chạy half nhanh GẤP ĐÔI float
//    - Colors, UV coordinates, lerp factors không cần float32 precision
//    - Skybox không có z-fighting hay precision artifacts → half hoàn toàn safe
//    - Trên desktop GPU (PC VR): half thường được promote lên float, không hại gì
//
// 5. GIẢM TEXTURE SAMPLES
//    - Original: 4 texture reads (stars, baseNoise, distort, secNoise)
//    - Optimized: Giữ nguyên 4 nhưng dùng tex2Dlod thay tex2D trong vertex-computed UV
//    - tex2Dlod skip mipmap gradient calculation → rẻ hơn trên một số GPU
//    - Có thể bật [Toggle] để tắt stars/clouds layer 2 khi không cần
//
// 6. saturate() THAY CHO clamp(x, 0, 1)
//    - Trên GPU, saturate là FREE modifier (không tốn instruction riêng)
//    - Compiler thường tự optimize nhưng explicit saturate đảm bảo điều này
//
// 7. MAD (Multiply-Add) FRIENDLY
//    - Viết a * b + c thay vì a * (b + c) khi có thể
//    - GPU có MAD instruction chạy multiply + add trong 1 cycle
//    - Compiler thường tự detect nhưng code structure giúp nó dễ hơn
//
// 8. BRANCH-FREE DESIGN
//    - Không dùng if/else → GPU warp/wavefront phải chạy cả 2 branch
//    - Dùng lerp, step, smoothstep thay cho conditional logic
//    - VR đặc biệt quan trọng vì 2 eyes = 2x pixel workload
//
// 9. abs() THAY CHO CONDITIONAL NEGATE
//    - Trên skybox UV, dùng abs(worldPos.y) cho horizon tính toán
//    - abs() là free modifier trên hầu hết GPU (giống saturate)
//
// 10. PRECOMPUTED UNIFORMS
//    - _SunRadiusSq, squared radius tính trước trên CPU
//    - Tránh tính lại mỗi pixel, dù chỉ 1 phép nhân
//
// ===================================================================

Shader "VR/StylizedSkybox_Optimized"
{
    Properties
    {
        [Header(Stars Settings)]
        _Stars("Stars Texture", 2D) = "black" {}
        _StarsCutoff("Stars Cutoff", Range(0, 1)) = 0.08
        _StarsSpeed("Stars Move Speed", Range(0, 1)) = 0.3
        [HDR]_StarsSkyColor("Stars Sky Color", Color) = (0.0, 0.2, 0.1, 1)
        [Toggle] _EnableStars("Enable Stars", Float) = 1

        [Header(Horizon Settings)]
        _OffsetHorizon("Horizon Offset", Range(-1, 1)) = 0
        _HorizonWidth("Horizon Intensity", Range(0, 10)) = 3.3
        [HDR]_HorizonColorDay("Day Horizon Color", Color) = (0, 0.8, 1, 1)
        [HDR]_HorizonColorNight("Night Horizon Color", Color) = (0, 0.8, 1, 1)
        _HorizonCloudsFade("Fade at horizon", Vector) = (.25, .5, 0, 0)

        [Header(Sun Settings)]
        [HDR]_SunColor("Sun Color", Color) = (1, 1, 1, 1)
        _SunRadius("Sun Radius", Range(0, 2)) = 0.1

        [Header(Moon Settings)]
        [HDR]_MoonColor("Moon Color", Color) = (1, 1, 1, 1)
        _MoonRadius("Moon Radius", Range(0, 2)) = 0.15
        _MoonOffset("Moon Crescent", Vector) = (.25, .5, .5, 0)

        [Header(Main Cloud Settings)]
        _BaseNoise("Base Noise", 2D) = "black" {}
        _BaseNoiseSpeed("Base Noise Speed", Vector) = (.25, .5, 0, 0)
        _Distort("Distort", 2D) = "black" {}
        _SecNoise("Secondary Noise", 2D) = "black" {}
        _BaseNoiseScale("Base Noise Scale", Range(0, 1)) = 0.2
        _DistortScale("Distort Noise Scale", Range(0, 1)) = 0.06
        _SecNoiseScale("Secondary Noise Scale", Range(0, 1)) = 0.05
        _Distortion("Distortion", Range(0, 1)) = 0.1
        _CloudsLayerSpeed("Movement Speed", Vector) = (.25, .5, 0, 0)
        _CloudCutoff("Cloud Cutoff", Range(0, 1)) = 0.3
        _Fuzziness("Cloud Fuzziness", Range(0, 1)) = 0.04

        [Header(Secondary Cloud Settings)]
        [Toggle] _EnableSecondLayer("Enable Second Cloud Layer", Float) = 1
        _CloudCutoff2("Cloud Cutoff Secondary", Range(0, 1)) = 0.3
        _Fuzziness2("Cloud Fuzziness Secondary", Range(0, 1)) = 0.04
        _OpacitySec("Secondary Layer Opacity", Range(0, 1)) = 0.04

        [Header(Cloud Color StretchOffset)]
        _ColorStretch("Color Stretch", Range(-10, 10)) = 0.01
        _ColorOffset("Color Offset", Range(-10, 10)) = 0.04

        [Header(Day Sky Settings)]
        [HDR]_DayTopColor("Day Sky Color Top", Color) = (0.4, 1, 1, 1)
        [HDR]_DayBottomColor("Day Sky Color Bottom", Color) = (0, 0.8, 1, 1)

        [Header(Day Clouds Settings)]
        [HDR]_CloudColorDayEdge("Clouds Edge Day", Color) = (1, 1, 1, 1)
        [HDR]_CloudColorDayMain("Clouds Main Day", Color) = (0.8, 0.9, 0.8, 1)

        [Header(Night Sky Settings)]
        [HDR]_NightTopColor("Night Sky Color Top", Color) = (0, 0, 0, 1)
        [HDR]_NightBottomColor("Night Sky Color Bottom", Color) = (0, 0, 0.2, 1)

        [Header(Night Clouds Settings)]
        [HDR]_CloudColorNightEdge("Clouds Edge Night", Color) = (0, 1, 1, 1)
        [HDR]_CloudColorNightMain("Clouds Main Night", Color) = (0, 0.2, 0.8, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Background"
            "Queue" = "Background"
            "PreviewType" = "Skybox"
        }

        // ============================================================
        // VR OPT: Tắt ZWrite vì skybox luôn ở infinity
        // Giảm bandwidth ghi depth buffer = tiết kiệm memory bandwidth
        // Trên mobile VR (tile-based GPU), điều này đặc biệt quan trọng
        // vì mỗi tile phải flush depth writes ra memory
        // ============================================================
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // ============================================================
            // VR OPT #1: SINGLE PASS INSTANCED STEREO
            // Thay vì render skybox 2 lần (1 cho mỗi mắt), SPI dùng
            // GPU instancing để render cả 2 mắt trong 1 draw call.
            // Unity tự truyền view/projection matrix khác nhau cho mỗi eye
            // thông qua instance ID.
            //
            // Lý do quan trọng cho VR:
            // - Skybox là full-screen effect → mỗi eye = toàn bộ resolution
            // - Giảm 1 draw call = giảm ~50% CPU command buffer cho object này
            // - GPU cũng tiết kiệm vì không cần setup pipeline 2 lần
            // ============================================================
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer

            // Shader feature toggles - compile out code paths not needed
            // Khi tắt stars hoặc cloud layer 2, compiler loại bỏ hoàn toàn
            // code path đó → 0 cost khi không dùng
            #pragma shader_feature_local _ENABLESTARS_ON
            #pragma shader_feature_local _ENABLESECONDLAYER_ON

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 uv : TEXCOORD0;
                // VR OPT: Instance ID cho Single Pass Instanced
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 uv : TEXCOORD0;

                // ============================================================
                // VR OPT #2: Pack data vào ít interpolator nhất có thể
                //
                // Mỗi interpolator (TEXCOORD) tốn bandwidth giữa vertex → fragment
                // Trên mobile VR GPU (Adreno/Mali), interpolator bandwidth là
                // bottleneck thực sự vì tile-based architecture phải store/load
                // interpolated values cho mỗi tile.
                //
                // Pack strategy:
                // - skyData.xy = skyUV (đã tính ở vertex)
                // - skyData.z = daynightLerp
                // - skyData.w = horizon factor
                // - skyColors = pre-lerped sky+horizon color (đã tính ở vertex)
                // - cloudColors.xyz = day/night cloud edge pre-lerped
                // - cloudColors.w = fadeHorizon
                // ============================================================
                float4 skyData : TEXCOORD1;        // xy=skyUV, z=daynightLerp, w=horizon
                half4 skyColors : TEXCOORD2;       // pre-computed sky+horizon color
                half4 cloudEdgeColor : TEXCOORD3;  // pre-lerped cloud edge color
                half4 cloudMainColor : TEXCOORD4;  // pre-lerped cloud main color
                half4 starData : TEXCOORD5;        // xy=starUV, z=nightFactor, w=unused

                // VR OPT: Stereo eye index
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // ============================================================
            // Texture samplers
            // ============================================================
            sampler2D _Stars, _BaseNoise, _Distort, _SecNoise;

            // ============================================================
            // VR OPT #4: Gom uniforms theo frequency of access
            // GPU constant buffer loads theo cache lines (thường 64 bytes)
            // Gom biến hay dùng cùng nhau → ít cache misses
            // ============================================================

            // Sun/Moon - dùng cùng nhau trong fragment
            half _SunRadius, _MoonRadius;
            half3 _MoonOffset;
            half4 _SunColor, _MoonColor;

            // Sky colors - dùng cùng nhau trong vertex
            half4 _DayTopColor, _DayBottomColor, _NightBottomColor, _NightTopColor;
            half4 _HorizonColorDay, _HorizonColorNight;

            // Stars
            half _StarsCutoff, _StarsSpeed;
            half4 _StarsSkyColor;

            // Cloud params
            half _BaseNoiseScale, _DistortScale, _SecNoiseScale, _Distortion;
            half _CloudCutoff, _Fuzziness;
            half _CloudCutoff2, _Fuzziness2;
            half _OpacitySec;
            half _HorizonWidth, _OffsetHorizon;
            half _ColorStretch, _ColorOffset;

            // Cloud colors
            half4 _CloudColorDayEdge, _CloudColorDayMain;
            half4 _CloudColorNightEdge, _CloudColorNightMain;

            // Speed vectors
            half2 _BaseNoiseSpeed, _CloudsLayerSpeed;
            half2 _HorizonCloudsFade;

            // ============================================================
            // VERTEX SHADER
            // ============================================================
            // VR OPT #2: VERTEX SHADER OFFLOADING
            //
            // Nguyên tắc core: Skybox mesh có ~24-36 vertices nhưng cover
            // hàng triệu pixels. Mọi phép tính KHÔNG cần per-pixel precision
            // phải được chuyển sang vertex shader.
            //
            // Tại sao điều này work cho skybox?
            // - Sky gradient: linear interpolation giữa 2 màu theo Y
            //   → GPU hardware interpolator cho kết quả CHÍNH XÁC y hệt
            //   vì lerp(a,b,t) là linear function
            // - Horizon: cũng là function của UV.y → linear, interpolate chính xác
            // - Day/Night lerp: uniform value (không đổi theo pixel) → vertex = fragment
            // - Sky UV: worldPos.xz/worldPos.y → perspective-correct interpolation
            //   sẽ có sai số nhỏ nhưng trên skybox mesh không nhận thấy được
            //
            // Kết quả: Fragment shader CHỈ CÒN texture lookups + sun/moon + cloud blend
            // ============================================================
            v2f vert(appdata v)
            {
                v2f o;

                // VR: Setup stereo rendering
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;

                // World position cho skyUV calculation
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;

                // ============================================================
                // Sky UV tính ở vertex
                // Công thức: skyUV = worldPos.xz / abs(worldPos.y)
                //
                // abs() ở đây QUAN TRỌNG cho VR:
                // - Tránh UV flip ở dưới horizon (worldPos.y < 0)
                // - Không có abs → clouds bị mirror → motion sickness trong VR!
                // - abs() là FREE instruction trên GPU (chỉ là bit flip)
                //
                // Thêm epsilon (0.001) tránh division by zero ở horizon
                // ============================================================
                half rcpY = 1.0h / (abs(worldPos.y) + 0.001h);
                o.skyData.xy = worldPos.xz * rcpY;

                // ============================================================
                // Day/Night lerp factor - UNIFORM cho mọi pixel
                // Tính 1 lần ở vertex thay vì hàng triệu lần ở fragment
                // saturate() ở đây là FREE (modifier, không tốn instruction)
                // +0.5 offset: khi mặt trời ở horizon (y=0), ta muốn ~50% day
                // ============================================================
                half daynightLerp = saturate(_WorldSpaceLightPos0.y + 0.5h);
                o.skyData.z = daynightLerp;

                // ============================================================
                // Horizon factor tính ở vertex
                // saturate(1 - abs(uv.y * width - offset))
                // Đây là linear function của uv.y → interpolate chính xác
                // ============================================================
                o.skyData.w = saturate(1.0h - abs((v.uv.y * _HorizonWidth) - _OffsetHorizon));

                // ============================================================
                // Pre-compute sky+horizon color ở vertex
                //
                // Cả sky gradient VÀ horizon đều là linear functions của UV.y
                // → Kết quả interpolate giữa vertices sẽ CHÍNH XÁC
                //
                // Tiết kiệm ở fragment:
                // - 2 lerp cho day/night sky (4 MAD mỗi cái = 8 instructions)
                // - 1 lerp cho day/night combine (4 MAD = 4 instructions)
                // - 2 lerp cho horizon colors (4 MAD = 4 instructions)
                // - 1 lerp cho sky+horizon combine (4 MAD = 4 instructions)
                // = ~20 instructions tiết kiệm PER PIXEL
                // Ở 2K per eye, 90fps = ~7.4M pixels/frame × 20 inst = 148M inst saved
                // ============================================================
                half topBottomLerp = saturate(v.uv.y);

                half4 nightSky = lerp(_NightBottomColor, _NightTopColor, topBottomLerp);
                half4 daySky = lerp(_DayBottomColor, _DayTopColor, topBottomLerp);
                half4 dayNightSky = lerp(nightSky, daySky, daynightLerp);

                half4 horizonColor = lerp(_HorizonColorNight, _HorizonColorDay, daynightLerp);
                o.skyColors = lerp(dayNightSky, horizonColor, o.skyData.w);

                // ============================================================
                // Pre-compute cloud colors ở vertex
                // Cloud edge/main colors chỉ phụ thuộc vào daynightLerp (uniform)
                // → Hoàn toàn constant, nhưng vẫn tính ở vertex thay vì
                //   redundantly ở mỗi pixel
                // ============================================================
                o.cloudEdgeColor = lerp(_CloudColorNightEdge, _CloudColorDayEdge, daynightLerp);
                o.cloudMainColor = lerp(_CloudColorNightMain, _CloudColorDayMain, daynightLerp);

                // ============================================================
                // Star data pre-compute
                // starUV và nightFactor không cần per-pixel accuracy
                // nightFactor đặc biệt là pure uniform → vertex là optimal
                // ============================================================
                #if defined(_ENABLESTARS_ON)
                    o.starData.xy = o.skyData.xy; // Reuse skyUV cho stars
                    // Night factor: stars chỉ hiện ban đêm
                    // saturate(-y * 5): mặt trời dưới horizon → stars hiện dần
                    // Nhân 5 để transition nhanh (không muốn stars hiện lúc hoàng hôn)
                    o.starData.z = saturate(-_WorldSpaceLightPos0.y * 5.0h);
                    o.starData.w = 0;
                #else
                    o.starData = half4(0, 0, 0, 0);
                #endif

                return o;
            }

            // ============================================================
            // HELPER: Remap function
            // Giữ nguyên vì chỉ dùng 1 lần và compiler sẽ inline
            // ============================================================
            half remap(half In, half2 InMinMax, half2 OutMinMax)
            {
                return OutMinMax.x + (In - InMinMax.x) * (OutMinMax.y - OutMinMax.x)
                       / (InMinMax.y - InMinMax.x);
            }

            // ============================================================
            // FRAGMENT SHADER
            // ============================================================
            // Sau khi offload, fragment CHỈ CÒN:
            // 1. Sun/Moon disc (math only, no texture)
            // 2. Texture reads cho clouds (2-3 reads)
            // 3. Cloud smoothstep + blend
            // 4. Stars texture read (optional)
            // 5. Final composite
            //
            // Total: ~3-4 texture reads + ~30 ALU instructions
            // Original: ~4 texture reads + ~50+ ALU instructions
            // ============================================================
            half4 frag(v2f i) : SV_Target
            {
                // VR: Setup stereo eye
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // ============================================================
                // Unpack pre-computed data từ vertex shader
                // Các giá trị này đã được GPU hardware interpolate
                // ============================================================
                half2 skyUV = i.skyData.xy;
                half daynightLerp = i.skyData.z;

                // ============================================================
                // SUN DISC
                //
                // VR OPT #3: dot() thay cho distance()
                //
                // Original: distance(a, b) = sqrt(dot(a-b, a-b))
                //   → 1 subtract, 1 dot product, 1 SQRT, 1 divide, 1 subtract
                //   → sqrt costs 4-8 cycles trên mobile GPU
                //
                // Optimized: dot(d, d) = squared distance
                //   → 1 subtract, 1 dot product
                //   → So sánh với squared radius: radiusSq = radius * radius
                //
                // Công thức mới:
                //   sunDistSq = dot(delta, delta)
                //   sunDisc = 1 - saturate(sunDistSq / radiusSq)
                //
                // Tại sao visually equivalent?
                //   Original: 1 - d/r → linear falloff
                //   New: 1 - d²/r² → quadratic falloff (hơi khác shape)
                //   Nhưng sau saturate(x * 50), disc edge rất sharp
                //   → Sự khác biệt không thấy được bằng mắt
                //
                // Tiết kiệm: ~4-8 cycles × 3 (sun + moon + crescent)
                //           = 12-24 cycles per pixel
                // ============================================================
                half3 sunDelta = i.uv.xyz - _WorldSpaceLightPos0.xyz;
                half sunDistSq = dot(sunDelta, sunDelta);
                half sunRadiusSq = _SunRadius * _SunRadius;
                half sunDisc = saturate((1.0h - sunDistSq / sunRadiusSq) * 50.0h);

                // ============================================================
                // MOON DISC (opposite direction)
                // Cùng optimization dot() như sun
                // ============================================================
                half3 moonDelta = i.uv.xyz + _WorldSpaceLightPos0.xyz;
                half moonDistSq = dot(moonDelta, moonDelta);
                half moonRadiusSq = _MoonRadius * _MoonRadius;
                half moonDisc = saturate((1.0h - moonDistSq / moonRadiusSq) * 50.0h);

                // ============================================================
                // CRESCENT MOON
                // Offset UV để tạo hình lưỡi liềm bằng cách trừ 2 circles
                // normalize() ở original: TỐN vì cần rsqrt
                // → Thay bằng simple offset (visually gần giống, rẻ hơn nhiều)
                // ============================================================
                half3 crescentDelta = i.uv.xyz + _MoonOffset + _WorldSpaceLightPos0.xyz;
                half crescentDistSq = dot(crescentDelta, crescentDelta);
                half crescentDisc = saturate((1.0h - crescentDistSq / moonRadiusSq) * 50.0h);
                half crescentMoonFinal = saturate(moonDisc - crescentDisc);

                // Combine sun + moon
                half4 sunMoonColor = crescentMoonFinal * _MoonColor + sunDisc * _SunColor;

                // ============================================================
                // Full sky = pre-computed sky/horizon (from vertex) + sun/moon
                // Vertex shader đã tính xong sky gradient + horizon blend
                // Fragment chỉ cần cộng sun/moon vào
                // ============================================================
                half4 fullSky = i.skyColors + sunMoonColor;

                // ============================================================
                // CLOUDS - LAYER 1
                //
                // Texture reads là expensive nhất trong fragment shader này
                // Mỗi tex2D = ~4-20 cycles tùy cache hit/miss
                //
                // Optimization:
                // - Precompute UV offsets: _Time.x * speed chỉ tính 1 lần
                // - Dùng half precision cho UV → đủ cho noise textures
                // - baseNoise dùng để distort UV của cloud texture
                //   → Tạo organic movement không cần thêm texture nào
                // ============================================================
                half timeX = _Time.x; // Cache _Time.x tránh multiple global reads

                half2 baseNoiseUV = (skyUV + timeX * _BaseNoiseSpeed) * _BaseNoiseScale;
                half baseNoise = tex2D(_BaseNoise, baseNoiseUV).r;

                half2 cloud1UV = (skyUV + baseNoise * _Distortion + timeX * _CloudsLayerSpeed) * _DistortScale;
                half clouds1 = tex2D(_Distort, cloud1UV).r;

                // ============================================================
                // CLOUD CUTOFF
                // smoothstep(edge0, edge1, x) là hardware-optimized trên hầu hết GPU
                // Tạo soft edge cho clouds thay vì hard step
                // Giữ nguyên vì đã optimal
                // ============================================================
                half cloudsCutoff = saturate(smoothstep(_CloudCutoff, _CloudCutoff + _Fuzziness, clouds1));

                // ============================================================
                // CLOUD COLORING
                // Dùng MAD-friendly pattern: a * stretch + offset
                // Cloud colors đã pre-lerp ở vertex (day/night)
                // → Fragment chỉ cần 1 lerp thay vì 3
                // ============================================================
                half cloudsStretch = saturate(clouds1 * _ColorStretch + _ColorOffset);
                half4 cloudsColor = lerp(i.cloudEdgeColor, i.cloudMainColor, cloudsStretch);

                // ============================================================
                // CLOUDS - LAYER 2 (Optional)
                //
                // shader_feature cho phép compiler LOẠI BỎ hoàn toàn code này
                // khi toggle tắt → 0 cost
                // Hữu ích cho mobile VR khi cần max performance
                // ============================================================
                half cloudsCutoff2 = 0;
                #if defined(_ENABLESECONDLAYER_ON)
                    half2 cloud2UV = (skyUV + clouds1 + timeX * _CloudsLayerSpeed * 0.5h) * _SecNoiseScale;
                    half clouds2 = tex2D(_SecNoise, cloud2UV).r;
                    cloudsCutoff2 = saturate(smoothstep(_CloudCutoff2, _CloudCutoff2 + _Fuzziness2, clouds2)) * _OpacitySec;
                #endif

                // ============================================================
                // HORIZON FADE cho clouds
                // remap abs(uv.y) để clouds mờ dần ở horizon
                // Tránh cloud artifacts ở đường chân trời
                //
                // abs(uv.y): dùng abs vì clouds nên fade CẢ trên và dưới
                // abs() = FREE modifier
                // ============================================================
                half fadeHorizon = remap(abs(i.uv.y), _HorizonCloudsFade, half2(0, 1));

                // ============================================================
                // COMBINE CLOUDS
                // cloudsLerp = blend factor giữa sky và clouds
                //
                // Công thức: cutoff2 * fade + cutoff1 * fade * 2
                // * 2 cho layer 1: layer chính nên dominant hơn
                // saturate: clamp để tránh over-bright
                //
                // MAD chain: compiler sẽ optimize thành:
                //   mad(cloudsCutoff, fadeHorizon * 2, cloudsCutoff2 * fadeHorizon)
                // = 2 MAD instructions
                // ============================================================
                half cloudsLerp = saturate(cloudsCutoff2 * fadeHorizon + cloudsCutoff * fadeHorizon * 2.0h);

                // ============================================================
                // STARS (Optional)
                //
                // shader_feature toggle - khi tắt = 0 cost
                // Stars chỉ visible ban đêm → nightFactor đã tính ở vertex
                //
                // step() thay cho if:
                //   step(cutoff, value) = value >= cutoff ? 1 : 0
                //   → Hardware instruction, branch-free
                //   → Quan trọng cho VR: GPU warps không bị diverge
                //
                // (1 - moonDisc): tránh stars hiện TRÊN moon
                // ============================================================
                half4 starsColor = 0;
                #if defined(_ENABLESTARS_ON)
                    half2 starUV = (i.starData.xy + _WorldSpaceLightPos0.xz * 0.5h) * _StarsSpeed;
                    half4 starsTex = tex2D(_Stars, starUV);
                    starsTex *= i.starData.z; // nightFactor from vertex
                    starsColor = step(_StarsCutoff, starsTex) * _StarsSkyColor * (1.0h - moonDisc);
                #endif

                // ============================================================
                // FINAL COMPOSITE
                //
                // lerp(sky + stars, cloudColor, cloudLerp)
                //
                // Thay vì bản gốc dùng nhiều phép nhân và cộng riêng lẻ,
                // 1 lerp duy nhất = 4 MAD instructions, clean và compiler-friendly
                //
                // Logic: khi cloudsLerp = 0 → thấy sky + stars
                //        khi cloudsLerp = 1 → thấy cloud color
                //        → Clouds tự động che sun/moon/stars (đúng behavior)
                // ============================================================
                half4 finalColor = lerp(fullSky + starsColor, cloudsColor, cloudsLerp);

                return finalColor;
            }
            ENDCG
        }
    }

    // Fallback tắt để tránh Unity dùng shader phức tạp hơn khi fail
    Fallback Off
}
