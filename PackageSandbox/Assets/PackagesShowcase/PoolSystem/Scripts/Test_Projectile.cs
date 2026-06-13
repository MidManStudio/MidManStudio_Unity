using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Pools;

public class Test_Projectile : MonoBehaviour
{
    public CircleCollider2D collider;
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

            // FIX: Changed transform.forward (3D) to transform.right (2D Forward)
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