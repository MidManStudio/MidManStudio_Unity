// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/fx.md, section "EffectTypeGenerator.cs"
// ============================================================================
// Generates EffectCategory.cs and EffectType.cs from the provider assets in the project.
// Open via: MidManStudio > Utilities > Effect Type Generator

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.EditorUtils.Generators;
using MidManStudio.Core.FX.Generator;

namespace MidManStudio.Core.EditorUtils.FX
{
    // ── Result ────────────────────────────────────────────────────────────────

    public class FXGenerationResult
    {
        public bool         Success;
        public int          CategoryBlocksWritten;
        public int          TypeBlocksWritten;
        public List<string> Errors   = new();
        public List<string> Warnings = new();
        public bool         HasErrors => Errors.Count > 0;
        public void AddError(string m)   => Errors.Add(m);
        public void AddWarning(string m) => Warnings.Add(m);
    }

    // ── Lock file ─────────────────────────────────────────────────────────────

    // One group of three lists per enum: the numbers in use, the block ranges, and the numbers
    // of removed entries. A lock file written before blocks and retired numbers existed only
    // has the two *Entries lists. The other lists stay empty when it is read.
    [Serializable]
    internal class FXLockFile
    {
        public List<EnumLockEntry> categoryEntries = new();
        public List<EnumLockBlock> categoryBlocks  = new();
        public List<EnumLockEntry> categoryRetired = new();

        public List<EnumLockEntry> typeEntries = new();
        public List<EnumLockBlock> typeBlocks  = new();
        public List<EnumLockEntry> typeRetired = new();

        // JsonUtility can leave a list null when the file has the key with no value.
        public void Normalize()
        {
            categoryEntries ??= new();
            categoryBlocks  ??= new();
            categoryRetired ??= new();
            typeEntries     ??= new();
            typeBlocks      ??= new();
            typeRetired     ??= new();
        }

        public EnumLockSection CategorySection() =>
            new EnumLockSection { entries = categoryEntries, blocks = categoryBlocks, retired = categoryRetired };

        public EnumLockSection TypeSection() =>
            new EnumLockSection { entries = typeEntries, blocks = typeBlocks, retired = typeRetired };

        public void SetCategory(EnumLockSection s)
        {
            categoryEntries = s.entries;
            categoryBlocks  = s.blocks;
            categoryRetired = s.retired;
        }

        public void SetType(EnumLockSection s)
        {
            typeEntries = s.entries;
            typeBlocks  = s.blocks;
            typeRetired = s.retired;
        }
    }

    // ── Generator core ────────────────────────────────────────────────────────

