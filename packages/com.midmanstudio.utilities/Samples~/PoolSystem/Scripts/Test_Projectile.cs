// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/samples.md, section "Test_Projectile.cs"
// ============================================================================
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Pools;

/// <summary>
/// Projectile on the ProjectileVisual2D prefab of the PoolSystem sample. Each time the object leaves the pool
/// (<c>OnEnable</c>) it clears its velocity and pushes itself along its local right axis, which is forward in 2D, with
/// an impulse of <c>speed</c>. On any 2D trigger contact it takes a <c>Projectile_Impact</c> particle from
/// <c>LocalParticlePool</c> at its position and returns itself with <c>poolReturn.ReturnToPoolNow()</c>. Both pools
/// must exist in the scene, and the collider and pool-return fields must be set. Any trigger counts, not only targets.
/// </summary>
public class Test_Projectile : MonoBehaviour
{
    public new CircleCollider2D collider;
    public LocalPoolReturn poolReturn;
    public Rigidbody2D rb;
    public float speed = 10f; // Adjusted down slightly as Impulse force can be incredibly fast

    // OnEnable runs EVERY time the object is retrieved from the pool
    private void OnEnable()
    {
        if (rb != null)
        {
            // Reset velocity to prevent carrying over momentum from its previous life
            rb.velocity = Vector2.zero;

            // 2D: transform.right is the forward direction
            rb.AddForce(transform.right * speed, ForceMode2D.Impulse);
        }
    }

    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (LocalObjectPool.HasInstance && LocalParticlePool.HasInstance && collider != null && poolReturn != null)
        {
            LocalParticlePool.Instance.GetObject(PoolableParticleType.Projectile_Impact, transform.position, Quaternion.identity);
            poolReturn.ReturnToPoolNow();
        }
    }
}