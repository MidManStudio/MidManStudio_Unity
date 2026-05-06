// PoolTypeGeneratorSettingsSO.cs
// Project-wide settings for the pool type generator.
// Create via: MidManStudio > Utilities > Pool Type Generator Settings

using UnityEngine;

namespace MidManStudio.Core.Pools.Generator
{
    
[CreateAssetMenu(fileName="PoolTypeGeneratorSettings",
    menuName="MidManStudio/Utilities/Pool Type Generator Settings", order=150)]
    public class PoolTypeGeneratorSettingsSO : ScriptableObject
    {
        [Header("Output Paths")]
        public string objectEnumOutputPath =
            "packages/com.midmanstudio.utilities/Runtime/PoolSystems/Generated/PoolableObjectType.cs";

        public string particleEnumOutputPath =
            "packages/com.midmanstudio.utilities/Runtime/PoolSystems/Generated/PoolableParticleType.cs";

        public string networkEnumOutputPath =
            "packages/com.midmanstudio.netcode/Runtime/PoolSystems/Generated/PoolableNetworkObjectType.cs";

        [Tooltip("Commit this to source control. Keeps enum values stable across regenerations.")]
        public string lockFilePath =
            "Assets/MidManStudio/Generated/Pools/PoolTypeLock.json";

        [Header("Block Sizing")]
        [Min(10)]
        public int minimumBlockSize = 100;

        [Header("Namespace")]
        public string generatedNamespace = "MidManStudio.Core.Pools";

        [Header("Auto-Generate")]
        public bool autoGenerateOnAssetChange = false;

        [Header("Diagnostics")]
        public bool verboseProviderLogging = true;
    }
}
