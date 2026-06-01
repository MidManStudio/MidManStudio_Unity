// ClientPredictionManager.cs
// FIX (Rust sim back-and-forth oscillation): The reconciliation lerp was BACKWARDS.
//
// Old (broken) logic in Update:
//   displayPos = Lerp(predicted, reconcileTarget, 1 - t)
//   t=0 → jumps TO server position
//   t=1 → returns BACK to prediction position
// This caused the exact oscillation described: snap to server, drift back, snap again.
//
// Fixed logic:
//   - PredictedProjectile now stores ReconcileStartPosition (where the visual was
//     when reconciliation began).
//   - displayPos = Lerp(startPosition, reconcileTarget, t)  ← correct direction
//   - When reconciliation finishes, pred.Origin is rebased so the linear prediction
//     continues forward from the corrected position instead of immediately diverging.
//
// All other code (immediate prediction, SpawnImmediatePrediction, LinkPredictionId,
// multi-direction spawns, non-local player visuals) is unchanged.

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
        private readonly T[] _buffer;
        private int _head;
        private int _count;
        public int Capacity => _buffer.Length;
        public int Count    => _count;
        public CircularBuffer(int capacity) => _buffer = new T[capacity];
        public void Add(T item) { _buffer[_head] = item; _head = (_head + 1) % _buffer.Length; if (_count < _buffer.Length) _count++; }
        public T Get(int index) { if (index < 0 || index >= _count) throw new IndexOutOfRangeException(); return _buffer[(_head - _count + index + _buffer.Length) % _buffer.Length]; }
        public bool TryFindLatest(Predicate<T> match, out T result) { for (int i = _count - 1; i >= 0; i--) { var item = Get(i); if (match(item)) { result = item; return true; } } result = default; return false; }
        public void Clear() { _head = 0; _count = 0; }
    }

    internal struct ProjectileStatePayload
    {
        public int     ServerTick;
        public Vector3 PredictedPosition;
    }

    internal sealed class PredictedProjectile
    {
        public uint   BaseProjId;
        public uint   ProjId;
        public ushort ConfigId;
        public bool   Is3D;

        public Vector3 Origin;
        public Vector3 Direction;
        public float   Speed;
        public float   SpawnTime;
        public int     ServerSpawnTick;

        public GameObject           VisualObject;
        public ProjectileVisualBase VisualScript;
        public PoolableObjectType   UsedPoolType;

        public CircularBuffer<ProjectileStatePayload> History;

        // ── Reconciliation ────────────────────────────────────────────────────
        public bool    IsReconciling;
        public Vector3 ReconcileStartPosition; // where the visual was when reconcile began
        public Vector3 ReconcileTarget;         // server-reported position to correct toward
        public float   ReconcileStartTime;
        public float   ReconcileDuration;

        public float   MaxLifetime;
        public bool    IsConfirmedHit;
        public Vector3 ConfirmedHitPosition;
    }

    public sealed class ClientPredictionManager : MonoBehaviour
    {
        #region Configuration

        [Header("Reconciliation")]
        [Tooltip("Error in world units below which reconciliation is skipped (small jitter ignored).")]
        [SerializeField] private float _reconcileThreshold = 0.3f;
        [Tooltip("Error above which the visual hard-snaps instead of smoothly correcting.")]
        [SerializeField] private float _hardSnapThreshold  = 3f;
        [Tooltip("Seconds to smoothly blend from current position to server position.")]
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

        // Immediate prediction tracking: FIFO queue of temp IDs spawned before server confirms.
        private readonly Queue<uint> _pendingTempIds = new Queue<uint>(16);
        private uint _nextTempId = 0xFFFE0000u;

        #endregion

        #region Public API — Identity

        public void SetLocalPlayerMidId(ulong midId) => _localPlayerMidId = midId;

        #endregion

        #region Public API — Immediate Prediction

        /// <summary>
        /// Spawn a prediction visual this frame — before the server confirms.
        /// Called from the firing client's fire method (FireSim). When SpawnConfirmedClientRpc
        /// arrives, OnSpawnConfirmed links the temp visual to the server-assigned projId.
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

        #endregion

        #region Public API — Bridge callbacks

        /// <summary>
        /// Called by MID_ProjectileNetworkBridge when the server confirms a Rust sim spawn.
        ///
        /// Local player with pending temp: link temp visual to real ID (no second visual).
        /// Everyone else: spawn a fresh prediction visual so all players' projectiles are visible.
        /// </summary>
        public void OnSpawnConfirmed(SpawnConfirmation confirmation)
        {
            bool isLocal = confirmation.OwnerMidId == _localPlayerMidId;

            for (int i = 0; i < confirmation.ProjectileCount; i++)
            {
                uint serverProjId = confirmation.BaseProjId + (uint)i;

                if (isLocal && _pendingTempIds.Count > 0)
                    LinkPredictionId(_pendingTempIds.Dequeue(), serverProjId);
                else
                    SpawnPredictionVisual(serverProjId, confirmation, i);
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

        #region Update — Prediction Loop

        private void Update()
        {
            if (_predictions.Count == 0) return;

            float now      = Time.time;
            var   toRemove = new List<uint>(8);

            foreach (var kvp in _predictions)
            {
                var pred = kvp.Value;
                if (pred.VisualObject == null) { toRemove.Add(kvp.Key); continue; }

                // ── Confirmed hit: travel to impact point then despawn ─────────
                if (pred.IsConfirmedHit)
                {
                    pred.VisualObject.transform.position = Vector3.MoveTowards(
                        pred.VisualObject.transform.position,
                        pred.ConfirmedHitPosition,
                        pred.Speed * Time.deltaTime);

                    if (Vector3.Distance(pred.VisualObject.transform.position, pred.ConfirmedHitPosition) < 0.05f)
                    { ReturnPredictionVisual(pred); toRemove.Add(kvp.Key); }
                    continue;
                }

                // ── Lifetime expired ──────────────────────────────────────────
                if (now - pred.SpawnTime >= pred.MaxLifetime)
                { ReturnPredictionVisual(pred); toRemove.Add(kvp.Key); continue; }

                // ── Linear prediction ─────────────────────────────────────────
                float   elapsed   = now - pred.SpawnTime;
                Vector3 predicted = pred.Origin + pred.Direction * pred.Speed * elapsed;

                pred.History.Add(new ProjectileStatePayload
                {
                    ServerTick        = GetApproxServerTick(),
                    PredictedPosition = predicted
                });

                Vector3 displayPos;

                if (pred.IsReconciling)
                {
                    float t = Mathf.Clamp01((now - pred.ReconcileStartTime) / pred.ReconcileDuration);

                    // FIX: lerp FROM where reconciliation started TO the server-correct position.
                    // Old code was Lerp(predicted, target, 1-t) which started AT the server
                    // position and returned BACK to prediction — producing the back-and-forth.
                    displayPos = Vector3.Lerp(pred.ReconcileStartPosition, pred.ReconcileTarget, t);

                    if (t >= 1f)
                    {
                        pred.IsReconciling = false;
                        // Rebase origin so the prediction continues FORWARD from the corrected
                        // server position. Without this, prediction would immediately diverge
                        // again and trigger another reconciliation on the next snapshot.
                        pred.Origin = pred.ReconcileTarget - pred.Direction * pred.Speed * elapsed;
                    }
                }
                else
                {
                    displayPos = predicted;
                }

                pred.VisualObject.transform.position = displayPos;
                ApplyDirectionRotation(pred.VisualObject.transform, pred.Direction);
            }

            foreach (var id in toRemove) _predictions.Remove(id);
        }

        #endregion

        #region Spawn / Link Visual

        private void SpawnPredictionVisual(uint projId, SpawnConfirmation conf, int directionIndex = 0)
        {
            var cfg = ProjectileRegistry.Instance.Get(conf.ConfigId);
            if (cfg == null) return;

            Vector3 dir = conf.GetDirection(directionIndex).normalized;
            if (dir.sqrMagnitude < 0.001f) dir = Vector3.right;

            Quaternion rot = GetDirectionRotation(dir);
            PoolableObjectType poolType = cfg.Is3D ? _visualPoolType3D : _visualPoolType2D;

            var obj = LocalObjectPool.Instance?.GetObject(poolType, conf.Origin, rot);
            if (obj == null) { LogWarning($"Pool returned null for {poolType}, projId={projId}"); return; }

            var vis = obj.GetComponent<ProjectileVisualBase>();
            vis?.InitializeClientVisual(conf.ConfigId, conf.Origin, dir, conf.Speed);

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
                IsReconciling   = false
            };
        }

        private void LinkPredictionId(uint tempId, uint realId)
        {
            if (!_predictions.TryGetValue(tempId, out var pred))
            {
                LogWarning($"LinkPredictionId: tempId={tempId} expired before confirmation. realId={realId}");
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

            // Find what we predicted at the server tick (using history for accurate comparison).
            Vector3 ourPredicted;
            if (pred.History.TryFindLatest(s => s.ServerTick <= serverTick, out var state))
                ourPredicted = state.PredictedPosition;
            else
                ourPredicted = pred.VisualObject != null
                    ? pred.VisualObject.transform.position : pred.Origin;

            float error = Vector3.Distance(serverPos, ourPredicted);
            if (error < _reconcileThreshold) return; // small jitter — ignore

            if (error > _hardSnapThreshold)
            {
                // Large error: hard-snap and rebase immediately.
                if (pred.VisualObject != null)
                    pred.VisualObject.transform.position = serverPos;
                float elapsed = Time.time - pred.SpawnTime;
                pred.Origin        = serverPos - pred.Direction * pred.Speed * elapsed;
                pred.IsReconciling = false;
                return;
            }

            // Smooth correction: record where the visual currently is, then lerp toward server.
            pred.IsReconciling       = true;
            pred.ReconcileStartPosition = pred.VisualObject != null
                ? pred.VisualObject.transform.position : ourPredicted;
            pred.ReconcileTarget    = serverPos;
            pred.ReconcileStartTime = Time.time;
            pred.ReconcileDuration  = _reconcileDuration;
        }

        #endregion

        #region Cleanup

        private void ReturnPredictionVisual(PredictedProjectile pred)
        {
            if (pred.VisualScript != null) pred.VisualScript.ReturnToPoolImmediate();
            else if (pred.VisualObject != null) LocalObjectPool.Instance?.ReturnObject(pred.VisualObject, pred.UsedPoolType);
            pred.VisualObject = null;
            pred.VisualScript = null;
        }

        #endregion

        #region Rotation Helpers

        public static Quaternion GetDirectionRotation(Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.001f) return Quaternion.identity;
            if (Mathf.Abs(dir.z) < 0.01f)
                return Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
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

        #region Helpers

        private static int GetApproxServerTick()
            => NetworkManager.Singleton != null
                ? NetworkManager.Singleton.ServerTime.Tick
                : Mathf.RoundToInt(Time.time * 50f);

        private void Log(string msg)    { if (_enableLogs) MID_HelperFunctions.LogDebug(msg, nameof(ClientPredictionManager)); }
        private void LogWarning(string msg) => MID_HelperFunctions.LogWarning(msg, nameof(ClientPredictionManager));

        #endregion
    }
}
