// Creates default EffectCategoryProvider and EffectTypeProvider assets for utilities.
// Runs automatically via [InitializeOnLoad].

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.FX.Generator;

namespace MidManStudio.Core.EditorUtils.FX
{
    [InitializeOnLoad]
    internal static class PackageEffectProviderBootstrapper
    {
        private const string Dir = "Assets/MidManStudio/Utilities/FXProviders";

        static PackageEffectProviderBootstrapper()
        {
            EditorApplication.delayCall += Bootstrap;
        }

        [MenuItem("MidManStudio/Utilities/Internal/Recreate Default Effect Providers")]
        public static void Bootstrap()
        {
            bool changed = false;
            changed |= EnsureCategoryProvider();
            changed |= EnsureTypeProvider();
            if (changed) { AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); Debug.Log("[EffectProviderBootstrapper] Default provider assets created."); }
        }

        private static bool EnsureCategoryProvider()
        {
            const string path = Dir + "/EffectCategoryProvider_Utilities.asset";
            if (File.Exists(path)) return false;
            EnsureDir(Dir);

            var so = ScriptableObject.CreateInstance<EffectCategoryProviderSO>();
            so.packageId   = "com.midmanstudio.utilities";
            so.displayName = "MidMan Studio Utilities";
            so.priority    = 0;
            so.entries.Add(new FXEntryDefinition { entryName = "Impact",        comment = "Surface hit sparks / debris", explicitOffset = 0 });
            so.entries.Add(new FXEntryDefinition { entryName = "MuzzleFlash",   comment = "Weapon barrel flash",         explicitOffset = 1 });
            so.entries.Add(new FXEntryDefinition { entryName = "ShellEjection", comment = "Casing ejection",             explicitOffset = 2 });
            so.entries.Add(new FXEntryDefinition { entryName = "Explosion",     comment = "Area blast effect",           explicitOffset = 3 });
            AssetDatabase.CreateAsset(so, path);
            return true;
        }

        private static bool EnsureTypeProvider()
        {
            const string path = Dir + "/EffectTypeProvider_Utilities.asset";
            if (File.Exists(path)) return false;
            EnsureDir(Dir);

            var so = ScriptableObject.CreateInstance<EffectTypeProviderSO>();
            so.packageId   = "com.midmanstudio.utilities";
            so.displayName = "MidMan Studio Utilities";
            so.priority    = 0;
            so.entries.Add(new FXEntryDefinition { entryName = "Generic",          comment = "Catch-all fallback",         explicitOffset = 0  });
            so.entries.Add(new FXEntryDefinition { entryName = "MetalSurface",     comment = "Metal impact sparks",        explicitOffset = 1  });
            so.entries.Add(new FXEntryDefinition { entryName = "ConcreteSurface",  comment = "Concrete dust puff",         explicitOffset = 2  });
            so.entries.Add(new FXEntryDefinition { entryName = "DirtSurface",      comment = "Dirt debris",                explicitOffset = 3  });
            so.entries.Add(new FXEntryDefinition { entryName = "WoodSurface",      comment = "Wood splinter",              explicitOffset = 4  });
            so.entries.Add(new FXEntryDefinition { entryName = "FleshSurface",     comment = "Organic hit",                explicitOffset = 5  });
            so.entries.Add(new FXEntryDefinition { entryName = "SmallMuzzle",      comment = "Pistol / SMG flash",         explicitOffset = 10 });
            so.entries.Add(new FXEntryDefinition { entryName = "MediumMuzzle",     comment = "Rifle flash",                explicitOffset = 11 });
            so.entries.Add(new FXEntryDefinition { entryName = "LargeMuzzle",      comment = "Heavy weapon flash",         explicitOffset = 12 });
            so.entries.Add(new FXEntryDefinition { entryName = "BrassShell",       comment = "Standard brass casing",      explicitOffset = 20 });
            so.entries.Add(new FXEntryDefinition { entryName = "SteelShell",       comment = "Steel casing",               explicitOffset = 21 });
            so.entries.Add(new FXEntryDefinition { entryName = "SmallExplosion",   comment = "Grenade / small charge",     explicitOffset = 30 });
            so.entries.Add(new FXEntryDefinition { entryName = "MediumExplosion",  comment = "RPG / medium charge",        explicitOffset = 31 });
            so.entries.Add(new FXEntryDefinition { entryName = "LargeExplosion",   comment = "Vehicle / large charge",     explicitOffset = 32 });
            AssetDatabase.CreateAsset(so, path);
            return true;
        }

        private static void EnsureDir(string dir) { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
    }
}
#endif
