// MID_BasicLibraryItemSO.cs
// Concrete library item for cases that don't need custom data fields.
// Contains just an id, display name, description, and optional sprite.
//
// For items with custom fields, subclass MID_LibraryItemSO instead:
//   [CreateAssetMenu(menuName = "MyGame/Libraries/WeaponItem")]
//   public class WeaponItemSO : MID_LibraryItemSO { public float damage; }
//
// CREATE:
//   Right-click in Project > MidManStudio > Utilities > Library Item (Basic)

using UnityEngine;

namespace MidManStudio.Core.Libraries
{
    /// <summary>
    /// Ready-to-use library item with display name, description, and optional sprite.
    /// Create via: right-click > MidManStudio > Utilities > Library Item (Basic)
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
