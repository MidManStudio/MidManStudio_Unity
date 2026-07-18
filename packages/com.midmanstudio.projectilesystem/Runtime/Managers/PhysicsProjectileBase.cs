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
using MidManStudio.Netcode.Pools;
namespace MidManStudio.Projectiles.Managers
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public abstract class PhysicsProjectileBase : NetworkProjectileBase
    {
        #region Inspector

        [Header("Damage")]
        [Tooltip("Base damage used when no config is registered for _visualConfigId.")]
        [SerializeField] protected float     _baseDamage       = 30f;
        [SerializeField, Min(0f)] protected float _explosionRadius = 0f;
        [SerializeField] protected LayerMask _damageLayerMask   = -1;

        [Header("Visual Pool")]
        [Tooltip("Config ID used to drive visual appearance AND damage curve.\n" +
                 "Set this in the prefab inspector to match your registered ProjectileConfigSO.")]
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

        protected bool HasHit { get; set; }

        // Recorded at launch for travel-distance damage falloff
        private Vector3 _spawnPosition;

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

        #region Abstract / Virtual Hooks

        protected abstract void OnPhysicsSetup();
        protected abstract Vector3 OnLaunch(float bulletVelocity);
        protected abstract void StopPhysics();
        protected abstract bool Is2D { get; }

        #endregion

        #region NetworkProjectileBase Hooks

        protected override void OnNetworkVelocityReceived()
        {
            if (_poolVisual == null) return;
            Vector3 dir = GetDefaultLaunchDir();
            _poolVisual.InitializeClientVisual(
                _visualConfigId, transform.position, dir, BulletVelocity);
        }

        protected override void OnProjectileInitialised()
        {
            HasHit = false;
            // FIX: record spawn position so we can compute travel distance for damage falloff
            _spawnPosition = transform.position;

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
            => SpawnImpactEffectClientRpc(transform.position);

        protected override void OnCollisionNotifiedClient() { }

        protected override void OnSpawnImpactEffectClient(Vector3 position)
            => ReturnPoolVisual();

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

        /// <summary>
        /// BUG FIX: _visualConfigId was previously only ever set from the
        /// Inspector default on the prefab (0) — nothing in the fire pipeline
        /// ever told a spawned instance which config it was actually
        /// representing, so every instance of a given physics prefab used
        /// whatever ProjectileConfigSO happened to be registered under id 0
        /// (or none, falling back to a placeholder sprite) regardless of what
        /// was actually fired. Call this from SpawnPhysicsProjectile — or
        /// directly — before/at spawn so the visual matches the real config.
        /// Safe to call after the visual has already been spawned too; it
        /// re-initialises it against the new config immediately.
        /// </summary>
        public void SetVisualConfigId(ushort configId)
        {
            _visualConfigId = configId;

            if (_poolVisual != null)
            {
                Vector3 dir   = GetDefaultLaunchDir();
                float   speed = BulletVelocity > 0f ? BulletVelocity : 10f;
                _poolVisual.InitializeClientVisual(
                    _visualConfigId, transform.position, dir, speed);
            }
        }

        #endregion

        #region Hit Processing

        protected void HandleHit3D(GameObject hitGO, Vector3 hitPoint)
        {
            if (!IsServer || HasHit) return;
            HasHit = true;
            StopPhysics();
            if (_explosionRadius > 0.01f) ApplyExplosionDamage3D(hitPoint);
            else ApplyDirectHit(
                hitGO.GetComponentInParent<NetworkObject>(), hitPoint, false);
            DestroyProjectile();
        }

        protected void HandleHit2D(GameObject hitGO, Vector3 hitPoint)
        {
            if (!IsServer || HasHit) return;
            HasHit = true;
            StopPhysics();
            if (_explosionRadius > 0.01f) ApplyExplosionDamage2D(hitPoint);
            else ApplyDirectHit(
                hitGO.GetComponentInParent<NetworkObject>(), hitPoint, true);
            DestroyProjectile();
        }

        private void ApplyDirectHit(
            NetworkObject targetNetObj, Vector3 hitPoint, bool is2D)
        {
            if (targetNetObj == null) return;
            float damage = ComputeConfigDamage(hitPoint);
            FireHitEvent(
                (uint)targetNetObj.NetworkObjectId,
                damage,
                hitPoint, is2D);
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
                float damage  = ComputeConfigDamage(cols[i].transform.position) * falloff;
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
                float damage  = ComputeConfigDamage(cols[i].transform.position) * falloff;
                FireHitEvent((uint)no.NetworkObjectId, damage, centre, true);
            }
        }

        /// <summary>
        /// Evaluates damage from the ProjectileConfigSO using travel distance + damage curve.
        /// Falls back to _baseDamage when no config is registered for _visualConfigId.
        /// </summary>
        private float ComputeConfigDamage(Vector3 hitPoint)
        {
            if (ProjectileRegistry.HasInstance)
            {
                var cfg = ProjectileRegistry.Instance.Get(_visualConfigId);
                if (cfg != null)
                {
                    float travelDist = Vector3.Distance(_spawnPosition, hitPoint);
                    float normDist   = cfg.MaxRange > 0f
                        ? Mathf.Clamp01(travelDist / cfg.MaxRange) : 0f;
                    float damage = cfg.EvaluateDamage(normDist);

                    bool isCrit = UnityEngine.Random.value < cfg.CritChance;
                    if (isCrit) damage *= cfg.CritMultiplier;

                    return damage * _damageMultiplier;
                }
            }
            // Fallback: inspector _baseDamage
            return _baseDamage * _damageMultiplier;
        }

        private void FireHitEvent(
            uint targetId, float damage, Vector3 hitPoint, bool is2D)
        {
            OnHitServerConfirmed?.Invoke(new ProjectileHitPayload
            {
                ProjId                 = 0,
                ConfigId               = _visualConfigId,
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
            });
        }

        #endregion

        #region Visual Pool

        private void SpawnPoolVisual()
        {
            if (LocalObjectPool.Instance == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "SpawnPoolVisual: LocalObjectPool.Instance is null.",
                    nameof(PhysicsProjectileBase));
                return;
            }

            bool use3DVisual = !Is2D;
            if (ProjectileRegistry.HasInstance)
            {
                var cfg = ProjectileRegistry.Instance.Get(_visualConfigId);
                if (cfg != null) use3DVisual = cfg.Is3D;
            }

            _usedPoolType = use3DVisual ? _visual3DPoolType : _visual2DPoolType;

            Vector3    dir = GetDefaultLaunchDir();
            Quaternion rot = Network.ClientPredictionManager.GetDirectionRotation(dir);

            _poolVisualGO = LocalObjectPool.Instance.GetObject(
                _usedPoolType, transform.position, rot);

            if (_poolVisualGO == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"SpawnPoolVisual: LocalObjectPool returned null for type {_usedPoolType}. " +
                    "Ensure the pool has a prefab assigned for this type.",
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
            MID_Logger.LogDebug(_logLevel,
                "RetrySpawnVisual: pool wasn't ready, retrying.",
                nameof(PhysicsProjectileBase));
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
      
        private Vector3 GetDefaultLaunchDir()
            => Is2D ? transform.right : transform.forward;

        #endregion
    }
}
