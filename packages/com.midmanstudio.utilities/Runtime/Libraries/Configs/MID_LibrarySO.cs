// MID_LibrarySO.cs
// A named collection of MID_LibraryItemSO assets keyed by their ItemId.
// Register libraries in MID_LibraryRegistry to access them globally.
//
// CREATE:
//   Right-click > MidManStudio > Utilities > Library
//
// ADD ITEMS:
//   Create MID_BasicLibraryItemSO assets (or your own subclasses),
//   then drag them into this library's Items list.
//
// RETRIEVAL:
//   var item = MID_LibraryRegistry.Instance.GetItem<MID_BasicLibraryItemSO>("Weapons", "Sword");

using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Libraries
{
    [CreateAssetMenu(
        fileName = "NewLibrary",
        menuName = "MidManStudio/Utilities/Library")]
    public class MID_LibrarySO : ScriptableObject, IArrayElementTitle
    {
        [Tooltip("Unique ID used to retrieve this library from MID_LibraryRegistry.")]
        [SerializeField] private string _libraryId;

        [Tooltip("All items belonging to this library.")]
        [MID_NamedList]
        [SerializeField] private List<MID_LibraryItemSO> _items = new();

        private Dictionary<string, MID_LibraryItemSO> _lookup;
        private bool _built;

        // IArrayElementTitle
        public string Name =>
            !string.IsNullOrWhiteSpace(_libraryId) ? _libraryId :
            !string.IsNullOrEmpty(name) ? name : "Library";

        public string LibraryId =>
            !string.IsNullOrWhiteSpace(_libraryId) ? _libraryId : name;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Build the internal lookup dictionary. Called automatically on first access.
        /// Also called by Unity's OnEnable to handle domain reloads.
        /// </summary>
        public void BuildLookup()
        {
            if (_built && _lookup != null) return;

            _lookup = new Dictionary<string, MID_LibraryItemSO>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var item in _items)
            {
                if (item == null) continue;

                if (_lookup.ContainsKey(item.ItemId))
                {
                    Debug.LogWarning(
                        $"[MID_LibrarySO:{LibraryId}] Duplicate item ID '{item.ItemId}' — " +
                        "first entry kept. Check your library asset for duplicates.");
                    continue;
                }

                _lookup[item.ItemId] = item;
            }

            _built = true;
        }

        /// <summary>
        /// Retrieve an item by ID, cast to the requested type.
        /// Returns null if not found or wrong type.
        /// </summary>
        public T GetItem<T>(string itemId) where T : MID_LibraryItemSO
        {
            BuildLookup();
            return _lookup.TryGetValue(itemId, out var item) ? item as T : null;
        }

        /// <summary>True if an item with this ID exists in the library.</summary>
        public bool HasItem(string itemId)
        {
            BuildLookup();
            return _lookup.ContainsKey(itemId);
        }

        /// <summary>Number of valid items in this library.</summary>
        public int ItemCount
        {
            get { BuildLookup(); return _lookup.Count; }
        }

        /// <summary>All item IDs in this library (for debug/editor use).</summary>
        public IEnumerable<string> AllItemIds
        {
            get { BuildLookup(); return _lookup.Keys; }
        }

        // Force rebuild after domain reload
        private void OnEnable() => _built = false;
    }
}
