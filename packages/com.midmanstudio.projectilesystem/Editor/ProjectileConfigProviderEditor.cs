// Custom Inspector for ProjectileConfigProviderSO — adds an "Import Entries
// from JSON" panel below the default inspector. This is the SAME mechanic
// PoolTypeProviderEditorBase gives ObjectPoolTypeProviderSO /
// ParticlePoolTypeProviderSO — draws the normal Inspector untouched, then a
// paste box underneath, so bulk-adding the 40-odd entries that drive
// ProjectileConfigType generation doesn't mean clicking "+" and typing each
// field by hand in the list drawer.
//
// Parsing/configSO-linking logic lives in ProjectileConfigEntryJsonImporter.cs
// — this file is UI only, same split PoolTypeProviderEditor.cs /
// PoolEntryJsonImporter.cs use.

using MidManStudio.Projectiles.Config;
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Projectiles.EditorUtils
{
    [CustomEditor(typeof(ProjectileConfigProviderSO))]
    public sealed class ProjectileConfigProviderEditor : Editor
    {
        private string _jsonInput;
        private string _lastMessage;
        private bool   _lastMessageIsError;
        private bool   _replaceExisting;
        private bool   _foldout = true;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(8);
            _foldout = EditorGUILayout.Foldout(_foldout, "Import Entries from JSON", true);
            if (!_foldout) return;

            EditorGUILayout.HelpBox(
                "Paste a JSON array — either just names:\n" +
                "  [\"Aliyahoo419\", \"Beatle\"]\n" +
                "or full entries (all fields optional except one of enumName/name):\n" +
                "  [{\"enumName\":\"Aliyahoo419\",\"explicitOffset\":2,\"comment\":\"SMG basic\"}]\n\n" +
                "configSO can't be set from JSON — it's auto-linked by searching the " +
                "project for a ProjectileConfigSO asset with a matching name. Ambiguous " +
                "or missing matches are added anyway (name/comment intact) with configSO " +
                "left unassigned, and reported below for manual drag-and-drop.",
                MessageType.None);

            _jsonInput = EditorGUILayout.TextArea(_jsonInput ?? string.Empty, GUILayout.MinHeight(90));

            _replaceExisting = EditorGUILayout.ToggleLeft(
                "Replace existing entries (unchecked = append)", _replaceExisting);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Import"))
                    DoImport();

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

        private void DoImport()
        {
            if (!ProjectileConfigEntryJsonImporter.TryParse(
                    _jsonInput, out var parsed, out var warnings, out var error))
            {
                _lastMessage        = error;
                _lastMessageIsError = true;
                return;
            }

            var provider = (ProjectileConfigProviderSO)target;

            Undo.RecordObject(target, _replaceExisting
                ? "Replace ProjectileConfig Entries from JSON"
                : "Import ProjectileConfig Entries from JSON");

            if (_replaceExisting) provider.entries.Clear();
            provider.entries.AddRange(parsed);

            EditorUtility.SetDirty(target);
            serializedObject.Update();

            int unresolvedCount = parsed.FindAll(e => e.configSO == null).Count;

            string summary = parsed.Count == 0
                ? "Parsed 0 entries (empty array)."
                : $"Imported {parsed.Count} entr{(parsed.Count == 1 ? "y" : "ies")}. " +
                  (unresolvedCount > 0
                      ? $"{unresolvedCount} need configSO assigned manually. "
                      : "") +
                  "Remember to re-run the Config Type Generator afterwards so " +
                  "ProjectileConfigType picks these up.";

            if (warnings.Count > 0)
                summary += "\n\n" + string.Join("\n", warnings);

            _lastMessage        = summary;
            _lastMessageIsError = false;
        }
    }
}
