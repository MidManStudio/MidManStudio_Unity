// packages/com.midmanstudio.projectilesystem/Runtime/Managers/PhysicsProjectile.cs
//
// FIXES:
//   + ShouldAutoSpawnVisual overridden to false — prevents NetworkProjectileBase
//     from spawning a second, unparented pool visual in OnNetworkSpawn. Previously
//     that base visual sat frozen at the spawn point while the physics object moved.
//   + OnNetworkVelocityReceived override — on non-server clients, re-initialises
//     the pool visual with the correct speed and forward direction once the
//     BulletVelocity NetworkVariable arrives. Previously only the server called
//     InitializeClientVisual (inside OnProjectileInitialised), so client visuals
//     showed a default/uninitialized appearance.
//   + SpawnPoolVisual: visual is parented to transform immediately on spawn for
//     ALL clients so it follows the NetworkTransform-synced position.
//   + RetrySpawnVisual: unchanged but now only retries the ONE physics visual.

using System;
using System.Collections;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Data;
using MidManStudio.Projectiles.Network;
using MidManStudio.Projectiles.Visuals;

namespace MidManStudio.Projectiles.Managers
{
    [DisallowMultipleComponent]
    public class PhysicsProjectile : NetworkProjectileBase
    {
        #region Inspector

        [Header("Physics Mode")]
        [SerializeField] protected bool _use2D = false;

        [Header("Physics Settings")]
        [SerializeField] protected float _drag        = 0f;
        [SerializeField] protected float _angularDrag = 0.05f;
        [SerializeField] protected bool  _useGravity  = false;

        [Header("Damage")]
        [SerializeField] protected float     _baseDamage      = 30f;
        [SerializeField, Min(0f)] protected float _explosionRadius = 0f;
        [SerializeField] protected LayerMask _damageLayerMask = -1;

        [Header("Visual Pool")]
        [Tooltip("Config ID for this projectile — used to initialise the pool visual.")]
        [SerializeField] private ushort _visualConfigId = 0;

        [Tooltip("True = spawn 3D pool visual (MeshRenderer + TrailRenderer).\n" +
                 "False = spawn 2D pool visual (SpriteRenderer).")]
        [SerializeField] private bool _use3DVisual = true;

        [SerializeField] private PoolableObjectType _visual2DPoolType
            = PoolableObjectType.Projectile_Visual2D;
        [SerializeField] private PoolableObjectType _visual3DPoolType
            = PoolableObjectType.Projectile_Visual3D;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Events

        public event Action<ProjectileHitPayload> OnHitServerConfirmed;

        #endregion

        #region Private State

        private Rigidbody   _rb3D;
        private Rigidbody2D _rb2D;
        private bool        _hasHit;

        private ulong _ownerMidId;
        private ulong _firedByNetworkObjectId;
        private bool  _isBotOwner;
        private byte  _weaponLevel;
        private float _damageMultiplier = 1f;

        private GameObject           _poolVisualGO;
        private ProjectileVisualBase _poolVisual;
        private PoolableObjectType   _usedPoolType;

        private Coroutine _retryCoroutine;

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        //  Opt out of the base-class auto-visual to prevent the double-spawn bug.
        //  PhysicsProjectile manages its own pool visual entirely.
        // ─────────────────────────────────────────────────────────────────────
        protected override bool ShouldAutoSpawnVisual => false;

        #region NGO Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _hasHit = false;

            if (_use2D) _rb2D = GetComponent<Rigidbody2D>();
            else        _rb3D = GetComponent<Rigidbody>();

            // Spawn visual on ALL clients (server and clients).
            // transform.forward/rotation is valid here — NetworkTransform sends
            // the initial rotation with the same message that triggers OnNetworkSpawn.
            SpawnPoolVisual();

            if (_poolVisualGO == null)
                _retryCoroutine = StartCoroutine(RetrySpawnVisual());
        }

