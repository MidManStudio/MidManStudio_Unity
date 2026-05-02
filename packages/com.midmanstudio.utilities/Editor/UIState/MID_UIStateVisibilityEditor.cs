// MID_UIStateVisibilityEditor.cs
// Custom inspector for MID_UIStateVisibility.
// Shows multi-select checkboxes for UIStateId flags instead of a plain int field.
// Reads enum members at runtime via reflection so it automatically updates
// after the generator runs — no manual editor changes needed.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.UIState;

namespace MidManStudio.Core.Editor.UIState
{
    [CustomEditor(typeof(MID_UIStateVisibility))]
    public class MID_UIStateVisibilityEditor : UnityEditor.Editor
    {
        private SerializedProperty _showWhenProp;
        private SerializedProperty _logLevelProp;

        private void OnEnable()
        {
            _showWhenProp  = serializedObject.FindProperty("_showWhen");
            _logLevelProp  = serializedObject.FindProperty("_logLevel");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("UI State Visibility", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Panel is visible when the current UIStateId contains ANY of the checked flags.",
                MessageType.Info);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Show When:", EditorStyles.boldLabel);

            // Reflect current UIStateId enum members
            var stateType = typeof(UIStateId);
            var names  = Enum.GetNames(stateType);
            var values = (int[])Enum.GetValues(stateType) as int[];

            int currentMask = _showWhenProp.intValue;
            int newMask     = currentMask;

            // Checkboxes — skip None (0) and any composite values
            for (int i = 0; i < names.Length; i++)
            {
                if (values[i] == 0) continue;
                // Only show single-bit values (pure flags)
                if ((values[i] & (values[i] - 1)) != 0) continue;

                bool isSet    = (currentMask & values[i]) != 0;
                bool newIsSet = EditorGUILayout.ToggleLeft(names[i], isSet);

                if (newIsSet != isSet)
                {
                    if (newIsSet) newMask |=  values[i];
                    else          newMask &= ~values[i];
                }
            }

            if (newMask != currentMask)
                _showWhenProp.intValue = newMask;

            EditorGUILayout.Space(4);

            // Summary
            if (newMask == 0)
            {
                EditorGUILayout.HelpBox("No states selected — element will never be shown.",
                    MessageType.Warning);
            }
            else
            {
                var selected = new List<string>();
                for (int i = 0; i < names.Length; i++)
                    if (values[i] != 0 && (newMask & values[i]) != 0)
                        selected.Add(names[i]);
                EditorGUILayout.HelpBox($"Visible in: {string.Join(", ", selected)}",
                    MessageType.None);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(_logLevelProp);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
