// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio/enum-ids.md, section "AudioEntryDefinition.cs"
// ============================================================================
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Audio.Generator
{
    /// <summary>
    /// One row in an <see cref="AudioCategoryProviderSO"/> or <see cref="AudioIdProviderSO"/>.
    /// It becomes one member of the generated enum.
    /// </summary>
    [System.Serializable]
    public class AudioEntryDefinition : IArrayElementTitle
    {
        [Tooltip("Becomes the enum member name. PascalCase, no spaces.\n" +
                 "e.g. SwordHit, MenuClick, TavernAmbience")]
        public string entryName;

        [Tooltip("Optional comment written next to the enum member.")]
        public string comment;

        [Tooltip("-1 = auto-assigned by the generator.\n" +
                 ">=0 = pinned to this offset within the provider's block.\n" +
                 "Pin entries that other assets or saved data refer to by number.")]
        public int explicitOffset = -1;

        // IArrayElementTitle
        public string Name
        {
            get { return string.IsNullOrWhiteSpace(entryName) ? "Unnamed Entry" : entryName; }
        }
    }
}
