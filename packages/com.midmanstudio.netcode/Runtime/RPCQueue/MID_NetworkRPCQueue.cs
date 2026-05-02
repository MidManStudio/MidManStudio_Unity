// MID_NetworkRPCQueue.cs
// Batches NGO RPC calls into one send per network tick instead of per-call.
// Dramatically reduces packet overhead when many small state updates fire in one frame
// (e.g. 20 players sending position corrections simultaneously).
//
// HOW IT WORKS:
//   1. Callers enqueue a pending RPC payload (any struct implementing IMIDRPCPayload).
//   2. Every network tick (driven by NetworkTimer) the queue flushes — one RPC per channel.
//   3. Duplicate payloads in the same flush window are collapsed if TCollapseKey matches.
//
// USAGE:
//   // Define a payload
//   public struct PositionUpdate : IMIDRPCPayload
//   {
//       public Vector3 Position;
//       public string CollapseKey => $"pos_{OwnerId}";  // one update per owner per tick
//       public ulong OwnerId;
//   }
//
//   // Enqueue from any system
//   MID_NetworkRPCQueue.Instance.Enqueue(new PositionUpdate { Position = pos, OwnerId = id });
//
//   // The queue fires your registered handler each flush
//   MID_NetworkRPCQueue.Instance.RegisterChannel<PositionUpdate>(FlushPositionUpdates);
//
//   void FlushPositionUpdates(List<PositionUpdate> batch) { /* send one ClientRpc with batch */ }

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Netcode
{
    // ── Payload interface ─────────────────────────────────────────────────────

    /// <summary>
    /// Implement on any struct used as an RPC queue payload.
    /// CollapseKey: payloads with the same key in one flush window are deduplicated
    /// (last-write-wins). Return null or empty to never collapse.
    /// </summary>
    public interface IMIDRPCPayload
    {
        /// <summary>Deduplication key within one flush window. Null = never collapse.</summary>
        string CollapseKey { get; }
    }

    // ── Per-channel queue ─────────────────────────────────────────────────────

    internal class RPCChannel<T> where T : struct, IMIDRPCPayload
    {
        private readonly Dictionary<string, T> _collapsed = new(); // keyed by CollapseKey
        private readonly List<T>               _uncollapsed = new();
        private readonly Action<List<T>>        _flushHandler;
        private readonly List<T>               _flushBuffer = new();

        public RPCChannel(Action<List<T>> handler) => _flushHandler = handler;

        public void Enqueue(T payload)
        {
            string key = payload.CollapseKey;
            if (!string.IsNullOrEmpty(key))
                _collapsed[key] = payload;  // last-write-wins per key
            else
                _uncollapsed.Add(payload);
        }

        public void Flush()
        {
            if (_collapsed.Count == 0 && _uncollapsed.Count == 0) return;

            _flushBuffer.Clear();
            _flushBuffer.AddRange(_collapsed.Values);
            _flushBuffer.AddRange(_uncollapsed);

            _collapsed.Clear();
            _uncollapsed.Clear();

            _flushHandler?.Invoke(_flushBuffer);
        }

        public int PendingCount => _collapsed.Count + _uncollapsed.Count;
    }

    // ── Manager ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Tick-driven RPC batch queue. Reduces NGO packet count by flushing
    /// all enqueued payloads once per network tick.
    /// </summary>
    public class MID_NetworkRPCQueue : NetworkBehaviour
    {
        #region Singleton

        private static MID_NetworkRPCQueue _instance;
        public  static MID_NetworkRPCQueue Instance => _instance;
        public  static bool                HasInstance => _instance != null;

        #endregion

        #region Inspector

        [SerializeField] private float       _serverTickRate = 20f;
        [SerializeField] private MID_LogLevel _logLevel      = MID_LogLevel.Info;

        #endregion

        #region State

        private readonly Dictionary<Type, object> _channels = new();
        private NetworkTimer                      _timer;
        private int                               _totalFlushed;

        #endregion

        #region Lifecycle

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            _timer    = new NetworkTimer(_serverTickRate);

            MID_Logger.LogInfo(_logLevel,
                $"MID_NetworkRPCQueue ready — tick rate {_serverTickRate}/s.",
                nameof(MID_NetworkRPCQueue));
        }

        public override void OnDestroy()
        {
            if (_instance == this) _instance = null;
            base.OnDestroy();
        }

        private void Update()
        {
            _timer.Update(Time.deltaTime);
            while (_timer.ShouldTick())
                FlushAll();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Register a flush handler for payload type T.
        /// Call once at initialization — typically from OnNetworkSpawn.
        /// </summary>
        public void RegisterChannel<T>(Action<List<T>> flushHandler)
            where T : struct, IMIDRPCPayload
        {
            var type = typeof(T);
            if (_channels.ContainsKey(type))
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Channel for {type.Name} already registered — overwriting.",
                    nameof(MID_NetworkRPCQueue));
            }
            _channels[type] = new RPCChannel<T>(flushHandler);

            MID_Logger.LogDebug(_logLevel,
                $"Registered RPC channel for {type.Name}.",
                nameof(MID_NetworkRPCQueue));
        }

        /// <summary>
        /// Enqueue a payload for batched dispatch on next tick.
        /// Safe to call from any system every frame.
        /// </summary>
        public void Enqueue<T>(T payload) where T : struct, IMIDRPCPayload
        {
            var type = typeof(T);
            if (!_channels.TryGetValue(type, out var channelObj))
            {
                MID_Logger.LogWarning(_logLevel,
                    $"No channel registered for {type.Name}. Call RegisterChannel first.",
                    nameof(MID_NetworkRPCQueue));
                return;
            }

            ((RPCChannel<T>)channelObj).Enqueue(payload);
        }

        /// <summary>Remove a channel. Call when the owning system is despawned.</summary>
        public void UnregisterChannel<T>() where T : struct, IMIDRPCPayload
        {
            _channels.Remove(typeof(T));
        }

        /// <summary>Total payloads flushed since startup. Useful for profiling.</summary>
        public int TotalFlushed => _totalFlushed;

        #endregion

        #region Flush

        private void FlushAll()
        {
            foreach (var channel in _channels.Values)
            {
                // Use reflection-free virtual dispatch via interface
                (channel as IFlushable)?.Flush();
            }
        }

        #endregion
    }

    // ── Internal flush interface — avoids reflection in FlushAll ─────────────

    internal interface IFlushable { void Flush(); }

    // Make RPCChannel implement it
    internal partial class RPCChannel<T> : IFlushable where T : struct, IMIDRPCPayload { }
}
