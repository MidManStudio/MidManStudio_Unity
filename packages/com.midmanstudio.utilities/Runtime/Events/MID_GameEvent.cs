// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/events.md, section "MID_GameEvent.cs"
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Events
{
    /// <summary>
    /// ScriptableObject-based event channel: zero coupling between sender
    /// and receiver. Create via <c>MidManStudio &gt; Utilities &gt; Game
    /// Event</c>, assign the resulting asset to one or more
    /// <see cref="MID_GameEventListener"/> components, and call
    /// <see cref="Raise"/> from code (or wire a UnityEvent in the listener's
    /// inspector) to notify every listener currently registered.
    /// </summary>
   [CreateAssetMenu(fileName="New Game Event",
    menuName="MidManStudio/Utilities/Game Event", order=110)]
    public class MID_GameEvent : ScriptableObject
    {
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        private readonly HashSet<MID_GameEventListener> _listeners = new();

        /// <summary>How many listeners are currently registered.</summary>
        public int ListenerCount => _listeners.Count;

        /// <summary>Raise the event — notifies all registered listeners.</summary>
        public void Raise()
        {
            MID_Logger.LogDebug(_logLevel, $"Raised — {_listeners.Count} listener(s).",
                nameof(MID_GameEvent), name);

            // Copy to list before iterating — listeners may deregister during raise
            var snapshot = new List<MID_GameEventListener>(_listeners);
            foreach (var listener in snapshot)
                listener.OnEventRaised();
        }

        /// <summary>Registers a listener to receive future <see cref="Raise"/> calls. Called automatically by <see cref="MID_GameEventListener.OnEnable"/>.</summary>
        public void Register(MID_GameEventListener listener)
        {
            if (listener == null) return;
            _listeners.Add(listener);
        }

        /// <summary>Removes a listener. Called automatically by <see cref="MID_GameEventListener.OnDisable"/>. Safe to call for a listener that isn't registered.</summary>
        public void Deregister(MID_GameEventListener listener)
        {
            _listeners.Remove(listener);
        }
    }
}
