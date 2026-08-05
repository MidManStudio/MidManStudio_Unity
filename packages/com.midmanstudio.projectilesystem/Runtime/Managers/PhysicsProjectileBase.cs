using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Core;
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
                 "Set this in the prefab inspector to match your registered ProjectileConfigSO.\n" +
                 "NETWORKED: this is a server-authoritative NetworkVariable now — whatever the " +
                 "server sets (inspector default, or SetVisualConfigId()) is auto-synced to every " +
                 "client. It used to be a plain field, which is why only the host ever showed the " +
                 "correct visual: writing a plain C# field on the server-side NetworkBehaviour " +
                 "instance never reached remote clients at all.")]
        [SerializeField] private NetworkVariable<ushort> n_VisualConfigId
            = new NetworkVariable<ushort>(0,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        /// <summary>Current visual/config id — synced from server to every client.</summary>
        protected ushort VisualConfigId => n_VisualConfigId.Value;

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

        protected ulong _ownerMidId;
        protected ulong _firedByNetworkObjectId;
        protected bool  _isBotOwner;
        protected byte  _weaponLevel;
        protected float _damageMultiplier = 1f;

        private GameObject           _poolVisualGO;
        private ProjectileVisualBase _poolVisual;
        private PoolableObjectType   _usedPoolType;
        private Coroutine            _retryCoroutine;
        private Coroutine            _colliderGrowthCoroutine;

        // Piercing (mirrors RustSimAdapter.HandlePiercing so a physics-based
        // projectile and a raycast/rust-sim one behave identically for the
        // same ProjectileConfigSO). Non-piercing configs (the common case)
        // are unaffected — _collisionsRemaining starts at 1 and any single
        // hit ends the projectile exactly as before this was added.
        private byte _collisionsRemaining = 1;
        private readonly HashSet<uint> _hitTargetIds = new HashSet<uint>();

        #endregion

        protected override bool ShouldAutoSpawnVisual => false;

        #region NGO Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            HasHit = false;

            // Re-apply the visual whenever the server's config id changes —
            // including the very first delta a remote client receives shortly
            // after spawn, if SetVisualConfigId() was called after Spawn() rather
            // than before it (see MID_MasterProjectileSystem.SpawnPhysicsProjectile).
            n_VisualConfigId.OnValueChanged += HandleVisualConfigChanged;

            // SUPERSEDED: this used to reconcile away the firing client's own
            // predicted visual once the real, server-confirmed projectile
            // arrived (IsOwner check + ClientPredictionManager.
            // OnRealPhysicsProjectileSpawned). That was solving the wrong
            // problem — the firing client was never supposed to receive this
            // object at all. MID_MasterProjectileSystem.SpawnPhysicsProjectile
            // now NetworkHides it from the firing client entirely (their local
            // prediction ghost lives out its own MaxLifetime and glides to the
            // confirmed hit point via HitConfirmedClientRpc on its own), so
            // IsOwner is no longer meaningful here and this class no longer
            // needs to do anything owner-specific on spawn.

            OnPhysicsSetup();
            ApplyConfigScale(ResolveVisualConfig());
            SpawnPoolVisual();
            if (_poolVisualGO == null)
                _retryCoroutine = StartCoroutine(RetrySpawnVisual());
        }

        public override void OnNetworkDespawn()
        {
            n_VisualConfigId.OnValueChanged -= HandleVisualConfigChanged;

            if (_retryCoroutine != null)
            {
                StopCoroutine(_retryCoroutine);
                _retryCoroutine = null;
            }

            // GROWTH FIX companion — stop mid-flight collider growth
            // explicitly (same reasoning as ProjectileVisualBase.
            // ReturnToPoolImmediate's equivalent stop) rather than relying
            // solely on the eventual SetActive(false) inside
            // MID_NetworkObjectPool.ReturnNetworkObject to implicitly kill it.
            if (_colliderGrowthCoroutine != null)
            {
                StopCoroutine(_colliderGrowthCoroutine);
                _colliderGrowthCoroutine = null;
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

        /// <summary>
        /// SCALING FIX ("physics-based projectiles do not support scaling"):
        /// this NetworkObject's collider was always whatever fixed size the
        /// prefab happened to be authored with, completely independent of
        /// ProjectileConfigSO.FullSizeX/FullSizeY — every physics projectile
        /// hit-tested at the same size regardless of which config fired it,
        /// even though the cosmetic visual (ProjectileVisual_2D/3D — a
        /// *different*, separately LocalObjectPool-managed GameObject, see
        /// SpawnPoolVisual below) already reads FullSizeX/Y for its own
        /// rendering. Override ApplyColliderSize per-subclass to resize
        /// whichever collider type that subclass actually uses; this method
        /// is the shared, concrete orchestrator that decides WHAT size to
        /// apply and WHEN — subclasses never need to read cfg directly.
        ///
        /// Called:
        ///   • right after OnPhysicsSetup() in OnNetworkSpawn (best-effort
        ///     immediate size, using whatever VisualConfigId already
        ///     resolves to at that point)
        ///   • from SetVisualConfigId() / HandleVisualConfigChanged() —
        ///     self-corrects once the real, server-authoritative config id
        ///     lands, exactly the same pattern already used to refresh the
        ///     pool visual (see those methods' own doc comments for why the
        ///     config isn't always known yet at spawn time)
        ///
        /// cfg may be null (registry not ready yet, or configId not
        /// registered) — no-ops in that case, leaving whatever size was
        /// already applied (or the prefab default, pre-first-call).
        ///
        /// Deliberately NOT implemented by scaling transform.localScale on
        /// this object's own root: the pooled cosmetic visual is parented
        /// under this transform and already applies its OWN
        /// FullSizeX/FullSizeY-based scale independently (unconditionally for
        /// ProjectileVisual_3D; for sprite-path ProjectileVisual_2D as of the
        /// fix landing alongside this one) — scaling the root too would
        /// compound both and double the visible size. Resizing the collider
        /// component directly keeps hit-detection correctly sized without
        /// touching the visual's own scale at all.
        ///
        /// No extra NetworkVariable/RPC needed for the STATIC (non-growth)
        /// case: every peer (server and every client) resolves the same
        /// VisualConfigId to the same shared ProjectileConfigSO project
        /// asset and computes the same size locally — the config data itself
        /// is what's already synced.
        ///
        /// GROWTH ("gets spawned full scale rather than scaling up as
        /// intended"): if cfg.UseScaleGrowth is set, the collider doesn't
        /// jump straight to FullSizeX/Y — it animates from
        /// FullSize*SpawnScaleFraction up to full size over GrowthSpeed,
        /// using the exact same formula RustSim's native tick_scale already
        /// uses (rust_lib/projectile_core/src/simulation.rs):
        /// current += (target - current) * speed * dt. Only ever driven on
        /// the SERVER — this object's Rigidbody/collider only has gameplay
        /// consequence there (see NetworkProjectileBase's IsServer gate
        /// around all the actual physics/launch logic); a non-server peer
        /// just snaps straight to full size, which is harmless since nothing
        /// there ever reads this collider for hit detection.
        /// </summary>
        private void ApplyConfigScale(ProjectileConfigSO cfg)
        {
            if (_colliderGrowthCoroutine != null)
            {
                StopCoroutine(_colliderGrowthCoroutine);
                _colliderGrowthCoroutine = null;
            }

            if (cfg == null) return;

            float targetX = Mathf.Max(cfg.FullSizeX, 0.001f);
            float targetY = Mathf.Max(cfg.FullSizeY, 0.001f);

            if (!cfg.UseScaleGrowth || !IsServer)
            {
                ApplyColliderSize(targetX, targetY);
                return;
            }

            _colliderGrowthCoroutine = StartCoroutine(
                GrowColliderRoutine(targetX, targetY, cfg.SpawnScaleFraction, cfg.GrowthSpeed));
        }

        private IEnumerator GrowColliderRoutine(
            float targetX, float targetY, float spawnFraction, float speed)
        {
            float curX = targetX * spawnFraction;
            float curY = targetY * spawnFraction;
            ApplyColliderSize(curX, curY);

            // Mirrors tick_scale in rust_lib/projectile_core/src/simulation.rs
            // exactly, so physics-projectile growth looks the same as
            // RustSim-driven growth: diff = target - current; if |diff| >
            // 0.001, current += diff * speed * dt.
            while (true)
            {
                float dt    = Time.deltaTime;
                float diffX = targetX - curX;
                float diffY = targetY - curY;
                bool  doneX = Mathf.Abs(diffX) <= 0.001f;
                bool  doneY = Mathf.Abs(diffY) <= 0.001f;
                if (doneX && doneY) break;

                if (!doneX) curX += diffX * speed * dt;
                if (!doneY) curY += diffY * speed * dt;
                ApplyColliderSize(curX, curY);
                yield return null;
            }

            ApplyColliderSize(targetX, targetY);
            _colliderGrowthCoroutine = null;
        }

        /// <summary>
        /// Resizes whichever collider type this subclass actually uses to
        /// the given world-unit dimensions. Called by ApplyConfigScale
        /// above — either once (static size) or repeatedly across several
        /// frames (growth animation) — subclasses don't need to know or
        /// care which case is happening.
        /// </summary>
        protected abstract void ApplyColliderSize(float sizeX, float sizeY);

        /// <summary>
        /// Resolves the ProjectileConfigSO currently referenced by
        /// VisualConfigId, or null if not registered / registry not ready.
        /// Small shared helper — this same two-line lookup was already
        /// repeated in SpawnPoolVisual/ComputeConfigDamage/etc; new
        /// ApplyConfigScale call sites reuse it too rather than adding a
        /// fourth copy.
        /// </summary>
        private ProjectileConfigSO ResolveVisualConfig()
            => ProjectileRegistry.HasInstance ? ProjectileRegistry.Instance.Get(VisualConfigId) : null;

        #endregion

        #region NetworkProjectileBase Hooks

        protected override void OnNetworkVelocityReceived()
        {
            if (_poolVisual == null) return;
            Vector3 dir = GetDefaultLaunchDir();
            _poolVisual.InitializeClientVisual(
                VisualConfigId, transform.position, dir, BulletVelocity);
        }

        protected override void OnProjectileInitialised()
        {
            HasHit = false;
            //  record spawn position so we can compute travel distance for damage falloff
            _spawnPosition = transform.position;

            // Piercing budget — read from the config registered under
            // VisualConfigId, which SetVisualConfigId() must already have been
            // called with by this point (see its own doc comment). Pooled
            // projectiles are reused, so both the hit-target guard and the
            // remaining-collisions counter MUST be reset here every time,
            // not just once in OnNetworkSpawn.
            _hitTargetIds.Clear();
            _collisionsRemaining = 1;
            if (ProjectileRegistry.HasInstance)
            {
                var cfg = ProjectileRegistry.Instance.Get(VisualConfigId);
                if (cfg != null && cfg.PiercingType != ProjectilePiercingType.None)
                    _collisionsRemaining = Math.Max(cfg.MaxCollisions, (byte)1);
            }

            if (_poolVisualGO == null) SpawnPoolVisual();

            Vector3 launchDir = OnLaunch(BulletVelocity);

            if (_poolVisual != null)
            {
                float speed = BulletVelocity > 0f ? BulletVelocity : 10f;
                _poolVisual.InitializeClientVisual(
                    VisualConfigId, transform.position, launchDir, speed);
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
        ///
        /// NETWORK FIX: this now writes a NetworkVariable instead of a plain
        /// field, and only the server may write it — the value automatically
        /// replicates to every client (immediately if called before Spawn(),
        /// or as a delta shortly after if called post-spawn; either way
        /// HandleVisualConfigChanged re-applies the visual when it arrives).
        /// Calling this on a non-server instance is a safe no-op for the
        /// network write, but still refreshes the LOCAL visual if one exists.
        /// </summary>
        public void SetVisualConfigId(ushort configId)
        {
            if (IsServer) n_VisualConfigId.Value = configId;

            if (_poolVisual != null)
            {
                Vector3 dir   = GetDefaultLaunchDir();
                float   speed = BulletVelocity > 0f ? BulletVelocity : 10f;
                _poolVisual.InitializeClientVisual(
                    VisualConfigId, transform.position, dir, speed);
            }

            // SCALING FIX — see ApplyConfigScale's doc comment. Uses
            // VisualConfigId (not the raw configId param) so this reads back
            // whatever the NetworkVariable actually holds — on the server
            // that's the value just written above; on a non-server instance
            // (safe no-op for the network write, per this method's own
            // existing doc comment) it's still whatever the last-synced
            // value is, which is the correct thing to scale against.
            ApplyConfigScale(ResolveVisualConfig());
        }

        #endregion

        #region Hit Processing

        protected void HandleHit3D(GameObject hitGO, Vector3 hitPoint)
        {
            if (!IsServer || HasHit) return;

            var targetNetObj = hitGO.GetComponentInParent<NetworkObject>();
            // Physics can re-trigger a contact against the same collider across
            // consecutive frames (e.g. a slow projectile still overlapping on
            // FixedUpdate) — without this guard a piercing shot could burn
            // multiple collision "charges" on one target instead of one.
            if (targetNetObj != null && !_hitTargetIds.Add((uint)targetNetObj.NetworkObjectId))
                return;

            bool destroyNow = ResolvePiercingAndDamage(targetNetObj, hitPoint, false, hitGO);
            if (!destroyNow) return;

            HasHit = true;
            StopPhysics();
            DestroyProjectile();
        }

        protected void HandleHit2D(GameObject hitGO, Vector3 hitPoint)
        {
            if (!IsServer || HasHit) return;

            var targetNetObj = hitGO.GetComponentInParent<NetworkObject>();
            if (targetNetObj != null && !_hitTargetIds.Add((uint)targetNetObj.NetworkObjectId))
                return;

            bool destroyNow = ResolvePiercingAndDamage(targetNetObj, hitPoint, true, hitGO);
            if (!destroyNow) return;

            HasHit = true;
            StopPhysics();
            DestroyProjectile();
        }

        /// <summary>
        /// Applies damage for one contact and returns whether the projectile has
        /// exhausted its piercing budget (or has no piercing at all) and should
        /// therefore stop and be destroyed. Mirrors
        /// <see cref="RustSimAdapter"/>'s HandlePiercing so a physics-based
        /// projectile and a raycast/rust-sim one behave identically for the
        /// same <see cref="ProjectileConfigSO"/>.
        ///
        /// Explosion-radius configs always end the projectile on first contact
        /// — an AoE blast and multi-target piercing aren't meant to combine;
        /// author one or the other on a given ProjectileConfigSO, not both.
        /// </summary>
        private bool ResolvePiercingAndDamage(
            NetworkObject targetNetObj, Vector3 hitPoint, bool is2D, GameObject hitGO)
        {
            if (_explosionRadius > 0.01f)
            {
                if (is2D) ApplyExplosionDamage2D(hitPoint);
                else      ApplyExplosionDamage3D(hitPoint);
                return true;
            }

            if (targetNetObj == null)
            {
                // Hit something with no NetworkObject — environment/terrain.
                // Piercing lets a shot punch through damageable targets, not
                // walls; always stop here regardless of remaining budget.
                // (Matches prior behaviour: ApplyDirectHit(null, ...) used to
                // no-op and DestroyProjectile() ran unconditionally anyway.)
                return true;
            }

            ApplyDirectHit(targetNetObj, hitPoint, is2D, hitGO);

            var pierceType = ProjectilePiercingType.None;
            if (ProjectileRegistry.HasInstance)
            {
                var cfg = ProjectileRegistry.Instance.Get(VisualConfigId);
                if (cfg != null) pierceType = cfg.PiercingType;
            }

            if (pierceType == ProjectilePiercingType.None) return true;

            _collisionsRemaining--;
            return _collisionsRemaining <= 0;
        }

        private void ApplyDirectHit(
            NetworkObject targetNetObj, Vector3 hitPoint, bool is2D, GameObject hitGO)
        {
            if (targetNetObj == null) return;
            float damage = ComputeConfigDamage(hitPoint);
            FireHitEvent(
                (uint)targetNetObj.NetworkObjectId,
                damage,
                hitPoint, is2D, hitGO);
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
                FireHitEvent((uint)no.NetworkObjectId, damage, centre, false, cols[i].gameObject);
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
                FireHitEvent((uint)no.NetworkObjectId, damage, centre, true, cols[i].gameObject);
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
                var cfg = ProjectileRegistry.Instance.Get(VisualConfigId);
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
            uint targetId, float damage, Vector3 hitPoint, bool is2D, GameObject hitGO = null)
        {
            OnHitServerConfirmed?.Invoke(new ProjectileHitPayload
            {
                ProjId                 = 0,
                ConfigId               = VisualConfigId,
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
                DamageMultiplier        = _damageMultiplier,
                HitObject              = hitGO,
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
                var cfg = ProjectileRegistry.Instance.Get(VisualConfigId);
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

            var poolReturn = _poolVisualGO.GetComponent<LocalPoolReturn>();
            poolReturn?.SetAutoReturn(false);

            _poolVisual = _poolVisualGO.GetComponent<ProjectileVisualBase>();
            if (_poolVisual != null)
            {
                float speed = BulletVelocity > 0f ? BulletVelocity : 10f;
                _poolVisual.InitializeClientVisual(
                    VisualConfigId, transform.position, dir, speed);
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

        private void HandleVisualConfigChanged(ushort oldId, ushort newId)
        {
            // SCALING FIX — see ApplyConfigScale's doc comment. Runs even if
            // _poolVisual isn't ready yet (unlike the visual refresh below,
            // which needs it) since the collider is independent of the pool
            // visual entirely.
            ApplyConfigScale(ResolveVisualConfig());

            if (_poolVisual == null) return;
            Vector3 dir   = GetDefaultLaunchDir();
            float   speed = BulletVelocity > 0f ? BulletVelocity : 10f;
            _poolVisual.InitializeClientVisual(newId, transform.position, dir, speed);
        }

        private Vector3 GetDefaultLaunchDir()
            => Is2D ? transform.right : transform.forward;

        #endregion
    }
}
