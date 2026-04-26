#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Core.Utilities.Editor
{
    /// <summary>
    /// Custom property drawer for MID_NamedListAttribute with color support
    /// </summary>
    [CustomPropertyDrawer(typeof(MID_NamedListAttribute))]
    public class NamedListDrawer : PropertyDrawer
    {
        protected virtual MID_NamedListAttribute Attribute => (MID_NamedListAttribute)attribute;

        // Counter for missing names to ensure unique identifiers
        private static int missingNameCounter = 0;

        // Color tint intensity for backgrounds
        private const float BackgroundTintAlpha = 0.15f;
        private const float BorderTintAlpha = 0.5f;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUI.GetPropertyHeight(property, label, true);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // Get display name
            string displayName = GetDisplayName(property);
            label = new GUIContent(label) { text = displayName };

            // Get color if enabled
            Color? elementColor = null;
            if (Attribute.UseCustomColors)
            {
                elementColor = GetElementColor(property);
            }

            // Draw with color background if color is specified
            if (elementColor.HasValue && elementColor.Value != Color.clear)
            {
                DrawColoredProperty(position, property, label, elementColor.Value);
            }
            else
            {
                // Standard property field
                EditorGUI.PropertyField(position, property, label, true);
            }
        }

        /// <summary>
        /// Draw property with colored background
        /// </summary>
        private void DrawColoredProperty(Rect position, SerializedProperty property, GUIContent label, Color color)
        {
            // Calculate background rect (slightly inset for better visual)
            Rect backgroundRect = new Rect(
                position.x - 2,
                position.y - 2,
                position.width + 4,
                position.height + 4
            );

            // Draw colored background
            Color backgroundColor = new Color(color.r, color.g, color.b, BackgroundTintAlpha);
            EditorGUI.DrawRect(backgroundRect, backgroundColor);

            // Draw border (left side accent)
            Color borderColor = new Color(color.r, color.g, color.b, BorderTintAlpha);
            Rect leftBorder = new Rect(
                position.x - 2,
                position.y - 2,
                4, // Width of left border
                position.height + 4
            );
            EditorGUI.DrawRect(leftBorder, borderColor);

            // Draw top border
            Rect topBorder = new Rect(
                position.x - 2,
                position.y - 2,
                position.width + 4,
                2 // Height of top border
            );
            EditorGUI.DrawRect(topBorder, borderColor);

            // Draw the property itself
            EditorGUI.PropertyField(position, property, label, true);
        }

        /// <summary>
        /// Get display name for the property, handling null/empty cases
        /// </summary>
        private string GetDisplayName(SerializedProperty property)
        {
            // Try IArrayElementTitle interface first using boxedValue (Unity 2021.2+)
            try
            {
                if (property.boxedValue is IArrayElementTitle titled)
                {
                    string name = titled.Name;
                    return ValidateName(name, property);
                }
            }
            catch
            {
                // boxedValue might not be available in older Unity versions
            }

            // Try custom variable name
            if (!string.IsNullOrEmpty(Attribute.VarName))
            {
                string fullPathName = property.propertyPath + "." + Attribute.VarName;
                SerializedProperty nameProp = property.serializedObject.FindProperty(fullPathName);

                if (nameProp != null)
                {
                    string title = GetTitle(nameProp);
                    return ValidateName(title, property);
                }
                else
                {
                    Debug.LogWarning(
                        $"Could not get name for property path {fullPathName}, " +
                        $"did you define a path or inherit from IArrayElementTitle?");
                }
            }

            // Fallback to property name
            return ValidateName(property.displayName, property);
        }

        /// <summary>
        /// Get element color from various sources
        /// </summary>
        private Color? GetElementColor(SerializedProperty property)
        {
            // Try IArrayElementColor interface first using boxedValue (Unity 2021.2+)
            try
            {
                if (property.boxedValue is IArrayElementColor colorProvider)
                {
                    return colorProvider.ElementColor;
                }
            }
            catch
            {
                // boxedValue might not be available, continue to fallback
            }

            // FALLBACK: Try to find color field by name in the serialized property
            string colorFieldName = string.IsNullOrEmpty(Attribute.ColorFieldName)
                ? "color"
                : Attribute.ColorFieldName;

            string fullPathName = property.propertyPath + "." + colorFieldName;
            SerializedProperty colorProp = property.serializedObject.FindProperty(fullPathName);

            if (colorProp != null && colorProp.propertyType == SerializedPropertyType.Color)
            {
                return colorProp.colorValue;
            }

            // Alternative: Try relative path
            SerializedProperty relativeColorProp = property.FindPropertyRelative(colorFieldName);
            if (relativeColorProp != null && relativeColorProp.propertyType == SerializedPropertyType.Color)
            {
                return relativeColorProp.colorValue;
            }

            return null;
        }

        /// <summary>
        /// Validate and sanitize name, replacing null/empty with meaningful defaults
        /// </summary>
        private string ValidateName(string name, SerializedProperty property)
        {
            // Handle null or empty names
            if (string.IsNullOrWhiteSpace(name))
            {
                int arrayIndex = GetArrayIndex(property);

                if (arrayIndex >= 0)
                {
                    return $"Missing_{arrayIndex}";
                }
                else
                {
                    return $"Missing_{missingNameCounter++}";
                }
            }

            // Trim whitespace
            name = name.Trim();

            // Check if name is just whitespace after trim
            if (string.IsNullOrEmpty(name))
            {
                int arrayIndex = GetArrayIndex(property);
                return arrayIndex >= 0 ? $"Empty_{arrayIndex}" : $"Empty_{missingNameCounter++}";
            }

            return name;
        }

        /// <summary>
        /// Extract array index from property path if it's an array element
        /// </summary>
        private int GetArrayIndex(SerializedProperty property)
        {
            string path = property.propertyPath;

            // Look for pattern like "Array.data[X]"
            int startIndex = path.LastIndexOf("[");
            int endIndex = path.LastIndexOf("]");

            if (startIndex >= 0 && endIndex > startIndex)
            {
                string indexStr = path.Substring(startIndex + 1, endIndex - startIndex - 1);
                if (int.TryParse(indexStr, out int index))
                {
                    return index;
                }
            }

            return -1;
        }

        /// <summary>
        /// Get title from various property types
        /// </summary>
        private string GetTitle(SerializedProperty prop)
        {
            if (prop == null)
                return string.Empty;

            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue.ToString();

                case SerializedPropertyType.Boolean:
                    return prop.boolValue.ToString();

                case SerializedPropertyType.Float:
                    return prop.floatValue.ToString("G");

                case SerializedPropertyType.String:
                    return prop.stringValue ?? string.Empty;

                case SerializedPropertyType.Color:
                    return prop.colorValue.ToString();

                case SerializedPropertyType.ObjectReference:
                    if (prop.objectReferenceValue != null)
                        return prop.objectReferenceValue.name;
                    return "Null";

                case SerializedPropertyType.Enum:
                    if (prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumNames.Length)
                        return prop.enumNames[prop.enumValueIndex];
                    return "Invalid_Enum";

                case SerializedPropertyType.Vector2:
                    return prop.vector2Value.ToString();

                case SerializedPropertyType.Vector3:
                    return prop.vector3Value.ToString();

                case SerializedPropertyType.Vector4:
                    return prop.vector4Value.ToString();

                case SerializedPropertyType.Vector2Int:
                    return prop.vector2IntValue.ToString();

                case SerializedPropertyType.Vector3Int:
                    return prop.vector3IntValue.ToString();

                case SerializedPropertyType.Rect:
                    return prop.rectValue.ToString();

                case SerializedPropertyType.Bounds:
                    return prop.boundsValue.ToString();

                case SerializedPropertyType.Quaternion:
                    return prop.quaternionValue.ToString();

                default:
                    return string.Empty;
            }
        }
    }
}
#endif