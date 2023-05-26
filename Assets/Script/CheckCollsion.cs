using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CheckCollsion : MonoBehaviour
{
    [Header("Lấy các GameObject cần thiết để kiểm tra va chạm")]
    public GameObject centerPoint;
    
    [Header("Lấy các LayerMask để xác định va chạm")]
    public LayerMask playableLayer;
    public float raycastDistance = 1f;
    public static bool safe = true;

    private void Start() {
        safe = true;
    }
    private void OnDrawGizmos()
    {
        // Set màu gizmos thành xanh
        Gizmos.color = Color.green;

        // Vẽ raycast từ trung tâm điểm centerPoint xuống dưới
        Gizmos.DrawRay(centerPoint.transform.position, Vector3.down * 1);
    }

    public void CheckCollisionForFloor(){
        if (Physics.Raycast(centerPoint.transform.position, Vector3.down, raycastDistance, playableLayer))
        {
            Debug.Log("Va chạm với layer an toàn");
        }
        else
        {
            Debug.Log("Layer va chạm là biển");
            if (!PauseMenu.notice_Win)
            {
                safe = false;
            }
            else
            {}
        }
    }
}

