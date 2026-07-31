// Applies a parsed JSON object (from MiniJsonParser) onto a SerializedObject's
// properties by name. Works generically against whatever fields the target
// actually has — no hardcoded per-field schema — so it doesn't need updating
// every time a field gets added to ProjectileConfigScriptableObject (or any
// other ScriptableObject this ends up reused for).
//
// JSON keys should match the C# field name exactly, including the leading
// underscore (e.g. "_minDamage", "_capColliderSize", "_projectileType") —
// that's literally the SerializedProperty path, no translation layer to keep
// in sync separately.
//
// Enums: give the member name as a string ("_movementType": "Guided") — case-
// insensitive match against the property's enumNames. A plain number is also
// accepted and used as the enum's declared index directly.
//
// Vector2: {"x": 0.2, "y": 0.08}
//
// NOT settable from JSON, and reported rather than silently skipped: asset
// references (Sprite, Material, AudioClip, PhysicsMaterial2D, GameObject,
// ProjectileShapeSO, ...), AnimationCurve, Gradient, LayerMask bit patterns
// beyond a plain int. These need manual Inspector assignment same as
// ProjectileConfigEntry.configSO did in the enum-mapping importer.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Projectiles.Editor
{
    public static class ScriptableObjectJsonApplier
    {
        public class ApplyResult
        {
            public List<string> applied      = new List<string>();
            public List<string> unmatchedKeys = new List<string>();   // no property with this name
            public List<string> unsupported   = new List<string>();   // property exists but type isn't JSON-settable
            public List<string> errors        = new List<string>();
        }

        public static ApplyResult Apply(SerializedObject serializedObject, string json)
        {
            var result = new ApplyResult();

            object parsed;
            try
            {
                parsed = MiniJsonParser.Parse(json);
            }
            catch (System.Exception ex)
            {
                result.errors.Add($"Invalid JSON: {ex.Message}");
                return result;
            }

            if (!(parsed is Dictionary<string, object> dict))
            {
                result.errors.Add("Expected a JSON object at the top level, e.g. { \"_minDamage\": 10, ... }.");
                return result;
            }

            serializedObject.Update();

            foreach (var kv in dict)
            {
                var prop = serializedObject.FindProperty(kv.Key);
                if (prop == null)
                {
                    result.unmatchedKeys.Add(kv.Key);
                    continue;
                }

                if (TryApplyValue(prop, kv.Value))
                    result.applied.Add(kv.Key);
                else
                    result.unsupported.Add($"{kv.Key} ({prop.propertyType})");
            }

            serializedObject.ApplyModifiedProperties();
            return result;
        }

        private static bool TryApplyValue(SerializedProperty prop, object value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Float:
                    if (value is double d) { prop.floatValue = (float)d; return true; }
                    return false;

                case SerializedPropertyType.Integer:
                    if (value is double di) { prop.intValue = (int)di; return true; }
                    return false;

                case SerializedPropertyType.Boolean:
                    if (value is bool b) { prop.boolValue = b; return true; }
                    return false;

                case SerializedPropertyType.String:
                    if (value is string s) { prop.stringValue = s; return true; }
                    return false;

                case SerializedPropertyType.Enum:
                    if (value is string enumName)
                    {
                        for (int i = 0; i < prop.enumNames.Length; i++)
                        {
                            if (string.Equals(prop.enumNames[i], enumName, System.StringComparison.OrdinalIgnoreCase))
                            {
                                prop.enumValueIndex = i;
                                return true;
                            }
                        }
                        return false; // name not found among declared enum members
                    }
                    if (value is double enumIndex)
                    {
                        int idx = (int)enumIndex;
                        if (idx >= 0 && idx < prop.enumNames.Length) { prop.enumValueIndex = idx; return true; }
                        return false;
                    }
                    return false;

                case SerializedPropertyType.Vector2:
                    if (value is Dictionary<string, object> v2 &&
                        v2.TryGetValue("x", out var xv) && v2.TryGetValue("y", out var yv) &&
                        xv is double xd && yv is double yd)
                    {
                        prop.vector2Value = new Vector2((float)xd, (float)yd);
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Vector3:
                    if (value is Dictionary<string, object> v3 &&
                        v3.TryGetValue("x", out var x3) && v3.TryGetValue("y", out var y3) &&
                        v3.TryGetValue("z", out var z3) &&
                        x3 is double x3d && y3 is double y3d && z3 is double z3d)
                    {
                        prop.vector3Value = new Vector3((float)x3d, (float)y3d, (float)z3d);
                        return true;
                    }
                    return false;

                case SerializedPropertyType.LayerMask:
                    if (value is double lm) { prop.intValue = (int)lm; return true; }
                    return false;

                // Deliberately not handled — see file header. These need manual
                // Inspector assignment; reported as "unsupported" rather than
                // silently ignored so nothing looks like it was set when it wasn't.
                case SerializedPropertyType.ObjectReference:
                case SerializedPropertyType.AnimationCurve:
                case SerializedPropertyType.Gradient:
                case SerializedPropertyType.Generic:
                default:
                    return false;
            }
        }
    }
}
