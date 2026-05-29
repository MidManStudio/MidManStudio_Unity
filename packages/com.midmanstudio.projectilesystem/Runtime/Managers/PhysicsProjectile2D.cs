// packages/com.midmanstudio.projectilesystem/Runtime/Managers/PhysicsProjectile2D.cs
//
// Concrete 2D physics projectile.
// PREFAB REQUIREMENTS:
//   - This script (PhysicsProjectile2D)
//   - Rigidbody2D
//   - CircleCollider2D (or PolygonCollider2D) — trigger or collision
//   - NetworkObject
//   - NetworkTransform
//   - LocalPoolReturn (for NetworkObjectPool return)
//
// Pool entry in NetworkObjectPool: BaseProjectileBlueprint_2D -> this prefab
// Player: assign this pool type to _physicsPoolType2D in NetworkedDimensionPlayer

using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Logging;

namespace MidManStudio.Projectiles.Managers
{
    public sealed class PhysicsProjectile2D : PhysicsProjectileBase
    {
        #region Inspector

        [Header("2D Physics Settings")]
        [SerializeField] private float _drag        = 0f;
        [SerializeField] private bool  _useGravity  = false;

        #endregion

        #region State

        private Rigidbody2D _rb;

        #endregion

        #region PhysicsProjectileBase

        protected override bool Is2D => true;

        protected override void OnPhysicsSetup()
        {
            _rb = GetComponent<Rigidbody2D>();
            if (_rb == null)
                MID_Logger.LogError(_logLevel,
                    $"PhysicsProjectile2D: No Rigidbody2D on '{name}'. " +
                    "Add Rigidbody2D to the 2D physics projectile prefab.",
                    nameof(PhysicsProjectile2D));
        }

        protected override Vector3 OnLaunch(float bulletVelocity)
        {
            if (_rb == null) return transform.right;

            _rb.gravityScale = _useGravity ? 1f : 0f;
            _rb.drag         = _drag;
            _rb.isKinematic  = false;
            // 2D: fire along transform.right
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
            if (!IsServer) return;
            Vector3 pt = col.contacts.Length > 0
                ? (Vector3)col.contacts[0].point
                : transform.position;
            HandleHit2D(col.gameObject, pt);
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!IsServer) return;
            HandleHit2D(other.gameObject, transform.position);
        }

        #endregion
    }
}
