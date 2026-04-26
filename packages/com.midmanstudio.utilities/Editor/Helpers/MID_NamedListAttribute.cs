using UnityEngine;
using System;

namespace MidManStudio.Core.Utilities
{
    /// <summary>
    /// Custom attribute for list elements with custom display names and colors in inspector
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
    public class MID_NamedListAttribute : PropertyAttribute
    {
        public string VarName { get; }
        public bool UseCustomColors { get; }
        public string ColorFieldName { get; }

        /// <summary>
        /// Create a named list attribute
        /// </summary>
        /// <param name="elementTitleVar">Field name to use as display name</param>
        /// <param name="useCustomColors">Enable per-element custom colors</param>
        /// <param name="colorFieldName">Field name containing the color (default: "color")</param>
        public MID_NamedListAttribute(
            string elementTitleVar = "",
            bool useCustomColors = false,
            string colorFieldName = "color")
        {
            VarName = elementTitleVar;
            UseCustomColors = useCustomColors;
            ColorFieldName = colorFieldName;
        }
    }

    /// <summary>
    /// Interface for elements that can provide their own display name
    /// </summary>
    public interface IArrayElementTitle
    {
        public string Name { get; }
    }

    /// <summary>
    /// Interface for elements that can provide their own color
    /// </summary>
    public interface IArrayElementColor
    {
        public Color ElementColor { get; }
    }
}