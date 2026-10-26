// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio/enum-ids.md, section "MID_AudioIdDrawer.cs"
// ============================================================================
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Audio;

namespace MidManStudio.Core.EditorUtils.Audio
{
    /// <summary>
    /// Draws an <c>int</c> field as a popup of one of the generated audio enums. When the enum has
    /// not been generated yet, the field is drawn as a plain int with a tooltip that says so.
    /// </summary>
    public abstract class MID_AudioEnumPopupDrawer : PropertyDrawer
    {
        protected abstract MID_AudioEnumKind Kind { get; }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.Integer)
            {
                EditorGUI.LabelField(position, label.text, "Use [MID_AudioId] or [MID_AudioCategory] on an int field.");
                return;
            }

            string[] names = MID_AudioEnumLookup.GetNames(Kind);
            int[] values = MID_AudioEnumLookup.GetValues(Kind);

            EditorGUI.BeginProperty(position, label, property);
            EditorGUI.BeginChangeCheck();
            int selected;

            if (names.Length == 0)
            {
                var hint = new GUIContent(label.text,
                    "The generated " + Kind + " enum was not found. Run MidManStudio > Audio > Audio Type Generator.");
                selected = EditorGUI.IntField(position, hint, property.intValue);
            }
            else
            {
                int current = property.intValue;
                bool known = System.Array.IndexOf(values, current) >= 0;
                int extra = known ? 0 : 1;

                var options = new GUIContent[names.Length + extra];
                var optionValues = new int[names.Length + extra];
                if (!known)
                {
                    options[0] = new GUIContent("Missing (" + current + ")");
                    optionValues[0] = current;
                }
                for (int i = 0; i < names.Length; i++)
                {
                    options[i + extra] = new GUIContent(names[i]);
                    optionValues[i + extra] = values[i];
                }
                selected = EditorGUI.IntPopup(position, label, current, options, optionValues);
            }

            if (EditorGUI.EndChangeCheck()) property.intValue = selected;
            EditorGUI.EndProperty();
        }
    }

    /// <summary>Popup for <c>[MID_AudioId]</c> fields.</summary>
    [CustomPropertyDrawer(typeof(MID_AudioIdAttribute))]
    public sealed class MID_AudioIdDrawer : MID_AudioEnumPopupDrawer
    {
        protected override MID_AudioEnumKind Kind { get { return MID_AudioEnumKind.Id; } }
    }

    /// <summary>Popup for <c>[MID_AudioCategory]</c> fields.</summary>
    [CustomPropertyDrawer(typeof(MID_AudioCategoryAttribute))]
    public sealed class MID_AudioCategoryDrawer : MID_AudioEnumPopupDrawer
    {
        protected override MID_AudioEnumKind Kind { get { return MID_AudioEnumKind.Category; } }
    }
}
#endif
