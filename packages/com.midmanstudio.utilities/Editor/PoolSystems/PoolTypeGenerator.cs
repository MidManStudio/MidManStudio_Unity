// PoolTypeGenerator.cs
// Reads all PoolTypeProviderSO / ParticlePoolTypeProviderSO / NetworkPoolTypeProviderSO
// assets found anywhere in the project and writes the three generated enum files.
//
// NetworkPoolTypeProviderSO is loaded via SerializedObject reflection so this
// editor assembly has NO dependency on com.midmanstudio.netcode.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Core.Pools.Generator
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Internal data types
    // ─────────────────────────────────────────────────────────────────────────

    internal class ProviderData
    {
        public string PackageId;
        public string DisplayName;
        public int    Priority;
        public List<(string name, string comment, int offset)> Entries;
    }

    internal class ResolvedBlock
    {
        public string              PackageId;
        public string              DisplayName;
        public int                 Priority;
        public int                 BlockStart;
        public int                 BlockSize;
        public List<ResolvedEntry> Entries = new();
    }

    internal class ResolvedEntry
    {
        public string Name;
        public int    Value;
        public string Comment;
        public bool   WasPinned;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Lock file
    // ─────────────────────────────────────────────────────────────────────────

    [Serializable]
    internal class LockFile
    {
        public List<LockEntry> objectEntries   = new();
        public List<LockEntry> particleEntries = new();
        public List<LockEntry> networkEntries  = new();
    }

    [Serializable]
    internal class LockEntry
    {
        public string packageId;
        public string name;
        public int    value;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Generation result
    // ─────────────────────────────────────────────────────────────────────────

    public class GenerationResult
    {
        public bool         Success;
        public int          ObjectBlocksWritten;
        public int          ParticleBlocksWritten;
        public int          NetworkBlocksWritten;
        public List<string> Errors   = new();
        public List<string> Warnings = new();
        public bool         HasErrors => Errors.Count > 0;

        public void AddError(string msg)   => Errors.Add(msg);
        public void AddWarning(string msg) => Warnings.Add(msg);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Generator core
    // ─────────────────────────────────────────────────────────────────────────

    public static class PoolTypeGeneratorCore
    {
        public static PoolTypeGeneratorSettingsSO FindSettings()
        {
            var guids = AssetDatabase.FindAssets("t:PoolTypeGeneratorSettingsSO");
            if (guids.Length == 0) return null;
            if (guids.Length > 1)
                Debug.LogWarning("[PoolTypeGenerator] Multiple settings assets found — using first.");
            return AssetDatabase.LoadAssetAtPath<PoolTypeGeneratorSettingsSO>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        public static GenerationResult Generate(PoolTypeGeneratorSettingsSO settings)
        {
            var result = new GenerationResult();

            if (settings == null)
            {
                result.AddError(
                    "No PoolTypeGeneratorSettings found. " +
                    "Create one via MidManStudio > Pool Type Generator Settings.");
                return result;
            }

            var lockFile = LoadLockFile(settings.lockFilePath);

            // ── Object pool ───────────────────────────────────────────────────
            {
                var providers = CollectObjectProviders();
                LogProviders("Object", providers);
                var blocks = AssignBlocks(providers, settings.minimumBlockSize,
                    lockFile.objectEntries, result, "Object");

                if (!result.HasErrors)
                {
                    WriteEnumFile(blocks, settings.objectEnumOutputPath,
                        settings.generatedNamespace, "PoolableObjectType",
                        "Object pool type IDs. AUTO-GENERATED — do not edit manually.");
                    UpdateLockEntries(blocks, lockFile.objectEntries);
                    result.ObjectBlocksWritten = blocks.Count;
                }
            }

            if (result.HasErrors) return result;

            // ── Particle pool ─────────────────────────────────────────────────
            {
                var providers = CollectParticleProviders();
                LogProviders("Particle", providers);
                var blocks = AssignBlocks(providers, settings.minimumBlockSize,
                    lockFile.particleEntries, result, "Particle");

                if (!result.HasErrors)
                {
                    WriteEnumFile(blocks, settings.particleEnumOutputPath,
                        settings.generatedNamespace, "PoolableParticleType",
                        "Particle pool type IDs. AUTO-GENERATED — do not edit manually.");
                    UpdateLockEntries(blocks, lockFile.particleEntries);
                    result.ParticleBlocksWritten = blocks.Count;
                }
            }

            if (result.HasErrors) return result;

            // ── Network object pool ───────────────────────────────────────────
            // Loaded via SerializedObject reflection — no netcode assembly reference needed.
            {
                var providers = CollectNetworkProviders();
                LogProviders("NetworkObject", providers);
                var blocks = AssignBlocks(providers, settings.minimumBlockSize,
                    lockFile.networkEntries, result, "NetworkObject");

                if (!result.HasErrors)
                {
                    WriteEnumFile(blocks, settings.networkEnumOutputPath,
                        settings.generatedNamespace, "PoolableNetworkObjectType",
                        "Network object pool type IDs. AUTO-GENERATED — do not edit manually.");
                    UpdateLockEntries(blocks, lockFile.networkEntries);
                    result.NetworkBlocksWritten = blocks.Count;
                }
            }

            if (!result.HasErrors)
            {
                SaveLockFile(lockFile, settings.lockFilePath);
                AssetDatabase.Refresh();
                result.Success = true;
            }

            return result;
        }

        // ── Provider collection ───────────────────────────────────────────────

        private static List<ProviderData> CollectObjectProviders()
        {
            var list  = new List<ProviderData>();
            var guids = AssetDatabase.FindAssets("t:PoolTypeProviderSO");
            foreach (var g in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<PoolTypeProviderSO>(
                    AssetDatabase.GUIDToAssetPath(g));
                if (asset == null) continue;
                list.Add(new ProviderData
                {
                    PackageId   = asset.packageId,
                    DisplayName = asset.displayName,
                    Priority    = asset.priority,
                    Entries     = asset.entries
                        .Select(e => (e.entryName, e.comment, e.explicitOffset))
                        .ToList()
                });
            }
            return list;
        }

        private static List<ProviderData> CollectParticleProviders()
        {
            var list  = new List<ProviderData>();
            var guids = AssetDatabase.FindAssets("t:ParticlePoolTypeProviderSO");
            foreach (var g in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<ParticlePoolTypeProviderSO>(
                    AssetDatabase.GUIDToAssetPath(g));
                if (asset == null) continue;
                list.Add(new ProviderData
                {
                    PackageId   = asset.packageId,
                    DisplayName = asset.displayName,
                    Priority    = asset.priority,
                    Entries     = asset.entries
                        .Select(e => (e.entryName, e.comment, e.explicitOffset))
                        .ToList()
                });
            }
            return list;
        }

        // Reflection-based — no direct reference to NetworkPoolTypeProviderSO type.
        // Works regardless of whether com.midmanstudio.netcode is in the project.
        private static List<ProviderData> CollectNetworkProviders()
        {
            var list  = new List<ProviderData>();
            var guids = AssetDatabase.FindAssets("t:NetworkPoolTypeProviderSO");
            foreach (var g in guids)
            {
                var path  = AssetDatabase.GUIDToAssetPath(g);
                var asset = AssetDatabase.LoadMainAssetAtPath(path) as ScriptableObject;
                if (asset == null) continue;

                var so          = new SerializedObject(asset);
                var packageId   = so.FindProperty("packageId")?.stringValue   ?? "";
                var displayName = so.FindProperty("displayName")?.stringValue ?? asset.name;
                var priority    = so.FindProperty("priority")?.intValue       ?? 100;
                var entriesProp = so.FindProperty("entries");

                var entries = new List<(string name, string comment, int offset)>();
                if (entriesProp != null)
                {
                    for (int i = 0; i < entriesProp.arraySize; i++)
                    {
                        var elem    = entriesProp.GetArrayElementAtIndex(i);
                        var eName   = elem.FindPropertyRelative("entryName")?.stringValue    ?? "";
                        var comment = elem.FindPropertyRelative("comment")?.stringValue      ?? "";
                        var offset  = elem.FindPropertyRelative("explicitOffset")?.intValue  ?? -1;
                        entries.Add((eName, comment, offset));
                    }
                }

                list.Add(new ProviderData
                {
                    PackageId   = packageId,
                    DisplayName = displayName,
                    Priority    = priority,
                    Entries     = entries
                });
            }
            return list;
        }

        private static void LogProviders(string kind, List<ProviderData> providers)
        {
            if (providers.Count == 0)
            {
                Debug.Log($"[PoolTypeGenerator] {kind}: no providers found.");
                return;
            }
            var sb = new StringBuilder($"[PoolTypeGenerator] {kind} providers found:\n");
            foreach (var p in providers.OrderBy(x => x.Priority).ThenBy(x => x.PackageId))
                sb.AppendLine($"  [{p.Priority:D3}] {p.PackageId} — {p.Entries.Count} entries");
            Debug.Log(sb.ToString());
        }

        // ── Block assignment ──────────────────────────────────────────────────

        private static List<ResolvedBlock> AssignBlocks(
            List<ProviderData> providers,
            int                minBlockSize,
            List<LockEntry>    lockEntries,
            GenerationResult   result,
            string             poolKind)
        {
            var sorted = providers
                .OrderBy(p => p.Priority)
                .ThenBy(p => p.PackageId)
                .ToList();

            var dupes = sorted.GroupBy(p => p.PackageId)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            foreach (var d in dupes)
                result.AddError(
                    $"[{poolKind}] Duplicate packageId '{d}'. Each provider must have a unique package ID.");

            if (result.HasErrors) return null;

            var allNames = sorted.SelectMany(p => p.Entries.Select(e => e.name))
                .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var dupeNames = allNames.GroupBy(x => x).Where(g => g.Count() > 1)
                .Select(g => g.Key).ToList();
            foreach (var d in dupeNames)
                result.AddError(
                    $"[{poolKind}] Duplicate entry name '{d}'. All entry names must be globally unique.");

            if (result.HasErrors) return null;

            foreach (var p in sorted)
                foreach (var (name, _, _) in p.Entries)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    { result.AddError($"[{poolKind}] Provider '{p.PackageId}' has an entry with an empty name."); continue; }
                    if (!IsValidIdentifier(name))
                        result.AddError($"[{poolKind}] '{name}' in '{p.PackageId}' is not a valid C# identifier.");
                }

            if (result.HasErrors) return null;

            var blocks = new List<ResolvedBlock>();
            int cursor = 0;

            foreach (var p in sorted)
            {
                int entryCount = p.Entries.Count;
                int blockSize  = entryCount == 0
                    ? minBlockSize
                    : (int)Math.Ceiling((double)entryCount / minBlockSize) * minBlockSize;
                blockSize = Math.Max(blockSize, minBlockSize);

                int blockStart = cursor;
                var entries    = ResolveEntries(p.PackageId, p.Entries,
                    blockStart, blockSize, lockEntries, result, poolKind);
                if (result.HasErrors) return null;

                blocks.Add(new ResolvedBlock
                {
                    PackageId   = p.PackageId,
                    DisplayName = p.DisplayName,
                    Priority    = p.Priority,
                    BlockStart  = blockStart,
                    BlockSize   = blockSize,
                    Entries     = entries
                });
                cursor = blockStart + blockSize;
            }

            return blocks;
        }

        private static List<ResolvedEntry> ResolveEntries(
            string                                           packageId,
            List<(string name, string comment, int offset)> rawEntries,
            int                                              blockStart,
            int                                              blockSize,
            List<LockEntry>                                  lockEntries,
            GenerationResult                                 result,
            string                                           poolKind)
        {
            var resolved = new List<ResolvedEntry>();
            var slotMap  = new Dictionary<int, string>();

            // Pass 1: pinned offsets
            foreach (var (name, comment, offset) in rawEntries)
            {
                if (offset < 0) continue;
                if (offset >= blockSize)
                { result.AddError($"[{poolKind}] '{name}' in '{packageId}' pins to offset {offset} but block size is {blockSize}."); return null; }
                int absValue = blockStart + offset;
                if (slotMap.ContainsKey(absValue))
                { result.AddError($"[{poolKind}] '{name}' and '{slotMap[absValue]}' in '{packageId}' both pin to offset {offset}."); return null; }
                slotMap[absValue] = name;
                resolved.Add(new ResolvedEntry { Name = name, Value = absValue, Comment = comment, WasPinned = true });
            }

            // Pass 2: auto-assign
            int autoSlot = blockStart;
            foreach (var (name, comment, offset) in rawEntries)
            {
                if (offset >= 0) continue;
                var locked = lockEntries.FirstOrDefault(l => l.packageId == packageId && l.name == name);
                int targetValue;

                if (locked != null && locked.value >= blockStart &&
                    locked.value < blockStart + blockSize && !slotMap.ContainsKey(locked.value))
                {
                    targetValue = locked.value;
                }
                else
                {
                    while (slotMap.ContainsKey(autoSlot) && autoSlot < blockStart + blockSize) autoSlot++;
                    if (autoSlot >= blockStart + blockSize)
                    { result.AddError($"[{poolKind}] Provider '{packageId}' has overflowed its block (size {blockSize})."); return null; }
                    targetValue = autoSlot++;
                }

                if (locked != null && locked.value != targetValue)
                    result.AddWarning($"[{poolKind}] '{name}' in '{packageId}' changed value {locked.value} → {targetValue}.");

                slotMap[targetValue] = name;
                resolved.Add(new ResolvedEntry { Name = name, Value = targetValue, Comment = comment, WasPinned = false });
            }

            resolved.Sort((a, b) => a.Value.CompareTo(b.Value));
            return resolved;
        }

        // ── File writing ──────────────────────────────────────────────────────

        private static void WriteEnumFile(
            List<ResolvedBlock> blocks, string outputPath,
            string namespaceName, string enumName, string docComment)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// AUTO-GENERATED by MidManStudio Pool Type Generator.");
            sb.AppendLine("// DO NOT edit this file manually.");
            sb.AppendLine("// Regenerate via: MidManStudio >  Utilities > Pool Type Generator");
            sb.AppendLine($"// Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>{docComment}</summary>");
            sb.AppendLine($"    public enum {enumName}");
            sb.AppendLine("    {");

            for (int b = 0; b < blocks.Count; b++)
            {
                var blk     = blocks[b];
                int blockEnd = blk.BlockStart + blk.BlockSize - 1;
                sb.AppendLine(
                    $"        // ── {blk.DisplayName}  [{blk.PackageId}]" +
                    $"  (block {blk.BlockStart}–{blockEnd})" +
                    $"  priority={blk.Priority}  ──────────────────────────");

                if (blk.Entries.Count == 0)
                {
                    sb.AppendLine("        // (no entries defined)");
                }
                else
                {
                    foreach (var entry in blk.Entries)
                    {
                        string pinTag  = entry.WasPinned ? " [pinned]" : "";
                        string comment = string.IsNullOrWhiteSpace(entry.Comment)
                            ? pinTag : $" // {entry.Comment}{pinTag}";
                        sb.AppendLine($"        {entry.Name} = {entry.Value},{comment}");
                    }
                }

                if (b < blocks.Count - 1) sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            EnsureDirectory(outputPath);
            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            Debug.Log($"[PoolTypeGenerator] Wrote {enumName} → {outputPath}");
        }

        // ── Lock file ─────────────────────────────────────────────────────────

        private static LockFile LoadLockFile(string path)
        {
            if (!File.Exists(path)) return new LockFile();
            try { return JsonUtility.FromJson<LockFile>(File.ReadAllText(path)) ?? new LockFile(); }
            catch { Debug.LogWarning("[PoolTypeGenerator] Could not parse lock file — starting fresh."); return new LockFile(); }
        }

        private static void SaveLockFile(LockFile lf, string path)
        {
            EnsureDirectory(path);
            File.WriteAllText(path, JsonUtility.ToJson(lf, prettyPrint: true), Encoding.UTF8);
            Debug.Log($"[PoolTypeGenerator] Lock file saved → {path}");
        }

        private static void UpdateLockEntries(List<ResolvedBlock> blocks, List<LockEntry> lockEntries)
        {
            lockEntries.Clear();
            foreach (var blk in blocks)
                foreach (var entry in blk.Entries)
                    lockEntries.Add(new LockEntry { packageId = blk.PackageId, name = entry.Name, value = entry.Value });
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private static void EnsureDirectory(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        private static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!char.IsLetter(name[0]) && name[0] != '_') return false;
            foreach (var c in name)
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return !CSharpKeywords.Contains(name);
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
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Editor window
    // ─────────────────────────────────────────────────────────────────────────

    public class PoolTypeGeneratorWindow : EditorWindow
    {
        private PoolTypeGeneratorSettingsSO _settings;
        private GenerationResult            _lastResult;
        private Vector2                     _scroll;
        private bool                        _showObjectProviders   = true;
        private bool                        _showParticleProviders = true;
        private bool                        _showNetworkProviders  = true;

        [MenuItem("MidManStudio/Utilities/Pool Type Generator")]
        public static void Open()
        {
            var w = GetWindow<PoolTypeGeneratorWindow>("Pool Type Generator");
            w.minSize = new Vector2(500, 520);
        }

        private void OnEnable() => _settings = PoolTypeGeneratorCore.FindSettings();

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            DrawHeader();
            EditorGUILayout.Space(6);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawSettingsSection();
            EditorGUILayout.Space(6);
            DrawProvidersSection();
            EditorGUILayout.Space(6);
            DrawActionsSection();
            EditorGUILayout.Space(6);
            DrawResultsSection();
            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("MidManStudio — Pool Type Generator", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Discovers all PoolTypeProvider assets in the project and writes the shared enum files.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawSettingsSection()
        {
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            _settings = (PoolTypeGeneratorSettingsSO)EditorGUILayout.ObjectField(
                "Generator Settings", _settings, typeof(PoolTypeGeneratorSettingsSO), false);

            if (_settings == null)
            {
                EditorGUILayout.HelpBox(
                    "No PoolTypeGeneratorSettings found.\nCreate one: MidManStudio > Utilities > Pool Type Generator Settings",
                    MessageType.Warning);
                if (GUILayout.Button("Create Default Settings")) CreateDefaultSettings();
            }
            else
            {
                var so = new SerializedObject(_settings);
                so.Update();
                EditorGUILayout.PropertyField(so.FindProperty("objectEnumOutputPath"),  new GUIContent("Object Enum Output"));
                EditorGUILayout.PropertyField(so.FindProperty("particleEnumOutputPath"), new GUIContent("Particle Enum Output"));
                EditorGUILayout.PropertyField(so.FindProperty("networkEnumOutputPath"),  new GUIContent("Network Enum Output"));
                EditorGUILayout.PropertyField(so.FindProperty("lockFilePath"),            new GUIContent("Lock File"));
                EditorGUILayout.PropertyField(so.FindProperty("minimumBlockSize"),        new GUIContent("Min Block Size"));
                EditorGUILayout.PropertyField(so.FindProperty("generatedNamespace"),      new GUIContent("Namespace"));
                EditorGUILayout.PropertyField(so.FindProperty("autoGenerateOnAssetChange"), new GUIContent("Auto-Generate on Change"));
                so.ApplyModifiedProperties();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawProvidersSection()
        {
            EditorGUILayout.LabelField("Discovered Providers", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            DrawProviderGroup<PoolTypeProviderSO>(
                "Object Pool", ref _showObjectProviders,
                a => (a.packageId, a.displayName, a.priority, a.EntryCount));

            EditorGUILayout.Space(4);

            DrawProviderGroup<ParticlePoolTypeProviderSO>(
                "Particle Pool", ref _showParticleProviders,
                a => (a.packageId, a.displayName, a.priority, a.EntryCount));

            EditorGUILayout.Space(4);

            // Network providers loaded via string search — no netcode type reference needed
            DrawNetworkProviderGroup();

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Create a provider for your game:", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Object Provider"))
                CreateProvider<PoolTypeProviderSO>("PoolTypeProvider_MyGame.asset");
            if (GUILayout.Button("+ Particle Provider"))
                CreateProvider<ParticlePoolTypeProviderSO>("ParticlePoolTypeProvider_MyGame.asset");
            if (GUILayout.Button("+ Network Provider (needs netcode package)"))
                EditorUtility.DisplayDialog("Network Provider",
                    "Install com.midmanstudio.netcode then use:\n" +
                    "MidManStudio > Netcode Utilities > Pool Type Provider (Network Object)", "OK");
            EditorGUILayout.EndHorizontal();
        }

        private void DrawNetworkProviderGroup()
        {
            var guids = AssetDatabase.FindAssets("t:NetworkPoolTypeProviderSO");
            _showNetworkProviders = EditorGUILayout.Foldout(
                _showNetworkProviders, $"Network Object Pool  ({guids.Length} provider(s))", true);

            if (!_showNetworkProviders) return;

            if (guids.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No NetworkPoolTypeProviderSO assets found.\n" +
                    "Install com.midmanstudio.netcode to create them.",
                    MessageType.Info);
                return;
            }

            foreach (var g in guids)
            {
                var path  = AssetDatabase.GUIDToAssetPath(g);
                var asset = AssetDatabase.LoadMainAssetAtPath(path) as ScriptableObject;
                if (asset == null) continue;

                var so          = new SerializedObject(asset);
                var packageId   = so.FindProperty("packageId")?.stringValue   ?? "";
                var displayName = so.FindProperty("displayName")?.stringValue ?? asset.name;
                var priority    = so.FindProperty("priority")?.intValue       ?? 0;
                var count       = so.FindProperty("entries")?.arraySize       ?? 0;

                EditorGUILayout.BeginHorizontal();
                Color badgeCol = priority == 0  ? new Color(0.4f, 0.8f, 0.4f) :
                                 priority <= 10 ? new Color(0.4f, 0.6f, 1.0f) :
                                                  new Color(0.8f, 0.8f, 0.8f);
                var old = GUI.contentColor;
                GUI.contentColor = badgeCol;
                EditorGUILayout.LabelField($"[{priority:D3}]", GUILayout.Width(36));
                GUI.contentColor = old;
                EditorGUILayout.LabelField($"{displayName}  ({packageId})  — {count} entries", EditorStyles.miniLabel);
                if (GUILayout.Button("Select", GUILayout.Width(50))) Selection.activeObject = asset;
                if (GUILayout.Button("Ping",   GUILayout.Width(40))) EditorGUIUtility.PingObject(asset);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawProviderGroup<T>(
            string label, ref bool foldout,
            Func<T, (string id, string display, int pri, int count)> extract)
            where T : ScriptableObject
        {
            var guids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
            foldout = EditorGUILayout.Foldout(foldout, $"{label}  ({guids.Length} provider(s))", true);
            if (!foldout) return;

            if (guids.Length == 0)
            {
                EditorGUILayout.HelpBox($"No {typeof(T).Name} assets found.", MessageType.Info);
                return;
            }

            var items = guids
                .Select(g => { var path = AssetDatabase.GUIDToAssetPath(g); var asset = AssetDatabase.LoadAssetAtPath<T>(path); return asset == null ? default : (asset, path, extract(asset)); })
                .Where(x => x.asset != null)
                .OrderBy(x => x.Item3.pri).ThenBy(x => x.Item3.id)
                .ToList();

            foreach (var (asset, path, (id, display, pri, count)) in items)
            {
                EditorGUILayout.BeginHorizontal();
                Color badgeCol = pri == 0   ? new Color(0.4f, 0.8f, 0.4f) :
                                 pri <= 10  ? new Color(0.4f, 0.6f, 1.0f) :
                                              new Color(0.8f, 0.8f, 0.8f);
                var old = GUI.contentColor; GUI.contentColor = badgeCol;
                EditorGUILayout.LabelField($"[{pri:D3}]", GUILayout.Width(36));
                GUI.contentColor = old;
                EditorGUILayout.LabelField($"{display}  ({id})  — {count} entries", EditorStyles.miniLabel);
                if (GUILayout.Button("Select", GUILayout.Width(50))) Selection.activeObject = asset;
                if (GUILayout.Button("Ping",   GUILayout.Width(40))) EditorGUIUtility.PingObject(asset);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawActionsSection()
        {
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = _settings != null;
            var old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.3f, 0.85f, 0.3f);
            if (GUILayout.Button("⚙  Generate Now", GUILayout.Height(36)))
            {
                _lastResult = PoolTypeGeneratorCore.Generate(_settings);
                if (_lastResult.Success)
                    EditorUtility.DisplayDialog("Pool Type Generator",
                        $"Generation complete!\n\n" +
                        $"Object blocks:   {_lastResult.ObjectBlocksWritten}\n" +
                        $"Particle blocks: {_lastResult.ParticleBlocksWritten}\n" +
                        $"Network blocks:  {_lastResult.NetworkBlocksWritten}", "OK");
            }
            GUI.backgroundColor = old;
            GUI.enabled = true;

            if (GUILayout.Button("📂  Open Output Folder", GUILayout.Height(36)))
            {
                var dir = _settings != null
                    ? Path.GetDirectoryName(_settings.objectEnumOutputPath)
                    : "packages/com.midmanstudio.utilities/Runtime/PoolSystems";
                EditorUtility.RevealInFinder(string.IsNullOrEmpty(dir) ? "Assets" : dir);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);
            EditorGUILayout.HelpBox(
                "To add your own pool types:\n" +
                "  1. Click '+ Object/Particle Provider' above.\n" +
                "  2. Set your packageId (e.g. com.mygame), priority ≥ 100, and add entry names.\n" +
                "  3. Click Generate Now.", MessageType.None);
        }

        private void DrawResultsSection()
        {
            if (_lastResult == null) return;
            EditorGUILayout.LabelField("Last Result", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (_lastResult.Success)
                EditorGUILayout.HelpBox(
                    $"✓ Success — Object: {_lastResult.ObjectBlocksWritten}  " +
                    $"Particle: {_lastResult.ParticleBlocksWritten}  " +
                    $"Network: {_lastResult.NetworkBlocksWritten}", MessageType.Info);
            foreach (var w in _lastResult.Warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
            foreach (var e in _lastResult.Errors)   EditorGUILayout.HelpBox(e, MessageType.Error);
            EditorGUILayout.EndVertical();
        }

        private void CreateDefaultSettings()
        {
            const string dir  = "Assets/MidManStudio/Generated";
            const string path = dir + "/PoolTypeGeneratorSettings.asset";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var asset = ScriptableObject.CreateInstance<PoolTypeGeneratorSettingsSO>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            _settings = asset;
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        private static void CreateProvider<T>(string fileName) where T : ScriptableObject
        {
            const string dir = "Assets/MidManStudio/Generated/MyProviders";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string path = $"{dir}/{fileName}";
            var asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Auto-generate hook
    // ─────────────────────────────────────────────────────────────────────────

    internal class PoolTypeAssetPostprocessor : AssetPostprocessor
    {
        private static readonly HashSet<string> WatchedTypeNames = new()
        {
            "PoolTypeProviderSO",
            "ParticlePoolTypeProviderSO",
            "NetworkPoolTypeProviderSO",
            "PoolTypeGeneratorSettingsSO"
        };

        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            bool relevant = imported.Concat(deleted).Concat(moved).Any(path =>
            {
                if (!path.EndsWith(".asset")) return false;
                var t = AssetDatabase.GetMainAssetTypeAtPath(path);
                return t != null && WatchedTypeNames.Contains(t.Name);
            });

            if (!relevant) return;

            var settings = PoolTypeGeneratorCore.FindSettings();
            if (settings == null || !settings.autoGenerateOnAssetChange) return;

            EditorApplication.delayCall += () =>
            {
                var result = PoolTypeGeneratorCore.Generate(settings);
                if (result.HasErrors)
                    foreach (var e in result.Errors) Debug.LogError($"[PoolTypeGenerator Auto] {e}");
                else if (result.Warnings.Count > 0)
                    foreach (var w in result.Warnings) Debug.LogWarning($"[PoolTypeGenerator Auto] {w}");
                else
                    Debug.Log("[PoolTypeGenerator Auto] Regenerated successfully.");
            };
        }
    }
}
#endif