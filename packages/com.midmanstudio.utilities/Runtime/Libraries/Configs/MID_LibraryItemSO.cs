// MID_LibraryItemSO.cs
// Abstract base for library items. Subclass to add your own data fields.
//
// QUICK START — just need a named item with no extra data?
//   Use MID_BasicLibraryItemSO (CreateAssetMenu included).
//
// CUSTOM DATA — create your own subclass:
//   [CreateAssetMenu(menuName = "MyGame/Libraries/WeaponItem")]
//   public class WeaponItemSO : MID_LibraryItemSO
//   {
//       public float damage;
//       public Sprite icon;
//   }
//
// RETRIEVAL:
//   var weapon = MID_LibraryRegistry.Instance.GetItem<WeaponItemSO>("Weapons", "Sword");

using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Libraries
{
    /// <summary>
    /// Abstract base class for all library items.
    /// Subclass to add game-specific data fields.
    /// For simple string-only items, use <see cref="MID_BasicLibraryItemSO"/>.
    /// </summary>
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

        // IArrayElementTitle — shows ItemId in the inspector list
        public string Name => ItemId;
    }
}
