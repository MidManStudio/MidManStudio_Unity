// ClientPredictionManager.cs
//
// REWRITE — Deterministic Client-Local Special Movement Prediction.
//
// FIX (host mode — _pendingTempIds accumulation):
//   SpawnImmediatePrediction and SpawnLocalPhysicsVisual now return immediately
//   when IsServer is true. On the host, ServerProjectileAuthority.LateUpdate
//   renders directly from the Rust buffer. Creating prediction visuals on the
//   host produces double visuals AND causes _pendingTempIds to grow without
//   bound because SpawnConfirmedClientRpc always returns early for IsServer,
//   so the queue entries are never consumed.
//
// FIX (expired prediction → dropped visual):
//   OnSpawnConfirmed now checks _predictions.ContainsKey(tempId) before
//   calling LinkPredictionId. If the temp prediction expired before the RPC
//   arrived (typical under receive queue overflow — see transport config in
//   MID_ProjectileNetworkBridge.ConfigureTransportForHighThroughput), the
//   method falls back to spawning a fresh proxy visual at the server-confirmed
//   position rather than silently dropping the projectile visual entirely.
//   This means the LinkPredictionId "not found" warning is now an unexpected
//   state (should not fire from normal flow) rather than a routine occurrence.
//
// PROBLEM FIXED (wave/circular zig-zag):
//   Wave and Circular projectiles showed zig-zag on proxy clients and a
//   straight-then-choppy transition for the shooter. Root cause: the old
//   snapshot-velocity-estimation path computed velocity as Δposition/Δtime
//   between snapshot intervals. Over 4 ticks of curved motion the position
//   delta points along a chord, not the tangent — rapidly oscillating the
//   estimated velocity and producing zig-zag.
//
// SOLUTION — PredictionMode.DeterministicMath:
//   For MOVE_WAVE and MOVE_CIRCULAR, each client independently computes the
//   projectile's position using the exact closed-form integral that matches
//   the Rust simulation's differential equations (see DeterministicMotionMath).
//   The server's NetworkTime.TimeAsFloat captured at spawn (ServerNetworkTime
//   in SpawnConfirmation) serves as the shared t=0 clock anchor.
//
//   Shooter:     position set directly each frame — analytically exact, zero
//                dependency on server snapshots.
//   Proxy client: position lerped toward deterministic target at factor 15/s —
//                absorbs residual NGO clock drift only, not an interpolation delay.
//   Snapshots:   still sent for all types; DeterministicMath silently ignores
//                them. Linear mode (Straight/Arching/Guided) reconciliation unchanged.
//
// UNCHANGED:
//   Linear prediction and snapshot reconciliation for all non-Wave/Circular types.
//   Host rendering path (ServerProjectileAuthority.LateUpdate), collision detection,
//   impact effects, hit confirmation, physics and raycast visual paths.

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
    //  Internal helpers (unchanged)
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
        /// Linear prediction + threshold-based snapshot reconciliation.
        /// Used for Straight, Arching, Guided, Teleport.
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
        /// Server NetworkTime.TimeAsFloat at spawn. t=0 for all deterministic math.
        /// Set from SpawnConfirmation.ServerNetworkTime; falls back to client-side
        /// estimate for SpawnImmediatePrediction, then corrected by LinkPredictionId.
        public float ServerSpawnNetworkTime;

        // ── Cached movement type (avoids per-frame registry lookup) ───────────
        public byte CachedMovementType;

        // ── Initial velocity components ───────────────────────────────────────
        /// Direction.normalized * Speed, stored pre-computed for circular math.
        public float InitialVelX, InitialVelY, InitialVelZ;

        // ── Cached wave parameters ────────────────────────────────────────────
        public float CachedWaveAmplitude;
        public float CachedWaveFrequency;
        public float CachedWavePhaseOffset;

        // ── Cached circular parameters ────────────────────────────────────────
        /// Angular speed in RADIANS/sec (converted from degrees at spawn).
        public float CachedCircularAngularSpeedRad;
        /// Start angle in RADIANS (converted from degrees at spawn).
        public float CachedCircularStartAngleRad;
        /// Explicit orbit radius (used for 3D circular only; 2D uses speed/omega).
        public float CachedCircularRadius;

        // ── Pre-computed perpendicular axis ───────────────────────────────────
        /// Matches BatchSpawnHelper.GetAccel2D/3D for Wave/Circular spawn.
        public float PerpAxisX, PerpAxisY, PerpAxisZ;

        // ── Proxy flag ────────────────────────────────────────────────────────
        /// True for projectiles owned by other players. Controls lerp vs direct-set.
        public bool IsProxyProjectile;

        // ── Visual ────────────────────────────────────────────────────────────
        public GameObject           VisualObject;
        public ProjectileVisualBase VisualScript;
        public PoolableObjectType   UsedPoolType;

        // ── Linear prediction (PredictionMode.Linear only) ───────────────────
        /// Null for DeterministicMath mode — not needed.
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

        [Header("Reconciliation (Linear mode only)")]
        [Tooltip("Position error below which reconciliation is skipped for linear projectiles.")]
        [SerializeField] private float _reconcileThreshold = 0.3f;
        [Tooltip("Error above which a hard-snap is used instead of smooth blending.")]
        [SerializeField] private float _hardSnapThreshold  = 3f;
        [Tooltip("Seconds to blend the visual toward the server-correct position.")]
        [SerializeField] private float _reconcileDuration  = 0.12f;

        [Header("History Buffer (Linear mode)")]
        [SerializeField] private int _historySize = 32;

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
        /// ServerNetworkTime in <paramref name="tempConf"/> will be 0 (client-side);
        /// falls back to current server time as initial anchor, then corrected
        /// by LinkPredictionId when SpawnConfirmedClientRpc arrives.
        ///
        /// FIX: skipped in host mode — the host renders directly from
        /// ServerProjectileAuthority.LateUpdate (Rust buffer). Creating prediction
        /// visuals on the host would cause double visuals and _pendingTempIds
        /// accumulation because SpawnConfirmedClientRpc returns early for IsServer.
        /// </summary>
        public void SpawnImmediatePrediction(SpawnConfirmation tempConf)
        {
            // Host mode: server renders directly; predictions would create double
            // visuals and _pendingTempIds would grow without bound.
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
        /// Does NOT queue a temp ID — physics never sends SpawnConfirmedClientRpc.
        ///
        /// FIX: skipped in host mode — physics projectiles are server-owned
        /// NetworkObjects; NetworkTransform handles visual sync for the host.
        /// </summary>
        public void SpawnLocalPhysicsVisual(
            ushort configId, Vector3 origin, Vector3 dir, float speed)
        {
            // Host mode: NetworkTransform on the physics projectile drives
            // the visual — no separate prediction visual needed.
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
                ServerNetworkTime   = 0f   // falls back to GetApproxServerTime() in spawn
            }, 0);
            // NOT queued — standalone, no server confirmation expected.
        }

        #endregion

        #region Public API — Bridge Callbacks

        /// <summary>
        /// Called by MID_ProjectileNetworkBridge when server confirms a Rust sim spawn.
        /// Local player with pending temp IDs: links the visual to the real projId.
        /// Everyone else (proxy): spawns a fresh visual at the correct server position.
        ///
        /// FIX: if the dequeued temp prediction expired before this RPC arrived
        /// (typically caused by UnityTransport receive queue overflow delaying the RPC
        /// past MaxLifetime), fall back to spawning a fresh proxy visual at the
        /// server-confirmed position rather than silently dropping the projectile.
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
                        // Normal path: prediction still alive, link it.
                        LinkPredictionId(tempId, realId, conf.ServerNetworkTime);
                    }
                    else
                    {
                        // The temp prediction expired before SpawnConfirmedClientRpc
                        // arrived. This happens when the receive queue is full and the
                        // RPC is delayed past the projectile's MaxLifetime.
                        // Increase UnityTransport.RecvQueueCapacity (see
                        // MID_ProjectileNetworkBridge.ConfigureTransportForHighThroughput).
                        // Fallback: spawn a fresh proxy visual at the server position.
                        Log($"TempId={tempId} prediction expired before confirmation; " +
                            $"spawning proxy visual for realId={realId}");
                        SpawnPredictionVisual(realId, conf, i);
                        FastForwardProxyVisual(realId, conf.ServerNetworkTime);
                    }
                }
                else
                {
                    // Proxy clients, or local player with no pending predictions.
                    SpawnPredictionVisual(realId, conf, i);
                    // Immediately jump proxy visual to current deterministic position.
                    // Without this it appears at spawn origin and sweeps forward over
                    // several frames — highly visible for fast projectiles.
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

                    // Shooter: set directly — analytically exact, no lerp needed.
                    // Proxy:   gentle lerp to absorb residual NGO clock drift only.
                    //          15f/s corrects ~1 world unit of drift in ~0.5 s.
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

                        // 25f/s slerp for proxies tracks fast direction changes on arcs.
                        pred.VisualObject.transform.rotation = pred.IsProxyProjectile
                            ? Quaternion.Slerp(pred.VisualObject.transform.rotation,
                                targetRot, Time.deltaTime * 25f)
                            : targetRot;
                    }
                    continue;
                }

                // ── Linear mode lifetime check ────────────────────────────────
                if (now - pred.SpawnTime >= pred.MaxLifetime)
                {
                    ReturnPredictionVisual(pred);
                    toRemove.Add(kvp.Key);
                    continue;
                }

                // ── Linear prediction (Straight / Arching / Guided / Teleport) ─
                float elapsed   = now - pred.SpawnTime;
                Vector3 predicted = pred.Origin + pred.Direction * pred.Speed * elapsed;

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
                        // Rebase origin so prediction continues forward from corrected spot
                        pred.Origin = pred.ReconcileTarget
                                    - pred.Direction * pred.Speed * elapsed;
                    }
                }
                else
                {
                    linearDisplayPos = predicted;
                }

                pred.VisualObject.transform.position = linearDisplayPos;
                ApplyDirectionRotation(pred.VisualObject.transform, pred.Direction);
            }

            foreach (var id in toRemove) _predictions.Remove(id);
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

            // ── Determine prediction mode ─────────────────────────────────────
            bool isDeterministic = cfg.MovementType == ProjectileMovementType.Wave
                                || cfg.MovementType == ProjectileMovementType.Circular;
            PredictionMode mode  = isDeterministic
                ? PredictionMode.DeterministicMath
                : PredictionMode.Linear;

            // ── Initial velocity components ───────────────────────────────────
            float velX = dir.x * conf.Speed;
            float velY = dir.y * conf.Speed;
            float velZ = dir.z * conf.Speed;

            // ── Perpendicular axis — MUST match BatchSpawnHelper.GetAccel2D/3D ─
            Vector3 perpAxis = cfg.Is3D
                ? DeterministicMotionMath.ComputePerpAxis3D(dir)
                : DeterministicMotionMath.ComputePerpAxis2D(dir);

            // ── Cache wave / circular params to avoid per-frame lookups ────────
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
                else // Circular
                {
                    circOmegaRad = cfg.CircularAngularSpeed * Mathf.Deg2Rad;
                    circStartRad = cfg.CircularStartAngle   * Mathf.Deg2Rad;
                    circRadius   = cfg.CircularRadius;
                }
            }

            // ── Clock anchor ──────────────────────────────────────────────────
            // For SpawnImmediatePrediction calls, conf.ServerNetworkTime == 0.
            // Use current server time as initial estimate; LinkPredictionId will
            // update it to the server-confirmed value when the RPC arrives.
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

                // Prediction mode
                PredictionMode         = mode,
                CachedMovementType     = (byte)cfg.MovementType,
                ServerSpawnNetworkTime = serverNetTime,
                IsProxyProjectile      = isProxy,

                // Initial velocity
                InitialVelX = velX,
                InitialVelY = velY,
                InitialVelZ = velZ,

                // Wave params (zero if not wave)
                CachedWaveAmplitude   = waveAmp,
                CachedWaveFrequency   = waveFreq,
                CachedWavePhaseOffset = wavePhase,

                // Circular params (zero if not circular)
                CachedCircularAngularSpeedRad = circOmegaRad,
                CachedCircularStartAngleRad   = circStartRad,
                CachedCircularRadius          = circRadius,

                // Perpendicular axis
                PerpAxisX = perpAxis.x,
                PerpAxisY = perpAxis.y,
                PerpAxisZ = perpAxis.z,

                // Linear mode fields (null history for DeterministicMath saves alloc)
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
                // OnSpawnConfirmed now checks ContainsKey before calling here,
                // so this branch represents an unexpected state — should not fire
                // in normal operation. If it does, there is a race between Update
                // removing the prediction and OnSpawnConfirmed's ContainsKey check.
                LogWarning(
                    $"LinkPredictionId: tempId={tempId} missing unexpectedly. realId={realId}");
                return;
            }
            _predictions.Remove(tempId);
            pred.ProjId     = realId;
            pred.BaseProjId = realId;

            // Update clock anchor to the server's confirmed spawn time.
            // Corrects the initial estimate set during SpawnImmediatePrediction,
            // keeping the shooter's visual aligned with the server's t=0.
            if (serverNetworkTime > 0f)
                pred.ServerSpawnNetworkTime = serverNetworkTime;

            _predictions[realId] = pred;
            Log($"Linked temp={tempId} → server={realId}");
        }

        /// <summary>
        /// Immediately positions a newly-spawned proxy visual at its current
        /// deterministic location. Prevents the "spawns at origin then sweeps
        /// forward" artifact visible for fast projectiles with high latency.
        /// </summary>
        private void FastForwardProxyVisual(uint projId, float serverNetworkTime)
        {
            if (serverNetworkTime <= 0f) return;
            if (!_predictions.TryGetValue(projId, out var pred)) return;
            if (pred.PredictionMode != PredictionMode.DeterministicMath) return;
            if (pred.VisualObject == null) return;

            float catchUpTime = Mathf.Max(0f, GetApproxServerTime() - serverNetworkTime);
            if (catchUpTime < 0.02f) return;  // negligibly recent — skip
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
            // The closed-form formula is analytically exact — snapshots are intentionally
            // ignored for Wave and Circular types to prevent velocity-estimation zig-zag.
            if (pred.PredictionMode == PredictionMode.DeterministicMath)
                return;

            // ── Linear prediction reconciliation ─────────────────────────────
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

            // Smooth correction: blend from current visual position to server truth
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

        /// <summary>
        /// Compute the closed-form position for a DeterministicMath projectile
        /// at <paramref name="timeAlive"/> seconds from its spawn.
        /// Dispatches to the correct DeterministicMotionMath method.
        /// </summary>
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
                    // Fallback (should not reach here in DeterministicMath mode)
                    return pred.Origin + pred.Direction * pred.Speed * timeAlive;
            }
        }

        /// <summary>
        /// Compute the instantaneous velocity direction for a DeterministicMath
        /// projectile at <paramref name="timeAlive"/> seconds. Used for rotation.
        /// </summary>
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

        /// <summary>
        /// Returns the server's NetworkTime as a float.
        /// Falls back to Time.time in offline/editor contexts.
        /// </summary>
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
