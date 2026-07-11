using System.Collections.Generic;
using System.Text;
using UnityEngine;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.Logging;

namespace MidManStudio.Projectiles.Config
{
    /// <summary>
    /// Runtime registry mapping ushort patternId to ProjectilePatternSO.
    ///
    /// IDs are a stable FNV-1a hash of the asset's name — NOT the array
    /// position in _autoRegister. This matters: unlike ProjectileConfigSO/
    /// ProjectileRegistry, which get their cross-build ID stability from
    /// ProjectileConfigMappingSO (a checked-in, generator-baked asset where
    /// array order IS the enum value, identical on every machine by
    /// construction), _autoRegister here is just an Inspector-populated array
    /// on a scene component. Nothing guarantees two builds/processes/editor
    /// sessions populate it in the same order — reorder the list, insert a new
    /// pattern in the middle instead of appending, or have host and client
    /// running slightly different scene setups, and position-based IDs quietly
    /// point at different assets on each side. A hash of the name doesn't care
    /// about order or what else is registered — the same asset always gets the
    /// same ID everywhere, as long as you don't rename it.
    ///
    /// ID 0 is reserved as the wire-level "no pattern" sentinel
    /// (ProjectileFireRequest.PatternId / SpawnConfirmation.PatternId) — the
    /// hash is shifted to never produce 0.
    ///
    /// Singleton — attach to a persistent GameObject (same one as ProjectileRegistry).
    /// </summary>
    public sealed class ProjectilePatternRegistry : Singleton<ProjectilePatternRegistry>
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Storage
        // ─────────────────────────────────────────────────────────────────────

        private readonly Dictionary<ushort, ProjectilePatternSO> _byId     = new(32);
        private readonly Dictionary<string, ushort>               _nameToId = new(32);

        // ─────────────────────────────────────────────────────────────────────
        //  Initialisation
        // ─────────────────────────────────────────────────────────────────────

        [Header("Auto-register on Awake")]
        [Tooltip("Drag every ProjectilePatternSO asset used over the network here.\n" +
                 "IDs are hashed from asset name, not list position — order doesn't matter,\n" +
                 "but every connected process needs the SAME assets registered (same names),\n" +
                 "or a patternId sent by one side simply won't resolve on the other.\n" +
                 "Renaming a pattern asset changes its ID — re-sync after a rename.")]
        [SerializeField] private ProjectilePatternSO[] _autoRegister = System.Array.Empty<ProjectilePatternSO>();

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.None;

        protected override void Awake()
        {
            base.Awake();

            foreach (var pattern in _autoRegister)
            {
                if (pattern != null) Register(pattern);
            }

            MID_Logger.LogDebug(_logLevel,
                $"[ProjectilePatternRegistry] Initialised with {_byId.Count} patterns.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Registration API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Register a ProjectilePatternSO and assign it a stable ushort patternId
        /// derived from its name (also cached on the asset itself via
        /// ProjectilePatternSO.PatternId, mirroring ProjectileConfigSO.ConfigId).
        /// Returns the assigned ID — safe to call multiple times for the same
        /// asset, returns the existing ID. Returns 0 on failure (null asset, or
        /// a genuine hash collision against a DIFFERENT already-registered
        /// pattern — rename one of them).
        /// </summary>
        public ushort Register(ProjectilePatternSO pattern)
        {
            if (pattern == null)
            {
                MID_Logger.LogError(_logLevel, "[ProjectilePatternRegistry] Attempted to register null pattern.");
                return 0;
            }

            if (_nameToId.TryGetValue(pattern.name, out ushort existing))
                return existing;

            ushort id = StableId(pattern.name);

            if (_byId.TryGetValue(id, out var collision) && collision != pattern)
            {
                MID_Logger.LogError(_logLevel,
                    $"[ProjectilePatternRegistry] '{pattern.name}' hashes to the same id as " +
                    $"already-registered '{collision.name}' ({id}). Rename one of the two assets " +
                    "— returning 0 (\"no pattern\") for this asset until it's resolved.");
                return 0;
            }

            pattern.PatternId       = id;
            _byId[id]                = pattern;
            _nameToId[pattern.name]  = id;

            return id;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Lookup API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Total number of registered patterns.</summary>
        public int Count => _byId.Count;

        /// <summary>
        /// Get the pattern asset by ID. Returns null for ID 0 ("no pattern")
        /// or any unrecognised ID — callers must treat both the same way:
        /// fall back to the non-pattern spread path.
        /// </summary>
        public ProjectilePatternSO Get(ushort patternId)
        {
            if (patternId == 0) return null;
            return _byId.TryGetValue(patternId, out var pattern) ? pattern : null;
        }

        /// <summary>
        /// Get a pattern's ID by the SO's name. Returns false (id 0) if not registered.
        /// </summary>
        public bool TryGetId(string patternName, out ushort patternId)
        {
            return _nameToId.TryGetValue(patternName, out patternId);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Stable hashing
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// FNV-1a over the UTF8 bytes of the name, folded into [1, 65535].
        /// Deliberately NOT string.GetHashCode() — Unity does not guarantee
        /// that's stable across Mono/IL2CPP, platforms, or even separate runs;
        /// this needs to produce the exact same id for the exact same name on
        /// every machine, every time, which is the entire point of the switch
        /// away from array-position ids.
        /// </summary>
        private static ushort StableId(string name)
        {
            uint hash = 2166136261u;
            byte[] bytes = Encoding.UTF8.GetBytes(name);
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 16777619u;
            }
            // Fold to 16 bits, shift off zero (0 stays the "no pattern" sentinel).
            ushort folded = (ushort)((hash ^ (hash >> 16)) % (ushort.MaxValue - 1));
            return (ushort)(folded + 1);
        }

#if UNITY_EDITOR
        [ContextMenu("Log All Registered Patterns")]
        private void LogAll()
        {
            foreach (var kvp in _byId)
                Debug.Log($"  [{kvp.Key:D5}] {kvp.Value.name} | Shape:{kvp.Value.Shape} | Count:{kvp.Value.ProjectileCount}");
        }
#endif
    }
}
