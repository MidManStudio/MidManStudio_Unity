// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio/enum-ids.md, section "MID_AudioEnumAttributes.cs"
// ============================================================================
using System;
using UnityEngine;

namespace MidManStudio.Core.Audio
{
    /// <summary>Which of the two generated audio enums a type or field stands for.</summary>
    public enum MID_AudioEnumKind
    {
        /// <summary>The audio id enum (one value per clip).</summary>
        Id = 0,
        /// <summary>The audio category enum (one value per category).</summary>
        Category = 1
    }

    /// <summary>
    /// Written on the enums that the Audio Type Generator produces. The inspector drawers and
    /// the audio manager use it to find the generated enum by purpose instead of by name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
    public sealed class MID_AudioEnumAttribute : Attribute
    {
        /// <summary>Whether the enum holds audio ids or audio categories.</summary>
        public MID_AudioEnumKind Kind { get; private set; }

        public MID_AudioEnumAttribute(MID_AudioEnumKind kind)
        {
            Kind = kind;
        }
    }

    /// <summary>
    /// Put on an <c>int</c> field to draw it as a popup of the generated audio id enum.
    /// The field keeps the enum's numeric value, so it serializes as a plain int.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class MID_AudioIdAttribute : PropertyAttribute
    {
    }

    /// <summary>
    /// Put on an <c>int</c> field to draw it as a popup of the generated audio category enum.
    /// The field keeps the enum's numeric value, so it serializes as a plain int.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class MID_AudioCategoryAttribute : PropertyAttribute
    {
    }
}
