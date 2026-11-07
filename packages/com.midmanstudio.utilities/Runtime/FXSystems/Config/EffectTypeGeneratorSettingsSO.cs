// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/fx.md, section "EffectTypeGeneratorSettingsSO.cs"
// ============================================================================
using UnityEngine;

namespace MidManStudio.Core.FX.Generator
{
    /// <summary>
    /// Project-wide settings for the Effect Type Generator: output paths, lock file, block size,
    /// namespace and the auto-generate switch. Create via Create > MidManStudio > Utilities >
    /// Effect Type Generator Settings. The generator uses the first asset of this type it finds.
    /// </summary>
    [CreateAssetMenu(
        fileName = "EffectTypeGeneratorSettings",
        menuName = "MidManStudio/Utilities/Effect Type Generator Settings",
        order = 182)]
    public class EffectTypeGeneratorSettingsSO : ScriptableObject
    {
        [Header("Output Paths")]
        /// <summary>Where <c>EffectCategory.cs</c> is written. Relative to the Unity project root.</summary>
        public string categoryEnumOutputPath =
            "packages/com.midmanstudio.utilities/Runtime/FXSystems/Generated/EffectCategory.cs";

        /// <summary>Where <c>EffectType.cs</c> is written. Relative to the Unity project root.</summary>
        public string typeEnumOutputPath =
            "packages/com.midmanstudio.utilities/Runtime/FXSystems/Generated/EffectType.cs";

        [Tooltip("Commit this to source control. Keeps enum values stable across regenerations.")]
        /// <summary>Lock file that keeps enum numbers stable. Relative to the Unity project root.</summary>
        public string lockFilePath =
            "Assets/MidManStudio/Generated/FX/EffectTypeLock.json";

        [Header("Block Sizing")]
        [Min(10)]
        /// <summary>Smallest size of the number block given to a provider.</summary>
        public int minimumBlockSize = 100;

        [Header("Namespace")]
        /// <summary>Namespace of the generated enums.</summary>
        public string generatedNamespace = "MidManStudio.Core.FX";

        [Header("Auto-Generate")]
        /// <summary>Generate again when a provider asset or this settings asset changes.</summary>
        public bool autoGenerateOnAssetChange = false;
    }
}
