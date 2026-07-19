// MID_LagCompensator — server-side lag compensation coordinator.
//
// WHAT THIS IS FOR:
//   Every client sees the world slightly in the past (network delay + their
//   own interpolation buffer). When they fire, they're aiming at where a
//   target looked like it was on THEIR screen — by the time that shot reaches
//   the server, the target has moved on. Rewinding every registered target to
//   its historical pose at the shooter's fire tick before running the
//   server's own validation raycast (RaycastProjectileHandler.ValidateHitServer
//   / CastServerRay) makes that raycast check against the position the
//   shooter actually saw, instead of the target's current position.
//
// USAGE:
//   using (MID_LagCompensator.BeginRewind(clientFireTick))
//   {
//       Physics.Raycast(...);   // every MID_LagCompensatedTarget is at its
//                                // historical pose for clientFireTick here
//   }   // <- automatically restored, even on early return / exception
//
//   BeginRewind is a STATIC method and is always safe to call — if no
//   MID_LagCompensator exists in the scene (feature not set up yet) or
//   tick <= 0 (no compensation requested), it returns a no-op scope rather
//   than auto-creating a singleton or throwing. Deliberately does NOT use
//   the base Singleton<T>.Instance auto-create getter for this reason — see
//   BeginRewind below.
//
// SETUP:
//   1. Add this component to any persistent GameObject in your networked
//      scene (server-side only matters; it no-ops entirely on clients).
//   2. Add MID_LagCompensatedTarget to every prefab that should be
//      rewindable for hit-checks (player capsule, etc).
//   3. Thread the shooter's fire tick (NetworkManager.ServerTime.Tick, taken
//      client-side at the moment of firing — see ProjectileFireRequest.
//      ClientFireTick, already wired in this package) through to wherever
//      you call BeginRewind server-side.
//
// NOT reentrant / not nestable: don't open a second BeginRewind scope before
// disposing the first. Every raycast fire event in this package only ever
// needs one scope open at a time (a whole pattern's N pellets share one
// rewind — see RaycastProjectileHandler.ServerHandleFirePattern), so this
// keeps the implementation simple rather than stack-based.

using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.Logging;

namespace MidManStudio.Netcode.LagCompensation
{
    /// <summary>
    /// Disposable handle returned by MID_LagCompensator.BeginRewind. Disposing
    /// it (typically via a `using` block) restores every target that scope
    /// rewound back to its real current pose. A default-constructed instance
    /// (owner == null) is a valid no-op — see BeginRewind.
    /// </summary>
    public readonly struct MID_LagCompensationScope : IDisposable
    {
        private readonly MID_LagCompensator _owner;

        internal MID_LagCompensationScope(MID_LagCompensator owner) => _owner = owner;

        public void Dispose() => _owner?.RestoreAll();
    }

    public class MID_LagCompensator : Singleton<MID_LagCompensator>
    {
        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        private readonly List<MID_LagCompensatedTarget> _targets  = new(32);
        private readonly List<MID_LagCompensatedTarget> _rewound  = new(32);

        private int  _lastRecordedTick = int.MinValue;
        private bool _scopeOpen;

        // ── Registration — called by MID_LagCompensatedTarget itself ──────────

        internal void Register(MID_LagCompensatedTarget target)
        {
            if (target != null && !_targets.Contains(target))
                _targets.Add(target);
        }

        internal void Unregister(MID_LagCompensatedTarget target)
        {
            _targets.Remove(target);
        }

        // ── Recording — one snapshot per server tick ───────────────────────────

        private void FixedUpdate()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;

            // FixedUpdate can run more often (or, under load, less often) than
            // the network tick actually advances — only record when the tick
            // itself has changed, so history stays exactly one entry per tick
            // regardless of physics timestep vs. tick rate mismatch.
            int tick = NetworkManager.Singleton.ServerTime.Tick;
            if (tick == _lastRecordedTick) return;
            _lastRecordedTick = tick;

            for (int i = 0; i < _targets.Count; i++)
                _targets[i]?.RecordSnapshot(tick);
        }

        // ── Rewind scope ────────────────────────────────────────────────────

        /// <summary>
        /// Rewinds every registered target to its historical pose at
        /// <paramref name="tick"/> (a Unity.Netcode server tick — see
        /// ProjectileFireRequest.ClientFireTick, which is already in this
        /// coordinate system, not the client's own local tick counter).
        /// Dispose the returned scope (a `using` block does this for you) to
        /// restore every target's real current pose.
        ///
        /// Safe to call with no MID_LagCompensator in the scene, or tick &lt;= 0
        /// (e.g. an offline/local-only fire path that never set a fire tick) —
        /// both return a no-op scope. Uses TryGetInstance rather than the
        /// auto-creating Instance getter deliberately: a raycast fired before
        /// anyone has opted into lag compensation should never spin up an
        /// empty compensator GameObject as a side effect.
        /// </summary>
        public static MID_LagCompensationScope BeginRewind(int tick)
        {
            if (tick <= 0) return default;
            var self = TryGetInstance();
            if (self == null) return default;
            return self.BeginRewindInternal(tick);
        }

        private MID_LagCompensationScope BeginRewindInternal(int tick)
        {
            if (_scopeOpen)
            {
                MID_Logger.LogWarning(_logLevel,
                    "BeginRewind called while a scope is already open — " +
                    "MID_LagCompensator scopes are not nestable. Returning a " +
                    "no-op scope; the outer scope's restore still applies.",
                    nameof(MID_LagCompensator));
                return default;
            }

            _scopeOpen = true;
            _rewound.Clear();

            for (int i = 0; i < _targets.Count; i++)
            {
                var t = _targets[i];
                if (t != null && t.Rewind(tick))
                    _rewound.Add(t);
            }

            return new MID_LagCompensationScope(this);
        }

        internal void RestoreAll()
        {
            for (int i = 0; i < _rewound.Count; i++)
                _rewound[i]?.Restore();

            _rewound.Clear();
            _scopeOpen = false;
        }
    }
}
