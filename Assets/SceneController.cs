using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SceneController : MonoBehaviour
{
    [SerializeField] GameObject[] terrainObjects; // Mảng chứa nhiều Mesh Renderer
    [SerializeField] MeshRenderer[] _meshRenderers;

    [SerializeField] GameObject CapsuleRubber;
    
    public float _LengthTerrain;
    public float _WidthTerrain;
    public float _LengthCount;
    public float _LineCount;
    public Vector3 _StartPos;
    public float distanceForEachRubber;
    public int number_needtogenerateObject;
    public int number_linetogenerate;
    public static int sum_object;
    public static int numberObTrue;
    // Start is called before the first frame update
    void Start()
    {
        
        AssignComponentToMeshRenderer();
        AreaTerrain();
    }

    // Update is called once per frame
    void GenerateRubber(Vector3 sizeMeshTerrain, Transform parent, Vector3 centerPos) 
    {
        _LengthTerrain = sizeMeshTerrain.x;
        _WidthTerrain = sizeMeshTerrain.z;
        _LengthCount = _LengthTerrain / 2;
        _LineCount = _WidthTerrain / 2;
        number_needtogenerateObject = (int)(_LengthTerrain / distanceForEachRubber);
        number_linetogenerate = (int) (_WidthTerrain / distanceForEachRubber);
        Debug.Log(centerPos.x);
        Debug.Log(centerPos.z);
        Debug.Log(_LengthCount);
        Debug.Log(_LineCount);
        for(int i = 0; i < number_linetogenerate + 1; i++) {
            _LengthCount = -(_LengthTerrain / 2);
            for(int j = 0; j < number_needtogenerateObject + 1; j++) {
                _StartPos = new Vector3(centerPos.x + _LengthCount, CapsuleRubber.transform.position.y, centerPos.z - _LineCount);
                Instantiate(CapsuleRubber, _StartPos, Quaternion.identity, parent);
                sum_object += 1;
                _LengthCount += distanceForEachRubber;
            }
            _LineCount -= distanceForEachRubber;
        }
        Debug.Log("Tổng cộng có: " + sum_object);
    }

    // Tính diện tích của Terrain đó
    void AreaTerrain()
    {
        // Kiểm tra xem có Mesh Renderer hay không
        if (_meshRenderers != null && _meshRenderers.Length > 0)
        {
            // Lặp qua từng Mesh Renderer trong mảng
            for (int i = 0; i < _meshRenderers.Length; i++)
            {
                MeshRenderer renderer = _meshRenderers[i];

                // Lấy kích thước của Mesh Renderer (bao gồm cả kích thước chiều dài và chiều rộng)
                Vector3 size = renderer.bounds.size;

                // Tính diện tích bằng cách nhân kích thước chiều dài và chiều rộng
                float area = size.x * size.z;

                // In ra từng cạnh
                Debug.Log("Chiều dài của Mesh Renderer " + i + ": " + size.x);
                Debug.Log("Chiều rộng của Mesh Renderer " + i + ": " + size.z);

                // In ra kết quả
                Debug.Log("Diện tích của Mesh Renderer " + i + " là: " + area);
                GenerateRubber(size, terrainObjects[i].transform, terrainObjects[i].transform.position);
            }
        }
        else
        {
            Debug.LogWarning("Không tìm thấy Mesh Renderer trên GameObject hoặc mảng meshRenderers rỗng!");
        }
    }
    void AssignComponentToMeshRenderer() 
    {
        _meshRenderers = new MeshRenderer[terrainObjects.Length];
        for(int i = 0; i < _meshRenderers.Length; i++) {
            _meshRenderers[i] = terrainObjects[i].GetComponent<MeshRenderer>();
            }
    }

    
}
