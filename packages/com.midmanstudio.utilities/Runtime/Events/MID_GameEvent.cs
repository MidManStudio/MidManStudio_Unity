// MID_GameEvent.cs
// ScriptableObject-based event channel. Zero coupling between sender and receiver.
// Create via: MidManStudio > Utilities > Game Event
//
// USAGE:
//   1. Create a MID_GameEvent asset (e.g. "OnPlayerDied").
//   2. Assign it to a MID_GameEventListener on the receiving GameObject.
//   3. Raise from code: myEvent.Raise();
//   4. Or wire to a UnityEvent in the listener inspector.

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Events
{
   [CreateAssetMenu(fileName="New Game Event",
    menuName="MidManStudio/Utilities/Game Event", order=110)]
    public class MID_GameEvent : ScriptableObject
    {
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        private readonly HashSet<MID_GameEventListener> _listeners = new();

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

        public void Register(MID_GameEventListener listener)
        {
            if (listener == null) return;
            _listeners.Add(listener);
        }

        public void Deregister(MID_GameEventListener listener)
        {
            _listeners.Remove(listener);
        }
    }
}
