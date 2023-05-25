using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PauseMenu : MonoBehaviour
{
    public GameObject PausePanel;

    public void PauseGame() {
        Time.timeScale = 0;
        PausePanel.SetActive(true);
    }
    public void ContinueGame() {
        Time.timeScale = 1;
        PausePanel.SetActive(false);
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
