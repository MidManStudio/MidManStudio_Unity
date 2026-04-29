// PoolTypeGeneratorSettingsSO.cs
// Project-wide settings for the pool type generator.
// Create via: MidManStudio > Pool Type Generator Settings

using UnityEngine;

namespace MidManStudio.Core.Pools.Generator
{
    [CreateAssetMenu(
        fileName = "PoolTypeGeneratorSettings",
        menuName = "MidManStudio/Utilities/Pool Type Generator Settings")]
    public class PoolTypeGeneratorSettingsSO : ScriptableObject
    {
        [Header("Output Paths")]
        [Tooltip("Where PoolableObjectType.cs is written.\n" +
                 "Points into the utilities package Runtime so the enum stays in\n" +
                 "MidManStudio.Utilities assembly. Never move to Assets/.")]
        public string objectEnumOutputPath =
            "packages/com.midmanstudio.utilities/Runtime/PoolSystems/PoolableObjectType.cs";

        [Tooltip("Where PoolableParticleType.cs is written.\n" +
                 "Same rule — must stay in MidManStudio.Utilities assembly.")]
        public string particleEnumOutputPath =
            "packages/com.midmanstudio.utilities/Runtime/PoolSystems/PoolableParticleType.cs";

        [Tooltip("Where PoolableNetworkObjectType.cs is written.\n" +
                 "Points into the NETCODE package — only that package uses it.\n" +
                 "Must stay in MidManStudio.Netcode assembly.")]
        public string networkEnumOutputPath =
            "packages/com.midmanstudio.netcode/Runtime/PoolSystems/PoolableNetworkObjectType.cs";

        [Tooltip("Lock file path. Commit this to source control.\n" +
                 "Keeps enum values stable across regenerations.")]
        public string lockFilePath =
            "Assets/MidManStudio/Generated/PoolTypeLock.json";

        [Header("Block Sizing")]
        [Tooltip("Minimum gap between provider blocks. Must be >= 10.\n" +
                 "Recommended: 100.")]
        [Min(10)]
        public int minimumBlockSize = 100;

        [Header("Namespace")]
        public string generatedNamespace = "MidManStudio.Core.Pools";

        [Header("Auto-Generate")]
        [Tooltip("Regenerate automatically when any PoolTypeProvider asset changes.")]
        public bool autoGenerateOnAssetChange = false;

        [Header("Diagnostics")]
        [Tooltip("Log all discovered providers on every generation.")]
        public bool verboseProviderLogging = true;
    }
}