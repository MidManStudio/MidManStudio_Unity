// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/hierarchyarranger.md, section "MID_HierarchyArrangeOptions.cs"
// ============================================================================
// Options for MID_HierarchyArranger. Editor-only — nothing here is needed at runtime.

#if UNITY_EDITOR
using System;
using UnityEngine;

namespace MidManStudio.Core.EditorUtils.HierarchyArranger
{
    /// <summary>How <c>MID_HierarchyArranger</c> groups a parent's children before sorting and reordering them.</summary>
    public enum MID_HierarchyArrangeMode
    {
        /// <summary>No grouping: every child is one flat group, sorted alphabetically.</summary>
        None,
        /// <summary>One flat group (or, with separators on, bucketed by first letter), A-Z.</summary>
        Alphabetical,
        /// <summary>Same as <see cref="Alphabetical"/>, reversed, Z-A.</summary>
        AlphabeticalDescending,
        /// <summary>Grouped by the name of each object's first non-engine component.</summary>
        ByMainComponentType,
        /// <summary>Clustered by name similarity (<c>MID_NameMatcher</c>), not a fixed key.</summary>
        BySimilarity,
        /// <summary>Grouped by total component count.</summary>
        ByComponentCount,
        /// <summary>Grouped by child count.</summary>
        ByChildCount,
        /// <summary>Grouped by active/inactive state.</summary>
        ByActiveState,
        /// <summary>Grouped by GameObject tag.</summary>
        ByTag,
        /// <summary>Grouped by GameObject layer.</summary>
        ByLayer,
        /// <summary>Grouped by name with any trailing number/counter stripped (e.g. "Enemy (3)" and "Enemy 4" group together).</summary>
        ByNamePrefix
    }

    /// <summary>Once children are grouped (see <see cref="MID_HierarchyArrangeMode"/>), how the groups themselves are ordered relative to each other.</summary>
    public enum MID_HierarchyGroupOrder
    {
        /// <summary>Groups ordered by their label/key, A-Z (numerically for count-based modes).</summary>
        Alphabetical,
        /// <summary>Groups with the most members first.</summary>
        LargestFirst,
        /// <summary>Groups with the fewest members first.</summary>
        SmallestFirst
    }

    /// <summary>Settings for the optional separator GameObjects inserted between groups.</summary>
    [Serializable]
    public class MID_HierarchySeparatorSettings
    {
        /// <summary>Insert separator GameObjects between groups.</summary>
        public bool   enabled      = false;
        [Tooltip("Repeated to build the separator's name — can be multiple characters, e.g. \"+_\".")]
        public string repeatUnit   = "-";
        [Tooltip("Clamped 1–100.")]
        public int    repeatCount  = 20;
        [Tooltip("Wrap the repeat pattern around a label, e.g. \"── Enemies (4) ──\" instead of a bare \"────────\".")]
        public bool   includeLabel = true;
    }

    /// <summary>Full option set passed to <c>MID_HierarchyArranger.Arrange</c>/<c>ArrangeMany</c>.</summary>
    [Serializable]
    public class MID_HierarchyArrangeOptions
    {
        /// <summary>How to group children before sorting.</summary>
        public MID_HierarchyArrangeMode mode                = MID_HierarchyArrangeMode.Alphabetical;
        /// <summary>Also arrange each group member's own children, recursively.</summary>
        public bool                     recurseIntoChildren = false;
        /// <summary>How the resulting groups are ordered relative to each other.</summary>
        public MID_HierarchyGroupOrder  groupOrder          = MID_HierarchyGroupOrder.Alphabetical;

        [Range(0f, 1f)]
        [Tooltip("Only used by BySimilarity — minimum MID_NameMatcher score to join a cluster.")]
        public float similarityThreshold = 0.5f;

        /// <summary>Separator GameObject settings, inserted between groups when <see cref="MID_HierarchySeparatorSettings.enabled"/> is true.</summary>
        public MID_HierarchySeparatorSettings separators = new MID_HierarchySeparatorSettings();
    }
}
#endif
