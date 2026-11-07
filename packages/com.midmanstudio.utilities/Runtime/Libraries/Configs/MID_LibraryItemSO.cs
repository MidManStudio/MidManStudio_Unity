// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/libraries.md, section "MID_LibraryItemSO.cs"
// ============================================================================
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Libraries
{
    /// <summary>
    /// Abstract base class for all library items. Derive from it to add game-specific data fields.
    /// For an item with only a name and a description use <see cref="MID_BasicLibraryItemSO"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// [CreateAssetMenu(menuName = "MyGame/Libraries/WeaponItem")]
    /// public class WeaponItemSO : MID_LibraryItemSO { public float damage; public Sprite icon; }
    ///
    /// var weapon = MID_LibraryRegistry.Instance.GetItem&lt;WeaponItemSO&gt;("Weapons", "Sword");
    /// </code>
    /// </example>
    public abstract class MID_LibraryItemSO : ScriptableObject, IArrayElementTitle
    {
        [Tooltip("Unique string key used to retrieve this item from the registry.\n" +
                 "Leave blank to use the asset file name as the key.")]
        [SerializeField] private string _itemId;

        /// <summary>
        /// Unique string key. Falls back to the asset file name if left blank.
        /// </summary>
        public string ItemId =>
            !string.IsNullOrWhiteSpace(_itemId) ? _itemId : name;

        /// <summary>List element label in the inspector (<see cref="ItemId"/>).</summary>
        public string Name => ItemId;
    }
}
