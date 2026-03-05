using UnityEngine;
using System;
namespace CleanRender.RenderBiomes
{
    /// <summary>
    /// Holds baked grass data for runtime GPU indirect rendering
    /// </summary>
    public class GrassDataHolder : MonoBehaviour
    {
        [HideInInspector] public Vector3[] positions;
        [HideInInspector] public Vector3[] scales;
        [HideInInspector] public Vector4[] rotations;
        [HideInInspector] public Mesh grassMesh;
        [HideInInspector] public Material grassMaterial;

        public int InstanceCount => positions != null ? positions.Length : 0;
    }
}