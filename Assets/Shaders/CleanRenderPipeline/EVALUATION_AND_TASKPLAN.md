# CleanRenderPipeline — Đánh Giá Code Hiện Tại & Task Plan

## ══════════════════════════════════════════════════
## PHẦN 1: ĐÁNH GIÁ CODE HIỆN TẠI
## ══════════════════════════════════════════════════

### 1.1 StaticInstancingManager.cs — ĐÁNH GIÁ: 8.5/10

**Điểm mạnh:**
- CompressedInstanceData dùng float3 pos + float4 quat + float3 scale thay vì Matrix4x4 (64 bytes vs 128 bytes) → tiết kiệm 50% GPU memory bandwidth
- LOD range được pack vào per-instance data (lodRange: minDistSq, maxDistSq) → GPU chọn LOD, không cần CPU LODGroup
- Tách riêng VisibleIndices vs ShadowIndices → shadow objects behind camera vẫn cast shadow (rất đúng)
- Throttle culling bằng ShouldUpdateCulling() với moveThreshold + angleThreshold → không cull mỗi frame nếu camera đứng yên
- Sort batches theo material ID → giảm state change
- XR/VR support trong UpdateFrustumPlanes() — merge stereo frustum rất tốt

**Điểm cần cải thiện:**
- `BakeStaticGeometry()` dùng `FindObjectsByType` → chậm với scene lớn, nên cache
- Hash key `mesh.GetInstanceID() ^ material.GetInstanceID()` có thể collision nếu 2 mesh+mat pair có cùng XOR → nên dùng hash combine: `hash = meshID * 397 ^ matID`
- `_cullDistance * 0.5f` cho shadow distance là hardcode → nên lấy từ QualitySettings.shadowDistance
- ComputeBuffer.CopyCount gọi mỗi frame dù camera không di chuyển → nên cache kết quả khi ShouldUpdateCulling() = false
- PerformDrawing() gọi DrawMeshInstancedIndirect 2 lần per batch (visible + shadow) → có thể dùng ShadowCastingMode.On thay vì tách 2 call nếu visible set đã bao gồm shadow casters

### 1.2 StaticCulling.compute — ĐÁNH GIÁ: 9/10

**Điểm mạnh:**
- Check LOD range trên GPU → CPU cost = 0 cho LOD selection
- Shadow không check frustum (đúng! object sau lưng vẫn đổ bóng)
- Frustum culling 6 planes với AABB test (center + extents)
- numthreads(64,1,1) phù hợp cho GPU wavefront

**Cần cải thiện:**
- Thiếu Hi-Z occlusion culling → với scene BR đông đúc, frustum culling alone vẫn render nhiều occluded objects
- Không có distance-based fade/dithering → LOD pop rõ ràng khi transition
- Nên thêm screen-size culling: nếu projected size < 2 pixels thì skip

### 1.3 DynamicInstancingManager.cs — ĐÁNH GIÁ: 7/10

**Điểm mạnh:**
- Append buffer pattern đúng (source → cull → visible → indirect draw)
- TimeGate throttle CPU data upload
- BakeFromChildren rất tiện cho workflow

**Cần cải thiện:**
- UpdateCPUData() gọi `Matrix4x4.TRS()` + `.inverse` mỗi transform → **RẤT NẶNG CPU** cho dynamic objects
- inverse matrix tốn ~400 floating point ops → nên tính trên GPU hoặc dùng TransformAccessArray
- ObjectInstanceData chứa 2x Matrix4x4 + Vector4 = 132 bytes per instance → quá lớn, nên compress giống Static (pos + rot + scale = 40 bytes)
- Dispatch CeilToInt(count / 256f) nhưng compute dùng numthreads(256,1,1) → OK nhưng Static dùng 64, nên thống nhất
- Bounds hardcode `Vector3.one * 100000f` → quá lớn, frustum culling Unity level sẽ không loại được

### 1.4 Shader Analysis — ĐÁNH GIÁ: 8/10

