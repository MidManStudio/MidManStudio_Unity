// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/helpers.md, section "MID_SusValue.cs"
// ============================================================================
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
        /// <summary>Removes every subscriber from the value.</summary>
        void   ClearAllSubscriptions();

        /// <summary>True when the value was registered with <see cref="SusValueManager"/> at construction.</summary>
        bool   IsManaged { get; }

        /// <summary>The ID the value is registered under.</summary>
        string ValueId   { get; }
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Observable value container. Subscribe to change notifications without coupling to MonoBehaviour events
    /// or UnityEvents. No game dependencies.
    ///
    /// Two subscription kinds: <see cref="SubscribeToValueChanged"/> fires only when the stored value actually
    /// changes; <see cref="SubscribeToAnyUpdate"/> fires on every set, even with the same value. Each set is first
    /// checked by the optional validation function, and a rejected value is dropped with a warning. Subscribers
    /// are kept in HashSets, so adding the same delegate twice does nothing. The notification loops enumerate
    /// those sets directly, so changing the subscriptions from inside a callback is not safe.
    /// Implicit conversion to T lets a <c>MID_SusValue&lt;float&gt;</c> be used where a float is expected.
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
        /// Get or set the stored value. A set that passes validation notifies OnAnyUpdate always, and
        /// OnValueChanged only if the value differs from the current one. OnAnyUpdate fires before the value is
        /// stored, so inside an OnAnyUpdate callback <c>.Value</c> is still the old value and the new one is the
        /// callback argument. OnValueChanged fires after it is stored.
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

                // AnyUpdate fires BEFORE storing: listeners get the new value as the argument and can still read the old one from .Value
                NotifyAnyUpdate(value);

                if (!AreEqual(_value, value))
                {
                    T old = _value;
                    _value = value;
                    NotifyValueChanged(old, _value);
                }
            }
        }

        /// <summary>
        /// True if the current value is null (always false for value types). It is a plain C# null check, so a
        /// destroyed UnityEngine.Object stored here does not count as null.
        /// </summary>
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

        /// <summary>True if this delegate is currently subscribed to value-changed notifications.</summary>
        public bool IsSubscribedToValueChanged(OnValueChangedDelegate callback) =>
            callback != null && _onChanged.Contains(callback);

        // ── Subscriptions — OnAnyUpdate ───────────────────────────────────────

        /// <summary>
        /// Subscribe to receive the value on every set that passes validation, even if unchanged. The callback
        /// runs before the value is stored.
        /// </summary>
        /// <returns>True if added, false if already subscribed.</returns>
        public bool SubscribeToAnyUpdate(OnAnyUpdateDelegate callback)
        {
            if (callback == null) return false;
            return _onAnyUpdate.Add(callback);
        }

        /// <summary>Unsubscribe from any-update notifications.</summary>
        /// <returns>True if removed, false if it was not subscribed.</returns>
        public bool UnsubscribeFromAnyUpdate(OnAnyUpdateDelegate callback)
        {
            if (callback == null) return false;
            return _onAnyUpdate.Remove(callback);
        }

        /// <summary>True if this delegate is currently subscribed to any-update notifications.</summary>
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
        /// Fires OnValueChanged (not OnAnyUpdate) with the current value as both old and new.
        /// Useful to push the current state to newly-subscribed listeners.
        /// </summary>
        public void ForceNotify() => NotifyValueChanged(_value, _value);

        /// <summary>Removes every subscriber, of both kinds.</summary>
        public void ClearAllSubscriptions()
        {
            _onChanged.Clear();
            _onAnyUpdate.Clear();
        }

        /// <summary>Number of subscribers of both kinds added together.</summary>
        public int GetSubscriberCount() => _onChanged.Count + _onAnyUpdate.Count;

        // ── Implicit conversion ───────────────────────────────────────────────

        /// <summary>Allows using a <c>MID_SusValue&lt;T&gt;</c> directly where a T is expected. A null container converts to <c>default</c>.</summary>
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
    /// MID_SusValue that registers itself with <see cref="SusValueManager"/> when it is constructed, so its
    /// subscriptions can be cleared centrally (for example when the owning GameObject is destroyed).
    /// Registration needs <c>SusValueManager.Instance</c>, which exists only in Play Mode and is looked up with
    /// Unity APIs, so construct these in Awake or Start on the main thread (not in a field initialiser or
    /// constructor). Outside Play Mode the value is created but not managed. IDs must be unique among live values.
    /// </summary>
    public class ManagedSusValue<T> : MID_SusValue<T>, IManagedSusValue
    {
        private readonly string _valueId;
        private bool            _isManaged;

        /// <summary>The ID this value is registered under.</summary>
        public string ValueId   => _valueId;

        /// <summary>
        /// True when a manager existed at construction. It stays true even if the manager skipped the value
        /// because the ID was already registered.
        /// </summary>
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

        // Finalizer: the manager's dictionary holds the value strongly while it is registered, so this only runs
        // after the manager has dropped it (ClearAll or UnregisterValue). It runs on the finalizer thread and looks
        // the ID up again, which can remove a different value that has reused the same ID.
        ~ManagedSusValue()
        {
            if (_isManaged && SusValueManager.HasInstance)
                SusValueManager.Instance.UnregisterValue(_valueId);
        }
    }
}
