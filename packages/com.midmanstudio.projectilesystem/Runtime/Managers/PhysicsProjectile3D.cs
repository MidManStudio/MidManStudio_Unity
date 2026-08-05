// OnLaunch  consults ProjectileRegistry for gravity scale — mirrors PhysicsProjectile2D.
//   Previously _useGravity was a hardcoded inspector field with no config hookup.
//   If the config's GravityScale > 0, useGravity is forced true regardless of the inspector toggle.
//   The inspector toggle still controls the fallback when no config is found.

//   -  Ensure u set poolable object type to 3d
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.Managers
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(SphereCollider))]
    public class PhysicsProjectile3D : PhysicsProjectileBase
    {
        #region Inspector

        [Header("3D Physics Settings")]
        [SerializeField] private float _drag        = 0f;
        [SerializeField] private float _angularDrag  = 0.05f;
        [Tooltip("Inspector fallback. Overridden by config.GravityScale > 0 when a config is registered.")]
        [SerializeField] private bool  _useGravity   = false;

        #endregion

        #region State

        private Rigidbody       _rb;
        private SphereCollider  _sphereCollider;

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

        /// <summary>
        /// SCALING FIX — see PhysicsProjectileBase.ApplyConfigScale for the
        /// full explanation of why this exists, why it resizes the collider
        /// directly instead of scaling transform.localScale, and why it's
        /// called with a raw (sizeX, sizeY) pair rather than a config
        /// (PhysicsProjectileBase owns deciding the target size and whether
        /// to animate into it via GrowColliderRoutine — this method just
        /// applies whatever size it's given).
        ///
        /// SphereCollider (this class's [RequireComponent], so it's always
        /// present) has no directional axis, same situation as
        /// PhysicsProjectile2D's CircleCollider2D fallback — radius tracks
        /// sizeY (cross-section), not sizeX (travel-direction length). Same
        /// judgement call noted there: tune against your actual
        /// sprites/meshes if the hit-area feel is off.
        /// </summary>
        protected override void ApplyColliderSize(float sizeX, float sizeY)
        {
            if (_sphereCollider == null) _sphereCollider = GetComponent<SphereCollider>();
            if (_sphereCollider == null) return;

            _sphereCollider.radius = sizeY * 0.5f;
        }

        protected override Vector3 OnLaunch(float bulletVelocity)
        {
            if (_rb == null) return transform.forward;

            // FIX: consult the registered config for gravity scale — same pattern as PhysicsProjectile2D.
            // If config.GravityScale > 0, enable gravity regardless of inspector toggle.
            bool useGravity = _useGravity;
            if (ProjectileRegistry.HasInstance)
            {
                var cfg = ProjectileRegistry.Instance.Get(VisualConfigId);
                if (cfg != null)
                {
                    if (cfg.GravityScale > 0f)
                        useGravity = true;
                    else if (cfg.GravityScale == 0f && !_useGravity)
                        useGravity = false;
                }
            }

            _rb.useGravity  = useGravity;
            _rb.drag        = _drag;
            _rb.angularDrag = _angularDrag;
            _rb.isKinematic = false;
            // Fires along transform.forward — SpawnPhysicsProjectileLocal sets rotation via
            // Quaternion.LookRotation(direction) so transform.forward == fire direction.
            _rb.velocity    = transform.forward * bulletVelocity;

            MID_Logger.LogDebug(_logLevel,
                $"PhysicsProjectile3D launched: speed={bulletVelocity:F1} " +
                $"dir={transform.forward} gravity={useGravity}",
                nameof(PhysicsProjectile3D));

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
