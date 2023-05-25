using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PauseMenu : MonoBehaviour
{
    public GameObject PausePanel;
    [Header("Win Lose")]
    public GameObject WinPanel;
    public void PauseGame() {
        Time.timeScale = 0;
        PausePanel.SetActive(true);
    }
    public void ContinueGame() {
        Time.timeScale = 1;
        PausePanel.SetActive(false);
        
    }
    public static void WinGame() {
        Time.timeScale = 0;
    }
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