        public override void OnNetworkDespawn()
        {
            if (_retryCoroutine != null)
            {
                StopCoroutine(_retryCoroutine);
                _retryCoroutine = null;
            }
            ReturnPoolVisual();
            base.OnNetworkDespawn();
        }

        #endregion

        #region Client-side visual init hook

        /// <summary>
        /// Called by NetworkProjectileBase on non-server clients when the
        /// BulletVelocity NetworkVariable arrives from the server.
        /// Re-initialises the pool visual with the correct speed and direction.
        /// </summary>
        protected override void OnNetworkVelocityReceived()
        {
            if (_poolVisual == null) return;
            Vector3 dir = transform.forward; // rotation is already synced by NetworkTransform
            _poolVisual.InitializeClientVisual(
                _visualConfigId, transform.position, dir, BulletVelocity);

            MID_Logger.LogDebug(_logLevel,
                $"OnNetworkVelocityReceived: re-init visual configId={_visualConfigId} speed={BulletVelocity}",
                nameof(PhysicsProjectile));
        }

        #endregion

        #region Visual Pool Management

        private void SpawnPoolVisual()
        {
            if (LocalObjectPool.Instance == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "SpawnPoolVisual: LocalObjectPool.Instance is null.",
                    nameof(PhysicsProjectile));
                return;
            }

            _usedPoolType = _use3DVisual ? _visual3DPoolType : _visual2DPoolType;

            Vector3    dir = transform.forward;
            Quaternion rot = Network.ClientPredictionManager.GetDirectionRotation(dir);

