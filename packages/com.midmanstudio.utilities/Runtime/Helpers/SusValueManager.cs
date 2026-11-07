// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/helpers.md, section "SusValueManager.cs"
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;

namespace MidManStudio.Core.ObservableValues
{
    /// <summary>
    /// Registry of <see cref="ManagedSusValue{T}"/> instances, so their subscriptions can be cleared in bulk when
    /// a GameObject is destroyed or a scene unloads. It is a <see cref="Singleton{T}"/>: created on first use in
    /// Play Mode, and not available in Edit Mode.
    ///
    /// Automatic: <c>new ManagedSusValue&lt;float&gt;(0f, "MyFloat", gameObject)</c> registers itself, and its
    /// subscriptions are cleared when that GameObject is destroyed.
    /// Manual: <c>SusValueManager.Instance.ClearAllForOwner(myGameObject)</c> or <c>ClearAll()</c>.
    ///
    /// Clearing an owner removes the subscriptions only. The values stay registered, so their IDs stay taken.
    /// A value whose ID is already registered is skipped with a warning and is not tracked.
    /// </summary>
    public class SusValueManager : Singleton<SusValueManager>
    {
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        // valueId → value
        private readonly Dictionary<string, IManagedSusValue> _values = new();

        // owner instance ID → list of value IDs it owns
        private readonly Dictionary<int, List<string>> _ownerMap = new();

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Register a managed value. Called automatically by the ManagedSusValue constructor. A value whose ID is
        /// already registered is skipped with a warning. When an owner is given, a hidden watcher component is added
        /// to that GameObject to clear the owner's values when it is destroyed.
        /// </summary>
        public void RegisterValue(IManagedSusValue value, GameObject owner = null)
        {
            if (value == null || string.IsNullOrEmpty(value.ValueId)) return;

            if (_values.ContainsKey(value.ValueId))
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Value '{value.ValueId}' already registered — skipping.",
                    nameof(SusValueManager));
                return;
            }

            _values[value.ValueId] = value;

            if (owner != null)
            {
                int ownerId = owner.GetInstanceID();
                if (!_ownerMap.TryGetValue(ownerId, out var list))
                    _ownerMap[ownerId] = list = new List<string>();
                list.Add(value.ValueId);

                // Watch for owner destruction
                var watcher = owner.GetComponent<SusValueOwnerWatcher>()
                           ?? owner.AddComponent<SusValueOwnerWatcher>();
                watcher.Initialize(ownerId, this);
            }

            MID_Logger.LogDebug(_logLevel,
                $"Registered '{value.ValueId}'" +
                (owner != null ? $" owned by '{owner.name}'" : " (unowned)"),
                nameof(SusValueManager));
        }

        /// <summary>
        /// Unregister a value by ID and clear its subscriptions. Also called by the ManagedSusValue finalizer.
        /// It does nothing for an unknown ID.
        /// </summary>
        public void UnregisterValue(string valueId)
        {
            if (!_values.TryGetValue(valueId, out var value)) return;

            value.ClearAllSubscriptions();
            _values.Remove(valueId);

            MID_Logger.LogDebug(_logLevel,
                $"Unregistered '{valueId}'",
                nameof(SusValueManager));
        }

        /// <summary>Clear subscriptions on all values owned by this GameObject. The values stay registered.</summary>
        public void ClearAllForOwner(GameObject owner)
        {
            if (owner == null) return;
            ClearAllForOwnerId(owner.GetInstanceID());
        }

        internal void ClearAllForOwnerId(int ownerId)
        {
            if (!_ownerMap.TryGetValue(ownerId, out var ids)) return;

            foreach (var id in ids)
                if (_values.TryGetValue(id, out var value))
                    value.ClearAllSubscriptions();

            _ownerMap.Remove(ownerId);

            MID_Logger.LogInfo(_logLevel,
                $"Cleared {ids.Count} value(s) for owner id={ownerId}",
                nameof(SusValueManager));
        }

        /// <summary>Clear subscriptions on every registered value, then forget all values and owners.</summary>
        public void ClearAll()
        {
            foreach (var value in _values.Values)
                value.ClearAllSubscriptions();

            _ownerMap.Clear();

            MID_Logger.LogInfo(_logLevel,
                $"Cleared all {_values.Count} registered value(s).",
                nameof(SusValueManager));

            _values.Clear();
        }

        /// <summary>Number of currently registered values.</summary>
        public int RegisteredCount => _values.Count;

        /// <summary>Check whether a value with this ID is registered.</summary>
        public bool IsRegistered(string valueId) => _values.ContainsKey(valueId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Internal MonoBehaviour watcher — lives on the owning GameObject
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attached automatically to GameObjects that own ManagedSusValues.
    /// Notifies SusValueManager when the owner is destroyed.
    /// </summary>
    [AddComponentMenu("")]   // hide from Add Component menu
    internal sealed class SusValueOwnerWatcher : MonoBehaviour
    {
        private int             _ownerId;
        private SusValueManager _manager;
        private bool            _initialized;

        internal void Initialize(int ownerId, SusValueManager manager)
        {
            if (_initialized) return;
            _ownerId     = ownerId;
            _manager     = manager;
            _initialized = true;
        }

        private void OnDestroy()
        {
            if (_initialized && _manager != null)
                _manager.ClearAllForOwnerId(_ownerId);
        }
    }
}
