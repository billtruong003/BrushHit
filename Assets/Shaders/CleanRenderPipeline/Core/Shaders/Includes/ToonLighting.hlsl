// ============================================================================
// ToonLighting.hlsl — Clean Render Pipeline v2.0
//
// Core toon lighting include. Provides:
//
//   [LEGACY API — backward compatible, used by ToonLit, ToonMetal, ToonFoliage]
//     struct ToonLightResult { diffuse, rim, globalIllumination }
//     ToonLightResult ComputeToonMainLight(...)            — explicit params
//     half3 ComputeToonAdditionalLights(...)               — explicit params
//     ComputeToonMainLightGlobal(...)                      — MACRO wrapper
//     ComputeToonAdditionalLightsGlobal(...)               — MACRO wrapper
//
//   [NEW API — for ToonTerrain and future shaders]
//     struct ToonLightingInput / ToonLightingOutput
//     ToonLightingOutput ComputeToonLighting(ToonLightingInput)
//     half3 ComputeToonLightingSimple(...)
//
// IMPORTANT — WHY MACROS:
//   ComputeToonMainLightGlobal and ComputeToonAdditionalLightsGlobal are
//   #define MACROS, not functions. This is intentional:
//   - D3D11/FXC compiles ALL function bodies in an include file, even for
//     passes that never call them (ShadowCaster, DepthOnly, Meta...).
//   - This file is #included BEFORE CBUFFER_START in each shader.
//   - As functions referencing _Threshold etc. → "undeclared identifier"
//     in every non-ForwardLit pass.
//   - As macros → only expand at the call site where CBUFFER IS visible.
//     Passes that don't call the macro → _Threshold never appears → no error.
//
// REQUIRED KEYWORDS (add to every lit ForwardLit pass):
//   #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
//   #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
//   #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
//   #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
//   #pragma multi_compile _ LIGHTMAP_ON
//   #pragma multi_compile _ DIRLIGHTMAP_COMBINED
//   #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
//   #pragma multi_compile _ SHADOWS_SHADOWMASK
// ============================================================================

#ifndef TOON_LIGHTING_INCLUDED
#define TOON_LIGHTING_INCLUDED

// ════════════════════════════════════════════════════════════════
// 1.  SHADOW COORD — unified for every shader
// ════════════════════════════════════════════════════════════════

float4 ToonGetShadowCoord(float3 positionWS, float4 positionCS)
{
    #if defined(_MAIN_LIGHT_SHADOWS_SCREEN) && !defined(_SURFACE_TYPE_TRANSPARENT)
        return ComputeScreenPos(positionCS);
    #elif defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
        return TransformWorldToShadowCoord(positionWS);
    #else
        return float4(0, 0, 0, 0);
    #endif
}

float4 ToonGetShadowCoordSimple(float3 positionWS)
{
    #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
        return TransformWorldToShadowCoord(positionWS);
    #else
        return float4(0, 0, 0, 0);
    #endif
}


// ════════════════════════════════════════════════════════════════
// 2.  BAKED GI — Lightmap + Spherical Harmonics
// ════════════════════════════════════════════════════════════════

half3 ToonSampleBakedGI(float2 lightmapUV, half3 normalWS)
{
    #if defined(LIGHTMAP_ON)
        return SampleLightmap(lightmapUV, 0, normalWS);
    #else
        return SampleSH(normalWS);
    #endif
}

float2 ToonTransformLightmapUV(float2 uv1)
{
    #if defined(LIGHTMAP_ON)
        return uv1 * unity_LightmapST.xy + unity_LightmapST.zw;
    #else
        return float2(0.0, 0.0);
    #endif
}


// ════════════════════════════════════════════════════════════════
// 3.  CEL RAMP — branchless toon step
// ════════════════════════════════════════════════════════════════

half ToonCelRamp(half NdotL, half threshold, half smoothness)
{
    return smoothstep(threshold - smoothness, threshold + smoothness, NdotL);
}

