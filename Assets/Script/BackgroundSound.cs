using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class BackgroundSound : MonoBehaviour
{
    public AudioClip backgroundSound;

    private AudioSource audioSource;

    private void Start()
    {
        audioSource = GetComponent<AudioSource>();
        SetupAudioSource();
        PlayBackgroundSound();
    }

    private void SetupAudioSource()
    {
        audioSource.clip = backgroundSound;
        audioSource.loop = true;
    }

    private void PlayBackgroundSound()
    {
        audioSource.Play();
    }
}
