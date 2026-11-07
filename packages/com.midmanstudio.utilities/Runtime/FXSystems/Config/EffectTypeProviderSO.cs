// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/fx.md, section "EffectTypeProviderSO.cs"
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.FX.Generator
{
    /// <summary>
    /// Contributes names to the generated <c>EffectType</c> enum (the specific variant inside a
    /// category, such as MetalSurface or BrassShell). One asset per package or game project that needs
    /// its own effect variants. Works the same way as <see cref="EffectCategoryProviderSO"/>.
    /// </summary>
    [CreateAssetMenu(
        fileName = "EffectTypeProvider",
        menuName = "MidManStudio/Utilities/Effect Type Provider",
        order = 181)]
    public class EffectTypeProviderSO : ScriptableObject
    {
        [Header("Identity")]
        public string packageId   = "com.mygame";
        public string displayName = "My Game";

        [Header("Block Priority")]
        public int priority = 100;

        [Header("Entries")]
        [MID_NamedList]
        public List<FXEntryDefinition> entries = new List<FXEntryDefinition>();

        /// <summary>Number of entries in the list, 0 when the list is null.</summary>
        public int EntryCount => entries?.Count ?? 0;
    }
}
