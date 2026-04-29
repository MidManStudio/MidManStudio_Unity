
using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Libraries
{
    [CreateAssetMenu(fileName = "MID_Library", menuName = "MidManStudio/Utilities/Library")]
    public class MID_LibrarySO : ScriptableObject, IArrayElementTitle
    {
        [Tooltip("Unique ID used to retrieve this library from MID_LibraryRegistry.")]
        [SerializeField] private string _libraryId;

        [MID_NamedList]
        [SerializeField] private List<MID_LibraryItemSO> _items = new List<MID_LibraryItemSO>();

        private Dictionary<string, MID_LibraryItemSO> _lookup;
        private bool _built;

        // IArrayElementTitle — _libraryId first, asset name second, fallback third
        public string Name =>
            !string.IsNullOrEmpty(_libraryId) ? _libraryId :
            !string.IsNullOrEmpty(name) ? name :
                                                "Library";

        public string LibraryId => !string.IsNullOrEmpty(_libraryId) ? _libraryId : name;

        // ── Public API ──────────────────────────────────────────────────────

        public void BuildLookup()
        {
            if (_built) return;
            _lookup = new Dictionary<string, MID_LibraryItemSO>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var item in _items)
            {
                if (item == null) continue;
                if (_lookup.ContainsKey(item.ItemId))
                {
                    Debug.LogWarning($"[MID_LibrarySO:{LibraryId}] " +
                                     $"Duplicate item ID '{item.ItemId}' — skipped.");
                    continue;
                }
                _lookup[item.ItemId] = item;
            }
            _built = true;
        }

        public T GetItem<T>(string itemId) where T : MID_LibraryItemSO
        {
            BuildLookup();
            if (_lookup.TryGetValue(itemId, out var item))
                return item as T;
            return null;
        }

        public bool HasItem(string itemId)
        {
            BuildLookup();
            return _lookup.ContainsKey(itemId);
        }

        public int ItemCount
        {
            get { BuildLookup(); return _lookup.Count; }
        }
    }
}