#ifndef CLEAN_NOISE_LIB_INCLUDED
#define CLEAN_NOISE_LIB_INCLUDED

// ============================================================================
// CleanRender — Noise Library (v2)
// Texture-based noise sampling (NO procedural = fast on all GPUs / VR)
// For water scrolling, waterfall triplanar, wind animation, grass sway
// ============================================================================

// ── Simple UV Scroll ─────────────────────────────────────────────────────
float2 ScrollUV(float2 uv, float2 speed, float time)
{
    return uv + speed * time;
}

// ── Dual-layer scroll (depth effect) ─────────────────────────────────────
float2 DualScrollUV(float2 uv, float2 speed1, float2 speed2, float time, out float2 uv2)
{
    uv2 = uv + speed2 * time;
    return uv + speed1 * time;
}

// ════════════════════════════════════════════════════════════════════════════
// Triplanar UV Projection
// ════════════════════════════════════════════════════════════════════════════

struct TriplanarUV
{
    float2 uvX; // YZ plane (side)
    float2 uvY; // XZ plane (top-down)
    float2 uvZ; // XY plane (side)
    float3 blend;
};

TriplanarUV ComputeTriplanarUV(float3 worldPos, float3 worldNormal, float scale, float sharpness)
{
    TriplanarUV tp;
    tp.uvX = worldPos.yz * scale;
    tp.uvY = worldPos.xz * scale;
    tp.uvZ = worldPos.xy * scale;

    float3 blendRaw = abs(worldNormal);
    blendRaw = pow(blendRaw, sharpness);
    tp.blend = blendRaw / (blendRaw.x + blendRaw.y + blendRaw.z + 1e-6);

    return tp;
}

// Sample texture triplanar (static)
half4 SampleTriplanar(TEXTURE2D_PARAM(tex, samp), TriplanarUV tp)
{
    half4 cx = SAMPLE_TEXTURE2D(tex, samp, tp.uvX);
    half4 cy = SAMPLE_TEXTURE2D(tex, samp, tp.uvY);
    half4 cz = SAMPLE_TEXTURE2D(tex, samp, tp.uvZ);
    return cx * tp.blend.x + cy * tp.blend.y + cz * tp.blend.z;
}

// Sample triplanar with uniform scroll (original)
half4 SampleTriplanarScrolled(TEXTURE2D_PARAM(tex, samp), TriplanarUV tp,
                               float2 scrollDir, float time)
{
    float2 scroll = scrollDir * time;
    half4 cx = SAMPLE_TEXTURE2D(tex, samp, tp.uvX + scroll);
    half4 cy = SAMPLE_TEXTURE2D(tex, samp, tp.uvY + scroll);
    half4 cz = SAMPLE_TEXTURE2D(tex, samp, tp.uvZ + scroll);
    return cx * tp.blend.x + cy * tp.blend.y + cz * tp.blend.z;
}

// ════════════════════════════════════════════════════════════════════════════
// MinionsArt-Style Waterfall Triplanar
// Key insight: Stretch side UVs (X/Z), animate Y over time for falling effect
// mWorldPos = (worldPos.x * stretch, worldPos.y + _Time, worldPos.z * stretch)
// Side textures sample from this modified position
// Top texture uses gentle scroll or flow map instead
// ════════════════════════════════════════════════════════════════════════════

struct WaterfallTriplanar
{
    half  sideNoise;   // blended X+Z face noise
    half  topNoise;    // Y face noise (for pool surface at top/bottom)
    float3 blend;      // raw blend weights
};

