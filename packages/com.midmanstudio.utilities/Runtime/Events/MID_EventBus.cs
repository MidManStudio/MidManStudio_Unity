// MID_EventBus.cs
// Typed static event bus. Fire-and-forget global events without ScriptableObject assets.
// Pairs with MID_SusValue for fire-and-forget vs persistent-value use cases.
//
// WHEN TO USE WHICH:
//   MID_GameEvent  — designer-wired, inspector-configured, cross-scene
//   MID_EventBus   — code-only, typed, one-line subscribe/fire, no asset required
//   MID_SusValue   — reactive value that remembers its current state
//
// USAGE:
//   // Subscribe
//   MID_EventBus<PlayerDiedEvent>.Subscribe(OnPlayerDied);
//
//   // Fire
//   MID_EventBus<PlayerDiedEvent>.Raise(new PlayerDiedEvent { PlayerId = 5 });
//
//   // Unsubscribe
//   MID_EventBus<PlayerDiedEvent>.Unsubscribe(OnPlayerDied);
//
//   // Clear all (call on scene unload)
//   MID_EventBus<PlayerDiedEvent>.ClearAll();

using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Events
{
    // ── Marker interface — implement on all event payload structs/classes ─────

    /// <summary>
    /// Implement on any struct or class used as an event payload with MID_EventBus.
    /// Using a marker interface prevents accidental use of unintended types.
    /// </summary>
    public interface IMIDEvent { }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Typed static event bus. One channel per event type T.
    /// Thread-safe for subscribe/unsubscribe but Raise should be called from main thread.
    /// </summary>
    public static class MID_EventBus<T> where T : IMIDEvent
    {
        private static readonly HashSet<Action<T>> _subscribers = new();
        private static readonly object             _lock        = new();

        // ── Log level for this channel — settable at runtime ──────────────────
        public static MID_LogLevel LogLevel = MID_LogLevel.None;

        // ── Subscribe ─────────────────────────────────────────────────────────

        /// <summary>
        /// Subscribe to this event channel.
        /// Duplicate subscriptions are silently ignored.
        /// Always pair with Unsubscribe to avoid leaks.
        /// </summary>
        public static void Subscribe(Action<T> handler)
        {
            if (handler == null) return;
            lock (_lock) _subscribers.Add(handler);

            MID_Logger.LogDebug(LogLevel,
                $"Subscribed to {typeof(T).Name}. Total: {_subscribers.Count}",
                nameof(MID_EventBus<T>));
        }

        // ── Unsubscribe ───────────────────────────────────────────────────────

        /// <summary>
        /// Unsubscribe from this event channel. Safe to call if not subscribed.
        /// </summary>
        public static void Unsubscribe(Action<T> handler)
        {
            if (handler == null) return;
            lock (_lock) _subscribers.Remove(handler);

            MID_Logger.LogDebug(LogLevel,
                $"Unsubscribed from {typeof(T).Name}. Remaining: {_subscribers.Count}",
                nameof(MID_EventBus<T>));
        }

        // ── Raise ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Raise the event. All subscribers receive the payload.
        /// Exceptions in individual handlers are caught and logged — other handlers still fire.
        /// </summary>
        public static void Raise(T payload)
        {
            MID_Logger.LogDebug(LogLevel,
                $"Raised {typeof(T).Name} — {_subscribers.Count} subscriber(s).",
                nameof(MID_EventBus<T>));

            // Snapshot under lock — handlers may unsubscribe during raise
            Action<T>[] snapshot;
            lock (_lock)
            {
                snapshot = new Action<T>[_subscribers.Count];
                _subscribers.CopyTo(snapshot);
            }

            foreach (var handler in snapshot)
            {
                try
                {
                    handler(payload);
                }
                catch (Exception e)
                {
                    MID_Logger.LogError(MID_LogLevel.Error,
                        $"Exception in MID_EventBus<{typeof(T).Name}> handler: {e.Message}",
                        nameof(MID_EventBus<T>));
                }
            }
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        /// <summary>
        /// Remove all subscribers from this channel.
        /// Call on scene unload to prevent stale subscriptions.
        /// </summary>
        public static void ClearAll()
        {
            int count;
            lock (_lock) { count = _subscribers.Count; _subscribers.Clear(); }

            MID_Logger.LogInfo(MID_LogLevel.Info,
                $"Cleared {count} subscriber(s) from {typeof(T).Name}.",
                nameof(MID_EventBus<T>));
        }

        public static int SubscriberCount
        {
            get { lock (_lock) return _subscribers.Count; }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Utilities for bulk-clearing multiple event bus channels at once.
    /// Add your event types here and call MID_EventBusRegistry.ClearAll() on scene unload.
    /// </summary>
    public static class MID_EventBusRegistry
    {
        private static readonly List<Action> _clearActions = new();

        /// <summary>
        /// Register a channel's ClearAll for bulk teardown.
        /// Call once at startup: MID_EventBusRegistry.Register&lt;MyEvent&gt;();
        /// </summary>
        public static void Register<T>() where T : IMIDEvent
        {
            _clearActions.Add(MID_EventBus<T>.ClearAll);
        }

        /// <summary>Clear all registered event bus channels. Call on scene unload.</summary>
        public static void ClearAll()
        {
            foreach (var clear in _clearActions)
            {
                try { clear(); }
                catch (Exception e)
                {
                    Debug.LogError($"[MID_EventBusRegistry] ClearAll error: {e.Message}");
                }
            }
        }
    }
}
