// ClientPredictionManager.cs
// Client-side projectile prediction and server reconciliation.
//
// Adapted from NetworkPredictionComponent patterns:
//   CircularBuffer         — borrowed pattern, adapted for projectiles
//   StatePayLoad concept   — adapted as ProjectileStatePayLoad
//   Threshold reconcile    — same pattern, applied to visual transforms
//
// What is NOT reused from NetworkPredictionComponent:
//   InputPayLoad           — projectiles have no input
//   OnProcessMovement      — projectiles move deterministically
//   Rigidbody reconcile    — we reconcile visual transforms only
//   PlayerMovementController coupling — none
//
// Prediction model:
//   On SpawnConfirmed: client spawns a prediction visual and stores its
//   spawn params (origin, dir, speed, serverTick).
//
//   Each frame: visual position = origin + dir * speed * elapsedTime.
//   This matches Rust tick_projectiles() straight movement exactly.
//   For arching/guided, elapsedTime-based extrapolation is less accurate
//   but sufficient — snapshots will reconcile the error.
//
//   On Snapshot: for each (projId, serverPos) compare against predicted pos.
//   If error > threshold → snap visual to lerp between predicted and server.
//   If error > hardSnapThreshold → instant snap, no lerp.
//
//   On HitConfirmed: move visual to hit position, play impact, return to pool.
//
// MID ID note:
//   We only spawn prediction visuals for projectiles owned by the LOCAL player.
//   Other players' projectiles are handled by ClientProjectileVisualManager
//   which receives SpawnConfirmed and moves them independently.
//   The ownerMidId in SpawnConfirmation is compared to the local player's MID ID.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using MidManStudio.Core.PoolSystems;
using MidManStudio.InGame.Managers;
using MidManStudio.Core.HelperFunctions;

