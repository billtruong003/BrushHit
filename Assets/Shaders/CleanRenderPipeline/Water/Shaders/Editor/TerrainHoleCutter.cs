using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// TerrainHoleCutter — Editor tool that physically removes mesh triangles
/// in hole map regions so the lightmap baker's ray tracer sees actual gaps.
///
/// WHY:
///   Unity's Progressive Lightmapper (CPU mode) traces shadow rays against
///   raw mesh geometry BEFORE evaluating shader clip(). Even with
///   _ALPHATEST_ON, the baker sees solid triangles over hole regions
///   and blocks light from reaching caves below.
///
///   This tool solves it by modifying the mesh itself:
///   - Triangles fully inside holes → removed
///   - Triangles partially inside holes → split at hole boundary
///   - Triangles fully outside holes → kept as-is
///
/// USAGE:
///   1. Select terrain GameObject with MeshFilter + MeshRenderer
///   2. Menu: Tools → CleanRender → Cut Terrain Holes
///   3. Tool reads _HoleMap and _HoleThreshold from the material
///   4. Creates a new mesh asset with holes cut out
///   5. Original mesh is preserved (undo supported)
///
/// NOTES:
///   - Works on any mesh, not just Unity Terrain
///   - Hole map is sampled at vertex UVs (TEXCOORD0)
///   - Edge subdivision uses binary search for clean boundary
///   - Output mesh is saved as asset for lightmap UV stability
/// </summary>
public class TerrainHoleCutter : EditorWindow
{
    // ── Settings ──
    GameObject targetObject;
    int edgeSubdivisions = 3;   // binary search iterations for edge cutting
    bool createBackup = true;
    float thresholdOverride = -1f; // -1 = read from material

    // ── State ──
    Vector2 scrollPos;
    string lastLog = "";

    [MenuItem("Tools/CleanRender/Cut Terrain Holes")]
    static void Open()
    {
        var win = GetWindow<TerrainHoleCutter>("Terrain Hole Cutter");
        win.minSize = new Vector2(380, 320);
    }

    void OnEnable()
    {
        // Auto-select if terrain is selected
        if (Selection.activeGameObject != null)
        {
            var mf = Selection.activeGameObject.GetComponent<MeshFilter>();
            if (mf != null)
                targetObject = Selection.activeGameObject;
        }
    }

