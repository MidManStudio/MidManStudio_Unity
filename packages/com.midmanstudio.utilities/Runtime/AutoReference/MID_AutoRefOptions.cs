// Shared settings for MID_AutoReferenceResolver — used by both the runtime MID_AutoRef
// component (per-object) and the bulk Editor/AutoReference/MID_AutoReferenceWindow.

using System;
using UnityEngine;

namespace MidManStudio.Core.AutoReference
{
    public enum MID_AutoRefRunMode
    {
        Manual     = 0,
        Awake      = 1,
        Start      = 2,
        OnValidate = 3 // Edit-time only — resolves automatically after being added or edited, no button needed.
    }

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
