// Creates the built-in default ProjectileConfigSO + ProjectileConfigProviderSO
// + ProjectileConfigGeneratorSettingsSO assets on first import.
//
// Mirrors the pattern used by ProjectileSystemPoolProviderBootstrapper:
//   • Runs automatically via [InitializeOnLoad] using delayCall.
//   • Each asset is created only if the file does not already exist.
//   • Idempotent — safe to call multiple times.
//
// FLOW:
//   1. On import → assets created automatically.
//   2. Open MidManStudio > Projectile System > Config Type Generator.
//   3. Click "Generate Now" → writes ProjectileConfigType.cs + ProjectileConfigMapping.asset.
//   4. In the scene, assign ProjectileConfigMapping.asset to ProjectileConfigManager._mapping.
//   5. Fire with: system.Fire((int)ProjectileConfigType.Default, spawnPoints, count, context);
//
// Manual run: MidManStudio > Projectile System > Internal > Recreate Default Config Assets

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.EditorUtils
{
    [InitializeOnLoad]
    internal static class ProjectileConfigBootstrapper
    {
        // ── Output paths (kept consistent with ProjectileConfigGeneratorSettingsSO defaults) ──

        private const string ConfigsDir   = "Assets/MidManStudio/ProjectileSystem/Configs";
        private const string GeneratedDir = "Assets/MidManStudio/Generated/Projectiles";

        private const string DefaultConfigPath = ConfigsDir   + "/DefaultProjectile.asset";
        private const string ProviderPath      = GeneratedDir + "/ProjectileConfigProvider_ProjectileSystem.asset";
        private const string SettingsPath      = GeneratedDir + "/ProjectileConfigGeneratorSettings.asset";

        // ── Auto-run on first import ──────────────────────────────────────────

        static ProjectileConfigBootstrapper()
        {
            EditorApplication.delayCall += EnsureDefaults;
        }

        // ── Manual menu item ──────────────────────────────────────────────────

        [MenuItem("MidManStudio/Projectile System/Internal/Recreate Default Config Assets", priority = 101)]
        public static void EnsureDefaults()
        {
            bool changed = false;
            changed |= EnsureDefaultConfig();
            changed |= EnsureProvider();
            changed |= EnsureSettings();

            if (!changed) return;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(
                "[ProjectileConfigBootstrapper] Default projectile config assets created.\n" +
                "Next step: MidManStudio > Projectile System > Config Type Generator > Generate Now\n" +
                "Then assign ProjectileConfigMapping.asset to ProjectileConfigManager in the scene.");
        }

        // ── Default ProjectileConfigSO ─────────────────────────────────────────
        // A straight 2D bullet at speed 10, 3s lifetime, 50u range.
        // All other values use ProjectileConfigSO field defaults.
        // The asset can be customised via the Inspector after creation.

        private static bool EnsureDefaultConfig()
        {
            if (File.Exists(DefaultConfigPath)) return false;
            EnsureDir(ConfigsDir);

            var cfg = ScriptableObject.CreateInstance<ProjectileConfigSO>();
            cfg.name = "DefaultProjectile";
            AssetDatabase.CreateAsset(cfg, DefaultConfigPath);

            Debug.Log($"[ProjectileConfigBootstrapper] Created default config → {DefaultConfigPath}");
            return true;
        }

        // ── ProjectileConfigProviderSO for the package ────────────────────────
        // packageId  = "com.midmanstudio.projectilesystem"
        // priority   = 0  (system block — lower number = earlier in enum)
        // entry      = Default → DefaultProjectile, pinned at offset 0

        private static bool EnsureProvider()
        {
            if (File.Exists(ProviderPath)) return false;
            EnsureDir(GeneratedDir);

            // The config asset may have just been created in EnsureDefaultConfig();
            // AssetDatabase.LoadAssetAtPath requires the file to exist on disk first.
            AssetDatabase.Refresh();
            var defaultCfg = AssetDatabase.LoadAssetAtPath<ProjectileConfigSO>(DefaultConfigPath);

            var provider = ScriptableObject.CreateInstance<ProjectileConfigProviderSO>();
            provider.packageId   = "com.midmanstudio.projectilesystem";
            provider.displayName = "MidMan Projectile System";
            provider.priority    = 0;
            provider.entries.Add(new ProjectileConfigEntry
            {
                enumName       = "Default",
                configSO       = defaultCfg,
                comment        = "Default straight projectile",
                explicitOffset = 0   // pinned — enum value will always be 0
            });

            AssetDatabase.CreateAsset(provider, ProviderPath);
            Debug.Log($"[ProjectileConfigBootstrapper] Created default provider → {ProviderPath}");
            return true;
        }

        // ── ProjectileConfigGeneratorSettingsSO ───────────────────────────────
        // Paths match the SO's own field defaults so the generator window works
        // immediately after the settings asset is created.

        private static bool EnsureSettings()
        {
            if (File.Exists(SettingsPath)) return false;
            EnsureDir(GeneratedDir);

            var settings = ScriptableObject.CreateInstance<ProjectileConfigGeneratorSettingsSO>();
            // All output paths already default to GeneratedDir in the SO — no override needed.
            // autoGenerateOnAssetChange left as false to avoid unexpected regeneration.
            AssetDatabase.CreateAsset(settings, SettingsPath);
            Debug.Log($"[ProjectileConfigBootstrapper] Created generator settings → {SettingsPath}");
            return true;
        }

        // ─────────────────────────────────────────────────────────────────────

        private static void EnsureDir(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
    }
}
#endif
