// MID_EventUtilities.cs
// Subscription safety helpers for plain C# Action events.
// Prevents duplicate subscriptions and safe unsubscribes.
// Use these when managing Action fields directly instead of MID_EventBus.

using System;

namespace MidManStudio.Core.Events
{
    /// <summary>
    /// Safe subscribe/unsubscribe helpers for Action delegate fields.
    /// Prevents duplicate handlers and null-ref unsubscribes.
    /// </summary>
    public static class MID_EventUtilities
    {
        #region Duplicate-safe subscribe

        public static void Subscribe(ref Action eventAction, Action handler)
        {
            if (handler == null) return;
            if (!IsSubscribed(eventAction, handler))
                eventAction += handler;
        }

        public static void Subscribe<T>(ref Action<T> eventAction, Action<T> handler)
        {
            if (handler == null) return;
            if (!IsSubscribed(eventAction, handler))
                eventAction += handler;
        }

        public static void Subscribe<T1, T2>(ref Action<T1, T2> eventAction, Action<T1, T2> handler)
        {
            if (handler == null) return;
            if (!IsSubscribed(eventAction, handler))
                eventAction += handler;
        }

        #endregion

        #region Safe unsubscribe

        public static void Unsubscribe(ref Action eventAction, Action handler)
        {
            if (IsSubscribed(eventAction, handler))
                eventAction -= handler;
        }

        public static void Unsubscribe<T>(ref Action<T> eventAction, Action<T> handler)
        {
            if (IsSubscribed(eventAction, handler))
                eventAction -= handler;
        }

        public static void Unsubscribe<T1, T2>(ref Action<T1, T2> eventAction, Action<T1, T2> handler)
        {
            if (IsSubscribed(eventAction, handler))
                eventAction -= handler;
        }

        #endregion

        #region Subscription check

        public static bool IsSubscribed(Action eventAction, Action handler)
        {
            if (eventAction == null || handler == null) return false;
            foreach (var d in eventAction.GetInvocationList())
                if (d == (Delegate)handler) return true;
            return false;
        }

        public static bool IsSubscribed<T>(Action<T> eventAction, Action<T> handler)
        {
            if (eventAction == null || handler == null) return false;
            foreach (var d in eventAction.GetInvocationList())
                if (d == (Delegate)handler) return true;
            return false;
        }

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
