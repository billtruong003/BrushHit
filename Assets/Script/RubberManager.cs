using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// RubberManager v5 — WebGL Compatible
///
/// Thay đổi so với v4:
///   - StructuredBuffer  → MaterialPropertyBlock + SetVectorArray
///   - DrawMeshInstancedIndirect → DrawMeshInstanced (max 1023/batch)
///   - ComputeBuffer → KHÔNG dùng (WebGL không hỗ trợ)
///   - Spring sim throttle: chạy ở tốc độ tuỳ chỉnh (mặc định 24Hz)
///     trong khi render vẫn mỗi frame
///
/// Data packing (2 Vector4 per instance):
///   _InstPosTouch = (posX, posY, posZ, touched)
///   _InstDataSpring = (scaleX, scaleY, scaleZ, spring)
///
/// Batch: mỗi 1023 instances = 1 DrawMeshInstanced call
///        5000 rubber = 5 batch = 5 draw calls (vẫn rất tốt)
/// </summary>
public class RubberManager : MonoBehaviour
{
    public static RubberManager Instance { get; private set; }

    [Header("Mesh & Material")]
    [SerializeField] private Mesh rubberMesh;
    [SerializeField] private Material rubberMaterial;

    [Header("Instance Settings")]
    [SerializeField] private Vector3 rubberScale = new Vector3(0.5f, 0.5f, 0.5f);

    [Header("Collision")]
    [SerializeField] private float collisionRadius = 0.6f;

    [Header("Spring Physics")]
    [SerializeField] private float springStiffness = 80f;
    [SerializeField] private float springDamping = 6f;
    [SerializeField] private float squishTarget = 0.7f;
    [SerializeField] private float sleepThreshold = 0.001f;
    [SerializeField] private float springRadius = 1.5f;

    [Header("Performance")]
    [Tooltip("Số lần render rubber mỗi giây. 24 = smooth đủ, giảm xuống 12-16 nếu cần nhẹ hơn")]
    [SerializeField] private int renderTickRate = 24;

    [Header("Colors")]
    [SerializeField] private Color touchedColor = Color.green;

    [Header("Bounds (cho frustum culling)")]
    [SerializeField] private Vector3 boundsCenter = Vector3.zero;
    [SerializeField] private Vector3 boundsSize = new Vector3(500, 50, 500);

    // ── Constants ──
    private const int MAX_BATCH = 1023; // Unity limit cho DrawMeshInstanced

    // ── Player parts ──
    private Transform[] playerParts;
    private bool hasPlayerParts;

    private static readonly int PlayerCountID = Shader.PropertyToID("_PlayerPartCount");
    private static readonly int PlayerPos0ID = Shader.PropertyToID("_PlayerPos0");
    private static readonly int PlayerPos1ID = Shader.PropertyToID("_PlayerPos1");
    private static readonly int PlayerPos2ID = Shader.PropertyToID("_PlayerPos2");
    private static readonly int InstPosID = Shader.PropertyToID("_InstPosTouch");
    private static readonly int InstDataID = Shader.PropertyToID("_InstDataSpring");

    // ── CPU data ──
    private int totalCount;
    private Vector3[] positions;
    private bool[] touched;

    // ── Spring state ──
    private float[] springValues;
    private float[] springVelocities;
    private HashSet<int> activeSpringSet;
    private List<int> activeSpringList;
    private HashSet<int> currentlyPressedSet;

    // ── Batching ──
    private int batchCount;
    private Matrix4x4[][] batchMatrices;
    private MaterialPropertyBlock[] batchProps;
    private Vector4[][] batchPosTouch;     // (posX, posY, posZ, touched)
    private Vector4[][] batchDataSpring;   // (scaleX, scaleY, scaleZ, spring)
    private int[] batchSizes;
    private bool[] batchDirty;             // Per-batch dirty flag

    // ── Throttle ──
    private float springAccumulator;
    private float springInterval;

    // ── Spatial hash ──
    private Dictionary<long, List<int>> grid;
    private float cellSize;

    private bool initialized;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    // ══════════════════════════════════════════════════════════
    // Public API
    // ══════════════════════════════════════════════════════════

    public void RegisterPlayerParts(Transform head1, Transform head2, Transform body)
    {
        playerParts = new Transform[] { head1, head2, body };
        hasPlayerParts = true;
    }

