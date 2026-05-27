// EffectCategoryProviderSO.cs
// ScriptableObject that contributes entries to the EffectCategory enum.
// One of these lives in every package (and game project) that needs custom categories.
//
// SETUP:
//   Right-click in Project > MidManStudio > Utilities > Effect Category Provider
//   Set packageId, priority >= 100 for user game code.
//   Run MidManStudio > Utilities > Effect Type Generator.

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.FX.Generator
{
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

        public int EntryCount => entries?.Count ?? 0;
    }
}
