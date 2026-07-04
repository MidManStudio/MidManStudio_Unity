// Options for MID_HierarchyArranger. Editor-only — nothing here is needed at runtime.

#if UNITY_EDITOR
using System;
using UnityEngine;

namespace MidManStudio.Core.EditorUtils.HierarchyArranger
{
    public enum MID_HierarchyArrangeMode
    {
        None,
        Alphabetical,
        AlphabeticalDescending,
        ByMainComponentType,
        BySimilarity,
        ByComponentCount,
        ByChildCount,
        ByActiveState,
        ByTag,
        ByLayer,
        ByNamePrefix
    }

    public enum MID_HierarchyGroupOrder
    {
        Alphabetical,
        LargestFirst,
        SmallestFirst
    }

    [Serializable]
    public class MID_HierarchySeparatorSettings
    {
        public bool   enabled      = false;
        [Tooltip("Repeated to build the separator's name — can be multiple characters, e.g. \"+_\".")]
        public string repeatUnit   = "-";
        [Tooltip("Clamped 1–100.")]
        public int    repeatCount  = 20;
        [Tooltip("Wrap the repeat pattern around a label, e.g. \"── Enemies (4) ──\" instead of a bare \"────────\".")]
        public bool   includeLabel = true;
    }

    [Serializable]
    public class MID_HierarchyArrangeOptions
    {
        public MID_HierarchyArrangeMode mode                = MID_HierarchyArrangeMode.Alphabetical;
        public bool                     recurseIntoChildren = false;
        public MID_HierarchyGroupOrder  groupOrder          = MID_HierarchyGroupOrder.Alphabetical;

        [Range(0f, 1f)]
        [Tooltip("Only used by BySimilarity — minimum MID_NameMatcher score to join a cluster.")]
        public float similarityThreshold = 0.5f;

        public MID_HierarchySeparatorSettings separators = new MID_HierarchySeparatorSettings();
    }
}
#endif
