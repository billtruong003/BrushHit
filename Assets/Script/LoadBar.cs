using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LoadBar : MonoBehaviour
{
    public ProgressBarPro LoadingBar;
    float randomUp;
    [SerializeField] public static string SceneLoad;
    // Start is called before the first frame update
    void Start()
    {
        LoadingBar.SetValue(0);
        StartCoroutine(LoadBarProgressIn(0.1f));
    }

    // Update is called once per frame
    void Update()
    {
    }

    IEnumerator LoadBarProgressIn(float waitTime)
    {
        while (LoadingBar.Value < 100){
            randomUp = Random.Range(1,3);
            LoadingBar.SetValue(LoadingBar.Value + (randomUp/100));
            Debug.Log(LoadingBar.Value);
            yield return new WaitForSeconds(waitTime);
            if (LoadingBar.Value >= 100) {
                SceneManager.LoadScene(SceneLoad);
            }
        }
        
    }

}
