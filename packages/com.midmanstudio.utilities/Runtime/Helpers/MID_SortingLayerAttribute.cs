// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/helpers.md, section "MID_SortingLayerAttribute.cs"
// ============================================================================
using UnityEngine;
using System;

namespace MidManStudio.Core.EditorUtils
{
    /// <summary>
    /// Apply to a <c>string</c> field to show it as a dropdown of the project's Sorting Layers
    /// (Project Settings, Tags and Layers) instead of a free-typed string. The layer's <b>name</b> is stored.
    /// Sorting layer names are unique within a project, so the value is safe to compare and reads cleanly in
    /// asset diffs, but renaming a layer leaves the old name in existing assets.
    ///
    /// If the stored name matches no layer (renamed or deleted), the dropdown shows the first layer in the list,
    /// but the stored string is not changed until a layer is picked. The drawer (<c>SortingLayerDrawer</c>) is
    /// Editor-only. The attribute lives in the Runtime assembly so runtime classes can use it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
    public class MID_SortingLayerAttribute : PropertyAttribute
    {
    }
}