namespace MidManStudio.Projectiles
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Circular buffer — borrowed pattern from NetworkPredictionComponent
    //  Generic, fixed-capacity, overwrites oldest on overflow.
    // ─────────────────────────────────────────────────────────────────────────

    internal sealed class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private int _head;
        private int _count;

        public int Capacity => _buffer.Length;
        public int Count    => _count;

        public CircularBuffer(int capacity)
        {
            _buffer = new T[capacity];
        }

        public void Add(T item)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }

        /// Get by index from oldest (0) to newest (Count-1).
        public T Get(int index)
        {
            if (index < 0 || index >= _count)
                throw new IndexOutOfRangeException($"CircularBuffer: {index} out of [{_count}]");

            int bufIdx = (_head - _count + index + _buffer.Length) % _buffer.Length;
            return _buffer[bufIdx];
        }

        /// Find the most recent entry matching the predicate, or default.
        public bool TryFindLatest(Predicate<T> match, out T result)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                var item = Get(i);
                if (match(item)) { result = item; return true; }
            }
            result = default;
            return false;
        }

        public void Clear()
        {
            _head  = 0;
            _count = 0;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Projectile state pay-load — stored in CircularBuffer per projectile
    //  Records predicted position at a given server tick for reconciliation.
    // ─────────────────────────────────────────────────────────────────────────

    internal struct ProjectileStatePayload
    {
        /// Server tick this state was recorded at.
        public int     ServerTick;

        /// Predicted world-space position at this tick.
        public Vector3 PredictedPosition;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Per-projectile prediction state
    // ─────────────────────────────────────────────────────────────────────────

    internal sealed class PredictedProjectile
    {
        // ── Identity ─────────────────────────────────────────────────────────
        public uint   BaseProjId;     // first proj in this batch (pellet 0)
        public uint   ProjId;         // this pellet's ID
        public ushort ConfigId;
        public bool   Is3D;

        // ── Spawn params (deterministic movement base) ────────────────────────
        public Vector3 Origin;
        public Vector3 Direction;
        public float   Speed;
        public float   SpawnTime;     // Time.time at local spawn (not server time)
        public int     ServerSpawnTick;

        // ── Visual ────────────────────────────────────────────────────────────
        public GameObject VisualObject;
        public ProjectileVisual_ VisualScript;

        // ── Prediction history ────────────────────────────────────────────────
        public CircularBuffer<ProjectileStatePayload> History;

        // ── Reconciliation state ──────────────────────────────────────────────
        /// True while we are smoothly interpolating toward a reconciled position.
        public bool    IsReconciling;
        public Vector3 ReconcileTarget;
        public float   ReconcileStartTime;
        public float   ReconcileDuration;

        // ── Lifetime ──────────────────────────────────────────────────────────
        public float   MaxLifetime;
        public bool    IsConfirmedHit;
        public Vector3 ConfirmedHitPosition;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ClientPredictionManager
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Client-only. Manages prediction visuals for the LOCAL player's projectiles.
    /// Other players' visuals are handled by ClientProjectileVisualManager.
    /// Attach to a persistent GameObject in the scene.
    /// </summary>
    public sealed class ClientPredictionManager : MonoBehaviour
    {
        #region Configuration

        [Header("Reconciliation")]
        [Tooltip("If predicted position differs from server snapshot by more than this\n" +
                 "(world units), begin smooth correction over ReconcileDuration.")]
        [SerializeField] private float _reconcileThreshold = 0.5f;

        [Tooltip("If error exceeds this, snap instantly instead of smoothly correcting.")]
        [SerializeField] private float _hardSnapThreshold = 3f;

        [Tooltip("Duration (seconds) to smoothly lerp from predicted to server position.")]
        [SerializeField] private float _reconcileDuration = 0.15f;

        [Header("History Buffer")]
        [Tooltip("How many tick states to remember per projectile.\n" +
                 "Must be at least SnapshotInterval * 2.")]
        [SerializeField] private int _historySize = 32;

        [Header("Visual Pool")]
        [SerializeField] private PoolableObjectType _visualPoolType
            = PoolableObjectType.ProjectileVisual_;

        [Header("Local Player")]
        [Tooltip("The local player's MID ID.\n" +
                 "Only projectiles with this ownerMidId get prediction visuals.\n" +
                 "Set this at runtime from your player manager.")]
        [SerializeField] private ulong _localPlayerMidId;

        [Header("Debug")]
        [SerializeField] private bool _enableLogs = false;

        #endregion

        #region State

        private readonly Dictionary<uint, PredictedProjectile> _predictions
            = new Dictionary<uint, PredictedProjectile>(64);

        #endregion

        #region Public API — Set Local Player

        /// <summary>
        /// Set the local player's MID ID.
        /// Call this from your player spawn/login flow.
        /// Only projectiles owned by this MID ID get prediction visuals.
        /// </summary>
        public void SetLocalPlayerMidId(ulong midId)
        {
            _localPlayerMidId = midId;
        }

        #endregion

        #region Public API — Called by MID_ProjectileNetworkBridge

        /// <summary>
        /// Called when SpawnConfirmedClientRpc is received.
        /// Spawns prediction visuals for the local player's projectiles.
        /// Other players' projectiles: passes through to ClientProjectileVisualManager.
        /// </summary>
        public void OnSpawnConfirmed(SpawnConfirmation confirmation)
        {
            bool isLocalPlayer = confirmation.OwnerMidId == _localPlayerMidId;

            for (int i = 0; i < confirmation.ProjectileCount; i++)
            {
                uint projId = confirmation.BaseProjId + (uint)i;

                if (isLocalPlayer)
                {
                    SpawnPredictionVisual(projId, confirmation, i);
                }
                else
                {
                    // Other players — delegate to ClientProjectileVisualManager
                    // (it handles the visual for all non-local projectiles)
                    // Direction is the server's forward — no per-pellet spread on this path
                    ClientProjectileVisualManager.SpawnVisual(
                        (int)projId,
                        MID_AllProjectileNames.none,
                        confirmation.Origin,
                        confirmation.Direction,
                        confirmation.Speed,
                        confirmation.OwnerMidId,
                        false);
                }
            }
        }

        /// <summary>
        /// Called when HitConfirmedClientRpc is received.
        /// Moves prediction visual to hit position then returns it to pool.
        /// </summary>
        public void OnHitConfirmed(HitConfirmation confirmation)
        {
            if (!_predictions.TryGetValue(confirmation.ProjId, out var pred))
            {
                // Not a local player projectile — delegate
                ClientProjectileVisualManager.NotifyHit(
                    (int)confirmation.ProjId, confirmation.HitPosition, true);
                return;
            }

            pred.IsConfirmedHit      = true;
            pred.ConfirmedHitPosition = confirmation.HitPosition;

            Log($"HitConfirmed: projId={confirmation.ProjId} at {confirmation.HitPosition}");
        }

        /// <summary>
        /// Called when position snapshot arrives from server.
        /// Compares server positions against local predictions and reconciles if needed.
        /// </summary>
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

        #region Update — Prediction Loop

        private void Update()
        {
            if (_predictions.Count == 0) return;

            float now = Time.time;
            var toRemove = new List<uint>();

            foreach (var kvp in _predictions)
            {
                var pred = kvp.Value;

                if (pred.VisualObject == null)
                {
                    toRemove.Add(kvp.Key);
                    continue;
                }

                // Confirmed hit — move to hit point then clean up
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

                // Lifetime expiry
                if (now - pred.SpawnTime >= pred.MaxLifetime)
                {
                    ReturnPredictionVisual(pred);
                    toRemove.Add(kvp.Key);
                    continue;
                }

                // Compute deterministic predicted position
                float elapsed    = now - pred.SpawnTime;
                Vector3 predicted = pred.Origin + pred.Direction * pred.Speed * elapsed;

                // Store in history for reconciliation lookups
                int serverTick = GetApproxServerTick();
                pred.History.Add(new ProjectileStatePayload
                {
                    ServerTick        = serverTick,
                    PredictedPosition = predicted
                });

                // Apply reconciliation lerp if active
                Vector3 displayPos;
                if (pred.IsReconciling)
                {
                    float t = Mathf.Clamp01(
                        (now - pred.ReconcileStartTime) / pred.ReconcileDuration);
                    displayPos = Vector3.Lerp(predicted, pred.ReconcileTarget, 1f - t);

                    if (t >= 1f) pred.IsReconciling = false;
                }
                else
                {
                    displayPos = predicted;
                }

                pred.VisualObject.transform.position = displayPos;

                // Update rotation from direction
                if (pred.Direction.sqrMagnitude > 0.001f)
                {
                    pred.VisualObject.transform.rotation =
                        Quaternion.LookRotation(Vector3.forward, pred.Direction);
                }
            }

            foreach (var id in toRemove)
                _predictions.Remove(id);
        }

        #endregion

        #region Spawn Prediction Visual

        private void SpawnPredictionVisual(
            uint projId, SpawnConfirmation conf, int pelletIndex)
        {
            var cfg = ProjectileRegistry.Instance.Get(conf.ConfigId);
            if (cfg == null) return;

            // Spawn visual from pool
            Vector3 dir = conf.Direction.normalized;
            Quaternion rot = dir.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(Vector3.forward, dir)
                : Quaternion.identity;

            var obj = LocalObjectPool.Instance.GetObject(_visualPoolType, conf.Origin, rot);
            if (obj == null)
            {
                LogWarning($"Could not get visual from pool for projId={projId}");
                return;
            }

            var vis = obj.GetComponent<ProjectileVisual_>();
            vis?.InitializeClientVisual(
                MID_AllProjectileNames.none,
                conf.Origin, dir, conf.Speed);

            var pred = new PredictedProjectile
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
                History         = new CircularBuffer<ProjectileStatePayload>(_historySize),
                MaxLifetime     = cfg.Lifetime,
                IsConfirmedHit  = false,
                IsReconciling   = false
            };

            _predictions[projId] = pred;

            Log($"Prediction visual spawned: projId={projId} origin={conf.Origin}");
        }

        #endregion

        #region Reconciliation

        private void ReconcileOne(uint projId, Vector3 serverPos, int serverTick)
        {
            if (!_predictions.TryGetValue(projId, out var pred)) return;

            // Find our predicted position at or near this server tick
            Vector3 ourPredicted;
            if (pred.History.TryFindLatest(
                s => s.ServerTick <= serverTick, out var state))
            {
                ourPredicted = state.PredictedPosition;
            }
            else
            {
                // No matching history — use current visual position
                ourPredicted = pred.VisualObject != null
                    ? pred.VisualObject.transform.position
                    : pred.Origin;
            }

            float error = Vector3.Distance(serverPos, ourPredicted);

            if (error < _reconcileThreshold)
            {
                // Within tolerance — no correction needed
                return;
            }

            if (error > _hardSnapThreshold)
            {
                // Too far off — instant snap
                if (pred.VisualObject != null)
                    pred.VisualObject.transform.position = serverPos;

                // Adjust origin so future predictions start from corrected position
                float elapsed = Time.time - pred.SpawnTime;
                pred.Origin = serverPos - pred.Direction * pred.Speed * elapsed;

                pred.IsReconciling = false;

                Log($"Hard snap: projId={projId} error={error:F2}m");
                return;
            }

            // Smooth reconciliation — lerp over _reconcileDuration seconds
            // We don't restart the origin — we let the position drift toward server
            // over the reconcile window, then continue from normal prediction.
            pred.IsReconciling       = true;
            pred.ReconcileTarget     = serverPos;
            pred.ReconcileStartTime  = Time.time;
            pred.ReconcileDuration   = _reconcileDuration;

            Log($"Smooth reconcile: projId={projId} error={error:F2}m");
        }

        #endregion

        #region Cleanup

        private void ReturnPredictionVisual(PredictedProjectile pred)
        {
            if (pred.VisualScript != null)
                pred.VisualScript.ReturnToPoolImmediate();
            else if (pred.VisualObject != null)
                LocalObjectPool.Instance.ReturnObject(pred.VisualObject, _visualPoolType);

            pred.VisualObject = null;
            pred.VisualScript = null;
        }

        #endregion

        #region Helpers

        private static int GetApproxServerTick()
        {
            return Unity.Netcode.NetworkManager.Singleton != null
                ? Unity.Netcode.NetworkManager.Singleton.ServerTime.Tick
                : Mathf.RoundToInt(Time.time * 50f);
        }

        private void Log(string msg)
        {
            if (_enableLogs)
                MID_HelperFunctions.LogDebug(msg, nameof(ClientPredictionManager));
        }

        private void LogWarning(string msg)
        {
            MID_HelperFunctions.LogWarning(msg, nameof(ClientPredictionManager));
        }

        #endregion
    }
}
