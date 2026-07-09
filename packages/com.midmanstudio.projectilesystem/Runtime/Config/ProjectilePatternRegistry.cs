using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.Logging;

namespace MidManStudio.Projectiles.Config
{
    /// <summary>
    /// Runtime registry mapping ushort patternId to ProjectilePatternSO, mirroring
    /// ProjectileRegistry's storage/registration style with one deliberate
    /// difference: IDs here start at 1, not 0. 0 is reserved as the wire-level
    /// "no pattern" sentinel (ProjectileFireRequest.PatternId / SpawnConfirmation.PatternId),
    /// so a fire event can cheaply say "this wasn't pattern-based" without a
    /// separate bool field. Assigned IDs are stable for the session only.
    /// Singleton — attach to a persistent GameObject (same one as ProjectileRegistry).
    /// </summary>
    public sealed class ProjectilePatternRegistry : Singleton<ProjectilePatternRegistry>
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Storage
        // ─────────────────────────────────────────────────────────────────────

        private readonly List<ProjectilePatternSO>  _patterns = new(32);
        private readonly Dictionary<string, ushort> _nameToId = new(32);

        // ─────────────────────────────────────────────────────────────────────
        //  Initialisation
        // ─────────────────────────────────────────────────────────────────────

        [Header("Auto-register on Awake")]
        [Tooltip("Drag all ProjectilePatternSO assets here for automatic registration.\n" +
                 "ID 0 is reserved for \"no pattern\" — the first entry here becomes ID 1.\n" +
                 "Alternatively call Register() at runtime from your weapon system.")]
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
                $"[ProjectilePatternRegistry] Initialised with {_patterns.Count} patterns.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Registration API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Register a ProjectilePatternSO and assign it a session-stable ushort
        /// patternId (also cached on the asset itself via ProjectilePatternSO.PatternId,
        /// mirroring ProjectileConfigSO.ConfigId). Returns the assigned ID — safe to
        /// call multiple times for the same asset, returns the existing ID.
        /// Returns 0 on failure (null asset, or ID space exhausted).
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

            if (_patterns.Count >= ushort.MaxValue - 2)
            {
                MID_Logger.LogError(_logLevel, "[ProjectilePatternRegistry] Pattern ID space exhausted.");
                return 0;
            }

            ushort id = (ushort)(_patterns.Count + 1); // +1: id 0 reserved for "no pattern"
            pattern.PatternId = id;
            _patterns.Add(pattern);
            _nameToId[pattern.name] = id;

            return id;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Lookup API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Total number of registered patterns.</summary>
        public int Count => _patterns.Count;

        /// <summary>
        /// Get the pattern asset by ID. Returns null for ID 0 ("no pattern")
        /// or any unrecognised ID — callers must treat both the same way:
        /// fall back to the non-pattern spread path.
        /// </summary>
        public ProjectilePatternSO Get(ushort patternId)
        {
            if (patternId == 0) return null;
            int index = patternId - 1;
            if (index < 0 || index >= _patterns.Count) return null;
            return _patterns[index];
        }

        /// <summary>
        /// Get a pattern's ID by the SO's name. Returns false (id 0) if not registered.
        /// </summary>
        public bool TryGetId(string patternName, out ushort patternId)
        {
            return _nameToId.TryGetValue(patternName, out patternId);
        }

#if UNITY_EDITOR
        [ContextMenu("Log All Registered Patterns")]
        private void LogAll()
        {
            for (int i = 0; i < _patterns.Count; i++)
            {
                var p = _patterns[i];
                Debug.Log($"  [{(i + 1):D4}] {p.name} | Shape:{p.Shape} | Count:{p.ProjectileCount}");
            }
        }
#endif
    }
}
