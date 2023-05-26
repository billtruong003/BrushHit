using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerController : MonoBehaviour
{
    [Header("Xác định các part của player")]
    [SerializeField] private GameObject head1;
    [SerializeField] private GameObject head2;
    [SerializeField] private GameObject body;

    [Header("Vị trí trung tâm và tốc độ xoay của Object")]
    public Transform CenterPoint;
    public bool Direction;
    public float rotationSpeed = 200f;

    [Header("Script để kiểm tra va chạm")]
    public CheckCollsion CheckCollision;
    Transform[] elements;
    
    private void Start() {
        CenterPoint.position = new Vector3(head1.transform.position.x, head1.transform.position.y + 0.5f, head1.transform.position.z);
        CheckCollision = GetComponent<CheckCollsion>();
        elements = new Transform[]{ head1.transform, head2.transform, body.transform };
        Direction = true;
    }
    private void Update()
    {
        // Tính toán vị trí mới sau quay quanh điểm centerPoint
        /*Head1.transform.RotateAround(CenterPoint.position, Vector3.up, rotationSpeed * Time.deltaTime);
        Head2.transform.RotateAround(CenterPoint.position, Vector3.up, rotationSpeed * Time.deltaTime);
        Body.transform.RotateAround(CenterPoint.position, Vector3.up, rotationSpeed * Time.deltaTime);*/
        if(Direction)
        {
            foreach (Transform element in elements)
            {
                element.RotateAround(CenterPoint.position, Vector3.up, rotationSpeed * Time.deltaTime);
            }
        }
        else
        {
            foreach (Transform element in elements)
            {
                element.RotateAround(CenterPoint.position, Vector3.down, rotationSpeed * Time.deltaTime);
            }
        }
        changePath();
        
    }
    private void FixedUpdate() {
        
    }
    public void changePath()
    {
        // Tính toán vị trí mới sau click chuột
        if (Input.GetMouseButtonDown(0)){
            Debug.Log("Click Roi");
            if (CenterPoint.position.x == head1.transform.position.x && CenterPoint.position.z == head1.transform.position.z) {
                CenterPoint.transform.position = new Vector3(head2.transform.position.x, head2.transform.position.y + 0.5f, head2.transform.position.z);
                CheckCollision.CheckCollisionForFloor();
                Direction = false;
            }
            else {
                CenterPoint.position = new Vector3(head1.transform.position.x, head1.transform.position.y + 0.5f, head1.transform.position.z);
                CheckCollision.CheckCollisionForFloor();
                Direction = true;
            }
        }
    }
}
