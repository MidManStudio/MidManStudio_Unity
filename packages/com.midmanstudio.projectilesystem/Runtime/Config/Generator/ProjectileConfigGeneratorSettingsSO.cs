// Project-wide settings for the ProjectileConfigType enum generator.
// Create via: MidManStudio > Projectile System > Config Generator Settings

using UnityEngine;

namespace MidManStudio.Projectiles.Config
{
    [CreateAssetMenu(
        fileName = "ProjectileConfigGeneratorSettings",
        menuName  = "MidManStudio/Projectile System/Config Generator Settings",
        order     = 21)]
    public class ProjectileConfigGeneratorSettingsSO : ScriptableObject
    {
        [Header("Output Paths")]
        [Tooltip("Where the ProjectileConfigType.cs enum file is written.")]
        public string enumOutputPath =
            "Assets/MidManStudio/Generated/Projectiles/ProjectileConfigType.cs";

        [Tooltip("Path to the ProjectileConfigMappingSO asset.\n" +
                 "Created automatically on first generation if it does not exist.")]
        public string mappingAssetPath =
            "Assets/MidManStudio/Generated/Projectiles/ProjectileConfigMapping.asset";

        [Tooltip("Commit this to source control.\n" +
                 "Keeps enum integer values stable across regenerations.")]
        public string lockFilePath =
            "Assets/MidManStudio/Generated/Projectiles/ConfigTypeLock.json";

        [Header("Block Sizing")]
        [Min(10)]
        [Tooltip("Minimum block size per provider. Actual block is always a multiple of this.")]
        public int minimumBlockSize = 50;

        [Header("Namespace")]
        public string generatedNamespace = "MidManStudio.Projectiles.Config";

        [Header("Auto-Generate")]
        [Tooltip("Re-run the generator automatically whenever a provider asset changes.")]
        public bool autoGenerateOnAssetChange = false;
    }
}
