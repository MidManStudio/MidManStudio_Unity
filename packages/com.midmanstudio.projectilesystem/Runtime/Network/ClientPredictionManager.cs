// ClientPredictionManager.cs
// CHANGES:
//   + SpawnLocalPhysicsVisual(): standalone visual for physics fire on the client.
//     Does NOT add to _pendingTempIds — physics never sends SpawnConfirmedClientRpc
//     so there is nothing to link. The visual expires at cfg.Lifetime naturally.
//
//   + Snapshot-based extrapolation for non-linear movement types (Arching, Guided,
//     Wave, Circular). PredictedProjectile gains:
//       UseSnapshotExtrapolation — true for non-Straight, non-Teleport movement
//       HasSnapshot              — set after first ReconcileOne call
//       LastSnapshotPos          — server position at last snapshot
//       LastSnapshotVelocity     — estimated from Δpos/Δtime between snapshots
//       LastSnapshotTime         — Time.time of last snapshot
//
//     Before the first snapshot: identical to linear prediction (Origin + Dir*Speed*t).
//     After the first snapshot: displayPos = LastSnapshotPos + LastSnapshotVelocity * dt
//     where LastSnapshotVelocity is smoothly updated each snapshot from the actual
//     position change. This naturally follows curves, spirals, and arcs because the
//     velocity estimate tracks the real trajectory — eliminating zig-zag entirely.
//
//   + ReconcileOne for snapshot-based: updates velocity estimate and snapshot position;
//     skips threshold-gated reconciliation (every snapshot IS truth for these types).
//
//   + ReconcileOne for linear: retains the corrected forward-lerp reconciliation from
//     the previous version (ReconcileStartPosition → ReconcileTarget as t goes 0→1,
//     then origin rebased so prediction continues forward from the corrected spot).

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
    internal sealed class CircularBuffer<T>
    {
        private readonly T[] _buf; private int _head, _count;
        public int Count => _count;
        public CircularBuffer(int cap) => _buf = new T[cap];
        public void Add(T v) { _buf[_head] = v; _head = (_head + 1) % _buf.Length; if (_count < _buf.Length) _count++; }
        public T Get(int i) { if (i < 0 || i >= _count) throw new IndexOutOfRangeException(); return _buf[(_head - _count + i + _buf.Length) % _buf.Length]; }
        public bool TryFindLatest(Predicate<T> m, out T r) { for (int i = _count - 1; i >= 0; i--) { var v = Get(i); if (m(v)) { r = v; return true; } } r = default; return false; }
    }

    internal struct ProjectileStatePayload { public int ServerTick; public Vector3 PredictedPosition; }

    internal sealed class PredictedProjectile
    {
        public uint   BaseProjId, ProjId;
        public ushort ConfigId;
        public bool   Is3D;

        // ── Spawn state ───────────────────────────────────────────────────────
        public Vector3 Origin, Direction;
        public float   Speed, SpawnTime;
        public int     ServerSpawnTick;

        // ── Visual ────────────────────────────────────────────────────────────
        public GameObject           VisualObject;
        public ProjectileVisualBase VisualScript;
        public PoolableObjectType   UsedPoolType;

        // ── Linear prediction history (for straight movement reconciliation) ──
        public CircularBuffer<ProjectileStatePayload> History;

        // ── Linear reconciliation (for Straight / Teleport movements) ─────────
        public bool    IsReconciling;
        public Vector3 ReconcileStartPosition; // visual position when reconcile began
        public Vector3 ReconcileTarget;         // server position to smooth toward
        public float   ReconcileStartTime;
        public float   ReconcileDuration;

        // ── Snapshot-based extrapolation (for Wave/Circular/Arching/Guided) ───
        // Instead of linear prediction, we extrapolate from the last known server
        // position using a velocity estimated from consecutive snapshot deltas.
        // This naturally follows curves without zig-zag between snapshots.
        public bool    UseSnapshotExtrapolation;
        public bool    HasSnapshot;            // true after first snapshot arrives
        public Vector3 LastSnapshotPos;        // server position at last snapshot
        public Vector3 LastSnapshotVelocity;   // Δpos/Δtime from last two snapshots
        public float   LastSnapshotTime;       // Time.time of last snapshot

        // ── Lifetime / hit ────────────────────────────────────────────────────
        public float   MaxLifetime;
        public bool    IsConfirmedHit;
        public Vector3 ConfirmedHitPosition;
    }

    public sealed class ClientPredictionManager : MonoBehaviour
    {
        #region Config

        [Header("Reconciliation (linear movement only)")]
        [Tooltip("Position error below which reconciliation is skipped for straight projectiles.")]
        [SerializeField] private float _reconcileThreshold = 0.3f;
        [Tooltip("Error above which a hard-snap is used instead of smooth blending.")]
        [SerializeField] private float _hardSnapThreshold  = 3f;
        [Tooltip("Seconds to blend the visual toward the server-correct position.")]
        [SerializeField] private float _reconcileDuration  = 0.12f;

        [Header("History Buffer")]
        [SerializeField] private int _historySize = 32;

        [Header("Visual Pool Types")]
        [SerializeField] private PoolableObjectType _visualPoolType2D = PoolableObjectType.Projectile_Visual2D;
        [SerializeField] private PoolableObjectType _visualPoolType3D = PoolableObjectType.Projectile_Visual3D;

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
        /// Adds the temp ID to _pendingTempIds so OnSpawnConfirmed can link it
        /// to the server-assigned projId when the confirmation arrives.
        /// </summary>
        public void SpawnImmediatePrediction(SpawnConfirmation tempConf)
        {
            for (int i = 0; i < tempConf.ProjectileCount; i++)
            {
                uint tempId = _nextTempId++;
                SpawnPredictionVisual(tempId, tempConf, i);
                _pendingTempIds.Enqueue(tempId);
            }
        }

        /// <summary>
        /// Standalone visual for a physics projectile on the firing client.
        /// Unlike SpawnImmediatePrediction, this does NOT queue a temp ID —
        /// physics never sends SpawnConfirmedClientRpc so there is nothing to link.
        /// The visual simply expires at cfg.Lifetime. Movement type is respected:
        /// straight physics = linear prediction; arching/etc = snapshot extrapolation.
        /// </summary>
        public void SpawnLocalPhysicsVisual(ushort configId, Vector3 origin, Vector3 dir, float speed)
        {
            uint id = _nextTempId++;
            SpawnPredictionVisual(id, new SpawnConfirmation
            {
                BaseProjId = 0, ProjectileCount = 1, ConfigId = configId,
                ServerSpawnTick = GetApproxServerTick(),
                Origin = origin, Direction = dir, Speed = speed,
                OwnerMidId = _localPlayerMidId, // mark as local-origin
                ExtraDirectionCount = 0, ExtraDirections = null
            }, 0);
            // NOT queued in _pendingTempIds — standalone, no server confirmation expected.
        }

        #endregion

        #region Public API — Bridge Callbacks

        /// <summary>
        /// Called when server confirms a Rust sim spawn.
        /// Local player with pending: links the temp visual to the real projId.
        /// Everyone else: spawns a fresh visual (host's / other player's projectiles).
        /// </summary>
        public void OnSpawnConfirmed(SpawnConfirmation conf)
        {
            bool isLocal = conf.OwnerMidId == _localPlayerMidId;
            for (int i = 0; i < conf.ProjectileCount; i++)
            {
                uint realId = conf.BaseProjId + (uint)i;
                if (isLocal && _pendingTempIds.Count > 0)
                    LinkPredictionId(_pendingTempIds.Dequeue(), realId);
                else
                    SpawnPredictionVisual(realId, conf, i);
            }
        }

        public void OnHitConfirmed(HitConfirmation confirmation)
        {
            if (!_predictions.TryGetValue(confirmation.ProjId, out var pred)) return;
            pred.IsConfirmedHit = true;
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

                // ── Confirmed hit ─────────────────────────────────────────────
                if (pred.IsConfirmedHit)
                {
                    pred.VisualObject.transform.position = Vector3.MoveTowards(
                        pred.VisualObject.transform.position,
                        pred.ConfirmedHitPosition, pred.Speed * Time.deltaTime);
                    if (Vector3.Distance(pred.VisualObject.transform.position, pred.ConfirmedHitPosition) < 0.05f)
                    { ReturnPredictionVisual(pred); toRemove.Add(kvp.Key); }
                    continue;
                }

                // ── Lifetime ──────────────────────────────────────────────────
                if (now - pred.SpawnTime >= pred.MaxLifetime)
                { ReturnPredictionVisual(pred); toRemove.Add(kvp.Key); continue; }

                float elapsed = now - pred.SpawnTime;

                // ── Snapshot-based extrapolation (non-linear movements) ────────
                //
                // For Wave, Circular, Arching, Guided: linear prediction diverges
                // from the actual curved path between snapshots, causing zig-zag.
                // Instead, extrapolate from the last server-confirmed position using
                // a velocity estimated from consecutive snapshot position changes.
                // This velocity follows the actual curve so display is smooth.
                if (pred.UseSnapshotExtrapolation)
                {
                    Vector3 displayPos;
                    Vector3 velDir;

                    if (pred.HasSnapshot)
                    {
                        // Clamp extrapolation to avoid large errors between sparse snapshots.
                        float sinceSnapshot = Mathf.Min(now - pred.LastSnapshotTime, 0.12f);
                        displayPos = pred.LastSnapshotPos + pred.LastSnapshotVelocity * sinceSnapshot;
                        velDir     = pred.LastSnapshotVelocity.sqrMagnitude > 0.001f
                            ? pred.LastSnapshotVelocity.normalized : pred.Direction;
                    }
                    else
                    {
                        // Before first snapshot: linear from origin (same as non-snapshot).
                        displayPos = pred.Origin + pred.Direction * pred.Speed * elapsed;
                        velDir     = pred.Direction;
                    }

                    pred.VisualObject.transform.position = displayPos;
                    ApplyDirectionRotation(pred.VisualObject.transform, velDir);
                    continue; // no History/reconciliation needed for snapshot-based
                }

                // ── Linear prediction (Straight / Teleport movements) ─────────
                Vector3 predicted = pred.Origin + pred.Direction * pred.Speed * elapsed;

                pred.History.Add(new ProjectileStatePayload
                {
                    ServerTick        = GetApproxServerTick(),
                    PredictedPosition = predicted
                });

                Vector3 linearDisplayPos;
                if (pred.IsReconciling)
                {
                    float t = Mathf.Clamp01((now - pred.ReconcileStartTime) / pred.ReconcileDuration);
                    // Correct direction: start WHERE WE WERE, move TOWARD server position.
                    linearDisplayPos = Vector3.Lerp(pred.ReconcileStartPosition, pred.ReconcileTarget, t);

                    if (t >= 1f)
                    {
                        pred.IsReconciling = false;
                        // Rebase so prediction continues forward from the corrected position
                        // without immediately diverging and re-triggering reconciliation.
                        pred.Origin = pred.ReconcileTarget - pred.Direction * pred.Speed * elapsed;
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

        private void SpawnPredictionVisual(uint projId, SpawnConfirmation conf, int dirIndex = 0)
        {
            var cfg = ProjectileRegistry.Instance?.Get(conf.ConfigId);
            if (cfg == null) return;

            Vector3 dir = conf.GetDirection(dirIndex).normalized;
            if (dir.sqrMagnitude < 0.001f) dir = Vector3.right;

            PoolableObjectType poolType = cfg.Is3D ? _visualPoolType3D : _visualPoolType2D;
            var obj = LocalObjectPool.Instance?.GetObject(poolType, conf.Origin, GetDirectionRotation(dir));
            if (obj == null) { LogWarning($"Pool null for {poolType}, projId={projId}"); return; }

            var vis = obj.GetComponent<ProjectileVisualBase>();
            vis?.InitializeClientVisual(conf.ConfigId, conf.Origin, dir, conf.Speed);

            // Determine whether this projectile uses non-linear movement (snapshot extrapolation)
            // or linear movement (prediction + threshold-based reconciliation).
            bool nonLinear = cfg.MovementType != ProjectileMovementType.Straight
                          && cfg.MovementType != ProjectileMovementType.Teleport;

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
                History         = new CircularBuffer<ProjectileStatePayload>(_historySize),
                MaxLifetime     = cfg.Lifetime,
                IsConfirmedHit  = false,
                IsReconciling   = false,

                // Snapshot extrapolation state
                UseSnapshotExtrapolation = nonLinear,
                HasSnapshot              = false,
                LastSnapshotPos          = conf.Origin,
                LastSnapshotVelocity     = dir * conf.Speed, // initial estimate before first snapshot
                LastSnapshotTime         = Time.time
            };
        }

        private void LinkPredictionId(uint tempId, uint realId)
        {
            if (!_predictions.TryGetValue(tempId, out var pred))
            {
                LogWarning($"LinkPredictionId: tempId={tempId} not found (expired?). realId={realId}");
                return;
            }
            _predictions.Remove(tempId);
            pred.ProjId     = realId;
            pred.BaseProjId = realId;
            _predictions[realId] = pred;
            Log($"Linked temp={tempId} → server={realId}");
        }

        #endregion

        #region Reconciliation

        private void ReconcileOne(uint projId, Vector3 serverPos, int serverTick)
        {
            if (!_predictions.TryGetValue(projId, out var pred)) return;

            // ── Snapshot-based (non-linear movement) ─────────────────────────
            // Every snapshot IS truth. Update the velocity estimate and accept
            // the new position — the extrapolation in Update() handles smooth display.
            if (pred.UseSnapshotExtrapolation)
            {
                float dt = Time.time - pred.LastSnapshotTime;
                if (pred.HasSnapshot && dt > 0.005f)
                {
                    // Estimate velocity from consecutive snapshots (Δpos / Δtime).
                    // Blend with previous estimate (0.6 weight on new) for stability.
                    Vector3 rawVel = (serverPos - pred.LastSnapshotPos) / dt;
                    pred.LastSnapshotVelocity = Vector3.Lerp(pred.LastSnapshotVelocity, rawVel, 0.6f);
                }
                pred.LastSnapshotPos  = serverPos;
                pred.LastSnapshotTime = Time.time;
                pred.HasSnapshot      = true;
                return; // extrapolation in Update() handles display, no further action needed
            }

            // ── Linear prediction (Straight movement) ─────────────────────────
            // Compare server position to what we predicted at that server tick.
            Vector3 ourPredicted;
            if (pred.History.TryFindLatest(s => s.ServerTick <= serverTick, out var state))
                ourPredicted = state.PredictedPosition;
            else
                ourPredicted = pred.VisualObject != null
                    ? pred.VisualObject.transform.position : pred.Origin;

            float error = Vector3.Distance(serverPos, ourPredicted);
            if (error < _reconcileThreshold) return;

            if (error > _hardSnapThreshold)
            {
                if (pred.VisualObject != null) pred.VisualObject.transform.position = serverPos;
                float el = Time.time - pred.SpawnTime;
                pred.Origin        = serverPos - pred.Direction * pred.Speed * el;
                pred.IsReconciling = false;
                return;
            }

            // Smooth correction: save current visual position, blend TOWARD server position.
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
            if (pred.VisualScript != null) pred.VisualScript.ReturnToPoolImmediate();
            else if (pred.VisualObject != null) LocalObjectPool.Instance?.ReturnObject(pred.VisualObject, pred.UsedPoolType);
            pred.VisualObject = null; pred.VisualScript = null;
        }

        #endregion

        #region Rotation

        public static Quaternion GetDirectionRotation(Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.001f) return Quaternion.identity;
            if (Mathf.Abs(dir.z) < 0.01f)
                return Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
            Vector3 up = Mathf.Abs(Vector3.Dot(dir.normalized, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(dir.normalized, up);
        }

        public static void ApplyDirectionRotation(Transform t, Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.001f) return;
            t.rotation = GetDirectionRotation(dir);
        }

        #endregion

        #region Helpers

        private static int GetApproxServerTick()
            => NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Tick : Mathf.RoundToInt(Time.time * 50f);

        private void Log(string m)        { if (_enableLogs) MID_HelperFunctions.LogDebug(m, nameof(ClientPredictionManager)); }
        private void LogWarning(string m) => MID_HelperFunctions.LogWarning(m, nameof(ClientPredictionManager));

        #endregion
    }
}