    void OnGUI()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Terrain Hole Cutter", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Physically removes mesh triangles in hole map regions.\n" +
            "This makes the lightmap baker see actual gaps so light\n" +
            "can reach caves and tunnels below.",
            MessageType.Info);

        EditorGUILayout.Space(8);

        // ── Target ──
        targetObject = (GameObject)EditorGUILayout.ObjectField(
            "Target Terrain", targetObject, typeof(GameObject), true);

        if (targetObject != null)
        {
            var mf = targetObject.GetComponent<MeshFilter>();
            var mr = targetObject.GetComponent<MeshRenderer>();

            if (mf == null || mr == null)
            {
                EditorGUILayout.HelpBox("Target needs MeshFilter + MeshRenderer.", MessageType.Error);
                return;
            }

            if (mf.sharedMesh == null)
            {
                EditorGUILayout.HelpBox("MeshFilter has no mesh assigned.", MessageType.Error);
                return;
            }

            // Show mesh info
            var mesh = mf.sharedMesh;
            EditorGUILayout.LabelField($"Mesh: {mesh.name}  ({mesh.triangles.Length / 3} tris, {mesh.vertexCount} verts)",
                EditorStyles.miniLabel);

            // ── Find hole map from material ──
            Texture2D holeMap = null;
            float threshold = 0.5f;
            Vector4 holeMapST = new Vector4(1, 1, 0, 0);

            if (mr.sharedMaterial != null)
            {
                var mat = mr.sharedMaterial;
                if (mat.HasProperty("_HoleMap"))
                    holeMap = mat.GetTexture("_HoleMap") as Texture2D;
                if (mat.HasProperty("_HoleThreshold"))
                    threshold = mat.GetFloat("_HoleThreshold");
                if (mat.HasProperty("_HoleMap_ST"))
                    holeMapST = mat.GetVector("_HoleMap_ST");

                if (holeMap != null)
                {
                    EditorGUILayout.LabelField($"Hole Map: {holeMap.name} ({holeMap.width}×{holeMap.height})",
                        EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"Threshold: {threshold:F3}  ST: ({holeMapST.x:F2}, {holeMapST.y:F2}, {holeMapST.z:F2}, {holeMapST.w:F2})",
                        EditorStyles.miniLabel);
                }
                else
                {
                    EditorGUILayout.HelpBox("No _HoleMap found on material. Assign a hole map texture first.", MessageType.Warning);
                }
            }

            EditorGUILayout.Space(6);

            // ── Settings ──
            edgeSubdivisions = EditorGUILayout.IntSlider("Edge Subdivisions", edgeSubdivisions, 0, 6);
            EditorGUILayout.HelpBox(
                "0 = remove whole triangles only (fast, jagged edges)\n" +
                "3 = good balance (default)\n" +
                "6 = very smooth hole boundary (more triangles)",
                MessageType.None);

            createBackup = EditorGUILayout.Toggle("Create Backup", createBackup);

            if (thresholdOverride >= 0)
                threshold = thresholdOverride;
            EditorGUI.BeginChangeCheck();
            float overrideVal = EditorGUILayout.FloatField("Threshold Override (-1 = auto)",
                thresholdOverride);
            if (EditorGUI.EndChangeCheck())
                thresholdOverride = overrideVal;

            EditorGUILayout.Space(8);

            // ── Execute ──
            GUI.enabled = holeMap != null;

            if (GUILayout.Button("Cut Holes", GUILayout.Height(32)))
            {
                CutHoles(targetObject, holeMap, threshold, holeMapST);
            }

            GUI.enabled = true;
        }

        // ── Log ──
        if (!string.IsNullOrEmpty(lastLog))
        {
            EditorGUILayout.Space(6);
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.Height(80));
            EditorGUILayout.HelpBox(lastLog, MessageType.Info);
            EditorGUILayout.EndScrollView();
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Main Cut Logic
    // ════════════════════════════════════════════════════════════════

    void CutHoles(GameObject go, Texture2D holeMap, float threshold, Vector4 st)
    {
        var mf = go.GetComponent<MeshFilter>();
        Mesh srcMesh = mf.sharedMesh;

        // Make hole map readable
        Texture2D readableHoleMap = MakeReadable(holeMap);
        if (readableHoleMap == null)
        {
            lastLog = "ERROR: Could not make hole map readable. Check texture import settings.";
            return;
        }

        Undo.RecordObject(mf, "Cut Terrain Holes");

        // Backup
        if (createBackup)
        {
            string backupPath = $"Assets/{srcMesh.name}_backup.asset";
            backupPath = AssetDatabase.GenerateUniqueAssetPath(backupPath);
            Mesh backup = Instantiate(srcMesh);
            AssetDatabase.CreateAsset(backup, backupPath);
            lastLog = $"Backup saved: {backupPath}\n";
        }
        else
        {
            lastLog = "";
        }

        // Read source mesh data
        Vector3[] srcVerts = srcMesh.vertices;
        Vector3[] srcNormals = srcMesh.normals;
        Vector4[] srcTangents = srcMesh.tangents;
        Vector2[] srcUV0 = srcMesh.uv;
        Vector2[] srcUV1 = srcMesh.uv2;
        Color[] srcColors = srcMesh.colors;
        int[] srcTris = srcMesh.triangles;

        bool hasNormals = srcNormals != null && srcNormals.Length == srcVerts.Length;
        bool hasTangents = srcTangents != null && srcTangents.Length == srcVerts.Length;
        bool hasUV1 = srcUV1 != null && srcUV1.Length == srcVerts.Length;
        bool hasColors = srcColors != null && srcColors.Length == srcVerts.Length;

        // Pre-compute hole alpha per vertex
        float[] vertexHole = new float[srcVerts.Length];
        for (int i = 0; i < srcVerts.Length; i++)
        {
            vertexHole[i] = SampleHoleMap(readableHoleMap, srcUV0[i], st);
        }

        // Build output
        var newVerts = new List<Vector3>(srcVerts.Length);
        var newNormals = new List<Vector3>(srcVerts.Length);
        var newTangents = new List<Vector4>(srcVerts.Length);
        var newUV0 = new List<Vector2>(srcVerts.Length);
        var newUV1 = new List<Vector2>(srcVerts.Length);
        var newColors = new List<Color>(srcVerts.Length);
        var newTris = new List<int>(srcTris.Length);

        // Vertex cache: reuse existing vertices by original index
        int[] vertexRemap = new int[srcVerts.Length];
        for (int i = 0; i < vertexRemap.Length; i++) vertexRemap[i] = -1;

        // Stats
        int removedTris = 0;
        int keptTris = 0;
        int splitTris = 0;

        int triCount = srcTris.Length / 3;

        for (int t = 0; t < triCount; t++)
        {
            int i0 = srcTris[t * 3];
            int i1 = srcTris[t * 3 + 1];
            int i2 = srcTris[t * 3 + 2];

            float h0 = vertexHole[i0];
            float h1 = vertexHole[i1];
            float h2 = vertexHole[i2];

            bool solid0 = h0 >= threshold;
            bool solid1 = h1 >= threshold;
            bool solid2 = h2 >= threshold;

            int solidCount = (solid0 ? 1 : 0) + (solid1 ? 1 : 0) + (solid2 ? 1 : 0);

            if (solidCount == 0)
            {
                // All vertices in hole → remove triangle
                removedTris++;
                continue;
            }

            if (solidCount == 3)
            {
                // All vertices solid → keep triangle as-is
                int ni0 = RemapVertex(i0, srcVerts, srcNormals, srcTangents, srcUV0, srcUV1, srcColors,
                    hasNormals, hasTangents, hasUV1, hasColors,
                    newVerts, newNormals, newTangents, newUV0, newUV1, newColors, vertexRemap);
                int ni1 = RemapVertex(i1, srcVerts, srcNormals, srcTangents, srcUV0, srcUV1, srcColors,
                    hasNormals, hasTangents, hasUV1, hasColors,
                    newVerts, newNormals, newTangents, newUV0, newUV1, newColors, vertexRemap);
                int ni2 = RemapVertex(i2, srcVerts, srcNormals, srcTangents, srcUV0, srcUV1, srcColors,
                    hasNormals, hasTangents, hasUV1, hasColors,
                    newVerts, newNormals, newTangents, newUV0, newUV1, newColors, vertexRemap);

                newTris.Add(ni0);
                newTris.Add(ni1);
                newTris.Add(ni2);
                keptTris++;
                continue;
            }

            // ── Partial: need to split triangle at hole boundary ──
            if (edgeSubdivisions <= 0)
            {
                // No subdivision → just remove partial triangles
                removedTris++;
                continue;
            }

            splitTris++;

            // Reorder so solid vertices come first
            // Case: 2 solid, 1 hole → keep quad (2 tris)
            // Case: 1 solid, 2 hole → keep 1 smaller tri
            int[] idx = { i0, i1, i2 };
            bool[] solid = { solid0, solid1, solid2 };
            float[] hvals = { h0, h1, h2 };

            if (solidCount == 2)
            {
                // Find the ONE hole vertex, rotate it to position [2]
                int holeIdx = solid[0] ? (solid[1] ? 2 : 1) : 0;
                RotateToLast(ref idx, ref solid, ref hvals, holeIdx);

                // idx[0], idx[1] are solid; idx[2] is in hole
                // Find boundary points on edges [0→2] and [1→2]
                int edgeA = AddEdgeVertex(idx[0], idx[2], threshold, readableHoleMap, st,
                    srcVerts, srcNormals, srcTangents, srcUV0, srcUV1, srcColors,
                    hasNormals, hasTangents, hasUV1, hasColors,
                    newVerts, newNormals, newTangents, newUV0, newUV1, newColors);

                int edgeB = AddEdgeVertex(idx[1], idx[2], threshold, readableHoleMap, st,
                    srcVerts, srcNormals, srcTangents, srcUV0, srcUV1, srcColors,
                    hasNormals, hasTangents, hasUV1, hasColors,
                    newVerts, newNormals, newTangents, newUV0, newUV1, newColors);

                int n0 = RemapVertex(idx[0], srcVerts, srcNormals, srcTangents, srcUV0, srcUV1, srcColors,
                    hasNormals, hasTangents, hasUV1, hasColors,
                    newVerts, newNormals, newTangents, newUV0, newUV1, newColors, vertexRemap);
                int n1 = RemapVertex(idx[1], srcVerts, srcNormals, srcTangents, srcUV0, srcUV1, srcColors,
                    hasNormals, hasTangents, hasUV1, hasColors,
                    newVerts, newNormals, newTangents, newUV0, newUV1, newColors, vertexRemap);

                // Quad: 2 triangles
                // Tri 1: n0, n1, edgeA
                newTris.Add(n0); newTris.Add(n1); newTris.Add(edgeA);
                // Tri 2: n1, edgeB, edgeA
                newTris.Add(n1); newTris.Add(edgeB); newTris.Add(edgeA);
            }
            else // solidCount == 1
            {
                // Find the ONE solid vertex, rotate it to position [0]
                int solidIdx = solid[0] ? 0 : (solid[1] ? 1 : 2);
                RotateToFirst(ref idx, ref solid, ref hvals, solidIdx);

                // idx[0] is solid; idx[1], idx[2] are in hole
                int edgeA = AddEdgeVertex(idx[0], idx[1], threshold, readableHoleMap, st,
                    srcVerts, srcNormals, srcTangents, srcUV0, srcUV1, srcColors,
                    hasNormals, hasTangents, hasUV1, hasColors,
                    newVerts, newNormals, newTangents, newUV0, newUV1, newColors);

                int edgeB = AddEdgeVertex(idx[0], idx[2], threshold, readableHoleMap, st,
                    srcVerts, srcNormals, srcTangents, srcUV0, srcUV1, srcColors,
                    hasNormals, hasTangents, hasUV1, hasColors,
                    newVerts, newNormals, newTangents, newUV0, newUV1, newColors);

                int n0 = RemapVertex(idx[0], srcVerts, srcNormals, srcTangents, srcUV0, srcUV1, srcColors,
                    hasNormals, hasTangents, hasUV1, hasColors,
                    newVerts, newNormals, newTangents, newUV0, newUV1, newColors, vertexRemap);

                // Single triangle
                newTris.Add(n0); newTris.Add(edgeA); newTris.Add(edgeB);
            }
        }

        // ── Build new mesh ──
        Mesh newMesh = new Mesh();
        newMesh.name = srcMesh.name + "_HoleCut";
        newMesh.indexFormat = newVerts.Count > 65535
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;

        newMesh.SetVertices(newVerts);
        if (hasNormals) newMesh.SetNormals(newNormals);
        if (hasTangents) newMesh.SetTangents(newTangents);
        newMesh.SetUVs(0, newUV0);
        if (hasUV1) newMesh.SetUVs(1, newUV1);
        if (hasColors) newMesh.SetColors(newColors);
        newMesh.SetTriangles(newTris, 0);

        newMesh.RecalculateBounds();
        if (!hasNormals) newMesh.RecalculateNormals();

        // Save as asset for lightmap UV stability
        string assetPath = $"Assets/{newMesh.name}.asset";
        assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
        AssetDatabase.CreateAsset(newMesh, assetPath);
        AssetDatabase.SaveAssets();

        // Assign
        mf.sharedMesh = newMesh;

        // Force lightmap UV regeneration
        GameObjectUtility.SetStaticEditorFlags(go,
            GameObjectUtility.GetStaticEditorFlags(go) | StaticEditorFlags.ContributeGI);

        lastLog += $"Done!\n" +
                   $"  Kept: {keptTris} tris\n" +
                   $"  Split: {splitTris} tris (edge subdivision)\n" +
                   $"  Removed: {removedTris} tris\n" +
                   $"  Result: {newTris.Count / 3} tris, {newVerts.Count} verts\n" +
                   $"  Saved: {assetPath}";

        Debug.Log($"[TerrainHoleCutter] {lastLog}");
        Repaint();

        // Cleanup temp texture
        if (readableHoleMap != holeMap)
            DestroyImmediate(readableHoleMap);
    }

    // ════════════════════════════════════════════════════════════════
    // Hole Map Sampling
    // ════════════════════════════════════════════════════════════════

    static float SampleHoleMap(Texture2D tex, Vector2 uv, Vector4 st)
    {
        // Apply tiling/offset: transformedUV = uv * ST.xy + ST.zw
        float u = uv.x * st.x + st.z;
        float v = uv.y * st.y + st.w;

        // Wrap
        u = u - Mathf.Floor(u);
        v = v - Mathf.Floor(v);

        int x = Mathf.Clamp((int)(u * tex.width), 0, tex.width - 1);
        int y = Mathf.Clamp((int)(v * tex.height), 0, tex.height - 1);

        return tex.GetPixel(x, y).r;
    }

    // ════════════════════════════════════════════════════════════════
    // Edge Splitting — Binary search for hole boundary on an edge
    // ════════════════════════════════════════════════════════════════

    int AddEdgeVertex(int solidIdx, int holeIdx, float threshold,
        Texture2D holeMap, Vector4 st,
        Vector3[] srcVerts, Vector3[] srcNormals, Vector4[] srcTangents,
        Vector2[] srcUV0, Vector2[] srcUV1, Color[] srcColors,
        bool hasNormals, bool hasTangents, bool hasUV1, bool hasColors,
        List<Vector3> outVerts, List<Vector3> outNormals,
        List<Vector4> outTangents, List<Vector2> outUV0,
        List<Vector2> outUV1, List<Color> outColors)
    {
        // Binary search for threshold crossing along edge
        float lo = 0f; // solid end
        float hi = 1f; // hole end

        for (int i = 0; i < edgeSubdivisions; i++)
        {
            float mid = (lo + hi) * 0.5f;
            Vector2 midUV = Vector2.Lerp(srcUV0[solidIdx], srcUV0[holeIdx], mid);
            float sample = SampleHoleMap(holeMap, midUV, st);

            if (sample >= threshold)
                lo = mid; // still solid, push further
            else
                hi = mid; // entered hole, pull back
        }

        float t = (lo + hi) * 0.5f;

        // Interpolate all vertex attributes
        int newIdx = outVerts.Count;

        outVerts.Add(Vector3.Lerp(srcVerts[solidIdx], srcVerts[holeIdx], t));
        outUV0.Add(Vector2.Lerp(srcUV0[solidIdx], srcUV0[holeIdx], t));

        if (hasNormals)
            outNormals.Add(Vector3.Lerp(srcNormals[solidIdx], srcNormals[holeIdx], t).normalized);
        if (hasTangents)
            outTangents.Add(Vector4.Lerp(srcTangents[solidIdx], srcTangents[holeIdx], t));
        if (hasUV1)
            outUV1.Add(Vector2.Lerp(srcUV1[solidIdx], srcUV1[holeIdx], t));
        if (hasColors)
            outColors.Add(Color.Lerp(srcColors[solidIdx], srcColors[holeIdx], t));

        return newIdx;
    }

    // ════════════════════════════════════════════════════════════════
    // Vertex Remapping — reuse original vertices
    // ════════════════════════════════════════════════════════════════

    static int RemapVertex(int srcIdx,
        Vector3[] srcVerts, Vector3[] srcNormals, Vector4[] srcTangents,
        Vector2[] srcUV0, Vector2[] srcUV1, Color[] srcColors,
        bool hasNormals, bool hasTangents, bool hasUV1, bool hasColors,
        List<Vector3> outVerts, List<Vector3> outNormals,
        List<Vector4> outTangents, List<Vector2> outUV0,
        List<Vector2> outUV1, List<Color> outColors,
        int[] remap)
    {
        if (remap[srcIdx] >= 0) return remap[srcIdx];

        int newIdx = outVerts.Count;
        remap[srcIdx] = newIdx;

        outVerts.Add(srcVerts[srcIdx]);
        outUV0.Add(srcUV0[srcIdx]);

        if (hasNormals) outNormals.Add(srcNormals[srcIdx]);
        if (hasTangents) outTangents.Add(srcTangents[srcIdx]);
        if (hasUV1) outUV1.Add(srcUV1[srcIdx]);
        if (hasColors) outColors.Add(srcColors[srcIdx]);

        return newIdx;
    }

    // ════════════════════════════════════════════════════════════════
    // Array Rotation Helpers (for triangle vertex reordering)
    // ════════════════════════════════════════════════════════════════

    static void RotateToLast(ref int[] idx, ref bool[] solid, ref float[] hvals, int targetPos)
    {
        // Rotate array so targetPos ends up at index [2]
        while (targetPos != 2)
        {
            int ti = idx[0]; idx[0] = idx[1]; idx[1] = idx[2]; idx[2] = ti;
            bool tb = solid[0]; solid[0] = solid[1]; solid[1] = solid[2]; solid[2] = tb;
            float tf = hvals[0]; hvals[0] = hvals[1]; hvals[1] = hvals[2]; hvals[2] = tf;
            targetPos = (targetPos + 2) % 3;
        }
    }

    static void RotateToFirst(ref int[] idx, ref bool[] solid, ref float[] hvals, int targetPos)
    {
        // Rotate array so targetPos ends up at index [0]
        while (targetPos != 0)
        {
            int ti = idx[2]; idx[2] = idx[1]; idx[1] = idx[0]; idx[0] = ti;
            bool tb = solid[2]; solid[2] = solid[1]; solid[1] = solid[0]; solid[0] = tb;
            float tf = hvals[2]; hvals[2] = hvals[1]; hvals[1] = hvals[0]; hvals[0] = tf;
            targetPos = (targetPos + 1) % 3;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Make texture readable (handle compressed/non-readable textures)
    // ════════════════════════════════════════════════════════════════

    static Texture2D MakeReadable(Texture2D src)
    {
        if (src.isReadable)
            return src;

        // Try to set readable via importer
        string path = AssetDatabase.GetAssetPath(src);
        if (!string.IsNullOrEmpty(path))
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                bool wasReadable = importer.isReadable;
                if (!wasReadable)
                {
                    importer.isReadable = true;
                    importer.SaveAndReimport();

                    // Re-fetch texture after reimport
                    src = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (src != null && src.isReadable)
                        return src;
                }
            }
        }

        // Fallback: GPU readback
        RenderTexture tmp = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(src, tmp);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = tmp;

        Texture2D readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
        readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
        readable.Apply();

        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(tmp);

        return readable;
    }
}