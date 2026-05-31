// ClientPredictionManager.cs
// CHANGES:
//   + SpawnImmediatePrediction(): called by NetworkedDimensionPlayer the frame the client
//     fires. Spawns a pool visual with a temp ID so the bullet appears immediately.
//   + OnSpawnConfirmed(): for local-player confirmations with a pending temp prediction,
//     LinkPredictionId() swaps the temp ID for the server-assigned one so reconciliation
//     works correctly. For ALL other projectiles (non-local players, or no pending temp),
//     a fresh prediction visual is spawned — this is the fix for "host fires, clients see
//     nothing" (previously routed to the empty ClientProjectileVisualManager stub).
//   + SpawnPredictionVisual(): now accepts an int directionIndex so multi-pellet shots
//     (shotgun, patterns) each travel in their own direction instead of all using Direction[0].
//   + _pendingTempIds queue: FIFO matching of immediate predictions to server confirmations.
//     If a temp visual expires before confirmation arrives, it's cleaned up safely in Update.

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
    // ── Circular buffer ───────────────────────────────────────────────────────

    internal sealed class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private int _head;
        private int _count;

        public int Capacity => _buffer.Length;
        public int Count    => _count;

        public CircularBuffer(int capacity) => _buffer = new T[capacity];

        public void Add(T item)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }

        public T Get(int index)
        {
            if (index < 0 || index >= _count)
                throw new IndexOutOfRangeException();
            int bufIdx = (_head - _count + index + _buffer.Length) % _buffer.Length;
            return _buffer[bufIdx];
        }

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

        public void Clear() { _head = 0; _count = 0; }
    }

    // ── State payload ─────────────────────────────────────────────────────────

    internal struct ProjectileStatePayload
    {
        public int     ServerTick;
        public Vector3 PredictedPosition;
    }

    // ── Per-projectile prediction state ───────────────────────────────────────

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

        public bool    IsReconciling;
        public Vector3 ReconcileTarget;
        public float   ReconcileStartTime;
        public float   ReconcileDuration;

        public float   MaxLifetime;
        public bool    IsConfirmedHit;
        public Vector3 ConfirmedHitPosition;
    }

    // ── Manager ───────────────────────────────────────────────────────────────

    public sealed class ClientPredictionManager : MonoBehaviour
    {
        #region Configuration

        [Header("Reconciliation")]
        [SerializeField] private float _reconcileThreshold = 0.5f;
        [SerializeField] private float _hardSnapThreshold  = 3f;
        [SerializeField] private float _reconcileDuration  = 0.15f;

        [Header("History Buffer")]
        [SerializeField] private int _historySize = 32;

        [Header("Visual Pool Types")]
        [Tooltip("Pool type for 2D projectile visuals.")]
        [SerializeField] private PoolableObjectType _visualPoolType2D
            = PoolableObjectType.Projectile_Visual2D;

        [Tooltip("Pool type for 3D projectile visuals.")]
        [SerializeField] private PoolableObjectType _visualPoolType3D
            = PoolableObjectType.Projectile_Visual3D;

        [Header("Local Player")]
        [SerializeField] private ulong _localPlayerMidId;

        [Header("Debug")]
        [SerializeField] private bool _enableLogs = false;

        #endregion

        #region State

        private readonly Dictionary<uint, PredictedProjectile> _predictions
            = new Dictionary<uint, PredictedProjectile>(64);

        // ── Immediate prediction tracking ─────────────────────────────────────
        // When the owning client fires, we spawn a visual with a temp ID before
        // the server confirms. These temp IDs are queued FIFO; when the server
        // confirmation arrives we pop the oldest and link it to the real projId.
        private readonly Queue<uint> _pendingTempIds = new Queue<uint>(16);
        private uint _nextTempId = 0xFFFE0000u; // well above any real server projId

        #endregion

        #region Public API — Identity

        public void SetLocalPlayerMidId(ulong midId) => _localPlayerMidId = midId;

        #endregion

        #region Public API — Immediate Local Prediction

        /// <summary>
        /// Spawn a prediction visual right now — before the server has confirmed
        /// the fire event. Call this from the owning client's fire method.
        /// When SpawnConfirmedClientRpc arrives, OnSpawnConfirmed will link the
        /// temp visual(s) to the server-assigned projIds via LinkPredictionId().
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

        #region Public API — Called by MID_ProjectileNetworkBridge

        /// <summary>
        /// Called when the server confirms a projectile spawn.
        ///
        /// Three cases:
        ///   1. Local player + pending temp prediction  → link temp → real ID (no new visual).
        ///   2. Local player + no pending (edge case)  → spawn fresh visual.
        ///   3. Any other player (non-local)            → spawn fresh visual.
        ///
        /// Case 3 is the critical fix: previously non-local projectiles were routed to
        /// the empty ClientProjectileVisualManager stub, making host projectiles invisible
        /// to clients and vice-versa.
        /// </summary>
        public void OnSpawnConfirmed(SpawnConfirmation confirmation)
        {
            bool isLocal = confirmation.OwnerMidId == _localPlayerMidId;

            for (int i = 0; i < confirmation.ProjectileCount; i++)
            {
                uint serverProjId = confirmation.BaseProjId + (uint)i;

                if (isLocal && _pendingTempIds.Count > 0)
                {
                    // Link the oldest immediate-prediction visual to the confirmed ID.
                    uint tempId = _pendingTempIds.Dequeue();
                    LinkPredictionId(tempId, serverProjId);
                }
                else
                {
                    // No pending temp prediction (non-local player, or very rare edge case):
                    // spawn a fresh prediction visual so other players' projectiles are visible.
                    SpawnPredictionVisual(serverProjId, confirmation, i);
                }
            }
        }

        public void OnHitConfirmed(HitConfirmation confirmation)
        {
            if (!_predictions.TryGetValue(confirmation.ProjId, out var pred))
                return;

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

                // ── Confirmed hit: travel to hit point then despawn ────────────
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

                // ── Lifetime expired ──────────────────────────────────────────
                if (now - pred.SpawnTime >= pred.MaxLifetime)
                {
                    ReturnPredictionVisual(pred);
                    toRemove.Add(kvp.Key);
                    continue;
                }

                // ── Straight-line prediction ──────────────────────────────────
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
                ApplyDirectionRotation(pred.VisualObject.transform, pred.Direction);
            }

            foreach (var id in toRemove) _predictions.Remove(id);
        }

        #endregion

        #region Spawn / Link Prediction Visual

        /// <summary>
        /// Spawn a pooled prediction visual for one projectile in the batch.
        /// <paramref name="directionIndex"/> selects the correct per-pellet direction
        /// from the SpawnConfirmation (fixes multi-pellet shots all using Direction[0]).
        /// </summary>
        private void SpawnPredictionVisual(
            uint projId, SpawnConfirmation conf, int directionIndex = 0)
        {
            var cfg = ProjectileRegistry.Instance.Get(conf.ConfigId);
            if (cfg == null) return;

            // Use the per-pellet direction (shotgun / pattern support).
            Vector3 dir = conf.GetDirection(directionIndex).normalized;
            if (dir.sqrMagnitude < 0.001f) dir = Vector3.right;

            Quaternion rot      = GetDirectionRotation(dir);
            PoolableObjectType poolType = cfg.Is3D ? _visualPoolType3D : _visualPoolType2D;

            var obj = LocalObjectPool.Instance?.GetObject(poolType, conf.Origin, rot);
            if (obj == null)
            {
                LogWarning($"Pool returned null for type {poolType}, projId={projId}");
                return;
            }

            var vis = obj.GetComponent<ProjectileVisualBase>();
            vis?.InitializeClientVisual(conf.ConfigId, conf.Origin, dir, conf.Speed);

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
                UsedPoolType    = poolType,
                History         = new CircularBuffer<ProjectileStatePayload>(_historySize),
                MaxLifetime     = cfg.Lifetime,
                IsConfirmedHit  = false,
                IsReconciling   = false
            };

            _predictions[projId] = pred;
        }

        /// <summary>
        /// Swap a temp prediction ID for the real server-confirmed projId.
        /// The visual keeps moving without interruption.
        /// </summary>
        private void LinkPredictionId(uint tempId, uint realId)
        {
            if (!_predictions.TryGetValue(tempId, out var pred))
            {
                // Temp prediction already expired (extremely unlikely on LAN).
                LogWarning($"LinkPredictionId: tempId={tempId} not found (expired?). realId={realId} skipped.");
                return;
            }

            _predictions.Remove(tempId);
            pred.ProjId     = realId;
            pred.BaseProjId = realId; // update base too for snapshot reconciliation
            _predictions[realId] = pred;

            Log($"Linked temp={tempId} → server={realId}");
        }

        #endregion

        #region Reconciliation

        private void ReconcileOne(uint projId, Vector3 serverPos, int serverTick)
        {
            if (!_predictions.TryGetValue(projId, out var pred)) return;

            Vector3 ourPredicted;
            if (pred.History.TryFindLatest(s => s.ServerTick <= serverTick, out var state))
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
                float elapsed = Time.time - pred.SpawnTime;
                pred.Origin        = serverPos - pred.Direction * pred.Speed * elapsed;
                pred.IsReconciling = false;
                return;
            }

            pred.IsReconciling      = true;
            pred.ReconcileTarget    = serverPos;
            pred.ReconcileStartTime = Time.time;
            pred.ReconcileDuration  = _reconcileDuration;
        }

        #endregion

        #region Cleanup

        private void ReturnPredictionVisual(PredictedProjectile pred)
        {
            if (pred.VisualScript != null)
                pred.VisualScript.ReturnToPoolImmediate();
            else if (pred.VisualObject != null)
                LocalObjectPool.Instance?.ReturnObject(pred.VisualObject, pred.UsedPoolType);

            pred.VisualObject = null;
            pred.VisualScript = null;
        }

        #endregion

        #region Rotation Helpers

        public static Quaternion GetDirectionRotation(Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.001f) return Quaternion.identity;

            if (Mathf.Abs(dir.z) < 0.01f)
            {
                float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                return Quaternion.Euler(0f, 0f, angle);
            }
            else
            {
                Vector3 up = Mathf.Abs(Vector3.Dot(dir.normalized, Vector3.up)) > 0.99f
                    ? Vector3.forward : Vector3.up;
                return Quaternion.LookRotation(dir.normalized, up);
            }
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

        private void Log(string msg)
        {
            if (_enableLogs)
                MID_HelperFunctions.LogDebug(msg, nameof(ClientPredictionManager));
        }

        private void LogWarning(string msg)
            => MID_HelperFunctions.LogWarning(msg, nameof(ClientPredictionManager));

        #endregion
    }
}
