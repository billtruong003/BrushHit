using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Rolling : MonoBehaviour
{
    public GameObject Head1;
    public GameObject Head2;
    public GameObject Body;
    public Transform Center;
    public float rotationSpeed;
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        Head1.transform.RotateAround(Center.position, Vector3.forward, rotationSpeed * Time.deltaTime);
        Head2.transform.RotateAround(Center.position, Vector3.forward, rotationSpeed * Time.deltaTime);
        Body.transform.RotateAround(Center.position, Vector3.forward, rotationSpeed * Time.deltaTime);
    }
}
