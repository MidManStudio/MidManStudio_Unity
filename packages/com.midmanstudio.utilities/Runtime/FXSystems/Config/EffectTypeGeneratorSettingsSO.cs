// EffectTypeGeneratorSettingsSO.cs
// Project-wide settings for the Effect Type Generator.
// Create via: MidManStudio > Utilities > Effect Type Generator Settings

using UnityEngine;

namespace MidManStudio.Core.FX.Generator
{
    [CreateAssetMenu(
        fileName = "EffectTypeGeneratorSettings",
        menuName = "MidManStudio/Utilities/Effect Type Generator Settings",
        order = 182)]
    public class EffectTypeGeneratorSettingsSO : ScriptableObject
    {
        [Header("Output Paths")]
        public string categoryEnumOutputPath =
            "packages/com.midmanstudio.utilities/Runtime/FXSystems/Generated/EffectCategory.cs";

        public string typeEnumOutputPath =
            "packages/com.midmanstudio.utilities/Runtime/FXSystems/Generated/EffectType.cs";

        [Tooltip("Commit this to source control. Keeps enum values stable across regenerations.")]
        public string lockFilePath =
            "Assets/MidManStudio/Generated/FX/EffectTypeLock.json";

        [Header("Block Sizing")]
        [Min(10)]
        public int minimumBlockSize = 100;

        [Header("Namespace")]
        public string generatedNamespace = "MidManStudio.Core.FX";

        [Header("Auto-Generate")]
        public bool autoGenerateOnAssetChange = false;
    }
}
