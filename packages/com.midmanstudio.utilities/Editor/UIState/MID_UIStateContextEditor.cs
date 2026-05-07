// MID_UIStateContextEditor.cs
// Custom inspectors for MID_UIStateVisibility, MID_UIStateButton,
// MID_UIStateManager, and MID_UIStateBackButton.
//
// FIXES in this version:
//   - [CanEditMultipleObjects] added to all editors (fixes "multiple edit" warning)
//   - DrawPanelConfigRow uses EditorGUILayout.Foldout instead of nested
//     BeginFoldoutHeaderGroup (fixes "You can't nest Foldout Headers" error)
//   - All references updated for merged MID_UIStateContext SO

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.UIState;

namespace MidManStudio.Core.Editor.UIState
{
    // ── Visibility Inspector ──────────────────────────────────────────────────

    [CustomEditor(typeof(MID_UIStateVisibility))]
    [CanEditMultipleObjects]
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
                EditorGUILayout.HelpBox(
                    "Assign a MID_UIStateContext SO asset.", MessageType.Warning);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                $"Context: {contextSO.contextName}\n" +
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

    // ── Back Button Inspector ─────────────────────────────────────────────────

    [CustomEditor(typeof(MID_UIStateBackButton))]
    [CanEditMultipleObjects]
    public class MID_UIStateBackButtonEditor : UnityEditor.Editor
    {
        private SerializedProperty _contextProp;
        private SerializedProperty _disableProp;
        private SerializedProperty _logLevelProp;

        private void OnEnable()
        {
            _contextProp  = serializedObject.FindProperty("_context");
            _disableProp  = serializedObject.FindProperty("_disableWhenNoHistory");
            _logLevelProp = serializedObject.FindProperty("_logLevel");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("UI State Back Button", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_contextProp, new GUIContent("Context"));

            var contextSO = _contextProp.objectReferenceValue as MID_UIStateContext;
            if (contextSO != null)
            {
                EditorGUILayout.HelpBox(
                    $"Context: {contextSO.contextName}\n" +
                    "Calls GoBack() on the context when clicked.",
                    MessageType.None);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Assign a MID_UIStateContext SO asset.", MessageType.Warning);
            }

            EditorGUILayout.PropertyField(_disableProp,
                new GUIContent("Disable When No History",
                    "Disables the button automatically when there is no state to return to."));
            EditorGUILayout.PropertyField(_logLevelProp);
            serializedObject.ApplyModifiedProperties();
        }
    }

    // ── State Button Inspector ────────────────────────────────────────────────