    public void RegisterRubberPositions(List<Vector3> pos)
    {
        totalCount = pos.Count;
        positions = pos.ToArray();
        touched = new bool[totalCount];

        springValues = new float[totalCount];
        springVelocities = new float[totalCount];
        activeSpringSet = new HashSet<int>();
        activeSpringList = new List<int>(256);
        currentlyPressedSet = new HashSet<int>();

        GameSpawn.sum_object = totalCount;

        // ── Spatial grid ──
        cellSize = Mathf.Max(collisionRadius * 2.5f, 0.5f);
        grid = new Dictionary<long, List<int>>(totalCount / 4);
        for (int i = 0; i < totalCount; i++)
        {
            long key = CellKey(positions[i]);
            if (!grid.ContainsKey(key))
                grid[key] = new List<int>(8);
            grid[key].Add(i);
        }

        // ── Setup batches ──
        batchCount = (totalCount + MAX_BATCH - 1) / MAX_BATCH;
        batchMatrices = new Matrix4x4[batchCount][];
        batchProps = new MaterialPropertyBlock[batchCount];
        batchPosTouch = new Vector4[batchCount][];
        batchDataSpring = new Vector4[batchCount][];
        batchSizes = new int[batchCount];
        batchDirty = new bool[batchCount];

        for (int b = 0; b < batchCount; b++)
        {
            int start = b * MAX_BATCH;
            int count = Mathf.Min(MAX_BATCH, totalCount - start);
            batchSizes[b] = count;

            batchMatrices[b] = new Matrix4x4[count];
            batchPosTouch[b] = new Vector4[count];
            batchDataSpring[b] = new Vector4[count];
            batchProps[b] = new MaterialPropertyBlock();

            for (int i = 0; i < count; i++)
            {
                int idx = start + i;
                Vector3 p = positions[idx];

                // Matrix: identity rotation, custom scale, at position
                batchMatrices[b][i] = Matrix4x4.TRS(p, Quaternion.identity, rubberScale);

                batchPosTouch[b][i] = new Vector4(p.x, p.y, p.z, 0f);
                batchDataSpring[b][i] = new Vector4(rubberScale.x, rubberScale.y, rubberScale.z, 0f);
            }

            // Upload initial data
            batchProps[b].SetVectorArray(InstPosID, batchPosTouch[b]);
            batchProps[b].SetVectorArray(InstDataID, batchDataSpring[b]);
        }

        // Material global colors
        rubberMaterial.SetColor("_TouchedColor", touchedColor);

        // Throttle
        springInterval = 1f / Mathf.Max(renderTickRate, 1);
        springAccumulator = 0f;

        initialized = true;
        Debug.Log($"[RubberManager] {totalCount} rubbers in {batchCount} batches (WebGL ready)");
    }

    // ══════════════════════════════════════════════════════════
    // Update — Throttled sim & upload, draw mỗi frame
    //
    // DrawMeshInstanced chỉ render 1 frame, nên phải gọi mỗi frame.
    // Cái nặng là spring sim + SetVectorArray upload → throttle cái này.
    //
    // Ví dụ renderTickRate=24 ở game 60fps:
    //   - Draw: 60 lần/s (bắt buộc, nhưng nhẹ vì data không đổi)
    //   - Upload: 24 lần/s (tiết kiệm ~60% CPU bandwidth)
    //   - Spring sim: 24 lần/s
    //   - Collision: 60 lần/s (nhẹ, spatial hash)
    // ══════════════════════════════════════════════════════════

    private void Update()
    {
        if (!initialized) return;

        // ── Collision detection — mỗi frame (nhẹ, chỉ check vài cell) ──
        if (hasPlayerParts)
            CheckAllPartsCollision();

        // ── Player positions cho shader XZ push — mỗi frame (1 SetVector call) ──
        UpdateShaderPlayerPositions();

        // ── Throttle: spring sim + upload ──
        springAccumulator += Time.deltaTime;

        if (springAccumulator >= springInterval)
        {
            float simDt = springAccumulator;
            springAccumulator = 0f;

            // Spring sim
            if (hasPlayerParts)
                UpdateNearbySpringTargets();
            SimulateSprings(simDt);

            // Upload chỉ dirty batches
            UploadDirtyBatches();
        }

        // ── Draw — MỖI FRAME (bắt buộc, DrawMeshInstanced không persist) ──
        DrawAllBatches();
    }

    // ══════════════════════════════════════════════════════════
    // Upload chỉ dirty batches (tiết kiệm bandwidth đáng kể)
    // ══════════════════════════════════════════════════════════

    private void UploadDirtyBatches()
    {
        for (int b = 0; b < batchCount; b++)
        {
            if (!batchDirty[b]) continue;

            int start = b * MAX_BATCH;
            int count = batchSizes[b];

            for (int i = 0; i < count; i++)
            {
                int idx = start + i;
                Vector3 p = positions[idx];

                batchPosTouch[b][i] = new Vector4(p.x, p.y, p.z, touched[idx] ? 1f : 0f);
                batchDataSpring[b][i].w = springValues[idx];
            }

            batchProps[b].SetVectorArray(InstPosID, batchPosTouch[b]);
            batchProps[b].SetVectorArray(InstDataID, batchDataSpring[b]);
            batchDirty[b] = false;
        }
    }

