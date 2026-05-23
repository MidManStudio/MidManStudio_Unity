// ClientPredictionManager.cs
//
// FIX (rotation):
//   SpawnPredictionVisual used LookRotation(Vector3.forward, dir) for all cases.
//   LookRotation's second arg is the "up" vector — passing the direction as "up"
//   rotated the sprite 90° wrong in 2D and produced nonsense orientation in 3D.
//
//   Fixed: 2D projectiles (dir.z ≈ 0) use Z-axis Euler rotation from atan2.
//           3D projectiles use LookRotation(dir, Vector3.up) so the visual's
//           forward (Z) aligns with the travel direction.
//
//   The Update loop also refreshes rotation each frame so reconciliation
//   corrections don't leave the visual facing the old direction.

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
    // ─── Circular buffer ──────────────────────────────────────────────────────

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

    // ─── State payload ────────────────────────────────────────────────────────

    internal struct ProjectileStatePayload
    {
        public int     ServerTick;
        public Vector3 PredictedPosition;
    }

    // ─── Per-projectile prediction state ──────────────────────────────────────

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

        public GameObject    VisualObject;
        public ProjectileVisual_ VisualScript;

        public CircularBuffer<ProjectileStatePayload> History;

        public bool    IsReconciling;
        public Vector3 ReconcileTarget;
        public float   ReconcileStartTime;
        public float   ReconcileDuration;

        public float   MaxLifetime;
        public bool    IsConfirmedHit;
        public Vector3 ConfirmedHitPosition;
    }

    // ─── Manager ──────────────────────────────────────────────────────────────

    public sealed class ClientPredictionManager : MonoBehaviour
    {
        #region Configuration

        [Header("Reconciliation")]
        [SerializeField] private float _reconcileThreshold  = 0.5f;
        [SerializeField] private float _hardSnapThreshold   = 3f;
        [SerializeField] private float _reconcileDuration   = 0.15f;

        [Header("History Buffer")]
        [SerializeField] private int   _historySize = 32;

        [Header("Visual Pool")]
        [SerializeField] private PoolableObjectType _visualPoolType
            = PoolableObjectType.Projectile_Visual2D;

        [Header("Local Player")]
        [SerializeField] private ulong _localPlayerMidId;

        [Header("Debug")]
        [SerializeField] private bool _enableLogs = false;

        #endregion

        #region State

        private readonly Dictionary<uint, PredictedProjectile> _predictions
            = new Dictionary<uint, PredictedProjectile>(64);

        #endregion

        #region Public API — Identity

        public void SetLocalPlayerMidId(ulong midId) => _localPlayerMidId = midId;

        #endregion

        #region Public API — Called by MID_ProjectileNetworkBridge

        public void OnSpawnConfirmed(SpawnConfirmation confirmation)
        {
            bool isLocal = confirmation.OwnerMidId == _localPlayerMidId;

            for (int i = 0; i < confirmation.ProjectileCount; i++)
            {
                uint projId = confirmation.BaseProjId + (uint)i;

                if (isLocal)
                    SpawnPredictionVisual(projId, confirmation);
                else
                    ClientProjectileVisualManager.SpawnVisual(
                        (int)projId, confirmation.ConfigId,
                        confirmation.Origin, confirmation.Direction, confirmation.Speed,
                        confirmation.OwnerMidId, false);
            }
        }

        public void OnHitConfirmed(HitConfirmation confirmation)
        {
            if (!_predictions.TryGetValue(confirmation.ProjId, out var pred))
            {
                ClientProjectileVisualManager.NotifyHit(
                    (int)confirmation.ProjId, confirmation.HitPosition, true);
                return;
            }

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

            float now     = Time.time;
            var toRemove  = new List<uint>();

            foreach (var kvp in _predictions)
            {
                var pred = kvp.Value;
                if (pred.VisualObject == null) { toRemove.Add(kvp.Key); continue; }

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

                // Deterministic predicted position (straight movement)
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
                    displayPos = Vector3.Lerp(predicted, pred.ReconcileTarget, 1f - t);
                    if (t >= 1f) pred.IsReconciling = false;
                }
                else
                {
                    displayPos = predicted;
                }

                pred.VisualObject.transform.position = displayPos;

                // FIX: correct rotation applied every frame (handles reconcile position corrections)
                ApplyDirectionRotation(pred.VisualObject.transform, pred.Direction);
            }

            foreach (var id in toRemove) _predictions.Remove(id);
        }

        #endregion

        #region Spawn Prediction Visual

        private void SpawnPredictionVisual(uint projId, SpawnConfirmation conf)
        {
            var cfg = ProjectileRegistry.Instance.Get(conf.ConfigId);
            if (cfg == null) return;

            Vector3    dir = conf.Direction.normalized;
            // FIX: correct 2D/3D rotation — don't use LookRotation(forward, dir)
            Quaternion rot = GetDirectionRotation(dir);

            var obj = LocalObjectPool.Instance.GetObject(_visualPoolType, conf.Origin, rot);
            if (obj == null)
            {
                LogWarning($"Could not get visual from pool for projId={projId}");
                return;
            }

            var vis = obj.GetComponent<ProjectileVisual_>();
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
                History         = new CircularBuffer<ProjectileStatePayload>(_historySize),
                MaxLifetime     = cfg.Lifetime,
                IsConfirmedHit  = false,
                IsReconciling   = false
            };

            _predictions[projId] = pred;
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
                pred.Origin = serverPos - pred.Direction * pred.Speed * elapsed;
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
                LocalObjectPool.Instance.ReturnObject(pred.VisualObject, _visualPoolType);

            pred.VisualObject = null;
            pred.VisualScript = null;
        }

        #endregion

        #region Rotation Helpers

        /// <summary>
        /// Returns the correct rotation for a projectile visual given its travel direction.
        ///
        /// 2D (dir.z ≈ 0): Z-axis Euler from atan2(y, x).
        ///   → Sprite tip points in +X of sprite space; world rotation makes it face travel dir.
        ///
        /// 3D (non-zero dir.z): LookRotation(dir, up).
        ///   → Visual's forward (Z) aligns with travel direction, matches shot point forward.
        /// </summary>
        public static Quaternion GetDirectionRotation(Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.001f)
                return Quaternion.identity;

            if (Mathf.Abs(dir.z) < 0.01f)
            {
                // 2D: rotate around world Z axis
                float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                return Quaternion.Euler(0f, 0f, angle);
            }
            else
            {
                // 3D: align object forward (Z) to travel direction
                return Quaternion.LookRotation(dir.normalized, Vector3.up);
            }
        }

        /// <summary>Applies direction-based rotation directly to a transform.</summary>
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
            if (_enableLogs) MID_HelperFunctions.LogDebug(msg, nameof(ClientPredictionManager));
        }

        private void LogWarning(string msg)
            => MID_HelperFunctions.LogWarning(msg, nameof(ClientPredictionManager));

        #endregion
    }
}
