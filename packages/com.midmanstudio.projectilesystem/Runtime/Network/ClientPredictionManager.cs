// packages/com.midmanstudio.projectilesystem/Runtime/Network/ClientPredictionManager.cs
//
// SIMPLIFIED: The Rust sim prediction path has been removed entirely.
//   LocalProjectileManager now handles all Rust sim visuals on clients using the
//   same GPU instanced renderer (ProjectileRenderer2D/3D) as the host.
//
//   This class now only manages:
//     1. Physics projectile pool visuals (SpawnLocalPhysicsVisual) —
//        a temporary pool-object visual on the firing client during RPC round-trip.
//        Expires after MaxLifetime or on HitConfirmed.
//     2. Static rotation utilities — used by RaycastProjectileHandler and
//        PhysicsProjectileBase. These must remain public static.
//     3. SetLocalPlayerMidId — kept for bridge compatibility.
//
//   Removed:
//     - SpawnImmediatePrediction (Rust sim temp visual → now SpawnFiringClientBatch*)
//     - OnSpawnConfirmed (Rust sim ID linking → now LinkNetworkProjectileBatch)
//     - ReconcileSnapshot (Rust buffer position correction → now ReconcileSnapshots2D/3D)
//     - All PredictedProjectile, CircularBuffer, DeterministicMotionMath usage
//       (DeterministicMotionMath.cs stays — its formulas are still correct reference
//        material and may be used for Wave/Circular non-visual calculations.)

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
    // ── Physics visual entry ───────────────────────────────────────────────────

    internal sealed class PhysicsVisualEntry
    {
        public GameObject           Obj;
        public ProjectileVisualBase Script;
        public PoolableObjectType   PoolType;

        public Vector3 Origin;
        public Vector3 Direction;
        public float   Speed;
        public float   SpawnTime;
        public float   MaxLifetime;

        public bool    IsConfirmedHit;
        public Vector3 ConfirmedHitPos;
    }

    // ── ClientPredictionManager ───────────────────────────────────────────────

    public sealed class ClientPredictionManager : MonoBehaviour
    {
        #region Inspector

        [Header("Visual Pool Types (Physics Projectiles)")]
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

        // Physics pool visuals keyed by an arbitrary local ID (not a projId).
        // We use _nextPhysicsId as the key — there's no real projId for physics
        // on the firing client until the NetworkObject arrives.
        private readonly Dictionary<uint, PhysicsVisualEntry> _physicsVisuals = new(16);
        private uint _nextPhysicsId = 1;

        #endregion

        #region Public API — Identity

        public void SetLocalPlayerMidId(ulong midId) => _localPlayerMidId = midId;

        #endregion

        #region Public API — Physics Pool Visual

        /// <summary>
        /// Spawns a temporary travelling pool visual for a physics projectile on the
        /// firing client. The visual moves in a straight line at the given speed for up
        /// to MaxLifetime seconds, covering the RTT period before the NetworkObject arrives.
        ///
        /// Called from game code before or after FirePhysicsProjectileServerRpc.
        /// Returns a handle that can be passed to KillPhysicsVisual if you want to
        /// remove it explicitly (e.g. when the real NetworkObject spawns on this client).
        /// </summary>
        public uint SpawnLocalPhysicsVisual(
            ushort  configId,
            Vector3 origin,
            Vector3 direction,
            float   speed)
        {
            // No-op on server (host renders from authority buffer)
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                return 0;

            var cfg = ProjectileRegistry.Instance?.Get(configId);
            if (cfg == null) return 0;

            PoolableObjectType poolType = cfg.Is3D ? _visualPoolType3D : _visualPoolType2D;
            Vector3 dir = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector3.forward;

            var obj = LocalObjectPool.Instance?.GetObject(poolType, origin, GetDirectionRotation(dir));
            if (obj == null)
            {
                LogWarning($"SpawnLocalPhysicsVisual: pool null for {poolType}");
                return 0;
            }

            var vis = obj.GetComponent<ProjectileVisualBase>();
            vis?.InitializeClientVisual(configId, origin, dir, speed);

            uint id = _nextPhysicsId++;
            _physicsVisuals[id] = new PhysicsVisualEntry
            {
                Obj             = obj,
                Script          = vis,
                PoolType        = poolType,
                Origin          = origin,
                Direction       = dir,
                Speed           = speed,
                SpawnTime       = Time.time,
                MaxLifetime     = cfg.Lifetime,
                IsConfirmedHit  = false,
                ConfirmedHitPos = Vector3.zero
            };

            Log($"PhysicsVisual spawned id={id} config={configId}");
            return id;
        }

        /// <summary>
        /// Immediately kills and pools a specific physics visual by its local handle.
        /// Call when the real NetworkObject arrives so there is no visual overlap.
        /// </summary>
        public void KillPhysicsVisual(uint physicsVisualId)
        {
            if (!_physicsVisuals.TryGetValue(physicsVisualId, out var entry)) return;
            ReturnEntry(entry);
            _physicsVisuals.Remove(physicsVisualId);
        }

        #endregion

        #region Public API — Bridge Callbacks

        /// <summary>
        /// Called by MID_ProjectileNetworkBridge.HitConfirmedClientRpc.
        /// Moves the nearest physics visual toward the hit point and returns it to pool on arrival.
        /// Uses distance to hit point rather than projId because physics visuals don't have
        /// a real server projId — they are keyed by a local physics-visual ID.
        /// </summary>
        public void OnHitConfirmed(HitConfirmation confirmation)
        {
            // Find the physics visual closest to the hit position — this is a best-effort
            // match since we don't have a projId mapping for physics pool visuals.
            PhysicsVisualEntry best   = null;
            uint               bestId = 0;
            float              bestDist = float.MaxValue;

            foreach (var kv in _physicsVisuals)
            {
                if (kv.Value.Obj == null) continue;
                float d = Vector3.Distance(
                    kv.Value.Obj.transform.position, confirmation.HitPosition);
                if (d < bestDist)
                {
                    bestDist = d;
                    best     = kv.Value;
                    bestId   = kv.Key;
                }
            }

            // Only trigger if within a plausible range — avoid false matches
            if (best != null && bestDist < 20f)
            {
                best.IsConfirmedHit  = true;
                best.ConfirmedHitPos = confirmation.HitPosition;
            }
        }

        #endregion

        #region Update — Physics Visual Movement

        private void Update()
        {
            if (_physicsVisuals.Count == 0) return;

            float now      = Time.time;
            var   toRemove = new List<uint>(4);

            foreach (var kv in _physicsVisuals)
            {
                var entry = kv.Value;
                if (entry.Obj == null) { toRemove.Add(kv.Key); continue; }

                float elapsed = now - entry.SpawnTime;

                // Lifetime expired
                if (elapsed >= entry.MaxLifetime)
                {
                    ReturnEntry(entry);
                    toRemove.Add(kv.Key);
                    continue;
                }

                // Confirmed hit — glide to hit point
                if (entry.IsConfirmedHit)
                {
                    entry.Obj.transform.position = Vector3.MoveTowards(
                        entry.Obj.transform.position,
                        entry.ConfirmedHitPos,
                        entry.Speed * Time.deltaTime);

                    if (Vector3.Distance(entry.Obj.transform.position, entry.ConfirmedHitPos) < 0.05f)
                    {
                        ReturnEntry(entry);
                        toRemove.Add(kv.Key);
                    }
                    continue;
                }

                // Normal straight-line movement
                entry.Obj.transform.position = entry.Origin + entry.Direction * entry.Speed * elapsed;
                ApplyDirectionRotation(entry.Obj.transform, entry.Direction);
            }

            foreach (var id in toRemove) _physicsVisuals.Remove(id);
        }

        #endregion

        #region Cleanup

        private void ReturnEntry(PhysicsVisualEntry entry)
        {
            if (entry.Obj == null) return;
            if (entry.Script != null) entry.Script.ReturnToPoolImmediate();
            else LocalObjectPool.Instance?.ReturnObject(entry.Obj, entry.PoolType);
            entry.Obj    = null;
            entry.Script = null;
        }

        #endregion

        #region Static Rotation Utilities
        // These are used by RaycastProjectileHandler, PhysicsProjectileBase,
        // and previously by this class. Kept public static for compatibility.

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
