// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/samples.md, section "Test_VisualAndAudio.cs"
// ============================================================================
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Pools;using MidManStudio.Core.Audio; using MidManStudio.Core.FX;
using UnityEngine.UIElements;

/// <summary>
/// Test input for the VisualEffects sample. F plays a small muzzle flash through <c>GlobalFXManager</c> and a pooled
/// <c>SpawnableAudio</c> one-shot of <c>regularAudio</c>. E plays a small explosion and native audio clip 1 through
/// <c>MID_NativeAudioBridge</c>. Both happen at the mouse position (screen to world, z set to 0), at most once per
/// <c>fireRate</c> seconds. It uses the old <c>Input</c> class and the main camera.
///
/// This script uses <c>MID_SpawnableAudio</c> and <c>MID_NativeAudioBridge</c>, which are in the
/// <c>com.midmanstudio.audio</c> package. Without that package the sample does not compile. The FX calls pass -1 as the
/// last argument, which is the audio volume, not the particle count default; the audio package clamps a volume to 0 to
/// 1, so the effect's own relay sound (if an audio relay is in the scene) is silent.
/// </summary>
public class Test_VisualAndAudio : MonoBehaviour
{
    [Header("Spawn Settings")]
    [SerializeField] private PoolableObjectType objectToSpawn; // Assign the Prefab you want to spawn here
    [SerializeField] private AudioClip regularAudio;
    [SerializeField] private float fireRate = 0.2f;     // Cooldown time in seconds between spawns

    private float nextSpawnTime = 0f;
    [SerializeField]  private Camera mainCamera;

    void Start()
    {
        // Cache the main camera for better performance
        mainCamera = Camera.main;

        if (mainCamera == null)
        {
            Debug.LogError("Test_VisualAndAudio: No Camera tagged 'MainCamera' found in the scene!");
        }
    }

    void Update()
    {
        // Check for the 'F' key press and verify the throttling cooldown has passed
        if (Input.GetKeyDown(KeyCode.F) && Time.time >= nextSpawnTime)
        {
              
            SpawnObjectAtMouse(false);

            // Set the next allowed spawn time
            nextSpawnTime = Time.time + fireRate;
        }else if (Input.GetKeyDown(KeyCode.E) && Time.time >= nextSpawnTime)
        {
            SpawnObjectAtMouse(true);

            // Set the next allowed spawn time
            nextSpawnTime = Time.time + fireRate;
        }
    }

    private void SpawnObjectAtMouse(bool isExplosion)
    {
        if (mainCamera == null) return;

        // 1. Get the current mouse position in screen pixels (X, Y)
        Vector3 mouseScreenPos = Input.mousePosition;

        // 2. Convert the screen pixels into actual 3D/2D World coordinates
        Vector3 mouseWorldPos = mainCamera.ScreenToWorldPoint(mouseScreenPos);

        // 3. For 2D space, snap the Z axis back to 0 so it doesn't spawn behind the camera
        mouseWorldPos.z = 0f;
        if (isExplosion)
        {
            SpawnExplosion(mouseWorldPos);
        }
        else
        {
            SpawnMuzzleFlash(mouseWorldPos);
        }
       
    }
   private void SpawnMuzzleFlash(Vector3 position)
    {
        if (GlobalFXManager.HasInstance)
        {
            GlobalFXManager.Instance.TriggerEffect(EffectCategory.MuzzleFlash, EffectType.SmallMuzzle, position, Vector3.zero, 20,-1);
            if (LocalObjectPool.HasInstance)
            {
                var audio = LocalObjectPool.Instance.GetObject(PoolableObjectType.SpawnableAudio, position, Quaternion.identity);
                audio.GetComponent<MID_SpawnableAudio>().PlayOneShot(regularAudio,position);
            }

        }
    }
     private void SpawnExplosion(Vector3 position)
    {
        if (GlobalFXManager.HasInstance)
        {
            GlobalFXManager.Instance.TriggerEffect(EffectCategory.Explosion, EffectType.SmallExplosion, position, Vector3.zero, 80,-1);
            if (MID_NativeAudioBridge.HasInstance)
            {    
                MID_NativeAudioBridge.Instance.PlayClip(1);
            }

        }
    }
}
