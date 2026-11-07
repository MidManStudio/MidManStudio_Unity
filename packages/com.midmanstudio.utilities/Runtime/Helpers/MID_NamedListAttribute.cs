// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/helpers.md, section "MID_NamedListAttribute.cs"
// ============================================================================
using UnityEngine;
using System;

namespace MidManStudio.Core.EditorUtils
{
    /// <summary>
    /// Apply to a List or array field to label each element in the Inspector by name instead of
    /// "Element 0", "Element 1" and so on. Unity applies the attribute to every element. The label comes from
    /// <see cref="IArrayElementTitle.Name"/> when the element type implements it, otherwise from the element
    /// field named by <c>elementTitleVar</c>, otherwise from Unity's default label. The drawer
    /// (<c>NamedListDrawer</c>) is Editor-only. The attribute lives in the Runtime assembly so runtime classes
    /// can use it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
    public class MID_NamedListAttribute : PropertyAttribute
    {
        /// <summary>
        /// Element field to use as the label. Empty means use <see cref="IArrayElementTitle"/>. Ignored when the
        /// element type implements <see cref="IArrayElementTitle"/>.
        /// </summary>
        public string VarName        { get; }

        /// <summary>True to tint each element's background and its left and top border with a colour.</summary>
        public bool   UseCustomColors { get; }

        /// <summary>
        /// Name of the Color field on the element, used when the element type does not implement
        /// <see cref="IArrayElementColor"/>. Only read when <see cref="UseCustomColors"/> is true.
        /// </summary>
        public string ColorFieldName  { get; }

        /// <param name="elementTitleVar">
        /// Name of the field on the element type to use as the display label.
        /// Leave empty to use <see cref="IArrayElementTitle.Name"/> instead.
        /// </param>
        /// <param name="useCustomColors">
        /// If true, each element is tinted using <see cref="IArrayElementColor"/> or its colour field.
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
        /// <summary>The label shown for this element. A null or blank name is shown as "Missing_" plus the element index.</summary>
        string Name { get; }
    }

    /// <summary>
    /// Implement on list element types to control their background tint colour
    /// in the MID_NamedList inspector drawer. Only read when the attribute is created with
    /// <c>useCustomColors: true</c>.
    /// </summary>
    public interface IArrayElementColor
    {
        /// <summary>The tint colour. <c>Color.clear</c> means no tint.</summary>
        Color ElementColor { get; }
    }
}
