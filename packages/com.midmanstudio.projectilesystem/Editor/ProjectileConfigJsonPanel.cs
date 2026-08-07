// Reusable "Apply JSON" panel, extracted out of
// ProjectileConfigScriptableObjectEditor so any custom editor for a
// ProjectileConfigSO SUBCLASS can embed the exact same panel without
// duplicating the parsing/reporting logic.
//
// WHY THIS EXISTS ("my game's ProjectileConfigSO subclass doesn't extend the
// JSON-import custom editor stuff — what's the proper way to do it, does my
// own custom editor inherit it?"): ProjectileConfigScriptableObjectEditor
// already applies to every subclass automatically now
// ([CustomEditor(typeof(ProjectileConfigSO), true)] — see that class's own
// doc comment) — so if you DON'T need any UI beyond what it already gives
// you (DrawDefaultInspector, which shows subclass fields too, plus this
// panel), you don't need your own editor at all.
//
// If you DO want extra UI specific to your subclass (a custom widget for a
// subclass-only field, extra buttons, whatever), you write your own
// [CustomEditor(typeof(YourConfigSO))] targeting your exact type — Unity
// picks the more-derived match automatically, same rule as everywhere else
// in this codebase. But ProjectileConfigScriptableObjectEditor is sealed, so
// your editor can't literally `: ProjectileConfigScriptableObjectEditor` to
// reuse this panel — Unity's [CustomEditor] resolution is based on the
// attribute's target type, not C# inheritance of the editor class, so that
// was never required anyway. This class is what you actually embed instead.
//
// USAGE — inside your own custom editor:
//
//   [CustomEditor(typeof(YourGameProjectileConfigSO))]
//   public class YourGameProjectileConfigSOEditor : UnityEditor.Editor
//   {
//       private readonly ProjectileConfigJsonPanel _jsonPanel = new();
//
//       public override void OnInspectorGUI()
//       {
//           DrawDefaultInspector();
//           // ... your own extra fields/buttons/widgets here ...
//           _jsonPanel.Draw(serializedObject);
//       }
//   }
//
// ProjectileConfigScriptableObjectEditor itself now uses this same class
// internally (see that file) rather than duplicating the panel — this is
// the single source of truth for it.

using MidManStudio.Projectiles.Config;
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Projectiles.EditorUtils
{
    public sealed class ProjectileConfigJsonPanel
    {
        private string _jsonInput;
        private string _lastMessage;
        private bool   _lastMessageIsError;
        private bool   _foldout = true;

        /// <summary>
        /// Draws the panel. Call from inside your editor's OnInspectorGUI(),
        /// after DrawDefaultInspector() and any extra fields you draw
        /// yourself — matches where ProjectileConfigScriptableObjectEditor
        /// places it.
        /// </summary>
        public void Draw(SerializedObject serializedObject)
        {
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
                    DoApply(serializedObject);

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

        private void DoApply(SerializedObject serializedObject)
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