**StaticSimpleLit + StaticCelLit:**
- Setup() dùng `#pragma instancing_options procedural : Setup` → đúng pattern
- CBUFFER_START/END đúng → SRP Batcher compatible
- 3 passes (ForwardLit, ShadowCaster, DepthOnly) → đầy đủ cho URP
- InstancingHelpers.hlsl: TRS_To_Matrix rất tối ưu, tính quaternion → rotation matrix inline

**Cần:**
- Không có DepthNormals pass → SSAO sẽ không work
- ShadowCaster pass lặp code Setup() → nên dùng HLSLINCLUDE + UsePass hoặc refactor
- Cel shading params cứng per-material → nên có global params để đảm bảo style đồng nhất

### 1.5 DynamicCulling.compute — ĐÁNH GIÁ: 6.5/10

**Vấn đề:**
- Append toàn bộ ObjectInstanceData (132 bytes) vào AppendBuffer → bandwidth waste lớn
- So với Static chỉ append uint index (4 bytes) thì Dynamic append gấp 33x data
- Frustum check dùng point + boundRadius (sphere test) thay vì AABB → kém chính xác
- `[unroll]` trên for loop 6 iterations → OK cho GPU nhưng thực tế compiler đã auto unroll

---

## ══════════════════════════════════════════════════
## PHẦN 2: HƯỚNG ĐI MỚI — RenderMeshInstanced
## ══════════════════════════════════════════════════

### Tại sao chuyển sang Graphics.RenderMeshInstanced?

| Feature | DrawMeshInstancedIndirect | RenderMeshInstanced |
|---------|--------------------------|---------------------|
| Max instances/call | Unlimited (GPU buffer) | 1023 (511 with worldToObject) |
| CPU overhead | Thấp nhất | Thấp |
| Setup complexity | Cao (ComputeBuffer, args) | Thấp (array Matrix4x4) |
| Light Probes | Manual | Built-in support |
| Motion Vectors | Manual | Built-in (prevObjectToWorld) |
| Rendering Layer | Manual | Built-in (renderingLayerMask) |
| SRP Batcher | No | Partial |

### Chiến lược Hybrid:
- **Static props (>100 instances)**: Giữ DrawMeshInstancedIndirect + Compute Culling (đã có, cải thiện)
- **Dynamic objects (10-500)**: Graphics.RenderMeshInstanced batched per 511
- **Vegetation/Grass (>1000)**: DrawMeshInstancedIndirect + Compute LOD
- **Characters (<50)**: RenderMeshInstanced hoặc SRP Batcher standard

---

## ══════════════════════════════════════════════════
## PHẦN 3: TASK BREAKDOWN — 12 MODULES
## ══════════════════════════════════════════════════

### MODULE 0: Core Infrastructure [PRIORITY: HIGHEST]
- [x] Task 0.1: Common HLSL includes (ToonLighting.hlsl, InstancingCore.hlsl, NoiseLib.hlsl)
- [x] Task 0.2: Unified CompressedInstanceData struct
- [x] Task 0.3: Base InstanceRenderer class (shared culling, batching, buffer management)
- [x] Task 0.4: Global Toon Settings ScriptableObject (đồng nhất style xuyên suốt)

### MODULE 1: ToonLit Shader [PRIORITY: HIGHEST]
- [x] Task 1.1: ToonLit.shader — Cel shading kiểu minionsart (smoothstep ramp, shadow color, rim)
- [x] Task 1.2: Support cả Static Instance + Dynamic Instance + Standard rendering
- [x] Task 1.3: Multi-light support (directional + point/spot via LIGHT_LOOP)
- [x] Task 1.4: 3 passes: ForwardLit, ShadowCaster, DepthOnly (+ DepthNormals cho SSAO)

### MODULE 2: ToonMetal Shader [PRIORITY: HIGH]
- [x] Task 2.1: ToonMetal.shader — View-dependent specular + hard light ramp
- [x] Task 2.2: Matcap/fresnel metallic highlight kiểu minionsart toon metal
- [x] Task 2.3: Share cùng cel shading core với ToonLit

