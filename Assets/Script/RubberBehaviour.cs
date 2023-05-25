using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RubberBehaviour : MonoBehaviour
{
    [SerializeField] Material mat1;
    [SerializeField] Material mat2;
    [SerializeField] bool hasChangedMaterial;
    
    MeshRenderer MaterialMesh;
    // Số lượng pixel để thụt vị trí y khi va chạm xảy ra
    public float yOffset = 0.5f;

    // Tốc độ của hiệu ứng trở lại vị trí ban đầu
    public float returnSpeed = 1f;

    public bool isColliding = false;   // Biến boolean để kiểm tra đã va chạm hay chưa

    public Vector3 initialPosition;    // Vị trí ban đầu của đối tượng
    public static bool win;

    private void Start()
    {
        win = false;
        GameSpawn.numberObTrue = 0;
        MaterialMesh = GetComponent<MeshRenderer>();
        MaterialMesh.material = mat1;
        // Lưu trữ vị trí ban đầu của đối tượng
        initialPosition = transform.position;
    }

    private void OnTriggerEnter(Collider other)
    {
        // Kiểm tra nếu gameobject va chạm với player (ta giả định rằng player có tag "Player")
        if (other.gameObject.CompareTag("Player"))
        {
            // Thụt vị trí y của gameobject xuống theo giá trị yOffset
            transform.Translate(new Vector3(0, -yOffset, 0));
            isColliding = true;
            if (GameSpawn.numberObTrue >= GameSpawn.sum_object) {
                Debug.Log("Winnnn");
                win = true;
                PauseMenu.WinGame();
            }
        }
        if (other.gameObject.CompareTag("Player") && !hasChangedMaterial)
        {
            GetComponent<MeshRenderer>().material = mat2;
            hasChangedMaterial = true;
            GameSpawn.numberObTrue += 1;
            GameSpawn.Score += 1;
            Debug.Log("Point: " + GameSpawn.numberObTrue);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        // Khi player rời khỏi vật thể
        if (other.gameObject.CompareTag("Player"))
        {
            isColliding = false;
        }
    }

    private void Update()
    {
        // Kiểm tra xem đối tượng đang va chạm và đã đi quá xa so với vị trí ban đầu hay chưa
        if (!isColliding && transform.position.y < initialPosition.y)
        {
            // Trở lại vị trí ban đầu của đối tượng dần dần với tốc độ returnSpeed
            transform.Translate(new Vector3(0, returnSpeed * Time.deltaTime, 0));
        }
        else {

        }
    }
}

