using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class GameSpawn : MonoBehaviour
{
    [Header("Mesh + Terrain Settings")]
    [SerializeField] GameObject[] terrainObjects;
    [SerializeField] MeshRenderer[] _meshRenderers;

    [Header("Generate Rubber Settings")]
    [SerializeField] private float rubberYPosition = 0.25f;
    [SerializeField] private float distanceForEachRubber;

    private float _LengthTerrain;
    private float _WidthTerrain;
    private float _LengthCount;
    public float _LineCount;
    public int number_needtogenerateObject;
    public int number_linetogenerate;
    public static int sum_object;
    public static int numberObTrue;

    [Header("SpawnPlayer")]
    [SerializeField] GameObject SpawnPlayer;
    [SerializeField] Transform PlanSpawner;

    [Header("Camera")]
    [SerializeField] Camera maincam;
    public Transform centerPoint;
    [SerializeField] float smoothSpeed = 0.5f;
    [SerializeField] float heightOffset = 10f;
    [SerializeField] float distanceOffset = 5f;

    [Header("Score")]
    public static int score;
    [SerializeField] TextMeshProUGUI ScoreDisplay;

    private List<Vector3> allRubberPositions = new List<Vector3>();

    void Start()
    {
        score = 0;
        sum_object = 0;
        numberObTrue = 0;

        SpawnPlayerToNothingPlane();
        AssignComponentToMeshRenderer();
        AreaTerrain();

        if (RubberManager.Instance != null)
            RubberManager.Instance.RegisterRubberPositions(allRubberPositions);
        else
            Debug.LogError("[GameSpawn] RubberManager not found!");
    }

    private void Update()
    {
        ScoreDisplay.text = "Score " + score;
    }

    void SpawnPlayerToNothingPlane()
    {
        var playerSpawned = Instantiate(SpawnPlayer, PlanSpawner.position, Quaternion.identity, transform);
        centerPoint = playerSpawned.transform.Find("CenterPoint");
    }

    private void LateUpdate()
    {
        if (centerPoint == null) return;

        Vector3 desiredPosition = centerPoint.position + Vector3.up * heightOffset;
        desiredPosition.z -= distanceOffset;
        maincam.transform.position = Vector3.Lerp(
            maincam.transform.position, desiredPosition, smoothSpeed * Time.deltaTime);

        Quaternion desiredRotation = Quaternion.LookRotation(
            centerPoint.position - maincam.transform.position, Vector3.up);
        maincam.transform.rotation = Quaternion.Slerp(
            maincam.transform.rotation, desiredRotation, smoothSpeed * Time.deltaTime);
    }

    void GenerateRubber(Vector3 sizeMeshTerrain, Transform parent, Vector3 centerPos)
    {
        _LengthTerrain = sizeMeshTerrain.x;
        _WidthTerrain = sizeMeshTerrain.z;
        _LengthCount = _LengthTerrain / 2;
        _LineCount = _WidthTerrain / 2;
        number_needtogenerateObject = (int)(_LengthTerrain / distanceForEachRubber);
        number_linetogenerate = (int)(_WidthTerrain / distanceForEachRubber);

        for (int i = 0; i < number_linetogenerate + 1; i++)
        {
            _LengthCount = -(_LengthTerrain / 2);
            for (int j = 0; j < number_needtogenerateObject + 1; j++)
            {
                allRubberPositions.Add(new Vector3(
                    centerPos.x + _LengthCount,
                    rubberYPosition,
                    centerPos.z - _LineCount));
                _LengthCount += distanceForEachRubber;
            }
            _LineCount -= distanceForEachRubber;
        }
    }

    void AreaTerrain()
    {
        if (_meshRenderers == null || _meshRenderers.Length == 0) return;

        for (int i = 0; i < _meshRenderers.Length; i++)
        {
            Vector3 size = _meshRenderers[i].bounds.size;
            GenerateRubber(size, terrainObjects[i].transform, terrainObjects[i].transform.position);
        }
        Debug.Log($"[GameSpawn] Total positions: {allRubberPositions.Count}");
    }

    void AssignComponentToMeshRenderer()
    {
        _meshRenderers = new MeshRenderer[terrainObjects.Length];
        for (int i = 0; i < _meshRenderers.Length; i++)
            _meshRenderers[i] = terrainObjects[i].GetComponent<MeshRenderer>();
    }
}
