using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerController : MonoBehaviour
{
    [Header("Xác định các part của player")]
    public GameObject Head1;
    
    public GameObject Head2;
    public GameObject Body;

    [Header("Vị trí trung tâm và tốc độ xoay của Object")]
    public Transform CenterPoint;
    public float rotationSpeed = 200f;

    [Header("Script để kiểm tra va chạm")]
    public CheckCollsion CheckCollision;
    
    private void Start() {
        CenterPoint.position = new Vector3(Head1.transform.position.x, Head1.transform.position.y + 0.5f, Head1.transform.position.z);
        CheckCollision = GetComponent<CheckCollsion>();
    }
    private void Update()
    {
        // Tính toán vị trí mới sau quay quanh điểm centerPoint
        Head1.transform.RotateAround(CenterPoint.position, Vector3.up, rotationSpeed * Time.deltaTime);
        Head2.transform.RotateAround(CenterPoint.position, Vector3.up, rotationSpeed * Time.deltaTime);
        Body.transform.RotateAround(CenterPoint.position, Vector3.up, rotationSpeed * Time.deltaTime);
        changePath();
    }
    private void FixedUpdate() {
        
    }
    public void changePath()
    {
        // Tính toán vị trí mới sau click chuột
        if (Input.GetMouseButtonDown(0)){
            Debug.Log("Click Roi");
            if (CenterPoint.position.x == Head1.transform.position.x && CenterPoint.position.z == Head1.transform.position.z) {
                CenterPoint.transform.position = new Vector3(Head2.transform.position.x, Head2.transform.position.y + 0.5f, Head2.transform.position.z);
                CheckCollision.CheckCollisionForFloor();
            }
            else {
                CenterPoint.position = new Vector3(Head1.transform.position.x, Head1.transform.position.y + 0.5f, Head1.transform.position.z);
                CheckCollision.CheckCollisionForFloor();
            }
        }
    }
}
