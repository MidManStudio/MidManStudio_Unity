// MID_LibraryRegistry.cs
// Singleton registry for MID_LibrarySO assets.
// Supports both legacy string-key lookups and generated enum lookups.
// Generated enums: LibraryId, LibraryItemId (from Library Type Generator).

using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Libraries
{
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

        // ── String-key API (legacy) ───────────────────────────────────────────

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

        public bool LibraryExists(string libraryId)  => _byStringKey.ContainsKey(libraryId);
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

        public bool LibraryExists(LibraryId libraryId) => _byEnumKey.ContainsKey((int)libraryId);

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
        // Parses the generated LibraryId enum by name match. Works even if the
        // enum did not exist at compile time via string→int reflection.

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
            catch { /* LibraryId enum may not exist yet before first generation */ }
            return false;
        }
    }
}
