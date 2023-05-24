using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerController : MonoBehaviour
{
    public GameObject Head1;
    
    public GameObject Head2;
    public GameObject Body;
    public Transform CenterPoint;
    public float rotationSpeed = 50f;
    
    private void Start() {
    }
    private void Update()
    {
        // Tính toán vị trí mới sau quay quanh điểm centerPoint
        Head1.transform.RotateAround(CenterPoint.position, Vector3.up, rotationSpeed * Time.deltaTime);
        Head2.transform.RotateAround(CenterPoint.position, Vector3.up, rotationSpeed * Time.deltaTime);
        Body.transform.RotateAround(CenterPoint.position, Vector3.up, rotationSpeed * Time.deltaTime);
        changePath();
    }
    public void changePath()
    {
        // Tính toán vị trí mới sau click chuột
        if (Input.GetMouseButtonDown(0)){
            Debug.Log("Click Roi");
            if (CenterPoint.transform == Head1.transform) {
                CenterPoint.transform.position = Head2.transform.position;
            }
            else {
                CenterPoint.transform.position = Head1.transform.position;
            }
        }
    }
}
