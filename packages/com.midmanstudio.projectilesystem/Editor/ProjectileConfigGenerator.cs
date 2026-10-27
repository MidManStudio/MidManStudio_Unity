// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "ProjectileConfigGenerator.cs"
// ============================================================================
// Editor-only. Scans all ProjectileConfigProviderSO assets, assigns stable enum
// values (lock-file backed), writes the ProjectileConfigType enum, and
// auto-generates/updates the ProjectileConfigMappingSO asset.
// USAGE: MidManStudio > Projectile System > Config Type Generator → Generate Now

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.EditorUtils.Generators;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.EditorUtils
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Lock file
    // ─────────────────────────────────────────────────────────────────────────

    // Three lists: the numbers in use, the block ranges, and the numbers of removed entries.
    // A lock file written before blocks and retired numbers existed only has `entries`.
    // The other two lists stay empty when it is read. Entries use the field name `enumName`.
    [Serializable]
    internal class ConfigLockFile
    {
        public List<ConfigLockEntry> entries = new();
        public List<EnumLockBlock>   blocks  = new();
        public List<ConfigLockEntry> retired = new();

        // JsonUtility can leave a list null when the file has the key with no value.
        public void Normalize()
        {
            entries ??= new();
            blocks  ??= new();
            retired ??= new();
        }

        public EnumLockSection ToSection()
        {
            var s = new EnumLockSection { blocks = blocks };
            foreach (var e in entries) if (e != null) s.entries.Add(ToLogic(e));
            foreach (var e in retired) if (e != null) s.retired.Add(ToLogic(e));
            return s;
        }

        public void SetFrom(EnumLockSection s)
        {
            blocks  = s.blocks;
            entries = s.entries.Select(FromLogic).ToList();
            retired = s.retired.Select(FromLogic).ToList();
        }

        private static EnumLockEntry ToLogic(ConfigLockEntry e) =>
            new EnumLockEntry { packageId = e.packageId, name = e.enumName, value = e.value };

        private static ConfigLockEntry FromLogic(EnumLockEntry e) =>
            new ConfigLockEntry { packageId = e.packageId, enumName = e.name, value = e.value };
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

            var lockFile = LoadLockFile(settings.lockFilePath, result);
            if (lockFile == null) return result;

            var providers = CollectProviders(out var configByName);

            if (providers.Count == 0)
                result.AddWarning("No ProjectileConfigProviderSO assets found.");

            // The numbers are worked out before any file is written, so an error leaves the
            // enum file, the mapping asset and the lock file as they were.
            var blocks = EnumBlockLogic.Assign(
                providers, settings.minimumBlockSize, lockFile.ToSection(),
                result.Errors, result.Warnings, "Config", out var newLock);

            if (result.HasErrors) return result;

            try
            {
                WriteEnumFile(blocks, settings.enumOutputPath, settings.generatedNamespace);
                UpdateMappingSO(blocks, configByName, settings.mappingAssetPath, result);

                lockFile.SetFrom(newLock);
                SaveLockFile(lockFile, settings.lockFilePath);
            }
            catch (Exception e)
            {
                result.AddError("Could not write the generated files: " + e.Message);
                return result;
            }

            result.BlocksWritten  = blocks.Count;
            result.EntriesWritten = blocks.Sum(b => b.Entries.Count);

            AssetDatabase.Refresh();
            result.Success = true;
            return result;
        }

        // ── Provider collection ───────────────────────────────────────────────

        // Each entry becomes an enum name. The config asset behind a name is returned in
        // configByName, because the numbering only deals with names.
        private static List<EnumProviderData> CollectProviders(
            out Dictionary<string, ProjectileConfigSO> configByName)
        {
            var list  = new List<EnumProviderData>();
            configByName = new Dictionary<string, ProjectileConfigSO>(StringComparer.Ordinal);
            var guids = AssetDatabase.FindAssets("t:ProjectileConfigProviderSO");

            foreach (var g in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<ProjectileConfigProviderSO>(
                    AssetDatabase.GUIDToAssetPath(g));
                if (asset == null) continue;

                var data = new EnumProviderData
                {
                    PackageId   = asset.packageId,
                    DisplayName = asset.displayName,
                    Priority    = asset.priority
                };

                foreach (var e in asset.entries ?? new())
                {
                    if (e == null) continue;
                    if (string.IsNullOrWhiteSpace(e.enumName) && e.configSO == null)
                        continue;

                    string resolvedName = (!string.IsNullOrWhiteSpace(e.enumName)
                        ? e.enumName
                        : SanitiseIdentifier(e.configSO.name)).Trim();

                    configByName[resolvedName] = e.configSO;
                    data.Entries.Add(new EnumEntryData(resolvedName, e.comment ?? "", e.explicitOffset));
                }

                list.Add(data);
            }
            return list;
        }

        // ── Enum file writer ──────────────────────────────────────────────────

        private static void WriteEnumFile(
            List<EnumResolvedBlock> blocks, string outputPath, string ns)
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
                    $"        // ── {EnumBlockLogic.OneLine(blk.DisplayName)}  [{EnumBlockLogic.OneLine(blk.PackageId)}]" +
                    $"  (block {blk.BlockStart}–{blockEnd})" +
                    $"  priority={blk.Priority}  ──────────────────────────");

                if (blk.Entries.Count == 0)
                    sb.AppendLine("        // (no entries defined)");
                else
                    foreach (var e in blk.Entries)
                    {
                        // Everything after the value goes inside a // comment, so an entry
                        // with no comment text and a pin still compiles.
                        string pinTag  = e.Pinned ? "[pinned]" : "";
                        string comment = EnumBlockLogic.OneLine(e.Comment);
                        string cmt;
                        if (comment.Length > 0 && pinTag.Length > 0)
                            cmt = $" // {comment} {pinTag}";
                        else if (comment.Length > 0)
                            cmt = $" // {comment}";
                        else if (pinTag.Length > 0)
                            cmt = $" // {pinTag}";
                        else
                            cmt = "";
                        sb.AppendLine($"        {e.Name} = {e.Value},{cmt}");
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

        // The mapping asset is an array indexed by enum value. Numbers that no entry uses
        // (the rest of a block, numbers of removed entries) stay null.
        private static void UpdateMappingSO(
            List<EnumResolvedBlock> blocks,
            Dictionary<string, ProjectileConfigSO> configByName,
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
                    configs[e.Value] = configByName[e.Name];

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

        private static ConfigLockFile LoadLockFile(string path, ConfigGenerationResult result)
        {
            if (!File.Exists(path)) return new ConfigLockFile();
            try
            {
                var file = JsonUtility.FromJson<ConfigLockFile>(File.ReadAllText(path));
                if (file != null) { file.Normalize(); return file; }
            }
            catch (Exception)
            {
                // Falls through to the error below.
            }
            // Starting over would renumber every projectile config, and the numbers are stored
            // in the mapping asset and in scenes, so stop and let the owner decide.
            result.AddError($"The lock file '{path}' could not be read. Fix it or delete it, then generate again. " +
                            "Deleting it lets all values be assigned again.");
            return null;
        }

        private static void SaveLockFile(ConfigLockFile lf, string path)
        {
            EnsureDirectory(path);
            File.WriteAllText(path, JsonUtility.ToJson(lf, prettyPrint: true), Encoding.UTF8);
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
                // A warning can mean a number changed, so it is never left out of the console.
                foreach (var w in r.Warnings) Debug.LogWarning($"[ProjectileConfigGenerator Auto] {w}");
                if (r.HasErrors)
                    foreach (var e in r.Errors) Debug.LogError($"[ProjectileConfigGenerator Auto] {e}");
                else
                    Debug.Log("[ProjectileConfigGenerator Auto] Regenerated successfully.");
            };
        }
    }
}
#endif
