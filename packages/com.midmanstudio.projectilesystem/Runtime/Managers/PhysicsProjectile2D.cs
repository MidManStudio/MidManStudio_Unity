// packages/com.midmanstudio.projectilesystem/Runtime/Managers/PhysicsProjectile2D.cs
//
// Concrete 2D physics projectile.
//
// PREFAB REQUIREMENTS (enforced by RequireComponent):
//   - Rigidbody2D
//   - CircleCollider2D  (or PolygonCollider2D / BoxCollider2D)
//   - NetworkObject
//   - NetworkTransform  (inherited via NetworkProjectileBase -> NetworkTransform)
//
// POOL SETUP:
//   Add a NetworkObjectPool entry: BaseProjectileBlueprint_2D -> this prefab.
//   In NetworkedDimensionPlayer: _physicsPoolType2D = BaseProjectileBlueprint_2D.
//
// RIGIDBODY2D SETTINGS (recommended):
//   Gravity Scale: 0 (unless _useGravity = true)
//   Collision Detection: Continuous
//   Interpolate: Interpolate
//   Body Type: Dynamic

using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Projectiles.Managers
{
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(CircleCollider2D))]
    public sealed class PhysicsProjectile2D : PhysicsProjectileBase
    {
        #region Inspector

        [Header("2D Physics Settings")]
        [SerializeField] private float _drag       = 0f;
        [SerializeField] private bool  _useGravity = false;

        #endregion

        #region State

        private Rigidbody2D _rb;

        #endregion

        #region PhysicsProjectileBase

        protected override bool Is2D => true;

        protected override void OnPhysicsSetup()
        {
            _rb = GetComponent<Rigidbody2D>();
            // RequireComponent guarantees it exists but log clearly if something
            // went very wrong (e.g. stripped in build)
            if (_rb == null)
                MID_Logger.LogError(_logLevel,
                    $"PhysicsProjectile2D: Rigidbody2D missing on '{name}'.",
                    nameof(PhysicsProjectile2D));
        }

        protected override Vector3 OnLaunch(float bulletVelocity)
        {
            if (_rb == null) return transform.right;

            _rb.gravityScale = _useGravity ? 1f : 0f;
            _rb.drag         = _drag;
            _rb.isKinematic  = false;
            _rb.velocity     = (Vector2)(transform.right * bulletVelocity);

            return transform.right;
        }

        protected override void StopPhysics()
        {
            if (_rb == null) return;
            _rb.velocity    = Vector2.zero;
            _rb.isKinematic = true;
        }

        #endregion

        #region Collision (Server Only)

        private void OnCollisionEnter2D(Collision2D col)
        {
            Vector3 pt = col.contacts.Length > 0
                ? (Vector3)col.contacts[0].point
                : transform.position;
            HandleHit2D(col.gameObject, pt);
        }

        private void OnTriggerEnter2D(Collider2D other)
            => HandleHit2D(other.gameObject, transform.position);

        #endregion
    }
}
