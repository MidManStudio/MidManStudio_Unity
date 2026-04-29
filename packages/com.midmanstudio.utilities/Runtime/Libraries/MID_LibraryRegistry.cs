
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
        [Tooltip("Drag all your MID_LibrarySO assets here. Order does not matter.")]
        [SerializeField] private List<MID_LibrarySO> _libraries = new List<MID_LibrarySO>();

        private Dictionary<string, MID_LibrarySO> _registry;

        protected override void Awake()
        {
            base.Awake();
            Build();
        }

        // ── Public API ──────────────────────────────────────────────────────

        public T GetItem<T>(string libraryId, string itemId) where T : MID_LibraryItemSO
        {
            if (!_registry.TryGetValue(libraryId, out var lib))
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Library '{libraryId}' not found. " +
                    $"Registered: {string.Join(", ", _registry.Keys)}",
                    nameof(MID_LibraryRegistry), nameof(GetItem));
                return null;
            }

            var item = lib.GetItem<T>(itemId);
            if (item == null)
                MID_Logger.LogWarning(_logLevel,
                    $"Item '{itemId}' not found in library '{libraryId}'.",
                    nameof(MID_LibraryRegistry), nameof(GetItem));

            return item;
        }

        public bool LibraryExists(string libraryId) =>
            _registry.ContainsKey(libraryId);

        public bool ItemExists(string libraryId, string itemId)
        {
            if (!_registry.TryGetValue(libraryId, out var lib)) return false;
            return lib.HasItem(itemId);
        }

        // ── Private ─────────────────────────────────────────────────────────

        private void Build()
        {
            _registry = new Dictionary<string, MID_LibrarySO>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var lib in _libraries)
            {
                if (lib == null) continue;
                if (_registry.ContainsKey(lib.LibraryId))
                {
                    MID_Logger.LogWarning(_logLevel,
                        $"Duplicate library ID '{lib.LibraryId}' — skipped.",
                        nameof(MID_LibraryRegistry), nameof(Build));
                    continue;
                }
                lib.BuildLookup();
                _registry[lib.LibraryId] = lib;
            }

            MID_Logger.LogInfo(_logLevel,
                $"Registry built — {_registry.Count} libraries loaded.",
                nameof(MID_LibraryRegistry), nameof(Build));
        }
    }
}