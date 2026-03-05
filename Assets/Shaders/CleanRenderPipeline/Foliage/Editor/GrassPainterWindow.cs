#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CleanRender.RenderBiomes
{
    /// <summary>
    /// Grass Painter: Paint grass instances onto terrain/meshes.
    /// - Bake positions before play mode → ComputeBuffer indirect draw
    /// - Supports density brush, erase, randomize height/rotation
    /// - Outputs CompressedInstanceData array for StaticInstancingManager
    /// </summary>
    public class GrassPainterWindow : EditorWindow
    {
        [System.Serializable]
        public struct GrassInstance
        {
            public Vector3 position;
            public Vector3 scale;
            public Vector4 rotation; // quaternion
            public Vector3 normal;
        }

        // Settings
        private float brushRadius = 3f;
        private float brushDensity = 5f; // instances per brush area
        private float minHeight = 0.6f;
        private float maxHeight = 1.2f;
        private float minWidth = 0.08f;
        private float maxWidth = 0.15f;
        private bool randomRotation = true;
        private bool alignToNormal = true;
        private LayerMask paintLayer = ~0;
        private Mesh grassMesh;
        private Material grassMaterial;

        // State
        private List<GrassInstance> instances = new List<GrassInstance>();
        private bool isPainting;
        private bool isErasing;
        private UnityEditor.Tool previousTool;
        private SceneView activeSceneView;

        // Serialization target
        private GameObject targetObject;

        [MenuItem("Tools/CleanRender/Grass Painter")]
        public static void ShowWindow()
        {
            GetWindow<GrassPainterWindow>("Grass Painter");
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            if (isPainting)
                StopPainting();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(5);
            GUILayout.Label("━━━ GRASS PAINTER ━━━", EditorStyles.boldLabel);

            EditorGUILayout.Space(5);
            targetObject = (GameObject)EditorGUILayout.ObjectField("Target Object", targetObject, typeof(GameObject), true);
            grassMesh = (Mesh)EditorGUILayout.ObjectField("Grass Mesh", grassMesh, typeof(Mesh), false);
            grassMaterial = (Material)EditorGUILayout.ObjectField("Grass Material", grassMaterial, typeof(Material), false);

            EditorGUILayout.Space(5);
            GUILayout.Label("Brush Settings", EditorStyles.boldLabel);
            brushRadius = EditorGUILayout.Slider("Brush Radius", brushRadius, 0.5f, 20f);
            brushDensity = EditorGUILayout.Slider("Density (per stroke)", brushDensity, 1f, 50f);
            paintLayer = EditorGUILayout.MaskField("Paint Layer", paintLayer, UnityEditorInternal.InternalEditorUtility.layers);

            EditorGUILayout.Space(5);
            GUILayout.Label("Grass Variation", EditorStyles.boldLabel);
            minHeight = EditorGUILayout.Slider("Min Height", minHeight, 0.1f, 3f);
            maxHeight = EditorGUILayout.Slider("Max Height", maxHeight, 0.1f, 3f);
            minWidth = EditorGUILayout.Slider("Min Width", minWidth, 0.01f, 1f);
            maxWidth = EditorGUILayout.Slider("Max Width", maxWidth, 0.01f, 1f);
            randomRotation = EditorGUILayout.Toggle("Random Y Rotation", randomRotation);
            alignToNormal = EditorGUILayout.Toggle("Align to Surface Normal", alignToNormal);

            EditorGUILayout.Space(10);

            // Paint/Erase buttons
            EditorGUILayout.BeginHorizontal();
            GUI.color = isPainting && !isErasing ? Color.green : Color.white;
            if (GUILayout.Button("Paint (Hold Shift)", GUILayout.Height(30)))
                StartPainting(false);

            GUI.color = isErasing ? Color.red : Color.white;
            if (GUILayout.Button("Erase (Hold Ctrl)", GUILayout.Height(30)))
                StartPainting(true);
            GUI.color = Color.white;

            if (GUILayout.Button("Stop", GUILayout.Height(30)))
                StopPainting();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);
            GUILayout.Label($"Instances: {instances.Count}", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear All", GUILayout.Height(25)))
            {
                if (EditorUtility.DisplayDialog("Clear", "Remove all grass instances?", "Yes", "No"))
                    instances.Clear();
            }

            GUI.color = new Color(0.3f, 1f, 0.5f);
            if (GUILayout.Button("BAKE TO COMPONENT", GUILayout.Height(25)))
                BakeToComponent();
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);
            EditorGUILayout.HelpBox(
                "Paint Mode:\n" +
                "• Left Click = Place grass\n" +
                "• Ctrl + Click = Erase grass\n" +
                "• Scroll = Adjust brush size\n" +
                "• Bake → Creates data for GPU Indirect Draw",
                MessageType.Info);
        }

        private void StartPainting(bool erase)
        {
            isPainting = true;
            isErasing = erase;
            previousTool = UnityEditor.Tools.current;
            UnityEditor.Tools.current = UnityEditor.Tool.None;
            SceneView.RepaintAll();
        }   

        private void StopPainting()
        {
            isPainting = false;
            isErasing = false;
            UnityEditor.Tools.current = previousTool;
            SceneView.RepaintAll();
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            if (!isPainting) return;

            Event e = Event.current;
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);

            if (Physics.Raycast(ray, out RaycastHit hit, 1000f, paintLayer))
            {
                // Draw brush
                Handles.color = isErasing ? new Color(1, 0.2f, 0.2f, 0.3f) : new Color(0.2f, 1, 0.3f, 0.3f);
                Handles.DrawSolidDisc(hit.point, hit.normal, brushRadius);
                Handles.color = isErasing ? Color.red : Color.green;
                Handles.DrawWireDisc(hit.point, hit.normal, brushRadius);

                // Scroll to resize
                if (e.type == EventType.ScrollWheel)
                {
                    brushRadius = Mathf.Clamp(brushRadius + e.delta.y * -0.3f, 0.5f, 20f);
                    e.Use();
                    Repaint();
                }

                // Paint/Erase on click
                if (e.type == EventType.MouseDown && e.button == 0 ||
                    e.type == EventType.MouseDrag && e.button == 0)
                {
                    if (e.control || isErasing)
                        EraseAt(hit.point);
                    else
                        PaintAt(hit.point, hit.normal);

                    e.Use();
                }
            }

            // Draw existing instances
            Handles.color = new Color(0.3f, 0.8f, 0.2f, 0.5f);
            int drawCount = Mathf.Min(instances.Count, 5000); // limit draw for performance
            for (int i = 0; i < drawCount; i++)
            {
                Handles.DrawLine(instances[i].position, instances[i].position + Vector3.up * instances[i].scale.y * 0.5f);
            }

            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            sceneView.Repaint();
        }

        private void PaintAt(Vector3 center, Vector3 normal)
        {
            int count = Mathf.RoundToInt(brushDensity);
            for (int i = 0; i < count; i++)
            {
                Vector2 offset = Random.insideUnitCircle * brushRadius;
                Vector3 pos = center + new Vector3(offset.x, 0, offset.y);

                // Raycast down to find exact surface
                if (Physics.Raycast(pos + Vector3.up * 10f, Vector3.down, out RaycastHit hit, 50f, paintLayer))
                {
                    float height = Random.Range(minHeight, maxHeight);
                    float width = Random.Range(minWidth, maxWidth);

                    Quaternion rot = Quaternion.identity;
                    if (alignToNormal)
                        rot = Quaternion.FromToRotation(Vector3.up, hit.normal);
                    if (randomRotation)
                        rot *= Quaternion.Euler(0, Random.Range(0f, 360f), 0);

                    instances.Add(new GrassInstance
                    {
                        position = hit.point,
                        scale = new Vector3(width, height, width),
                        rotation = new Vector4(rot.x, rot.y, rot.z, rot.w),
                        normal = hit.normal
                    });
                }
            }

            Undo.RecordObject(this, "Paint Grass");
        }

        private void EraseAt(Vector3 center)
        {
            float radiusSq = brushRadius * brushRadius;
            instances.RemoveAll(inst =>
            {
                Vector3 diff = inst.position - center;
                return diff.x * diff.x + diff.z * diff.z < radiusSq;
            });
        }

        private void BakeToComponent()
        {
            if (targetObject == null)
            {
                EditorUtility.DisplayDialog("Error", "Assign a Target Object first!", "OK");
                return;
            }

            if (instances.Count == 0)
            {
                EditorUtility.DisplayDialog("Error", "No grass instances to bake!", "OK");
                return;
            }

            // Store as JSON on a MonoBehaviour or ScriptableObject
            var holder = targetObject.GetComponent<CleanRender.RenderBiomes.GrassDataHolder>();
            if (holder == null)
                holder = Undo.AddComponent<CleanRender.RenderBiomes.GrassDataHolder>(targetObject);

            holder.positions = new Vector3[instances.Count];
            holder.scales = new Vector3[instances.Count];
            holder.rotations = new Vector4[instances.Count];

            for (int i = 0; i < instances.Count; i++)
            {
                holder.positions[i] = instances[i].position;
                holder.scales[i] = instances[i].scale;
                holder.rotations[i] = instances[i].rotation;
            }

            holder.grassMesh = grassMesh;
            holder.grassMaterial = grassMaterial;

            EditorUtility.SetDirty(holder);
            EditorUtility.DisplayDialog("Baked!",
                $"Baked {instances.Count} grass instances to {targetObject.name}.\n" +
                $"GrassDataHolder component is ready for GPU Indirect Draw at runtime.",
                "OK");
        }
    }

    
}
#endif