half ToonRimFactor(half NdotV, half rimPower, half lightIntensity)
{
    half rim = 1.0h - saturate(NdotV);
    return smoothstep(1.0h - rcp(max(rimPower, 0.01h)), 1.0h, rim * lightIntensity);
}


// ════════════════════════════════════════════════════════════════
// 4.  LEGACY API — ToonLightResult + ComputeToonMainLight
//     Used by: ToonLit, ToonMetal, ToonFoliage
// ════════════════════════════════════════════════════════════════

struct ToonLightResult
{
    half3 diffuse;
    half3 rim;
    half3 globalIllumination;
};

// Full explicit-params version. NO CBUFFER references — safe in all passes.
ToonLightResult ComputeToonMainLight(
    float3 posWS, float3 normalWS, float3 viewDirWS,
    half3 albedo, float2 lightmapUV,
    half threshold, half smoothness, half3 shadowColor,
    half3 rimColor, half rimPower, half rimIntensity, half ambientScale)
{
    ToonLightResult result = (ToonLightResult)0;

    float4 shadowCoord = ToonGetShadowCoordSimple(posWS);
    Light mainLight = GetMainLight(shadowCoord);

    half3 N = (half3)normalWS;
    half3 V = (half3)viewDirWS;
    half3 L = (half3)mainLight.direction;

    half NdotL = dot(N, L);
    half NdotV = dot(N, V);
    half shadowAtten = (half)mainLight.shadowAttenuation;

    half intensity = ToonCelRamp(NdotL * shadowAtten, threshold, smoothness);

    result.diffuse = albedo * lerp(shadowColor, (half3)mainLight.color, intensity);

    half rim = ToonRimFactor(NdotV, rimPower, intensity);
    result.rim = rimColor * rim * rimIntensity;

    #if defined(LIGHTMAP_ON)
        result.globalIllumination = SampleLightmap(lightmapUV, 0, N) * albedo;
    #else
        result.globalIllumination = SampleSH(N) * albedo * ambientScale;
    #endif

    return result;
}

// ────────────────────────────────────────────────────────────────
// "Global" convenience wrapper — MACRO, not function.
// Expands to ComputeToonMainLight(...) with CBUFFER variable names.
// Only expands at call site (ForwardLit) where CBUFFER is visible.
//
// Requires in caller's CBUFFER: _Threshold, _Smoothness, _ShadowColor, _RimColor, _RimPower
// ────────────────────────────────────────────────────────────────

#define ComputeToonMainLightGlobal(posWS, normalWS, viewDirWS, albedo, lightmapUV) \
    ComputeToonMainLight(                                                           \
        (posWS), (normalWS), (viewDirWS), (albedo), (lightmapUV),                  \
        _Threshold, _Smoothness, _ShadowColor.rgb,                                 \
        _RimColor.rgb, _RimPower, _RimColor.a, 1.0h)

// ────────────────────────────────────────────────────────────────
// Additional lights — explicit-params function (safe in all passes).
// ────────────────────────────────────────────────────────────────

half3 ComputeToonAdditionalLights(float3 posWS, float3 normalWS, half3 albedo,
                                   half threshold, half smoothness)
{
    half3 addColor = half3(0.0h, 0.0h, 0.0h);

    #if defined(_ADDITIONAL_LIGHTS)
    {
        uint lightCount = GetAdditionalLightsCount();
        for (uint i = 0u; i < lightCount; i++)
        {
            Light addLight = GetAdditionalLight(i, posWS);
            half addAtten = (half)(addLight.distanceAttenuation * addLight.shadowAttenuation);
            half addNdotL = saturate(dot((half3)normalWS, (half3)addLight.direction));
            half addIntensity = ToonCelRamp(addNdotL * addAtten, threshold, smoothness);
            addColor += albedo * (half3)addLight.color * addIntensity;
        }
    }
    #endif

    return addColor;
}

