// packages/com.midmanstudio.projectilesystem/Runtime/Managers/PhysicsProjectile3D.cs
//
// Concrete 3D physics projectile.
//
// PREFAB REQUIREMENTS (enforced by RequireComponent):
//   - Rigidbody
//   - SphereCollider  (or CapsuleCollider / BoxCollider)
//   - NetworkObject
//   - NetworkTransform (inherited via NetworkProjectileBase -> NetworkTransform)
//
// POOL SETUP:
//   Add a NetworkObjectPool entry: BaseProjectileBlueprint_3D -> this prefab.
//   In NetworkedDimensionPlayer: _physicsPoolType3D = BaseProjectileBlueprint_3D.
//
// RIGIDBODY SETTINGS (recommended):
//   Use Gravity: false (unless _useGravity = true)
//   Collision Detection: Continuous
//   Interpolate: Interpolate
//   Constraints: Freeze Rotation X Y Z (so the shell doesn't tumble)

using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Projectiles.Managers
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(SphereCollider))]
    public sealed class PhysicsProjectile3D : PhysicsProjectileBase
    {
        #region Inspector

        [Header("3D Physics Settings")]
        [SerializeField] private float _drag        = 0f;
        [SerializeField] private float _angularDrag  = 0.05f;
        [SerializeField] private bool  _useGravity   = false;

        #endregion

        #region State

        private Rigidbody _rb;

        #endregion

        #region PhysicsProjectileBase

        protected override bool Is2D => false;

        protected override void OnPhysicsSetup()
        {
            _rb = GetComponent<Rigidbody>();
            if (_rb == null)
                MID_Logger.LogError(_logLevel,
                    $"PhysicsProjectile3D: Rigidbody missing on '{name}'.",
                    nameof(PhysicsProjectile3D));
        }

        protected override Vector3 OnLaunch(float bulletVelocity)
        {
            if (_rb == null) return transform.forward;

            _rb.useGravity  = _useGravity;
            _rb.drag        = _drag;
            _rb.angularDrag = _angularDrag;
            _rb.isKinematic = false;
            _rb.velocity    = transform.forward * bulletVelocity;

            return transform.forward;
        }

        protected override void StopPhysics()
        {
            if (_rb == null) return;
            _rb.velocity    = Vector3.zero;
            _rb.isKinematic = true;
        }

        #endregion

        #region Collision (Server Only)

        private void OnCollisionEnter(Collision col)
        {
            Vector3 pt = col.contacts.Length > 0
                ? col.contacts[0].point
                : transform.position;
            HandleHit3D(col.gameObject, pt);
        }

        private void OnTriggerEnter(Collider other)
            => HandleHit3D(other.gameObject, transform.position);

        #endregion
    }
}
