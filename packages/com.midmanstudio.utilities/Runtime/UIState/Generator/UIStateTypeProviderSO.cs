// UIStateTypeProviderSO.cs
// Contributes UI state entries to the generated UIStateId [Flags] enum.
// Values are powers of 2 assigned by bit position — the generator handles this.
// Create via: MidManStudio > Utilities > UI State Type Provider

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.UIState.Generator
{
    [System.Serializable]
    public class UIStateEntryDefinition : IArrayElementTitle
    {
        [Tooltip("Becomes the UIStateId enum member. PascalCase, no spaces.")]
        public string enumName;

        [Tooltip("Optional comment written next to the enum member.")]
        public string comment;

        public string Name => string.IsNullOrWhiteSpace(enumName) ? "Unnamed" : enumName;
    }

    [CreateAssetMenu(
        fileName = "UIStateTypeProvider",
        menuName = "MidManStudio/Utilities/UI State Type Provider")]
    public class UIStateTypeProviderSO : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Reverse-domain package ID. Must be unique across all UI state providers.")]
        public string packageId   = "com.mygame";
        public string displayName = "My Game";

        [Header("Priority")]
        [Tooltip("Lower = earlier bit block. Use 100+ for game code.")]
        public int priority = 100;

        [Header("States")]
        [MID_NamedList]
        public List<UIStateEntryDefinition> states = new();

        public int StateCount => states?.Count ?? 0;
    }
}
