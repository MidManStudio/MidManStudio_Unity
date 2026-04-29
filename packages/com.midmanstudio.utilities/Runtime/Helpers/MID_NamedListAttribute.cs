// MID_NamedListAttribute.cs
// Runtime attribute — must be in Runtime assembly so it can be referenced
// by both Runtime code (LocalObjectPool, LibrarySO, etc.) and Editor drawers.
// NamedListDrawer.cs (the PropertyDrawer) stays in the Editor assembly.

using UnityEngine;
using System;

namespace MidManStudio.Core.EditorUtils
{
    /// <summary>
    /// Apply to a List field to show per-element display names in the Inspector
    /// instead of the default "Element 0, Element 1..." labels.
    /// The element type should implement <see cref="IArrayElementTitle"/> to
    /// provide its own display name, or supply a field name via elementTitleVar.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
    public class MID_NamedListAttribute : PropertyAttribute
    {
        public string VarName        { get; }
        public bool   UseCustomColors { get; }
        public string ColorFieldName  { get; }

        /// <param name="elementTitleVar">
        /// Name of the field on the element type to use as the display label.
        /// Leave empty to use <see cref="IArrayElementTitle.Name"/> instead.
        /// </param>
        /// <param name="useCustomColors">
        /// If true, each element is tinted using its colour field.
        /// </param>
        /// <param name="colorFieldName">
        /// Name of the Color field on the element type (default: "color").
        /// Only used when useCustomColors is true.
        /// </param>
        public MID_NamedListAttribute(
            string elementTitleVar  = "",
            bool   useCustomColors  = false,
            string colorFieldName   = "color")
        {
            VarName         = elementTitleVar;
            UseCustomColors = useCustomColors;
            ColorFieldName  = colorFieldName;
        }
    }

    /// <summary>
    /// Implement on list element types to control their display name
    /// in the MID_NamedList inspector drawer.
    /// </summary>
    public interface IArrayElementTitle
    {
        string Name { get; }
    }

    /// <summary>
    /// Implement on list element types to control their background tint colour
    /// in the MID_NamedList inspector drawer.
    /// </summary>
    public interface IArrayElementColor
    {
        Color ElementColor { get; }
    }
}