    public static class EffectTypeGeneratorCore
    {
        public static EffectTypeGeneratorSettingsSO FindSettings()
        {
            var guids = AssetDatabase.FindAssets("t:EffectTypeGeneratorSettingsSO");
            if (guids.Length == 0) return null;
            return AssetDatabase.LoadAssetAtPath<EffectTypeGeneratorSettingsSO>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        public static FXGenerationResult Generate(EffectTypeGeneratorSettingsSO settings)
        {
            var result = new FXGenerationResult();
            if (settings == null)
            {
                result.AddError("No EffectTypeGeneratorSettings found. Create one via MidManStudio > Utilities > Effect Type Generator Settings.");
                return result;
            }

            var lockFile = LoadLock(settings.lockFilePath, result);
            if (lockFile == null) return result;

            // Both enums are worked out before any file is written, so an error in one
            // leaves the files and the lock file as they were.
            var categoryBlocks = EnumBlockLogic.Assign(
                CollectCategoryProviders(), settings.minimumBlockSize, lockFile.CategorySection(),
                result.Errors, result.Warnings, "Category", out var newCategories);
            var typeBlocks = EnumBlockLogic.Assign(
                CollectTypeProviders(), settings.minimumBlockSize, lockFile.TypeSection(),
                result.Errors, result.Warnings, "Type", out var newTypes);
            if (result.HasErrors) return result;

            try
            {
                WriteEnum(categoryBlocks, settings.categoryEnumOutputPath, settings.generatedNamespace, "EffectCategory",
                    "Effect category IDs. AUTO-GENERATED — do not edit manually.");
                WriteEnum(typeBlocks, settings.typeEnumOutputPath, settings.generatedNamespace, "EffectType",
                    "Specific effect variant IDs. AUTO-GENERATED — do not edit manually.");

                lockFile.SetCategory(newCategories);
                lockFile.SetType(newTypes);
                SaveLock(lockFile, settings.lockFilePath);
            }
            catch (Exception e)
            {
                result.AddError("Could not write the generated files: " + e.Message);
                return result;
            }

            result.CategoryBlocksWritten = categoryBlocks.Count;
            result.TypeBlocksWritten     = typeBlocks.Count;
            AssetDatabase.Refresh();
            result.Success = true;
            return result;
        }

        // ── Provider collection ───────────────────────────────────────────────

        private static List<EnumProviderData> CollectCategoryProviders()
        {
            var list = new List<EnumProviderData>();
            foreach (var g in AssetDatabase.FindAssets($"t:{nameof(EffectCategoryProviderSO)}"))
            {
                var a = AssetDatabase.LoadAssetAtPath<EffectCategoryProviderSO>(AssetDatabase.GUIDToAssetPath(g));
                if (a != null) list.Add(ToProvider(a.packageId, a.displayName, a.priority, a.entries));
            }
            return list;
        }

        private static List<EnumProviderData> CollectTypeProviders()
        {
            var list = new List<EnumProviderData>();
            foreach (var g in AssetDatabase.FindAssets($"t:{nameof(EffectTypeProviderSO)}"))
            {
                var a = AssetDatabase.LoadAssetAtPath<EffectTypeProviderSO>(AssetDatabase.GUIDToAssetPath(g));
                if (a != null) list.Add(ToProvider(a.packageId, a.displayName, a.priority, a.entries));
            }
            return list;
        }

        private static EnumProviderData ToProvider(string packageId, string displayName, int priority,
            List<FXEntryDefinition> entries)
        {
            var data = new EnumProviderData { PackageId = packageId, DisplayName = displayName, Priority = priority };
            if (entries == null) return data;
            foreach (var e in entries)
                if (e != null) data.Entries.Add(new EnumEntryData(e.entryName, e.comment, e.explicitOffset));
            return data;
        }

        // ── File writing ──────────────────────────────────────────────────────

        private static void WriteEnum(List<EnumResolvedBlock> blocks, string path,
            string ns, string enumName, string doc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// AUTO-GENERATED by MidManStudio Effect Type Generator.");
            sb.AppendLine("// DO NOT edit this file manually.");
            sb.AppendLine("// Regenerate via: MidManStudio > Utilities > Effect Type Generator");
            sb.AppendLine($"// Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>{doc}</summary>");
            sb.AppendLine($"    public enum {enumName}");
            sb.AppendLine("    {");

            for (int b = 0; b < blocks.Count; b++)
            {
                var blk = blocks[b];
                sb.AppendLine($"        // ── {EnumBlockLogic.OneLine(blk.DisplayName)}  [{EnumBlockLogic.OneLine(blk.PackageId)}]  (block {blk.BlockStart}–{blk.BlockStart + blk.BlockSize - 1})  priority={blk.Priority}  ──");
                if (blk.Entries.Count == 0) { sb.AppendLine("        // (no entries defined)"); }
                else foreach (var e in blk.Entries)
                {
                    string pin     = e.Pinned ? " //[pinned]" : "";
                    string comment = EnumBlockLogic.OneLine(e.Comment);
                    string cmt     = comment.Length == 0 ? pin : $" // {comment}{pin}";
                    sb.AppendLine($"        {e.Name} = {e.Value},{cmt}");
                }
                if (b < blocks.Count - 1) sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Debug.Log($"[EffectTypeGenerator] Wrote {enumName} → {path}");
        }

        // ── Lock file ─────────────────────────────────────────────────────────

        private static FXLockFile LoadLock(string path, FXGenerationResult result)
        {
            if (!File.Exists(path)) return new FXLockFile();
            try
            {
                var file = JsonUtility.FromJson<FXLockFile>(File.ReadAllText(path));
                if (file != null) { file.Normalize(); return file; }
            }
            catch (Exception)
            {
                // Falls through to the error below.
            }
            // Starting over would renumber every effect, so stop and let the owner decide.
            result.AddError($"The lock file '{path}' could not be read. Fix it or delete it, then generate again. " +
                            "Deleting it lets all values be assigned again.");
            return null;
        }

        private static void SaveLock(FXLockFile lf, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonUtility.ToJson(lf, prettyPrint: true), Encoding.UTF8);
        }
    }

