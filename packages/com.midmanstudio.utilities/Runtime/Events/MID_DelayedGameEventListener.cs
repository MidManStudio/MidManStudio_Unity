// MID_DelayedGameEventListener.cs
// Fires an immediate UnityEvent when the GameEvent is raised, then fires a
// delayed UnityEvent after _delay seconds using MID_TickDelay (zero allocation).
// Replaces the old coroutine/Task.Delay pattern.

using UnityEngine;
using UnityEngine.Events;
using MidManStudio.Core.Logging;
using MidManStudio.Core.TickDispatcher;

namespace MidManStudio.Core.Events
{
    /// <summary>
    /// Listener that fires an immediate response and a delayed response.
    /// Uses MID_TickDelay — no coroutine or Task allocation.
    /// </summary>
    public class MID_DelayedGameEventListener : MID_GameEventListener
    {
        [Header("Delayed Response")]
        [SerializeField] private float      _delay          = 1f;
        [SerializeField] private TickRate   _tickRate       = TickRate.Tick_0_1;
        [SerializeField] private UnityEvent _delayedResponse;
        [SerializeField] private MID_LogLevel _logLevel     = MID_LogLevel.None;

        private TickDelayHandle _pendingHandle;

        private void OnDisable()
        {
            // Cancel any in-flight delay when the object is disabled
            _pendingHandle.Cancel();
        }

        public override void OnEventRaised()
        {
            // Fire immediate response first (via base)
            base.OnEventRaised();

            MID_Logger.LogDebug(_logLevel,
                $"Scheduling delayed response in {_delay}s.",
                nameof(MID_DelayedGameEventListener));

            // Cancel any previous pending delay (prevents pile-up)
            _pendingHandle.Cancel();

            _pendingHandle = MID_TickDelay.After(_delay, FireDelayed, _tickRate);
        }

        private void FireDelayed()
        {
            MID_Logger.LogDebug(_logLevel, "Firing delayed response.",
                nameof(MID_DelayedGameEventListener));
            _delayedResponse?.Invoke();
        }
    }
}
