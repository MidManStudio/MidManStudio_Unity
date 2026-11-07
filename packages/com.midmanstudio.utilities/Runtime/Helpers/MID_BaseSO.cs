// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/helpers.md, section "MID_BaseSO.cs"
// ============================================================================
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core
{
    /// <summary>
    /// Base class for MidManStudio ScriptableObject assets. It adds an optional icon (one per asset, or one per
    /// type through <see cref="GroupIconPath"/>) that the Project window draws over the asset's default icon,
    /// and it implements <see cref="IArrayElementTitle"/> so lists of these assets show the asset name in
    /// <c>[MID_NamedList]</c> lists. The icon is drawn by Editor-only classes and does nothing at run time.
    ///
    /// Per-asset icon: select the asset and drag a Texture2D into the Icon field.
    /// Per-type icon: override <see cref="GroupIconPath"/> with a project path to a Texture2D.
    /// A type-level icon can also come from Unity's own <c>[Icon("path")]</c> attribute (Unity 2021.2 and later),
    /// which needs none of this class.
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
        /// Example: "Assets/Icons/library_icon.png"
        /// Per-instance <see cref="CustomIcon"/> takes precedence over this.
        /// </summary>
        public virtual string GroupIconPath => null;

        /// <summary>
        /// Returns the icon to show: the per-asset icon first, then the group icon, otherwise null.
        /// The group icon is loaded through the AssetDatabase, so it is only found in the Editor; in a build
        /// this returns the per-asset icon or null. Used by the Project window drawer, the inspector preview
        /// and the ScriptableObject Viewer.
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
