// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/libraries.md, section "MID_LibraryRegistry.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Libraries
{
    /// <summary>
    /// Scene singleton that holds the <see cref="MID_LibrarySO"/> assets and answers lookups by string key
    /// (<c>GetItem&lt;T&gt;("Weapons", "Sword")</c>) or by the generated enums
    /// (<c>GetItem&lt;T&gt;(LibraryId.Weapons, LibraryItemId.Weapons_Sword)</c>). The tables are built once in
    /// <c>Awake</c> from the Libraries list.
    /// </summary>
    /// <remarks>
    /// Place one in a scene and add every library asset to its list. Without one in the scene,
    /// <c>Instance</c> creates an empty registry and every lookup logs a warning. A library is reachable by
    /// enum only when its id equals the name of a <c>LibraryId</c> member (case-sensitive).
    /// </remarks>
    public class MID_LibraryRegistry : Singleton<MID_LibraryRegistry>
    {
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [MID_NamedList]
        [Tooltip("Drag all your MID_LibrarySO assets here.")]
        [SerializeField] private List<MID_LibrarySO> _libraries = new();

        private readonly Dictionary<string, MID_LibrarySO> _byStringKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, MID_LibrarySO>    _byEnumKey   = new();

        protected override void Awake()
        {
            base.Awake();
            Build();
        }

        // ── Build ─────────────────────────────────────────────────────────────

        private void Build()
        {
            _byStringKey.Clear();
            _byEnumKey.Clear();

            foreach (var lib in _libraries)
            {
                if (lib == null) continue;

                if (_byStringKey.ContainsKey(lib.LibraryId))
                {
                    MID_Logger.LogWarning(_logLevel,
                        $"Duplicate library ID '{lib.LibraryId}' — skipped.",
                        nameof(MID_LibraryRegistry));
                    continue;
                }

                lib.BuildLookup();
                _byStringKey[lib.LibraryId] = lib;

                // Map enum value if LibraryId enum exists and has a matching name
                if (TryParseLibraryId(lib.LibraryId, out int enumVal))
                    _byEnumKey[enumVal] = lib;
            }

            MID_Logger.LogInfo(_logLevel,
                $"Registry built — {_byStringKey.Count} libraries loaded.",
                nameof(MID_LibraryRegistry));
        }

        // ── String-key API ────────────────────────────────────────────────────

        /// <summary>Gets an item by library id and item id (both case-insensitive). Null and a warning when missing or of another type.</summary>
        public T GetItem<T>(string libraryId, string itemId) where T : MID_LibraryItemSO
        {
            if (!_byStringKey.TryGetValue(libraryId, out var lib))
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Library '{libraryId}' not found.",
                    nameof(MID_LibraryRegistry));
                return null;
            }
            return GetItemFromLibrary<T>(lib, itemId);
        }

        /// <summary>True when a library with this id is registered.</summary>
        public bool LibraryExists(string libraryId)  => _byStringKey.ContainsKey(libraryId);
        /// <summary>True when the library exists and holds an item with this id.</summary>
        public bool ItemExists(string lib, string id) =>
            _byStringKey.TryGetValue(lib, out var l) && l.HasItem(id);

        // ── Enum API (generated) ─────────────────────────────────────────────

        /// <summary>
        /// Retrieve an item using generated enum keys.
        /// Example: registry.GetItem&lt;WeaponSO&gt;(LibraryId.Weapons, LibraryItemId.Weapons_Sword);
        /// </summary>
        public T GetItem<T>(LibraryId libraryId, LibraryItemId itemId) where T : MID_LibraryItemSO
        {
            int libVal = (int)libraryId;
            if (!_byEnumKey.TryGetValue(libVal, out var lib))
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Library '{libraryId}' (id={libVal}) not found.",
                    nameof(MID_LibraryRegistry));
                return null;
            }

            // Item enum name format: LibraryName_ItemName — strip the prefix
            string itemName = itemId.ToString();
            string prefix   = libraryId.ToString() + "_";
            if (itemName.StartsWith(prefix))
                itemName = itemName.Substring(prefix.Length);

            return GetItemFromLibrary<T>(lib, itemName);
        }

        /// <summary>True when a library registered under this enum value exists.</summary>
        public bool LibraryExists(LibraryId libraryId) => _byEnumKey.ContainsKey((int)libraryId);

        /// <summary>True when the library exists and holds the item named by the enum member (prefix <c>LibraryName_</c> removed).</summary>
        public bool ItemExists(LibraryId libraryId, LibraryItemId itemId)
        {
            if (!_byEnumKey.TryGetValue((int)libraryId, out var lib)) return false;
            string itemName = itemId.ToString();
            string prefix   = libraryId + "_";
            if (itemName.StartsWith(prefix)) itemName = itemName.Substring(prefix.Length);
            return lib.HasItem(itemName);
        }

        // ── Shared ────────────────────────────────────────────────────────────

        private T GetItemFromLibrary<T>(MID_LibrarySO lib, string itemId)
            where T : MID_LibraryItemSO
        {
            var item = lib.GetItem<T>(itemId);
            if (item == null)
                MID_Logger.LogWarning(_logLevel,
                    $"Item '{itemId}' not found in library '{lib.LibraryId}'.",
                    nameof(MID_LibraryRegistry));
            return item;
        }

        // ── Enum parsing helper ───────────────────────────────────────────────
        // Parses the generated LibraryId enum by member name (case-sensitive). A library whose id
        // matches no member is registered for string lookups only.

        private static bool TryParseLibraryId(string name, out int value)
        {
            value = 0;
            try
            {
                if (Enum.TryParse(typeof(LibraryId), name, out var result))
                {
                    value = (int)result;
                    return true;
                }
            }
            catch { /* treated as no match */ }
            return false;
        }
    }
}
