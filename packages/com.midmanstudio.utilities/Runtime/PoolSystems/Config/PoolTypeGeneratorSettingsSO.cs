// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/pool-systems.md, section "PoolTypeGeneratorSettingsSO.cs"
// ============================================================================
using UnityEngine;

namespace MidManStudio.Core.Pools.Generator
{
    
    /// <summary>
    /// Project-wide settings for the Pool Type Generator: where the three enum files and the lock file are written, the
    /// minimum block size, the namespace and whether to generate again when a provider changes. Create it with Create >
    /// MidManStudio > Utilities > Pool Type Generator Settings. The enum output paths are relative to the project root and
    /// match the layout of the MidManStudio_Unity repository.
    /// </summary>
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
