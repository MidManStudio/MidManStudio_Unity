// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/samples.md, section "Test_ProjectileSpawner.cs"
// ============================================================================
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Pools;

/// <summary>
/// Fires a <c>Projectile_Visual2D</c> from <c>LocalObjectPool</c> at <c>shotpoint</c> (position and rotation) when F is
/// pressed, at most once per <c>firerate</c> seconds (default 1). The cooldown starts on a key press even when the
/// pools are missing and nothing was fired. It reads the old <c>Input</c> class, so Active Input Handling must be
/// Input Manager or Both. Used by the PoolSystemTest scene.
/// </summary>
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