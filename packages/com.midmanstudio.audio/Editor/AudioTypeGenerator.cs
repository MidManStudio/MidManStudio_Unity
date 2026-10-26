// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio/enum-ids.md, section "AudioTypeGenerator.cs"
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Audio.Generator;

namespace MidManStudio.Core.EditorUtils.Audio
{
    // ── Generator core ────────────────────────────────────────────────────────

    /// <summary>
    /// Generates AudioCategory.cs and AudioId.cs from the provider assets in the project.
    /// Open the window via MidManStudio > Audio > Audio Type Generator.
    /// </summary>
    public static class AudioTypeGeneratorCore
    {
        public static AudioTypeGeneratorSettingsSO FindSettings()
        {
            string[] guids = AssetDatabase.FindAssets("t:AudioTypeGeneratorSettingsSO");
            if (guids.Length == 0) return null;
            return AssetDatabase.LoadAssetAtPath<AudioTypeGeneratorSettingsSO>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        /// <summary>True when both generated enum files are already on disk.</summary>
        public static bool OutputsExist(AudioTypeGeneratorSettingsSO settings)
        {
            return settings != null &&
                   File.Exists(settings.categoryEnumOutputPath) &&
                   File.Exists(settings.idEnumOutputPath);
        }

        public static AudioGenerationResult Generate(AudioTypeGeneratorSettingsSO settings)
        {
            var result = new AudioGenerationResult();
            if (settings == null)
            {
                result.AddError("No AudioTypeGeneratorSettings found. Create one via MidManStudio > Audio > Audio Type Generator Settings.");
                return result;
            }

            ValidateSettings(settings, result);
            if (result.HasErrors) return result;

            AudioLockFile lockFile = LoadLock(settings.lockFilePath, result);
            if (lockFile == null) return result;

            AudioLockSection newCategories;
            AudioLockSection newIds;
            List<AudioResolvedBlock> categoryBlocks = AudioTypeLogic.Assign(
                CollectCategoryProviders(), settings.categoryBlockSize, lockFile.categories, result, "Category", out newCategories);
            List<AudioResolvedBlock> idBlocks = AudioTypeLogic.Assign(
                CollectIdProviders(), settings.idBlockSize, lockFile.ids, result, "Id", out newIds);
            if (result.HasErrors) return result;

            lockFile.categories = newCategories;
            lockFile.ids = newIds;

            string categoryText = AudioTypeLogic.BuildEnumSource(categoryBlocks, settings.generatedNamespace,
                "AudioCategory", "Audio category values. AUTO-GENERATED, do not edit.", "Category");
            string idText = AudioTypeLogic.BuildEnumSource(idBlocks, settings.generatedNamespace,
                "AudioId", "Audio id values, one per playable clip. AUTO-GENERATED, do not edit.", "Id");
            string lockText = JsonUtility.ToJson(lockFile, true) + "\n";

            if (WriteIfChanged(settings.categoryEnumOutputPath, categoryText, result)) result.FilesChanged++;
            if (WriteIfChanged(settings.idEnumOutputPath, idText, result)) result.FilesChanged++;
            if (result.HasErrors) return result;
            if (WriteIfChanged(settings.lockFilePath, lockText, result)) result.FilesChanged++;
            if (result.HasErrors) return result;

            result.CategoryBlocks = categoryBlocks.Count;
            result.IdBlocks = idBlocks.Count;
            if (result.FilesChanged > 0) AssetDatabase.Refresh();
            result.Success = true;
            Debug.Log("[AudioTypeGenerator] Done. " + result.FilesChanged + " file(s) changed.");
            return result;
        }

        // ── Providers ─────────────────────────────────────────────────────────

        private static List<AudioProviderData> CollectCategoryProviders()
        {
            var list = new List<AudioProviderData>();
            foreach (string guid in AssetDatabase.FindAssets("t:AudioCategoryProviderSO"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<AudioCategoryProviderSO>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null) list.Add(ToData(asset.packageId, asset.displayName, asset.priority, asset.entries));
            }
            return list;
        }

        private static List<AudioProviderData> CollectIdProviders()
        {
            var list = new List<AudioProviderData>();
            foreach (string guid in AssetDatabase.FindAssets("t:AudioIdProviderSO"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<AudioIdProviderSO>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null) list.Add(ToData(asset.packageId, asset.displayName, asset.priority, asset.entries));
            }
            return list;
        }

        private static AudioProviderData ToData(string packageId, string displayName, int priority,
            List<AudioEntryDefinition> entries)
        {
            var data = new AudioProviderData { PackageId = packageId, DisplayName = displayName, Priority = priority };
            if (entries == null) return data;
            foreach (AudioEntryDefinition e in entries)
                if (e != null) data.Entries.Add(new AudioEntryData(e.entryName, e.comment, e.explicitOffset));
            return data;
        }

        // ── Settings and files ────────────────────────────────────────────────

        private static void ValidateSettings(AudioTypeGeneratorSettingsSO s, AudioGenerationResult result)
        {
            if (!AudioTypeLogic.IsValidNamespace(s.generatedNamespace))
                result.AddError("Namespace '" + s.generatedNamespace + "' is not a valid C# namespace.");
            CheckPath(s.categoryEnumOutputPath, ".cs", "Category Enum Output", result);
            CheckPath(s.idEnumOutputPath, ".cs", "Id Enum Output", result);
            CheckPath(s.lockFilePath, ".json", "Lock File", result);
            if (!result.HasErrors &&
                (string.Equals(s.categoryEnumOutputPath, s.idEnumOutputPath, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(s.categoryEnumOutputPath, s.lockFilePath, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(s.idEnumOutputPath, s.lockFilePath, StringComparison.OrdinalIgnoreCase)))
                result.AddError("The two enum output paths and the lock file path must all be different.");
        }

        private static void CheckPath(string path, string extension, string label, AudioGenerationResult result)
        {
            if (string.IsNullOrWhiteSpace(path))
                result.AddError(label + " is empty.");
            else if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                result.AddError(label + " must end with " + extension + ".");
        }

        private static AudioLockFile LoadLock(string path, AudioGenerationResult result)
        {
            if (!File.Exists(path)) return new AudioLockFile();
            try
            {
                AudioLockFile file = JsonUtility.FromJson<AudioLockFile>(File.ReadAllText(path));
                if (file != null) return file;
            }
            catch (Exception)
            {
                // Falls through to the error below.
            }
            // Starting over would renumber every id, so stop and let the owner decide.
            result.AddError("The lock file '" + path + "' could not be read. Fix it or delete it, then generate again. " +
                            "Deleting it lets all values be assigned again.");
            return null;
        }

        private static bool WriteIfChanged(string path, string text, AudioGenerationResult result)
        {
            try
            {
                if (File.Exists(path) &&
                    File.ReadAllText(path).Replace("\r\n", "\n") == text) return false;

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, text, new UTF8Encoding(false));
                Debug.Log("[AudioTypeGenerator] Wrote " + path);
                return true;
            }
            catch (Exception e)
            {
                result.AddError("Could not write '" + path + "': " + e.Message +
                                " Output paths must be inside the project's Assets folder.");
                return false;
            }
        }
    }

    // ── Editor window ─────────────────────────────────────────────────────────

    public class AudioTypeGeneratorWindow : EditorWindow
    {
        private const string ProviderDir = "Assets/MidManStudio/Audio/EnumProviders";

        private AudioTypeGeneratorSettingsSO _settings;
        private AudioGenerationResult _lastResult;
        private Vector2 _scroll;
        private bool _foldCategories = true;
        private bool _foldIds = true;

        [MenuItem("MidManStudio/Audio/Audio Type Generator", priority = 120)]
        public static void Open()
        {
            var w = GetWindow<AudioTypeGeneratorWindow>("Audio Type Generator");
            w.minSize = new Vector2(500, 480);
        }

        private void OnEnable()
        {
            _settings = AudioTypeGeneratorCore.FindSettings();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("MidManStudio - Audio Type Generator", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Generates AudioCategory.cs and AudioId.cs from provider assets.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(6);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawSettings();
            EditorGUILayout.Space(4);
            DrawProviders();
            EditorGUILayout.Space(4);
            DrawActions();
            EditorGUILayout.Space(4);
            DrawResults();
            EditorGUILayout.EndScrollView();
        }

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            _settings = (AudioTypeGeneratorSettingsSO)EditorGUILayout.ObjectField(
                "Generator Settings", _settings, typeof(AudioTypeGeneratorSettingsSO), false);

            if (_settings == null)
            {
                EditorGUILayout.HelpBox("No AudioTypeGeneratorSettings found.\nCreate one: MidManStudio > Audio > Audio Type Generator Settings", MessageType.Warning);
                if (GUILayout.Button("Create Default Settings")) CreateDefaultSettings();
            }
            else
            {
                var so = new SerializedObject(_settings);
                so.Update();
                EditorGUILayout.PropertyField(so.FindProperty("categoryEnumOutputPath"), new GUIContent("Category Enum Output"));
                EditorGUILayout.PropertyField(so.FindProperty("idEnumOutputPath"), new GUIContent("Id Enum Output"));
                EditorGUILayout.PropertyField(so.FindProperty("lockFilePath"), new GUIContent("Lock File"));
                EditorGUILayout.PropertyField(so.FindProperty("categoryBlockSize"), new GUIContent("Category Block Size"));
                EditorGUILayout.PropertyField(so.FindProperty("idBlockSize"), new GUIContent("Id Block Size"));
                EditorGUILayout.PropertyField(so.FindProperty("generatedNamespace"), new GUIContent("Namespace"));
                EditorGUILayout.PropertyField(so.FindProperty("autoGenerateOnAssetChange"), new GUIContent("Auto-Generate on Change"));
                so.ApplyModifiedProperties();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawProviders()
        {
            EditorGUILayout.LabelField("Discovered Providers", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            DrawProviderGroup<AudioCategoryProviderSO>("Audio Categories", ref _foldCategories,
                a => new ProviderInfo(a.packageId, a.displayName, a.priority, a.EntryCount));
            EditorGUILayout.Space(4);
            DrawProviderGroup<AudioIdProviderSO>("Audio Ids", ref _foldIds,
                a => new ProviderInfo(a.packageId, a.displayName, a.priority, a.EntryCount));

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Create providers for your game:", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Category Provider")) CreateProvider<AudioCategoryProviderSO>("AudioCategoryProvider_MyGame.asset");
            if (GUILayout.Button("+ Audio Id Provider")) CreateProvider<AudioIdProviderSO>("AudioIdProvider_MyGame.asset");
            EditorGUILayout.EndHorizontal();
        }

        private struct ProviderInfo
        {
            public readonly string Id;
            public readonly string Display;
            public readonly int Priority;
            public readonly int Count;

            public ProviderInfo(string id, string display, int priority, int count)
            {
                Id = id;
                Display = display;
                Priority = priority;
                Count = count;
            }
        }

        private void DrawProviderGroup<T>(string label, ref bool fold, Func<T, ProviderInfo> extract)
            where T : ScriptableObject
        {
            string[] guids = AssetDatabase.FindAssets("t:" + typeof(T).Name);
            fold = EditorGUILayout.Foldout(fold, label + "  (" + guids.Length + " provider(s))", true);
            if (!fold) return;
            if (guids.Length == 0)
            {
                EditorGUILayout.HelpBox("No " + typeof(T).Name + " assets found.", MessageType.Info);
                return;
            }

            foreach (string g in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(g));
                if (asset == null) continue;
                ProviderInfo info = extract(asset);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("[" + info.Priority.ToString("D3") + "]", GUILayout.Width(36));
                EditorGUILayout.LabelField(info.Display + "  (" + info.Id + ")  - " + info.Count + " entries", EditorStyles.miniLabel);
                if (GUILayout.Button("Select", GUILayout.Width(50))) Selection.activeObject = asset;
                if (GUILayout.Button("Ping", GUILayout.Width(40))) EditorGUIUtility.PingObject(asset);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = _settings != null;
            Color old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.3f, 0.85f, 0.3f);
            if (GUILayout.Button("Generate Now", GUILayout.Height(36)))
            {
                _lastResult = AudioTypeGeneratorCore.Generate(_settings);
                if (_lastResult.Success)
                    EditorUtility.DisplayDialog("Audio Type Generator",
                        "Done.\nCategory blocks: " + _lastResult.CategoryBlocks +
                        "\nId blocks: " + _lastResult.IdBlocks +
                        "\nFiles changed: " + _lastResult.FilesChanged, "OK");
            }
            GUI.backgroundColor = old;
            GUI.enabled = true;
            if (GUILayout.Button("Open Output Folder", GUILayout.Height(36)))
            {
                string dir = _settings != null ? Path.GetDirectoryName(_settings.idEnumOutputPath) : "Assets";
                EditorUtility.RevealInFinder(string.IsNullOrEmpty(dir) ? "Assets" : dir);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("To add your own categories or audio ids:\n" +
                "  1. Click + Category Provider or + Audio Id Provider above.\n" +
                "  2. Set a packageId (e.g. com.mygame), a priority of 100 or more, and add entry names.\n" +
                "  3. Click Generate Now.\n" +
                "Then assign ids and categories to clips in an Audio Catalog.", MessageType.None);
        }

        private void DrawResults()
        {
            if (_lastResult == null) return;
            EditorGUILayout.LabelField("Last Result", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (_lastResult.Success)
                EditorGUILayout.HelpBox("Category blocks: " + _lastResult.CategoryBlocks + "   Id blocks: " + _lastResult.IdBlocks +
                                        "   Files changed: " + _lastResult.FilesChanged, MessageType.Info);
            foreach (string w in _lastResult.Warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
            foreach (string e in _lastResult.Errors) EditorGUILayout.HelpBox(e, MessageType.Error);
            EditorGUILayout.EndVertical();
        }

        private void CreateDefaultSettings()
        {
            _settings = AudioEnumProviderBootstrapper.CreateDefaultSettings();
            Selection.activeObject = _settings;
            EditorGUIUtility.PingObject(_settings);
        }

        private static void CreateProvider<T>(string fileName) where T : ScriptableObject
        {
            if (!Directory.Exists(ProviderDir)) Directory.CreateDirectory(ProviderDir);
            string path = AssetDatabase.GenerateUniqueAssetPath(ProviderDir + "/" + fileName);
            var asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }
    }

    // ── Auto-generate hook ────────────────────────────────────────────────────

    internal class AudioTypeAssetPostprocessor : AssetPostprocessor
    {
        private static readonly HashSet<string> Watched = new HashSet<string>
        {
            "AudioCategoryProviderSO", "AudioIdProviderSO", "AudioTypeGeneratorSettingsSO"
        };

        private static bool s_Pending;

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (s_Pending) return;
            if (!Touches(imported) && !Touches(deleted) && !Touches(moved)) return;

            AudioTypeGeneratorSettingsSO settings = AudioTypeGeneratorCore.FindSettings();
            if (settings == null || !settings.autoGenerateOnAssetChange) return;

            s_Pending = true;
            EditorApplication.delayCall += () =>
            {
                s_Pending = false;
                AudioGenerationResult r = AudioTypeGeneratorCore.Generate(settings);
                foreach (string e in r.Errors) Debug.LogError("[AudioTypeGen Auto] " + e);
            };
        }

        private static bool Touches(string[] paths)
        {
            foreach (string path in paths)
            {
                if (!path.EndsWith(".asset", StringComparison.Ordinal)) continue;
                Type t = AssetDatabase.GetMainAssetTypeAtPath(path);
                if (t != null && Watched.Contains(t.Name)) return true;
            }
            return false;
        }
    }
}
#endif
