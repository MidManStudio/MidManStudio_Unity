// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "PhysicsProjectileBase.cs"
// ============================================================================
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

        // ── Wave/Circular/Guided/Teleport movement. The Rigidbody itself is
        // steered so the visual, which follows the body, follows the path.
        // See SetupMovementType's doc comment below.
        private ProjectileMovementType _movementType = ProjectileMovementType.Straight;
        private Vector3                _movementLaunchVelocity;
        private Vector3                _movementPerpAxis;
        private float                  _movementStartServerTime;

        // ── CustomCurve path-movement state ──────────────────────────────────
        // See ApplyCustomCurve()'s doc comment for the full design.
        private Vector3 _movementSpawnPosition;
        private const int PATH_WARP_TABLE_SIZE = 17;
        private bool _customCurveInvalidLogged;
        private readonly float[] _pathWarpTable = new float[PATH_WARP_TABLE_SIZE];
        private bool _pathWarpTableValid;

        // Guided — see SetGuidedTarget/ApplyGuided.
        private bool      _hasGuidedTarget;
        private Transform _guidedTargetTransform;
        private Vector3   _guidedTargetPosition;
        private const float GUIDED_TURN_RATE_RAD_PER_SEC = Mathf.PI; // matches tick_guided's hardcoded turn_rate exactly

        // Teleport — see ApplyTeleport.
        private float _teleportTimer;
        private const float TELEPORT_INTERVAL_SECONDS = 0.12f; // matches tick_teleport's hardcoded INTERVAL exactly

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

        // Piercing mirrors RustSimAdapter.HandlePiercing so a physics projectile
        // and a raycast or Rust sim one behave the same for one
        // ProjectileConfigSO. Non-piercing configs (the common case) start with
        // _collisionsRemaining at 1, so any single hit ends the projectile.
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

            // Nothing here is specific to the owner client.
            // MID_MasterProjectileSystem.SpawnPhysicsProjectile hides this object
            // from the firing client with NetworkHide, so that client never
            // receives it. Its own predicted visual lives out its lifetime and
            // glides to the confirmed hit point via HitConfirmedClientRpc.

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

            // Stop mid-flight collider growth explicitly, as
            // ProjectileVisualBase.ReturnToPoolImmediate does for its own growth,
            // instead of relying on the later SetActive(false) inside
            // MID_NetworkObjectPool.ReturnNetworkObject to end the coroutine.
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
        /// Server-only range limit. MaxRange also feeds the damage falloff in
        /// ComputeConfigDamage, but a physics projectile has no other range
        /// cutoff (a raycast is bounded by its own distance and a Rust sim
        /// projectile by the native max-range check), so this ends the shot once
        /// the straight-line distance from the spawn position reaches MaxRange.
        /// Without it a low-drag, gravity-free projectile would fly until
        /// TimeToLive (see ApplyConfigLifetime).
        ///
        /// base.Update() still runs first, so the TimeToLive watchdog and the
        /// NetworkTransform interpolation are unaffected; this adds a server-only
        /// distance check on top.
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
        /// Per-physics-tick movement for the current movement type. Wave and
        /// Circular set the Rigidbody velocity to DeterministicMotionMath's
        /// closed-form output for the time since launch; Guided, Teleport and
        /// CustomCurve have their own steps. The body stays a real Rigidbody that
        /// the physics engine integrates, so collision detection is unchanged and
        /// only the velocity follows the path. See SetupMovementType.
        ///
        /// Runs on the server only (the one place this Rigidbody is physically
        /// authoritative), and only once launched and not yet hit, the same gating
        /// as the MaxRange check in Update(). A subclass may override it.
        /// </summary>
        protected virtual void FixedUpdate()
        {
            if (!IsServer || !_launched || HasHit) return;

            switch (_movementType)
            {
                case ProjectileMovementType.Wave:
                case ProjectileMovementType.Circular:
                    ApplyWaveCircular();
                    break;
                case ProjectileMovementType.Guided:
                    ApplyGuided();
                    break;
                case ProjectileMovementType.Teleport:
                    ApplyTeleport();
                    break;
                case ProjectileMovementType.CustomCurve:
                    ApplyCustomCurve();
                    break;
                // Straight: nothing to do here. Rigidbody physics (velocity and
                // gravity) already handles it, arc included. There is no separate
                // Arching case: an arc is Straight with gravity (see
                // ProjectileMovementType.Straight's doc comment in
                // ProjectileLib.cs), which the subclass OnLaunch gets from the
                // gravity scale.
            }
        }

        private void ApplyWaveCircular()
        {
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
        /// Custom path movement (physics projectiles only; see the enum member's
        /// own doc comment for why the Rust sim never touches it). The path is a
        /// set of points or a formula, authored the same way ProjectilePatternSO
        /// authors spawn patterns, and not an AnimationCurve. Only the pacing
        /// along the path (cfg.CustomCurveSpeedMultiplier) is a curve.
        ///
        /// Exact start and exact end: the target position is recomputed from the
        /// elapsed time every tick (spawn + forward * offset.x + perpAxis *
        /// offset.y, with the offset from cfg.EvaluateCustomPath(t)) instead of
        /// being accumulated by integrating a velocity. Integration lets float
        /// error build up over a long flight and can miss the authored endpoint.
        /// Recomputing from t never depends on where the projectile ended up on
        /// the previous tick, so it cannot drift.
        ///
        /// Physics-integrated, not teleported: setting the Rigidbody position
        /// directly would skip collision detection between the old and new
        /// position (TeleportBody does that on purpose for the Teleport type, this
        /// type must not). Instead the velocity needed to reach the target this
        /// tick, (target - currentPos) / dt, goes to ApplyMovementVelocity like
        /// every other movement type here. It corrects itself: a deviation from a
        /// collision nudge or physics rounding is fixed on the next tick, because
        /// the target always comes from elapsed time.
        ///
        /// The speed curve is a warp, not a velocity multiplier. Scaling the
        /// instantaneous velocity by an arbitrary curve does not preserve the
        /// total distance covered and can miss the endpoint. EvaluateSpeedWarp
        /// instead maps "fraction of the duration elapsed" to "fraction of the path
        /// covered by now" using a normalized cumulative-sum table built once at
        /// launch (BuildPathWarpTable). It is normalized so tWarped(1) is always 1,
        /// so the trip always ends on time at the last point whatever the curve
        /// looks like. Faster parts of the curve cover path distance faster.
        ///
        /// A path authored as flat (forward, sideways) numbers works in 2D and 3D:
        /// offset.x follows the launch direction (_movementLaunchVelocity) and
        /// offset.y follows _movementPerpAxis (DeterministicMotionMath.
        /// ComputePerpAxis2D/3D, the same perpendicular axis Wave uses).
        ///
        /// protected virtual so a subclass can replace just this movement type's
        /// path math without reimplementing all of FixedUpdate. FixedUpdate itself
        /// is protected virtual too, for fully custom movement.
        /// </summary>
        protected virtual void ApplyCustomCurve()
        {
            var cfg = ResolveVisualConfig();
            if (cfg == null) return;

            float duration = cfg.CustomCurveDuration > 0f ? cfg.CustomCurveDuration : cfg.Lifetime;
            if (duration <= 0f) return;

            float timeAlive = NetworkManager.ServerTime.TimeAsFloat - _movementStartServerTime;
            float tLinear = timeAlive / duration;
            tLinear = cfg.CustomCurveLoop ? Mathf.Repeat(tLinear, 1f) : Mathf.Clamp01(tLinear);

            float tWarped = EvaluateSpeedWarp(tLinear);
            Vector2 offset = cfg.EvaluateCustomPath(tWarped);

            Vector3 forwardDir = _movementLaunchVelocity.sqrMagnitude > 0.0001f
                ? _movementLaunchVelocity.normalized
                : GetCurrentVelocity().normalized;

            Vector3 targetPos = _movementSpawnPosition
                + forwardDir     * offset.x
                + _movementPerpAxis * offset.y;

            float dt = Time.fixedDeltaTime;
            Vector3 velocity = dt > 0.0001f
                ? (targetPos - transform.position) / dt
                : Vector3.zero;

            // A path formula that divides by zero or takes the root of a negative
            // gives a non-finite position. Handing that to a Rigidbody as a velocity
            // is an engine error and the projectile stops working, so skip the
            // update and say why once per launch.
            if (!IsFinite(velocity))
            {
                if (!_customCurveInvalidLogged)
                {
                    _customCurveInvalidLogged = true;
                    MID_Logger.LogWarning(_logLevel,
                        $"CustomCurve path for config {VisualConfigId} produced a non-finite " +
                        "position. Check the path formula for a division by zero or a square " +
                        "root of a negative number. Skipping the movement update.",
                        nameof(PhysicsProjectileBase));
                }
                return;
            }

            ApplyMovementVelocity(velocity);
        }

        private static bool IsFinite(Vector3 v)
            => !(float.IsNaN(v.x) || float.IsInfinity(v.x)
              || float.IsNaN(v.y) || float.IsInfinity(v.y)
              || float.IsNaN(v.z) || float.IsInfinity(v.z));

        /// <summary>
        /// Homing step for the Guided movement type. Every FixedUpdate it
        /// recomputes the direction to the current target (a Transform target is
        /// followed live, a fixed point stays fixed) and turns the Rigidbody's
        /// current velocity toward it. Only the direction changes; speed is kept.
        /// This matches tick_guided and tick_guided_3d in
        /// rust_lib/projectile_core/src/simulation.rs: a fixed PI rad/s (180
        /// degrees per second) turn rate, clamped so one tick never overshoots the
        /// target direction. The native sim hardcodes that rate too (it is not a
        /// config field), and GUIDED_TURN_RATE_RAD_PER_SEC matches it so a physics
        /// and a Rust sim guided shot turn alike.
        ///
        /// It uses Vector3.Slerp between the current and target direction, scaled
        /// to advance exactly turnRate * dt radians per tick. That is exact for a
        /// fixed angular step, and one implementation serves 2D and 3D (a 2D
        /// velocity has Z = 0, and a Slerp between two Z = 0 unit vectors stays in
        /// the plane), where the native sim uses a separate atan2 form for 2D.
        ///
        /// With no target set it does nothing: the velocity stays whatever real
        /// physics produced (gravity, drag, collision response), so a Guided shot
        /// is a normal physics projectile until SetGuidedTarget is called.
        /// </summary>
        private void ApplyGuided()
        {
            if (!_hasGuidedTarget) return;

            Vector3 targetPos = _guidedTargetTransform != null
                ? _guidedTargetTransform.position
                : _guidedTargetPosition;

            Vector3 curVel = GetCurrentVelocity();
            float   speed  = curVel.magnitude;
            if (speed < 0.0001f) return; // no meaningful direction to turn from

            Vector3 curDir   = curVel / speed;
            Vector3 toTarget = targetPos - transform.position;
            if (toTarget.sqrMagnitude < 0.0001f) return; // already at target position

            Vector3 tgtDir = toTarget.normalized;
            float   angle  = Vector3.Angle(curDir, tgtDir) * Mathf.Deg2Rad;
            if (angle <= 0.0001f) return; // already aligned

            float maxTurnThisTick = GUIDED_TURN_RATE_RAD_PER_SEC * Time.fixedDeltaTime;
            float t = Mathf.Min(maxTurnThisTick / angle, 1f);

            Vector3 newDir = Vector3.Slerp(curDir, tgtDir, t);
            ApplyMovementVelocity(newDir * speed);
        }

        /// <summary>
        /// Sets (or replaces) the target this physics projectile steers
        /// toward while its config's MovementType is Guided. Safe to call
        /// any time after the projectile has launched, including changing
        /// targets mid-flight (e.g. retargeting onto whoever gets closest).
        /// Has no effect on any other movement type — this only ever gets
        /// read by ApplyGuided, itself only ever called when
        /// MovementType == Guided.
        /// </summary>
        public void SetGuidedTarget(Transform target)
        {
            _guidedTargetTransform = target;
            _hasGuidedTarget       = target != null;
            if (target != null) _guidedTargetPosition = target.position;
        }

        /// <summary>
        /// Sets a fixed point to steer toward instead of a live Transform —
        /// e.g. a target's last-known position, or a static objective.
        /// </summary>
        public void SetGuidedTarget(Vector3 worldPosition)
        {
            _guidedTargetTransform = null;
            _guidedTargetPosition  = worldPosition;
            _hasGuidedTarget       = true;
        }

        /// <summary>Stops homing — reverts to flying whatever direction real physics already has it going.</summary>
        public void ClearGuidedTarget()
        {
            _guidedTargetTransform = null;
            _hasGuidedTarget       = false;
        }

        /// <summary>
        /// Port of tick_teleport and tick_teleport_3d: every
        /// TELEPORT_INTERVAL_SECONDS (0.12 s, the native sim's hardcoded interval)
        /// the projectile jumps forward by speed * interval in one instant instead
        /// of moving continuously. Between jumps it does not move at all, as in the
        /// native sim, which only changes position inside its own "timer >=
        /// INTERVAL" branch.
        ///
        /// Velocity is zeroed every tick this is active (the subclass OnLaunch also
        /// turns gravity off for Teleport, as for Wave and Circular), so nothing
        /// pushes the body between hops. Only TeleportBody moves it, using the
        /// original launch direction and speed saved in _movementLaunchVelocity,
        /// since the live velocity is kept at zero.
        ///
        /// The jump goes through TeleportBody, which moves the Rigidbody itself
        /// rather than only the transform, so the physics engine knows where the
        /// body is.
        ///
        /// Limit: a teleport skips the space between the two positions with no
        /// sweep, so thin colliders can be passed through at a high enough speed.
        /// tick_teleport has the same property natively.
        /// </summary>
        private void ApplyTeleport()
        {
            ApplyMovementVelocity(Vector3.zero); // frozen between hops, matches native

            _teleportTimer += Time.fixedDeltaTime;
            if (_teleportTimer < TELEPORT_INTERVAL_SECONDS) return;
            _teleportTimer -= TELEPORT_INTERVAL_SECONDS;

            float   speed = _movementLaunchVelocity.magnitude;
            Vector3 dir   = _movementLaunchVelocity.sqrMagnitude > 0.0001f
                ? _movementLaunchVelocity.normalized
                : transform.forward;
            float   jump  = speed * TELEPORT_INTERVAL_SECONDS;

            TeleportBody(transform.position + dir * jump);
        }

        /// <summary>
        /// Captures, once right after launch, the state the movement-type steps in
        /// FixedUpdate need. Called from OnProjectileInitialised.
        ///
        /// _movementLaunchVelocity is direction * BulletVelocity, built from
        /// launchDir and BulletVelocity instead of read back from the Rigidbody,
        /// so it works the same in 2D and 3D without the base class touching the
        /// Rigidbody. Wave uses it as its direction basis, Teleport for the jump
        /// direction and speed, and CustomCurve for its forward axis. Circular
        /// only needs the perpendicular axis, and Guided reads the live velocity
        /// through GetCurrentVelocity each tick.
        ///
        /// _movementPerpAxis comes from DeterministicMotionMath.ComputePerpAxis2D/3D,
        /// which produces the same axis as BatchSpawnHelper.GetAccel2D/3D for a
        /// Rust sim projectile with the same config, so a physics and a Rust sim
        /// projectile with the same Wave or Circular config curve the same way
        /// (the wave amplitude differs, see DeterministicMotionMath). Only Wave,
        /// Circular and CustomCurve read it, so only those compute it.
        ///
        /// Straight and Guided return right after the movement type is resolved:
        /// neither needs the rest (Guided gets its state from SetGuidedTarget).
        /// </summary>
        private void SetupMovementType(Vector3 launchDir)
        {
            var cfg = ResolveVisualConfig();
            _movementType  = cfg != null ? cfg.MovementType : ProjectileMovementType.Straight;
            _teleportTimer = 0f;

            // Reset every new launch — a reused pooled instance must not
            // silently inherit a target from whatever it was guided toward
            // last time it fired. Callers set a fresh target per-shot via
            // SetGuidedTarget after this runs (SpawnPhysicsProjectile →
            // InitialiseProjectile, both of which happen before any caller
            // has a chance to call SetGuidedTarget for THIS shot).
            _hasGuidedTarget       = false;
            _guidedTargetTransform = null;

            if (_movementType != ProjectileMovementType.Wave &&
                _movementType != ProjectileMovementType.Circular &&
                _movementType != ProjectileMovementType.Teleport &&
                _movementType != ProjectileMovementType.CustomCurve)
                return;

            _movementLaunchVelocity  = launchDir.normalized * BulletVelocity;
            _movementStartServerTime = NetworkManager.ServerTime.TimeAsFloat;

            if (_movementType == ProjectileMovementType.Wave ||
                _movementType == ProjectileMovementType.Circular ||
                _movementType == ProjectileMovementType.CustomCurve)
            {
                _movementPerpAxis = Is2D
                    ? DeterministicMotionMath.ComputePerpAxis2D(launchDir)
                    : DeterministicMotionMath.ComputePerpAxis3D(launchDir);
            }

            if (_movementType == ProjectileMovementType.CustomCurve)
            {
                _customCurveInvalidLogged = false;
                _movementSpawnPosition = transform.position;
                BuildPathWarpTable(cfg);
            }
        }

        /// <summary>
        /// Samples cfg.CustomCurveSpeedMultiplier at PATH_WARP_TABLE_SIZE evenly
        /// spaced t values, builds a cumulative sum and normalizes it so the last
        /// entry is exactly 1.0. The result is a monotonic lookup table mapping
        /// "fraction of the duration elapsed" to "fraction of the path covered by
        /// now". See ApplyCustomCurve's doc comment for why a table is used
        /// instead of a per-tick velocity multiplier. Built once per launch, which
        /// is cheap (17 curve evaluations).
        /// </summary>
        private void BuildPathWarpTable(ProjectileConfigSO cfg)
        {
            if (cfg == null || cfg.CustomCurveSpeedMultiplier == null)
            {
                _pathWarpTableValid = false;
                return;
            }

            float cumulative = 0f;
            _pathWarpTable[0] = 0f;
            for (int i = 1; i < PATH_WARP_TABLE_SIZE; i++)
            {
                float t = (float)i / (PATH_WARP_TABLE_SIZE - 1);
                // Floored at a small positive value instead of 0: a zero-width
                // segment would leave a flat span in the table that the lerp
                // below cannot index into, so the floor keeps every step
                // interpolatable even where the authored curve dips to 0.
                float speed = Mathf.Max(0.0001f, cfg.CustomCurveSpeedMultiplier.Evaluate(t));
                cumulative += speed;
                _pathWarpTable[i] = cumulative;
            }

            float total = _pathWarpTable[PATH_WARP_TABLE_SIZE - 1];
            if (total > 0.0001f)
            {
                for (int i = 0; i < PATH_WARP_TABLE_SIZE; i++)
                    _pathWarpTable[i] /= total;
            }
            _pathWarpTableValid = true;
        }

        /// <summary>
        /// Maps a linear 0..1 fraction of elapsed duration to a warped 0..1
        /// fraction of path progress, via the table BuildPathWarpTable built
        /// at launch. Falls through unchanged (tWarped == tLinear) if no
        /// speed curve was available to build a table from.
        /// </summary>
        private float EvaluateSpeedWarp(float tLinear)
        {
            if (!_pathWarpTableValid) return tLinear;

            float scaled = Mathf.Clamp01(tLinear) * (PATH_WARP_TABLE_SIZE - 1);
            int   idx    = Mathf.Clamp((int)scaled, 0, PATH_WARP_TABLE_SIZE - 2);
            float frac   = scaled - idx;
            return Mathf.Lerp(_pathWarpTable[idx], _pathWarpTable[idx + 1], frac);
        }

        #endregion

        #region Abstract / Virtual Hooks

        protected abstract void OnPhysicsSetup();
        protected abstract Vector3 OnLaunch(float bulletVelocity);
        protected abstract void StopPhysics();
        protected abstract bool Is2D { get; }

        /// <summary>
        /// Refreshes the per-instance state that depends on the resolved
        /// ProjectileConfigSO: collider size, lifetime and hit layers. Called from
        /// every place VisualConfigId is first known or changes (OnNetworkSpawn,
        /// SetVisualConfigId, HandleVisualConfigChanged and
        /// RevalidateConfigAfterSpawn), so each of them needs one call.
        /// </summary>
        private void RefreshConfigDependentState(ProjectileConfigSO cfg)
        {
            ApplyConfigScale(cfg);
            ApplyConfigLifetime(cfg);
            ApplyConfigHitLayers(cfg);
        }

        /// <summary>
        /// Sets the inherited TimeToLive from ProjectileConfigSO.Lifetime.
        /// NetworkProjectileBase does not know ProjectileConfigSO (it is
        /// deliberately generic), so without this the lifetime would be whatever
        /// fixed value the prefab inspector holds (8 s by default). The watchdog in
        /// NetworkProjectileBase.Update() (server time >= _endOfLifeTime) then
        /// enforces it as usual.
        ///
        /// It has to run before InitialiseProjectile(), which reads TimeToLive
        /// exactly once to compute _endOfLifeTime. The normal
        /// SpawnPhysicsProjectile flow calls SetVisualConfigId() first and launches
        /// afterwards, so that order holds.
        /// </summary>
        private void ApplyConfigLifetime(ProjectileConfigSO cfg)
        {
            if (cfg != null) TimeToLive = cfg.Lifetime;
        }

        /// <summary>
        /// Copies cfg.HitLayers into _damageLayerMask, which
        /// ApplyExplosionDamage2D/3D pass to OverlapCircleNonAlloc and
        /// OverlapSphereNonAlloc. Without it an explosion config's HitLayers would
        /// be ignored in favour of the prefab's own mask (-1, everything). The
        /// direct-hit path uses PassesConfigLayerMask instead.
        /// </summary>
        private void ApplyConfigHitLayers(ProjectileConfigSO cfg)
        {
            if (cfg != null) _damageLayerMask = cfg.HitLayers;
        }

        /// <summary>
        /// Speed rule for physics projectiles. cfg.ResolveSpeed() (the config's
        /// MinSpeed and MaxSpeed) is the config's speed, and by default it wins
        /// over whatever bulletVelocity the caller passes whenever a config
        /// resolves. SpawnPhysicsProjectile does not call InitialiseProjectile;
        /// the calling weapon script launches the projectile itself and passes its
        /// own bulletVelocity, and a weapon script that always passes a non-zero
        /// speed would make a "use the config when the caller passes 0" rule never
        /// apply.
        ///
        /// _allowCallerVelocityOverride (inspector, default false) is for a
        /// weapon that deliberately varies speed per shot (charged or randomised
        /// shots). With it on the caller's value wins and cfg.ResolveSpeed() is
        /// only the fallback when the caller passes 0 or less. With it off, the
        /// caller's value is only used when no config resolves.
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
                // The caller's value wins whenever it is a real (> 0) speed. The
                // config is only the fallback when the caller did not specify one.
                if (bulletVelocity <= 0f && cfg != null)
                    bulletVelocity = cfg.ResolveSpeed();
            }
            else if (cfg != null)
            {
                // Default: the config wins when one resolves, whatever the caller
                // passed.
                bulletVelocity = cfg.ResolveSpeed();
            }
            // Otherwise no config resolved (VisualConfigId is not registered), so
            // the caller's value is used.

            base.InitialiseProjectile(
                ownerMidId, firedByNetworkObjectId, bulletVelocity,
                isBotOwned, weaponLevel, serverIsActualOwner, enableVisualSynch);
        }

        /// <summary>
        /// Sizes the collider from ProjectileConfigSO.FullSizeX and FullSizeY, so a
        /// physics projectile hit-tests at the size of the config that fired it.
        /// The cosmetic visual (ProjectileVisual_2D/3D, a separate GameObject from
        /// the LocalObjectPool, see SpawnPoolVisual) reads the same fields for its
        /// own rendering. The subclass overrides ApplyColliderSize for whichever
        /// collider type it uses; this method decides what size to apply and when,
        /// so subclasses never read the config themselves.
        ///
        /// Called right after OnPhysicsSetup() in OnNetworkSpawn (a best-effort
        /// size from whatever VisualConfigId resolves to at that point), and from
        /// SetVisualConfigId, HandleVisualConfigChanged and
        /// RevalidateConfigAfterSpawn, which correct it once the server's config id
        /// has arrived (the config is not always known yet at spawn time).
        ///
        /// cfg may be null (registry not ready, or the id is not registered). The
        /// method then does nothing and leaves the size already applied, or the
        /// prefab default before the first call.
        ///
        /// It resizes the collider instead of scaling transform.localScale on this
        /// root: the pooled visual is parented under this transform and applies its
        /// own FullSizeX and FullSizeY scale, so scaling the root as well would
        /// double the visible size.
        ///
        /// The static case needs no extra NetworkVariable or RPC. Every peer
        /// resolves the same VisualConfigId to the same shared ProjectileConfigSO
        /// asset and computes the same size.
        ///
        /// With cfg.UseScaleGrowth the collider does not jump to FullSizeX and
        /// FullSizeY. It grows from FullSize * SpawnScaleFraction to full size with
        /// the formula of the native tick_scale (rust_lib/projectile_core/src/
        /// simulation.rs): current += (target - current) * speed * dt. Growth runs
        /// on the server only, where the collider has gameplay effect. A
        /// non-server peer snaps to full size, which is harmless because nothing
        /// there reads the collider for hit detection.
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
        /// Reads the Rigidbody's CURRENT velocity — used by ApplyGuided, which
        /// (unlike Wave/Circular/Teleport) needs the live, continuously-updated
        /// velocity each tick rather than a value captured once at launch,
        /// since it turns whatever real physics currently has the body doing
        /// rather than overriding it outright. Return Vector3.zero if the
        /// Rigidbody reference isn't available for any reason.
        /// </summary>
        protected abstract Vector3 GetCurrentVelocity();

        /// <summary>
        /// Instantly repositions the Rigidbody — used by ApplyTeleport for its
        /// per-interval jump. Implementations should set the Rigidbody's own
        /// .position (not transform.position directly), so the physics engine
        /// stays aware of where the body actually is rather than only finding
        /// out on the next sync.
        /// </summary>
        protected abstract void TeleportBody(Vector3 position);

        /// <summary>
        /// Resolves the ProjectileConfigSO currently referenced by
        /// VisualConfigId, or null if it is not registered or the registry is not
        /// ready.
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
        /// Plays the config's impact effect on every client and returns the pool
        /// visual. It calls ProjectileImpactHandler.PlayImpact, the same call
        /// MID_ProjectileNetworkBridge.HitConfirmedClientRpc makes for the raycast
        /// and Rust sim path, so physics projectiles honour cfg.ImpactEffectType.
        ///
        /// PlayImpact also takes an isHeadshot flag for a headshot-specific
        /// variant. It is not passed here, because that would change the signature
        /// of NetworkProjectileBase.OnSpawnImpactEffectClient, a protected virtual
        /// method that a game subclass may already override, and an override must
        /// match the signature exactly.
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
        /// Tells this instance which ProjectileConfigSO (by ProjectileRegistry id)
        /// it represents, so its visual, lifetime, hit layers and collider size
        /// match the config that was fired. Without a call the instance uses
        /// whatever the prefab inspector holds (id 0). MID_MasterProjectileSystem.
        /// SpawnPhysicsProjectile calls it. It is safe to call after the visual has
        /// spawned: the visual is re-initialised against the new config at once.
        ///
        /// The id is the server-written NetworkVariable n_VisualConfigId, so it
        /// replicates to every client (immediately when set before Spawn(), or as a
        /// delta shortly after when set later; HandleVisualConfigChanged re-applies
        /// the visual either way). On a non-server instance the network write is
        /// skipped, but the local visual is still refreshed when one exists.
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

            // See ApplyConfigScale and ApplyConfigLifetime. This reads
            // VisualConfigId, not the configId parameter, so it applies whatever the
            // NetworkVariable actually holds: the value just written on the
            // server, or the last synced value on a non-server instance.
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
        /// Direct-hit layer filter from cfg.HitLayers. Its tooltip says to exclude
        /// the 'Player' layer to prevent friendly fire or self damage from
        /// pattern projectiles, and this is what applies that to a direct physics
        /// hit. With no config every layer passes.
        ///
        /// It follows LocalProjectileManager.PassesLayerMask: a mask of -1 means
        /// "everything" (the documented HitLayers default), otherwise a bitwise
        /// membership test on the hit object's layer.
        ///
        /// It filters damage and hit registration only. The projectile still
        /// collides physically (bounces, stops) with colliders on excluded layers.
        /// Passing through them with no physical response would need
        /// Physics.IgnoreCollision per instance, or trigger colliders with manual
        /// movement.
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
                // Hit something with no NetworkObject (environment or terrain).
                // Piercing lets a shot pass through damageable targets, not
                // walls, so it always stops here whatever budget remains.
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
        /// Evaluates damage from the ProjectileConfigSO: the damage curve at the
        /// normalized travel distance, times HeadshotMultiplier when
        /// CheckHeadshotPhysics returns true, times CritMultiplier when the crit
        /// roll succeeds, times the owner's damage multiplier. Falls back to
        /// _baseDamage times the multiplier when no config is registered for
        /// VisualConfigId. The crit and headshot results come back through the
        /// out parameters so FireHitEvent can put them in the payload.
        ///
        /// Headshots: LocalProjectileManager.CheckHeadshotLocal (the hook for the
        /// offline and local path) defaults to false and is meant to be overridden
        /// by a game subclass that knows its hitboxes, which this shared package
        /// does not. CheckHeadshotPhysics follows the same pattern for the physics
        /// path.
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
        /// Headshot hook, see ComputeConfigDamage. Returns false (no headshots) by
        /// default, like LocalProjectileManager.CheckHeadshotLocal. A game subclass
        /// overrides it once it can tell head hitboxes apart (for example from
        /// hitGO's tag, layer or a hitbox-type component).
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
        /// Safety net for a config that is not settled when the projectile spawns.
        /// It records the config id and the resolved config at spawn, waits two
        /// frames and compares. When either differs it logs a warning that names
        /// both, re-applies the config-dependent state and re-initializes the
        /// visual. It runs whether or not HandleVisualConfigChanged already fired
        /// for the same change.
        ///
        /// It was added for a reported symptom on pooled instances: the visual and
        /// collider were right only on the second fire, and the first fire showed
        /// a valid but wrong config's sprite. The mechanism is not identified.
        /// n_VisualConfigId.Value is written synchronously before Spawn(), and on a
        /// remote client NetworkVariable data is applied before OnNetworkSpawn runs
        /// (both checked against NGO 1.7.1), so neither explains it. This method
        /// masks the symptom and is not a confirmed root-cause fix.
        ///
        /// If the wrong visual still shows on the first fire and the warning never
        /// logs, the config was already settled two frames in and the cause is
        /// elsewhere. The next step is to log VisualConfigId, Time.frameCount,
        /// IsServer and IsClient at the top of OnNetworkSpawn and inside
        /// SpawnPoolVisual, and compare a first and a second fire of the same
        /// pooled instance.
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
            // See ApplyConfigScale and ApplyConfigLifetime. This runs even when
            // _poolVisual is not ready yet, unlike the visual refresh below,
            // because neither the collider nor TimeToLive depends on the visual.
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