// Full waterfall triplanar — returns separated side/top for downstream foam logic
WaterfallTriplanar SampleWaterfallTriplanar(
    TEXTURE2D_PARAM(tex, samp),
    float3 worldPos, float3 worldNormal,
    float scale, float sharpness,
    float sideStretch, float fallSpeed, float time,
    float2 topScrollSpeed)
{
    WaterfallTriplanar result;

    // Modified world position: stretched sides, falling Y
    float3 mWorldPos = float3(
        worldPos.x * sideStretch,
        worldPos.y + time * fallSpeed,
        worldPos.z * sideStretch
    );

    // Blend weights
    float3 blendRaw = abs(worldNormal);
    blendRaw = pow(blendRaw, sharpness);
    result.blend = blendRaw / (blendRaw.x + blendRaw.y + blendRaw.z + 1e-6);

    // Side faces — use modified (stretched + falling) coordinates
    half nX = SAMPLE_TEXTURE2D(tex, samp, mWorldPos.zy * scale).r;
    half nZ = SAMPLE_TEXTURE2D(tex, samp, mWorldPos.xy * scale).r;
    result.sideNoise = nX * result.blend.x + nZ * result.blend.z;

    // Top face — gentle scroll, not affected by fall speed
    float2 topUV = worldPos.xz * scale * 0.5 + topScrollSpeed * time;
    result.topNoise = SAMPLE_TEXTURE2D(tex, samp, topUV).r;

    return result;
}

// Simple combined waterfall noise (convenience)
half SampleWaterfallNoiseCombined(
    TEXTURE2D_PARAM(tex, samp),
    float3 worldPos, float3 worldNormal,
    float scale, float sharpness,
    float sideStretch, float fallSpeed, float time,
    float2 topScrollSpeed)
{
    WaterfallTriplanar wf = SampleWaterfallTriplanar(
        TEXTURE2D_ARGS(tex, samp),
        worldPos, worldNormal,
        scale, sharpness, sideStretch, fallSpeed, time,
        topScrollSpeed);

    return wf.sideNoise + wf.topNoise * wf.blend.y;
}

// ════════════════════════════════════════════════════════════════════════════
// Flow Map Sampling
// For baked flow directions (rivers, custom flow in waterfall pool)
// Dual-phase technique avoids UV stretching
// ════════════════════════════════════════════════════════════════════════════

half4 SampleWithFlow(TEXTURE2D_PARAM(tex, samp), float2 uv,
                     TEXTURE2D_PARAM(flowMap, flowSamp),
                     float time, float flowStrength)
{
    float2 flow = SAMPLE_TEXTURE2D(flowMap, flowSamp, uv).rg * 2.0 - 1.0;
    flow *= flowStrength;

    // Dual phase to avoid stretching
    float phase0 = frac(time * 0.5);
    float phase1 = frac(time * 0.5 + 0.5);
    float blend  = abs(phase0 * 2.0 - 1.0);

    float2 uv0 = uv - flow * phase0;
    float2 uv1 = uv - flow * phase1;

    half4 c0 = SAMPLE_TEXTURE2D(tex, samp, uv0);
    half4 c1 = SAMPLE_TEXTURE2D(tex, samp, uv1);

    return lerp(c0, c1, blend);
}

// ════════════════════════════════════════════════════════════════════════════
// Wind Displacement (vegetation vertex animation)
// ════════════════════════════════════════════════════════════════════════════

float3 SampleWind(TEXTURE2D_PARAM(windTex, windSamp), float3 worldPos,
                  float windScale, float windSpeed, float windStrength, float time)
{
    float2 windUV = worldPos.xz * windScale + float2(time * windSpeed, 0);
    float windNoise = SAMPLE_TEXTURE2D_LOD(windTex, windSamp, windUV, 0).r;

    float3 displacement;
    displacement.x = (windNoise * 2.0 - 1.0) * windStrength;
    displacement.z = (windNoise * 2.0 - 1.0) * windStrength * 0.5;
    displacement.y = -abs(windNoise - 0.5) * windStrength * 0.2;
    return displacement;
}

// ════════════════════════════════════════════════════════════════════════════
// Hash Functions (per-instance variation)
// ════════════════════════════════════════════════════════════════════════════

float Hash(float n)
{
    return frac(sin(n) * 43758.5453);
}

float Hash2D(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
}

// ════════════════════════════════════════════════════════════════════════════
// Cheap Vertex Noise (sine-based, no texture needed)
// ════════════════════════════════════════════════════════════════════════════

float SimpleWave(float3 worldPos, float speed, float scale, float time)
{
    return sin(worldPos.x * scale + time * speed) *
           cos(worldPos.z * scale * 0.7 + time * speed * 0.8);
}

#endif // CLEAN_NOISE_LIB_INCLUDED
