// packages/com.midmanstudio.projectilesystem/Runtime/Managers/PhysicsProjectileBase.cs
//
// Abstract base for 2D and 3D physics projectiles.
// Derive PhysicsProjectile2D and PhysicsProjectile3D from this.
// Each concrete class adds the matching Rigidbody type and collision callbacks.
//
// This class owns:
//   - Owner context (ownerMidId, weaponLevel, etc.)
//   - Pool visual management (spawn, retry, return)
//   - Visual pool type selection via config.Is3D
//   - OnHitServerConfirmed event
//   - Shared hit/explosion helpers that call FireHitEvent
//   - ShouldAutoSpawnVisual = false (prevents base NetworkProjectileBase double-spawn)
//
// Derived classes own:
//   - GetComponent for their Rigidbody type
//   - OnProjectileInitialised: set velocity on their Rigidbody
//   - Collision callbacks (OnCollisionEnter / OnCollisionEnter2D etc.)
//   - StopPhysics for their Rigidbody type

using System;
using System.Collections;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Network;
using MidManStudio.Projectiles.Visuals;

namespace MidManStudio.Projectiles.Managers
{
    [DisallowMultipleComponent]
    public abstract class PhysicsProjectileBase : NetworkProjectileBase
    {
        #region Inspector

        [Header("Damage")]
        [SerializeField] protected float     _baseDamage      = 30f;
        [SerializeField, Min(0f)] protected float _explosionRadius = 0f;
        [SerializeField] protected LayerMask _damageLayerMask  = -1;

        [Header("Visual Pool")]
        [Tooltip("Config ID used to drive visual appearance.")]
        [SerializeField] protected ushort _visualConfigId = 0;

        [Tooltip("Pool type for 2D visual (SpriteRenderer + trail).")]
        [SerializeField] protected PoolableObjectType _visual2DPoolType
            = PoolableObjectType.Projectile_Visual2D;

        [Tooltip("Pool type for 3D visual (MeshRenderer + trail).")]
        [SerializeField] protected PoolableObjectType _visual3DPoolType
            = PoolableObjectType.Projectile_Visual3D;

        [Header("Debug")]
        [SerializeField] protected MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Events

        public event Action<ProjectileHitPayload> OnHitServerConfirmed;

        #endregion

        #region Shared State

        protected bool  HasHit           { get; set; }

        private ulong _ownerMidId;
        private ulong _firedByNetworkObjectId;
        private bool  _isBotOwner;
        private byte  _weaponLevel;
        private float _damageMultiplier = 1f;

        private GameObject           _poolVisualGO;
        private ProjectileVisualBase _poolVisual;
        private PoolableObjectType   _usedPoolType;
        private Coroutine            _retryCoroutine;

        #endregion

        // ── Prevent base class auto-spawning a visual ─────────────────────────
        protected override bool ShouldAutoSpawnVisual => false;

        #region NGO Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            HasHit = false;
            OnPhysicsSetup();
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

        #region Abstract / Virtual Hooks for Derived Classes

        /// <summary>
        /// Called in OnNetworkSpawn before SpawnPoolVisual.
        /// Derived class should cache its Rigidbody reference here.
        /// </summary>
        protected abstract void OnPhysicsSetup();

        /// <summary>
        /// Called in OnProjectileInitialised after visual setup.
        /// Derived class should configure and launch its Rigidbody here.
        /// Returns the launch direction for visual orientation.
        /// </summary>
        protected abstract Vector3 OnLaunch(float bulletVelocity);

        /// <summary>
        /// Zero velocity and set isKinematic = true on the Rigidbody.
        /// </summary>
        protected abstract void StopPhysics();

        /// <summary>
        /// True if the projectile uses 2D physics (drives pool visual type selection
        /// when no config is registered).
        /// </summary>
        protected abstract bool Is2D { get; }

        #endregion

        #region NetworkProjectileBase Hooks

        protected override void OnNetworkVelocityReceived()
        {
            if (_poolVisual == null) return;
            Vector3 dir = GetLaunchDirection();
            _poolVisual.InitializeClientVisual(
                _visualConfigId, transform.position, dir, BulletVelocity);
        }

        protected override void OnProjectileInitialised()
        {
            HasHit = false;
            if (_poolVisualGO == null) SpawnPoolVisual();

            Vector3 launchDir = OnLaunch(BulletVelocity);

            if (_poolVisual != null)
            {
                float speed = BulletVelocity > 0f ? BulletVelocity : 10f;
                _poolVisual.InitializeClientVisual(
                    _visualConfigId, transform.position, launchDir, speed);
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

        #region Public API

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

        #region Hit Processing (shared, called by derived collision handlers)

        protected void HandleHit3D(GameObject hitGO, Vector3 hitPoint)
        {
            if (HasHit) return;
            HasHit = true;
            StopPhysics();
            if (_explosionRadius > 0.01f) ApplyExplosionDamage3D(hitPoint);
            else ApplyDirectHit(hitGO.GetComponentInParent<NetworkObject>(), hitPoint, false);
            DestroyProjectile();
        }

        protected void HandleHit2D(GameObject hitGO, Vector3 hitPoint)
        {
            if (HasHit) return;
            HasHit = true;
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

        #endregion

        #region Visual Pool (shared)

        private void SpawnPoolVisual()
        {
            if (LocalObjectPool.Instance == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "SpawnPoolVisual: LocalObjectPool.Instance is null.",
                    nameof(PhysicsProjectileBase));
                return;
            }

            // Prefer config.Is3D if registered; fall back to !Is2D
            bool use3DVisual = !Is2D;
            if (ProjectileRegistry.HasInstance)
            {
                var cfg = ProjectileRegistry.Instance.Get(_visualConfigId);
                if (cfg != null) use3DVisual = cfg.Is3D;
            }

            _usedPoolType = use3DVisual ? _visual3DPoolType : _visual2DPoolType;

            Vector3    dir = GetLaunchDirection();
            Quaternion rot = Network.ClientPredictionManager.GetDirectionRotation(dir);

            _poolVisualGO = LocalObjectPool.Instance.GetObject(
                _usedPoolType, transform.position, rot);

            if (_poolVisualGO == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"SpawnPoolVisual: Pool returned null for type {_usedPoolType}. " +
                    "Ensure LocalObjectPool has a prefab assigned for this pool type.",
                    nameof(PhysicsProjectileBase));
                return;
            }

            _poolVisual = _poolVisualGO.GetComponent<ProjectileVisualBase>();
            if (_poolVisual != null)
            {
                float speed = BulletVelocity > 0f ? BulletVelocity : 10f;
                _poolVisual.InitializeClientVisual(
                    _visualConfigId, transform.position, dir, speed);
            }

            _poolVisualGO.transform.SetParent(transform);
            _poolVisualGO.transform.localPosition = Vector3.zero;
            _poolVisualGO.transform.localRotation = Quaternion.identity;
        }

        private IEnumerator RetrySpawnVisual()
        {
            yield return null;
            _retryCoroutine = null;
            if (!IsSpawned || _poolVisualGO != null) yield break;
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

        /// <summary>Returns the expected fire direction for this projectile type.</summary>
        private Vector3 GetLaunchDirection()
            => Is2D ? transform.right : transform.forward;

        #endregion
    }
}
