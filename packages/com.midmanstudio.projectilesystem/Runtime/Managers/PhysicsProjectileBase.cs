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

        [Tooltip("EDITOR-ONLY BOOKKEEPING — not read by any runtime code. n_VisualConfigId " +
                 "above (a ushort) is the actual live value, and IDs are assigned dynamically " +
                 "by ProjectileRegistry at runtime — session-stable, not something you can " +
                 "look up from a fixed number at edit time. Drag the ProjectileConfigSO this " +
                 "prefab is meant to represent in here purely so you can SEE it at a glance " +
                 "in the Inspector instead of squinting at a raw ushort. If your spawn flow " +
                 "always calls SetVisualConfigId() with the real id anyway, this is just a " +
                 "label; it has zero effect on behaviour.")]
        [SerializeField] private ProjectileConfigSO _configReferenceForEditorOnly;

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
        private bool    _launched;
        private Coroutine _configRevalidateCoroutine;

        // ── Wave/Circular movement ("it should offset the physics body since
        // the visual is meant to follow the physics body") — see
        // SetupMovementType's doc comment below for the full explanation.
        private ProjectileMovementType _movementType = ProjectileMovementType.Straight;
        private Vector3                _movementLaunchVelocity;
        private Vector3                _movementPerpAxis;
        private float                  _movementStartServerTime;

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
            RefreshConfigDependentState(ResolveVisualConfig());
            SpawnPoolVisual();
            if (_poolVisualGO == null)
                _retryCoroutine = StartCoroutine(RetrySpawnVisual());

            _configRevalidateCoroutine = StartCoroutine(RevalidateConfigAfterSpawn());
        }

        public override void OnNetworkDespawn()
        {
            n_VisualConfigId.OnValueChanged -= HandleVisualConfigChanged;

            if (_retryCoroutine != null)
            {
                StopCoroutine(_retryCoroutine);
                _retryCoroutine = null;
            }

            if (_configRevalidateCoroutine != null)
            {
                StopCoroutine(_configRevalidateCoroutine);
                _configRevalidateCoroutine = null;
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
            _launched = false;
            base.OnNetworkDespawn();
        }

        /// <summary>
        /// MAXRANGE FIX ("check the whole config... this is more serious than
        /// we thought"): MaxRange was only ever consumed by ComputeConfigDamage
        /// for damage-falloff normalisation — nothing actually stopped a
        /// physics projectile once it traveled past that distance, unlike a
        /// raycast (inherently bounded by its own max distance) or a RustSim
        /// projectile (native-side max-range cutoff). A physics projectile
        /// with low/no drag and gravity disabled could travel indefinitely
        /// past its configured range, still fully "alive" until TimeToLive
        /// (see ApplyConfigLifetime) eventually catches it.
        ///
        /// Further-overrides NetworkProjectileBase.Update() (not sealed) —
        /// base.Update() still runs first, so the existing TTL watchdog and
        /// NetworkTransform interpolation are both unaffected; this just adds
        /// an additional server-only distance check on top.
        /// </summary>
        protected override void Update()
        {
            base.Update();

            if (!IsServer || !_launched || HasHit) return;

            var cfg = ResolveVisualConfig();
            if (cfg == null || cfg.MaxRange <= 0f) return;

            float travelDist = Vector3.Distance(_spawnPosition, transform.position);
            if (travelDist >= cfg.MaxRange)
            {
                HasHit = true;
                StopPhysics();
                DestroyProjectile();
            }
        }

        /// <summary>
        /// WAVE/CIRCULAR FIX ("physics projectiles' movement types don't work
        /// — it should offset the physics body since the visual is meant to
        /// follow the physics body"): every FixedUpdate, sets the Rigidbody's
        /// velocity directly to DeterministicMotionMath's closed-form output
        /// for the elapsed time since launch — see SetupMovementType's doc
        /// comment for the full explanation of why that's the correct/safe
        /// thing to reuse here. A real Rigidbody, still integrating a real
        /// velocity every physics step, so collision detection is completely
        /// unaffected — only the velocity itself now follows the curve
        /// instead of staying constant.
        ///
        /// Only ever runs on the server (the only place this Rigidbody is
        /// physically authoritative — see the IsServer-gated logic
        /// throughout this class), and only once launched and not yet hit —
        /// same gating Update()'s MaxRange check above uses.
        /// </summary>
        protected virtual void FixedUpdate()
        {
            if (!IsServer || !_launched || HasHit) return;
            if (_movementType != ProjectileMovementType.Wave &&
                _movementType != ProjectileMovementType.Circular)
                return;

            var cfg = ResolveVisualConfig();
            if (cfg == null) return;

            float timeAlive = NetworkManager.ServerTime.TimeAsFloat - _movementStartServerTime;
            Vector3 velocity;

            if (_movementType == ProjectileMovementType.Wave)
            {
                velocity = Is2D
                    ? DeterministicMotionMath.CalculateWave2DVelocityDirection(
                        _movementLaunchVelocity.x, _movementLaunchVelocity.y,
                        _movementLaunchVelocity.magnitude,
                        cfg.WaveAmplitude, cfg.WaveFrequency, cfg.WavePhaseOffset,
                        _movementPerpAxis.x, _movementPerpAxis.y, timeAlive)
                    : DeterministicMotionMath.CalculateWave3DVelocityDirection(
                        _movementLaunchVelocity, cfg.WaveAmplitude, cfg.WaveFrequency,
                        cfg.WavePhaseOffset, _movementPerpAxis, timeAlive);
            }
            else // Circular
            {
                float omegaRad = cfg.CircularAngularSpeed * Mathf.Deg2Rad;
                float startRad = cfg.CircularStartAngle   * Mathf.Deg2Rad;

                velocity = Is2D
                    ? DeterministicMotionMath.CalculateCircular2DVelocityDirection(
                        _movementLaunchVelocity.x, _movementLaunchVelocity.y,
                        omegaRad, startRad, timeAlive)
                    : DeterministicMotionMath.CalculateCircular3DVelocityDirection(
                        _movementLaunchVelocity, omegaRad, startRad,
                        _movementPerpAxis, cfg.CircularRadius, timeAlive);
            }

            ApplyMovementVelocity(velocity);
        }

        /// <summary>
        /// Captures the state FixedUpdate's movement-type driver needs, once,
        /// right after launch. Called from OnProjectileInitialised.
        ///
        /// _movementLaunchVelocity is the exact velocity OnLaunch just
        /// assigned (direction * bulletVelocity) — captured here from
        /// launchDir + BulletVelocity rather than reading it back off the
        /// Rigidbody, so this works identically for 2D/3D without the base
        /// class needing Rigidbody access at all.
        ///
        /// _movementPerpAxis uses DeterministicMotionMath.ComputePerpAxis2D/3D
        /// — verified (by that file's own header contract) to produce the
        /// same axis BatchSpawnHelper.GetAccel2D/3D computes for a
        /// RustSim-driven projectile with the same config, so a physics and a
        /// RustSim projectile firing the same Wave/Circular config curve the
        /// same way.
        ///
        /// Non-Wave/Circular configs return immediately — FixedUpdate's own
        /// _movementType check makes the rest a no-op either way, but this
        /// also avoids computing an axis nobody will read.
        /// </summary>
        private void SetupMovementType(Vector3 launchDir)
        {
            var cfg = ResolveVisualConfig();
            _movementType = cfg != null ? cfg.MovementType : ProjectileMovementType.Straight;

            if (_movementType != ProjectileMovementType.Wave &&
                _movementType != ProjectileMovementType.Circular)
                return;

            _movementLaunchVelocity  = launchDir.normalized * BulletVelocity;
            _movementStartServerTime = NetworkManager.ServerTime.TimeAsFloat;
            _movementPerpAxis        = Is2D
                ? DeterministicMotionMath.ComputePerpAxis2D(launchDir)
                : DeterministicMotionMath.ComputePerpAxis3D(launchDir);
        }

        #endregion

        #region Abstract / Virtual Hooks

        protected abstract void OnPhysicsSetup();
        protected abstract Vector3 OnLaunch(float bulletVelocity);
        protected abstract void StopPhysics();
        protected abstract bool Is2D { get; }

        /// <summary>
        /// Refreshes every piece of per-instance state that depends on the
        /// resolved ProjectileConfigSO. Called from the three places
        /// VisualConfigId is known/changes (OnNetworkSpawn, SetVisualConfigId,
        /// HandleVisualConfigChanged) — centralised here so each new
        /// config-driven fix only needs one call site added to those three
        /// methods instead of one per fix.
        /// </summary>
        private void RefreshConfigDependentState(ProjectileConfigSO cfg)
        {
            ApplyConfigScale(cfg);
            ApplyConfigLifetime(cfg);
            ApplyConfigHitLayers(cfg);
        }

        /// <summary>
        /// LIFETIME FIX ("lifetime doesn't get applied to physics
        /// projectiles"): TimeToLive (NetworkProjectileBase, inherited) was
        /// always just whatever fixed value the prefab's Inspector happened to
        /// have (8f unless overridden per-prefab) — completely decoupled from
        /// ProjectileConfigSO.Lifetime. Confirmed by grep: TimeToLive is
        /// referenced nowhere else in this package, for ANY projectile type —
        /// NetworkProjectileBase itself doesn't know ProjectileConfigSO exists
        /// at all (deliberately generic), so nothing was ever bridging the two.
        ///
        /// The actual TTL enforcement (NetworkProjectileBase.Update()'s
        /// NetworkManager.ServerTime.TimeAsFloat >= _endOfLifeTime check) was
        /// already correct and already runs for physics projectiles — it was
        /// just measuring against the wrong duration.
        ///
        /// This must run BEFORE InitialiseProjectile() is called, since that
        /// method reads TimeToLive exactly once to compute _endOfLifeTime.
        /// SetVisualConfigId() is already documented/established as being
        /// called before Spawn() by the normal SpawnPhysicsProjectile flow,
        /// and InitialiseProjectile() (the actual "launch") only ever happens
        /// after that — so this ordering already holds without further changes.
        /// </summary>
        private void ApplyConfigLifetime(ProjectileConfigSO cfg)
        {
            if (cfg != null) TimeToLive = cfg.Lifetime;
        }

        /// <summary>
        /// HITLAYERS FIX, explosion-damage half: ApplyExplosionDamage2D/3D
        /// already correctly pass _damageLayerMask into
        /// OverlapCircleNonAlloc/OverlapSphereNonAlloc — that part was never
        /// broken. It just wasn't synced from cfg.HitLayers anywhere, so an
        /// AoE config's HitLayers setting was silently ignored in favour of
        /// whatever _damageLayerMask happened to default to on the prefab
        /// (-1 / everything). See PassesConfigLayerMask for the equivalent
        /// fix on the direct-hit path, which needed an explicit new check
        /// rather than just a sync since it had no layer filtering at all.
        /// </summary>
        private void ApplyConfigHitLayers(ProjectileConfigSO cfg)
        {
            if (cfg != null) _damageLayerMask = cfg.HitLayers;
        }

        /// <summary>
        /// SPEED FIX ("physics projectiles don't abide by the speed of the
        /// config at all"): confirmed by grep across this entire package —
        /// cfg.ResolveSpeed() (== MinSpeed/MaxSpeed) is called exactly ONCE,
        /// in MID_MasterProjectileSystem.FireNetworkedSim for the
        /// raycast/RustSim path. SpawnPhysicsProjectile never calls
        /// InitialiseProjectile at all — launching a physics projectile is a
        /// separate step the calling weapon script does itself, passing its
        /// own bulletVelocity. There was no bridge from cfg's speed fields
        /// into that value anywhere in this package.
        ///
        /// ORIGINAL FIX brought this in line with FireNetworkedSim's own
        /// convention: `bulletVelocity <= 0 ? cfg.ResolveSpeed() : bulletVelocity`
        /// — treating "<= 0" as "caller didn't specify". CONFIRMED (by testing)
        /// this doesn't fix it: the calling weapon script
        /// (NetworkedDimensionPlayer.FirePhysics — lives in the game project,
        /// not this shared package, so I still can't see it directly) is
        /// evidently always passing SOME non-zero bulletVelocity of its own —
        /// just not one sourced from the config — so the <= 0f branch never
        /// triggers at all, and cfg.ResolveSpeed() never actually gets
        /// consulted.
        ///
        /// FIX: cfg.ResolveSpeed() now takes priority by default whenever a
        /// config resolves, full stop — matching what testing confirmed is
        /// actually needed. _allowCallerVelocityOverride (Inspector, defaults
        /// false) exists for the legitimate opposite case — a charged/power
        /// shot or velocity-randomization weapon that DELIBERATELY wants to
        /// override config speed per-shot — flip it per-prefab if you have a
        /// weapon type that needs that. With it false (default), whatever the
        /// caller passes is ONLY used as a fallback when no config resolves
        /// (VisualConfigId not yet registered) — same as before.
        /// </summary>
        [Header("Speed")]
        [Tooltip("Default OFF: cfg.ResolveSpeed() always wins over whatever bulletVelocity " +
                 "the calling weapon script passes, whenever VisualConfigId resolves to a " +
                 "real config. Turn ON for a specific prefab if that weapon deliberately " +
                 "needs to override config speed per-shot (charged shots, spread/randomised " +
                 "velocity, etc.) — in that case the caller's value always wins instead, and " +
                 "cfg.ResolveSpeed() is only used as a fallback when bulletVelocity <= 0.")]
        [SerializeField] protected bool _allowCallerVelocityOverride = false;

        public override void InitialiseProjectile(
            ulong ownerMidId, ulong firedByNetworkObjectId, float bulletVelocity,
            bool isBotOwned = false, byte weaponLevel = 0,
            bool serverIsActualOwner = false, bool enableVisualSynch = true)
        {
            var cfg = ResolveVisualConfig();

            if (_allowCallerVelocityOverride)
            {
                // Old behaviour: caller's value wins whenever it's a real (> 0) speed;
                // config is only the fallback when the caller didn't specify one.
                if (bulletVelocity <= 0f && cfg != null)
                    bulletVelocity = cfg.ResolveSpeed();
            }
            else if (cfg != null)
            {
                // New default: config always wins when one resolves, regardless of
                // what the caller passed — this is the branch that actually fixes
                // the confirmed bug.
                bulletVelocity = cfg.ResolveSpeed();
            }
            // else: no config resolved yet (VisualConfigId not registered) — fall
            // through with whatever the caller passed, same safety net as before.

            base.InitialiseProjectile(
                ownerMidId, firedByNetworkObjectId, bulletVelocity,
                isBotOwned, weaponLevel, serverIsActualOwner, enableVisualSynch);
        }

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
        /// Applies a computed velocity vector directly to whichever
        /// Rigidbody type this subclass uses. Used by FixedUpdate's
        /// Wave/Circular movement driver — see that method and
        /// SetupMovementType for the full explanation.
        /// </summary>
        protected abstract void ApplyMovementVelocity(Vector3 velocity);

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
            HasHit    = false;
            _launched = true;
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
            SetupMovementType(launchDir);

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

        /// <summary>
        /// IMPACT VFX FIX ("check the whole config... this is more serious
        /// than we thought"): confirmed by grep — cfg.ImpactEffectType was
        /// never referenced anywhere in this file. This method used to be
        /// just `=> ReturnPoolVisual();` — physics projectiles spawned NO
        /// impact effect at all, regardless of what ImpactEffectType was set
        /// to on the config. ProjectileImpactHandler.PlayImpact already
        /// exists and is the exact mechanism MID_ProjectileNetworkBridge
        /// already calls for the raycast/RustSim path's impact VFX (per that
        /// class's own header comment: "MID_ProjectileNetworkBridge calls
        /// PlayImpact() on HitConfirmedClientRpc") — this wires physics
        /// projectiles into the same call.
        ///
        /// SCOPE NOTE: PlayImpact takes an optional isHeadshot flag for a
        /// headshot-specific VFX variant, which I'm deliberately NOT
        /// threading through here — doing so would mean changing
        /// NetworkProjectileBase.OnSpawnImpactEffectClient's signature
        /// (adding a parameter), which is a protected virtual method any
        /// external game-specific subclass could already be overriding.
        /// Changing its signature would silently break any such override
        /// (parameter lists must match exactly for C# override resolution —
        /// a default value doesn't help there) rather than just extend it,
        /// and I have no visibility into whether anything outside this
        /// package does that. Say if you want that threaded through too and
        /// I'll do it properly with the signature change called out
        /// explicitly rather than sneaking it in.
        /// </summary>
        protected override void OnSpawnImpactEffectClient(Vector3 position)
        {
            if (ProjectileImpactHandler.HasInstance)
                ProjectileImpactHandler.Instance.PlayImpact(position, VisualConfigId);

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

            // SCALING FIX / LIFETIME FIX — see ApplyConfigScale/ApplyConfigLifetime
            // doc comments. Uses VisualConfigId (not the raw configId param) so
            // this reads back whatever the NetworkVariable actually holds — on
            // the server that's the value just written above; on a non-server
            // instance (safe no-op for the network write, per this method's own
            // existing doc comment) it's still whatever the last-synced value
            // is, which is the correct thing to apply.
            RefreshConfigDependentState(ResolveVisualConfig());
        }

        #endregion

        #region Hit Processing

        protected void HandleHit3D(GameObject hitGO, Vector3 hitPoint)
        {
            if (!IsServer || HasHit) return;
            if (!PassesConfigLayerMask(hitGO.layer)) return;

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
            if (!PassesConfigLayerMask(hitGO.layer)) return;

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
        /// HITLAYERS FIX ("check the whole config, this is more serious than
        /// we thought"): confirmed by grep — cfg.HitLayers was referenced
        /// nowhere in this file at all. Direct physics hits (this method) hit
        /// ANYTHING with a collider, completely ignoring HitLayers — the
        /// tooltip on that field explicitly says "Exclude the 'Player' layer
        /// to prevent friendly-fire or self-damage from pattern projectiles",
        /// none of which was actually happening for physics projectiles.
        ///
        /// Mirrors LocalProjectileManager.PassesLayerMask's exact established
        /// pattern (mask == -1 means "everything", matching HitLayers'
        /// documented default) rather than inventing a new check — same
        /// mask == -1 short-circuit, same bitwise membership test.
        ///
        /// NOTE: this only filters DAMAGE/hit-registration, matching the
        /// tooltip's stated intent. The projectile still physically collides
        /// (bounces, stops, etc.) with excluded-layer colliders exactly as
        /// before — if you also want it to pass through them with no physical
        /// response at all, that's a separate, deeper fix (Physics2D/
        /// Physics.IgnoreCollision per-instance, or Trigger colliders +
        /// manual movement) — say if you want that too.
        /// </summary>
        private bool PassesConfigLayerMask(int hitLayer)
        {
            var cfg = ResolveVisualConfig();
            if (cfg == null) return true;
            int mask = cfg.HitLayers.value;
            return mask == -1 || (mask & (1 << hitLayer)) != 0;
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
            float damage = ComputeConfigDamage(hitPoint, hitGO, out bool isCrit, out bool isHeadshot);
            FireHitEvent(
                (uint)targetNetObj.NetworkObjectId,
                damage, isCrit, isHeadshot,
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
                float damage  = ComputeConfigDamage(
                    cols[i].transform.position, cols[i].gameObject,
                    out bool isCrit, out bool isHeadshot) * falloff;
                FireHitEvent(
                    (uint)no.NetworkObjectId, damage, isCrit, isHeadshot,
                    centre, false, cols[i].gameObject);
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
                float damage  = ComputeConfigDamage(
                    cols[i].transform.position, cols[i].gameObject,
                    out bool isCrit, out bool isHeadshot) * falloff;
                FireHitEvent(
                    (uint)no.NetworkObjectId, damage, isCrit, isHeadshot,
                    centre, true, cols[i].gameObject);
            }
        }

        /// <summary>
        /// Evaluates damage from the ProjectileConfigSO using travel distance +
        /// damage curve. Falls back to _baseDamage when no config is
        /// registered for VisualConfigId.
        ///
        /// CRIT FIX ("check the whole config, this is more serious than we
        /// thought"): isCrit was already being computed right here, but the
        /// caller (FireHitEvent) hardcoded IsCrit = false in the payload
        /// instead of ever reading it — the computed value was silently
        /// thrown away every time. Now returned via out param and threaded
        /// through by both callers below.
        ///
        /// HEADSHOT SCAFFOLDING: cfg.HeadshotMultiplier was never referenced
        /// anywhere in this file at all. LocalProjectileManager.
        /// CheckHeadshotLocal (the equivalent hook for the offline/local
        /// path) defaults to `=> false` and is meant to be overridden by a
        /// game-specific subclass with real hitbox/head-zone knowledge this
        /// shared package doesn't have — CheckHeadshotPhysics below mirrors
        /// that exact pattern for the physics path. Wire it up in a
        /// game-specific PhysicsProjectileBase subclass (or edit it directly
        /// here if you'd rather keep it in-package) once you've got a way to
        /// tell head hitboxes apart from body ones — I didn't want to guess
        /// at that logic.
        /// </summary>
        private float ComputeConfigDamage(
            Vector3 hitPoint, GameObject hitGO, out bool isCrit, out bool isHeadshot)
        {
            isCrit     = false;
            isHeadshot = false;

            if (ProjectileRegistry.HasInstance)
            {
                var cfg = ProjectileRegistry.Instance.Get(VisualConfigId);
                if (cfg != null)
                {
                    float travelDist = Vector3.Distance(_spawnPosition, hitPoint);
                    float normDist   = cfg.MaxRange > 0f
                        ? Mathf.Clamp01(travelDist / cfg.MaxRange) : 0f;
                    float damage = cfg.EvaluateDamage(normDist);

                    isHeadshot = CheckHeadshotPhysics(hitGO, hitPoint);
                    if (isHeadshot) damage *= cfg.HeadshotMultiplier;

                    isCrit = UnityEngine.Random.value < cfg.CritChance;
                    if (isCrit) damage *= cfg.CritMultiplier;

                    return damage * _damageMultiplier;
                }
            }
            // Fallback: inspector _baseDamage
            return _baseDamage * _damageMultiplier;
        }

        /// <summary>
        /// Headshot scaffolding — see ComputeConfigDamage's doc comment.
        /// Defaults to false (no headshots), matching
        /// LocalProjectileManager.CheckHeadshotLocal's own default. Override
        /// in a game-specific subclass once head-hitbox detection is
        /// available (e.g. checking hitGO's tag/layer/a HitboxType
        /// component).
        /// </summary>
        protected virtual bool CheckHeadshotPhysics(GameObject hitGO, Vector3 hitPoint) => false;

        private void FireHitEvent(
            uint targetId, float damage, bool isCrit, bool isHeadshot,
            Vector3 hitPoint, bool is2D, GameObject hitGO = null)
        {
            OnHitServerConfirmed?.Invoke(new ProjectileHitPayload
            {
                ProjId                 = 0,
                ConfigId               = VisualConfigId,
                Is3D                   = !is2D,
                TargetId               = targetId,
                Damage                 = damage,
                IsHeadshot             = isHeadshot,
                IsCrit                 = isCrit,
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

        /// <summary>
        /// SAFETY NET for "physics projectile visual/collider only becomes
        /// correct on the SECOND fire of a given pooled instance, every time
        /// — reproduces regardless of whether the config uses a CustomShape,
        /// and regardless of whether ForceSpriteRendererOnly is set (in which
        /// case the FIRST fire shows a real, valid, but WRONG config's sprite
        /// — not a fallback)". That last detail is the key one: it means
        /// VisualConfigId itself is resolving to the wrong (but registered)
        /// config on the very first read, not that the rendering branch logic
        /// is choosing wrong.
        ///
        /// I traced the two most likely explanations against actual source
        /// and ruled BOTH out with certainty:
        ///   • SetVisualConfigId() writes n_VisualConfigId.Value synchronously
        ///     before Spawn() — verified against NetworkVariable.Value's
        ///     setter in NGO 1.7.1: the write updates the backing field
        ///     immediately, no deferred/staged commit, so a same-machine
        ///     (server or host) read right after should already be correct.
        ///   • On a genuinely remote client, verified against NGO 1.7.1's
        ///     NetworkObject.AddSceneObject: SynchronizeNetworkBehaviours
        ///     (which deserializes and applies incoming NetworkVariable data,
        ///     including n_VisualConfigId) runs BEFORE SpawnNetworkObjectLocally
        ///     (which is what eventually fires OnNetworkSpawn) — so the
        ///     correct value should already be applied before OnNetworkSpawn
        ///     ever runs there too.
        ///
        /// Neither NGO-level explanation holds up against the source, which
        /// means the actual mechanism is still unidentified — either
        /// somewhere else in this codebase, or in how the calling weapon
        /// script sequences things. Rather than leave the symptom
        /// unaddressed while that stays open, this re-checks VisualConfigId
        /// a couple of frames after spawn and force-re-applies everything
        /// config-dependent if it differs from what was read at spawn time —
        /// independent of whether HandleVisualConfigChanged already fired for
        /// that same transition (in case that subscription is somehow missing
        /// it). This should mask the symptom regardless of the exact
        /// mechanism, but it IS a safety net, not a confirmed root-cause fix.
        ///
        /// If projectiles still show the wrong visual on first fire after
        /// this: the gap isn't "config settles a couple frames late" at all.
        /// The MID_Logger.LogWarning below will tell you immediately whether
        /// this safety net even triggered — if it never logs, the value was
        /// already correct 2 frames in and the bug is happening somewhere
        /// else entirely (next step: log VisualConfigId + Time.frameCount +
        /// IsServer/IsClient right at the top of OnNetworkSpawn AND inside
        /// SpawnPoolVisual, compare across a first vs. second fire of the
        /// same pooled instance).
        /// </summary>
        private IEnumerator RevalidateConfigAfterSpawn()
        {
            ushort            configIdAtSpawn = VisualConfigId;
            ProjectileConfigSO cfgAtSpawn      = ResolveVisualConfig();

            yield return null;
            yield return null;

            ProjectileConfigSO cfgNow = ResolveVisualConfig();

            // Covers TWO distinct possible causes, not just one: VisualConfigId
            // itself reading a different (but registered) id a couple frames
            // later (id-level mismatch), OR the id staying the same the whole
            // time but ProjectileRegistry.Get() resolving to a DIFFERENT
            // ProjectileConfigSO reference for it now than it did at spawn
            // (registry-population-timing mismatch, distinct root cause).
            bool idChanged  = VisualConfigId != configIdAtSpawn;
            bool cfgChanged = cfgNow != cfgAtSpawn;

            if (idChanged || cfgChanged)
            {
                MID_Logger.LogWarning(_logLevel,
                    "PhysicsProjectileBase: config resolution changed within 2 " +
                    $"frames of spawn (id {configIdAtSpawn}->{VisualConfigId}, " +
                    $"cfg '{cfgAtSpawn?.name}'->'{cfgNow?.name}') — re-applying " +
                    "config-dependent state. This confirms the config genuinely " +
                    "wasn't settled at spawn time; please report this back so " +
                    "the actual mechanism can be tracked down.",
                    nameof(PhysicsProjectileBase));

                RefreshConfigDependentState(cfgNow);

                if (_poolVisual != null)
                {
                    Vector3 dir   = GetDefaultLaunchDir();
                    float   speed = BulletVelocity > 0f ? BulletVelocity : 10f;
                    _poolVisual.InitializeClientVisual(
                        VisualConfigId, transform.position, dir, speed);
                }
            }

            _configRevalidateCoroutine = null;
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
            // SCALING FIX / LIFETIME FIX — see ApplyConfigScale/
            // ApplyConfigLifetime doc comments. Runs even if _poolVisual isn't
            // ready yet (unlike the visual refresh below, which needs it)
            // since neither the collider nor TimeToLive depend on the pool
            // visual at all.
            RefreshConfigDependentState(ResolveVisualConfig());

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
