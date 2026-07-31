using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Pools;

public class Test_ProjectileSpawner : MonoBehaviour
{
    public Transform shotpoint;
    public float firerate = 1f;
    private float nextFireTime = 0f;

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F) && Time.time >= nextFireTime)
        {
            if (LocalObjectPool.HasInstance && LocalParticlePool.HasInstance)
            {
                // Spawns the projectile at the position and rotation of your shotpoint
                var projectile = LocalObjectPool.Instance.GetObject(PoolableObjectType.Projectile_Visual2D, shotpoint.position, shotpoint.rotation);
            }
            nextFireTime = Time.time + firerate;
        }
    }
}