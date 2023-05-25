using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ScenesController : MonoBehaviour
{
    public enum SceneName
    {
        Loading = 0,
        Game = 1,
    }
    private void Start()
    {
    }
    
    public void LoadScene(string SceneName){
        SceneManager.LoadScene(SceneManager.GetSceneByName(SceneName).buildIndex);
    }
}
