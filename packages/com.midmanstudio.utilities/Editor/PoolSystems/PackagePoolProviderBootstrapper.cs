// PackagePoolProviderBootstrapper.cs
// Editor-only. Creates the default PoolTypeProvider and ParticlePoolTypeProvider
// assets for com.midmanstudio.utilities and com.midmanstudio.projectilesystem
// if they don't already exist in the project.
//
// Runs automatically once after import via [InitializeOnLoad].
// Also exposed as a menu item for manual recovery.

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Pools.Generator;

namespace MidManStudio.Core.Editor
{
    [InitializeOnLoad]
    internal static class PackagePoolProviderBootstrapper
    {
        // Paths relative to project root (Assets/...)
        // Stored inside the package folders so they ship with the package.
        // Users should NOT modify these — create your own provider asset instead.

        private const string UtilitiesDir    = "Assets/MidManStudio/Utilities/PoolProviders";
        private const string ProjectileDir   = "Assets/MidManStudio/ProjectileSystem/PoolProviders";

        static PackagePoolProviderBootstrapper()
        {
            // Defer so the asset database is fully ready after a domain reload
            EditorApplication.delayCall += EnsureDefaultProviders;
        }

        [MenuItem("MidManStudio/Internal/Recreate Default Pool Providers")]
        public static void EnsureDefaultProviders()
        {
            bool changed = false;
            changed |= EnsureUtilitiesObjectProvider();
            changed |= EnsureUtilitiesParticleProvider();
            changed |= EnsureProjectileObjectProvider();
            changed |= EnsureProjectileParticleProvider();

            if (changed)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log("[PoolProviderBootstrapper] Default provider assets created/updated.");
            }
        }

        // ── Utilities — object pool ───────────────────────────────────────────

        private static bool EnsureUtilitiesObjectProvider()
        {
            const string path = UtilitiesDir + "/PoolTypeProvider_Utilities.asset";
            if (File.Exists(path)) return false;

            EnsureDirectory(UtilitiesDir);
            var so = ScriptableObject.CreateInstance<PoolTypeProviderSO>();
            so.packageId   = "com.midmanstudio.utilities";
            so.displayName = "MidMan Studio Utilities";
            so.priority    = 0;
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "SpawnableAudio",
                comment        = "Pooled one-shot / looping audio source",
                explicitOffset = 0    // pinned — never moves
            });
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Trail",
                comment        = "Pooled trail renderer object",
                explicitOffset = 1    // pinned — never moves
            });

            AssetDatabase.CreateAsset(so, path);
            return true;
        }

        // ── Utilities — particle pool ─────────────────────────────────────────

        private static bool EnsureUtilitiesParticleProvider()
        {
            const string path = UtilitiesDir + "/ParticlePoolTypeProvider_Utilities.asset";
            if (File.Exists(path)) return false;

            EnsureDirectory(UtilitiesDir);
            var so = ScriptableObject.CreateInstance<ParticlePoolTypeProviderSO>();
            so.packageId   = "com.midmanstudio.utilities";
            so.displayName = "MidMan Studio Utilities";
            so.priority    = 0;
            // Utilities defines no particle types by default.
            // User game or other packages start at block 0 for particles.

            AssetDatabase.CreateAsset(so, path);
            return true;
        }

        // ── Projectile system — object pool ───────────────────────────────────

        private static bool EnsureProjectileObjectProvider()
        {
            const string path = ProjectileDir + "/PoolTypeProvider_ProjectileSystem.asset";
            if (File.Exists(path)) return false;

            EnsureDirectory(ProjectileDir);
            var so = ScriptableObject.CreateInstance<PoolTypeProviderSO>();
            so.packageId   = "com.midmanstudio.projectilesystem";
            so.displayName = "MidMan Projectile System";
            so.priority    = 10;
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Projectile_Visual2D",
                comment        = "2D projectile visual / sprite object",
                explicitOffset = 0
            });
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Projectile_Visual3D",
                comment        = "3D projectile visual object",
                explicitOffset = 1
            });
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Projectile_Trail",
                comment        = "Projectile trail renderer",
                explicitOffset = 2
            });

            AssetDatabase.CreateAsset(so, path);
            return true;
        }

        // ── Projectile system — particle pool ─────────────────────────────────

        private static bool EnsureProjectileParticleProvider()
        {
            const string path = ProjectileDir + "/ParticlePoolTypeProvider_ProjectileSystem.asset";
            if (File.Exists(path)) return false;

            EnsureDirectory(ProjectileDir);
            var so = ScriptableObject.CreateInstance<ParticlePoolTypeProviderSO>();
            so.packageId   = "com.midmanstudio.projectilesystem";
            so.displayName = "MidMan Projectile System";
            so.priority    = 10;
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Projectile_Impact",
                comment        = "Standard hit / impact particle",
                explicitOffset = 0
            });
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Projectile_Explosion_Small",
                comment        = "Small explosion",
                explicitOffset = 1
            });
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Projectile_Explosion_Medium",
                comment        = "Medium explosion",
                explicitOffset = 2
            });
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Projectile_Explosion_Large",
                comment        = "Large explosion",
                explicitOffset = 3
            });
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Projectile_Headshot",
                comment        = "Headshot / critical hit particle",
                explicitOffset = 4
            });

            AssetDatabase.CreateAsset(so, path);
            return true;
        }

        private static void EnsureDirectory(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
    }
}
#endif
