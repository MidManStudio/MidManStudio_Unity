// EffectTypeProviderSO.cs
// ScriptableObject that contributes entries to the EffectType enum.
// One of these per package/game that needs custom effect variants.

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.FX.Generator
{
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

        public int EntryCount => entries?.Count ?? 0;
    }
}
