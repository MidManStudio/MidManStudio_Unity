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

        [Header("Debug")]
        [SerializeField] MID_LogLevel _LogLevel = MID_LogLevel.None;

        protected override void Awake()
        {
            base.Awake();

            // Validate Rust struct sizes first — crash loudly if there is a mismatch
            // rather than silently corrupting memory later.
            // NOTE: ProjectileLib.ValidateStructSizes() now also throws
            // InvalidOperationException (instead of letting DllNotFoundException
            // escape uncaught) when the native lib itself fails to load, so this
            // catch correctly handles both "size mismatch" and "lib missing" cases
            // without any change needed here.
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

            // Register Rust-side movement params for Wave/Circular types.
            // ProjectileConfigSO.RegisterMovementParams() already no-ops safely
            // when the native lib is unavailable, but this try/catch is kept as
            // defense-in-depth so a future change there (or any other
            // unanticipated exception) can never abort whichever loop or
            // coroutine called Register() — a config that fails to register its
            // native movement params should not prevent every config after it
            // in the same batch from registering.
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

            // FIX ("physics-based projectile visuals only show correctly after
            // firing several times", reported for both 2D and 3D — this repo's
            // Runtime/Shaders and PackageSandbox/.../PROJECTILE_SPRITE_ATLAS just
            // moved from the legacy .spriteatlas format to .spriteatlasv2):
            //
            // Unity's Sprite Atlas system resolves a packed Sprite's `.texture`
            // LAZILY — the first access(es) can still be mid-redirect from the
            // sprite's original individual texture to the shared packed atlas
            // texture, and `.texture`/`.rect` only report the atlas's FINAL,
            // correct values once that resolution has actually completed. This
            // is a background/on-enter-Play-Mode process, not something that's
            // guaranteed finished by the time any particular script's Awake/
            // Start runs.
            //
            // ProjectileVisual_2D/3D's ApplyMaterial/ApplyShapeMeshOptimised
            // already re-read `sprite.texture` fresh on every single fire (see
            // those files), so they DO self-correct — but only once some fire
            // happens to land AFTER Unity's atlas resolution has caught up. If
            // the very first access ever made to a given sprite is that
            // projectile's first live shot, "self-correct" means "the first
            // several shots of actual gameplay show the wrong/stale texture."
            //
            // Fix: touch every registered sprite's .texture right here, at
            // registration — which normally happens during scene load, well
            // before the player ever fires — so Unity's one-time atlas
            // resolution cost lands on load, not on gameplay. This does NOT
            // help the underlying async timing at all; it just moves WHEN that
            // timing cost gets paid to a moment nobody is watching.
            //
            // For 2D configs that don't need a CustomShape mesh at all, pair
            // this with ProjectileVisual_2D's new _forceSpriteRendererOnly
            // inspector override — SpriteRenderer resolves atlas textures
            // through Unity's own built-in, continuously-re-resolved rendering
            // path every frame (not a one-time snapshot the way this manual
            // mesh/shader/UVRect path works), so it isn't subject to this
            // timing window at all and needs no warm-up to be reliable.
            if (config.UseSprite && config.ProjectileSprite != null)
            {
                _ = config.ProjectileSprite.texture; // access is the trigger — see above
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

                MID_Logger.LogError(_LogLevel, $"[ProjectileRegistry] No config for id {configId}");
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
