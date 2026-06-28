// OnLaunch mirrors original Projectile.cs velocity setup:
//   velocity = transform.right * BulletVelocity  (2D convention)
//   gravityScale from config if available
//   CapsuleCollider2D added as alternative to CircleCollider2D
//   Config SO consulted for gravity scale at launch time
//
// PREFAB REQUIREMENTS:
//   - This script
//   - Rigidbody2D
//   - CapsuleCollider2D  (matches original Projectile.cs — or CircleCollider2D)
//   - NetworkObject
//   - NetworkTransform (via NetworkProjectileBase)
//   -  Ensure u set poolable object type to 2d
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Projectiles.Config;
namespace MidManStudio.Projectiles.Managers
{
    [RequireComponent(typeof(Rigidbody2D))]
    public sealed class PhysicsProjectile2D : PhysicsProjectileBase
    {
        [Header("2D Physics Settings")]
        [Tooltip("Overridden by config.GravityScale if a config is registered for _visualConfigId.")]
        [SerializeField] private float _drag        = 0f;
        [SerializeField] private float _gravityScale = 0f;

        private Rigidbody2D _rb;

        protected override bool Is2D => true;


        protected override void OnPhysicsSetup()
        {
            _rb = GetComponent<Rigidbody2D>();
            if (_rb == null)
                MID_Logger.LogError(_logLevel,
                    $"PhysicsProjectile2D: No Rigidbody2D on '{name}'.",
                    nameof(PhysicsProjectile2D));
        }

        protected override Vector3 OnLaunch(float bulletVelocity)
        {
            if (_rb == null) return transform.right;

            // Consult config for gravity scale — mirrors original Projectile.cs
            float gravity = _gravityScale;
            if (ProjectileRegistry.HasInstance)
            {
                var cfg = ProjectileRegistry.Instance.Get(_visualConfigId);
                if (cfg != null) gravity = cfg.GravityScale;
            }

            _rb.gravityScale = gravity;
            _rb.drag         = _drag;
            _rb.isKinematic  = false;

            // 2D convention: fire along transform.right — EXACTLY as original Projectile.cs
            // m_ProjectileRigidBody2D.velocity = transform.right * internalBulletVelocity
            _rb.velocity = (Vector2)(transform.right * bulletVelocity);

            MID_Logger.LogDebug(_logLevel,
                $"PhysicsProjectile2D launched: speed={bulletVelocity} " +
                $"dir={transform.right} gravity={gravity}",
                nameof(PhysicsProjectile2D));

            return transform.right;
        }

        protected override void StopPhysics()
        {
            if (_rb == null) return;
            _rb.velocity    = Vector2.zero;
            _rb.isKinematic = true;
        }

        // Accept either CapsuleCollider2D (original) or CircleCollider2D
        private void OnCollisionEnter2D(Collision2D col)
        {
            Vector3 pt = col.contacts.Length > 0
                ? (Vector3)col.contacts[0].point
                : transform.position;
            HandleHit2D(col.gameObject, pt);
        }

        private void OnTriggerEnter2D(Collider2D other)
            => HandleHit2D(other.gameObject, transform.position);
    }
}
