// PhysicsProjectile.cs
// Concrete NetworkProjectileBase implementation for Unity physics-driven projectiles.
// Attach to a prefab alongside: NetworkObject, Rigidbody (3D) or Rigidbody2D (2D),
// and a Collider/Collider2D.
//
// Spawn flow (server):
//   1. var netObj = MID_MasterProjectileSystem.Instance
//          .SpawnPhysicsProjectile(PoolableNetworkObjectType.BaseProjectileBlueprint, pos, rot);
//   2. var proj = netObj.GetComponent<PhysicsProjectile>();
//   3. proj.SetOwnerContext(ownerMidId, firedByNetObjId, false, weaponLevel, damageMultiplier);
//   4. proj.InitialiseProjectile(ownerMidId, firedByNetObjId, speed, isBotOwner, weaponLevel);
//      → sets Rigidbody velocity = transform.forward * speed
//
// Clients follow via NetworkTransform (inherited from NetworkProjectileBase).
// Hit detection is server-only. OnHitServerConfirmed event fires for the damage system.
//
// Extend this in your game assembly for custom behavior:
//   public class RocketProjectile : PhysicsProjectile
//   {
//       protected override void OnImpactServer() { base.OnImpactServer(); LaunchExplosion(); }
//       protected override void OnSpawnImpactEffectClient(Vector3 p) { SpawnFireball(p); }
//   }

using System;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Logging;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Data;
using MidManStudio.Projectiles.Network;

namespace MidManStudio.Projectiles.Managers
{
    [DisallowMultipleComponent]
    public class PhysicsProjectile : NetworkProjectileBase
    {
        #region Inspector

        [Header("Physics Mode")]
        [Tooltip("True = Rigidbody2D (2D game). False = Rigidbody (3D game).")]
        [SerializeField] protected bool _use2D = false;

        [Header("Physics Settings")]
        [SerializeField] protected float _drag        = 0f;
        [SerializeField] protected float _angularDrag = 0.05f;
        [Tooltip("True for grenades/mortars. False for rockets/bullets.")]
        [SerializeField] protected bool  _useGravity  = false;

        [Header("Damage")]
        [Tooltip("Base impact damage. Multiplied by damageMultiplier from fire context.")]
        [SerializeField] protected float _baseDamage = 30f;
        [Tooltip("Explosion radius in world units. 0 = single target hit only.")]
        [SerializeField, Min(0f)] protected float _explosionRadius = 0f;
        [SerializeField] protected LayerMask _damageLayerMask = -1;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Events

        /// <summary>
        /// Fired on the server when a physics collision is confirmed.
        /// Subscribe from your game's damage system.
        /// </summary>
        public event Action<ProjectileHitPayload> OnHitServerConfirmed;

        #endregion

        #region Private State

        private Rigidbody   _rb3D;
        private Rigidbody2D _rb2D;
        private bool        _hasHit;

        // Owner context set via SetOwnerContext before InitialiseProjectile
        private ulong _ownerMidId;
        private ulong _firedByNetworkObjectId;
        private bool  _isBotOwner;
        private byte  _weaponLevel;
        private float _damageMultiplier = 1f;

        #endregion

        #region NGO Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            _hasHit = false;

            // Cache Rigidbody on each spawn (pool recycling may reuse the GO)
            if (_use2D) _rb2D = GetComponent<Rigidbody2D>();
            else        _rb3D = GetComponent<Rigidbody>();
        }

        #endregion

        #region Public API — Owner Context

        /// <summary>
        /// Set damage ownership context. Call on server before InitialiseProjectile().
        /// </summary>
        public void SetOwnerContext(
            ulong ownerMidId,
            ulong firedByNetworkObjectId,
            bool  isBotOwner,
            byte  weaponLevel,
            float damageMultiplier = 1f)
        {
            _ownerMidId             = ownerMidId;
            _firedByNetworkObjectId = firedByNetworkObjectId;
            _isBotOwner             = isBotOwner;
            _weaponLevel            = weaponLevel;
            _damageMultiplier       = damageMultiplier;
        }

        #endregion

        #region NetworkProjectileBase Hooks

        /// <summary>
        /// Fires on server after NetworkVariables are written by InitialiseProjectile.
        /// Sets Rigidbody velocity along the projectile's forward axis.
        /// </summary>
        protected override void OnProjectileInitialised()
        {
            _hasHit = false;

            if (_use2D && _rb2D != null)
            {
                _rb2D.gravityScale = _useGravity ? 1f : 0f;
                _rb2D.drag         = _drag;
                _rb2D.isKinematic  = false;
                // 2D fire direction: transform.right (+X) is the typical 2D forward
                _rb2D.velocity = (Vector2)(transform.right * BulletVelocity);
            }
            else if (!_use2D && _rb3D != null)
            {
                _rb3D.useGravity  = _useGravity;
                _rb3D.drag        = _drag;
                _rb3D.angularDrag = _angularDrag;
                _rb3D.isKinematic = false;
                // 3D fire direction: transform.forward (+Z) — matches shot point orientation
                _rb3D.velocity = transform.forward * BulletVelocity;
            }

            MID_Logger.LogDebug(_logLevel,
                $"PhysicsProjectile initialised. velocity={(_use2D ? (Vector3)(Vector2)(transform.right * BulletVelocity) : transform.forward * BulletVelocity)}",
                nameof(PhysicsProjectile));
        }

        /// <summary>
        /// Fires on server inside DestroyProjectile() — override to add explosion logic.
        /// Call base.OnImpactServer() to broadcast the impact effect RPC to clients.
        /// </summary>
        protected override void OnImpactServer()
        {
            SpawnImpactEffectClientRpc(transform.position);
        }

