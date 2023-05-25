using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PauseMenu : MonoBehaviour
{
    public GameObject PausePanel;
    [Header("Win Lose")]
    public GameObject WinPanel;
    public bool Win;
    public void PauseGame() {
        Time.timeScale = 0;
        PausePanel.SetActive(true);
    }
    public void ContinueGame() {
        Time.timeScale = 1;
        PausePanel.SetActive(false);
        
    }
    public void WinGame() {
        Time.timeScale = 0;
        WinPanel.SetActive(true);
        Win = true;
    }
    
    // Start is called before the first frame update
    void Start()
    {
        GameSpawn.numberObTrue = 0;
        Win = false;
    }

    // Update is called once per frame
    void Update()
    {
        if(GameSpawn.numberObTrue >= GameSpawn.sum_object && !Win) {
            WinGame();
        }
    }
}
