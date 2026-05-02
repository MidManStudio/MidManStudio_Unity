// MID_UIStateContextEditor.cs
// Shared custom drawer logic used by both the Visibility and Button inspectors.
// Reflects the generated enum type from the context SO's enumTypeName field
// and draws named checkboxes / dropdown instead of a raw int.

#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.UIState;

namespace MidManStudio.Core.Editor.UIState
{
    // ── Visibility Inspector ──────────────────────────────────────────────────

    [CustomEditor(typeof(MID_UIStateVisibility))]
    public class MID_UIStateVisibilityEditor : UnityEditor.Editor
    {
        private SerializedProperty _contextProp;
        private SerializedProperty _maskProp;
        private SerializedProperty _logLevelProp;

        private void OnEnable()
        {
            _contextProp  = serializedObject.FindProperty("_context");
            _maskProp     = serializedObject.FindProperty("_showWhenMask");
            _logLevelProp = serializedObject.FindProperty("_logLevel");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("UI State Visibility", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(_contextProp, new GUIContent("Context"));

            var contextSO = _contextProp.objectReferenceValue as MID_UIStateContext;
            if (contextSO == null)
            {
                EditorGUILayout.HelpBox("Assign a MID_UIStateContext SO asset.", MessageType.Warning);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                $"Context: {contextSO.contextDisplayName}\n" +
                "Panel is visible when state contains ANY checked flag.",
                MessageType.Info);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Show When:", EditorStyles.boldLabel);

            UIStateContextEditorUtils.DrawFlagsCheckboxes(contextSO.enumTypeName, _maskProp);

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(_logLevelProp);

            serializedObject.ApplyModifiedProperties();
        }
    }

    // ── Button Inspector ──────────────────────────────────────────────────────

    [CustomEditor(typeof(MID_UIStateButton))]
    public class MID_UIStateButtonEditor : UnityEditor.Editor
    {
        private SerializedProperty _contextProp;
        private SerializedProperty _maskProp;
        private SerializedProperty _disableProp;
        private SerializedProperty _logLevelProp;

        private void OnEnable()
        {
            _contextProp  = serializedObject.FindProperty("_context");
            _maskProp     = serializedObject.FindProperty("_targetStateMask");
            _disableProp  = serializedObject.FindProperty("_disableWhenActive");
            _logLevelProp = serializedObject.FindProperty("_logLevel");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("UI State Button", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(_contextProp, new GUIContent("Context"));

            var contextSO = _contextProp.objectReferenceValue as MID_UIStateContext;
            if (contextSO == null)
            {
                EditorGUILayout.HelpBox("Assign a MID_UIStateContext SO asset.", MessageType.Warning);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Target State:", EditorStyles.boldLabel);

            // Buttons transition to a single state — use a dropdown not checkboxes
            UIStateContextEditorUtils.DrawSingleStateDropdown(
                contextSO.enumTypeName, _maskProp);

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(_disableProp, new GUIContent("Disable When Active"));
            EditorGUILayout.PropertyField(_logLevelProp);

            serializedObject.ApplyModifiedProperties();
        }
    }

    // ── Shared Utils ──────────────────────────────────────────────────────────

    public static class UIStateContextEditorUtils
    {
        /// <summary>
        /// Draw multi-select flag checkboxes by reflecting the type at enumTypeName.
        /// Falls back to an int field if the type is not found (before first generation).
        /// </summary>
        public static void DrawFlagsCheckboxes(string enumTypeName, SerializedProperty maskProp)
        {
            var enumType = FindEnumType(enumTypeName);
            if (enumType == null)
            {
                EditorGUILayout.HelpBox(
                    $"Enum type '{enumTypeName}' not found.\n" +
                    "Run the UI State Context Generator first.",
                    MessageType.Warning);
                EditorGUILayout.PropertyField(maskProp, new GUIContent("Show When (raw int)"));
                return;
            }

            var (names, values) = GetEnumNamesValues(enumType);
            int current = maskProp.intValue;
            int next    = current;

            foreach (var (name, value) in ZipSkipNone(names, values))
            {
                bool isSet    = (current & value) != 0;
                bool newIsSet = EditorGUILayout.ToggleLeft(name, isSet);
                if (newIsSet != isSet)
                {
                    if (newIsSet) next |=  value;
                    else          next &= ~value;
                }
            }

            if (next != current) maskProp.intValue = next;

            DrawSummary(next, names, values);
        }

        /// <summary>
        /// Draw a single-state dropdown (for buttons that transition to one state).
        /// </summary>
        public static void DrawSingleStateDropdown(string enumTypeName, SerializedProperty maskProp)
        {
            var enumType = FindEnumType(enumTypeName);
            if (enumType == null)
            {
                EditorGUILayout.HelpBox(
                    $"Enum type '{enumTypeName}' not found.\n" +
                    "Run the UI State Context Generator first.",
                    MessageType.Warning);
                EditorGUILayout.PropertyField(maskProp, new GUIContent("Target State (raw int)"));
                return;
            }

            var (names, values) = GetEnumNamesValues(enumType);

            // Build popup options (skip None)
            var options     = new List<string>();
            var optionVals  = new List<int>();
            foreach (var (name, value) in ZipSkipNone(names, values))
            {
                // Only include single-bit values for button targets
                if ((value & (value - 1)) == 0)
                {
                    options.Add(name);
                    optionVals.Add(value);
                }
            }

            int current = maskProp.intValue;
            int idx     = optionVals.IndexOf(current);
            if (idx < 0) idx = 0;

            int newIdx = EditorGUILayout.Popup("State", idx, options.ToArray());
            if (newIdx >= 0 && newIdx < optionVals.Count)
                maskProp.intValue = optionVals[newIdx];
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        private static Type FindEnumType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            // Search all loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(typeName);
                if (t != null && t.IsEnum) return t;
            }
            return null;
        }

        private static (string[] names, int[] values) GetEnumNamesValues(Type enumType)
        {
            var names  = Enum.GetNames(enumType);
            var raw    = Enum.GetValues(enumType);
            var values = new int[raw.Length];
            for (int i = 0; i < raw.Length; i++) values[i] = (int)raw.GetValue(i);
            return (names, values);
        }

        private static IEnumerable<(string name, int value)> ZipSkipNone(
            string[] names, int[] values)
        {
            for (int i = 0; i < names.Length; i++)
                if (values[i] != 0) yield return (names[i], values[i]);
        }

        private static void DrawSummary(int mask, string[] names, int[] values)
        {
            EditorGUILayout.Space(2);
            if (mask == 0)
            {
                EditorGUILayout.HelpBox("No states selected — element will never be shown.",
                    MessageType.Warning);
            }
            else
            {
                var selected = new List<string>();
                for (int i = 0; i < names.Length; i++)
                    if (values[i] != 0 && (mask & values[i]) != 0)
                        selected.Add(names[i]);
                EditorGUILayout.HelpBox($"Visible in: {string.Join(", ", selected)}",
                    MessageType.None);
            }
        }
    }
}
#endif
