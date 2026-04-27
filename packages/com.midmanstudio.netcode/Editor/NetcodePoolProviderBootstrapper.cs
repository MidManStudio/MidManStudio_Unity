// NetcodePoolProviderBootstrapper.cs
// Creates default NetworkPoolTypeProvider assets for the netcode package on first import.

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Pools.Generator;
using MidManStudio.Core.Netcode.Generator;

namespace MidManStudio.Core.Netcode.Editor
{
    [InitializeOnLoad]
    internal static class NetcodePoolProviderBootstrapper
    {
        private const string NetcodeDir =
            "Assets/MidManStudio/Netcode/PoolProviders";

        static NetcodePoolProviderBootstrapper()
        {
            EditorApplication.delayCall += EnsureProviders;
        }

        [MenuItem("MidManStudio/Internal/Recreate Netcode Pool Providers")]
        public static void EnsureProviders()
        {
            bool changed = EnsureNetcodeNetworkProvider();
            if (changed)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log("[NetcodePoolProviderBootstrapper] Netcode providers created.");
            }
        }

        private static bool EnsureNetcodeNetworkProvider()
        {
            const string path =
                NetcodeDir + "/NetworkPoolTypeProvider_Netcode.asset";
            if (File.Exists(path)) return false;

            EnsureDirectory(NetcodeDir);
            var so = ScriptableObject.CreateInstance<NetworkPoolTypeProviderSO>();
            so.packageId   = "com.midmanstudio.netcode";
            so.displayName = "MidMan Studio Netcode";
            so.priority    = 0;
            // The netcode package defines no network pool entries by default.
            // Game code adds entries via their own NetworkPoolTypeProviderSO.

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
