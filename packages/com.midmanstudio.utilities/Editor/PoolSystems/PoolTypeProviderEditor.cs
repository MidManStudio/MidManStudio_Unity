// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/pool-systems.md, section "PoolTypeProviderEditor.cs"
// ============================================================================
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Core.Pools.Generator
{
    /// <summary>
    /// Shared inspector for <see cref="ObjectPoolTypeProviderSO"/> and <see cref="ParticlePoolTypeProviderSO"/>. Draws the
    /// normal inspector, then an Import Entries from JSON panel, so a large old enum (20 or 30 entries) can be moved in at
    /// once. The accepted JSON shapes are described on <see cref="PoolEntryJsonImporter"/>.
    /// </summary>
    public abstract class PoolTypeProviderEditorBase : UnityEditor.Editor
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

            if (!(target is IPoolEntryProviderSO provider))
            {
                EditorGUILayout.HelpBox(
                    $"{target.GetType().Name} doesn't implement IPoolEntryProviderSO.",
                    MessageType.Error);
                return;
            }

            EditorGUILayout.HelpBox(
                "Paste a JSON array — either just names:\n" +
                "  [\"BulletShell\", \"MuzzleFlash\"]\n" +
                "or full entries (all fields optional except one of entryName/name):\n" +
                "  [{\"entryName\":\"BulletShell\",\"explicitOffset\":2,\"comment\":\"9mm\"}]",
                MessageType.None);

            _jsonInput = EditorGUILayout.TextArea(_jsonInput ?? string.Empty, GUILayout.MinHeight(90));

            _replaceExisting = EditorGUILayout.ToggleLeft(
                "Replace existing entries (unchecked = append)", _replaceExisting);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Import"))
                    DoImport(provider);

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

        private void DoImport(IPoolEntryProviderSO provider)
        {
            if (!PoolEntryJsonImporter.TryParse(_jsonInput, out var parsed, out var error))
            {
                _lastMessage        = error;
                _lastMessageIsError = true;
                return;
            }

            Undo.RecordObject(target, _replaceExisting
                ? "Replace Pool Entries from JSON"
                : "Import Pool Entries from JSON");

            var entries = provider.Entries;
            if (_replaceExisting) entries.Clear();
            entries.AddRange(parsed);

            EditorUtility.SetDirty(target);
            serializedObject.Update();

            _lastMessage = parsed.Count == 0
                ? "Parsed 0 entries (empty array)."
                : $"Imported {parsed.Count} entr{(parsed.Count == 1 ? "y" : "ies")}. " +
                  "Remember to re-run the Pool Type Generator window afterwards so the " +
                  "generated enum picks these up.";
            _lastMessageIsError = false;
        }
    }

    /// <summary>Inspector for <see cref="ObjectPoolTypeProviderSO"/>.</summary>
    [CustomEditor(typeof(ObjectPoolTypeProviderSO))]
    public sealed class ObjectPoolTypeProviderEditor : PoolTypeProviderEditorBase { }

    /// <summary>Inspector for <see cref="ParticlePoolTypeProviderSO"/>.</summary>
    [CustomEditor(typeof(ParticlePoolTypeProviderSO))]
    public sealed class ParticlePoolTypeProviderEditor : PoolTypeProviderEditorBase { }
}
