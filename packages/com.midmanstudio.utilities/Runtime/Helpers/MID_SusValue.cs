// MID_SusValue.cs
// Subscribable Value
// Generic observable value container.
// Subscribe to value changes or any update attempt.
// Part of com.midmanstudio.utilities — no game dependencies.
//
// TWO SUBSCRIPTION TYPES:
//   SubscribeToValueChanged — fires only when the value actually changes (inequality check)
//   SubscribeToAnyUpdate    — fires every time .Value is set, even if value is the same
//
// DUPLICATE PREVENTION:
//   Both subscription sets use HashSet — adding the same delegate twice is a no-op.
//
// VALIDATION:
//   Optionally supply a predicate via SetValidationFunction(). Values that fail
//   validation are rejected silently (with a LogWarning).
//
// IMPLICIT CONVERSION:
//   MID_SusValue<float> susFloat = new(1f);
//   float raw = susFloat; // works via implicit operator T

using System;
using System.Collections.Generic;
using UnityEngine;

namespace MidManStudio.Core.ObservableValues
{
    /// <summary>
    /// Marker interface so managers can operate on MID_SusValue instances
    /// without knowing the generic type parameter.
    /// </summary>
    public interface IManagedSusValue
    {
        void   ClearAllSubscriptions();
        bool   IsManaged { get; }
        string ValueId   { get; }
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Observable value container. Subscribe to change notifications without
    /// coupling to MonoBehaviour events or UnityEvents.
    /// </summary>
    public class MID_SusValue<T>
    {
        private T _value;

        private readonly HashSet<OnValueChangedDelegate> _onChanged  = new();
        private readonly HashSet<OnAnyUpdateDelegate>    _onAnyUpdate = new();

        private Func<T, bool> _validationFunc;

        // ── Delegate types ────────────────────────────────────────────────────

        /// <summary>Invoked when the stored value changes to a different value.</summary>
        public delegate void OnValueChangedDelegate(T previousValue, T newValue);

        /// <summary>Invoked on every Value set attempt, even if value is unchanged.</summary>
        public delegate void OnAnyUpdateDelegate(T value);

        // ── Constructor ───────────────────────────────────────────────────────

        /// <param name="initialValue">Starting value.</param>
        /// <param name="validationFunc">
        /// Optional predicate. Return true to accept a value, false to reject it.
        /// </param>
        public MID_SusValue(T initialValue = default, Func<T, bool> validationFunc = null)
        {
            _value          = initialValue;
            _validationFunc = validationFunc;
        }

        // ── Value property ────────────────────────────────────────────────────

        /// <summary>
        /// Get or set the stored value.
        /// Setting triggers OnAnyUpdate always, and OnValueChanged only if the
        /// value actually differs from the current one.
        /// </summary>
        public T Value
        {
            get => _value;
            set
            {
                if (_validationFunc != null && !_validationFunc(value))
                {
                    Debug.LogWarning(
                        $"[MID_SusValue<{typeof(T).Name}>] Validation rejected value: {value}");
                    return;
                }

                // Always fire AnyUpdate BEFORE storing so listeners see old value too if needed
                NotifyAnyUpdate(value);

                if (!AreEqual(_value, value))
                {
                    T old = _value;
                    _value = value;
                    NotifyValueChanged(old, _value);
                }
            }
        }

        /// <summary>True if the current value is null (always false for value types).</summary>
        public bool IsValueNull => _value == null;

        // ── Validation ────────────────────────────────────────────────────────

        /// <summary>Replace the validation function at runtime.</summary>
        public void SetValidationFunction(Func<T, bool> func) => _validationFunc = func;

        /// <summary>Remove the validation function so all values are accepted.</summary>
        public void ClearValidationFunction() => _validationFunc = null;

        // ── Subscriptions — OnValueChanged ────────────────────────────────────

        /// <summary>
        /// Subscribe to receive old and new value when the value changes.
        /// Duplicate subscriptions are silently ignored.
        /// </summary>
        /// <returns>True if added, false if already subscribed.</returns>
        public bool SubscribeToValueChanged(OnValueChangedDelegate callback)
        {
            if (callback == null) return false;
            return _onChanged.Add(callback);
        }

        /// <summary>
        /// Unsubscribe from value-changed notifications.
        /// </summary>
        /// <returns>True if removed, false if it was not subscribed.</returns>
        public bool UnsubscribeFromValueChanged(OnValueChangedDelegate callback)
        {
            if (callback == null) return false;
            return _onChanged.Remove(callback);
        }

