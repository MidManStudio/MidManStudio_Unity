// Core hierarchy-arranging logic — static, reusable from the window or your own
// editor scripts. Groups a parent's children per MID_HierarchyArrangeOptions,
// sorts within each group alphabetically, orders the groups themselves, and
// inserts separator GameObjects between them. One Undo step covers the whole
// operation, including any recursion.
//
// Reordering uses SetAsLastSibling() applied in the desired final sequence,
// NOT SetSiblingIndex(cursor++). Interleaving object creation with an
// incrementing SetSiblingIndex is a known Unity gotcha — a freshly created
// GameObject doesn't reliably honor an explicit SetSiblingIndex call in the
// same pass, so it just stays wherever `new GameObject()` put it (the end).
// SetAsLastSibling(), called once per item in exact target order, is the
// standard bulletproof fix: each call unconditionally moves that object to
// the end at that moment, so sequential calls always yield the correct order.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MidManStudio.Core.HierarchyArranger;
using MidManStudio.Core.AutoReference; // reusing MID_NameMatcher for BySimilarity

namespace MidManStudio.Core.EditorUtils.HierarchyArranger
{
    public static class MID_HierarchyArranger
    {
        private const int MaxSeparatorRepeat = 100;

        private static readonly Regex TrailingNumberPattern =
            new Regex(@"^(?<prefix>.*?)[\s_\-]*\(?\d+\)?$", RegexOptions.Compiled);

        /// <summary>Arranges everything under <paramref name="root"/> in one Undo step. Returns objects processed.</summary>
        public static int Arrange(Transform root, MID_HierarchyArrangeOptions options)
        {
            Undo.RegisterFullObjectHierarchyUndo(root.gameObject, "Arrange Hierarchy");

            int processed = ArrangeRecursive(root, options);

            EditorUtility.SetDirty(root.gameObject);
            if (PrefabStageUtility.GetCurrentPrefabStage() == null)
                EditorSceneManager.MarkSceneDirty(root.gameObject.scene);

            return processed;
        }

        public static int ArrangeMany(IEnumerable<Transform> roots, MID_HierarchyArrangeOptions options)
        {
            int total = 0;
            foreach (var root in roots) total += Arrange(root, options);
            return total;
        }

        private static int ArrangeRecursive(Transform parent, MID_HierarchyArrangeOptions options)
        {
            // 1) Strip separators from a previous run before doing anything else.
            var stale = new List<GameObject>();
            var realChildren = new List<Transform>();
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.GetComponent<MID_HierarchySeparatorMarker>() != null)
                    stale.Add(child.gameObject);
                else
                    realChildren.Add(child);
            }
            foreach (var go in stale) Undo.DestroyObjectImmediate(go);

            int processed = realChildren.Count;
            if (realChildren.Count == 0) return processed;

            // 2) Build groups.
            List<(string Label, List<Transform> Members)> groups = options.mode switch
            {
                MID_HierarchyArrangeMode.None        => new List<(string, List<Transform>)> { (string.Empty, realChildren) },
                MID_HierarchyArrangeMode.BySimilarity => ClusterBySimilarity(realChildren, options.similarityThreshold),
                _                                     => GroupByKey(realChildren, options)
            };

            // 3) Sort within each group — always alphabetical; AlphabeticalDescending reverses it.
            bool descending = options.mode == MID_HierarchyArrangeMode.AlphabeticalDescending;
            foreach (var g in groups)
            {
                g.Members.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
                if (descending) g.Members.Reverse();
            }

            // 4) Order the groups themselves.
            groups = OrderGroups(groups, options);

            // 5) Apply final order. SetAsLastSibling() in exact target sequence —
            //    see file header for why this replaced SetSiblingIndex(cursor++).
            for (int g = 0; g < groups.Count; g++)
            {
                if (g > 0 && options.separators.enabled)
                {
                    var sep = CreateSeparator(parent, groups[g].Label, groups[g].Members.Count, options.separators);
                    sep.transform.SetAsLastSibling();
                }

                foreach (var child in groups[g].Members)
                {
                    child.SetAsLastSibling();
                    if (options.recurseIntoChildren && child.childCount > 0)
                        processed += ArrangeRecursive(child, options);
                }
            }

