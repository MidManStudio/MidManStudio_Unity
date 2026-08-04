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

using MidManStudio.Core.EditorUtils;
using MidManStudio.Projectiles.Config;
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Projectiles.EditorUtils
{

    [CustomEditor(typeof(ProjectileConfigSO))]
    [CanEditMultipleObjects]
    public sealed partial class ProjectileConfigScriptableObjectEditor : UnityEditor.Editor
    {
        private string _jsonInput;
        private string _lastMessage;
        private bool   _lastMessageIsError;
        private bool   _foldout = true;

        public override void OnInspectorGUI()
        {
            // ── Icon cache fix ───────────────────────────────────────────────
            // ProjectileConfigSO extends MID_BaseSO, whose per-instance
            // "custom icon" (_customIcon, a plain Texture2D field — this is
            // what shows up as the Project window thumbnail/"custom sprite"
            // for the asset) normally relies on MID_BaseSOEditor
            // ([CustomEditor(typeof(MID_BaseSO), editorForChildClasses: true)])
            // to invalidate MID_BaseSOProjectIconDrawer's per-GUID icon cache
            // the moment that field changes, so the thumbnail updates
            // immediately. Unity always resolves the MOST DERIVED
            // [CustomEditor] match for a given type — since THIS class targets
            // ProjectileConfigSO directly (an exact-type match beats
            // MID_BaseSOEditor's editorForChildClasses match), MID_BaseSOEditor
            // never runs for ProjectileConfigSO assets at all, and the plain
            // DrawDefaultInspector() call below has no idea _customIcon needs
            // special handling — it just writes the new value like any other
            // field.
            //
            // Net effect (the reported bug): assigning/changing a custom
            // icon/texture on a ProjectileConfigSO asset silently updates the
            // underlying data, but MID_BaseSOProjectIconDrawer's cache is
            // never invalidated, so the Project window keeps painting the
            // stale/old icon. MID_BaseSOProjectIconDrawer.ClearCacheOnScriptReload()
            // is [InitializeOnLoadMethod] — it fires after any domain reload,
            // and entering Play Mode triggers one by default, which is why the
            // correct icon only ever shows up once Play is hit. Configs that
            // never set a custom icon (falling back to the MID_BaseSO
            // default/GroupIconPath behaviour — "the MID_BaseSO thing") never
            // populate the cache with a stale value in the first place, so
            // they were never affected — matching the reported "regular
            // configs update instantly" half of this.
            //
            // Fix: reproduce MID_BaseSOEditor's own invalidate+repaint step
            // here too. EditorGUI.BeginChangeCheck/EndChangeCheck around
            // DrawDefaultInspector() picks up ANY field edit (not just the
            // icon) — cheap and harmless to invalidate a couple of extra
            // times on an unrelated field change, and far simpler/more
            // robust than trying to diff _customIcon specifically before vs.
            // after the call. Loops over `targets` (not just `target`) since
            // this editor supports multi-select ([CanEditMultipleObjects]).
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (EditorGUI.EndChangeCheck())
            {
                foreach (var t in targets)
                {
                    if (t == null) continue;
                    string path = AssetDatabase.GetAssetPath(t);
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    if (!string.IsNullOrEmpty(guid))
                        MID_BaseSOProjectIconDrawer.InvalidateCache(guid);
                }
                EditorApplication.RepaintProjectWindow();
            }

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
