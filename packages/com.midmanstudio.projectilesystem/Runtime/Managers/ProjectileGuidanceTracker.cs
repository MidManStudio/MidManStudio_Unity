// ProjectileGuidanceTracker — the missing "assign a target, it homes
// automatically" layer on top of the RustSim Guided movement type.
//
// WHAT ALREADY EXISTED before this file:
//   - ProjectileMovementType.Guided is a real, selectable value on
//     ProjectileConfigSO / RustSpawnParams.
//   - MID_MasterProjectileSystem.SetHomingDirection2D/3D writes a desired
//     steering direction straight into the native sim's Ax/Ay fields for a
//     given ProjId (the Rust tick then presumably turns the projectile
//     toward that heading each tick — this file doesn't change or guess at
//     that turn-rate behaviour, it only supplies a fresh direction).
//   - Nothing called SetHomingDirection2D/3D more than once, and nothing
//     tracked a target over time. You had to compute and push a direction
//     yourself, every tick, forever.
//
// WHAT THIS FILE ADDS:
//   Register a ProjId against a Transform (or a fixed point) once, and this
//   component recomputes (target - currentPos) and pushes it every frame
//   for you until the projectile dies or the target is gone.
//
// SCOPE NOTE: this only drives the AUTHORITATIVE copy of a projectile (see
// MID_MasterProjectileSystem.SetHomingDirection2D/3D — it only writes when
// IsServer or !IsNetworked, matching every other per-projectile call in that
// class). On a pure network client that doesn't own the projectile, calls
// here are harmless no-ops, same as calling SetHomingDirection2D directly
// would be. Client-side visual prediction of a curving path is a separate,
// larger problem — the existing snapshot-reconciliation code in
// LocalProjectileManager already documents that it approximates Guided
// movement as a straight line between snapshots, and this file doesn't
// change that.

using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Singleton;

namespace MidManStudio.Projectiles.Managers
{
    /// <summary>
    /// Drop this in once (or just call ProjectileGuidanceTracker.Instance. —
    /// it auto-creates via Singleton&lt;T&gt; same as every other manager in
    /// this package) and register a ProjId + Transform pair to make that
    /// projectile home toward the target every frame.
    /// </summary>
    public sealed class ProjectileGuidanceTracker : Singleton<ProjectileGuidanceTracker>
    {
        private struct Entry
        {
            public Transform TargetTransform; // null if using a fixed point instead
            public Vector3   FixedPoint;
            public bool      UseFixedPoint;
            public bool      Is3D;
        }

        private readonly Dictionary<uint, Entry> _entries = new Dictionary<uint, Entry>(64);
        private List<uint> _scratchRemoveList; // reused across ticks to avoid per-frame alloc once warmed up

        /// <summary>
        /// Fired when a projectile stops being tracked, either because it
        /// died (position lookup failed) or its target Transform was
        /// destroyed. Does NOT fire on manual Unregister().
        /// </summary>
        public event Action<uint> OnGuidanceEnded;

        #region Public API

        /// <summary>Home this 2D projectile toward a moving Transform every frame.</summary>
        public void RegisterGuidedTarget2D(uint projId, Transform target)
        {
            if (target == null) return;
            _entries[projId] = new Entry { TargetTransform = target, Is3D = false };
        }

        /// <summary>Home this 2D projectile toward a fixed world-space point.</summary>
        public void RegisterGuidedTarget2D(uint projId, Vector2 fixedPoint)
        {
            _entries[projId] = new Entry
            {
                UseFixedPoint = true,
                FixedPoint    = new Vector3(fixedPoint.x, fixedPoint.y, 0f),
                Is3D          = false
            };
        }

        /// <summary>Home this 3D projectile toward a moving Transform every frame.</summary>
        public void RegisterGuidedTarget3D(uint projId, Transform target)
        {
            if (target == null) return;
            _entries[projId] = new Entry { TargetTransform = target, Is3D = true };
        }

        /// <summary>Home this 3D projectile toward a fixed world-space point.</summary>
        public void RegisterGuidedTarget3D(uint projId, Vector3 fixedPoint)
        {
            _entries[projId] = new Entry { UseFixedPoint = true, FixedPoint = fixedPoint, Is3D = true };
        }

        /// <summary>Stop tracking this projectile (does not fire OnGuidanceEnded).</summary>
        public void Unregister(uint projId) => _entries.Remove(projId);

        public bool IsTracking(uint projId) => _entries.ContainsKey(projId);

        public void ClearAll() => _entries.Clear();

        #endregion

        #region Update Loop
        //
        // Runs in Update(), not FixedUpdate(). The sim tick consumes Ax/Ay
        // once per FixedUpdate; Update() always runs at least once before the
        // next FixedUpdate regardless of framerate or this component's
        // position in Script Execution Order, so the direction is always
        // fresh by the time it's read — without requiring Su to configure
        // execution order at all.

        private void Update()
        {
            if (_entries.Count == 0) return;

            var master = MID_MasterProjectileSystem.HasInstance ? MID_MasterProjectileSystem.Instance : null;
            if (master == null) return;

            foreach (var kv in _entries)
            {
                uint  projId = kv.Key;
                Entry entry  = kv.Value;

                // Target lost (Transform destroyed) and not a fixed-point entry -> give up.
                if (!entry.UseFixedPoint && entry.TargetTransform == null)
                {
                    MarkForRemoval(projId);
                    continue;
                }

                if (entry.Is3D)
                {
                    if (!master.TryGetProjectilePosition3D(projId, out Vector3 pos))
                    {
                        MarkForRemoval(projId);
                        continue;
                    }
                    Vector3 targetPos = entry.UseFixedPoint ? entry.FixedPoint : entry.TargetTransform.position;
                    Vector3 dir       = targetPos - pos;
                    if (dir.sqrMagnitude > 0.0001f)
                        master.SetHomingDirection3D(projId, dir);
                }
                else
                {
                    if (!master.TryGetProjectilePosition2D(projId, out Vector2 pos))
                    {
                        MarkForRemoval(projId);
                        continue;
                    }
                    Vector2 targetPos = entry.UseFixedPoint
                        ? new Vector2(entry.FixedPoint.x, entry.FixedPoint.y)
                        : (Vector2)entry.TargetTransform.position;
                    Vector2 dir = targetPos - pos;
                    if (dir.sqrMagnitude > 0.0001f)
                        master.SetHomingDirection2D(projId, dir);
                }
            }

            if (_scratchRemoveList != null && _scratchRemoveList.Count > 0)
            {
                for (int i = 0; i < _scratchRemoveList.Count; i++)
                {
                    uint id = _scratchRemoveList[i];
                    _entries.Remove(id);
                    OnGuidanceEnded?.Invoke(id);
                }
                _scratchRemoveList.Clear();
            }
        }

        private void MarkForRemoval(uint projId)
        {
            // Can't mutate _entries mid-foreach — collect and remove after the loop.
            (_scratchRemoveList ??= new List<uint>(8)).Add(projId);
        }

        #endregion
    }
}
