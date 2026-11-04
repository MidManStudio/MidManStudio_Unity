// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "PhysicsProjectile2D.cs"
// ============================================================================
// 2D Rigidbody implementation of PhysicsProjectileBase.
// OnLaunch sets velocity = transform.right * BulletVelocity (the 2D convention:
// a shot flies along transform.right) and reads the gravity scale from the
// projectile's config when one resolves.
//
// PREFAB REQUIREMENTS:
//   - This script
//   - Rigidbody2D
//   - CapsuleCollider2D or CircleCollider2D
//   - NetworkObject
//   - NetworkTransform (via NetworkProjectileBase)
//   - The poolable object type must be a 2D one
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

        // Used by ApplyColliderSize (see PhysicsProjectileBase.ApplyConfigScale).
        // Resolved once, lazily: whichever of the two the prefab actually has.
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
        /// Applies the collider size that PhysicsProjectileBase.ApplyConfigScale
        /// chose (see there for why the collider is resized instead of the
        /// transform being scaled). It receives a raw (sizeX, sizeY) pair; the base
        /// class decides the target size and whether to grow into it.
        ///
        /// CapsuleCollider2D.size is (long axis, cross axis) relative to its own
        /// .direction. This method leaves .direction as the prefab author set it
        /// and maps sizeX (the travel-direction length; shots fly along
        /// transform.right, see OnLaunch) onto whichever local axis is the
        /// capsule's long axis, so it is correct for any prefab orientation.
        ///
        /// CircleCollider2D has no direction, so it cannot represent an elongated
        /// (sizeX != sizeY) shape. Its radius follows sizeY (the cross section),
        /// so the hit area does not grow along the travel axis for a long, thin
        /// sprite. That mapping is a judgement call; use a CapsuleCollider2D for
        /// an exact fit.
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

            // The config's gravity scale replaces the inspector value when a
            // config resolves.
            float gravity = _gravityScale;
            if (ProjectileRegistry.HasInstance)
            {
                var cfg = ProjectileRegistry.Instance.Get(VisualConfigId);
                if (cfg != null)
                {
                    gravity = cfg.GravityScale;

                    // Wave, Circular and Teleport drive the body directly every
                    // FixedUpdate (velocity for Wave and Circular, a direct
                    // reposition for Teleport, see PhysicsProjectileBase), and
                    // none of them accounts for gravity. The Rust sim's tick_wave,
                    // tick_circular and tick_teleport do not combine with gravity
                    // either. Guided is excluded on purpose: it turns whatever
                    // velocity physics is already producing, so gravity stays on
                    // and a homing shot still arcs while it steers.
                    if (cfg.MovementType == ProjectileMovementType.Wave ||
                        cfg.MovementType == ProjectileMovementType.Circular ||
                        cfg.MovementType == ProjectileMovementType.Teleport)
                        gravity = 0f;
                }
            }

            _rb.gravityScale = gravity;
            _rb.drag         = _drag;
            _rb.isKinematic  = false;

            // 2D convention: fire along transform.right.
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

        // Both a solid collision and a trigger overlap count as a hit. The prefab
        // may use a CapsuleCollider2D or a CircleCollider2D.
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
