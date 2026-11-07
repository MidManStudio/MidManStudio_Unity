// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/fx.md, section "EffectCategoryProviderSO.cs"
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.FX.Generator
{
    /// <summary>
    /// Contributes names to the generated <c>EffectCategory</c> enum. Each package or game project that
    /// needs its own effect categories has one asset of this type. The Effect Type Generator reads all of
    /// them and gives each provider a block of numbers, ordered by <see cref="priority"/>.
    /// </summary>
    /// <remarks>
    /// Setup: in the Project window choose Create > MidManStudio > Utilities > Effect Category Provider.
    /// Give it a unique <see cref="packageId"/>, use a priority of 100 or more for game code, then run
    /// MidManStudio > Utilities > Effect Type Generator.
    /// </remarks>
    [CreateAssetMenu(
        fileName = "EffectCategoryProvider",
        menuName = "MidManStudio/Utilities/Effect Category Provider",
        order = 180)]
    public class EffectCategoryProviderSO : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Reverse-domain package ID. Must be unique across all providers.")]
        public string packageId = "com.mygame";

        [Tooltip("Human-readable name shown in the generator window.")]
        public string displayName = "My Game";

        [Header("Block Priority")]
        [Tooltip("0 = utilities (reserved). 10 = projectile system (reserved). 100+ = user game.")]
        public int priority = 100;

        [Header("Entries")]
        [MID_NamedList]
        public List<FXEntryDefinition> entries = new List<FXEntryDefinition>();

        /// <summary>Number of entries in the list, 0 when the list is null.</summary>
        public int EntryCount => entries?.Count ?? 0;
    }
}
