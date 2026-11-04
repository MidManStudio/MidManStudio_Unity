// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "PhysicsProjectile3D.cs"
// ============================================================================
// 3D Rigidbody implementation of PhysicsProjectileBase.
// OnLaunch reads the gravity setting from the projectile's config, as
// PhysicsProjectile2D does. If the config's GravityScale > 0, useGravity is forced
// on regardless of the inspector toggle. The config value only switches Unity's
// gravity on or off and does not scale it. The inspector toggle is the fallback
// when no config resolves.
//
// PREFAB REQUIREMENTS: Rigidbody, SphereCollider, NetworkObject, and a poolable
// object type that is a 3D one.
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Core;

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
        /// Applies the collider size that PhysicsProjectileBase.ApplyConfigScale
        /// chose (see there for why the collider is resized instead of the
        /// transform being scaled).
        ///
        /// SphereCollider (required by this class, so always present) has no
        /// direction, as with PhysicsProjectile2D's CircleCollider2D: the radius
        /// follows sizeY (the cross section), not sizeX (the travel-direction
        /// length). The same judgement call applies; tune it against your sprites
        /// or meshes if the hit area feels off.
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

            // Gravity comes from the registered config, as in PhysicsProjectile2D.
            // If config.GravityScale > 0, gravity is enabled regardless of the
            // inspector toggle.
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

                    // Same reasoning as PhysicsProjectile2D.OnLaunch: Wave, Circular
                    // and Teleport drive the body directly every FixedUpdate and
                    // none of them accounts for gravity (as with the Rust sim's
                    // tick_wave_3d, tick_circular_3d and tick_teleport_3d). Guided
                    // is excluded on purpose: it turns whatever velocity physics is
                    // already producing, so gravity stays on.
                    if (cfg.MovementType == ProjectileMovementType.Wave ||
                        cfg.MovementType == ProjectileMovementType.Circular ||
                        cfg.MovementType == ProjectileMovementType.Teleport)
                        useGravity = false;
                }
            }

            _rb.useGravity  = useGravity;
            _rb.drag        = _drag;
            _rb.angularDrag = _angularDrag;
            _rb.isKinematic = false;
            // Fires along transform.forward. The spawner sets the rotation with
            // Quaternion.LookRotation(direction), so transform.forward is the fire
            // direction.
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

        /// <summary>Wave/Circular FixedUpdate driver — see PhysicsProjectileBase.FixedUpdate.</summary>
        protected override void ApplyMovementVelocity(Vector3 velocity)
        {
            if (_rb != null) _rb.velocity = velocity;
        }

        /// <summary>Guided FixedUpdate driver — see PhysicsProjectileBase.ApplyGuided.</summary>
        protected override Vector3 GetCurrentVelocity()
            => _rb != null ? _rb.velocity : Vector3.zero;

        /// <summary>Teleport FixedUpdate driver — see PhysicsProjectileBase.ApplyTeleport.</summary>
        protected override void TeleportBody(Vector3 position)
        {
            if (_rb != null) _rb.position = position;
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
