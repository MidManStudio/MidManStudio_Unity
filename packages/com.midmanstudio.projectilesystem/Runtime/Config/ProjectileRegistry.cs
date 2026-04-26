// ProjectileRegistry.cs — UPDATE
//
// Changes from plan:
//   + GetRustSpawnData(configId) — returns RustSpawnParams without full SO ref
//   + Register Is3D configs separately from 2D
//   + RegisterMovementParams() called after ConfigId is assigned
//   + Stores ProjectileConfigSO (package version) not the game-specific SO
//   + ValidateStructSizes() called here at Awake
//
// ProjectileRegistry is the runtime lookup table for the sim system.
// It is separate from ProjectileConfigManager (game-side O(1) cache by enum name).
// Registry is indexed by ushort configId — compact, Rust-compatible.

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Singleton;

namespace MidManStudio.Projectiles
{
    /// <summary>
    /// Runtime registry mapping ushort configId to ProjectileConfigSO.
    /// Assigned IDs are stable for the session — do not rely on them across sessions.
    /// Singleton — attach to a persistent GameObject (same one as ServerProjectileAuthority).
    /// </summary>
    public sealed class ProjectileRegistry : Singleton<ProjectileRegistry>
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Storage
        // ─────────────────────────────────────────────────────────────────────

        private readonly List<ProjectileConfigSO>         _configs   = new(64);
        private readonly Dictionary<string, ushort>        _nameToId  = new(64);
        private readonly HashSet<ushort>                   _ids3D     = new(16);

        // ─────────────────────────────────────────────────────────────────────
        //  Initialisation
        // ─────────────────────────────────────────────────────────────────────

        [Header("Auto-register on Awake")]
        [Tooltip("Drag all ProjectileConfigSO assets here for automatic registration.\n" +
                 "Alternatively call Register() at runtime from your weapon system.")]
        [SerializeField] private ProjectileConfigSO[] _autoRegister = System.Array.Empty<ProjectileConfigSO>();

        protected override void Awake()
        {
            base.Awake();

            // Validate Rust struct sizes first — crash loudly if there is a mismatch
            // rather than silently corrupting memory later.
            try
            {
                ProjectileLib.ValidateStructSizes();
            }
            catch (System.InvalidOperationException ex)
            {
                Debug.LogError($"[ProjectileRegistry] {ex.Message}");
                // Disable the system entirely so projectiles don't fire with corrupt data
                enabled = false;
                return;
            }

            // Auto-register any configs assigned in the inspector
            foreach (var cfg in _autoRegister)
            {
                if (cfg != null) Register(cfg);
            }

            Debug.Log($"[ProjectileRegistry] Initialised with {_configs.Count} configs " +
                      $"({_ids3D.Count} 3D, {_configs.Count - _ids3D.Count} 2D).");
        }

        private void OnDestroy()
        {
            // Unregister movement params from Rust on shutdown
            foreach (var cfg in _configs)
            {
                cfg?.UnregisterMovementParams();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Registration API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Register a ProjectileConfigSO and assign it a session-stable ushort configId.
        /// Returns the assigned ID. Safe to call multiple times for the same config — returns existing ID.
        /// </summary>
        public ushort Register(ProjectileConfigSO config)
        {
            if (config == null)
            {
                Debug.LogError("[ProjectileRegistry] Attempted to register null config.");
                return ushort.MaxValue;
            }

            // Already registered?
            if (_nameToId.TryGetValue(config.name, out ushort existing))
                return existing;

            if (_configs.Count >= ushort.MaxValue - 1)
            {
                Debug.LogError("[ProjectileRegistry] Config ID space exhausted (max 65534 configs).");
                return ushort.MaxValue;
            }

            ushort id = (ushort)_configs.Count;
            config.ConfigId = id;
            _configs.Add(config);
            _nameToId[config.name] = id;

            if (config.Is3D)
                _ids3D.Add(id);

            // Register Rust-side movement params for Wave/Circular types
            config.RegisterMovementParams();

            return id;
        }

        /// <summary>
        /// Register a ProjectileConfigSO by name from Resources.
        /// Loads and registers in one call. Returns ushort.MaxValue on failure.
        /// </summary>
        public ushort RegisterByResourcePath(string resourcePath)
        {
            var cfg = Resources.Load<ProjectileConfigSO>(resourcePath);
            if (cfg == null)
            {
                Debug.LogError($"[ProjectileRegistry] Resource not found: {resourcePath}");
                return ushort.MaxValue;
            }
            return Register(cfg);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Lookup API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Total number of registered configs.</summary>
        public int Count => _configs.Count;

        /// <summary>
        /// Get the full config SO by ID.
        /// Returns null if ID is out of range.
        /// </summary>
        public ProjectileConfigSO Get(ushort configId)
        {
            if (configId >= _configs.Count) return null;
            return _configs[configId];
        }

        /// <summary>
        /// Get a config ID by the SO's name.
        /// Returns ushort.MaxValue if not registered.
        /// </summary>
        public bool TryGetId(string configName, out ushort configId)
        {
            return _nameToId.TryGetValue(configName, out configId);
        }

        /// <summary>True if this configId belongs to a 3D config.</summary>
        public bool Is3D(ushort configId) => _ids3D.Contains(configId);

        // ─────────────────────────────────────────────────────────────────────
        //  Rust spawn data helper
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Get the minimal Rust spawn params for a configId without exposing the full SO.
        /// Called by BatchSpawnHelper — avoids passing the full config SO reference
        /// across the spawn path.
        /// speedOverride: pass > 0 to override the config's speed resolution.
        /// </summary>
        public RustSpawnParams GetRustSpawnParams(ushort configId, float speedOverride = -1f)
        {
            var cfg = Get(configId);
            if (cfg == null)
            {
                Debug.LogError($"[ProjectileRegistry] No config for id {configId}");
                return default;
            }
            return cfg.GetRustSpawnParams(speedOverride);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Atlas UV support (for ProjectileRenderer2D / 3D)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Get the atlas UV rect for a config's sprite.
        /// Returns (0,0,1,1) if no atlas is set (full-texture fallback).
        /// ProjectileRenderer2D calls this each LateUpdate.
        /// </summary>
        public Vector4 GetUVRect(ushort configId)
        {
            var cfg = Get(configId);
            if (cfg?.ProjectileSprite == null)
                return new Vector4(0f, 0f, 1f, 1f);

            var sprite = cfg.ProjectileSprite;
            var tex    = sprite.texture;

            if (tex == null)
                return new Vector4(0f, 0f, 1f, 1f);

            // Convert sprite rect to 0-1 UV space
            return new Vector4(
                sprite.rect.x      / tex.width,
                sprite.rect.y      / tex.height,
                sprite.rect.width  / tex.width,
                sprite.rect.height / tex.height);
        }

#if UNITY_EDITOR
        [ContextMenu("Log All Registered Configs")]
        private void LogAll()
        {
            for (int i = 0; i < _configs.Count; i++)
            {
                var c = _configs[i];
                Debug.Log($"  [{i:D4}] {c.name} | 3D:{c.Is3D} | " +
                          $"Move:{c.MovementType} | Pierce:{c.PiercingType} | " +
                          $"Lifetime:{c.Lifetime:F1}s");
            }
        }
#endif
    }
}
