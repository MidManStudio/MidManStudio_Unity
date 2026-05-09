
// Custom editor for MID_BaseSO. Applies the resolved icon to the asset in
// the Project window whenever the SO is selected or the icon field changes.
//
// Also draws a small preview of the active icon at the top of the inspector.

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using MidManStudio.Core;

namespace MidManStudio.Core.EditorUtils
{
    [CustomEditor(typeof(MID_BaseSO), editorForChildClasses: true)]
    public class MID_BaseSOEditor : UnityEditor.Editor
    {
        private SerializedProperty _iconProp;
        private Texture2D          _lastAppliedIcon;

        private void OnEnable()
        {
            _iconProp = serializedObject.FindProperty("_customIcon");
            ApplyIcon();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // ── Icon preview row ──────────────────────────────────────────────
            var so          = (MID_BaseSO)target;
            var resolvedIcon = so.ResolveIcon();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                // Preview thumbnail
                if (resolvedIcon != null)
                {
                    var rect = GUILayoutUtility.GetRect(36, 36,
                        GUILayout.Width(36), GUILayout.Height(36));
                    GUI.DrawTexture(rect, resolvedIcon, ScaleMode.ScaleToFit);
                }
                else
                {
                    // Show the default Unity SO icon
                    var defaultIcon = EditorGUIUtility.ObjectContent(target, target.GetType()).image;
                    if (defaultIcon != null)
                    {
                        var rect = GUILayoutUtility.GetRect(36, 36,
                            GUILayout.Width(36), GUILayout.Height(36));
                        GUI.DrawTexture(rect, defaultIcon as Texture2D
                            ?? Texture2D.whiteTexture, ScaleMode.ScaleToFit);
                    }
                }

                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(target.name, EditorStyles.boldLabel);

                    var iconSource =
                        _iconProp.objectReferenceValue != null ? "Per-instance icon" :
                        !string.IsNullOrEmpty(so.GroupIconPath)
                            ? $"Group icon: {so.GroupIconPath}"
                            : "Default Unity icon";

                    var old = GUI.color;
                    GUI.color = new Color(0.6f, 0.6f, 0.6f, 1f);
                    EditorGUILayout.LabelField(iconSource, EditorStyles.miniLabel);
                    GUI.color = old;
                }
            }

            EditorGUILayout.Space(4);

            // ── Custom icon field ─────────────────────────────────────────────
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(_iconProp,
                new GUIContent("Custom Icon",
                    "Per-instance icon shown in the Project window.\n" +
                    "Overrides the group icon if both are set."));
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                ApplyIcon();
            }

            if (_iconProp.objectReferenceValue == null &&
                !string.IsNullOrEmpty(so.GroupIconPath))
            {
                EditorGUILayout.HelpBox(
                    $"Using group icon from:\n{so.GroupIconPath}",
                    MessageType.None);
            }

            EditorGUILayout.Space(6);

            // ── Draw all remaining fields ─────────────────────────────────────
            // Exclude the icon field we already drew above.
            DrawPropertiesExcluding(serializedObject, "_customIcon");

            serializedObject.ApplyModifiedProperties();
        }

        private void ApplyIcon()
        {
            if (target == null) return;
            var so   = (MID_BaseSO)target;
            var icon = so.ResolveIcon();

            // Only call SetIconForObject if the icon actually changed — avoids
            // unnecessary asset database writes on every inspector repaint.
            if (icon == _lastAppliedIcon) return;
            _lastAppliedIcon = icon;

            // EditorGUIUtility.SetIconForObject applies the icon to the asset's
            // thumbnail in the Project window. Null resets to the default.
            EditorGUIUtility.SetIconForObject(target, icon);
            EditorUtility.SetDirty(target);
        }
    }
}
#endif
