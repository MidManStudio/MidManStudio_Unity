// Custom Inspector for ProjectileConfigScriptableObject. Draws the normal
// Inspector untouched, then adds an "Apply JSON" panel — paste a JSON object
// whose keys match this asset's serialized field names (base class fields
// included — MaxRange, MovementType, MinSpeed, etc., same as the ones
// declared directly on this class) and it fills in every primitive/enum/
// Vector2/Vector3 field it can, then reports exactly what it couldn't:
// unmatched keys (typos) separately from unsupported ones (asset references,
// AnimationCurve, Gradient — those need manual drag-and-drop same as
// ProjectileConfigEntry.configSO did in the enum-mapping importer).
//
// Example JSON — not exhaustive, just enough to show the shape:
//   {
//     "_movementType": "Straight",
//     "_minSpeed": 25, "_maxSpeed": 30,
//     "_lifetime": 3, "_maxRange": 50,
//     "_piercingType": "None",
//     "_projectileType": "basic", "_projectileClass": "basic",
//     "_capColliderSize": { "x": 0.2, "y": 0.08 },
//     "_minDamage": 8, "_maxDamage": 12,
//     "_minHeadShotDamage": 20, "_maxHeadShotDamage": 28
//   }

using UnityEditor;
using UnityEngine;

namespace MidManStudio.Projectiles.EditorUtils
{

    [CustomEditor(typeof(ProjectileShapeSO))]
    [CanEditMultipleObjects]
    public sealed partial class ProjectileConfigScriptableObjectEditor : UnityEditor.Editor
    {
        private string _jsonInput;
        private string _lastMessage;
        private bool   _lastMessageIsError;
        private bool   _foldout = true;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(8);
            _foldout = EditorGUILayout.Foldout(_foldout, "Apply JSON", true);
            if (!_foldout) return;

            EditorGUILayout.HelpBox(
                "Paste a JSON object whose keys match this asset's field names " +
                "exactly (including the leading underscore) — e.g. " +
                "{\"_minDamage\": 8, \"_maxDamage\": 12, \"_movementType\": \"Straight\"}. " +
                "Enums take the member name as a string. Asset references (sprites, " +
                "audio clips, materials, etc.), AnimationCurve, and Gradient can't come " +
                "from JSON — those are reported separately for manual assignment.",
                MessageType.None);

            _jsonInput = EditorGUILayout.TextArea(_jsonInput ?? string.Empty, GUILayout.MinHeight(120));

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Apply"))
                    DoApply();

                if (GUILayout.Button("Clear", GUILayout.Width(60)))
                {
                    _jsonInput   = string.Empty;
                    _lastMessage = null;
                }
            }

            if (!string.IsNullOrEmpty(_lastMessage))
            {
                EditorGUILayout.HelpBox(
                    _lastMessage,
                    _lastMessageIsError ? MessageType.Error : MessageType.Info);
            }
        }

        private void DoApply()
        {
            if (string.IsNullOrWhiteSpace(_jsonInput))
            {
                _lastMessage        = "Nothing pasted.";
                _lastMessageIsError = true;
                return;
            }

            Undo.RecordObjects(serializedObject.targetObjects, "Apply Projectile Config JSON");

            var result = ScriptableObjectJsonApplier.Apply(serializedObject, _jsonInput);

            if (result.errors.Count > 0)
            {
                _lastMessage        = string.Join("\n", result.errors);
                _lastMessageIsError = true;
                return;
            }

            foreach (var t in serializedObject.targetObjects)
                EditorUtility.SetDirty(t);

            var msg = new System.Text.StringBuilder();
            msg.Append($"Applied {result.applied.Count} field(s).");

            if (result.unsupported.Count > 0)
                msg.Append($"\n{result.unsupported.Count} need manual assignment " +
                           $"(asset references / curves / gradients): {string.Join(", ", result.unsupported)}");

            if (result.unmatchedKeys.Count > 0)
                msg.Append($"\n{result.unmatchedKeys.Count} key(s) didn't match any field " +
                           $"(check spelling/underscore): {string.Join(", ", result.unmatchedKeys)}");

            _lastMessage        = msg.ToString();
            _lastMessageIsError = false;
        }
    }
}