        public bool IsSubscribedToValueChanged(OnValueChangedDelegate callback) =>
            callback != null && _onChanged.Contains(callback);

        // ── Subscriptions — OnAnyUpdate ───────────────────────────────────────

        /// <summary>
        /// Subscribe to receive the value on every set attempt, even if unchanged.
        /// </summary>
        public bool SubscribeToAnyUpdate(OnAnyUpdateDelegate callback)
        {
            if (callback == null) return false;
            return _onAnyUpdate.Add(callback);
        }

        public bool UnsubscribeFromAnyUpdate(OnAnyUpdateDelegate callback)
        {
            if (callback == null) return false;
            return _onAnyUpdate.Remove(callback);
        }

        public bool IsSubscribedToAnyUpdate(OnAnyUpdateDelegate callback) =>
            callback != null && _onAnyUpdate.Contains(callback);

        // ── Utility ───────────────────────────────────────────────────────────

        /// <summary>
        /// Set value without firing any callbacks.
        /// Validation still runs.
        /// </summary>
        public void SetValueSilently(T newValue)
        {
            if (_validationFunc != null && !_validationFunc(newValue))
            {
                Debug.LogWarning(
                    $"[MID_SusValue<{typeof(T).Name}>] Silent set rejected value: {newValue}");
                return;
            }
            _value = newValue;
        }

        /// <summary>
        /// Force-fire OnValueChanged with the current value as both old and new.
        /// Useful to push the current state to newly-subscribed listeners.
        /// </summary>
        public void ForceNotify() => NotifyValueChanged(_value, _value);

        public void ClearAllSubscriptions()
        {
            _onChanged.Clear();
            _onAnyUpdate.Clear();
        }

        public int GetSubscriberCount() => _onChanged.Count + _onAnyUpdate.Count;

        // ── Implicit conversion ───────────────────────────────────────────────

        /// <summary>Allows using a MID_SusValue<T> directly where a T is expected.</summary>
        public static implicit operator T(MID_SusValue<T> susValue) =>
            susValue == null ? default : susValue._value;

        // ── Private ───────────────────────────────────────────────────────────

        private void NotifyValueChanged(T oldValue, T newValue)
        {
            foreach (var sub in _onChanged)
            {
                try   { sub?.Invoke(oldValue, newValue); }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[MID_SusValue<{typeof(T).Name}>] Exception in OnValueChanged " +
                        $"callback: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        private void NotifyAnyUpdate(T value)
        {
            foreach (var sub in _onAnyUpdate)
            {
                try   { sub?.Invoke(value); }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[MID_SusValue<{typeof(T).Name}>] Exception in OnAnyUpdate " +
                        $"callback: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        private static bool AreEqual(T a, T b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return EqualityComparer<T>.Default.Equals(a, b);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ManagedSusValue — auto-registers with SusValueManager
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MID_SusValue that automatically registers itself with <see cref="SusValueManager"/>
    /// and can be cleaned up centrally (e.g. on scene unload or object destruction).
    /// </summary>
    public class ManagedSusValue<T> : MID_SusValue<T>, IManagedSusValue
    {
        private readonly string _valueId;
        private bool            _isManaged;

        public string ValueId   => _valueId;
        public bool   IsManaged => _isManaged;

        /// <param name="initialValue">Starting value.</param>
        /// <param name="id">
        /// Unique string ID. Auto-generated (GUID) if null or empty.
        /// Use a descriptive ID in production so logs are readable.
        /// </param>
        /// <param name="owner">
        /// Optional owning GameObject. If supplied, subscriptions are automatically
        /// cleared when the owner is destroyed.
        /// </param>
        /// <param name="validationFunc">Optional value validator.</param>
        public ManagedSusValue(
            T              initialValue   = default,
            string         id             = null,
            GameObject     owner          = null,
            Func<T, bool>  validationFunc = null)
            : base(initialValue, validationFunc)
        {
            _valueId = string.IsNullOrEmpty(id)
                ? Guid.NewGuid().ToString()
                : id;

            if (SusValueManager.Instance != null)
            {
                SusValueManager.Instance.RegisterValue(this, owner);
                _isManaged = true;
            }
        }

        ~ManagedSusValue()
        {
            if (_isManaged && SusValueManager.HasInstance)
                SusValueManager.Instance.UnregisterValue(_valueId);
        }
    }
}