            return processed;
        }

        private static List<(string Label, List<Transform> Members)> GroupByKey(
            List<Transform> children, MID_HierarchyArrangeOptions options)
        {
            var byKey = new Dictionary<string, List<Transform>>();
            foreach (var child in children)
            {
                string key = GetGroupKey(child, options);
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<Transform>();
                list.Add(child);
            }
            return byKey.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        private static string GetGroupKey(Transform t, MID_HierarchyArrangeOptions options)
        {
            switch (options.mode)
            {
                case MID_HierarchyArrangeMode.Alphabetical:
                case MID_HierarchyArrangeMode.AlphabeticalDescending:
                    // Flat sort (one implicit group) unless separators are on — in
                    // which case bucket by first letter so there's something to
                    // actually separate between.
                    return options.separators.enabled ? GetFirstLetterKey(t.name) : string.Empty;

                case MID_HierarchyArrangeMode.ByMainComponentType:
                    return GetMainComponentTypeName(t.gameObject);

                case MID_HierarchyArrangeMode.ByComponentCount:
                    return t.GetComponents<Component>().Length.ToString();

                case MID_HierarchyArrangeMode.ByChildCount:
                    return t.childCount.ToString();

                case MID_HierarchyArrangeMode.ByActiveState:
                    return t.gameObject.activeSelf ? "Active" : "Inactive";

                case MID_HierarchyArrangeMode.ByTag:
                    return t.gameObject.tag;

                case MID_HierarchyArrangeMode.ByLayer:
                    return LayerMask.LayerToName(t.gameObject.layer);

                case MID_HierarchyArrangeMode.ByNamePrefix:
                    return ExtractNamePrefix(t.name);

                default:
                    return string.Empty;
            }
        }

        private static string GetFirstLetterKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "#";
            char c = char.ToUpperInvariant(name.TrimStart()[0]);
            return char.IsLetter(c) ? c.ToString() : "#";
        }

        private static string GetMainComponentTypeName(GameObject go)
        {
            var components = go.GetComponents<Component>();

            // Prefer the first custom script — usually what "main component" means
            // for a gameplay object — over generic engine types on the same object.
            foreach (var c in components)
            {
                if (c == null || c is Transform) continue;
                string ns = c.GetType().Namespace ?? string.Empty;
                bool isEngineType = ns.StartsWith("UnityEngine") || ns.StartsWith("Unity.") || ns.StartsWith("TMPro");
                if (!isEngineType) return c.GetType().Name;
            }

            foreach (var c in components)
            {
                if (c == null || c is Transform) continue;
                return c.GetType().Name;
            }

            return "(No Components)";
        }

        private static string ExtractNamePrefix(string name)
        {
            var match = TrailingNumberPattern.Match(name);
            if (!match.Success) return name;
            string prefix = match.Groups["prefix"].Value.TrimEnd();
            return string.IsNullOrEmpty(prefix) ? name : prefix;
        }

        private static List<(string Label, List<Transform> Members)> ClusterBySimilarity(
            List<Transform> items, float threshold)
        {
            var remaining = new List<Transform>(items);
            var clusters  = new List<(string, List<Transform>)>();

            while (remaining.Count > 0)
            {
                var seed = remaining[0];
                remaining.RemoveAt(0);
                var cluster = new List<Transform> { seed };

                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    float score = MID_NameMatcher.Score(seed.name, remaining[i].name);
                    if (score >= threshold)
                    {
                        cluster.Add(remaining[i]);
                        remaining.RemoveAt(i);
                    }
                }

                clusters.Add((seed.name, cluster));
            }

            return clusters;
        }

        private static List<(string Label, List<Transform> Members)> OrderGroups(
            List<(string Label, List<Transform> Members)> groups, MID_HierarchyArrangeOptions options)
        {
            bool numericKey = options.mode == MID_HierarchyArrangeMode.ByComponentCount
                            || options.mode == MID_HierarchyArrangeMode.ByChildCount;

            List<(string Label, List<Transform> Members)> ordered = options.groupOrder switch
            {
                MID_HierarchyGroupOrder.LargestFirst  => groups.OrderByDescending(g => g.Members.Count).ToList(),
                MID_HierarchyGroupOrder.SmallestFirst => groups.OrderBy(g => g.Members.Count).ToList(),
                _ => numericKey
                    ? groups.OrderBy(g => int.TryParse(g.Label, out int n) ? n : 0).ToList()
                    : groups.OrderBy(g => g.Label, StringComparer.OrdinalIgnoreCase).ToList()
            };

            // Descending alphabetical + letter categories: flip the group order too
            // (Z-group first) — member order within each group is already reversed
            // in step 3. Only auto-applies if the user hasn't explicitly chosen a
            // different group-order strategy.
            if (options.mode == MID_HierarchyArrangeMode.AlphabeticalDescending
                && options.groupOrder == MID_HierarchyGroupOrder.Alphabetical)
                ordered.Reverse();

            return ordered;
        }

        private static GameObject CreateSeparator(
            Transform parent, string groupLabel, int groupSize, MID_HierarchySeparatorSettings settings)
        {
            string pattern = BuildRepeatPattern(settings.repeatUnit, settings.repeatCount);
            string name = settings.includeLabel && !string.IsNullOrEmpty(groupLabel)
                ? $"{pattern} {groupLabel} ({groupSize}) {pattern}"
                : pattern;

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MID_HierarchySeparatorMarker>();
            Undo.RegisterCreatedObjectUndo(go, "Arrange Hierarchy");
            return go;
        }

        private static string BuildRepeatPattern(string unit, int count)
        {
            if (string.IsNullOrEmpty(unit)) unit = "-";
            count = Mathf.Clamp(count, 1, MaxSeparatorRepeat);
            var sb = new StringBuilder(unit.Length * count);
            for (int i = 0; i < count; i++) sb.Append(unit);
            return sb.ToString();
        }
    }
}
#endif