    // ══════════════════════════════════════════════════════════
    // Draw all batches
    // ══════════════════════════════════════════════════════════

    private void DrawAllBatches()
    {
        for (int b = 0; b < batchCount; b++)
        {
            Graphics.DrawMeshInstanced(
                rubberMesh, 0, rubberMaterial,
                batchMatrices[b],
                batchSizes[b],
                batchProps[b]
            );
        }
    }

    // ══════════════════════════════════════════════════════════
    // Shader player positions
    // ══════════════════════════════════════════════════════════

    private void UpdateShaderPlayerPositions()
    {
        Vector4 far = new Vector4(9999, 9999, 9999, 0);

        if (!hasPlayerParts || playerParts == null)
        {
            rubberMaterial.SetFloat(PlayerCountID, 0);
            rubberMaterial.SetVector(PlayerPos0ID, far);
            rubberMaterial.SetVector(PlayerPos1ID, far);
            rubberMaterial.SetVector(PlayerPos2ID, far);
            return;
        }

        int count = 0;
        Vector4[] posArray = new Vector4[3] { far, far, far };

        for (int i = 0; i < playerParts.Length && i < 3; i++)
        {
            if (playerParts[i] != null)
            {
                Vector3 p = playerParts[i].position;
                posArray[i] = new Vector4(p.x, p.y, p.z, 0);
                count++;
            }
        }

        rubberMaterial.SetFloat(PlayerCountID, count);
        rubberMaterial.SetVector(PlayerPos0ID, posArray[0]);
        rubberMaterial.SetVector(PlayerPos1ID, posArray[1]);
        rubberMaterial.SetVector(PlayerPos2ID, posArray[2]);
    }

    // ══════════════════════════════════════════════════════════
    // Spring: find nearby rubbers → activate
    // ══════════════════════════════════════════════════════════

    private void UpdateNearbySpringTargets()
    {
        currentlyPressedSet.Clear();
        float rSq = springRadius * springRadius;

        for (int p = 0; p < 2; p++)
        {
            if (playerParts[p] == null) continue;
            GatherNearby(playerParts[p].position, rSq, currentlyPressedSet);
        }

        if (playerParts[0] != null && playerParts[1] != null)
            GatherNearbySegment(playerParts[0].position, playerParts[1].position, rSq, currentlyPressedSet);

        foreach (int idx in currentlyPressedSet)
        {
            if (!activeSpringSet.Contains(idx))
                activeSpringSet.Add(idx);
        }
    }

    private void GatherNearby(Vector3 point, float rSq, HashSet<int> output)
    {
        int cx = Mathf.FloorToInt(point.x / cellSize);
        int cz = Mathf.FloorToInt(point.z / cellSize);
        int range = Mathf.CeilToInt(springRadius / cellSize);

        for (int dx = -range; dx <= range; dx++)
            for (int dz = -range; dz <= range; dz++)
            {
                long key = PackKey(cx + dx, cz + dz);
                if (!grid.TryGetValue(key, out List<int> cell)) continue;
                for (int c = 0; c < cell.Count; c++)
                {
                    int idx = cell[c];
                    Vector3 rPos = positions[idx];
                    float distSq = (point.x - rPos.x) * (point.x - rPos.x)
                                 + (point.z - rPos.z) * (point.z - rPos.z);
                    if (distSq <= rSq) output.Add(idx);
                }
            }
    }

    private void GatherNearbySegment(Vector3 a, Vector3 b, float rSq, HashSet<int> output)
    {
        float minX = Mathf.Min(a.x, b.x) - springRadius;
        float maxX = Mathf.Max(a.x, b.x) + springRadius;
        float minZ = Mathf.Min(a.z, b.z) - springRadius;
        float maxZ = Mathf.Max(a.z, b.z) + springRadius;

        int cMinX = Mathf.FloorToInt(minX / cellSize), cMaxX = Mathf.FloorToInt(maxX / cellSize);
        int cMinZ = Mathf.FloorToInt(minZ / cellSize), cMaxZ = Mathf.FloorToInt(maxZ / cellSize);

        float abx = b.x - a.x, abz = b.z - a.z;
        float abLenSq = abx * abx + abz * abz;

        for (int cx = cMinX; cx <= cMaxX; cx++)
            for (int cz = cMinZ; cz <= cMaxZ; cz++)
            {
                long key = PackKey(cx, cz);
                if (!grid.TryGetValue(key, out List<int> cell)) continue;
                for (int c = 0; c < cell.Count; c++)
                {
                    int idx = cell[c];
                    float distSq = PtSegDistSqXZ(positions[idx].x, positions[idx].z, a.x, a.z, abx, abz, abLenSq);
                    if (distSq <= rSq) output.Add(idx);
                }
            }
    }

    // ══════════════════════════════════════════════════════════
    // Spring Simulation (throttled)
    // ══════════════════════════════════════════════════════════

