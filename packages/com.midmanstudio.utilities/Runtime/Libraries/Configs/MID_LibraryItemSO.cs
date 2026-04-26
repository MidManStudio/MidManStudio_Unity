
using UnityEngine;
using MidManStudio.Core.Utilities;

namespace MidManStudio.Core.Libraries
{
    /// <summary>
    /// Base ScriptableObject for any item stored in a MID_LibrarySO.
    /// Override ItemId in the inspector or in a subclass property.
    /// </summary>
    public abstract class MID_LibraryItemSO : ScriptableObject, IArrayElementTitle
    {
        [Tooltip("Unique string key used to retrieve this item from the registry.")]
        [SerializeField] private string _itemId;

        // IArrayElementTitle — _itemId first, asset name second, fallback third
        public string Name =>
            !string.IsNullOrEmpty(_itemId) ? _itemId :
            !string.IsNullOrEmpty(name) ? name :
                                             "Library Item";

        public string ItemId => !string.IsNullOrEmpty(_itemId) ? _itemId : name;
    }
}