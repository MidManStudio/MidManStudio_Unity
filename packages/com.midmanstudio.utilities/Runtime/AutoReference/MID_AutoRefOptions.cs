// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/autoreference.md, section "MID_AutoRefOptions.cs"
// ============================================================================
using System;
using UnityEngine;

namespace MidManStudio.Core.AutoReference
{
    /// <summary>When a <see cref="MID_AutoRef"/> component resolves by itself.</summary>
    public enum MID_AutoRefRunMode
    {
        /// <summary>Only through the context menu, the inspector button, the editor window or a code call.</summary>
        Manual     = 0,
        /// <summary>In <c>Awake</c>, in Play Mode and in builds.</summary>
        Awake      = 1,
        /// <summary>In <c>Start</c>, in Play Mode and in builds.</summary>
        Start      = 2,
        /// <summary>Edit Mode only: resolves after the component is added or edited, no button needed.</summary>
        OnValidate = 3
    }
    /// <summary>
    /// Settings for <see cref="MID_AutoReferenceResolver"/>. Used by the <see cref="MID_AutoRef"/> component
    /// (one set per object) and by the bulk Auto Reference editor window (one set for all objects).
    /// </summary>
    [Serializable]
    public class MID_AutoRefOptions
    {
        [Header("Search Scope")]
        [Tooltip("Search this GameObject's children (recursively) for matching components.")]
        public bool includeChildren = true;

        [Tooltip("Include inactive children in the child search.")]
        public bool includeInactiveChildren = true;

        [Tooltip("Also search an external hierarchy — e.g. a detached Canvas not parented under this object.")]
        public bool includeExternalRoot = false;

        [Tooltip("Root to search when Include External Root is enabled.")]
        public Transform externalSearchRoot;

        [Header("Assignment")]
        [Tooltip("Overwrite fields that already have a value. Off by default — a re-run never clobbers manual edits.")]
        public bool overwriteExisting = false;

        [Header("Runtime")]
        [Tooltip("When to auto-resolve. Manual = only via ContextMenu / editor window / explicit code call. OnValidate = edit-time auto-resolve, no button needed.")]
        public MID_AutoRefRunMode runMode = MID_AutoRefRunMode.Manual;

        [Header("Logging")]
        [Tooltip("Log a warning for fields with zero matching candidates.")]
        public bool logUnresolved = true;

        [Tooltip("Log a line whenever an ambiguous field (2+ candidates) was resolved by name match.")]
        public bool logAmbiguousResolved = true;
    }
}