// ────────────────────────────────────────────────────────────────
// Additional lights — "Global" MACRO wrapper.
// Requires in caller's CBUFFER: _Threshold, _Smoothness
// ────────────────────────────────────────────────────────────────

#define ComputeToonAdditionalLightsGlobal(posWS, normalWS, albedo) \
    ComputeToonAdditionalLights((posWS), (normalWS), (albedo), _Threshold, _Smoothness)


// ════════════════════════════════════════════════════════════════
// 5.  NEW API — struct-based, for ToonTerrain and future shaders
//     All params via struct — NO CBUFFER references — safe everywhere.
// ════════════════════════════════════════════════════════════════

struct ToonLightingInput
{
    half3  albedo;
    half3  normalWS;
    float3 positionWS;
    float4 positionCS;
    float2 lightmapUV;
    half3  shadowColor;
    half   threshold;
    half   smoothness;
};

struct ToonLightingOutput
{
    half3 color;
};

ToonLightingOutput ComputeToonLighting(ToonLightingInput input)
{
    ToonLightingOutput output;

    float4 shadowCoord = ToonGetShadowCoord(input.positionWS, input.positionCS);
    Light mainLight = GetMainLight(shadowCoord);

    half3 L = (half3)mainLight.direction;
    half  NdotL = dot(input.normalWS, L);
    half  shadowAtt = (half)mainLight.shadowAttenuation;
    half  distAtt = (half)mainLight.distanceAttenuation;

    half celFactor = ToonCelRamp(NdotL, input.threshold, input.smoothness);
    half combinedLight = celFactor * shadowAtt * distAtt;

    half3 toonTint = lerp(input.shadowColor, half3(1.0h, 1.0h, 1.0h), combinedLight);
    half3 directDiffuse = toonTint * (half3)mainLight.color * input.albedo;

    half3 additionalDiffuse = half3(0.0h, 0.0h, 0.0h);
    #if defined(_ADDITIONAL_LIGHTS)
    {
        uint lightCount = GetAdditionalLightsCount();
        for (uint i = 0u; i < lightCount; i++)
        {
            Light addLight = GetAdditionalLight(i, input.positionWS);
            half addNdotL = dot(input.normalWS, (half3)addLight.direction);
            half addCel = ToonCelRamp(addNdotL, input.threshold, input.smoothness);
            half addAtten = (half)(addLight.distanceAttenuation * addLight.shadowAttenuation);
            additionalDiffuse += addCel * addAtten * (half3)addLight.color * input.albedo;
        }
    }
    #endif

    half3 indirectDiffuse = ToonSampleBakedGI(input.lightmapUV, input.normalWS);
    half3 indirectContrib = indirectDiffuse * input.albedo;

    #if defined(LIGHTMAP_SHADOW_MIXING) && defined(LIGHTMAP_ON)
        directDiffuse *= shadowAtt;
    #endif

    output.color = directDiffuse + additionalDiffuse + indirectContrib;
    return output;
}

// Simplified variant for shaders with custom emission (Water, Lava).
half3 ComputeToonLightingSimple(
    half3 albedo, half3 normalWS, float3 positionWS,
    half3 shadowColor, half threshold, half smoothness)
{
    float4 shadowCoord = ToonGetShadowCoordSimple(positionWS);
    Light mainLight = GetMainLight(shadowCoord);
    half NdotL = dot(normalWS, (half3)mainLight.direction);
    half cel = ToonCelRamp(NdotL, threshold, smoothness);
    half shadow = cel * (half)mainLight.shadowAttenuation * (half)mainLight.distanceAttenuation;
    half3 tint = lerp(shadowColor, half3(1.0h, 1.0h, 1.0h), shadow);
    return tint * (half3)mainLight.color * albedo;
}


#endif // TOON_LIGHTING_INCLUDED