            _poolVisualGO = LocalObjectPool.Instance.GetObject(_usedPoolType, transform.position, rot);
            if (_poolVisualGO == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"SpawnPoolVisual: Pool returned null for type {_usedPoolType}.",
                    nameof(PhysicsProjectile));
                return;
            }

            _poolVisual = _poolVisualGO.GetComponent<ProjectileVisualBase>();

            if (_poolVisual != null)
            {
                float speed = BulletVelocity > 0f ? BulletVelocity : 10f;
                _poolVisual.InitializeClientVisual(_visualConfigId, transform.position, dir, speed);
            }

            // Parent immediately on ALL clients so the visual follows the
            // NetworkTransform-synced position without waiting for the server
            // to call OnProjectileInitialised (which is server-only).
            _poolVisualGO.transform.SetParent(transform);
            _poolVisualGO.transform.localPosition = Vector3.zero;
            _poolVisualGO.transform.localRotation = Quaternion.identity;

            MID_Logger.LogDebug(_logLevel,
                $"SpawnPoolVisual OK: type={_usedPoolType} config={_visualConfigId}",
                nameof(PhysicsProjectile));
        }

        private IEnumerator RetrySpawnVisual()
        {
            yield return null;
            _retryCoroutine = null;

            if (!IsSpawned || _poolVisualGO != null) yield break;

            MID_Logger.LogDebug(_logLevel,
                "RetrySpawnVisual: retrying after one frame.",
                nameof(PhysicsProjectile));
            SpawnPoolVisual();
        }

        private void ReturnPoolVisual()
        {
            if (_poolVisualGO == null) return;

            _poolVisualGO.transform.SetParent(null);

            if (_poolVisual != null)
                _poolVisual.ReturnToPoolImmediate();
            else
                LocalObjectPool.Instance?.ReturnObject(_poolVisualGO, _usedPoolType);

            _poolVisualGO = null;
            _poolVisual   = null;
        }

        #endregion

        #region Public API — Owner Context

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

        protected override void OnProjectileInitialised()
        {
            _hasHit = false;

            Vector3 launchDir;

            if (_use2D && _rb2D != null)
            {
                _rb2D.gravityScale = _useGravity ? 1f : 0f;
                _rb2D.drag         = _drag;
                _rb2D.isKinematic  = false;
                _rb2D.velocity     = (Vector2)(transform.right * BulletVelocity);
                launchDir          = transform.right;
            }
            else if (!_use2D && _rb3D != null)
            {
                _rb3D.useGravity  = _useGravity;
                _rb3D.drag        = _drag;
                _rb3D.angularDrag = _angularDrag;
                _rb3D.isKinematic = false;
                _rb3D.velocity    = transform.forward * BulletVelocity;
                launchDir         = transform.forward;
            }
            else
            {
                launchDir = transform.forward;
            }

            // If pool wasn't ready at OnNetworkSpawn, try again now (server only).
            if (_poolVisualGO == null)
                SpawnPoolVisual();

            // Re-initialise visual with correct speed + launch direction on the server.
            // Clients receive this via OnNetworkVelocityReceived instead.
            if (_poolVisual != null)
            {
                float speed = BulletVelocity > 0f ? BulletVelocity : 10f;
                _poolVisual.InitializeClientVisual(
                    _visualConfigId,
                    transform.position,
                    launchDir,
                    speed);
            }
            else if (_poolVisualGO != null)
            {
                Network.ClientPredictionManager.ApplyDirectionRotation(
                    _poolVisualGO.transform, launchDir);
            }
        }

        protected override void OnImpactServer()
        {
            SpawnImpactEffectClientRpc(transform.position);
        }

        protected override void OnCollisionNotifiedClient() { }

        protected override void OnSpawnImpactEffectClient(Vector3 position)
        {
            ReturnPoolVisual();
        }

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
                ? (Vector3)col.contacts[0].point
                : transform.position;
            ProcessHit2D(col.gameObject, pt);
        }

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
            if (_explosionRadius > 0.01f) ApplyExplosionDamage3D(hitPoint);
            else ApplyDirectHit(hitGO.GetComponentInParent<NetworkObject>(), hitPoint, false);
            DestroyProjectile();
        }

        private void ProcessHit2D(GameObject hitGO, Vector3 hitPoint)
        {
            _hasHit = true;
            StopPhysics();
            if (_explosionRadius > 0.01f) ApplyExplosionDamage2D(hitPoint);
            else ApplyDirectHit(hitGO.GetComponentInParent<NetworkObject>(), hitPoint, true);
            DestroyProjectile();
        }

        private void ApplyDirectHit(NetworkObject targetNetObj, Vector3 hitPoint, bool is2D)
        {
            if (targetNetObj == null) return;
            FireHitEvent((uint)targetNetObj.NetworkObjectId,
                _baseDamage * _damageMultiplier, hitPoint, is2D);
        }

        private void ApplyExplosionDamage3D(Vector3 centre)
        {
            var cols  = new Collider[32];
            int count = Physics.OverlapSphereNonAlloc(
                centre, _explosionRadius, cols, _damageLayerMask);
            for (int i = 0; i < count; i++)
            {
                var no = cols[i].GetComponentInParent<NetworkObject>();
                if (no == null) continue;
                float dist    = Vector3.Distance(centre, cols[i].transform.position);
                float falloff = 1f - Mathf.Clamp01(dist / _explosionRadius);
                FireHitEvent((uint)no.NetworkObjectId,
                    _baseDamage * _damageMultiplier * falloff, centre, false);
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
                float dist    = Vector2.Distance(
                    (Vector2)centre, (Vector2)cols[i].transform.position);
                float falloff = 1f - Mathf.Clamp01(dist / _explosionRadius);
                FireHitEvent((uint)no.NetworkObjectId,
                    _baseDamage * _damageMultiplier * falloff, centre, true);
            }
        }

        private void FireHitEvent(uint targetId, float damage, Vector3 hitPoint, bool is2D)
        {
            var payload = new ProjectileHitPayload
            {
                ProjId                 = 0,
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
        }

        private void StopPhysics()
        {
            if (_rb3D != null) { _rb3D.velocity = Vector3.zero; _rb3D.isKinematic = true; }
            if (_rb2D != null) { _rb2D.velocity = Vector2.zero; _rb2D.isKinematic = true; }
        }

        #endregion
    }
}
