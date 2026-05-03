// MID_NetworkRPCQueue.cs
// Batches NGO ClientRpc/ServerRpc payloads into one send per network tick.
// Reduces packet overhead when many small state updates fire in one frame.
//
// CONSTRAINT: T must implement both IMIDRPCPayload and INetworkSerializable.
// INetworkSerializable is required so NGO can write T into a FastBufferWriter.
//
// USAGE:
//   // 1. Define your payload
//   public struct HitEvent : IMIDRPCPayload, INetworkSerializable
//   {
//       public ulong  TargetId;
//       public float  Damage;
//       public string CollapseKey => null; // never collapse — all hits matter
//
//       public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
//       {
//           s.SerializeValue(ref TargetId);
//           s.SerializeValue(ref Damage);
//       }
//   }
//
//   // 2. Register your flush handler (in OnNetworkSpawn)
//   MID_NetworkRPCQueue.Instance.RegisterChannel<HitEvent>(FlushHits);
//
//   // 3. Enqueue from any system — batches automatically
//   MID_NetworkRPCQueue.Instance.Enqueue(new HitEvent { TargetId = id, Damage = 10f });
//
//   // 4. Implement your flush handler — receives the whole batch as one call
//   private void FlushHits(List<HitEvent> batch)
//   {
//       // Send one RPC with all hits this tick
//       SendHitBatchClientRpc(batch.ToArray());
//   }

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Netcode
{
    // ── Payload interface ─────────────────────────────────────────────────────

    /// <summary>
    /// Implement on any struct used as a batched RPC payload.
    /// Must also implement INetworkSerializable for NGO to serialize it.
    /// CollapseKey: payloads with the same key in one flush window are
    /// deduplicated (last-write-wins). Return null to never collapse.
    /// </summary>
    public interface IMIDRPCPayload
    {
        /// <summary>Deduplication key within one flush window. Null = never collapse.</summary>
        string CollapseKey { get; }
    }

    // ── Channel interface (avoids reflection in FlushAll) ─────────────────────

    internal interface IRPCChannel
    {
        void  Flush();
        int   PendingCount { get; }
    }

    // ── Per-type channel ──────────────────────────────────────────────────────

    internal class RPCChannel<T> : IRPCChannel
        where T : struct, IMIDRPCPayload, INetworkSerializable
    {
        // Collapsed payloads — last-write-wins per CollapseKey
        private readonly Dictionary<string, T> _collapsed   = new();
        // Uncollapsed payloads — all kept
        private readonly List<T>               _uncollapsed = new();
        // Reused flush list — passed to handler, cleared after
        private readonly List<T>               _flushBuffer = new();

        private readonly Action<List<T>> _flushHandler;

        public RPCChannel(Action<List<T>> handler) => _flushHandler = handler;

        public int PendingCount => _collapsed.Count + _uncollapsed.Count;

        public void Enqueue(T payload)
        {
            string key = payload.CollapseKey;
            if (!string.IsNullOrEmpty(key))
                _collapsed[key] = payload;
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
    }

    // ── Manager ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Tick-driven RPC batch queue for Unity Netcode for GameObjects.
    /// Attach to a persistent NetworkBehaviour GameObject.
    /// </summary>
    public class MID_NetworkRPCQueue : NetworkBehaviour
    {
        #region Singleton

        private static MID_NetworkRPCQueue _instance;
        public  static MID_NetworkRPCQueue Instance   => _instance;
        public  static bool                HasInstance => _instance != null;

        #endregion

        #region Inspector

        [Tooltip("How many times per second to flush all queued RPC payloads.")]
        [SerializeField] private float        _flushRate = 20f;

        [SerializeField] private MID_LogLevel _logLevel  = MID_LogLevel.Info;

        #endregion

        #region State

        private readonly Dictionary<Type, IRPCChannel> _channels = new();
        private NetworkTimer                           _timer;
        private int                                    _totalFlushes;

        #endregion

        #region Lifecycle

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            _timer    = new NetworkTimer(_flushRate);

            MID_Logger.LogInfo(_logLevel,
                $"MID_NetworkRPCQueue ready — flush rate {_flushRate}/s.",
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
        /// Call once per channel, typically in OnNetworkSpawn.
        /// T must implement both IMIDRPCPayload and INetworkSerializable.
        /// </summary>
        public void RegisterChannel<T>(Action<List<T>> flushHandler)
            where T : struct, IMIDRPCPayload, INetworkSerializable
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
        /// Enqueue a payload for dispatch on the next flush tick.
        /// Payloads with the same CollapseKey are deduplicated — last-write-wins.
        /// Safe to call every frame.
        /// </summary>
        public void Enqueue<T>(T payload)
            where T : struct, IMIDRPCPayload, INetworkSerializable
        {
            var type = typeof(T);
            if (!_channels.TryGetValue(type, out var channelObj))
            {
                MID_Logger.LogWarning(_logLevel,
                    $"No channel for {type.Name}. Call RegisterChannel<{type.Name}>() first.",
                    nameof(MID_NetworkRPCQueue));
                return;
            }

            ((RPCChannel<T>)channelObj).Enqueue(payload);
        }

        /// <summary>Unregister a channel. Call when the owning system despawns.</summary>
        public void UnregisterChannel<T>()
            where T : struct, IMIDRPCPayload, INetworkSerializable
        {
            _channels.Remove(typeof(T));
        }

        /// <summary>Total flush cycles executed since startup. Useful for profiling.</summary>
        public int TotalFlushes => _totalFlushes;

        /// <summary>Number of payloads pending across all channels.</summary>
        public int TotalPending()
        {
            int total = 0;
            foreach (var c in _channels.Values) total += c.PendingCount;
            return total;
        }

        #endregion

        #region Flush

        private void FlushAll()
        {
            _totalFlushes++;
            foreach (var channel in _channels.Values)
            {
                try   { channel.Flush(); }
                catch (Exception e)
                {
                    MID_Logger.LogError(_logLevel,
                        $"Flush error: {e.Message}",
                        nameof(MID_NetworkRPCQueue));
                }
            }
        }

        #endregion
    }
}
