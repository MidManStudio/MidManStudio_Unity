// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio/enum-ids.md, section "MID_AudioEnumLookup.cs"
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Core.Audio
{
    /// <summary>
    /// Editor only. Finds the generated audio enums (the ones carrying
    /// <see cref="MID_AudioEnumAttribute"/>) and lists their names and values.
    /// Results are cached until the next domain reload, which is when a regenerated enum appears.
    /// </summary>
    public static class MID_AudioEnumLookup
    {
        private static readonly Type[] s_Types = new Type[2];
        private static readonly string[][] s_Names = new string[2][];
        private static readonly int[][] s_Values = new int[2][];
        private static readonly bool[] s_Resolved = new bool[2];

        /// <summary>The generated enum of the given kind, or null when none has been generated yet.</summary>
        public static Type GetEnumType(MID_AudioEnumKind kind)
        {
            Resolve(kind);
            return s_Types[(int)kind];
        }

        /// <summary>Member names of the generated enum, ordered by value. Empty when it does not exist.</summary>
        public static string[] GetNames(MID_AudioEnumKind kind)
        {
            Resolve(kind);
            return s_Names[(int)kind];
        }

        /// <summary>Member values of the generated enum, in the same order as <see cref="GetNames"/>.</summary>
        public static int[] GetValues(MID_AudioEnumKind kind)
        {
            Resolve(kind);
            return s_Values[(int)kind];
        }

        /// <summary>Looks up the member name for a numeric value.</summary>
        public static bool TryGetName(MID_AudioEnumKind kind, int value, out string name)
        {
            Resolve(kind);
            string[] names = s_Names[(int)kind];
            int[] values = s_Values[(int)kind];
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] != value) continue;
                name = names[i];
                return true;
            }
            name = null;
            return false;
        }

        private static void Resolve(MID_AudioEnumKind kind)
        {
            int k = (int)kind;
            if (s_Resolved[k]) return;
            s_Resolved[k] = true;

            Type found = null;
            var candidates = new List<Type>();
            foreach (Type t in TypeCache.GetTypesWithAttribute<MID_AudioEnumAttribute>())
            {
                if (!t.IsEnum) continue;
                var attr = (MID_AudioEnumAttribute)Attribute.GetCustomAttribute(t, typeof(MID_AudioEnumAttribute), false);
                if (attr != null && attr.Kind == kind) candidates.Add(t);
            }

            if (candidates.Count > 0)
            {
                candidates.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
                found = candidates[0];
                if (candidates.Count > 1)
                    Debug.LogWarning("[MID_AudioEnumLookup] More than one generated audio enum of kind " + kind +
                                     " exists. Using " + found.FullName + ".");
            }

            s_Types[k] = found;
            if (found == null)
            {
                s_Names[k] = new string[0];
                s_Values[k] = new int[0];
                return;
            }

            Array raw = Enum.GetValues(found);
            var pairs = new List<KeyValuePair<int, string>>(raw.Length);
            foreach (object o in raw)
                pairs.Add(new KeyValuePair<int, string>(Convert.ToInt32(o), Enum.GetName(found, o)));
            pairs.Sort((a, b) => a.Key.CompareTo(b.Key));

            s_Names[k] = new string[pairs.Count];
            s_Values[k] = new int[pairs.Count];
            for (int i = 0; i < pairs.Count; i++)
            {
                s_Values[k][i] = pairs[i].Key;
                s_Names[k][i] = pairs[i].Value;
            }
        }
    }
}
#endif
