// packages/com.midmanstudio.projectilesystem/Editor/ProjectileConfigGenerator.cs
// Editor-only. Scans all ProjectileConfigProviderSO assets, assigns stable enum
// values (lock-file backed), writes the ProjectileConfigType enum, and
// auto-generates/updates the ProjectileConfigMappingSO asset.
//
// FIX: [pinned] tag is now always emitted inside a // comment.
//      Previously, when Comment was empty and WasPinned=true, the tag was
//      appended without a // prefix, producing invalid C#:
//          Default = 0, [pinned]      ← compile error
//      Now always emits:
//          Default = 0,               (no pin, no comment)
//          Default = 0, // [pinned]   (pin only)
//          Default = 0, // my note    (comment only)
//          Default = 0, // my note [pinned]  (both)
//
// USAGE: MidManStudio > Projectile System > Config Type Generator → Generate Now

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.EditorUtils
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Internal data types
    // ─────────────────────────────────────────────────────────────────────────

    internal class ConfigProviderData
    {
        public string PackageId;
        public string DisplayName;
        public int    Priority;
        public List<(string name, ProjectileConfigSO so, string comment, int offset)> Entries;
    }

    internal class ConfigResolvedBlock
    {
        public string PackageId;
        public string DisplayName;
        public int    Priority;
        public int    BlockStart;
        public int    BlockSize;
        public List<ConfigResolvedEntry> Entries = new();
    }

    internal class ConfigResolvedEntry
    {
        public string             EnumName;
        public ProjectileConfigSO ConfigSO;
        public int                Value;
        public string             Comment;
        public bool               WasPinned;
    }

    [Serializable]
    internal class ConfigLockFile
    {
        public List<ConfigLockEntry> entries = new();
    }

    [Serializable]
    internal class ConfigLockEntry
    {
        public string packageId;
        public string enumName;
        public int    value;
    }

    public class ConfigGenerationResult
    {
        public bool         Success;
        public int          BlocksWritten;
        public int          EntriesWritten;
        public List<string> Errors   = new();
        public List<string> Warnings = new();
        public bool HasErrors => Errors.Count > 0;
        public void AddError(string m)   => Errors.Add(m);
        public void AddWarning(string m) => Warnings.Add(m);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Generator core  (pure static — no EditorWindow dependency)
    // ─────────────────────────────────────────────────────────────────────────

    public static class ProjectileConfigGeneratorCore
    {
        public static ProjectileConfigGeneratorSettingsSO FindSettings()
        {
            var guids = AssetDatabase.FindAssets("t:ProjectileConfigGeneratorSettingsSO");
            if (guids.Length == 0) return null;
            if (guids.Length > 1)
                Debug.LogWarning("[ProjectileConfigGenerator] Multiple settings assets — using first.");
            return AssetDatabase.LoadAssetAtPath<ProjectileConfigGeneratorSettingsSO>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        public static ConfigGenerationResult Generate(
            ProjectileConfigGeneratorSettingsSO settings)
        {
            var result = new ConfigGenerationResult();

            if (settings == null)
            {
                result.AddError(
                    "No ProjectileConfigGeneratorSettingsSO found. " +
                    "Create via MidManStudio > Projectile System > Config Generator Settings.");
                return result;
            }

            var lockFile  = LoadLockFile(settings.lockFilePath);
            var providers = CollectProviders();

            if (providers.Count == 0)
                result.AddWarning("No ProjectileConfigProviderSO assets found.");

            var blocks = AssignBlocks(
                providers, settings.minimumBlockSize, lockFile.entries, result);

            if (result.HasErrors) return result;

            WriteEnumFile(blocks, settings.enumOutputPath, settings.generatedNamespace);
            UpdateMappingSO(blocks, settings.mappingAssetPath, result);

            result.BlocksWritten  = blocks.Count;
            result.EntriesWritten = blocks.Sum(b => b.Entries.Count);

            UpdateLockEntries(blocks, lockFile.entries);
            SaveLockFile(lockFile, settings.lockFilePath);

            AssetDatabase.Refresh();
            result.Success = true;
            return result;
        }

        // ── Provider collection ───────────────────────────────────────────────

        private static List<ConfigProviderData> CollectProviders()
        {
            var list  = new List<ConfigProviderData>();
            var guids = AssetDatabase.FindAssets("t:ProjectileConfigProviderSO");

            foreach (var g in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<ProjectileConfigProviderSO>(
                    AssetDatabase.GUIDToAssetPath(g));
                if (asset == null) continue;

                var entries = new List<(string, ProjectileConfigSO, string, int)>();
                foreach (var e in asset.entries ?? new())
                {
                    if (string.IsNullOrWhiteSpace(e.enumName) && e.configSO == null)
                        continue;

                    string resolvedName = !string.IsNullOrWhiteSpace(e.enumName)
                        ? e.enumName
                        : SanitiseIdentifier(e.configSO.name);

                    entries.Add((resolvedName, e.configSO, e.comment ?? "", e.explicitOffset));
                }

                list.Add(new ConfigProviderData
                {
                    PackageId   = asset.packageId,
                    DisplayName = asset.displayName,
                    Priority    = asset.priority,
                    Entries     = entries
                });
            }
            return list;
        }

        // ── Block assignment ──────────────────────────────────────────────────

        private static List<ConfigResolvedBlock> AssignBlocks(
            List<ConfigProviderData> providers,
            int                      minBlock,
            List<ConfigLockEntry>    lockEntries,
            ConfigGenerationResult   result)
        {
            var sorted = providers
                .OrderBy(p => p.Priority).ThenBy(p => p.PackageId)
                .ToList();

            // Duplicate package ID
            foreach (var d in sorted.GroupBy(p => p.PackageId).Where(g => g.Count() > 1))
                result.AddError($"Duplicate packageId '{d.Key}' — each provider must be unique.");

            // Duplicate enum names globally
            var allNames = sorted.SelectMany(p => p.Entries.Select(e => e.name)).ToList();
            foreach (var d in allNames.GroupBy(x => x).Where(g => g.Count() > 1))
                result.AddError($"Duplicate enum name '{d.Key}' — all names must be globally unique.");

            // Identifier validation
            foreach (var p in sorted)
                foreach (var (name, _, _, _) in p.Entries)
                    if (!IsValidIdentifier(name))
                        result.AddError($"'{name}' in '{p.PackageId}' is not a valid C# identifier.");

            if (result.HasErrors) return null;

            var blocks = new List<ConfigResolvedBlock>();
            int cursor  = 0;

            foreach (var p in sorted)
            {
                int n         = p.Entries.Count;
                int blockSize = n == 0
                    ? minBlock
                    : (int)Math.Ceiling((double)n / minBlock) * minBlock;
                blockSize = Math.Max(blockSize, minBlock);

                var entries = ResolveEntries(
                    p.PackageId, p.Entries, cursor, blockSize, lockEntries, result);
                if (result.HasErrors) return null;

                blocks.Add(new ConfigResolvedBlock
                {
                    PackageId   = p.PackageId,
                    DisplayName = p.DisplayName,
                    Priority    = p.Priority,
                    BlockStart  = cursor,
                    BlockSize   = blockSize,
                    Entries     = entries
                });
                cursor += blockSize;
            }

            return blocks;
        }

        private static List<ConfigResolvedEntry> ResolveEntries(
            string packageId,
            List<(string name, ProjectileConfigSO so, string comment, int offset)> raw,
            int blockStart, int blockSize,
            List<ConfigLockEntry> lockEntries,
            ConfigGenerationResult result)
        {
            var resolved = new List<ConfigResolvedEntry>();
            var slotMap  = new Dictionary<int, string>();

            // Pass 1: pinned offsets
            foreach (var (name, so, comment, offset) in raw)
            {
                if (offset < 0) continue;
                if (offset >= blockSize)
                { result.AddError($"'{name}' pins to offset {offset} >= block size {blockSize}."); return null; }
                int abs = blockStart + offset;
                if (slotMap.ContainsKey(abs))
                { result.AddError($"'{name}' and '{slotMap[abs]}' both pin to offset {offset}."); return null; }
                slotMap[abs] = name;
                resolved.Add(new ConfigResolvedEntry { EnumName = name, ConfigSO = so, Value = abs, Comment = comment, WasPinned = true });
            }

            // Pass 2: auto-assign
            int autoSlot = blockStart;
            foreach (var (name, so, comment, offset) in raw)
            {
                if (offset >= 0) continue;
                var locked = lockEntries.FirstOrDefault(l => l.packageId == packageId && l.enumName == name);
                int target;

                if (locked != null &&
                    locked.value >= blockStart &&
                    locked.value < blockStart + blockSize &&
                    !slotMap.ContainsKey(locked.value))
                {
                    target = locked.value;
                }
                else
                {
                    while (slotMap.ContainsKey(autoSlot) && autoSlot < blockStart + blockSize)
                        autoSlot++;
                    if (autoSlot >= blockStart + blockSize)
                    { result.AddError($"Provider '{packageId}' overflowed block (size {blockSize})."); return null; }
                    target = autoSlot++;
                }

                if (locked != null && locked.value != target)
                    result.AddWarning($"'{name}' in '{packageId}' changed value {locked.value} → {target}.");

                slotMap[target] = name;
                resolved.Add(new ConfigResolvedEntry { EnumName = name, ConfigSO = so, Value = target, Comment = comment, WasPinned = false });
            }

            resolved.Sort((a, b) => a.Value.CompareTo(b.Value));
            return resolved;
        }

        // ── Enum file writer ──────────────────────────────────────────────────

        private static void WriteEnumFile(
            List<ConfigResolvedBlock> blocks, string outputPath, string ns)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// AUTO-GENERATED by MidManStudio Projectile Config Type Generator.");
            sb.AppendLine("// DO NOT edit manually.");
            sb.AppendLine("// Regenerate via: MidManStudio > Projectile System > Config Type Generator");
            sb.AppendLine($"// Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Named identifiers for registered ProjectileConfigSO assets.");
            sb.AppendLine("    /// AUTO-GENERATED — do not edit manually.");
            sb.AppendLine("    /// Use ProjectileConfigManager.Instance.GetConfigId((int)value)");
            sb.AppendLine("    /// or the system.Fire((int)value, ...) extension method.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public enum ProjectileConfigType");
            sb.AppendLine("    {");

            for (int b = 0; b < blocks.Count; b++)
            {
                var blk      = blocks[b];
                int blockEnd = blk.BlockStart + blk.BlockSize - 1;
                sb.AppendLine(
                    $"        // ── {blk.DisplayName}  [{blk.PackageId}]" +
                    $"  (block {blk.BlockStart}–{blockEnd})" +
                    $"  priority={blk.Priority}  ──────────────────────────");

                if (blk.Entries.Count == 0)
                    sb.AppendLine("        // (no entries defined)");
                else
                    foreach (var e in blk.Entries)
                    {
                        // FIX: always emit the trailing text inside a // comment.
                        // Previously: pin = " [pinned]" was appended without //,
                        // producing invalid syntax when Comment was empty.
                        string pinTag  = e.WasPinned ? "[pinned]" : "";
                        string comment = e.Comment?.Trim() ?? "";
                        string cmt;
                        if (comment.Length > 0 && pinTag.Length > 0)
                            cmt = $" // {comment} {pinTag}";
                        else if (comment.Length > 0)
                            cmt = $" // {comment}";
                        else if (pinTag.Length > 0)
                            cmt = $" // {pinTag}";
                        else
                            cmt = "";
                        sb.AppendLine($"        {e.EnumName} = {e.Value},{cmt}");
                    }

                if (b < blocks.Count - 1) sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            EnsureDirectory(outputPath);
            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            Debug.Log($"[ProjectileConfigGenerator] Wrote ProjectileConfigType → {outputPath}");
        }

        // ── Mapping SO ────────────────────────────────────────────────────────

        private static void UpdateMappingSO(
            List<ConfigResolvedBlock> blocks,
            string mappingPath,
            ConfigGenerationResult result)
        {
            int maxValue = -1;
            foreach (var blk in blocks)
                foreach (var e in blk.Entries)
                    if (e.Value > maxValue) maxValue = e.Value;

            if (maxValue < 0) { result.AddWarning("No entries — mapping SO not updated."); return; }

            var configs = new ProjectileConfigSO[maxValue + 1];
            foreach (var blk in blocks)
                foreach (var e in blk.Entries)
                    configs[e.Value] = e.ConfigSO;

            var mapping = AssetDatabase.LoadAssetAtPath<ProjectileConfigMappingSO>(mappingPath);
            if (mapping == null)
            {
                EnsureDirectory(mappingPath);
                mapping = ScriptableObject.CreateInstance<ProjectileConfigMappingSO>();
                AssetDatabase.CreateAsset(mapping, mappingPath);
            }

            mapping.SetConfigs(configs);
            EditorUtility.SetDirty(mapping);
            AssetDatabase.SaveAssets();
            Debug.Log(
                $"[ProjectileConfigGenerator] Updated ProjectileConfigMappingSO " +
                $"({configs.Length} slots) → {mappingPath}");
        }

        // ── Lock file ─────────────────────────────────────────────────────────

        private static ConfigLockFile LoadLockFile(string path)
        {
            if (!File.Exists(path)) return new ConfigLockFile();
            try { return JsonUtility.FromJson<ConfigLockFile>(File.ReadAllText(path)) ?? new ConfigLockFile(); }
            catch { Debug.LogWarning("[ProjectileConfigGenerator] Lock file unreadable — starting fresh."); return new ConfigLockFile(); }
        }

        private static void SaveLockFile(ConfigLockFile lf, string path)
        {
            EnsureDirectory(path);
            File.WriteAllText(path, JsonUtility.ToJson(lf, prettyPrint: true), Encoding.UTF8);
        }

        private static void UpdateLockEntries(List<ConfigResolvedBlock> blocks, List<ConfigLockEntry> entries)
        {
            entries.Clear();
            foreach (var blk in blocks)
                foreach (var e in blk.Entries)
                    entries.Add(new ConfigLockEntry { packageId = blk.PackageId, enumName = e.EnumName, value = e.Value });
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        internal static void EnsureDirectory(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        internal static string SanitiseIdentifier(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Config";
            var sb = new StringBuilder();
            foreach (char c in raw)
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('_');
            string s = sb.ToString();
            if (s.Length > 0 && char.IsDigit(s[0])) s = "_" + s;
            return string.IsNullOrEmpty(s) ? "Config" : s;
        }

        private static readonly HashSet<string> CSharpKeywords = new()
        {
            "abstract","as","base","bool","break","byte","case","catch","char","checked",
            "class","const","continue","decimal","default","delegate","do","double","else",
            "enum","event","explicit","extern","false","finally","fixed","float","for",
            "foreach","goto","if","implicit","in","int","interface","internal","is","lock",
            "long","namespace","new","null","object","operator","out","override","params",
            "private","protected","public","readonly","ref","return","sbyte","sealed",
            "short","sizeof","stackalloc","static","string","struct","switch","this",
            "throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort",
            "using","virtual","void","volatile","while"
        };

        internal static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!char.IsLetter(name[0]) && name[0] != '_') return false;
            foreach (char c in name)
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return !CSharpKeywords.Contains(name);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Editor Window
    // ─────────────────────────────────────────────────────────────────────────

    public class ProjectileConfigGeneratorWindow : EditorWindow
    {
        private ProjectileConfigGeneratorSettingsSO _settings;
        private ConfigGenerationResult              _lastResult;
        private Vector2                             _scroll;
        private bool                                _showProviders = true;

        [MenuItem("MidManStudio/Projectile System/Config Type Generator", priority = 60)]
        public static void Open()
        {
            var w = GetWindow<ProjectileConfigGeneratorWindow>("Config Type Generator");
            w.minSize = new Vector2(520, 500);
        }

        private void OnEnable()
            => _settings = ProjectileConfigGeneratorCore.FindSettings();

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(
                "MidManStudio — Projectile Config Type Generator",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Discovers all ProjectileConfigProviderSO assets and writes\n" +
                "ProjectileConfigType.cs enum + ProjectileConfigMapping.asset.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(6);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawSettings();
            EditorGUILayout.Space(6);
            DrawProviders();
            EditorGUILayout.Space(6);
            DrawActions();
            EditorGUILayout.Space(6);
            DrawResults();
            EditorGUILayout.EndScrollView();
        }

        // ── Settings ──────────────────────────────────────────────────────────

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            _settings = (ProjectileConfigGeneratorSettingsSO)EditorGUILayout.ObjectField(
                "Settings Asset", _settings,
                typeof(ProjectileConfigGeneratorSettingsSO), false);

            if (_settings == null)
            {
                EditorGUILayout.HelpBox(
                    "No settings found.\n" +
                    "Create: MidManStudio > Projectile System > Config Generator Settings\n" +
                    "Or run: MidManStudio > Projectile System > Internal > Recreate Default Config Assets",
                    MessageType.Warning);
                if (GUILayout.Button("Create Default Settings")) CreateDefaultSettings();
            }
            else
            {
                var so = new SerializedObject(_settings);
                so.Update();
                EditorGUILayout.PropertyField(so.FindProperty("enumOutputPath"),
                    new GUIContent("Enum Output (.cs)"));
                EditorGUILayout.PropertyField(so.FindProperty("mappingAssetPath"),
                    new GUIContent("Mapping Asset (.asset)"));
                EditorGUILayout.PropertyField(so.FindProperty("lockFilePath"),
                    new GUIContent("Lock File (.json)"));
                EditorGUILayout.PropertyField(so.FindProperty("minimumBlockSize"),
                    new GUIContent("Min Block Size"));
                EditorGUILayout.PropertyField(so.FindProperty("generatedNamespace"),
                    new GUIContent("Namespace"));
                EditorGUILayout.PropertyField(so.FindProperty("autoGenerateOnAssetChange"),
                    new GUIContent("Auto-Generate on Change"));
                so.ApplyModifiedProperties();
            }

            EditorGUILayout.EndVertical();
        }

        // ── Providers list ────────────────────────────────────────────────────

        private void DrawProviders()
        {
            var guids = AssetDatabase.FindAssets("t:ProjectileConfigProviderSO");

            _showProviders = EditorGUILayout.Foldout(
                _showProviders, $"Discovered Providers  ({guids.Length})", true);

            if (!_showProviders) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (guids.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No ProjectileConfigProviderSO assets found. " +
                    "Run MidManStudio > Projectile System > Internal > Recreate Default Config Assets " +
                    "to create the built-in provider, or create one manually.",
                    MessageType.Info);
            }
            else
            {
                var sorted = guids
                    .Select(g => AssetDatabase.LoadAssetAtPath<ProjectileConfigProviderSO>(
                        AssetDatabase.GUIDToAssetPath(g)))
                    .Where(p => p != null)
                    .OrderBy(p => p.priority).ThenBy(p => p.packageId)
                    .ToList();

                foreach (var p in sorted)
                {
                    EditorGUILayout.BeginHorizontal();
                    var old = GUI.contentColor;
                    GUI.contentColor = p.priority <= 10
                        ? new Color(0.4f, 0.8f, 0.4f) : new Color(0.8f, 0.8f, 0.8f);
                    EditorGUILayout.LabelField($"[{p.priority:D3}]", GUILayout.Width(36));
                    GUI.contentColor = old;
                    EditorGUILayout.LabelField(
                        $"{p.displayName}  ({p.packageId})  — {p.EntryCount} entries",
                        EditorStyles.miniLabel);
                    if (GUILayout.Button("Select", GUILayout.Width(50))) Selection.activeObject = p;
                    if (GUILayout.Button("Ping",   GUILayout.Width(40))) EditorGUIUtility.PingObject(p);
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("+ Create Provider for My Game")) CreateProvider();

            EditorGUILayout.EndVertical();
        }

        // ── Actions ───────────────────────────────────────────────────────────

        private void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = _settings != null;

            var oldBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.3f, 0.85f, 0.3f);
            if (GUILayout.Button("Generate Now", GUILayout.Height(36)))
            {
                _lastResult = ProjectileConfigGeneratorCore.Generate(_settings);
                if (_lastResult.Success)
                    EditorUtility.DisplayDialog(
                        "Config Type Generator",
                        $"Generation complete!\n\n" +
                        $"Blocks:  {_lastResult.BlocksWritten}\n" +
                        $"Entries: {_lastResult.EntriesWritten}\n\n" +
                        "Assign ProjectileConfigMapping.asset to ProjectileConfigManager in the scene.", "OK");
            }
            GUI.backgroundColor = oldBg;
            GUI.enabled = true;

            if (GUILayout.Button("Open Output Folder", GUILayout.Height(36)))
            {
                var dir = _settings != null
                    ? Path.GetDirectoryName(_settings.enumOutputPath) : "Assets";
                EditorUtility.RevealInFinder(string.IsNullOrEmpty(dir) ? "Assets" : dir);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "After generation:\n" +
                "  1. Assign the generated ProjectileConfigMapping.asset to " +
                "ProjectileConfigManager._mapping in the scene.\n" +
                "  2. Use (int)ProjectileConfigType.YourConfig with system.Fire() " +
                "or manager.GetConfigId().",
                MessageType.None);
        }

        // ── Results ───────────────────────────────────────────────────────────

        private void DrawResults()
        {
            if (_lastResult == null) return;
            EditorGUILayout.LabelField("Last Result", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (_lastResult.Success)
                EditorGUILayout.HelpBox(
                    $"Success — {_lastResult.BlocksWritten} blocks, " +
                    $"{_lastResult.EntriesWritten} entries.", MessageType.Info);
            foreach (var w in _lastResult.Warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
            foreach (var e in _lastResult.Errors)   EditorGUILayout.HelpBox(e, MessageType.Error);
            EditorGUILayout.EndVertical();
        }

        // ── Asset creation helpers ────────────────────────────────────────────

        private void CreateDefaultSettings()
        {
            const string dir  = "Assets/MidManStudio/Generated/Projectiles";
            const string path = dir + "/ProjectileConfigGeneratorSettings.asset";
            ProjectileConfigGeneratorCore.EnsureDirectory(path);
            var asset = ScriptableObject.CreateInstance<ProjectileConfigGeneratorSettingsSO>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            _settings = asset;
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        private static void CreateProvider()
        {
            const string dir  = "Assets/MidManStudio/Generated/Projectiles";
            const string path = dir + "/ProjectileConfigProvider_MyGame.asset";
            ProjectileConfigGeneratorCore.EnsureDirectory(path);
            var asset = ScriptableObject.CreateInstance<ProjectileConfigProviderSO>();
            asset.packageId   = "com.mygame";
            asset.displayName = "My Game";
            asset.priority    = 100;
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Auto-generate on asset change
    // ─────────────────────────────────────────────────────────────────────────

    internal class ProjectileConfigAssetPostprocessor : AssetPostprocessor
    {
        private static readonly HashSet<string> WatchedTypes = new()
        {
            "ProjectileConfigProviderSO",
            "ProjectileConfigGeneratorSettingsSO"
        };

        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted, string[] moved, string[] _)
        {
            bool relevant = imported.Concat(deleted).Concat(moved).Any(p =>
            {
                if (!p.EndsWith(".asset")) return false;
                var t = AssetDatabase.GetMainAssetTypeAtPath(p);
                return t != null && WatchedTypes.Contains(t.Name);
            });

            if (!relevant) return;

            var settings = ProjectileConfigGeneratorCore.FindSettings();
            if (settings == null || !settings.autoGenerateOnAssetChange) return;

            EditorApplication.delayCall += () =>
            {
                var r = ProjectileConfigGeneratorCore.Generate(settings);
                if (r.HasErrors)
                    foreach (var e in r.Errors) Debug.LogError($"[ProjectileConfigGenerator Auto] {e}");
                else
                    Debug.Log("[ProjectileConfigGenerator Auto] Regenerated successfully.");
            };
        }
    }
}
#endif
