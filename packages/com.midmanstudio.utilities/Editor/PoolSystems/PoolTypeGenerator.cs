// PoolTypeGenerator.cs
// Reads all PoolTypeProviderSO / ParticlePoolTypeProviderSO / NetworkPoolTypeProviderSO
// assets found anywhere in the project and writes the three generated enum files.
//
// OPEN VIA: MidManStudio > Pool Type Generator
//
// HOW PROVIDERS ARE DISCOVERED:
//   AssetDatabase.FindAssets scans the entire project — no hardcoded package list.
//   Each package ships its own provider asset. User game code creates its own.
//   The generator just reads whatever it finds and sorts by priority.
//
// ADDING A NEW PACKAGE:
//   Ship a PoolTypeProviderSO asset with the package. Nothing else needed.
//
// USER WORKFLOW:
//   1. Right-click in Project: MidManStudio > Pool Type Provider (Object/Particle/Network)
//   2. Set packageId, priority, add entry names
//   3. MidManStudio > Pool Type Generator > Generate Now
//
// LOCK FILE:
//   Keeps enum values stable across regenerations. Commit to source control.
//   Values only shift if you add entries above an unpinned entry AND
//   no lock file entry exists. Pin critical entries with explicitOffset.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Netcode.Generator;

namespace MidManStudio.Core.Pools.Generator
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Internal data types
    // ─────────────────────────────────────────────────────────────────────────

    internal class ProviderData
    {
        public string                           PackageId;
        public string                           DisplayName;
        public int                              Priority;
        public List<(string name, string comment, int offset)> Entries;
    }

    internal class ResolvedBlock
    {
        public string               PackageId;
        public string               DisplayName;
        public int                  Priority;
        public int                  BlockStart;
        public int                  BlockSize;
        public List<ResolvedEntry>  Entries = new();
    }

    internal class ResolvedEntry
    {
        public string Name;
        public int    Value;
        public string Comment;
        public bool   WasPinned;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Lock file — keeps values stable across regenerations
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
        // ── Settings ──────────────────────────────────────────────────────────

        public static PoolTypeGeneratorSettingsSO FindSettings()
        {
            var guids = AssetDatabase.FindAssets("t:PoolTypeGeneratorSettingsSO");
            if (guids.Length == 0) return null;
            if (guids.Length > 1)
                Debug.LogWarning(
                    "[PoolTypeGenerator] Multiple settings assets found — using first.");
            return AssetDatabase.LoadAssetAtPath<PoolTypeGeneratorSettingsSO>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        // ── Main entry point ──────────────────────────────────────────────────

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
                    WriteEnumFile(
                        blocks,
                        settings.objectEnumOutputPath,
                        settings.generatedNamespace,
                        "PoolableObjectType",
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
                    WriteEnumFile(
                        blocks,
                        settings.particleEnumOutputPath,
                        settings.generatedNamespace,
                        "PoolableParticleType",
                        "Particle pool type IDs. AUTO-GENERATED — do not edit manually.");
                    UpdateLockEntries(blocks, lockFile.particleEntries);
                    result.ParticleBlocksWritten = blocks.Count;
                }
            }

            if (result.HasErrors) return result;

            // ── Network object pool ───────────────────────────────────────────
            {
                var providers = CollectNetworkProviders();
                LogProviders("NetworkObject", providers);

                var blocks = AssignBlocks(providers, settings.minimumBlockSize,
                    lockFile.networkEntries, result, "NetworkObject");

                if (!result.HasErrors)
                {
                    WriteEnumFile(
                        blocks,
                        settings.networkEnumOutputPath,
                        settings.generatedNamespace,
                        "PoolableNetworkObjectType",
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
        // Each Collect* method scans the entire project for one provider type.
        // No hardcoded list — new packages just drop their asset in.

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

        private static List<ProviderData> CollectNetworkProviders()
        {
            var list  = new List<ProviderData>();
            var guids = AssetDatabase.FindAssets("t:NetworkPoolTypeProviderSO");
            foreach (var g in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<NetworkPoolTypeProviderSO>(
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
            // Sort: priority ascending, then packageId alphabetically for stability
            var sorted = providers
                .OrderBy(p => p.Priority)
                .ThenBy(p => p.PackageId)
                .ToList();

            // Detect duplicate packageIds
            var dupes = sorted
                .GroupBy(p => p.packageId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            foreach (var d in dupes)
                result.AddError(
                    $"[{poolKind}] Duplicate packageId '{d}'. " +
                    "Each provider must have a unique package ID.");

            if (result.HasErrors) return null;

            // Detect duplicate entry names across all providers
            var allNames = sorted
                .SelectMany(p => p.Entries.Select(e => e.name))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            var dupeNames = allNames
                .GroupBy(x => x)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            foreach (var d in dupeNames)
                result.AddError(
                    $"[{poolKind}] Duplicate entry name '{d}' found across providers. " +
                    "All entry names must be globally unique.");

            if (result.HasErrors) return null;

            // Validate entry names — must be valid C# identifiers
            foreach (var p in sorted)
            {
                foreach (var (name, _, _) in p.Entries)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        result.AddError(
                            $"[{poolKind}] Provider '{p.PackageId}' has an entry " +
                            "with an empty name.");
                        continue;
                    }
                    if (!IsValidIdentifier(name))
                        result.AddError(
                            $"[{poolKind}] '{name}' in '{p.PackageId}' is not a " +
                            "valid C# identifier. Use PascalCase, letters/digits/underscore only.");
                }
            }

            if (result.HasErrors) return null;

            // Assign blocks
            var blocks = new List<ResolvedBlock>();
            int cursor = 0;

            foreach (var p in sorted)
            {
                int entryCount = p.Entries.Count;

                // Block size = smallest multiple of minBlockSize >= entryCount, min minBlockSize
                int blockSize = entryCount == 0
                    ? minBlockSize
                    : (int)Math.Ceiling((double)entryCount / minBlockSize) * minBlockSize;
                blockSize = Math.Max(blockSize, minBlockSize);

                int blockStart = cursor;

                var entries = ResolveEntries(
                    p.PackageId, p.Entries, blockStart, blockSize,
                    lockEntries, result, poolKind);

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

        // ── Entry resolution ──────────────────────────────────────────────────

        private static List<ResolvedEntry> ResolveEntries(
            string                                     packageId,
            List<(string name, string comment, int offset)> rawEntries,
            int                                        blockStart,
            int                                        blockSize,
            List<LockEntry>                            lockEntries,
            GenerationResult                           result,
            string                                     poolKind)
        {
            var resolved = new List<ResolvedEntry>();
            var slotMap  = new Dictionary<int, string>(); // absolute value → name

            // ── Pass 1: explicitly pinned offsets ─────────────────────────────
            foreach (var (name, comment, offset) in rawEntries)
            {
                if (offset < 0) continue;

                if (offset >= blockSize)
                {
                    result.AddError(
                        $"[{poolKind}] '{name}' in '{packageId}' pins to offset {offset} " +
                        $"but block size is {blockSize}. " +
                        "Either reduce the offset or the block will auto-expand on next run.");
                    return null;
                }

                int absValue = blockStart + offset;

                if (slotMap.ContainsKey(absValue))
                {
                    result.AddError(
                        $"[{poolKind}] '{name}' and '{slotMap[absValue]}' in '{packageId}' " +
                        $"both pin to offset {offset} (absolute value {absValue}).");
                    return null;
                }

                slotMap[absValue] = name;
                resolved.Add(new ResolvedEntry
                {
                    Name      = name,
                    Value     = absValue,
                    Comment   = comment,
                    WasPinned = true
                });
            }

            // ── Pass 2: auto-assign remaining entries ─────────────────────────
            // Prefer lock-file values for stability; fall back to sequential.
            int autoSlot = blockStart;

            foreach (var (name, comment, offset) in rawEntries)
            {
                if (offset >= 0) continue; // already handled

                // Try to honour the last known value from the lock file
                var locked = lockEntries.FirstOrDefault(
                    l => l.packageId == packageId && l.name == name);

                int targetValue;

                if (locked != null
                    && locked.value >= blockStart
                    && locked.value < blockStart + blockSize
                    && !slotMap.ContainsKey(locked.value))
                {
                    // Lock file gives a stable slot — use it
                    targetValue = locked.value;
                }
                else
                {
                    // Walk forward to the next free slot
                    while (slotMap.ContainsKey(autoSlot) && autoSlot < blockStart + blockSize)
                        autoSlot++;

                    if (autoSlot >= blockStart + blockSize)
                    {
                        result.AddError(
                            $"[{poolKind}] Provider '{packageId}' has overflowed its " +
                            $"block (size {blockSize}, start {blockStart}). " +
                            $"Has {rawEntries.Count} entries. The block will auto-expand " +
                            "to the next 100-multiple on the next run — remove pinned offsets " +
                            "that waste slots, or reduce entry count.");
                        return null;
                    }

                    targetValue = autoSlot;
                    autoSlot++;
                }

                // Warn if this entry's value changed from last generation
                if (locked != null && locked.value != targetValue)
                    result.AddWarning(
                        $"[{poolKind}] '{name}' in '{packageId}' changed value " +
                        $"{locked.value} → {targetValue}. " +
                        "Serialised inspector fields referencing this entry will need " +
                        "re-selecting. Pin the entry with explicitOffset to prevent this.");

                slotMap[targetValue] = name;
                resolved.Add(new ResolvedEntry
                {
                    Name      = name,
                    Value     = targetValue,
                    Comment   = comment,
                    WasPinned = false
                });
            }

            // Sort by value so the file reads in a clean ascending order
            resolved.Sort((a, b) => a.Value.CompareTo(b.Value));
            return resolved;
        }

        // ── File writing ──────────────────────────────────────────────────────

        private static void WriteEnumFile(
            List<ResolvedBlock> blocks,
            string              outputPath,
            string              namespaceName,
            string              enumName,
            string              docComment)
        {
            var sb = new StringBuilder();

            sb.AppendLine("// AUTO-GENERATED by MidManStudio Pool Type Generator.");
            sb.AppendLine("// DO NOT edit this file manually.");
            sb.AppendLine("// Source of truth: PoolTypeProvider assets in your project.");
            sb.AppendLine("// Regenerate via: MidManStudio > Pool Type Generator");
            sb.AppendLine($"// Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>{docComment}</summary>");
            sb.AppendLine($"    public enum {enumName}");
            sb.AppendLine("    {");

            for (int b = 0; b < blocks.Count; b++)
            {
                var blk = blocks[b];

                // Block separator comment
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
                        string pinTag = entry.WasPinned ? " [pinned]" : "";
                        string trailingComment = string.IsNullOrWhiteSpace(entry.Comment)
                            ? pinTag
                            : $" // {entry.Comment}{pinTag}";

                        sb.AppendLine(
                            $"        {entry.Name} = {entry.Value},{trailingComment}");
                    }
                }

                // Blank line between blocks for readability
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
            try
            {
                return JsonUtility.FromJson<LockFile>(File.ReadAllText(path))
                       ?? new LockFile();
            }
            catch
            {
                Debug.LogWarning(
                    "[PoolTypeGenerator] Could not parse lock file — starting fresh. " +
                    "Enum values may shift this generation.");
                return new LockFile();
            }
        }

        private static void SaveLockFile(LockFile lf, string path)
        {
            EnsureDirectory(path);
            File.WriteAllText(path, JsonUtility.ToJson(lf, prettyPrint: true), Encoding.UTF8);
            Debug.Log($"[PoolTypeGenerator] Lock file saved → {path}");
        }

        private static void UpdateLockEntries(
            List<ResolvedBlock> blocks, List<LockEntry> lockEntries)
        {
            lockEntries.Clear();
            foreach (var blk in blocks)
                foreach (var entry in blk.Entries)
                    lockEntries.Add(new LockEntry
                    {
                        packageId = blk.PackageId,
                        name      = entry.Name,
                        value     = entry.Value
                    });
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
            // Check it's not a C# keyword
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
    //  Settings SO — updated with network output path
    // ─────────────────────────────────────────────────────────────────────────

    // (Full replacement — update your existing PoolTypeGeneratorSettingsSO)
    [CreateAssetMenu(
        fileName = "PoolTypeGeneratorSettings",
        menuName = "MidManStudio/Pool Type Generator Settings")]
    public class PoolTypeGeneratorSettingsSO : ScriptableObject
    {
        [Header("Output Paths")]
        [Tooltip("Where PoolableObjectType.cs is written. Must be inside Assets/.")]
        public string objectEnumOutputPath =
            "Assets/MidManStudio/Generated/PoolableObjectType.cs";

        [Tooltip("Where PoolableParticleType.cs is written.")]
        public string particleEnumOutputPath =
            "Assets/MidManStudio/Generated/PoolableParticleType.cs";

        [Tooltip("Where PoolableNetworkObjectType.cs is written.")]
        public string networkEnumOutputPath =
            "Assets/MidManStudio/Generated/PoolableNetworkObjectType.cs";

        [Tooltip("Lock file path. Commit this to source control.")]
        public string lockFilePath =
            "Assets/MidManStudio/Generated/PoolTypeLock.json";

        [Header("Block Sizing")]
        [Tooltip("Minimum gap between provider blocks in the enum.\n" +
                 "Blocks auto-expand in multiples of this value.\n" +
                 "Recommended: 100.")]
        [Min(10)]
        public int minimumBlockSize = 100;

        [Header("Namespace")]
        public string generatedNamespace = "MidManStudio.Core.Pools";

        [Header("Auto-Generate")]
        [Tooltip("Regenerate automatically when any provider asset changes.\n" +
                 "Disable for manual control.")]
        public bool autoGenerateOnAssetChange = false;

        [Header("Diagnostics")]
        [Tooltip("Log all discovered providers and their entry counts on every generation.")]
        public bool verboseProviderLogging = true;
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

        [MenuItem("MidManStudio/Pool Type Generator")]
        public static void Open()
        {
            var w = GetWindow<PoolTypeGeneratorWindow>("Pool Type Generator");
            w.minSize = new Vector2(500, 520);
        }

        private void OnEnable()
        {
            _settings = PoolTypeGeneratorCore.FindSettings();
        }

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

        // ── Sections ──────────────────────────────────────────────────────────

        private void DrawHeader()
        {
            EditorGUILayout.LabelField(
                "MidManStudio — Pool Type Generator", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Discovers all PoolTypeProvider assets in the project and writes " +
                "the shared enum files. Users add their own providers — no code needed.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawSettingsSection()
        {
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            _settings = (PoolTypeGeneratorSettingsSO)EditorGUILayout.ObjectField(
                "Generator Settings", _settings,
                typeof(PoolTypeGeneratorSettingsSO), false);

            if (_settings == null)
            {
                EditorGUILayout.HelpBox(
                    "No PoolTypeGeneratorSettings found.\n" +
                    "Create one: MidManStudio > Pool Type Generator Settings",
                    MessageType.Warning);
                if (GUILayout.Button("Create Default Settings"))
                    CreateDefaultSettings();
            }
            else
            {
                var so = new SerializedObject(_settings);
                so.Update();
                EditorGUILayout.PropertyField(
                    so.FindProperty("objectEnumOutputPath"),
                    new GUIContent("Object Enum Output"));
                EditorGUILayout.PropertyField(
                    so.FindProperty("particleEnumOutputPath"),
                    new GUIContent("Particle Enum Output"));
                EditorGUILayout.PropertyField(
                    so.FindProperty("networkEnumOutputPath"),
                    new GUIContent("Network Enum Output"));
                EditorGUILayout.PropertyField(
                    so.FindProperty("lockFilePath"),
                    new GUIContent("Lock File"));
                EditorGUILayout.PropertyField(
                    so.FindProperty("minimumBlockSize"),
                    new GUIContent("Min Block Size"));
                EditorGUILayout.PropertyField(
                    so.FindProperty("generatedNamespace"),
                    new GUIContent("Namespace"));
                EditorGUILayout.PropertyField(
                    so.FindProperty("autoGenerateOnAssetChange"),
                    new GUIContent("Auto-Generate on Change"));
                so.ApplyModifiedProperties();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawProvidersSection()
        {
            EditorGUILayout.LabelField("Discovered Providers", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            DrawProviderGroup<PoolTypeProviderSO>(
                "Object Pool",
                ref _showObjectProviders,
                a => (a.packageId, a.displayName, a.priority, a.EntryCount));

            EditorGUILayout.Space(4);

            DrawProviderGroup<ParticlePoolTypeProviderSO>(
                "Particle Pool",
                ref _showParticleProviders,
                a => (a.packageId, a.displayName, a.priority, a.EntryCount));

            EditorGUILayout.Space(4);

            DrawProviderGroup<NetworkPoolTypeProviderSO>(
                "Network Object Pool",
                ref _showNetworkProviders,
                a => (a.packageId, a.displayName, a.priority, a.EntryCount));

            EditorGUILayout.EndVertical();

            // Quick-create buttons
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "Create a provider for your game:", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Object Provider"))
                CreateProvider<PoolTypeProviderSO>("PoolTypeProvider_MyGame.asset");
            if (GUILayout.Button("+ Particle Provider"))
                CreateProvider<ParticlePoolTypeProviderSO>(
                    "ParticlePoolTypeProvider_MyGame.asset");
            if (GUILayout.Button("+ Network Provider"))
                CreateProvider<NetworkPoolTypeProviderSO>(
                    "NetworkPoolTypeProvider_MyGame.asset");
            EditorGUILayout.EndHorizontal();
        }

        private void DrawProviderGroup<T>(
            string label,
            ref bool foldout,
            Func<T, (string id, string display, int pri, int count)> extract)
            where T : ScriptableObject
        {
            var guids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
            foldout = EditorGUILayout.Foldout(
                foldout, $"{label}  ({guids.Length} provider(s))", true);

            if (!foldout) return;

            if (guids.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    $"No {typeof(T).Name} assets found in project.",
                    MessageType.Info);
                return;
            }

            // Sort by priority for display
            var items = guids
                .Select(g =>
                {
                    var path  = AssetDatabase.GUIDToAssetPath(g);
                    var asset = AssetDatabase.LoadAssetAtPath<T>(path);
                    return asset == null ? default : (asset, path, extract(asset));
                })
                .Where(x => x.asset != null)
                .OrderBy(x => x.Item3.pri)
                .ThenBy(x => x.Item3.id)
                .ToList();

            foreach (var (asset, path, (id, display, pri, count)) in items)
            {
                EditorGUILayout.BeginHorizontal();

                // Priority badge colour
                Color badgeCol = pri == 0   ? new Color(0.4f, 0.8f, 0.4f) :
                                 pri <= 10  ? new Color(0.4f, 0.6f, 1.0f) :
                                              new Color(0.8f, 0.8f, 0.8f);
                var oldCol = GUI.contentColor;
                GUI.contentColor = badgeCol;
                EditorGUILayout.LabelField($"[{pri:D3}]", GUILayout.Width(36));
                GUI.contentColor = oldCol;

                EditorGUILayout.LabelField(
                    $"{display}  ({id})  — {count} entries",
                    EditorStyles.miniLabel);

                if (GUILayout.Button("Select", GUILayout.Width(50)))
                    Selection.activeObject = asset;

                if (GUILayout.Button("Ping", GUILayout.Width(40)))
                    EditorGUIUtility.PingObject(asset);

                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawActionsSection()
        {
            EditorGUILayout.BeginHorizontal();

            GUI.enabled = _settings != null;
            var oldCol = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.3f, 0.85f, 0.3f);
            if (GUILayout.Button("⚙  Generate Now", GUILayout.Height(36)))
            {
                _lastResult = PoolTypeGeneratorCore.Generate(_settings);
                if (_lastResult.Success)
                    EditorUtility.DisplayDialog(
                        "Pool Type Generator",
                        $"Generation complete!\n\n" +
                        $"Object blocks:   {_lastResult.ObjectBlocksWritten}\n" +
                        $"Particle blocks: {_lastResult.ParticleBlocksWritten}\n" +
                        $"Network blocks:  {_lastResult.NetworkBlocksWritten}",
                        "OK");
            }
            GUI.backgroundColor = oldCol;
            GUI.enabled = true;

            if (GUILayout.Button("📂  Open Output Folder", GUILayout.Height(36)))
            {
                var dir = _settings != null
                    ? Path.GetDirectoryName(_settings.objectEnumOutputPath)
                    : "Assets/MidManStudio/Generated";
                EditorUtility.RevealInFinder(
                    string.IsNullOrEmpty(dir) ? "Assets" : dir);
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            // Workflow hint
            EditorGUILayout.HelpBox(
                "To add your own pool types:\n" +
                "  1. Click '+ Object/Particle/Network Provider' above.\n" +
                "  2. Set your packageId (e.g. com.mygame), priority ≥ 100, " +
                     "and add entry names.\n" +
                "  3. Click Generate Now.\n\n" +
                "You never need to modify the generator code.",
                MessageType.None);
        }

        private void DrawResultsSection()
        {
            if (_lastResult == null) return;

            EditorGUILayout.LabelField("Last Result", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (_lastResult.Success)
                EditorGUILayout.HelpBox(
                    $"✓ Success — Object: {_lastResult.ObjectBlocksWritten} block(s)  " +
                    $"Particle: {_lastResult.ParticleBlocksWritten} block(s)  " +
                    $"Network: {_lastResult.NetworkBlocksWritten} block(s)",
                    MessageType.Info);

            foreach (var w in _lastResult.Warnings)
                EditorGUILayout.HelpBox(w, MessageType.Warning);

            foreach (var e in _lastResult.Errors)
                EditorGUILayout.HelpBox(e, MessageType.Error);

            EditorGUILayout.EndVertical();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

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

        private static void CreateProvider<T>(string fileName)
            where T : ScriptableObject
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
        private static readonly HashSet<string> WatchedTypes = new()
        {
            nameof(PoolTypeProviderSO),
            nameof(ParticlePoolTypeProviderSO),
            nameof(NetworkPoolTypeProviderSO),
            nameof(PoolTypeGeneratorSettingsSO)
        };

        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted,
            string[] moved,    string[] movedFrom)
        {
            bool relevant = imported.Concat(deleted).Concat(moved).Any(path =>
            {
                if (!path.EndsWith(".asset")) return false;
                var t = AssetDatabase.GetMainAssetTypeAtPath(path);
                return t != null && WatchedTypes.Contains(t.Name);
            });

            if (!relevant) return;

            var settings = PoolTypeGeneratorCore.FindSettings();
            if (settings == null || !settings.autoGenerateOnAssetChange) return;

            EditorApplication.delayCall += () =>
            {
                var result = PoolTypeGeneratorCore.Generate(settings);
                if (result.HasErrors)
                    foreach (var e in result.Errors)
                        Debug.LogError($"[PoolTypeGenerator Auto] {e}");
                else if (result.Warnings.Count > 0)
                    foreach (var w in result.Warnings)
                        Debug.LogWarning($"[PoolTypeGenerator Auto] {w}");
                else
                    Debug.Log(
                        "[PoolTypeGenerator Auto] Regenerated successfully.");
            };
        }
    }
}
#endif
