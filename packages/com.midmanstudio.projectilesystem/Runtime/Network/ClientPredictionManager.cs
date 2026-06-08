// ClientPredictionManager.cs
//
// REWRITE — Deterministic Client-Local Special Movement Prediction.
//
// FIX (Arching visual jank): Linear extrapolation completely ignores gravity,
//   causing the predicted position to diverge from the server's parabolic path
//   immediately. Every 4-tick snapshot interval triggers reconciliation, snapping
//   the visual repeatedly. Fix: parabolic prediction for MOVE_ARCHING using
//   pos(t) = origin + dir*speed*t + (0, 0.5*gravityAy*t², 0). GravityAy is read
//   from cfg.GravityScale and stored in PredictedProjectile.GravityAy.
//   Rotation also updated to match the instantaneous velocity direction.
//
// FIX (Guided "goes back then forward" + zig-zag): Linear extrapolation
//   origin + dir*speed*t does not follow homing trajectories. Each snapshot
//   showing the curved path triggered reconciliation, snapping the visual backward
//   (especially severe immediately after spawn when predicted position overshoots
//   the server's delayed snapshot position). Fix: MOVE_GUIDED and MOVE_TELEPORT
//   now use a "snapshot-chase" mode: position and direction are updated from
//   server snapshots, and the visual smoothly lerps toward the extrapolated
//   snapshot position. Standard linear reconciliation is bypassed for these types.
//
// FIX (host mode — _pendingTempIds accumulation):
//   SpawnImmediatePrediction and SpawnLocalPhysicsVisual return immediately when
//   IsServer is true.
//
// FIX (expired prediction → dropped visual):
//   OnSpawnConfirmed checks _predictions.ContainsKey before LinkPredictionId.
//   Falls back to spawning a fresh proxy visual rather than dropping it silently.
//
// UNCHANGED:
//   DeterministicMath (Wave/Circular) — analytically exact, no snapshots.
//   Linear prediction (Straight) — unchanged.
//   Host rendering path, collision, impact, physics/raycast visual paths.

using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Pools;
using MidManStudio.Core.HelperFunctions;
using MidManStudio.Projectiles.Core;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Managers;
using MidManStudio.Projectiles.Visuals;
using Unity.Netcode;

