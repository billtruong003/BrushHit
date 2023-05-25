using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ScenesController : MonoBehaviour
{
    [Header("FPS")]
    public int targetFPS = 120;
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
    }
    
    public void LoadScene(string SceneName){
        SceneManager.LoadScene(SceneName);
    }
}
