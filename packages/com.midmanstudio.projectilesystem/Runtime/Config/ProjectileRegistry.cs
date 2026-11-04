// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "ProjectileRegistry.cs"
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Singleton;
using MidManStudio.Projectiles.Core;
using MidManStudio.Core.Logging;
namespace MidManStudio.Projectiles.Config
{
    /// <summary>
    /// Runtime registry mapping ushort configId to ProjectileConfigSO.
    /// Assigned IDs are stable for the session — do not rely on them across sessions.
    /// Singleton — attach to a persistent GameObject (same one as ServerProjectileAuthority).
    ///
    /// ProjectileConfigSO.ConfigId is a plain field that is only assigned inside
    /// Register() below. A script that reads it before this registration has
    /// run gets 0, which is whichever config sits at index 0.
    /// [DefaultExecutionOrder(-200)] makes this class's Awake() (where
    /// _autoRegister is processed) run before every other script's Awake().
    /// Unity finishes every Awake() before it calls any Start(), so that also
    /// covers scripts that read ConfigId from their own Start().
    /// ProjectileConfigManager runs at -150, right after this class, because it
    /// needs the registry to exist.
    /// </summary>
    [DefaultExecutionOrder(-200)]
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

        [Header("Debug")]
        [SerializeField] MID_LogLevel _LogLevel = MID_LogLevel.None;

        protected override void Awake()
        {
            base.Awake();

            // Validate the Rust struct sizes first. A mismatch would otherwise
            // corrupt memory later.
            // ProjectileLib.ValidateStructSizes() throws InvalidOperationException
            // both for a size mismatch and for a native library that fails to
            // load, so this one catch covers both cases.
            try
            {
                ProjectileLib.ValidateStructSizes();
            }
            catch (System.InvalidOperationException ex)
            {
                MID_Logger.LogDebug(_LogLevel,$"[ProjectileRegistry] {ex.Message}");
                // Disable the system entirely so projectiles don't fire with corrupt data
                enabled = false;
                return;
            }

            // Auto-register any configs assigned in the inspector
            foreach (var cfg in _autoRegister)
            {
                if (cfg != null) Register(cfg);
            }

            MID_Logger.LogDebug(_LogLevel, $"[ProjectileRegistry] Initialised with {_configs.Count} configs " +
                      $"({_ids3D.Count} 3D, {_configs.Count - _ids3D.Count} 2D).");
        }

        protected override void OnDestroy()
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
                MID_Logger.LogError( _LogLevel, "[ProjectileRegistry] Attempted to register null config.");
                return ushort.MaxValue;
            }

            // Already registered?
            if (_nameToId.TryGetValue(config.name, out ushort existing))
                return existing;

            if (_configs.Count >= ushort.MaxValue - 1)
            {

                MID_Logger.LogError(_LogLevel, "[ProjectileRegistry] Config ID space exhausted (max 65534 configs).");
                return ushort.MaxValue;
            }

            ushort id = (ushort)_configs.Count;
            config.ConfigId = id;
            _configs.Add(config);
            _nameToId[config.name] = id;

            if (config.Is3D)
                _ids3D.Add(id);

            // Register the Rust-side movement params for Wave/Circular types.
            // ProjectileConfigSO.RegisterMovementParams() already does nothing
            // when the native library is unavailable. The try/catch is a second
            // guard: an exception here must not abort the loop or coroutine that
            // called Register(), because that would stop every config after this
            // one in the same batch from registering.
            try
            {
                config.RegisterMovementParams();
            }
            catch (System.Exception ex)
            {
                MID_Logger.LogError(_LogLevel,
                    $"[ProjectileRegistry] '{config.name}': RegisterMovementParams() threw " +
                    $"{ex.GetType().Name} — {ex.Message}. Continuing without native movement params " +
                    "for this config.");
            }

            // Sprite atlas warm-up. Unity resolves a packed Sprite's `.texture`
            // lazily: the first accesses can still point at the sprite's original
            // texture, and `.texture` and `.rect` only report the final atlas
            // values once that resolution has finished. ProjectileVisual_2D/3D
            // read `sprite.texture` again on every fire, so they correct
            // themselves, but only after the atlas has caught up. Touching
            // `.texture` here, at registration (normally during scene load), moves
            // that one-time cost out of gameplay. It does not make the resolution
            // itself any faster.
            // A 2D config that needs no CustomShape mesh can also set
            // ProjectileVisual_2D's _forceSpriteRendererOnly. SpriteRenderer
            // resolves atlas textures through Unity's own rendering path every
            // frame, so it has no such timing window and needs no warm-up.
            if (config.UseSprite && config.ProjectileSprite != null)
            {
                _ = config.ProjectileSprite.texture; // the access itself is the trigger
            }

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

                MID_Logger.LogError(_LogLevel, $"[ProjectileRegistry] Resource not found: {resourcePath}");
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
        /// All registered configs, in registration order (index == ConfigId).
        /// Read-only — used by ProjectileVisualWarmupManager to iterate every
        /// config for its pool/atlas warm-up pass instead of requiring a
        /// second, separately-maintained list in the scene. Exposed as
        /// IReadOnlyList so callers can't mutate registry internals.
        /// </summary>
        public IReadOnlyList<ProjectileConfigSO> AllConfigs => _configs;

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
        /// Looks up a config ID by the SO's asset name.
        /// Returns false (and configId 0) if no config with that name is registered.
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

                MID_Logger.LogError(_LogLevel, $"[ProjectileRegistry] No config for id {configId}");
                return default;
            }
            return cfg.GetRustSpawnParams(speedOverride);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Atlas UV support (for ProjectileRenderer2D / 3D)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Get the UV rect (x, y, width, height in 0-1 texture space) for a
        /// config's sprite.
        /// Returns (0,0,1,1) if the config has no sprite or the sprite has no
        /// texture (full-texture fallback).
        /// Called by ProjectileRenderer3D, ProjectileVisual_2D and ProjectileVisual_3D.
        /// ProjectileRenderer2D computes its own rect.
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
