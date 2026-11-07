// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/fx.md, section "FXEntryDefinition.cs"
// ============================================================================
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.FX.Generator
{
    /// <summary>
    /// One entry in an <see cref="EffectCategoryProviderSO"/> or <see cref="EffectTypeProviderSO"/>.
    /// Has the same three fields and the same pinning and auto-assign rules as <c>PoolEntryDefinition</c>.
    /// </summary>
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
        /// <summary>List element label: the entry name, or "Unnamed Entry" when it is blank.</summary>
        public string Name =>
            string.IsNullOrWhiteSpace(entryName) ? "Unnamed Entry" : entryName;
    }
}
