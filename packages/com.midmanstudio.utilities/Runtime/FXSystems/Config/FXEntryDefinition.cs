// One entry in an EffectCategoryProviderSO or EffectTypeProviderSO.
// Mirrors PoolEntryDefinition exactly — same pinning and auto-assign rules.

using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.FX.Generator
{
    [System.Serializable]
    public class FXEntryDefinition : IArrayElementTitle
    {
        [Tooltip("Becomes the enum member name. PascalCase, no spaces.\n" +
                 "e.g. MetalSurface, BrassShell, SmallExplosion")]
        public string entryName;

        [Tooltip("Optional comment written next to the enum member.")]
        public string comment;

        [Tooltip("-1 = auto-assigned by generator.\n" +
                 ">=0 = pinned to this offset within the provider's block.\n" +
                 "Pin entries referenced from serialised inspector data.")]
        public int explicitOffset = -1;

        // IArrayElementTitle
        public string Name =>
            string.IsNullOrWhiteSpace(entryName) ? "Unnamed Entry" : entryName;
    }
}
