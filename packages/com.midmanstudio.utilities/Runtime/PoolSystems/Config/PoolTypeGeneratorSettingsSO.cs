// PoolTypeGeneratorSettingsSO.cs
// Project-wide settings for the pool type generator.
// Place one instance anywhere in your Assets folder.
// Create via: MidManStudio > Pool Type Generator Settings

using UnityEngine;

namespace MidManStudio.Core.Pools.Generator
{
    [CreateAssetMenu(
        fileName = "PoolTypeGeneratorSettings",
        menuName = "MidManStudio/Pool Type Generator Settings")]
    public class PoolTypeGeneratorSettingsSO : ScriptableObject
    {
        [Header("Output Paths")]
        [Tooltip("Project-relative path where PoolableObjectType.cs is written.\n" +
                 "Must be inside Assets/. Include filename.")]
        public string objectEnumOutputPath =
            "Assets/MidManStudio/Generated/PoolableObjectType.cs";

        [Tooltip("Project-relative path where PoolableParticleType.cs is written.\n" +
                 "Must be inside Assets/. Include filename.")]
        public string particleEnumOutputPath =
            "Assets/MidManStudio/Generated/PoolableParticleType.cs";

        [Tooltip("Project-relative path for the stable-value lock file.\n" +
                 "Commit this file to source control — it prevents enum values\n" +
                 "from shifting when entries are added or reordered.")]
        public string lockFilePath =
            "Assets/MidManStudio/Generated/PoolTypeLock.json";

        [Header("Block Sizing")]
        [Tooltip("Minimum gap between provider blocks. Must be >= 10.\n" +
                 "A provider's block is ceil(entryCount / blockSize) * blockSize wide.\n" +
                 "Recommended: 100.")]
        [Min(10)]
        public int minimumBlockSize = 100;

        [Header("Namespace")]
        [Tooltip("Namespace written into the generated files.")]
        public string generatedNamespace = "MidManStudio.Core.Pools";

        [Header("Auto-Generate")]
        [Tooltip("If true, regeneration runs automatically when any PoolTypeProvider\n" +
                 "asset is created, deleted, or modified.\n" +
                 "Disable if you prefer manual control.")]
        public bool autoGenerateOnAssetChange = false;
    }
}
