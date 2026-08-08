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
using MidManStudio.Projectiles.Core;
namespace MidManStudio.Projectiles.Managers
{
    [RequireComponent(typeof(Rigidbody2D))]
    public class PhysicsProjectile2D : PhysicsProjectileBase
    {
        [Header("2D Physics Settings")]
        [Tooltip("Overridden by config.GravityScale if a config is registered for _visualConfigId.")]
        [SerializeField] private float _drag        = 0f;
        [SerializeField] private float _gravityScale = 0f;

        private Rigidbody2D _rb;

        // SCALING FIX (see PhysicsProjectileBase.ApplyConfigScale doc comment).
        // Resolved once, lazily — whichever of the two the prefab actually has
        // (see this file's own header comment: "CapsuleCollider2D added as
        // alternative to CircleCollider2D").
        private CapsuleCollider2D _capsuleCollider;
        private CircleCollider2D  _circleCollider;
        private bool              _colliderResolved;

        protected override bool Is2D => true;


        protected override void OnPhysicsSetup()
        {
            _rb = GetComponent<Rigidbody2D>();
            if (_rb == null)
                MID_Logger.LogError(_logLevel,
                    $"PhysicsProjectile2D: No Rigidbody2D on '{name}'.",
                    nameof(PhysicsProjectile2D));
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
        /// CapsuleCollider2D.size is (long-axis, cross-axis) relative to its
        /// OWN .direction — this deliberately does not touch .direction
        /// (whatever the prefab author set stays as-is), it just maps
        /// sizeX (the travel-direction length — 2D convention here is
        /// "fire along transform.right", see OnLaunch below) onto whichever
        /// local axis is currently the capsule's long axis, so this is
        /// correct regardless of prefab orientation.
        ///
        /// CircleCollider2D has no directional axis at all, so there's no
        /// way to represent an elongated (sizeX != sizeY) shape exactly —
        /// radius tracks sizeY (the cross-section/"width"), not sizeX, so
        /// the hit area doesn't balloon out along the travel axis for
        /// long/thin projectile sprites. This is a judgement call, not a
        /// verified-correct mapping (couldn't render/compare in-editor
        /// here) — tune it against your actual sprites if the feel is off,
        /// or swap the prefab to CapsuleCollider2D for an exact fit.
        /// </summary>
        protected override void ApplyColliderSize(float sizeX, float sizeY)
        {
            if (!_colliderResolved)
            {
                _capsuleCollider  = GetComponent<CapsuleCollider2D>();
                _circleCollider   = _capsuleCollider == null ? GetComponent<CircleCollider2D>() : null;
                _colliderResolved = true;

                if (_capsuleCollider == null && _circleCollider == null)
                    MID_Logger.LogWarning(_logLevel,
                        $"PhysicsProjectile2D: no CapsuleCollider2D or CircleCollider2D " +
                        $"on '{name}' — cannot apply config scale.",
                        nameof(PhysicsProjectile2D));
            }

            if (_capsuleCollider != null)
            {
                _capsuleCollider.size = _capsuleCollider.direction == CapsuleDirection2D.Horizontal
                    ? new Vector2(sizeX, sizeY)
                    : new Vector2(sizeY, sizeX);
            }
            else if (_circleCollider != null)
            {
                _circleCollider.radius = sizeY * 0.5f;
            }
        }

        protected override Vector3 OnLaunch(float bulletVelocity)
        {
            if (_rb == null) return transform.right;

            // Consult config for gravity scale — mirrors original Projectile.cs
            float gravity = _gravityScale;
            if (ProjectileRegistry.HasInstance)
            {
                var cfg = ProjectileRegistry.Instance.Get(VisualConfigId);
                if (cfg != null)
                {
                    gravity = cfg.GravityScale;

                    // MOVEMENT TYPE FIX ("it should offset the physics body"):
                    // Wave/Circular/Teleport all drive the body directly every
                    // FixedUpdate (velocity for Wave/Circular, direct
                    // reposition for Teleport — see PhysicsProjectileBase's
                    // FixedUpdate/ApplyWaveCircular/ApplyTeleport), none of
                    // which account for gravity, matching RustSim's own
                    // tick_wave/tick_circular/tick_teleport, which don't
                    // combine with gravity either. Guided is deliberately
                    // excluded — it turns whatever velocity real physics is
                    // already producing rather than overriding it, so gravity
                    // stays on and a homing shot still arcs while it steers.
                    if (cfg.MovementType == ProjectileMovementType.Wave ||
                        cfg.MovementType == ProjectileMovementType.Circular ||
                        cfg.MovementType == ProjectileMovementType.Teleport)
                        gravity = 0f;
                }
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

        /// <summary>Wave/Circular FixedUpdate driver — see PhysicsProjectileBase.FixedUpdate.</summary>
        protected override void ApplyMovementVelocity(Vector3 velocity)
        {
            if (_rb != null) _rb.velocity = (Vector2)velocity;
        }

        /// <summary>Guided FixedUpdate driver — see PhysicsProjectileBase.ApplyGuided.</summary>
        protected override Vector3 GetCurrentVelocity()
            => _rb != null ? (Vector3)_rb.velocity : Vector3.zero;

        /// <summary>Teleport FixedUpdate driver — see PhysicsProjectileBase.ApplyTeleport.</summary>
        protected override void TeleportBody(Vector3 position)
        {
            if (_rb != null) _rb.position = (Vector2)position;
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
