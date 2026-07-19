// MID_LagCompensatedTarget — attach to any networked hitbox root that raycast
// hit-checks should be lag-compensated against (player capsule, etc).
//
// Records a fixed-capacity ring of (tick, position, rotation) snapshots,
// server-side only. Ring buffer shape is a direct port of mid-log's
// console_buffer.rs (ConsoleBufferInner): fixed array, wrapping write head,
// monotonic write counter, oldest entry silently overwritten once full —
// same structure, applied to pose history instead of log entries.
//
// Does nothing on clients — history and rewinding are both server-only
// concerns; clients never call Rewind/Restore.

using UnityEngine;
using Unity.Netcode;

namespace MidManStudio.Netcode.LagCompensation
{
    [DisallowMultipleComponent]
    public class MID_LagCompensatedTarget : NetworkBehaviour
    {
        #region Inspector

        [Tooltip("Transform actually moved during a rewind. Defaults to this " +
                 "object's own transform if left empty. Move colliders under " +
                 "THIS transform (or put it on the same object as them) — " +
                 "anything parented under it follows automatically.")]
        [SerializeField] private Transform _hitboxRoot;

        [Tooltip("Ticks of history retained. At a 30Hz server tick, 64 ≈ 2.1s — " +
                 "keep this comfortably above your expected worst-case RTT. " +
                 "Memory cost is trivial (Vector3 + Quaternion per tick per target).")]
        [SerializeField, Min(4)] private int _historyCapacity = 64;

        #endregion

        #region Ring buffer

        private struct Snapshot
        {
            public int        Tick;
            public Vector3    Position;
            public Quaternion Rotation;
        }

        private Snapshot[] _ring;
        private int        _writeHead;
        private int        _count;

        #endregion

        #region Rewind state

        private Vector3    _savedPosition;
        private Quaternion _savedRotation;
        private bool       _isRewound;

        #endregion

        #region NGO lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (!IsServer) return; // history is a server-only concern

            if (_hitboxRoot == null) _hitboxRoot = transform;

            _ring      = new Snapshot[Mathf.Max(4, _historyCapacity)];
            _writeHead = 0;
            _count     = 0;
            _isRewound = false;

            // TryGetInstance, not Instance — see MID_LagCompensator.BeginRewind's
            // doc comment. A target spawning before anyone has added a
            // compensator to the scene should not create one as a side effect.
            MID_LagCompensator.TryGetInstance()?.Register(this);
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
            {
                // Safety net — should never actually be needed, since a rewind
                // scope always restores before the RPC handler that opened it
                // returns, long before any despawn could happen. Guards against
                // a target being destroyed mid-scope by something unrelated.
                if (_isRewound) Restore();

                MID_LagCompensator.TryGetInstance()?.Unregister(this);
            }

            base.OnNetworkDespawn();
        }

        #endregion

        #region Recording — called by MID_LagCompensator once per server tick

        internal void RecordSnapshot(int tick)
        {
            if (_ring == null || _hitboxRoot == null || _isRewound) return;

            _ring[_writeHead] = new Snapshot
            {
                Tick     = tick,
                Position = _hitboxRoot.position,
                Rotation = _hitboxRoot.rotation
            };

            _writeHead = (_writeHead + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }

        #endregion

        #region Rewind / Restore — called by MID_LagCompensator's scope

        /// <summary>
        /// Moves this target to its interpolated historical pose at
        /// <paramref name="tick"/>, remembering the current pose so Restore()
        /// can put it back. Returns false (no-op) if there's no history yet.
        /// </summary>
        internal bool Rewind(int tick)
        {
            if (_hitboxRoot == null || _count == 0) return false;
            if (!SampleAt(tick, out Vector3 pos, out Quaternion rot)) return false;

            _savedPosition = _hitboxRoot.position;
            _savedRotation = _hitboxRoot.rotation;

            _hitboxRoot.position = pos;
            _hitboxRoot.rotation = rot;
            _isRewound = true;
            return true;
        }

        /// <summary>Restores the real current pose saved by Rewind().</summary>
        internal void Restore()
        {
            if (!_isRewound) return;
            _hitboxRoot.position = _savedPosition;
            _hitboxRoot.rotation = _savedRotation;
            _isRewound = false;
        }

        #endregion

        #region History sampling

        /// <summary>
        /// Finds the two ring entries bracketing <paramref name="tick"/> and
        /// Lerp/Slerps between them. Clamps to the newest entry if
        /// <paramref name="tick"/> is more recent than anything recorded, and
        /// to the oldest if it's older than the whole retained window (best
        /// effort — a requested rewind further back than _historyCapacity
        /// ticks can't be reconstructed exactly).
        /// </summary>
        private bool SampleAt(int tick, out Vector3 pos, out Quaternion rot)
        {
            pos = _hitboxRoot.position;
            rot = _hitboxRoot.rotation;
            if (_count == 0) return false;

            int capacity  = _ring.Length;
            int newestIdx = (_writeHead - 1 + capacity) % capacity;
            Snapshot newest = _ring[newestIdx];

            if (tick >= newest.Tick)
            {
                pos = newest.Position;
                rot = newest.Rotation;
                return true;
            }

            int oldestIdx = (_writeHead - _count + capacity) % capacity;
            Snapshot oldest = _ring[oldestIdx];

            if (tick <= oldest.Tick)
            {
                pos = oldest.Position;
                rot = oldest.Rotation;
                return true;
            }

            // Walk newest → oldest looking for the bracketing pair.
            Snapshot after = newest;
            for (int i = _count - 2; i >= 0; i--)
            {
                int idx = (_writeHead - _count + i + capacity) % capacity;
                Snapshot before = _ring[idx];

                if (before.Tick <= tick && tick <= after.Tick)
                {
                    float span = after.Tick - before.Tick;
                    float t    = span > 0f ? (tick - before.Tick) / span : 0f;
                    pos = Vector3.Lerp(before.Position, after.Position, t);
                    rot = Quaternion.Slerp(before.Rotation, after.Rotation, t);
                    return true;
                }

                after = before;
            }

            // Unreachable in practice (the clamp checks above cover the full
            // range) — fall back to newest just in case.
            pos = newest.Position;
            rot = newest.Rotation;
            return true;
        }

        #endregion

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (_ring == null || _count == 0) return;

            Gizmos.color = new Color(1f, 0.5f, 0f, 0.6f);
            int capacity = _ring.Length;
            for (int i = 0; i < _count; i++)
            {
                int idx = (_writeHead - _count + i + capacity) % capacity;
                Gizmos.DrawWireSphere(_ring[idx].Position, 0.1f);
            }
        }
#endif
    }
}
