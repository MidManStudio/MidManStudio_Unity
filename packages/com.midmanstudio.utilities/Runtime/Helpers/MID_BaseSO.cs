// MID_BaseSO.cs
// Base ScriptableObject for all MidManStudio SO assets.
// Provides:
//   - Custom icon per instance (drag any Texture2D into _customIcon in the inspector)
//   - Group icon per type (override GroupIconPath in subclass to point to a project asset)
//   - Auto-applies icon on asset import/change via MID_BaseSOEditor
//   - IArrayElementTitle so any List<MID_BaseSO> uses SO name by default
//
// USAGE — custom icon per instance:
//   Select the SO asset, expand "Icon" in the inspector, drag a Texture2D.
//
// USAGE — group icon (all assets of one type share an icon):
//   [CreateAssetMenu(...)]
//   public class WeaponItemSO : MID_BaseSO
//   {
//       protected override string GroupIconPath =>
//           "Packages/com.midmanstudio.utilities/Editor/Icons/weapon_icon.png";
//   }
//
// USAGE — type-level icon via Unity attribute (Unity 2021.2+, no runtime cost):
//   [Icon("Packages/com.midmanstudio.utilities/Editor/Icons/weapon_icon.png")]
//   public class WeaponItemSO : MID_BaseSO { }

using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core
{
    /// <summary>
    /// Base class for all MidManStudio ScriptableObject assets.
    /// Provides per-instance custom icons and optional per-type group icons.
    /// </summary>
    public abstract class MID_BaseSO : ScriptableObject, IArrayElementTitle
    {
        [Header("Icon  (optional — overrides the default script icon in the Project window)")]
        [Tooltip("Drag any Texture2D here to use it as this asset's icon in the Project window.\n" +
                 "Leave null to use the group icon (if defined in code) or Unity's default.")]
        [SerializeField] private Texture2D _customIcon;

        // ── IArrayElementTitle ────────────────────────────────────────────────

        /// <summary>
        /// Display name shown in MID_NamedList inspector drawers.
        /// Defaults to the asset's name. Override in subclasses for custom labels.
        /// </summary>
        public virtual string Name => name;

        // ── Icon API ──────────────────────────────────────────────────────────

        /// <summary>
        /// The per-instance icon set in the inspector. Null if not assigned.
        /// </summary>
        public Texture2D CustomIcon => _customIcon;

        /// <summary>
        /// Override in a subclass to specify a project-relative path to a Texture2D
        /// that will be used as the icon for ALL assets of that type (group icon).
        /// Example: "Packages/com.midmanstudio.utilities/Editor/Icons/library_icon.png"
        /// Per-instance <see cref="CustomIcon"/> takes precedence over this.
        /// </summary>
        protected virtual string GroupIconPath => null;

        /// <summary>
        /// Resolves the effective icon: per-instance first, group second, null otherwise.
        /// Called by the custom editor to apply the icon to the asset.
        /// </summary>
        public Texture2D ResolveIcon()
        {
            if (_customIcon != null) return _customIcon;

#if UNITY_EDITOR
            if (!string.IsNullOrEmpty(GroupIconPath))
            {
                var groupIcon = UnityEditor.AssetDatabase
                    .LoadAssetAtPath<Texture2D>(GroupIconPath);
                if (groupIcon != null) return groupIcon;
            }
#endif
            return null;
        }
    }
}
