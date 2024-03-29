using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;
using UnityEngine.UI;

public class ScenesController : MonoBehaviour
{
    [Header("Loading Scene")]
    public GameObject LoadingScreen;
    public Image LoadingBarFill;
    public float speed;

    [Header("FPS")]
    public int targetFPS = 120;
    public TextMeshProUGUI fpsText;
    public enum SceneName
    {
        Loading = 0,
        Game = 1,
    }
    private void Start()
    {
        Application.targetFrameRate = targetFPS;
        Time.timeScale = 0;
        Time.timeScale = 1;
        StartCoroutine(FPS_Setup());
    }
    private void Update() {
        
    }
    
    public void LoadScene(string SceneName)
    {
        StartCoroutine(LoadSceneAsync(SceneName));
    }
    public void ResetGame(){
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }
    IEnumerator LoadSceneAsync(string SceneName)
    {

        AsyncOperation operation = SceneManager.LoadSceneAsync(SceneName);
        LoadingScreen.SetActive(true);
        while(!operation.isDone)
        {
            float progressValue = Mathf.Clamp01(operation.progress / speed);
            LoadingBarFill.fillAmount = progressValue;

            yield return null;
        }
        
        
        yield return new WaitForSeconds(2f);
        
    }
    IEnumerator FPS_Setup()
    {
        while(true && fpsText != null){
            int fps = (int)(1f / Time.deltaTime);
            fpsText.text = "FPS: " + fps.ToString();
            yield return new WaitForSeconds(0.2f);
        }
    }
}
