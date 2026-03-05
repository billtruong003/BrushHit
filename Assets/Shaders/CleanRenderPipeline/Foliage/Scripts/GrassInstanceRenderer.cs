using UnityEngine;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace CleanRender
{
    /// <summary>
    /// Runtime renderer for baked grass instances.
    /// Uses compute shader culling + DrawMeshInstancedIndirect.
    /// Supports player interaction (bend grass when walking through).
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class GrassInstanceRenderer : MonoBehaviour
    {
        [System.Serializable]
        [StructLayout(LayoutKind.Sequential)]
        public struct GrassInstanceData
        {
            public Vector3 position;
            public Vector3 scale;
            public Vector4 rotation;
            public Vector2 lodRange;
        }

        [Header("━━━ Data Source ━━━")]
        [SerializeField] private Mesh grassMesh;
        [SerializeField] private Material grassMaterial;
        [SerializeField] private ComputeShader cullingShader;

        [Header("━━━ Settings ━━━")]
        [SerializeField] private float cullDistance = 100f;
        [SerializeField] private float shadowDistance = 30f;
        [SerializeField, Range(0.01f, 0.1f)] private float cullInterval = 0.033f;
        [SerializeField] private float moveThreshold = 0.2f;

        [Header("━━━ Interaction ━━━")]
        [SerializeField] private bool enableInteraction = true;
        [SerializeField] private int maxInteractors = 8;

        private ComputeBuffer _sourceBuffer;
        private ComputeBuffer _boundsBuffer;
        private ComputeBuffer _visibleIndexBuffer;
        private ComputeBuffer _argsBuffer;
        private MaterialPropertyBlock _props;
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
        private bool _initialized;

        // Interaction
        private static readonly List<Transform> _interactors = new List<Transform>();
        private Vector4[] _interactorData;

        /// <summary>
        /// Register an interactor (player, NPC, etc.) that bends grass
        /// </summary>
        public static void RegisterInteractor(Transform t) { if (!_interactors.Contains(t)) _interactors.Add(t); }
        public static void UnregisterInteractor(Transform t) { _interactors.Remove(t); }

        private void Start()
        {
            _mainCamera = Camera.main;
            if (_mainCamera) _camTransform = _mainCamera.transform;
            _interactorData = new Vector4[maxInteractors];

            // Try to load baked data from GrassDataHolder
            var holder = GetComponent<CleanRender.RenderBiomes.GrassDataHolder>();
            if (holder != null && holder.InstanceCount > 0)
            {
                if (holder.grassMesh) grassMesh = holder.grassMesh;
                if (holder.grassMaterial) grassMaterial = holder.grassMaterial;
                InitializeFromHolder(holder);
            }
        }

        private void InitializeFromHolder(CleanRender.RenderBiomes.GrassDataHolder holder)
        {
            _count = holder.InstanceCount;
            if (_count == 0 || grassMesh == null || grassMaterial == null) return;

            var data = new GrassInstanceData[_count];
            var bounds = new Vector3[_count];
            Vector3 min = Vector3.one * float.MaxValue;
            Vector3 max = Vector3.one * float.MinValue;

            float meshExtent = grassMesh.bounds.extents.magnitude;

            for (int i = 0; i < _count; i++)
            {
                data[i] = new GrassInstanceData
                {
                    position = holder.positions[i],
                    scale = holder.scales[i],
                    rotation = holder.rotations[i],
                    lodRange = new Vector2(0, cullDistance * cullDistance)
                };

                min = Vector3.Min(min, holder.positions[i] - Vector3.one * meshExtent);
                max = Vector3.Max(max, holder.positions[i] + Vector3.one * meshExtent);
            }

            _globalBounds = new Bounds((min + max) * 0.5f, max - min);
            InitializeBuffers(data);
        }

        /// <summary>
        /// Initialize from external data (e.g., runtime procedural generation)
        /// </summary>
        public void InitializeFromData(GrassInstanceData[] data, Bounds bounds)
        {
            _count = data.Length;
            _globalBounds = bounds;
            InitializeBuffers(data);
        }

        private void InitializeBuffers(GrassInstanceData[] data)
        {
            ReleaseBuffers();

            _sourceBuffer = new ComputeBuffer(_count, Marshal.SizeOf<GrassInstanceData>());
            _sourceBuffer.SetData(data);

            _visibleIndexBuffer = new ComputeBuffer(_count, sizeof(uint), ComputeBufferType.Append);

            _argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
            uint[] args = { (uint)grassMesh.GetIndexCount(0), 0, (uint)grassMesh.GetIndexStart(0), (uint)grassMesh.GetBaseVertex(0), 0 };
            _argsBuffer.SetData(args);

            _kernelID = cullingShader.FindKernel("CSMain");

            _props = new MaterialPropertyBlock();
            _props.SetBuffer("_SourceData", _sourceBuffer);

            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized || _count == 0) return;
            if (!_mainCamera) { _mainCamera = Camera.main; if (_mainCamera) _camTransform = _mainCamera.transform; else return; }

            bool shouldCull = Time.time - _lastCullTime > cullInterval &&
                (Vector3.Distance(_camTransform.position, _lastCamPos) > moveThreshold ||
                 Quaternion.Angle(_camTransform.rotation, _lastCamRot) > 1f);

            if (shouldCull)
                PerformCulling();

            UpdateInteraction();
            DrawGrass();
        }

        private void PerformCulling()
        {
            GeometryUtility.CalculateFrustumPlanes(_mainCamera, _cameraPlanes);
            for (int i = 0; i < 6; i++)
            {
                var n = _cameraPlanes[i].normal;
                _frustumV4[i] = new Vector4(n.x, n.y, n.z, _cameraPlanes[i].distance);
            }

            _visibleIndexBuffer.SetCounterValue(0);

            cullingShader.SetVectorArray("_CameraPlanes", _frustumV4);
            cullingShader.SetVector("_CameraPosition", _camTransform.position);
            cullingShader.SetFloat("_MaxDistanceSq", cullDistance * cullDistance);
            cullingShader.SetFloat("_ShadowDistanceSq", shadowDistance * shadowDistance);
            cullingShader.SetInt("_Count", _count);
            cullingShader.SetFloat("_ScreenHeight", Screen.height);
            cullingShader.SetFloat("_FOVFactor", 2f * Mathf.Tan(_mainCamera.fieldOfView * 0.5f * Mathf.Deg2Rad));
            cullingShader.SetFloat("_MinScreenSize", 1f); // grass can be tiny

            cullingShader.SetBuffer(_kernelID, "_SourceData", _sourceBuffer);
            cullingShader.SetBuffer(_kernelID, "_VisibleIndices", _visibleIndexBuffer);

            int threadGroups = Mathf.CeilToInt(_count / 64f);
            cullingShader.Dispatch(_kernelID, threadGroups, 1, 1);

            ComputeBuffer.CopyCount(_visibleIndexBuffer, _argsBuffer, 4);

            _lastCamPos = _camTransform.position;
            _lastCamRot = _camTransform.rotation;
            _lastCullTime = Time.time;
        }

        private void UpdateInteraction()
        {
            if (!enableInteraction) return;

            // Clean null interactors
            _interactors.RemoveAll(t => t == null);

            int count = Mathf.Min(_interactors.Count, maxInteractors);
            for (int i = 0; i < maxInteractors; i++)
            {
                if (i < count)
                {
                    var pos = _interactors[i].position;
                    _interactorData[i] = new Vector4(pos.x, pos.y, pos.z, 1.5f); // default radius
                }
                else
                {
                    _interactorData[i] = Vector4.zero;
                }
            }

            grassMaterial.SetVectorArray("_InteractorPositions", _interactorData);
            grassMaterial.SetInt("_InteractorCount", count);
        }

        private void DrawGrass()
        {
            _props.SetBuffer("_VisibleIndices", _visibleIndexBuffer);
            Graphics.DrawMeshInstancedIndirect(
                grassMesh, 0, grassMaterial,
                _globalBounds, _argsBuffer, 0, _props);
        }

        private void OnDestroy() => ReleaseBuffers();

        private void ReleaseBuffers()
        {
            _sourceBuffer?.Release();
            _visibleIndexBuffer?.Release();
            _argsBuffer?.Release();
            _initialized = false;
        }
    }

    /// <summary>
    /// Attach to player/characters to interact with grass
    /// </summary>
    public class FoliageInteractor : MonoBehaviour
    {
        private void OnEnable() => GrassInstanceRenderer.RegisterInteractor(transform);
        private void OnDisable() => GrassInstanceRenderer.UnregisterInteractor(transform);
    }
}
