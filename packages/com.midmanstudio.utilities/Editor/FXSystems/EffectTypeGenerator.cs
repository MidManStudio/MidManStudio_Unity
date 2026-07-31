// Generates EffectCategory.cs and EffectType.cs from provider assets.
// Follows the same block/priority/lock pattern as PoolTypeGenerator.
// Open via: MidManStudio > Utilities > Effect Type Generator

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
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

    // ── Internal data ─────────────────────────────────────────────────────────

    internal class FXProviderData
    {
        public string PackageId, DisplayName;
        public int    Priority;
        public List<(string name, string comment, int offset)> Entries;
    }

    internal class FXResolvedBlock
    {
        public string PackageId, DisplayName;
        public int    Priority, BlockStart, BlockSize;
        public List<(string Name, int Value, string Comment, bool Pinned)> Entries = new();
    }

    // ── Lock file ─────────────────────────────────────────────────────────────

    [Serializable]
    internal class FXLockFile
    {
        public List<FXLockEntry> categoryEntries = new();
        public List<FXLockEntry> typeEntries     = new();
    }

    [Serializable]
    internal class FXLockEntry
    {
        public string packageId, name;
        public int    value;
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

            var lockFile = LoadLock(settings.lockFilePath);

            // ── Category enum ─────────────────────────────────────────────────
            {
                var providers = Collect<EffectCategoryProviderSO>(a => (a.packageId, a.displayName, a.priority, a.entries.Select(e => (e.entryName, e.comment, e.explicitOffset)).ToList()));
                var blocks    = AssignBlocks(providers, settings.minimumBlockSize, lockFile.categoryEntries, result, "Category");
                if (!result.HasErrors)
                {
                    WriteEnum(blocks, settings.categoryEnumOutputPath, settings.generatedNamespace, "EffectCategory",
                        "Effect category IDs. AUTO-GENERATED — do not edit manually.");
                    UpdateLock(blocks, lockFile.categoryEntries);
                    result.CategoryBlocksWritten = blocks.Count;
                }
            }
            if (result.HasErrors) return result;

            // ── Type enum ─────────────────────────────────────────────────────
            {
                var providers = Collect<EffectTypeProviderSO>(a => (a.packageId, a.displayName, a.priority, a.entries.Select(e => (e.entryName, e.comment, e.explicitOffset)).ToList()));
                var blocks    = AssignBlocks(providers, settings.minimumBlockSize, lockFile.typeEntries, result, "Type");
                if (!result.HasErrors)
                {
                    WriteEnum(blocks, settings.typeEnumOutputPath, settings.generatedNamespace, "EffectType",
                        "Specific effect variant IDs. AUTO-GENERATED — do not edit manually.");
                    UpdateLock(blocks, lockFile.typeEntries);
                    result.TypeBlocksWritten = blocks.Count;
                }
            }

            if (!result.HasErrors)
            {
                SaveLock(lockFile, settings.lockFilePath);
                AssetDatabase.Refresh();
                result.Success = true;
            }
            return result;
        }

        // ── Provider collection ───────────────────────────────────────────────

        private static List<FXProviderData> Collect<T>(
            Func<T, (string id, string display, int pri, List<(string, string, int)> entries)> extract)
            where T : ScriptableObject
        {
            var list  = new List<FXProviderData>();
            var guids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
            foreach (var g in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(g));
                if (asset == null) continue;
                var (id, display, pri, entries) = extract(asset);
                list.Add(new FXProviderData { PackageId = id, DisplayName = display, Priority = pri, Entries = entries });
            }
            return list;
        }

        // ── Block assignment — identical algorithm to PoolTypeGeneratorCore ───

        private static List<FXResolvedBlock> AssignBlocks(
            List<FXProviderData> providers, int minBlock,
            List<FXLockEntry> lockEntries, FXGenerationResult result, string kind)
        {
            var sorted = providers.OrderBy(p => p.Priority).ThenBy(p => p.PackageId).ToList();

            // Duplicate package ID check
            foreach (var d in sorted.GroupBy(p => p.PackageId).Where(g => g.Count() > 1).Select(g => g.Key))
                result.AddError($"[{kind}] Duplicate packageId '{d}'.");
            if (result.HasErrors) return null;

            // Duplicate entry name check
            foreach (var d in sorted.SelectMany(p => p.Entries.Select(e => e.name))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key))
                result.AddError($"[{kind}] Duplicate entry name '{d}'.");
            if (result.HasErrors) return null;

            var blocks = new List<FXResolvedBlock>();
            int cursor = 0;

            foreach (var p in sorted)
            {
                int n         = p.Entries.Count;
                int blockSize = Math.Max(minBlock, (int)Math.Ceiling((double)n / minBlock) * minBlock);
                int start     = cursor;
                var entries   = ResolveEntries(p.PackageId, p.Entries, start, blockSize, lockEntries, result, kind);
                if (result.HasErrors) return null;

                blocks.Add(new FXResolvedBlock { PackageId = p.PackageId, DisplayName = p.DisplayName, Priority = p.Priority, BlockStart = start, BlockSize = blockSize, Entries = entries });
                cursor = start + blockSize;
            }
            return blocks;
        }

        private static List<(string Name, int Value, string Comment, bool Pinned)> ResolveEntries(
            string pkg, List<(string name, string comment, int offset)> raw,
            int start, int size, List<FXLockEntry> lockEntries, FXGenerationResult result, string kind)
        {
            var resolved = new List<(string, int, string, bool)>();
            var slotMap  = new Dictionary<int, string>();

            // Pass 1 — pinned
            foreach (var (name, comment, offset) in raw)
            {
                if (offset < 0) continue;
                if (offset >= size) { result.AddError($"[{kind}] '{name}' in '{pkg}' pins to offset {offset} but block size is {size}."); return null; }
                int abs = start + offset;
                if (slotMap.ContainsKey(abs)) { result.AddError($"[{kind}] Collision at offset {offset} in '{pkg}'."); return null; }
                slotMap[abs] = name;
                resolved.Add((name, abs, comment, true));
            }

            // Pass 2 — auto
            int autoSlot = start;
            foreach (var (name, comment, offset) in raw)
            {
                if (offset >= 0) continue;
                var locked = lockEntries.FirstOrDefault(l => l.packageId == pkg && l.name == name);
                int target;
                if (locked != null && locked.value >= start && locked.value < start + size && !slotMap.ContainsKey(locked.value))
                    target = locked.value;
                else
                {
                    while (slotMap.ContainsKey(autoSlot) && autoSlot < start + size) autoSlot++;
                    if (autoSlot >= start + size) { result.AddError($"[{kind}] Block overflow for '{pkg}'."); return null; }
                    target = autoSlot++;
                }
                if (locked != null && locked.value != target)
                    result.AddWarning($"[{kind}] '{name}' in '{pkg}' changed {locked.value} → {target}.");
                slotMap[target] = name;
                resolved.Add((name, target, comment, false));
            }

            resolved.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return resolved;
        }

        // ── File writing ──────────────────────────────────────────────────────

        private static void WriteEnum(List<FXResolvedBlock> blocks, string path,
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
                sb.AppendLine($"        // ── {blk.DisplayName}  [{blk.PackageId}]  (block {blk.BlockStart}–{blk.BlockStart + blk.BlockSize - 1})  priority={blk.Priority}  ──");
                if (blk.Entries.Count == 0) { sb.AppendLine("        // (no entries defined)"); }
                else foreach (var (name, value, comment, pinned) in blk.Entries)
                {
                    string pin  = pinned ? " //[pinned]" : "";
                    string cmt  = string.IsNullOrWhiteSpace(comment) ? pin : $" // {comment}{pin}";
                    sb.AppendLine($"        {name} = {value},{cmt}");
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

        private static FXLockFile LoadLock(string path)
        {
            if (!File.Exists(path)) return new FXLockFile();
            try { return JsonUtility.FromJson<FXLockFile>(File.ReadAllText(path)) ?? new FXLockFile(); }
            catch { Debug.LogWarning("[EffectTypeGenerator] Could not parse lock file — starting fresh."); return new FXLockFile(); }
        }

        private static void SaveLock(FXLockFile lf, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonUtility.ToJson(lf, prettyPrint: true), Encoding.UTF8);
        }

        private static void UpdateLock(List<FXResolvedBlock> blocks, List<FXLockEntry> entries)
        {
            entries.Clear();
            foreach (var b in blocks) foreach (var (name, value, _, _) in b.Entries)
                entries.Add(new FXLockEntry { packageId = b.PackageId, name = name, value = value });
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

    // ── Auto-generate hook -_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-__--__--__--__--__--__--___---___---___---___---___---____----____----____----____----_____-----_____------_____-----

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
