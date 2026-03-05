#ifndef CLEAN_INSTANCING_CORE_INCLUDED
#define CLEAN_INSTANCING_CORE_INCLUDED

// ============================================================================
// CleanRender — Instancing Core
// Shared data structures + matrix construction for all instanced shaders
// ============================================================================

// ── Compressed Instance Data (40 bytes) ──────────────────────────────────
// Used by both Static and Dynamic systems
// Significantly smaller than 2x Matrix4x4 (128 bytes)
struct CompressedInstanceData
{
    float3 position;    // 12 bytes
    float3 scale;       // 12 bytes
    float4 rotation;    // 16 bytes (quaternion xyzw)
    float2 lodRange;    // 8 bytes  (minDistSq, maxDistSq) — optional for static LOD
};

// ── Full Instance Data (for dynamic objects needing per-instance color) ───
struct FullInstanceData
{
    float3 position;
    float3 scale;
    float4 rotation;
    float4 color;       // per-instance tint
};

// ── Bounds Data ──────────────────────────────────────────────────────────
struct InstanceBounds
{
    float3 center;
    float3 extents;
};

// ============================================================================
// Quaternion → Rotation Matrix (Optimized)
// ============================================================================

float4x4 QuatToMatrix(float3 pos, float4 rot, float3 scl)
{
    float x2 = rot.x + rot.x;
    float y2 = rot.y + rot.y;
    float z2 = rot.z + rot.z;
    float xx = rot.x * x2;
    float xy = rot.x * y2;
    float xz = rot.x * z2;
    float yy = rot.y * y2;
    float yz = rot.y * z2;
    float zz = rot.z * z2;
    float wx = rot.w * x2;
    float wy = rot.w * y2;
    float wz = rot.w * z2;

    float4x4 m;
    m[0] = float4((1.0 - (yy + zz)) * scl.x, (xy - wz) * scl.y, (xz + wy) * scl.z, pos.x);
    m[1] = float4((xy + wz) * scl.x, (1.0 - (xx + zz)) * scl.y, (yz - wx) * scl.z, pos.y);
    m[2] = float4((xz - wy) * scl.x, (yz + wx) * scl.y, (1.0 - (xx + yy)) * scl.z, pos.z);
    m[3] = float4(0, 0, 0, 1);
    return m;
}

float4x4 QuatToInverseMatrix(float3 pos, float4 rot, float3 scl)
{
    float3 is = 1.0 / (scl + 1e-6);

    float x2 = rot.x + rot.x;
    float y2 = rot.y + rot.y;
    float z2 = rot.z + rot.z;
    float xx = rot.x * x2;
    float xy = rot.x * y2;
    float xz = rot.x * z2;
    float yy = rot.y * y2;
    float yz = rot.y * z2;
    float zz = rot.z * z2;
    float wx = rot.w * x2;
    float wy = rot.w * y2;
    float wz = rot.w * z2;

    // Transposed rotation rows (inverse rotation = transpose)
    float3 r0 = float3(1.0 - (yy + zz), xy + wz, xz - wy);
    float3 r1 = float3(xy - wz, 1.0 - (xx + zz), yz + wx);
    float3 r2 = float3(xz + wy, yz - wx, 1.0 - (xx + yy));

    float4x4 m;
    m[0] = float4(r0.x * is.x, r1.x * is.x, r2.x * is.x, 0);
    m[1] = float4(r0.y * is.y, r1.y * is.y, r2.y * is.y, 0);
    m[2] = float4(r0.z * is.z, r1.z * is.z, r2.z * is.z, 0);

    float3 np = -pos;
    m[0].w = m[0].x * np.x + m[0].y * np.y + m[0].z * np.z;
    m[1].w = m[1].x * np.x + m[1].y * np.y + m[1].z * np.z;
    m[2].w = m[2].x * np.x + m[2].y * np.y + m[2].z * np.z;
    m[3] = float4(0, 0, 0, 1);
    return m;
}

// ============================================================================
// Macro: Setup procedural instancing from CompressedInstanceData
// Usage: Call SETUP_INSTANCE(bufferName, indexBufferName) in Setup()
// ============================================================================

#define SETUP_COMPRESSED_INSTANCE(srcBuffer, idxBuffer) \
    uint _idx = idxBuffer[unity_InstanceID]; \
    CompressedInstanceData _data = srcBuffer[_idx]; \
    unity_ObjectToWorld = QuatToMatrix(_data.position, _data.rotation, _data.scale); \
    unity_WorldToObject = QuatToInverseMatrix(_data.position, _data.rotation, _data.scale);

#define SETUP_FULL_INSTANCE(srcBuffer, idxBuffer) \
    uint _idx = idxBuffer[unity_InstanceID]; \
    FullInstanceData _data = srcBuffer[_idx]; \
    unity_ObjectToWorld = QuatToMatrix(_data.position, _data.rotation, _data.scale); \
    unity_WorldToObject = QuatToInverseMatrix(_data.position, _data.rotation, _data.scale);

#endif // CLEAN_INSTANCING_CORE_INCLUDED
