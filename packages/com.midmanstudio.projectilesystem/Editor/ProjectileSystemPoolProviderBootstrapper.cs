// ProjectileSystemPoolProviderBootstrapper.cs
// Creates default pool provider assets for the projectile system on first import.
// Runs automatically via [InitializeOnLoad].

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Pools.Generator;

namespace MidManStudio.Projectiles.Editor
{
    [InitializeOnLoad]
    internal static class ProjectileSystemPoolProviderBootstrapper
    {
        private const string ProjectileDir =
            "Assets/MidManStudio/ProjectileSystem/PoolProviders";

        static ProjectileSystemPoolProviderBootstrapper()
        {
            EditorApplication.delayCall += EnsureProviders;
        }

        [MenuItem("MidManStudio/Internal/Recreate Projectile Pool Providers")]
        public static void EnsureProviders()
        {
            bool changed = false;
            changed |= EnsureObjectProvider();
            changed |= EnsureParticleProvider();

            if (changed)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log("[ProjectilePoolBootstrapper] Projectile pool providers created.");
            }
        }

        private static bool EnsureObjectProvider()
        {
            const string path =
                ProjectileDir + "/PoolTypeProvider_ProjectileSystem.asset";
            if (File.Exists(path)) return false;

            EnsureDir(ProjectileDir);
            var so = ScriptableObject.CreateInstance<PoolTypeProviderSO>();
            so.packageId   = "com.midmanstudio.projectilesystem";
            so.displayName = "MidMan Projectile System";
            so.priority    = 10;
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Projectile_Visual2D",
                comment        = "2D projectile sprite visual",
                explicitOffset = 0
            });
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Projectile_Visual3D",
                comment        = "3D projectile visual",
                explicitOffset = 1
            });
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Projectile_Flipbook",
                comment        = "Sprite-sheet flipbook for impact explosions",
                explicitOffset = 2
            });

            AssetDatabase.CreateAsset(so, path);
            return true;
        }

        private static bool EnsureParticleProvider()
        {
            const string path =
                ProjectileDir + "/ParticlePoolTypeProvider_ProjectileSystem.asset";
            if (File.Exists(path)) return false;

            EnsureDir(ProjectileDir);
            var so = ScriptableObject.CreateInstance<ParticlePoolTypeProviderSO>();
            so.packageId   = "com.midmanstudio.projectilesystem";
            so.displayName = "MidMan Projectile System";
            so.priority    = 10;
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Projectile_Impact",
                comment        = "Generic hit / impact particle",
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
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Projectile_Tracer",
                comment        = "Tracer round particle",
                explicitOffset = 5
            });

            AssetDatabase.CreateAsset(so, path);
            return true;
        }

        private static void EnsureDir(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
    }
}
#endif
