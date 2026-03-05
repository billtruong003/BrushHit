using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerController : MonoBehaviour
{
    [Header("Xác định các part của player")]
    [SerializeField] private GameObject head1;
    [SerializeField] private GameObject head2;
    [SerializeField] private GameObject body;

    [Header("Vị trí trung tâm và tốc độ xoay")]
    public Transform CenterPoint;
    public bool Direction;
    public float rotationSpeed = 200f;

    [Header("Script kiểm tra va chạm")]
    public CheckCollision CheckCollision;
    Transform[] elements;
    
    private void Start()
    {
        CenterPoint.position = new Vector3(
            head1.transform.position.x,
            head1.transform.position.y + 0.5f,
            head1.transform.position.z);

        CheckCollision = GetComponent<CheckCollision>();
        elements = new Transform[] { head1.transform, head2.transform, body.transform };
        Direction = true;

        // ★ Đăng ký player parts với RubberManager để collision + shader interaction hoạt động
        if (RubberManager.Instance != null)
        {
            RubberManager.Instance.RegisterPlayerParts(
                head1.transform,
                head2.transform,
                body.transform
            );
        }
    }

    private void Update()
    {
        if (Direction)
        {
            foreach (Transform element in elements)
                element.RotateAround(CenterPoint.position, Vector3.up, rotationSpeed * Time.deltaTime);
        }
        else
        {
            foreach (Transform element in elements)
                element.RotateAround(CenterPoint.position, Vector3.down, rotationSpeed * Time.deltaTime);
        }

        changePath();
    }

    public void changePath()
    {
        if (Input.GetMouseButtonDown(0))
        {
            if (CenterPoint.position.x == head1.transform.position.x
                && CenterPoint.position.z == head1.transform.position.z)
            {
                CenterPoint.transform.position = new Vector3(
                    head2.transform.position.x,
                    head2.transform.position.y + 0.5f,
                    head2.transform.position.z);
                CheckCollision.CheckCollisionForFloor();
                Direction = false;
            }
            else
            {
                CenterPoint.position = new Vector3(
                    head1.transform.position.x,
                    head1.transform.position.y + 0.5f,
                    head1.transform.position.z);
                CheckCollision.CheckCollisionForFloor();
                Direction = true;
            }
        }
    }
}
