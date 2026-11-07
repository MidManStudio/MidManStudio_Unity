// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/libraries.md, section "MID_BasicLibraryItemSO.cs"
// ============================================================================
using UnityEngine;

namespace MidManStudio.Core.Libraries
{
    /// <summary>
    /// Ready-to-use library item for cases that need no custom data: the id from the base class, a display
    /// name, a description, an optional icon and optional tags. Create via Create > MidManStudio >
    /// Utilities > Library Item (Basic). For an item with its own fields, derive from
    /// <see cref="MID_LibraryItemSO"/> instead.
    /// </summary>
    
[CreateAssetMenu(fileName="NewLibraryItem",
    menuName="MidManStudio/Utilities/Library Item (Basic)", order=130)]
    public class MID_BasicLibraryItemSO : MID_LibraryItemSO
    {
        [Tooltip("Human-readable display name shown in UI.")]
        public string displayName;

        [Tooltip("Longer description for tooltips, menus, etc.")]
        [TextArea(2, 4)]
        public string description;

        [Tooltip("Optional icon or thumbnail sprite.")]
        public Sprite icon;

        [Tooltip("Optional additional tags for filtering.")]
        public string[] tags;
    }
}
