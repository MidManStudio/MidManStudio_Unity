// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/events.md, section "MID_EventUtilities.cs"
// ============================================================================
using System;

namespace MidManStudio.Core.Events
{
    /// <summary>
    /// Safe subscribe/unsubscribe helpers for plain <c>Action</c> delegate
    /// fields (not <see cref="MID_EventBus{T}"/> or
    /// <see cref="MID_GameEvent"/>). Prevents duplicate handlers and
    /// null-ref unsubscribes when you're managing an Action field directly.
    /// Overloaded for zero-, one-, and two-parameter Action delegates.
    /// </summary>
    public static class MID_EventUtilities
    {
        #region Duplicate-safe subscribe

        /// <summary>Adds <paramref name="handler"/> to <paramref name="eventAction"/> unless it's already subscribed.</summary>
        public static void Subscribe(ref Action eventAction, Action handler)
        {
            if (handler == null) return;
            if (!IsSubscribed(eventAction, handler))
                eventAction += handler;
        }

        /// <summary>One-parameter overload of <see cref="Subscribe(ref Action, Action)"/>.</summary>
        public static void Subscribe<T>(ref Action<T> eventAction, Action<T> handler)
        {
            if (handler == null) return;
            if (!IsSubscribed(eventAction, handler))
                eventAction += handler;
        }

        /// <summary>Two-parameter overload of <see cref="Subscribe(ref Action, Action)"/>.</summary>
        public static void Subscribe<T1, T2>(ref Action<T1, T2> eventAction, Action<T1, T2> handler)
        {
            if (handler == null) return;
            if (!IsSubscribed(eventAction, handler))
                eventAction += handler;
        }

        #endregion

        #region Safe unsubscribe

        /// <summary>Removes <paramref name="handler"/> from <paramref name="eventAction"/> if it's currently subscribed; a no-op otherwise.</summary>
        public static void Unsubscribe(ref Action eventAction, Action handler)
        {
            if (IsSubscribed(eventAction, handler))
                eventAction -= handler;
        }

        /// <summary>One-parameter overload of <see cref="Unsubscribe(ref Action, Action)"/>.</summary>
        public static void Unsubscribe<T>(ref Action<T> eventAction, Action<T> handler)
        {
            if (IsSubscribed(eventAction, handler))
                eventAction -= handler;
        }

        /// <summary>Two-parameter overload of <see cref="Unsubscribe(ref Action, Action)"/>.</summary>
        public static void Unsubscribe<T1, T2>(ref Action<T1, T2> eventAction, Action<T1, T2> handler)
        {
            if (IsSubscribed(eventAction, handler))
                eventAction -= handler;
        }

        #endregion

        #region Subscription check

        /// <summary>True if <paramref name="handler"/> is already in <paramref name="eventAction"/>'s invocation list.</summary>
        public static bool IsSubscribed(Action eventAction, Action handler)
        {
            if (eventAction == null || handler == null) return false;
            foreach (var d in eventAction.GetInvocationList())
                if (d == (Delegate)handler) return true;
            return false;
        }

        /// <summary>One-parameter overload of <see cref="IsSubscribed(Action, Action)"/>.</summary>
        public static bool IsSubscribed<T>(Action<T> eventAction, Action<T> handler)
        {
            if (eventAction == null || handler == null) return false;
            foreach (var d in eventAction.GetInvocationList())
                if (d == (Delegate)handler) return true;
            return false;
        }

        /// <summary>Two-parameter overload of <see cref="IsSubscribed(Action, Action)"/>.</summary>
        public static bool IsSubscribed<T1, T2>(Action<T1, T2> eventAction, Action<T1, T2> handler)
        {
            if (eventAction == null || handler == null) return false;
            foreach (var d in eventAction.GetInvocationList())
                if (d == (Delegate)handler) return true;
            return false;
        }

        #endregion
    }
}
