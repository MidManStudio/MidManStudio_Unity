
// Runtime attribute — must be in Runtime assembly so it can be referenced
// by both Runtime code (ProjectileConfigSO, ProjectileVisual_2D, etc.) and
// Editor drawers. SortingLayerDrawer.cs (the PropertyDrawer) stays in the
// Editor assembly — mirrors MID_NamedListAttribute.cs's exact split for the
// same reason (a PropertyAttribute type must be visible to the Runtime
// fields it's applied to, but its drawer may only reference UnityEditor).

using UnityEngine;
using System;

namespace MidManStudio.Core.EditorUtils
{
    /// <summary>
    /// Apply to a <c>string</c> field to render it as a dropdown of the
    /// project's Sorting Layers (Project Settings → Tags and Layers) instead
    /// of a free-typed string. Stores the layer's <b>name</b> — sorting layer
    /// names are unique within a project, so this is safe to compare/serialize
    /// and reads cleanly in raw asset diffs.
    ///
    /// Falls back to "Default" if the stored name no longer matches any
    /// existing layer (e.g. the layer was renamed/deleted in the editor).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
    public class MID_SortingLayerAttribute : PropertyAttribute
    {
    }
}
