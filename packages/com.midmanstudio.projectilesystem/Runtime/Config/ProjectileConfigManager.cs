// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "ProjectileConfigManager.cs"
// ============================================================================
// Runtime bridge between the generated ProjectileConfigType enum and the
// session-local ushort configIds assigned by ProjectileRegistry.
//
// SETUP:
//   1. Generate enum + mapping SO: MidManStudio > Projectile System > Config Type Generator.
//   2. Assign the generated ProjectileConfigMappingSO to _mapping in the Inspector.
//   3. Place on the same persistent GameObject as ProjectileRegistry.
//   4. Registration happens in Awake(), ordered explicitly (-150) to run
//      after ProjectileRegistry's own Awake() (-200).
//
// USAGE:
//   ushort id = ProjectileConfigManager.Instance.GetConfigId((int)ProjectileConfigType.FireBall);
//   // or use the extension method (it takes the enum value as an int):
//   system.Fire((int)ProjectileConfigType.FireBall, spawnPoints, count, context);

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Singleton;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Managers;
using MidManStudio.Core.Logging;
namespace MidManStudio.Projectiles.Config
{
    /// <summary>
    /// Maps generated ProjectileConfigType enum values to the runtime ushort
    /// configIds that ProjectileRegistry assigns. RegisterAll() runs in Awake()
    /// at execution order -150, right after ProjectileRegistry (-200), so the
    /// ids exist before any other script's Start() reads them.
    /// </summary>
    [DefaultExecutionOrder(-150)]
    [AddComponentMenu("MidMan/Projectile System/Projectile Config Manager")]
    public sealed class ProjectileConfigManager : Singleton<ProjectileConfigManager>
    {
        #region Inspector

        [Header("Config Mapping  (AUTO-GENERATED asset)")]
        [SerializeField] private ProjectileConfigMappingSO _mapping;

        [Header("Debug")]
        [SerializeField] private bool _logRegistrations = false;
        [SerializeField] private MID_LogLevel _LogLevel = MID_LogLevel.None;

        #endregion

        // enum int value → runtime ushort configId
        private readonly Dictionary<int, ushort> _enumToId = new(128);

        // ── Lifecycle ─────────────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();

            if (_mapping != null)
                RegisterAll(_mapping);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Register all configs in the mapping SO into ProjectileRegistry.
        /// The mapping array is ordered by enum value, so array index i is
        /// enum value i. The registry assigns configIds in registration order and
        /// skips null padding slots, so a configId is not guaranteed to equal the
        /// enum value. Always translate through GetConfigId.
        /// Registering the same asset again returns its existing id, so a repeat
        /// call is harmless. Call it before any Fire() calls.
        /// </summary>
        public void RegisterAll(ProjectileConfigMappingSO mapping)
        {
            if (mapping == null) return;
            if (!ProjectileRegistry.HasInstance)
            {
              MID_Logger.LogDebug(_LogLevel,
                    "[ProjectileConfigManager] ProjectileRegistry not found. " +
                    "Ensure ProjectileRegistry.Awake() runs before ProjectileConfigManager.Start().");
                return;
            }

            var reg = ProjectileRegistry.Instance;
            _enumToId.Clear();

            for (int i = 0; i < mapping.Configs.Length; i++)
            {
                var cfg = mapping.Configs[i];
                if (cfg == null) continue;     // padding slot

                ushort id = reg.Register(cfg);
                _enumToId[i] = id;

                if (_logRegistrations)
                    MID_Logger.LogDebug(_LogLevel, $"[ProjectileConfigManager] [{i}] {cfg.name}  → configId={id}");
            }

            MID_Logger.LogDebug(_LogLevel, $"[ProjectileConfigManager] Registered {_enumToId.Count} configs.");
        }

        /// <summary>
        /// Get the runtime ushort configId for a ProjectileConfigType enum value.
        /// Returns ushort.MaxValue and logs a warning if not registered.
        /// </summary>
        public ushort GetConfigId(int configTypeValue)
        {
            if (_enumToId.TryGetValue(configTypeValue, out ushort id)) return id;
            MID_Logger.LogWarning(_LogLevel,
                $"[ProjectileConfigManager] ConfigType {configTypeValue} not registered. " +
                "Ensure RegisterAll() was called at startup before Fire() calls.");
            return ushort.MaxValue;
        }

        // ── Debug ─────────────────────────────────────────────────────────────

        [ContextMenu("Log All Registered Config Types")]
        private void LogAll()
        {
            foreach (var kv in _enumToId)
            {
                var cfg = (_mapping != null && kv.Key < _mapping.Configs.Length)
                    ? _mapping.Configs[kv.Key] : null;
                MID_Logger.LogDebug(_LogLevel, $"  [{kv.Key}] configId={kv.Value}  so={cfg?.name ?? "null"}");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Extension method — lets callers pass the generated enum directly
    //  without knowing the underlying ushort configId.
    // ─────────────────────────────────────────────────────────────────────────

    public static class ProjectileConfigTypeExtensions
    {
        /// <summary>
        /// Fire projectiles using a generated ProjectileConfigType int value.
        /// Example: <c>system.Fire((int)ProjectileConfigType.FireBall, pts, n, ctx);</c>
        /// Requires <see cref="ProjectileConfigManager.Instance"/> to be initialised.
        /// </summary>
        public static void Fire(
            this MID_MasterProjectileSystem system,
            int                             configTypeValue,
            SpawnPoint[]                    spawnPoints,
            int                             count,
            WeaponFireContext               context)
        {
            if (!ProjectileConfigManager.HasInstance)
            {
                Debug.LogError(
                    "[ProjectileConfigTypeExtensions] ProjectileConfigManager not found. " +
                    "Add it to the scene and call RegisterAll() at startup.");
                return;
            }
            ushort id = ProjectileConfigManager.Instance.GetConfigId(configTypeValue);
            if (id == ushort.MaxValue) return;
            system.Fire(id, spawnPoints, count, context);
        }
    }
}
