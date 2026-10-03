// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/events.md, section "MID_GameEventListener.cs"
// ============================================================================
using UnityEngine;
using UnityEngine.Events;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Events
{
    /// <summary>
    /// Attach to any GameObject. Assign a <see cref="MID_GameEvent"/> and
    /// wire the response in the inspector (or handle
    /// <see cref="OnEventRaised"/> in code). Registers itself with the
    /// event on <see cref="OnEnable"/> and deregisters on
    /// <see cref="OnDisable"/>, so a disabled listener never responds.
    /// </summary>
    public class MID_GameEventListener : MonoBehaviour
    {
        [SerializeField] private MID_GameEvent _gameEvent;
        [SerializeField] private UnityEvent    _onResponse;
        [SerializeField] protected MID_LogLevel _logLevel = MID_LogLevel.None;

        /// <summary>Registers with the assigned event. Override (calling base) if a subclass needs its own enable-time setup.</summary>
        protected virtual void OnEnable()
        {
            if (_gameEvent == null)
            {
                MID_Logger.LogWarning(MID_LogLevel.Info, "No GameEvent assigned.",
                    nameof(MID_GameEventListener));
                return;
            }
            _gameEvent.Register(this);
        }

        /// <summary>
        /// Deregisters from the assigned event. Declared <c>protected
        /// virtual</c> rather than private specifically so a subclass can
        /// override it and call <c>base.OnDisable()</c> instead of
        /// silently shadowing it: Unity only invokes the most-derived
        /// declaration of a lifecycle method when both a base and derived
        /// class declare one privately, so a private override here would
        /// mean this deregistration never runs for subclass instances (see
        /// <see cref="MID_DelayedGameEventListener"/>'s fix history).
        /// </summary>
        protected virtual void OnDisable()
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