    private void SimulateSprings(float dt)
    {
        if (activeSpringSet.Count == 0 || dt <= 0f) return;

        activeSpringList.Clear();
        activeSpringList.AddRange(activeSpringSet);

        for (int i = 0; i < activeSpringList.Count; i++)
        {
            int idx = activeSpringList[i];
            bool pressed = currentlyPressedSet.Contains(idx);
            float target = pressed ? squishTarget : 0f;

            float x = springValues[idx];
            float v = springVelocities[idx];
            float force = springStiffness * (target - x) - springDamping * v;
            v += force * dt;
            x += v * dt;

            springValues[idx] = x;
            springVelocities[idx] = v;
            batchDirty[idx / MAX_BATCH] = true;

            if (!pressed && Mathf.Abs(x) < sleepThreshold && Mathf.Abs(v) < sleepThreshold)
            {
                springValues[idx] = 0f;
                springVelocities[idx] = 0f;
                activeSpringSet.Remove(idx);
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    // Collision — touched state (score)
    // ══════════════════════════════════════════════════════════

    private void CheckAllPartsCollision()
    {
        if (playerParts == null || playerParts.Length < 3) return;
        float rSq = collisionRadius * collisionRadius;

        for (int p = 0; p < 2; p++)
        {
            if (playerParts[p] == null) continue;
            CheckPointCollision(playerParts[p].position, rSq);
        }

        if (playerParts[0] != null && playerParts[1] != null)
            CheckSegmentCollision(playerParts[0].position, playerParts[1].position, rSq);
    }

    private void CheckPointCollision(Vector3 point, float rSq)
    {
        int cx = Mathf.FloorToInt(point.x / cellSize);
        int cz = Mathf.FloorToInt(point.z / cellSize);

        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                long key = PackKey(cx + dx, cz + dz);
                if (!grid.TryGetValue(key, out List<int> cell)) continue;
                for (int c = 0; c < cell.Count; c++)
                {
                    int idx = cell[c];
                    if (touched[idx]) continue;
                    Vector3 rPos = positions[idx];
                    float distSq = (point.x - rPos.x) * (point.x - rPos.x)
                                 + (point.z - rPos.z) * (point.z - rPos.z);
                    if (distSq <= rSq) MarkTouched(idx);
                }
            }
    }

    private void CheckSegmentCollision(Vector3 a, Vector3 b, float rSq)
    {
        float minX = Mathf.Min(a.x, b.x) - collisionRadius;
        float maxX = Mathf.Max(a.x, b.x) + collisionRadius;
        float minZ = Mathf.Min(a.z, b.z) - collisionRadius;
        float maxZ = Mathf.Max(a.z, b.z) + collisionRadius;

        int cMinX = Mathf.FloorToInt(minX / cellSize), cMaxX = Mathf.FloorToInt(maxX / cellSize);
        int cMinZ = Mathf.FloorToInt(minZ / cellSize), cMaxZ = Mathf.FloorToInt(maxZ / cellSize);

        float abx = b.x - a.x, abz = b.z - a.z;
        float abLenSq = abx * abx + abz * abz;

        for (int cx = cMinX; cx <= cMaxX; cx++)
            for (int cz = cMinZ; cz <= cMaxZ; cz++)
            {
                long key = PackKey(cx, cz);
                if (!grid.TryGetValue(key, out List<int> cell)) continue;
                for (int c = 0; c < cell.Count; c++)
                {
                    int idx = cell[c];
                    if (touched[idx]) continue;
                    float distSq = PtSegDistSqXZ(positions[idx].x, positions[idx].z, a.x, a.z, abx, abz, abLenSq);
                    if (distSq <= rSq) MarkTouched(idx);
                }
            }
    }

    private float PtSegDistSqXZ(float px, float pz, float ax, float az, float abx, float abz, float abLenSq)
    {
        if (abLenSq < 0.0001f) { float dx = px - ax, dz = pz - az; return dx * dx + dz * dz; }
        float t = Mathf.Clamp01(((px - ax) * abx + (pz - az) * abz) / abLenSq);
        float cx = ax + t * abx, cz = az + t * abz;
        float dx2 = px - cx, dz2 = pz - cz;
        return dx2 * dx2 + dz2 * dz2;
    }

    private void MarkTouched(int idx)
    {
        touched[idx] = true;
        batchDirty[idx / MAX_BATCH] = true;
        GameSpawn.numberObTrue++;
        GameSpawn.score++;
    }

    // ── Spatial hash ──
    private long CellKey(Vector3 pos) => PackKey(Mathf.FloorToInt(pos.x / cellSize), Mathf.FloorToInt(pos.z / cellSize));
    private long PackKey(int x, int z) => ((long)x << 32) | ((long)z & 0xFFFFFFFFL);
}