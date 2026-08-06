#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.EditorUtils
{
    /// <summary>
    /// Draws a [MID_SortingLayer] string field as a popup of
    /// UnityEngine.SortingLayer.layers — deliberately built on the fully
    /// public SortingLayer API (layers / name / value) rather than the
    /// private EditorGUILayout.SortingLayerField overload (which only exists
    /// via reflection and is not guaranteed stable across Unity versions).
    /// </summary>
    [CustomPropertyDrawer(typeof(MID_SortingLayerAttribute))]
    public class SortingLayerDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.String)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            var layers = SortingLayer.layers;
            if (layers == null || layers.Length == 0)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            string[] names = new string[layers.Length];
            int      current = 0;
            for (int i = 0; i < layers.Length; i++)
            {
                names[i] = layers[i].name;
                if (names[i] == property.stringValue) current = i;
            }

            EditorGUI.BeginProperty(position, label, property);
            EditorGUI.BeginChangeCheck();
            int chosen = EditorGUI.Popup(position, label.text, current, names);
            if (EditorGUI.EndChangeCheck())
                property.stringValue = names[chosen];
            EditorGUI.EndProperty();
        }
    }
}
#endif
