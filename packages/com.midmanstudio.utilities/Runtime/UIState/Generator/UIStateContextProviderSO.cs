// UIStateContextProviderSO.cs
// Defines one UI state context and its states.
// The generator creates one [Flags] enum per context.
// Create via: MidManStudio > Utilities > UI State Context Provider

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.UIState.Generator
{
    [System.Serializable]
    public class UIStateEntry : IArrayElementTitle
    {
        [Tooltip("Becomes the enum member name. PascalCase, no spaces.")]
        public string enumName;

        [Tooltip("Optional comment.")]
        public string comment;

        public string Name =>
            string.IsNullOrWhiteSpace(enumName) ? "Unnamed" : enumName;
    }

    
[CreateAssetMenu(fileName="UIStateContextProvider",
    menuName="MidManStudio/Utilities/UI State Context Provider", order=200)]
    public class UIStateContextProviderSO : ScriptableObject
    {
        [Header("Context Identity")]
        [Tooltip("Used as the enum class name: {ContextName}UIState. No spaces, PascalCase.")]
        public string contextName = "Menu";

        [Tooltip("Reverse-domain package ID. Must be unique across all providers.")]
        public string packageId   = "com.mygame";

        [Header("States")]
        [MID_NamedList]
        public List<UIStateEntry> states = new();

        public int StateCount => states?.Count ?? 0;

        /// <summary>Full generated enum type name. Used by the runtime context SO.</summary>
        public string GeneratedTypeName =>
            $"MidManStudio.Core.UIState.{contextName}UIState";
    }
}
