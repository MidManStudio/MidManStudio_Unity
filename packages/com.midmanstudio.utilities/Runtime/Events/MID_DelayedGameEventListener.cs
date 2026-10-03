// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/events.md, section "MID_DelayedGameEventListener.cs"
// ============================================================================
using UnityEngine;
using UnityEngine.Events;
using MidManStudio.Core.Logging;
using MidManStudio.Core.TickDispatcher;

namespace MidManStudio.Core.Events
{
    /// <summary>
    /// Listener that fires an immediate response and a delayed response.
    /// Uses <c>MID_TickDelay</c>, no coroutine or Task allocation.
    /// </summary>
    public class MID_DelayedGameEventListener : MID_GameEventListener
    {
        [Header("Delayed Response")]
        [SerializeField] private float      _delay          = 1f;
        [SerializeField] private TickRate   _tickRate       = TickRate.Tick_0_1;
        [SerializeField] private UnityEvent _delayedResponse;

        private TickDelayHandle _pendingHandle;

        /// <summary>Cancels any in-flight delay, then deregisters via <c>base.OnDisable()</c>. See this file's Fixes and Problems entry for why the base call matters here.</summary>
        protected override void OnDisable()
        {
            // Cancel any in-flight delay when the object is disabled
            _pendingHandle.Cancel();
            base.OnDisable();
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