        /// <summary>
        /// Fires on all clients when a collision notification arrives.
        /// Override in your game class to play audio, camera shake, etc.
        /// </summary>
        protected override void OnCollisionNotifiedClient() { }

        /// <summary>
        /// Fires on all clients when SpawnImpactEffectClientRpc arrives.
        /// Override to call LocalParticlePool.Instance.GetObject() etc.
        /// </summary>
        protected override void OnSpawnImpactEffectClient(Vector3 position) { }

        /// <summary>
        /// Fires on all clients when SpawnKillEffectClientRpc arrives.
        /// Override to instantiate kill-effect prefabs.
        /// </summary>
        protected override void OnSpawnKillEffectClient(Vector3 position) { }

        #endregion

        #region Collision Detection (Server Only)

        private void OnCollisionEnter(Collision col)
        {
            if (!IsServer || _hasHit) return;
            Vector3 pt = col.contacts.Length > 0 ? col.contacts[0].point : transform.position;
            ProcessHit3D(col.gameObject, pt);
        }

        private void OnCollisionEnter2D(Collision2D col)
        {
            if (!IsServer || _hasHit) return;
            Vector3 pt = col.contacts.Length > 0
                ? (Vector3)col.contacts[0].point : transform.position;
            ProcessHit2D(col.gameObject, pt);
        }

        // Trigger variants for projectiles using trigger colliders
        private void OnTriggerEnter(Collider other)
        {
            if (!IsServer || _hasHit) return;
            ProcessHit3D(other.gameObject, transform.position);
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!IsServer || _hasHit) return;
            ProcessHit2D(other.gameObject, transform.position);
        }

        #endregion

        #region Hit Processing

        private void ProcessHit3D(GameObject hitGO, Vector3 hitPoint)
        {
            _hasHit = true;
            StopPhysics();

            if (_explosionRadius > 0.01f)
                ApplyExplosionDamage3D(hitPoint);
            else
                ApplyDirectHit(hitGO.GetComponentInParent<NetworkObject>(), hitPoint, false);

            DestroyProjectile();
        }

        private void ProcessHit2D(GameObject hitGO, Vector3 hitPoint)
        {
            _hasHit = true;
            StopPhysics();

            if (_explosionRadius > 0.01f)
                ApplyExplosionDamage2D(hitPoint);
            else
                ApplyDirectHit(hitGO.GetComponentInParent<NetworkObject>(), hitPoint, true);

            DestroyProjectile();
        }

        private void ApplyDirectHit(NetworkObject targetNetObj, Vector3 hitPoint, bool is2D)
        {
            if (targetNetObj == null) return;

            float damage = _baseDamage * _damageMultiplier;
            FireHitEvent((uint)targetNetObj.NetworkObjectId, damage, hitPoint, is2D);
        }

        private void ApplyExplosionDamage3D(Vector3 centre)
        {
            var cols  = new Collider[32];
            int count = Physics.OverlapSphereNonAlloc(centre, _explosionRadius, cols, _damageLayerMask);

            for (int i = 0; i < count; i++)
            {
                var no = cols[i].GetComponentInParent<NetworkObject>();
                if (no == null) continue;

                // Scale damage by distance (linear falloff)
                float dist     = Vector3.Distance(centre, cols[i].transform.position);
                float falloff  = 1f - Mathf.Clamp01(dist / _explosionRadius);
                float damage   = _baseDamage * _damageMultiplier * falloff;

                FireHitEvent((uint)no.NetworkObjectId, damage, centre, false);
            }
        }

        private void ApplyExplosionDamage2D(Vector3 centre)
        {
            var cols  = new Collider2D[32];
            int count = Physics2D.OverlapCircleNonAlloc(
                (Vector2)centre, _explosionRadius, cols, _damageLayerMask);

            for (int i = 0; i < count; i++)
            {
                var no = cols[i].GetComponentInParent<NetworkObject>();
                if (no == null) continue;

                float dist    = Vector2.Distance((Vector2)centre, (Vector2)cols[i].transform.position);
                float falloff = 1f - Mathf.Clamp01(dist / _explosionRadius);
                float damage  = _baseDamage * _damageMultiplier * falloff;

                FireHitEvent((uint)no.NetworkObjectId, damage, centre, true);
            }
        }

        private void FireHitEvent(uint targetId, float damage, Vector3 hitPoint, bool is2D)
        {
            var payload = new ProjectileHitPayload
            {
                ProjId                 = 0,   // physics projectiles have no Rust sim ProjId
                ConfigId               = 0,
                Is3D                   = !is2D,
                TargetId               = targetId,
                Damage                 = damage,
                IsHeadshot             = false,
                IsCrit                 = false,
                HitPosition            = hitPoint,
                OwnerMidId             = _ownerMidId,
                FiredByNetworkObjectId = _firedByNetworkObjectId,
                IsBotOwner             = _isBotOwner,
                WeaponLevel            = _weaponLevel,
            };

            OnHitServerConfirmed?.Invoke(payload);

            MID_Logger.LogDebug(_logLevel,
                $"Physics hit: target={targetId} damage={damage:F1} pos={hitPoint}",
                nameof(PhysicsProjectile));
        }

        #endregion

        #region Helpers

        private void StopPhysics()
        {
            if (_rb3D != null) { _rb3D.velocity = Vector3.zero; _rb3D.isKinematic = true; }
            if (_rb2D != null) { _rb2D.velocity = Vector2.zero; _rb2D.isKinematic = true; }
        }

        #endregion
    }
}
