using UnityEngine;

public class CheckCollision : MonoBehaviour
{
    [Header("Lấy các GameObject cần thiết để kiểm tra va chạm")]
    public GameObject centerPoint;

    [Header("Lấy các LayerMask để xác định va chạm")]
    public LayerMask playableLayer;
    public float raycastDistance = 1f;
    public static bool safe = true;

    private void Start()
    {
        safe = true;
    }

    private void OnDrawGizmos()
    {
        DrawRaycastGizmo();
    }

    public void CheckCollisionForFloor()
    {
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
            {
                // Xử lý thêm logic tùy vào trạng thái PauseMenu.notice_Win (nếu cần)
            }
        }
    }

    private void DrawRaycastGizmo()
    {
        Gizmos.color = Color.green;
        Gizmos.DrawRay(centerPoint.transform.position, Vector3.down * raycastDistance);
    }
}