    // ── Editor Window ─────────────────────────────────────────────────────────

    public class EffectTypeGeneratorWindow : EditorWindow
    {
        private EffectTypeGeneratorSettingsSO _settings;
        private FXGenerationResult            _lastResult;
        private Vector2                       _scroll;
        private bool _fCat = true, _fType = true;

        [MenuItem("MidManStudio/Utilities/Effect Type Generator", priority = 102)]
        public static void Open()
        {
            var w = GetWindow<EffectTypeGeneratorWindow>("Effect Type Generator");
            w.minSize = new Vector2(500, 480);
        }

        private void OnEnable() => _settings = EffectTypeGeneratorCore.FindSettings();

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("MidManStudio — Effect Type Generator", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Generates EffectCategory.cs and EffectType.cs from provider assets.", EditorStyles.wordWrappedMiniLabel);
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

            _settings = (EffectTypeGeneratorSettingsSO)EditorGUILayout.ObjectField(
                "Generator Settings", _settings, typeof(EffectTypeGeneratorSettingsSO), false);

            if (_settings == null)
            {
                EditorGUILayout.HelpBox("No EffectTypeGeneratorSettings found.\nCreate one: MidManStudio > Utilities > Effect Type Generator Settings", MessageType.Warning);
                if (GUILayout.Button("Create Default Settings")) CreateDefaultSettings();
            }
            else
            {
                var so = new SerializedObject(_settings); so.Update();
                EditorGUILayout.PropertyField(so.FindProperty("categoryEnumOutputPath"), new GUIContent("Category Enum Output"));
                EditorGUILayout.PropertyField(so.FindProperty("typeEnumOutputPath"),     new GUIContent("Type Enum Output"));
                EditorGUILayout.PropertyField(so.FindProperty("lockFilePath"),           new GUIContent("Lock File"));
                EditorGUILayout.PropertyField(so.FindProperty("minimumBlockSize"),       new GUIContent("Min Block Size"));
                EditorGUILayout.PropertyField(so.FindProperty("generatedNamespace"),     new GUIContent("Namespace"));
                EditorGUILayout.PropertyField(so.FindProperty("autoGenerateOnAssetChange"), new GUIContent("Auto-Generate on Change"));
                so.ApplyModifiedProperties();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawProviders()
        {
            EditorGUILayout.LabelField("Discovered Providers", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            DrawProviderGroup<EffectCategoryProviderSO>("Effect Categories", ref _fCat,
                a => (a.packageId, a.displayName, a.priority, a.EntryCount));
            EditorGUILayout.Space(4);
            DrawProviderGroup<EffectTypeProviderSO>("Effect Types", ref _fType,
                a => (a.packageId, a.displayName, a.priority, a.EntryCount));

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Create providers for your game:", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Category Provider")) CreateProvider<EffectCategoryProviderSO>("EffectCategoryProvider_MyGame.asset");
            if (GUILayout.Button("+ Type Provider"))     CreateProvider<EffectTypeProviderSO>("EffectTypeProvider_MyGame.asset");
            EditorGUILayout.EndHorizontal();
        }

        private void DrawProviderGroup<T>(string label, ref bool fold,
            Func<T, (string id, string display, int pri, int count)> extract) where T : ScriptableObject
        {
            var guids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
            fold = EditorGUILayout.Foldout(fold, $"{label}  ({guids.Length} provider(s))", true);
            if (!fold) return;
            if (guids.Length == 0) { EditorGUILayout.HelpBox($"No {typeof(T).Name} assets found.", MessageType.Info); return; }

            foreach (var g in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(g));
                if (asset == null) continue;
                var (id, display, pri, count) = extract(asset);
                EditorGUILayout.BeginHorizontal();
                var old = GUI.contentColor;
                GUI.contentColor = pri == 0 ? new Color(0.4f, 0.8f, 0.4f) : pri <= 10 ? new Color(0.4f, 0.6f, 1f) : new Color(0.8f, 0.8f, 0.8f);
                EditorGUILayout.LabelField($"[{pri:D3}]", GUILayout.Width(36));
                GUI.contentColor = old;
                EditorGUILayout.LabelField($"{display}  ({id})  — {count} entries", EditorStyles.miniLabel);
                if (GUILayout.Button("Select", GUILayout.Width(50))) Selection.activeObject = asset;
                if (GUILayout.Button("Ping",   GUILayout.Width(40))) EditorGUIUtility.PingObject(asset);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = _settings != null;
            var old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.3f, 0.85f, 0.3f);
            if (GUILayout.Button("⚙  Generate Now", GUILayout.Height(36)))
            {
                _lastResult = EffectTypeGeneratorCore.Generate(_settings);
                if (_lastResult.Success)
                    EditorUtility.DisplayDialog("Effect Type Generator",
                        $"Done!\nCategory blocks: {_lastResult.CategoryBlocksWritten}\n" +
                        $"Type blocks: {_lastResult.TypeBlocksWritten}", "OK");
            }
            GUI.backgroundColor = old; GUI.enabled = true;
            if (GUILayout.Button("  Open Output Folder", GUILayout.Height(36)))
            {
                var dir = _settings != null ? Path.GetDirectoryName(_settings.categoryEnumOutputPath) : "packages/com.midmanstudio.utilities/Runtime/FXSystems/Generated";
                EditorUtility.RevealInFinder(string.IsNullOrEmpty(dir) ? "Assets" : dir);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("To add custom categories or effect types:\n  1. Click + Category / + Type Provider above.\n  2. Set your packageId (e.g. com.mygame), priority ≥ 100, and add entry names.\n  3. Click Generate Now.", MessageType.None);
        }

        private void DrawResults()
        {
            if (_lastResult == null) return;
            EditorGUILayout.LabelField("Last Result", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (_lastResult.Success) EditorGUILayout.HelpBox($"✓ Category blocks: {_lastResult.CategoryBlocksWritten}  Type blocks: {_lastResult.TypeBlocksWritten}", MessageType.Info);
            foreach (var w in _lastResult.Warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
            foreach (var e in _lastResult.Errors)   EditorGUILayout.HelpBox(e, MessageType.Error);
            EditorGUILayout.EndVertical();
        }

        private void CreateDefaultSettings()
        {
            const string dir = "Assets/MidManStudio/Generated";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var asset = ScriptableObject.CreateInstance<EffectTypeGeneratorSettingsSO>();
            AssetDatabase.CreateAsset(asset, dir + "/EffectTypeGeneratorSettings.asset");
            AssetDatabase.SaveAssets(); _settings = asset;
            Selection.activeObject = asset; EditorGUIUtility.PingObject(asset);
        }

        private static void CreateProvider<T>(string fileName) where T : ScriptableObject
        {
            const string dir = "Assets/MidManStudio/Generated/MyProviders";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, $"{dir}/{fileName}");
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            Selection.activeObject = asset; EditorGUIUtility.PingObject(asset);
        }
    }

    // ── Auto-generate hook ────────────────────────────────────────────────────

    internal class FXTypeAssetPostprocessor : AssetPostprocessor
    {
        private static readonly HashSet<string> Watched = new() { "EffectCategoryProviderSO", "EffectTypeProviderSO", "EffectTypeGeneratorSettingsSO" };

        private static void OnPostprocessAllAssets(string[] imp, string[] del, string[] mov, string[] movFrom)
        {
            bool relevant = imp.Concat(del).Concat(mov).Any(path =>
            {
                if (!path.EndsWith(".asset")) return false;
                var t = AssetDatabase.GetMainAssetTypeAtPath(path);
                return t != null && Watched.Contains(t.Name);
            });
            if (!relevant) return;
            var settings = EffectTypeGeneratorCore.FindSettings();
            if (settings == null || !settings.autoGenerateOnAssetChange) return;
            EditorApplication.delayCall += () => { var r = EffectTypeGeneratorCore.Generate(settings); if (r.HasErrors) foreach (var e in r.Errors) Debug.LogError($"[FXTypeGen Auto] {e}"); };
        }
    }
}
#endif