### MODULE 3: Water/Lava Shader [PRIORITY: HIGH]
- [x] Task 3.1: ToonWater.shader — 2D noise texture scroll, NO procedural noise
- [x] Task 3.2: Triplanar mapping option cho waterfalls
- [x] Task 3.3: Editor bake tool: bake noise direction/flow map cho waterfall (dọc/chéo)
- [x] Task 3.4: ToonLava.shader — variant nóng, glow emission từ noise

### MODULE 4: Foliage — Tree System [PRIORITY: HIGH]
- [x] Task 4.1: ToonTree.shader — Instance-ready, wind animation vertex
- [x] Task 4.2: Bark + Leaves variants
- [x] Task 4.3: GPU LOD selection trong compute

### MODULE 5: Foliage — Grass System [PRIORITY: HIGH]
- [x] Task 5.1: ToonGrass.shader — Billboard grass, interactive (bend khi character đi qua)
- [x] Task 5.2: GrassPainter editor tool — paint grass lên terrain/mesh
- [x] Task 5.3: Bake grass positions trước play mode → ComputeBuffer indirect draw
- [x] Task 5.4: Static wind animation (noise-based vertex offset)
- [x] Task 5.5: Player interaction via global RT hoặc position buffer

### MODULE 6: Optimized Text Shader [PRIORITY: MEDIUM]
- [x] Task 6.1: SimpleText.shader — SDF-based như TMP nhưng lightweight
- [x] Task 6.2: Support font atlas assignment
- [x] Task 6.3: Instance-ready cho world-space text (damage numbers, nameplates)

### MODULE 7: Terrain/Triplanar Shader [PRIORITY: MEDIUM]
- [x] Task 7.1: ToonTerrain.shader — Splatmap height-based blending
- [x] Task 7.2: Triplanar projection cho cliffs
- [x] Task 7.3: Cel-shaded lighting consistent với ToonLit

### MODULE 8: Fog Volume Shader [PRIORITY: MEDIUM]
- [x] Task 8.1: CaveFog.shader — Volumetric-like fog plane/box
- [x] Task 8.2: Distance fade: tan dần khi lại gần, biến mất khi đủ gần
- [x] Task 8.3: Trigger system: vào trong cave → hide outside, show inside
- [x] Task 8.4: Soft edge blending

### MODULE 9: Improved Static Instance Manager [PRIORITY: HIGH]
- [x] Task 9.1: Refactor hash key (fix collision)
- [x] Task 9.2: Add Hi-Z occlusion culling pass
- [x] Task 9.3: Screen-size culling trong compute
- [x] Task 9.4: LOD crossfade dithering
- [x] Task 9.5: Cache CopyCount khi camera idle

### MODULE 10: Improved Dynamic Instance Manager [PRIORITY: HIGH]
- [x] Task 10.1: Compress data giống Static (pos+rot+scale thay vì 2x matrix)
- [x] Task 10.2: GPU-side matrix construction (tính TRS trên vertex shader)
- [x] Task 10.3: Batch by 511 cho RenderMeshInstanced fallback
- [x] Task 10.4: TransformAccessArray cho parallel CPU update

### MODULE 11: Editor Tools [PRIORITY: MEDIUM]
- [x] Task 11.1: Grass Painter tool
- [x] Task 11.2: Noise flow bake tool cho water
- [x] Task 11.3: One-click bake all instances
- [x] Task 11.4: Scene analyzer integration

### MODULE 12: Global Toon Style Config [PRIORITY: HIGH]
- [x] Task 12.1: ToonStyleConfig ScriptableObject
- [x] Task 12.2: Shared shadow color, rim color, ramp settings
- [x] Task 12.3: Runtime style switching (day/night mood)

---

## ══════════════════════════════════════════════════
## PHẦN 4: THỨ TỰ THỰC HIỆN
## ══════════════════════════════════════════════════

**Phase 1** (Core + Style): Module 0 → Module 12
**Phase 2** (Main Shaders): Module 1 → Module 2 → Module 3
**Phase 3** (Foliage): Module 4 → Module 5
**Phase 4** (Support): Module 6 → Module 7 → Module 8
**Phase 5** (Managers): Module 9 → Module 10
**Phase 6** (Tools): Module 11
