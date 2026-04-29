// PackagePoolProviderBootstrapper.cs
// Editor-only. Runs automatically after import via [InitializeOnLoad].
// Creates default PoolTypeProvider ScriptableObject assets for the utilities package.
//
// Stub enum files are NO LONGER written here — they ship as committed source files
// inside the package Runtime/PoolSystems/ folder and compile immediately as part of
// MidManStudio.Utilities without any bootstrapping needed.

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Pools.Generator;

namespace MidManStudio.Core.EditorUtils
{
    [InitializeOnLoad]
    internal static class PackagePoolProviderBootstrapper
    {
        private const string UtilitiesDir = "Assets/MidManStudio/Utilities/PoolProviders";

        static PackagePoolProviderBootstrapper()
        {
            EditorApplication.delayCall += Bootstrap;
        }

        [MenuItem("MidManStudio/Utilities/Internal/Recreate Default Pool Providers")]
        public static void Bootstrap()
        {
            bool changed = false;
            changed |= EnsureUtilitiesObjectProvider();
            changed |= EnsureUtilitiesParticleProvider();

            if (changed)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log("[PoolProviderBootstrapper] Default provider assets created.");
            }
        }

        // ── Provider assets ───────────────────────────────────────────────────

        private static bool EnsureUtilitiesObjectProvider()
        {
            const string path = UtilitiesDir + "/PoolTypeProvider_Utilities.asset";
            if (File.Exists(path)) return false;

            EnsureDir(UtilitiesDir);
            var so = ScriptableObject.CreateInstance<PoolTypeProviderSO>();
            so.packageId   = "com.midmanstudio.utilities";
            so.displayName = "MidMan Studio Utilities";
            so.priority    = 0;
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "SpawnableAudio",
                comment        = "Pooled one-shot / looping audio source",
                explicitOffset = 0
            });
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "Trail",
                comment        = "Pooled trail renderer object",
                explicitOffset = 1
            });

            AssetDatabase.CreateAsset(so, path);
            return true;
        }

        private static bool EnsureUtilitiesParticleProvider()
        {
            const string path = UtilitiesDir + "/ParticlePoolTypeProvider_Utilities.asset";
            if (File.Exists(path)) return false;

            EnsureDir(UtilitiesDir);
            var so = ScriptableObject.CreateInstance<ParticlePoolTypeProviderSO>();
            so.packageId   = "com.midmanstudio.utilities";
            so.displayName = "MidMan Studio Utilities";
            so.priority    = 0;
            // Utilities defines no particle types by default

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