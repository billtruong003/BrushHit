using UnityEngine;
using UnityEngine.Rendering;
using System.Runtime.InteropServices;

namespace CleanRender
{
    /// <summary>
    /// Runtime GPU Indirect Draw manager for a single Mesh+Material group.
    /// Created by StaticInstanceSetup editor tool.
    /// 
    /// Pipeline:
    ///   SerializedInstanceData → ComputeBuffer → Compute Cull → AppendBuffer → DrawMeshInstancedIndirect
    /// 
    /// Features:
    ///   - Frustum culling on GPU (ImprovedStaticCulling.compute)
    ///   - Distance culling
    ///   - Screen-size culling
    ///   - Shadow buffer separation
    ///   - LOD range support
    ///   - Throttled culling (only re-cull when camera moves)
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class StaticInstanceManager : MonoBehaviour
    {
        [System.Serializable]
        public struct SerializedInstanceData
        {
            public Vector3 position;
            public Vector4 rotation; // quaternion xyzw
            public Vector3 scale;
        }

        [Header("━━━ Mesh & Material ━━━")]
        public Mesh instanceMesh;
        public Material instanceMaterial;

        [Header("━━━ Compute Shader ━━━")]
        public ComputeShader cullingShader;

        [Header("━━━ Distances ━━━")]
        public float cullDistance = 500f;
        public float shadowDistance = 150f;

        [Header("━━━ Performance ━━━")]
        [Range(0.01f, 0.1f)]
        public float cullInterval = 0.033f; // ~30fps culling
        public float moveThreshold = 0.5f;  // camera must move this far to re-cull

        [Header("━━━ Instance Data (serialized) ━━━")]
        [HideInInspector] public SerializedInstanceData[] instanceData;

        [Header("━━━ Source Tracking ━━━")]
        [HideInInspector] public GameObject[] sourceObjects;

        // ── GPU Buffers ──
        private ComputeBuffer _sourceBuffer;
        private ComputeBuffer _boundsBuffer;
        private ComputeBuffer _visibleBuffer;
        private ComputeBuffer _shadowBuffer;
        private ComputeBuffer _argsBuffer;
        private ComputeBuffer _shadowArgsBuffer;

        // ── State ──
        private int _count;
        private int _kernelID;
        private Camera _mainCamera;
        private Transform _camTransform;
        private Plane[] _cameraPlanes = new Plane[6];
        private Vector4[] _frustumV4 = new Vector4[6];
        private Vector3 _lastCamPos;
        private Quaternion _lastCamRot;
        private float _lastCullTime;
        private Bounds _globalBounds;
        private MaterialPropertyBlock _props;
        private bool _initialized;

        // ── Compressed struct matching InstancingCore.hlsl ──
        [StructLayout(LayoutKind.Sequential)]
        private struct CompressedGPU
        {
            public Vector3 position;
            public Vector3 scale;
            public Vector4 rotation;
            public Vector2 lodRange;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BoundsGPU
        {
            public Vector3 center;
            public Vector3 extents;
        }

        private void Start()
        {
            Initialize();
        }

        private void Initialize()
        {
            if (instanceData == null || instanceData.Length == 0) return;
            if (instanceMesh == null || instanceMaterial == null) return;
            if (cullingShader == null)
            {
                Debug.LogError($"[StaticInstanceManager] {name}: Missing culling compute shader!");
                return;
            }

            _mainCamera = Camera.main;
            if (_mainCamera != null) _camTransform = _mainCamera.transform;

            _count = instanceData.Length;
            float meshExtent = instanceMesh.bounds.extents.magnitude;
            float maxCullSq = cullDistance * cullDistance;

            // Build GPU data
            var gpuData = new CompressedGPU[_count];
            var boundsData = new BoundsGPU[_count];
            Vector3 min = Vector3.one * float.MaxValue;
            Vector3 max = Vector3.one * float.MinValue;

            for (int i = 0; i < _count; i++)
            {
                var src = instanceData[i];
                float maxScale = Mathf.Max(src.scale.x, Mathf.Max(src.scale.y, src.scale.z));
                float ext = meshExtent * maxScale;

                gpuData[i] = new CompressedGPU
                {
                    position = src.position,
                    scale = src.scale,
                    rotation = src.rotation,
                    lodRange = new Vector2(0, maxCullSq)
                };

                boundsData[i] = new BoundsGPU
                {
                    center = src.position,
                    extents = Vector3.one * ext
                };

                min = Vector3.Min(min, src.position - Vector3.one * ext);
                max = Vector3.Max(max, src.position + Vector3.one * ext);
            }

            _globalBounds = new Bounds((min + max) * 0.5f, (max - min) + Vector3.one);

            // Create buffers
            _sourceBuffer = new ComputeBuffer(_count, Marshal.SizeOf<CompressedGPU>());
            _sourceBuffer.SetData(gpuData);

            _boundsBuffer = new ComputeBuffer(_count, Marshal.SizeOf<BoundsGPU>());
            _boundsBuffer.SetData(boundsData);

            _visibleBuffer = new ComputeBuffer(_count, sizeof(uint), ComputeBufferType.Append);
            _shadowBuffer = new ComputeBuffer(_count, sizeof(uint), ComputeBufferType.Append);

            // Args buffer: indexCount, instanceCount, indexStart, baseVertex, startInstance
            uint[] args = new uint[5]
            {
                instanceMesh.GetIndexCount(0),
                0, // filled by compute
                instanceMesh.GetIndexStart(0),
                instanceMesh.GetBaseVertex(0),
                0
            };
            _argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
            _argsBuffer.SetData(args);

            _shadowArgsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
            _shadowArgsBuffer.SetData(args);

            _kernelID = cullingShader.FindKernel("CSMain");

            _props = new MaterialPropertyBlock();
            _props.SetBuffer("_SourceData", _sourceBuffer);

            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized) return;

            if (_mainCamera == null)
            {
                _mainCamera = Camera.main;
                if (_mainCamera != null) _camTransform = _mainCamera.transform;
                else return;
            }

            bool shouldCull = Time.time - _lastCullTime > cullInterval &&
                (Vector3.Distance(_camTransform.position, _lastCamPos) > moveThreshold ||
                 Quaternion.Angle(_camTransform.rotation, _lastCamRot) > 1f);

            if (shouldCull)
                PerformCulling();

            DrawInstances();
        }

        private void PerformCulling()
        {
            GeometryUtility.CalculateFrustumPlanes(_mainCamera, _cameraPlanes);
            for (int i = 0; i < 6; i++)
            {
                var n = _cameraPlanes[i].normal;
                _frustumV4[i] = new Vector4(n.x, n.y, n.z, _cameraPlanes[i].distance);
            }

            _visibleBuffer.SetCounterValue(0);
            _shadowBuffer.SetCounterValue(0);

            cullingShader.SetVectorArray("_CameraPlanes", _frustumV4);
            cullingShader.SetVector("_CameraPosition", _camTransform.position);
            cullingShader.SetVector("_CameraForward", _camTransform.forward);
            cullingShader.SetFloat("_MaxDistanceSq", cullDistance * cullDistance);
            cullingShader.SetFloat("_ShadowDistanceSq", shadowDistance * shadowDistance);
            cullingShader.SetInt("_Count", _count);
            cullingShader.SetFloat("_ScreenHeight", Screen.height);
            cullingShader.SetFloat("_FOVFactor",
                2f * Mathf.Tan(_mainCamera.fieldOfView * 0.5f * Mathf.Deg2Rad));
            cullingShader.SetFloat("_MinScreenSize", 2f);

            cullingShader.SetBuffer(_kernelID, "_SourceData", _sourceBuffer);
            cullingShader.SetBuffer(_kernelID, "_SourceBounds", _boundsBuffer);
            cullingShader.SetBuffer(_kernelID, "_VisibleIndices", _visibleBuffer);
            cullingShader.SetBuffer(_kernelID, "_ShadowIndices", _shadowBuffer);

            int threadGroups = Mathf.CeilToInt(_count / 64f);
            cullingShader.Dispatch(_kernelID, threadGroups, 1, 1);

            // Copy visible count into args buffer
            ComputeBuffer.CopyCount(_visibleBuffer, _argsBuffer, sizeof(uint)); // offset to instanceCount
            ComputeBuffer.CopyCount(_shadowBuffer, _shadowArgsBuffer, sizeof(uint));

            _lastCamPos = _camTransform.position;
            _lastCamRot = _camTransform.rotation;
            _lastCullTime = Time.time;
        }

        private void DrawInstances()
        {
            _props.SetBuffer("_VisibleIndices", _visibleBuffer);

            Graphics.DrawMeshInstancedIndirect(
                instanceMesh, 0, instanceMaterial,
                _globalBounds, _argsBuffer, 0, _props);
        }

        private void OnDestroy()
        {
            _sourceBuffer?.Release();
            _boundsBuffer?.Release();
            _visibleBuffer?.Release();
            _shadowBuffer?.Release();
            _argsBuffer?.Release();
            _shadowArgsBuffer?.Release();
            _initialized = false;
        }

        private void OnDrawGizmosSelected()
        {
            if (!_initialized && instanceData != null && instanceData.Length > 0)
            {
                // Show bounds in editor
                Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.15f);
                Vector3 min = Vector3.one * float.MaxValue;
                Vector3 max = Vector3.one * float.MinValue;
                foreach (var d in instanceData)
                {
                    min = Vector3.Min(min, d.position - Vector3.one);
                    max = Vector3.Max(max, d.position + Vector3.one);
                }
                var bounds = new Bounds((min + max) * 0.5f, max - min);
                Gizmos.DrawCube(bounds.center, bounds.size);
                Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.5f);
                Gizmos.DrawWireCube(bounds.center, bounds.size);
            }
            else if (_initialized)
            {
                Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.1f);
                Gizmos.DrawCube(_globalBounds.center, _globalBounds.size);
                Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.4f);
                Gizmos.DrawWireCube(_globalBounds.center, _globalBounds.size);
            }
        }
    }
}