namespace MidManStudio.Projectiles.Network
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Internal helpers
    // ─────────────────────────────────────────────────────────────────────────

    internal sealed class CircularBuffer<T>
    {
        private readonly T[] _buf; private int _head, _count;
        public int Count => _count;
        public CircularBuffer(int cap) => _buf = new T[cap];
        public void Add(T v)
        {
            _buf[_head] = v;
            _head = (_head + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
        }
        public T Get(int i)
        {
            if (i < 0 || i >= _count) throw new IndexOutOfRangeException();
            return _buf[(_head - _count + i + _buf.Length) % _buf.Length];
        }
        public bool TryFindLatest(Predicate<T> m, out T r)
        {
            for (int i = _count - 1; i >= 0; i--)
            { var v = Get(i); if (m(v)) { r = v; return true; } }
            r = default; return false;
        }
    }

    internal struct ProjectileStatePayload
    {
        public int    ServerTick;
        public Vector3 PredictedPosition;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Prediction mode
    // ─────────────────────────────────────────────────────────────────────────

    internal enum PredictionMode : byte
    {
        /// Linear / parabolic prediction + snapshot reconciliation.
        /// Straight: linear. Arching: parabolic (gravity-corrected).
        Linear = 0,

        /// Closed-form parametric calculation — no snapshot reconciliation.
        /// Used for Wave and Circular. Analytically exact for all clients.
        DeterministicMath = 1,
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Predicted projectile data
    // ─────────────────────────────────────────────────────────────────────────

    internal sealed class PredictedProjectile
    {
        // ── Identity ─────────────────────────────────────────────────────────
        public uint   BaseProjId, ProjId;
        public ushort ConfigId;
        public bool   Is3D;

        // ── Spawn state ───────────────────────────────────────────────────────
        public Vector3 Origin, Direction;
        public float   Speed, SpawnTime;
        public int     ServerSpawnTick;

        // ── Prediction mode ───────────────────────────────────────────────────
        public PredictionMode PredictionMode;

        // ── DeterministicMath clock anchor ────────────────────────────────────
        public float ServerSpawnNetworkTime;

        // ── Cached movement type ──────────────────────────────────────────────
        public byte CachedMovementType;

        // ── Initial velocity components ───────────────────────────────────────
        public float InitialVelX, InitialVelY, InitialVelZ;

        // ── Cached wave parameters ────────────────────────────────────────────
        public float CachedWaveAmplitude;
        public float CachedWaveFrequency;
        public float CachedWavePhaseOffset;

        // ── Cached circular parameters ────────────────────────────────────────
        public float CachedCircularAngularSpeedRad;
        public float CachedCircularStartAngleRad;
        public float CachedCircularRadius;

        // ── Pre-computed perpendicular axis ───────────────────────────────────
        public float PerpAxisX, PerpAxisY, PerpAxisZ;

        // ── Proxy flag ────────────────────────────────────────────────────────
        public bool IsProxyProjectile;

        // ── Visual ────────────────────────────────────────────────────────────
        public GameObject           VisualObject;
        public ProjectileVisualBase VisualScript;
        public PoolableObjectType   UsedPoolType;

        // ── Arching gravity (FIX) ─────────────────────────────────────────────
        /// Vertical gravity acceleration (cfg.GravityScale == Rust GravityAy).
        /// Non-zero for MOVE_ARCHING configs that have gravity.
        public float GravityAy;

        // ── Snapshot-chase (FIX) — Guided and Teleport ───────────────────────
        /// Latest server-confirmed position received via snapshot.
        public Vector3 LastSnapshotPos;
        /// Velocity direction estimated from consecutive snapshots.
        /// Initialized to Direction; updated each snapshot.
        public Vector3 LastSnapshotVelDir;
        /// Time.time when LastSnapshotPos was recorded.
        public float LastSnapshotTime;
        /// True once at least one snapshot has been received.
        public bool HasSnapshot;

        // ── Linear prediction (PredictionMode.Linear) ────────────────────────
        public CircularBuffer<ProjectileStatePayload> History;
        public bool    IsReconciling;
        public Vector3 ReconcileStartPosition;
        public Vector3 ReconcileTarget;
        public float   ReconcileStartTime;
        public float   ReconcileDuration;

        // ── Lifetime / hit ────────────────────────────────────────────────────
        public float   MaxLifetime;
        public bool    IsConfirmedHit;
        public Vector3 ConfirmedHitPosition;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ClientPredictionManager
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class ClientPredictionManager : MonoBehaviour
    {
        #region Inspector

        [Header("Reconciliation (Linear mode — Straight/Arching only)")]
        [Tooltip("Position error below which reconciliation is skipped.")]
        [SerializeField] private float _reconcileThreshold = 0.3f;
        [Tooltip("Error above which a hard-snap is used instead of smooth blending.")]
        [SerializeField] private float _hardSnapThreshold  = 3f;
        [Tooltip("Seconds to blend the visual toward the server-correct position.")]
        [SerializeField] private float _reconcileDuration  = 0.12f;

        [Header("History Buffer (Linear mode)")]
        [SerializeField] private int _historySize = 32;

        [Header("Snapshot Chase (Guided/Teleport)")]
        [Tooltip("Lerp speed (units/sec factor) for snapshot-chase visual correction.\n" +
                 "Higher = snappier, lower = smoother but laggier.")]
        [SerializeField] private float _snapshotChaseLerp = 15f;

        [Header("Visual Pool Types")]
        [SerializeField] private PoolableObjectType _visualPoolType2D
            = PoolableObjectType.Projectile_Visual2D;
        [SerializeField] private PoolableObjectType _visualPoolType3D
            = PoolableObjectType.Projectile_Visual3D;

        [Header("Local Player")]
        [SerializeField] private ulong _localPlayerMidId;

        [Header("Debug")]
        [SerializeField] private bool _enableLogs = false;

        #endregion

        #region State

        private readonly Dictionary<uint, PredictedProjectile> _predictions = new(64);
        private readonly Queue<uint> _pendingTempIds = new Queue<uint>(16);
        private uint _nextTempId = 0xFFFE0000u;

        #endregion

        #region Public API — Identity

        public void SetLocalPlayerMidId(ulong midId) => _localPlayerMidId = midId;

        #endregion

        #region Public API — Prediction Spawn

        /// <summary>
        /// Immediate visual for the firing client's Rust sim projectile.
        /// FIX: skipped in host mode — host renders from ServerProjectileAuthority.LateUpdate.
        /// </summary>
        public void SpawnImmediatePrediction(SpawnConfirmation tempConf)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                return;

            for (int i = 0; i < tempConf.ProjectileCount; i++)
            {
                uint tempId = _nextTempId++;
                SpawnPredictionVisual(tempId, tempConf, i);
                _pendingTempIds.Enqueue(tempId);
            }
        }

        /// <summary>
        /// Standalone visual for a physics projectile on the firing client.
        /// FIX: skipped in host mode.
        /// </summary>
        public void SpawnLocalPhysicsVisual(
            ushort configId, Vector3 origin, Vector3 dir, float speed)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                return;

            uint id = _nextTempId++;
            SpawnPredictionVisual(id, new SpawnConfirmation
            {
                BaseProjId          = 0,
                ProjectileCount     = 1,
                ConfigId            = configId,
                ServerSpawnTick     = GetApproxServerTick(),
                Origin              = origin,
                Direction           = dir,
                Speed               = speed,
                OwnerMidId          = _localPlayerMidId,
                ExtraDirectionCount = 0,
                ExtraDirections     = null,
                ServerNetworkTime   = 0f
            }, 0);
        }

        #endregion

        #region Public API — Bridge Callbacks

        /// <summary>
        /// Called by MID_ProjectileNetworkBridge when server confirms a Rust sim spawn.
        /// FIX: if temp prediction expired before RPC arrived, spawns a fresh proxy
        /// visual rather than silently dropping the projectile.
        /// </summary>
        public void OnSpawnConfirmed(SpawnConfirmation conf)
        {
            bool isLocal = conf.OwnerMidId == _localPlayerMidId;
            for (int i = 0; i < conf.ProjectileCount; i++)
            {
                uint realId = conf.BaseProjId + (uint)i;
                if (isLocal && _pendingTempIds.Count > 0)
                {
                    uint tempId = _pendingTempIds.Dequeue();
                    if (_predictions.ContainsKey(tempId))
                    {
                        LinkPredictionId(tempId, realId, conf.ServerNetworkTime);
                    }
                    else
                    {
                        Log($"TempId={tempId} prediction expired before confirmation; " +
                            $"spawning proxy visual for realId={realId}");
                        SpawnPredictionVisual(realId, conf, i);
                        FastForwardProxyVisual(realId, conf.ServerNetworkTime);
                    }
                }
                else
                {
                    SpawnPredictionVisual(realId, conf, i);
                    FastForwardProxyVisual(realId, conf.ServerNetworkTime);
                }
            }
        }

        public void OnHitConfirmed(HitConfirmation confirmation)
        {
            if (!_predictions.TryGetValue(confirmation.ProjId, out var pred)) return;
            pred.IsConfirmedHit       = true;
            pred.ConfirmedHitPosition = confirmation.HitPosition;
        }

        public void ReconcileSnapshot(
            ProjectileSnapshot2D[] snapshots2D, int count2D,
            ProjectileSnapshot3D[] snapshots3D, int count3D)
        {
            for (int i = 0; i < count2D; i++)
                ReconcileOne(snapshots2D[i].ProjId,
                    new Vector3(snapshots2D[i].X, snapshots2D[i].Y, 0f),
                    snapshots2D[i].ServerTick);
            for (int i = 0; i < count3D; i++)
                ReconcileOne(snapshots3D[i].ProjId,
                    new Vector3(snapshots3D[i].X, snapshots3D[i].Y, snapshots3D[i].Z),
                    snapshots3D[i].ServerTick);
        }

        #endregion

        #region Update — Visual Loop

        private void Update()
        {
            if (_predictions.Count == 0) return;

            float now      = Time.time;
            var   toRemove = new List<uint>(8);

            foreach (var kvp in _predictions)
            {
                var pred = kvp.Value;
                if (pred.VisualObject == null) { toRemove.Add(kvp.Key); continue; }

                // ── Confirmed hit (same for all modes) ───────────────────────
                if (pred.IsConfirmedHit)
                {
                    pred.VisualObject.transform.position = Vector3.MoveTowards(
                        pred.VisualObject.transform.position,
                        pred.ConfirmedHitPosition,
                        pred.Speed * Time.deltaTime);
                    if (Vector3.Distance(
                            pred.VisualObject.transform.position,
                            pred.ConfirmedHitPosition) < 0.05f)
                    {
                        ReturnPredictionVisual(pred);
                        toRemove.Add(kvp.Key);
                    }
                    continue;
                }

                // ── DeterministicMath path (Wave / Circular) ─────────────────
                if (pred.PredictionMode == PredictionMode.DeterministicMath)
                {
                    float timeAlive = Mathf.Max(0f,
                        GetApproxServerTime() - pred.ServerSpawnNetworkTime);

                    if (timeAlive >= pred.MaxLifetime)
                    {
                        ReturnPredictionVisual(pred);
                        toRemove.Add(kvp.Key);
                        continue;
                    }

                    Vector3 targetPos = ComputeDeterministicPosition(pred, timeAlive);
                    Vector3 velDir    = ComputeDeterministicVelocityDir(pred, timeAlive);

                    if (pred.IsProxyProjectile)
                    {
                        pred.VisualObject.transform.position = Vector3.Lerp(
                            pred.VisualObject.transform.position, targetPos,
                            Time.deltaTime * 15f);
                    }
                    else
                    {
                        pred.VisualObject.transform.position = targetPos;
                    }

                    if (velDir.sqrMagnitude > 0.001f)
                    {
                        Quaternion targetRot = pred.Is3D
                            ? DeterministicMotionMath.CalculateLookRotation3D(velDir)
                            : DeterministicMotionMath.CalculateLookRotation2D(velDir);

                        pred.VisualObject.transform.rotation = pred.IsProxyProjectile
                            ? Quaternion.Slerp(pred.VisualObject.transform.rotation,
                                targetRot, Time.deltaTime * 25f)
                            : targetRot;
                    }
                    continue;
                }

                // ── Snapshot-chase path (Guided, Teleport) ────────────────────
                // FIX: These movement types don't follow straight-line paths.
                // Linear extrapolation causes zig-zag and "goes back" artifacts.
                // Instead, chase the latest server snapshot position.
                if (pred.CachedMovementType == (byte)ProjectileMovementType.Guided ||
                    pred.CachedMovementType == (byte)ProjectileMovementType.Teleport)
                {
                    float elapsed = now - pred.SpawnTime;
                    if (elapsed >= pred.MaxLifetime)
                    {
                        ReturnPredictionVisual(pred);
                        toRemove.Add(kvp.Key);
                        continue;
                    }
                    UpdateSnapshotChaseVisual(pred, elapsed, now);
                    continue;
                }

                // ── Linear mode lifetime check ────────────────────────────────
                if (now - pred.SpawnTime >= pred.MaxLifetime)
                {
                    ReturnPredictionVisual(pred);
                    toRemove.Add(kvp.Key);
                    continue;
                }

                // ── Linear / Parabolic prediction (Straight and Arching) ──────
                float elapsedLinear   = now - pred.SpawnTime;
                Vector3 predicted;
                Vector3 currentVelDir = pred.Direction;

                if (pred.CachedMovementType == (byte)ProjectileMovementType.Arching
                    && pred.GravityAy != 0f)
                {
                    // FIX: Parabolic integration matching the Rust semi-implicit Euler:
                    //   pos(t) = origin + dir*speed*t + (0, 0.5*g*t², 0)
                    // This eliminates the per-snapshot drift that triggered constant
                    // reconciliation for arching projectiles with significant gravity.
                    Vector3 basePos  = pred.Origin + pred.Direction * pred.Speed * elapsedLinear;
                    float   gravDisp = 0.5f * pred.GravityAy * elapsedLinear * elapsedLinear;
                    predicted = new Vector3(basePos.x, basePos.y + gravDisp, basePos.z);

                    // Update rotation to match current velocity direction (not fixed initial dir).
                    // vel(t) = dir*speed + (0, g*t, 0)
                    float vyNow = pred.Direction.y * pred.Speed + pred.GravityAy * elapsedLinear;
                    Vector3 velNow = new Vector3(
                        pred.Direction.x * pred.Speed,
                        vyNow,
                        pred.Direction.z * pred.Speed);
                    if (velNow.sqrMagnitude > 0.001f)
                        currentVelDir = velNow.normalized;
                }
                else
                {
                    // Straight: simple linear extrapolation.
                    predicted = pred.Origin + pred.Direction * pred.Speed * elapsedLinear;
                }

                if (pred.History != null)
                {
                    pred.History.Add(new ProjectileStatePayload
                    {
                        ServerTick        = GetApproxServerTick(),
                        PredictedPosition = predicted
                    });
                }

                Vector3 linearDisplayPos;
                if (pred.IsReconciling)
                {
                    float t = Mathf.Clamp01(
                        (now - pred.ReconcileStartTime) / pred.ReconcileDuration);
                    linearDisplayPos = Vector3.Lerp(
                        pred.ReconcileStartPosition, pred.ReconcileTarget, t);

                    if (t >= 1f)
                    {
                        pred.IsReconciling = false;
                        // Rebase origin so prediction continues forward from corrected spot.
                        pred.Origin = pred.ReconcileTarget
                                    - pred.Direction * pred.Speed * elapsedLinear;
                    }
                }
                else
                {
                    linearDisplayPos = predicted;
                }

                pred.VisualObject.transform.position = linearDisplayPos;
                // FIX: use gravity-corrected velocity direction for arching rotation.
                ApplyDirectionRotation(pred.VisualObject.transform, currentVelDir);
            }

            foreach (var id in toRemove) _predictions.Remove(id);
        }

        #endregion

        #region Snapshot Chase (Guided / Teleport)

        /// <summary>
        /// Moves the visual toward the latest server snapshot position, extrapolated
        /// forward at projectile speed using the direction estimated from consecutive
        /// snapshots. Avoids the zig-zag caused by linear-vs-curved path mismatch.
        ///
        /// Before the first snapshot arrives, falls back to straight-line extrapolation
        /// from spawn (same as before, but only for a brief period until first snapshot).
        /// </summary>
        private void UpdateSnapshotChaseVisual(
            PredictedProjectile pred, float elapsed, float now)
        {
            if (pred.VisualObject == null) return;

            Vector3 targetPos;
            Vector3 velDir;

            if (pred.HasSnapshot)
            {
                // Extrapolate from latest known snapshot position using snapshot velocity.
                float timeSinceSnap = Mathf.Max(0f, now - pred.LastSnapshotTime);
                targetPos = pred.LastSnapshotPos
                          + pred.LastSnapshotVelDir * pred.Speed * timeSinceSnap;
                velDir    = pred.LastSnapshotVelDir;
            }
            else
            {
                // No snapshot yet — use linear extrapolation from spawn origin.
                // This is only used in the first snapshot interval (~80ms at 50Hz).
                targetPos = pred.Origin + pred.Direction * pred.Speed * elapsed;
                velDir    = pred.Direction;
            }

            // Smooth lerp — absorbs clock drift without jarring snaps.
            pred.VisualObject.transform.position = Vector3.Lerp(
                pred.VisualObject.transform.position, targetPos,
                Time.deltaTime * _snapshotChaseLerp);

            if (velDir.sqrMagnitude > 0.001f)
                ApplyDirectionRotation(pred.VisualObject.transform, velDir);
        }

        #endregion

        #region Spawn / Link

        private void SpawnPredictionVisual(
            uint projId, SpawnConfirmation conf, int dirIndex = 0)
        {
            var cfg = ProjectileRegistry.Instance?.Get(conf.ConfigId);
            if (cfg == null) return;

            Vector3 dir = conf.GetDirection(dirIndex).normalized;
            if (dir.sqrMagnitude < 0.001f) dir = Vector3.right;

            PoolableObjectType poolType = cfg.Is3D ? _visualPoolType3D : _visualPoolType2D;
            var obj = LocalObjectPool.Instance?.GetObject(
                poolType, conf.Origin, GetDirectionRotation(dir));
            if (obj == null)
            {
                LogWarning($"Pool null for {poolType}, projId={projId}");
                return;
            }

            var vis = obj.GetComponent<ProjectileVisualBase>();
            vis?.InitializeClientVisual(conf.ConfigId, conf.Origin, dir, conf.Speed);

            // ── Determine prediction mode ──────────────────────────────────────
            bool isDeterministic = cfg.MovementType == ProjectileMovementType.Wave
                                || cfg.MovementType == ProjectileMovementType.Circular;
            PredictionMode mode  = isDeterministic
                ? PredictionMode.DeterministicMath
                : PredictionMode.Linear;

            // ── Initial velocity components ────────────────────────────────────
            float velX = dir.x * conf.Speed;
            float velY = dir.y * conf.Speed;
            float velZ = dir.z * conf.Speed;

            // ── Perpendicular axis ─────────────────────────────────────────────
            Vector3 perpAxis = cfg.Is3D
                ? DeterministicMotionMath.ComputePerpAxis3D(dir)
                : DeterministicMotionMath.ComputePerpAxis2D(dir);

            // ── Cache wave / circular params ───────────────────────────────────
            float waveAmp = 0f, waveFreq = 0f, wavePhase = 0f;
            float circOmegaRad = 0f, circStartRad = 0f, circRadius = 0f;

            if (isDeterministic)
            {
                if (cfg.MovementType == ProjectileMovementType.Wave)
                {
                    waveAmp   = cfg.WaveAmplitude;
                    waveFreq  = cfg.WaveFrequency;
                    wavePhase = cfg.WavePhaseOffset;
                }
                else
                {
                    circOmegaRad = cfg.CircularAngularSpeed * Mathf.Deg2Rad;
                    circStartRad = cfg.CircularStartAngle   * Mathf.Deg2Rad;
                    circRadius   = cfg.CircularRadius;
                }
            }

            // ── Clock anchor ───────────────────────────────────────────────────
            float serverNetTime = conf.ServerNetworkTime > 0f
                ? conf.ServerNetworkTime
                : GetApproxServerTime();

            bool isProxy = conf.OwnerMidId != _localPlayerMidId;

            _predictions[projId] = new PredictedProjectile
            {
                BaseProjId      = conf.BaseProjId,
                ProjId          = projId,
                ConfigId        = conf.ConfigId,
                Is3D            = cfg.Is3D,
                Origin          = conf.Origin,
                Direction       = dir,
                Speed           = conf.Speed,
                SpawnTime       = Time.time,
                ServerSpawnTick = conf.ServerSpawnTick,
                VisualObject    = obj,
                VisualScript    = vis,
                UsedPoolType    = poolType,

                PredictionMode         = mode,
                CachedMovementType     = (byte)cfg.MovementType,
                ServerSpawnNetworkTime = serverNetTime,
                IsProxyProjectile      = isProxy,

                InitialVelX = velX,
                InitialVelY = velY,
                InitialVelZ = velZ,

                CachedWaveAmplitude   = waveAmp,
                CachedWaveFrequency   = waveFreq,
                CachedWavePhaseOffset = wavePhase,

                CachedCircularAngularSpeedRad = circOmegaRad,
                CachedCircularStartAngleRad   = circStartRad,
                CachedCircularRadius          = circRadius,

                PerpAxisX = perpAxis.x,
                PerpAxisY = perpAxis.y,
                PerpAxisZ = perpAxis.z,

                // FIX: Arching gravity from config
                GravityAy = cfg.GravityScale,

                // FIX: Snapshot-chase initial state for Guided/Teleport
                LastSnapshotVelDir = dir,   // initial direction guess before first snapshot
                LastSnapshotPos    = Vector3.zero,
                LastSnapshotTime   = 0f,
                HasSnapshot        = false,

                History       = mode == PredictionMode.Linear
                    ? new CircularBuffer<ProjectileStatePayload>(_historySize)
                    : null,
                IsReconciling = false,
                MaxLifetime   = cfg.Lifetime,
                IsConfirmedHit = false,
            };
        }

        private void LinkPredictionId(uint tempId, uint realId, float serverNetworkTime)
        {
            if (!_predictions.TryGetValue(tempId, out var pred))
            {
                LogWarning(
                    $"LinkPredictionId: tempId={tempId} missing unexpectedly. realId={realId}");
                return;
            }
            _predictions.Remove(tempId);
            pred.ProjId     = realId;
            pred.BaseProjId = realId;

            if (serverNetworkTime > 0f)
                pred.ServerSpawnNetworkTime = serverNetworkTime;

            _predictions[realId] = pred;
            Log($"Linked temp={tempId} → server={realId}");
        }

        /// <summary>
        /// Immediately positions a newly-spawned proxy visual at its current
        /// deterministic location. Only applies to DeterministicMath mode (Wave/Circular).
        /// Guided/Teleport snapshots handle their own catch-up via the snapshot-chase path.
        /// </summary>
        private void FastForwardProxyVisual(uint projId, float serverNetworkTime)
        {
            if (serverNetworkTime <= 0f) return;
            if (!_predictions.TryGetValue(projId, out var pred)) return;
            if (pred.PredictionMode != PredictionMode.DeterministicMath) return;
            if (pred.VisualObject == null) return;

            float catchUpTime = Mathf.Max(0f, GetApproxServerTime() - serverNetworkTime);
            if (catchUpTime < 0.02f) return;
            catchUpTime = Mathf.Min(catchUpTime, pred.MaxLifetime);

            pred.VisualObject.transform.position =
                ComputeDeterministicPosition(pred, catchUpTime);

            Vector3 velDir = ComputeDeterministicVelocityDir(pred, catchUpTime);
            if (velDir.sqrMagnitude > 0.001f)
            {
                pred.VisualObject.transform.rotation = pred.Is3D
                    ? DeterministicMotionMath.CalculateLookRotation3D(velDir)
                    : DeterministicMotionMath.CalculateLookRotation2D(velDir);
            }
        }

        #endregion

        #region Reconciliation

        private void ReconcileOne(uint projId, Vector3 serverPos, int serverTick)
        {
            if (!_predictions.TryGetValue(projId, out var pred)) return;

            // DeterministicMath projectiles don't need snapshot reconciliation.
            if (pred.PredictionMode == PredictionMode.DeterministicMath)
                return;

            // ── FIX: Guided and Teleport — update snapshot data, don't reconcile. ──
            // Standard linear reconciliation causes zig-zag because these types
            // don't follow straight-line paths. Instead, store the server position
            // and estimate direction from consecutive snapshots for the chase path.
            if (pred.CachedMovementType == (byte)ProjectileMovementType.Guided ||
                pred.CachedMovementType == (byte)ProjectileMovementType.Teleport)
            {
                // Estimate velocity direction from delta between consecutive snapshots.
                if (pred.HasSnapshot)
                {
                    Vector3 delta = serverPos - pred.LastSnapshotPos;
                    if (delta.sqrMagnitude > 0.0001f)
                        pred.LastSnapshotVelDir = delta.normalized;
                    // If delta is tiny (projectile barely moved), keep previous direction.
                }
                else
                {
                    // First snapshot: use initial fire direction as best guess.
                    pred.LastSnapshotVelDir = pred.Direction;
                }

                pred.LastSnapshotPos  = serverPos;
                pred.LastSnapshotTime = Time.time;
                pred.HasSnapshot      = true;
                return;
            }

            // ── Linear prediction reconciliation (Straight and Arching) ─────────
            // Arching prediction is now parabolic so errors should be small —
            // reconciliation fires much less often than before.
            Vector3 ourPredicted;
            if (pred.History != null
                && pred.History.TryFindLatest(s => s.ServerTick <= serverTick, out var state))
                ourPredicted = state.PredictedPosition;
            else
                ourPredicted = pred.VisualObject != null
                    ? pred.VisualObject.transform.position
                    : pred.Origin;

            float error = Vector3.Distance(serverPos, ourPredicted);
            if (error < _reconcileThreshold) return;

            if (error > _hardSnapThreshold)
            {
                if (pred.VisualObject != null)
                    pred.VisualObject.transform.position = serverPos;
                float el   = Time.time - pred.SpawnTime;
                pred.Origin        = serverPos - pred.Direction * pred.Speed * el;
                pred.IsReconciling = false;
                return;
            }

            pred.IsReconciling          = true;
            pred.ReconcileStartPosition = pred.VisualObject != null
                ? pred.VisualObject.transform.position : ourPredicted;
            pred.ReconcileTarget        = serverPos;
            pred.ReconcileStartTime     = Time.time;
            pred.ReconcileDuration      = _reconcileDuration;
        }

        #endregion

        #region Cleanup

        private void ReturnPredictionVisual(PredictedProjectile pred)
        {
            if (pred.VisualScript != null)
                pred.VisualScript.ReturnToPoolImmediate();
            else if (pred.VisualObject != null)
                LocalObjectPool.Instance?.ReturnObject(
                    pred.VisualObject, pred.UsedPoolType);
            pred.VisualObject = null;
            pred.VisualScript = null;
        }

        #endregion

        #region Deterministic Position / Velocity Helpers

        private static Vector3 ComputeDeterministicPosition(
            PredictedProjectile pred, float timeAlive)
        {
            timeAlive = Mathf.Clamp(timeAlive, 0f, pred.MaxLifetime);
            var perp = new Vector3(pred.PerpAxisX, pred.PerpAxisY, pred.PerpAxisZ);

            switch ((ProjectileMovementType)pred.CachedMovementType)
            {
                case ProjectileMovementType.Circular:
                    if (pred.Is3D)
                    {
                        return DeterministicMotionMath.CalculateCircular3DPosition(
                            pred.Origin,
                            new Vector3(pred.InitialVelX, pred.InitialVelY, pred.InitialVelZ),
                            pred.CachedCircularAngularSpeedRad,
                            pred.CachedCircularStartAngleRad,
                            perp,
                            pred.CachedCircularRadius,
                            timeAlive);
                    }
                    return DeterministicMotionMath.CalculateCircular2DPosition(
                        pred.Origin,
                        pred.InitialVelX, pred.InitialVelY,
                        pred.CachedCircularAngularSpeedRad,
                        pred.CachedCircularStartAngleRad,
                        timeAlive);

                case ProjectileMovementType.Wave:
                    if (pred.Is3D)
                    {
                        return DeterministicMotionMath.CalculateWave3DPosition(
                            pred.Origin,
                            new Vector3(pred.InitialVelX, pred.InitialVelY, pred.InitialVelZ),
                            pred.CachedWaveAmplitude,
                            pred.CachedWaveFrequency,
                            pred.CachedWavePhaseOffset,
                            perp,
                            timeAlive);
                    }
                    return DeterministicMotionMath.CalculateWave2DPosition(
                        pred.Origin,
                        pred.Direction.x, pred.Direction.y, pred.Speed,
                        pred.CachedWaveAmplitude,
                        pred.CachedWaveFrequency,
                        pred.CachedWavePhaseOffset,
                        pred.PerpAxisX, pred.PerpAxisY,
                        timeAlive);

                default:
                    return pred.Origin + pred.Direction * pred.Speed * timeAlive;
            }
        }

        private static Vector3 ComputeDeterministicVelocityDir(
            PredictedProjectile pred, float timeAlive)
        {
            timeAlive = Mathf.Clamp(timeAlive, 0f, pred.MaxLifetime);
            var perp = new Vector3(pred.PerpAxisX, pred.PerpAxisY, pred.PerpAxisZ);

            switch ((ProjectileMovementType)pred.CachedMovementType)
            {
                case ProjectileMovementType.Circular:
                    if (pred.Is3D)
                    {
                        return DeterministicMotionMath.CalculateCircular3DVelocityDirection(
                            new Vector3(pred.InitialVelX, pred.InitialVelY, pred.InitialVelZ),
                            pred.CachedCircularAngularSpeedRad,
                            pred.CachedCircularStartAngleRad,
                            perp,
                            pred.CachedCircularRadius,
                            timeAlive);
                    }
                    return DeterministicMotionMath.CalculateCircular2DVelocityDirection(
                        pred.InitialVelX, pred.InitialVelY,
                        pred.CachedCircularAngularSpeedRad,
                        pred.CachedCircularStartAngleRad,
                        timeAlive);

                case ProjectileMovementType.Wave:
                    if (pred.Is3D)
                    {
                        return DeterministicMotionMath.CalculateWave3DVelocityDirection(
                            new Vector3(pred.InitialVelX, pred.InitialVelY, pred.InitialVelZ),
                            pred.CachedWaveAmplitude,
                            pred.CachedWaveFrequency,
                            pred.CachedWavePhaseOffset,
                            perp,
                            timeAlive);
                    }
                    return DeterministicMotionMath.CalculateWave2DVelocityDirection(
                        pred.Direction.x, pred.Direction.y, pred.Speed,
                        pred.CachedWaveAmplitude,
                        pred.CachedWaveFrequency,
                        pred.CachedWavePhaseOffset,
                        pred.PerpAxisX, pred.PerpAxisY,
                        timeAlive);

                default:
                    return pred.Direction * pred.Speed;
            }
        }

        #endregion

        #region Rotation Utilities

        public static Quaternion GetDirectionRotation(Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.001f) return Quaternion.identity;
            if (Mathf.Abs(dir.z) < 0.01f)
                return Quaternion.Euler(
                    0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
            Vector3 up = Mathf.Abs(Vector3.Dot(dir.normalized, Vector3.up)) > 0.99f
                ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(dir.normalized, up);
        }

        public static void ApplyDirectionRotation(Transform t, Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.001f) return;
            t.rotation = GetDirectionRotation(dir);
        }

        #endregion

        #region Clock Helpers

        private static float GetApproxServerTime()
            => NetworkManager.Singleton != null
                ? (float)NetworkManager.Singleton.ServerTime.TimeAsFloat
                : Time.time;

        private static int GetApproxServerTick()
            => NetworkManager.Singleton != null
                ? NetworkManager.Singleton.ServerTime.Tick
                : Mathf.RoundToInt(Time.time * 50f);

        #endregion

        #region Logging

        private void Log(string m)
        {
            if (_enableLogs) MID_HelperFunctions.LogDebug(m, nameof(ClientPredictionManager));
        }

        private void LogWarning(string m)
            => MID_HelperFunctions.LogWarning(m, nameof(ClientPredictionManager));

        #endregion
    }
}
