using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.SceneManagement;

public class ExampleSceneLoader : MonoBehaviour
{
  public void ToMainMenu()
    {
        if(MID_SceneLoader.HasInstance)
        MID_SceneLoader.Instance.LoadScene((int)SceneId.MainMenu);
        
    }
    public void ToLobby()
    {
        if (MID_SceneLoader.HasInstance)
            MID_SceneLoader.Instance.LoadScene((int)SceneId.Lobby,delayMs:1000);
    }
}