    [CustomEditor(typeof(MID_UIStateButton))]
    [CanEditMultipleObjects]
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
                EditorGUILayout.HelpBox(
                    "Assign a MID_UIStateContext SO asset.", MessageType.Warning);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Target State:", EditorStyles.boldLabel);
            UIStateContextEditorUtils.DrawSingleStateDropdown(contextSO.enumTypeName, _maskProp);

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(_disableProp,
                new GUIContent("Disable When Active"));
            EditorGUILayout.PropertyField(_logLevelProp);
            serializedObject.ApplyModifiedProperties();
        }
    }

    // ── UIStateManager Inspector ──────────────────────────────────────────────

    [CustomEditor(typeof(MID_UIStateManager))]
    public class MID_UIStateManagerEditor : UnityEditor.Editor
    {
        private SerializedProperty _contextProp;
        private SerializedProperty _initialStateProp;
        private SerializedProperty _configsProp;
        private SerializedProperty _logLevelProp;
        private bool _configsFold = true;

        private void OnEnable()
        {
            _contextProp      = serializedObject.FindProperty("_context");
            _initialStateProp = serializedObject.FindProperty("_initialState");
            _configsProp      = serializedObject.FindProperty("_configurations");
            _logLevelProp     = serializedObject.FindProperty("_logLevel");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("UI State Manager", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(_contextProp, new GUIContent("Context"));

            var contextSO = _contextProp.objectReferenceValue as MID_UIStateContext;

            EditorGUILayout.Space(4);
            if (contextSO != null)
            {
                EditorGUILayout.LabelField("Initial State:", EditorStyles.boldLabel);
                UIStateContextEditorUtils.DrawSingleStateDropdown(
                    contextSO.enumTypeName, _initialStateProp, includeNone: true);
            }
            else
            {
                EditorGUILayout.PropertyField(_initialStateProp,
                    new GUIContent("Initial State (raw int)"));
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(_logLevelProp);

            // Panel Configs — outer foldout uses BeginFoldoutHeaderGroup (top level, no nesting)
            EditorGUILayout.Space(6);
            _configsFold = EditorGUILayout.BeginFoldoutHeaderGroup(
                _configsFold, "Panel Configurations");

            if (_configsFold)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.HelpBox(
                    contextSO != null
                        ? $"Using context: {contextSO.contextName}\n" +
                          "Each config row shows named flags from the generated enum."
                        : "Assign a Context SO above to get named flag dropdowns.",
                    MessageType.None);

                EditorGUILayout.Space(4);

                for (int i = 0; i < _configsProp.arraySize; i++)
                    DrawPanelConfigRow(i, contextSO);

                EditorGUILayout.Space(4);
                if (GUILayout.Button("+ Add Panel Config"))
                    _configsProp.InsertArrayElementAtIndex(_configsProp.arraySize);

                EditorGUILayout.EndVertical();
            }

            // Must always close the outer header group
            EditorGUILayout.EndFoldoutHeaderGroup();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawPanelConfigRow(int i, MID_UIStateContext contextSO)
        {
            var element     = _configsProp.GetArrayElementAtIndex(i);
            var maskProp    = element.FindPropertyRelative("stateMask");
            var displayProp = element.FindPropertyRelative("displayName");
            var showProp    = element.FindPropertyRelative("show");
            var hideProp    = element.FindPropertyRelative("hide");
            var enterProp   = element.FindPropertyRelative("onEnter");
            var exitProp    = element.FindPropertyRelative("onExit");

            string title = string.IsNullOrEmpty(displayProp.stringValue)
                ? $"Config [{i}]"
                : displayProp.stringValue;

            // FIX: Use regular Foldout (NOT BeginFoldoutHeaderGroup) here —
            // BeginFoldoutHeaderGroup cannot be nested inside another one.
            element.isExpanded = EditorGUILayout.Foldout(
                element.isExpanded, title, true, EditorStyles.foldoutHeader);

            if (!element.isExpanded) return;

            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(displayProp, new GUIContent("Display Name"));

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("State Mask:", EditorStyles.boldLabel);
            if (contextSO != null)
                UIStateContextEditorUtils.DrawSingleStateDropdown(
                    contextSO.enumTypeName, maskProp);
            else
                EditorGUILayout.PropertyField(maskProp, new GUIContent("State Mask (raw int)"));

            EditorGUILayout.Space(2);
            EditorGUILayout.PropertyField(showProp, new GUIContent("Show"), true);
            EditorGUILayout.PropertyField(hideProp, new GUIContent("Hide"), true);
            EditorGUILayout.PropertyField(enterProp, new GUIContent("On Enter"));
            EditorGUILayout.PropertyField(exitProp,  new GUIContent("On Exit"));

            EditorGUILayout.Space(4);
            var old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
            if (GUILayout.Button("Remove Config"))
                _configsProp.DeleteArrayElementAtIndex(i);
            GUI.backgroundColor = old;

            EditorGUI.indentLevel--;
        }
    }

    // ── Shared Utilities ──────────────────────────────────────────────────────

    public static class UIStateContextEditorUtils
    {
        /// <summary>
        /// Draw multi-select flag checkboxes by reflecting the generated enum type.
        /// Supports multi-object editing via SerializedProperty.
        /// </summary>
        public static void DrawFlagsCheckboxes(string enumTypeName, SerializedProperty maskProp)
        {
            var enumType = FindEnumType(enumTypeName);
            if (enumType == null)
            {
                DrawMissingTypeHelpBox(enumTypeName);
                EditorGUILayout.PropertyField(maskProp,
                    new GUIContent("Show When (raw int)"));
                return;
            }

            var (names, values) = GetEnumNamesValues(enumType);
            int current = maskProp.intValue;
            int next    = current;

            foreach (var (name, value) in ZipSkipZero(names, values))
            {
                bool isSet    = (current & value) != 0;
                bool newIsSet = EditorGUILayout.ToggleLeft(name, isSet);
                if (newIsSet != isSet)
                {
                    if (newIsSet) next |=  value;
                    else          next &= ~value;
                }
            }

            if (next != current)
                maskProp.intValue = next;

            DrawSummary(next, names, values);
        }

        /// <summary>
        /// Draw a single-state dropdown for buttons / configs that transition to one state.
        /// Supports multi-object editing via SerializedProperty.
        /// </summary>
        public static void DrawSingleStateDropdown(string enumTypeName,
            SerializedProperty maskProp, bool includeNone = false)
        {
            var enumType = FindEnumType(enumTypeName);
            if (enumType == null)
            {
                DrawMissingTypeHelpBox(enumTypeName);
                EditorGUILayout.PropertyField(maskProp,
                    new GUIContent("Target State (raw int)"));
                return;
            }

            var (names, values) = GetEnumNamesValues(enumType);

            var options    = new List<string>();
            var optionVals = new List<int>();

            if (includeNone)
            {
                options.Add("None (0)");
                optionVals.Add(0);
            }

            foreach (var (name, value) in ZipSkipZero(names, values))
            {
                // Only single-bit values for direct state transitions
                if (value != 0 && (value & (value - 1)) == 0)
                {
                    options.Add(name);
                    optionVals.Add(value);
                }
            }

            if (options.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No single-bit states defined in this enum yet.\n" +
                    "Add states and run the generator.",
                    MessageType.Info);
                EditorGUILayout.PropertyField(maskProp, new GUIContent("State (raw int)"));
                return;
            }

            int current = maskProp.intValue;
            int idx     = optionVals.IndexOf(current);
            if (idx < 0) idx = 0;

            int newIdx = EditorGUILayout.Popup("State", idx, options.ToArray());
            if (newIdx >= 0 && newIdx < optionVals.Count)
                maskProp.intValue = optionVals[newIdx];
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static void DrawMissingTypeHelpBox(string typeName)
        {
            string shortName = typeName.Contains(".")
                ? typeName.Substring(typeName.LastIndexOf('.') + 1)
                : typeName;
            EditorGUILayout.HelpBox(
                $"Enum type '{shortName}' not found.\n" +
                "Run the generator first:\n" +
                "MidManStudio > Utilities > UI State Context Generator",
                MessageType.Warning);
        }

        private static Type FindEnumType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
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

        private static IEnumerable<(string name, int value)> ZipSkipZero(
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
                EditorGUILayout.HelpBox(
                    "No states selected — element will never be shown.",
                    MessageType.Warning);
            }
            else
            {
                var selected = new List<string>();
                for (int i = 0; i < names.Length; i++)
                    if (values[i] != 0 && (mask & values[i]) != 0)
                        selected.Add(names[i]);
                EditorGUILayout.HelpBox(
                    $"Visible in: {string.Join(", ", selected)}",
                    MessageType.None);
            }
        }
    }
}
#endif
