
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using MidManStudio.Core.Logging;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MidManStudio.Core.AutoReference
{
    public enum MID_AutoRefOutcome
    {
        Assigned,
        SkippedAlreadySet,
        NoCandidates,
        AmbiguousResolved
    }

    public readonly struct MID_AutoRefFieldResult
    {
        public readonly string ScriptTypeName;
        public readonly string FieldName;
        public readonly string FieldTypeName;
        public readonly MID_AutoRefOutcome Outcome;
        public readonly int CandidateCount;
        public readonly float MatchScore;
        public readonly string AssignedObjectName;

        public MID_AutoRefFieldResult(string scriptTypeName, string fieldName, string fieldTypeName,
            MID_AutoRefOutcome outcome, int candidateCount, float matchScore, string assignedObjectName)
        {
            ScriptTypeName     = scriptTypeName;
            FieldName          = fieldName;
            FieldTypeName      = fieldTypeName;
            Outcome            = outcome;
            CandidateCount     = candidateCount;
            MatchScore         = matchScore;
            AssignedObjectName = assignedObjectName;
        }
    }
    /// <summary>
    ///  Core auto-reference resolver. Scans a GameObject's [MID_AutoRefable] MonoBehaviours,
    /// reflects over their assignable reference fields (Component / GameObject / interface),
    /// and auto-assigns the best candidate found on self, children, and optionally an
    /// external search root. No per-field attribute required — opt a field out with
    /// [MID_NoAutoRef]. Editor-only concerns (Undo, dirtying, logging) are wrapped in
    /// #if UNITY_EDITOR so this stays in the runtime assembly and still supports proper
    /// undo when called from the editor — same pattern MID_Logger.cs already uses.
    /// </summary>
    public static class MID_AutoReferenceResolver
    {
        private const BindingFlags FieldFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        /// <summary>Resolves references for every [MID_AutoRefable] MonoBehaviour on <paramref name="target"/>. Safe in edit or play mode.</summary>
        public static List<MID_AutoRefFieldResult> Resolve(GameObject target, MID_AutoRefOptions options)
        {
            var results = new List<MID_AutoRefFieldResult>();
            if (target == null || options == null) return results;

            foreach (var behaviour in target.GetComponents<MonoBehaviour>())
            {
                if (behaviour == null) continue;               // missing script
                if (behaviour is MID_AutoRef) continue;         // never scan the resolver's own component
                var type = behaviour.GetType();
                if (!IsAutoRefable(type)) continue;

                ResolveBehaviour(behaviour, type, options, results);
            }
            return results;
        }

        /// <summary>True if the type (or a base type) carries [MID_AutoRefable].</summary>
        public static bool IsAutoRefable(Type type)
        {
            return type.GetCustomAttribute<MID_AutoRefableAttribute>(inherit: true) != null;
        }

        private static void ResolveBehaviour(MonoBehaviour behaviour, Type type,
            MID_AutoRefOptions options, List<MID_AutoRefFieldResult> results)
        {
            foreach (var field in GetEligibleFields(type))
            {
                var current = field.GetValue(behaviour) as UnityEngine.Object;
                if (current != null && !options.overwriteExisting)
                {
                    results.Add(new MID_AutoRefFieldResult(type.Name, field.Name, field.FieldType.Name,
                        MID_AutoRefOutcome.SkippedAlreadySet, 0, 0f, current.name));
                    continue;
                }

                var candidates = CollectCandidates(behaviour.gameObject, field.FieldType, options);

                if (candidates.Count == 0)
                {
                    if (options.logUnresolved)
                        LogWarn($"No match for '{field.Name}' ({field.FieldType.Name}) on {type.Name} (GameObject '{behaviour.gameObject.name}').");

                    results.Add(new MID_AutoRefFieldResult(type.Name, field.Name, field.FieldType.Name,
                        MID_AutoRefOutcome.NoCandidates, 0, 0f, null));
                    continue;
                }

                UnityEngine.Object chosen;
                float score;
                var outcome = MID_AutoRefOutcome.Assigned;

                if (candidates.Count == 1)
                {
                    chosen = candidates[0];
                    score  = 1f;
                }
                else
                {
                    (chosen, score) = PickBestMatch(field.Name, candidates);
                    outcome = MID_AutoRefOutcome.AmbiguousResolved;
                    if (options.logAmbiguousResolved)
                        LogInfo($"'{field.Name}' on {type.Name} had {candidates.Count} candidates — picked '{OwnerName(chosen)}' (score {score:F2}).");
                }

                AssignField(behaviour, field, chosen);

                results.Add(new MID_AutoRefFieldResult(type.Name, field.Name, field.FieldType.Name,
                    outcome, candidates.Count, score, OwnerName(chosen)));
            }
        }

        private static IEnumerable<FieldInfo> GetEligibleFields(Type type)
        {
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                foreach (var field in t.GetFields(FieldFlags))
                {
                    if (IsEligibleField(field)) yield return field;
                }
            }
        }

        private static bool IsEligibleField(FieldInfo field)
        {
            if (field.IsStatic || field.IsInitOnly || field.IsLiteral) return false;
            if (field.IsDefined(typeof(MID_NoAutoRefAttribute), true)) return false;
            if (field.IsDefined(typeof(NonSerializedAttribute), true)) return false;
            if (!field.IsPublic && field.GetCustomAttribute<SerializeField>() == null) return false;

            var t = field.FieldType;
            return typeof(Component).IsAssignableFrom(t) || t == typeof(GameObject) || t.IsInterface;
        }

        private static List<UnityEngine.Object> CollectCandidates(GameObject self, Type fieldType, MID_AutoRefOptions options)
        {
            var seen    = new HashSet<int>();
            var ordered = new List<UnityEngine.Object>();
            bool wantsGameObject = fieldType == typeof(GameObject);

            void TryAdd(UnityEngine.Object obj)
            {
                if (obj == null) return;
                if (seen.Add(obj.GetInstanceID())) ordered.Add(obj);
            }

            void ScanRoot(Transform root, bool includeInactive)
            {
                if (wantsGameObject)
                {
                    foreach (var t in root.GetComponentsInChildren<Transform>(includeInactive))
                        TryAdd(t.gameObject);
                }
                else
                {
                    foreach (var c in root.GetComponentsInChildren(fieldType, includeInactive))
                        TryAdd(c);
                }
            }

            // GetComponentsInChildren always visits self first (depth-first) — this is what
            // gives us "first found wins" ordering for tie-break in PickBestMatch.
            if (options.includeChildren)
            {
                ScanRoot(self.transform, options.includeInactiveChildren);
            }
            else if (wantsGameObject)
            {
                TryAdd(self);
            }
            else
            {
                foreach (var c in self.GetComponents(fieldType)) TryAdd(c);
            }

            if (options.includeExternalRoot && options.externalSearchRoot != null)
                ScanRoot(options.externalSearchRoot, true);

            return ordered;
        }

        private static (UnityEngine.Object chosen, float score) PickBestMatch(string fieldName, List<UnityEngine.Object> candidates)
        {
            UnityEngine.Object best = candidates[0];
            float bestScore = -1f;

            foreach (var candidate in candidates)
            {
                float score = MID_NameMatcher.Score(fieldName, OwnerName(candidate));
                if (score > bestScore) // strictly greater -> ties keep the first-found candidate
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return (best, bestScore);
        }

        private static string OwnerName(UnityEngine.Object obj) => obj is Component c ? c.gameObject.name : obj.name;

        private static void AssignField(MonoBehaviour behaviour, FieldInfo field, UnityEngine.Object value)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) Undo.RecordObject(behaviour, "Auto-Assign References");
#endif
            field.SetValue(behaviour, value);
#if UNITY_EDITOR
            if (!Application.isPlaying) EditorUtility.SetDirty(behaviour);
#endif
        }

        // Avoids waking MID_Logger's auto-instantiating singleton during edit-mode scans —
        // same reason SceneDependencyInjector guards on Application.isPlaying before logging.
        private static void LogWarn(string msg)
        {
            if (Application.isPlaying) MID_Logger.LogWarning(MID_LogLevel.Info, msg, nameof(MID_AutoReferenceResolver));
            else Debug.LogWarning($"[AutoRef] {msg}");
        }

        private static void LogInfo(string msg)
        {
            if (Application.isPlaying) MID_Logger.LogInfo(MID_LogLevel.Info, msg, nameof(MID_AutoReferenceResolver));
            else Debug.Log($"[AutoRef] {msg}");
        }
    }
}
