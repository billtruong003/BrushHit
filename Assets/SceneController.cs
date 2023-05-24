using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SceneController : MonoBehaviour
{
    [Header("Mesh + Terrain Settings")]
    [SerializeField] GameObject[] terrainObjects; // Mảng chứa nhiều Mesh Renderer
    [SerializeField] MeshRenderer[] _meshRenderers;

    [Header("Generate Rubber Settings")]
    [SerializeField] GameObject CapsuleRubber;
    [SerializeField] private float distanceForEachRubber;
    private float _LengthTerrain; // Độ dài của plane tính theo x
    private float _WidthTerrain; // Độ rộng của plane tính theo z
    private float _LengthCount; // Đếm số điểm của length dựa trên chiều dài
    public float _LineCount;
    private Vector3 _StartPos;
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
        // Gán giá trị kích thước terrain
        _LengthTerrain = sizeMeshTerrain.x;
        _WidthTerrain = sizeMeshTerrain.z;

        // Tính giá trị đếm ban đầu cho đối tượng Rubber
        _LengthCount = _LengthTerrain / 2;
        _LineCount = _WidthTerrain / 2;

        // Tính số lượng đối tượng cần tạo dựa trên khoảng cách giữa chúng
        number_needtogenerateObject = (int)(_LengthTerrain / distanceForEachRubber);
        number_linetogenerate = (int)(_WidthTerrain / distanceForEachRubber);

        // In ra các giá trị để kiểm tra
        Debug.Log(centerPos.x);
        Debug.Log(centerPos.z);
        Debug.Log(_LengthCount);
        Debug.Log(_LineCount);

        // Vòng lặp để tạo ra các đối tượng Rubber trên khu vực terrain
        for(int i = 0; i < number_linetogenerate + 1; i++) {
            // Đặt lại giá trị đếm cho hàng ngang
            _LengthCount = -(_LengthTerrain / 2);
            
            for(int j = 0; j < number_needtogenerateObject + 1; j++) {
                // Tính toán vị trí bắt đầu của đối tượng Rubber
                _StartPos = new Vector3(centerPos.x + _LengthCount, CapsuleRubber.transform.position.y, centerPos.z - _LineCount);
                
                // Tạo đối tượng Rubber và đặt trong transform cha
                Instantiate(CapsuleRubber, _StartPos, Quaternion.identity, parent);
                
                // Cập nhật biến đếm và tổng số đối tượng
                sum_object += 1;
                _LengthCount += distanceForEachRubber;
            }
            
            // Giảm giá trị đếm cho hàng dọc
            _LineCount -= distanceForEachRubber;
        }

        // In ra tổng số đối tượng đã tạo thành công
        Debug.Log("Tổng cộng có: " + sum_object);

        /* 
        Hàm này sẽ hoạt động như sau, ví dụ size.x và size.z của gameobject plan = 10;
        Chúng sẽ chia nửa và lấy giá trị âm để lấy ra vị trí góc dưới bên phải của mỗi gameobject;
        Minh họa là vị trí bên dưới;
        _____________
        |           |
        |           |
        |           |
        |*__________|
        Sau đó mỗi vòng lặp chúng sẽ tạo ra mô hình chồng chất cho đủ số lượng object đã tính toán.
        Ví dụ: 
        Vòng lặp 1:
        _____________
        |           |
        |           |
        |           |
        |***********|
        Vòng lặp 2:
        _____________
        |           |
        |           |
        |***********|
        |***********|
        Vòng lặp 3:
        _____________
        |           |
        |***********|
        |***********|
        |***********|
        Vòng lặp 4:
        _____________
        |***********|
        |***********|
        |***********|
        |***********|
        */
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
