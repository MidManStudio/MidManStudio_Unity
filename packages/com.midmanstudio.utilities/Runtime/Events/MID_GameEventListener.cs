// MID_GameEventListener.cs
// MonoBehaviour that listens to a MID_GameEvent and fires a UnityEvent response.
// Self-registers/deregisters on Enable/Disable.

using UnityEngine;
using UnityEngine.Events;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Events
{
    /// <summary>
    /// Attach to any GameObject. Assign a MID_GameEvent and wire OnResponse.
    /// When the event is raised, OnResponse fires.
    /// </summary>
    public class MID_GameEventListener : MonoBehaviour
    {
        [SerializeField] private MID_GameEvent _gameEvent;
        [SerializeField] private UnityEvent    _onResponse;
        [SerializeField] private MID_LogLevel  _logLevel = MID_LogLevel.None;

        private void OnEnable()
        {
            if (_gameEvent == null)
            {
                MID_Logger.LogWarning(MID_LogLevel.Info, "No GameEvent assigned.",
                    nameof(MID_GameEventListener));
                return;
            }
            _gameEvent.Register(this);
        }

        private void OnDisable()
        {
            _gameEvent?.Deregister(this);
        }

        /// <summary>Called by the GameEvent when it is raised.</summary>
        public virtual void OnEventRaised()
        {
            MID_Logger.LogDebug(_logLevel, $"Responding to {_gameEvent?.name}.",
                nameof(MID_GameEventListener));
            _onResponse?.Invoke();
        }

        /// <summary>Raise the assigned event from code.</summary>
        public void RaiseEvent() => _gameEvent?.Raise();
    }
}
