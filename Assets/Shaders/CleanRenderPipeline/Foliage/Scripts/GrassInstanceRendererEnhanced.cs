using UnityEngine;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace CleanRender
{
    /// <summary>
    /// Enhanced runtime renderer for baked grass instances.
    /// Uses compute shader culling + DrawMeshInstancedIndirect.
    /// Supports player interaction with dynamic radius from PlayerFoliageInteractor.
    /// 
    /// Changes from original:
    ///   - Reads GetShaderData() from PlayerFoliageInteractor for per-interactor radius
    ///   - Fallback to default radius if interactor doesn't have the component
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class GrassInstanceRendererEnhanced : MonoBehaviour
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
        [SerializeField] private float defaultInteractRadius = 1.5f;

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

        public static void RegisterInteractor(Transform t)
        {
            if (!_interactors.Contains(t)) _interactors.Add(t);
        }

        public static void UnregisterInteractor(Transform t)
        {
            _interactors.Remove(t);
        }

        private void Start()
        {
            _mainCamera = Camera.main;
            if (_mainCamera) _camTransform = _mainCamera.transform;
            _interactorData = new Vector4[maxInteractors];

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
            if (!_mainCamera)
            {
                _mainCamera = Camera.main;
                if (_mainCamera) _camTransform = _mainCamera.transform;
                else return;
            }

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
            cullingShader.SetFloat("_MinScreenSize", 1f);

            cullingShader.SetBuffer(_kernelID, "_SourceData", _sourceBuffer);
            cullingShader.SetBuffer(_kernelID, "_VisibleIndices", _visibleIndexBuffer);

            int threadGroups = Mathf.CeilToInt(_count / 64f);
            cullingShader.Dispatch(_kernelID, threadGroups, 1, 1);

            ComputeBuffer.CopyCount(_visibleIndexBuffer, _argsBuffer, 4);

            _lastCamPos = _camTransform.position;
            _lastCamRot = _camTransform.rotation;
            _lastCullTime = Time.time;
        }

        /// <summary>
        /// Enhanced interaction: reads per-interactor radius from PlayerFoliageInteractor
        /// </summary>
        private void UpdateInteraction()
        {
            if (!enableInteraction) return;

            _interactors.RemoveAll(t => t == null);

            int count = Mathf.Min(_interactors.Count, maxInteractors);
            for (int i = 0; i < maxInteractors; i++)
            {
                if (i < count)
                {
                    // Try to get enhanced data from PlayerFoliageInteractor
                    var interactor = _interactors[i].GetComponent<PlayerFoliageInteractor>();
                    if (interactor != null)
                    {
                        _interactorData[i] = interactor.GetShaderData();
                    }
                    else
                    {
                        // Fallback: use transform position + default radius
                        var pos = _interactors[i].position;
                        _interactorData[i] = new Vector4(pos.x, pos.y, pos.z, defaultInteractRadius);
                    }
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
}
