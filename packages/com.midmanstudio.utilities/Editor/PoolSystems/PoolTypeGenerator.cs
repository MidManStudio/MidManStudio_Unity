// PoolTypeGenerator.cs
// Editor-only. Reads all PoolTypeProviderSO / ParticlePoolTypeProviderSO assets,
// assigns stable integer values, and writes the two generated enum files.
//
// OPEN VIA: MidManStudio > Pool Type Generator

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace MidManStudio.Core.Pools.Generator
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Data structures
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Resolved block assigned to one provider.</summary>
    internal class ResolvedBlock
    {
        public string PackageId;
        public string DisplayName;
        public int    Priority;
        public int    BlockStart;
        public int    BlockSize;   // always a multiple of MinBlockSize
        public List<ResolvedEntry> Entries = new List<ResolvedEntry>();
    }

    internal class ResolvedEntry
    {
        public string Name;
        public int    Value;       // absolute enum value
        public string Comment;
        public bool   WasPinned;   // was it explicitly pinned by the user?
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Lock file — keeps values stable across regenerations
    // ─────────────────────────────────────────────────────────────────────────

    [Serializable]
    internal class LockFile
    {
        public List<LockEntry> objectEntries   = new List<LockEntry>();
        public List<LockEntry> particleEntries = new List<LockEntry>();
    }

    [Serializable]
    internal class LockEntry
    {
        public string packageId;
        public string name;
        public int    value;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Generator core
    // ─────────────────────────────────────────────────────────────────────────

    public static class PoolTypeGeneratorCore
    {
        private const int FallbackBlockSize = 100;

        // ── Settings lookup ───────────────────────────────────────────────────

        public static PoolTypeGeneratorSettingsSO FindSettings()
        {
            var guids = AssetDatabase.FindAssets("t:PoolTypeGeneratorSettingsSO");
            if (guids.Length == 0) return null;
            if (guids.Length > 1)
                Debug.LogWarning("[PoolTypeGenerator] Multiple PoolTypeGeneratorSettings found — " +
                                 "using first. Consider having only one in the project.");
            return AssetDatabase.LoadAssetAtPath<PoolTypeGeneratorSettingsSO>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        // ── Main entry point ──────────────────────────────────────────────────

        public static GenerationResult Generate(PoolTypeGeneratorSettingsSO settings)
        {
            var result = new GenerationResult();

            if (settings == null)
            {
                result.AddError("No PoolTypeGeneratorSettings asset found. " +
                                "Create one via MidManStudio > Pool Type Generator Settings.");
                return result;
            }

            // Load lock file (may not exist yet)
            var lockFile = LoadLockFile(settings.lockFilePath);

            // ── Object pool ───────────────────────────────────────────────────
            {
                var providers = LoadAllProviders<PoolTypeProviderSO>(
                    p => (p.packageId, p.displayName, p.priority,
                          p.entries.Select(e => (e.entryName, e.comment, e.explicitOffset))
                                   .ToList()));

                var blocks = AssignBlocks(providers, settings.minimumBlockSize,
                                          lockFile.objectEntries, result, "Object");

                if (!result.HasErrors)
                {
                    WriteEnumFile(blocks, settings.objectEnumOutputPath,
                                  settings.generatedNamespace, "PoolableObjectType",
                                  "Object pool type identifiers. AUTO-GENERATED.");
                    UpdateLockEntries(blocks, lockFile.objectEntries);
                    result.ObjectBlocksWritten = blocks.Count;
                }
            }

            if (result.HasErrors) return result;

            // ── Particle pool ─────────────────────────────────────────────────
            {
                var providers = LoadAllProviders<ParticlePoolTypeProviderSO>(
                    p => (p.packageId, p.displayName, p.priority,
                          p.entries.Select(e => (e.entryName, e.comment, e.explicitOffset))
                                   .ToList()));

                var blocks = AssignBlocks(providers, settings.minimumBlockSize,
                                          lockFile.particleEntries, result, "Particle");

                if (!result.HasErrors)
                {
                    WriteEnumFile(blocks, settings.particleEnumOutputPath,
                                  settings.generatedNamespace, "PoolableParticleType",
                                  "Particle pool type identifiers. AUTO-GENERATED.");
                    UpdateLockEntries(blocks, lockFile.particleEntries);
                    result.ParticleBlocksWritten = blocks.Count;
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

        // ── Provider loading ──────────────────────────────────────────────────

        private static List<(string packageId, string displayName, int priority,
            List<(string name, string comment, int offset)> entries)>
            LoadAllProviders<T>(
            Func<T, (string, string, int, List<(string, string, int)>)> extractor)
            where T : ScriptableObject
        {
            var result = new List<(string, string, int, List<(string, string, int)>)>();

            var guids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
            foreach (var guid in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<T>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (asset == null) continue;
                result.Add(extractor(asset));
            }

            return result;
        }

        // ── Block assignment ──────────────────────────────────────────────────

        private static List<ResolvedBlock> AssignBlocks(
            List<(string packageId, string displayName, int priority,
                  List<(string name, string comment, int offset)> entries)> providers,
            int minBlockSize,
            List<LockEntry> lockEntries,
            GenerationResult result,
            string poolKind)
        {
            // Sort by priority, then packageId for stability
            var sorted = providers
                .OrderBy(p => p.priority)
                .ThenBy(p => p.packageId)
                .ToList();

            // Detect duplicate package IDs
            var ids = sorted.Select(p => p.packageId).ToList();
            var dupes = ids.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            foreach (var d in dupes)
                result.AddError($"[{poolKind}] Duplicate packageId '{d}' — " +
                                 "each provider must have a unique package ID.");

            if (result.HasErrors) return null;

            // Detect duplicate entry names globally
            var allNames = sorted.SelectMany(p => p.entries.Select(e => e.name)).ToList();
            var dupeNames = allNames.GroupBy(x => x).Where(g => g.Count() > 1)
                                    .Select(g => g.Key).ToList();
            foreach (var d in dupeNames)
                result.AddError($"[{poolKind}] Duplicate entry name '{d}' found across providers. " +
                                 "All entry names must be unique.");

            if (result.HasErrors) return null;

            var blocks   = new List<ResolvedBlock>();
            int cursor   = 0;  // next block start candidate

            foreach (var (packageId, displayName, priority, entries) in sorted)
            {
                // Block size: smallest multiple of minBlockSize >= entryCount, min minBlockSize
                int needed    = entries.Count;
                int blockSize = needed == 0
                    ? minBlockSize
                    : (int)Math.Ceiling((double)needed / minBlockSize) * minBlockSize;
                blockSize = Math.Max(blockSize, minBlockSize);

                int blockStart = cursor;

                // Resolve entries — honour pinned offsets and lock file
                var resolved = ResolveEntries(
                    packageId, entries, blockStart, blockSize, lockEntries, result, poolKind);

                if (result.HasErrors) return null;

                blocks.Add(new ResolvedBlock
                {
                    PackageId   = packageId,
                    DisplayName = displayName,
                    Priority    = priority,
                    BlockStart  = blockStart,
                    BlockSize   = blockSize,
                    Entries     = resolved
                });

                cursor = blockStart + blockSize;
            }

            return blocks;
        }

        // ── Entry resolution (pins + lock) ────────────────────────────────────

        private static List<ResolvedEntry> ResolveEntries(
            string packageId,
            List<(string name, string comment, int offset)> entries,
            int blockStart,
            int blockSize,
            List<LockEntry> lockEntries,
            GenerationResult result,
            string poolKind)
        {
            var resolved = new List<ResolvedEntry>();
            // Slot → name, so we can detect collisions within the block
            var slotMap  = new Dictionary<int, string>();

            // Pass 1: honour explicitly pinned offsets
            foreach (var (name, comment, offset) in entries)
            {
                if (offset < 0) continue;  // auto — handled in pass 2

                int absoluteValue = blockStart + offset;

                if (offset >= blockSize)
                {
                    result.AddError($"[{poolKind}] '{name}' in '{packageId}' has " +
                                    $"explicitOffset={offset} but block size is {blockSize}. " +
                                    "Offset exceeds block boundary.");
                    return null;
                }

                if (slotMap.ContainsKey(absoluteValue))
                {
                    result.AddError($"[{poolKind}] '{name}' and '{slotMap[absoluteValue]}' in " +
                                    $"'{packageId}' both pin to offset {offset}.");
                    return null;
                }

                slotMap[absoluteValue] = name;
                resolved.Add(new ResolvedEntry
                {
                    Name      = name,
                    Value     = absoluteValue,
                    Comment   = comment,
                    WasPinned = true
                });
            }

            // Pass 2: auto-assign remaining entries, preferring lock-file values
            int autoSlot = blockStart;  // walks forward

            foreach (var (name, comment, offset) in entries)
            {
                if (offset >= 0) continue;  // already handled in pass 1

                // Check lock file — did this name have a value last generation?
                var locked = lockEntries.FirstOrDefault(
                    l => l.packageId == packageId && l.name == name);

                int targetValue = -1;
                if (locked != null && locked.value >= blockStart &&
                    locked.value < blockStart + blockSize &&
                    !slotMap.ContainsKey(locked.value))
                {
                    // Lock file gives us a stable value — use it
                    targetValue = locked.value;
                }
                else
                {
                    // Advance autoSlot to next free slot
                    while (slotMap.ContainsKey(autoSlot))
                        autoSlot++;

                    if (autoSlot >= blockStart + blockSize)
                    {
                        result.AddError($"[{poolKind}] Provider '{packageId}' has overflowed " +
                                        $"its block (size {blockSize}). Add fewer entries or " +
                                        "the generator will expand the block on next run — " +
                                        "remove pinned offsets that waste slots.");
                        return null;
                    }

                    targetValue = autoSlot;
                    autoSlot++;
                }

                // Warn if value changed from last generation (would break serialised refs)
                if (locked != null && locked.value != targetValue)
                    result.AddWarning($"[{poolKind}] '{name}' in '{packageId}' changed value " +
                                      $"from {locked.value} → {targetValue}. Serialised " +
                                      "inspector fields referencing this entry will need updating. " +
                                      "Consider pinning this entry with explicitOffset.");

                slotMap[targetValue] = name;
                resolved.Add(new ResolvedEntry
                {
                    Name      = name,
                    Value     = targetValue,
                    Comment   = comment,
                    WasPinned = false
                });
            }

            // Sort by value so the file reads cleanly
            resolved.Sort((a, b) => a.Value.CompareTo(b.Value));
            return resolved;
        }

        // ── File writing ──────────────────────────────────────────────────────

        private static void WriteEnumFile(
            List<ResolvedBlock> blocks,
            string outputPath,
            string namespaceName,
            string enumName,
            string headerComment)
        {
            var sb = new StringBuilder();

            sb.AppendLine("// AUTO-GENERATED by MidManStudio Pool Type Generator.");
            sb.AppendLine("// DO NOT edit this file manually.");
            sb.AppendLine("// Modify your PoolTypeProvider assets and regenerate via");
            sb.AppendLine("//   MidManStudio > Pool Type Generator");
            sb.AppendLine($"// Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>{headerComment}</summary>");
            sb.AppendLine($"    public enum {enumName}");
            sb.AppendLine("    {");

            for (int b = 0; b < blocks.Count; b++)
            {
                var block = blocks[b];

                // Block header comment
                sb.AppendLine($"        // ── {block.DisplayName}  " +
                              $"[{block.PackageId}]" +
                              $"  (block {block.BlockStart}–{block.BlockStart + block.BlockSize - 1})" +
                              $"  ──");

                if (block.Entries.Count == 0)
                {
                    sb.AppendLine("        // (no entries defined)");
                }
                else
                {
                    foreach (var entry in block.Entries)
                    {
                        string pin     = entry.WasPinned ? " [pinned]" : "";
                        string comment = string.IsNullOrWhiteSpace(entry.Comment)
                            ? pin
                            : $" // {entry.Comment}{pin}";
                        sb.AppendLine($"        {entry.Name} = {entry.Value},{comment}");
                    }
                }

                if (b < blocks.Count - 1) sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            EnsureDirectory(outputPath);
            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }

        // ── Lock file I/O ─────────────────────────────────────────────────────

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
                Debug.LogWarning("[PoolTypeGenerator] Could not parse lock file — starting fresh.");
                return new LockFile();
            }
        }

        private static void SaveLockFile(LockFile lf, string path)
        {
            EnsureDirectory(path);
            File.WriteAllText(path, JsonUtility.ToJson(lf, prettyPrint: true), Encoding.UTF8);
        }

        private static void UpdateLockEntries(List<ResolvedBlock> blocks,
                                              List<LockEntry> lockEntries)
        {
            lockEntries.Clear();
            foreach (var block in blocks)
                foreach (var entry in block.Entries)
                    lockEntries.Add(new LockEntry
                    {
                        packageId = block.PackageId,
                        name      = entry.Name,
                        value     = entry.Value
                    });
        }

        private static void EnsureDirectory(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Generation result
    // ─────────────────────────────────────────────────────────────────────────

    public class GenerationResult
    {
        public bool          Success;
        public int           ObjectBlocksWritten;
        public int           ParticleBlocksWritten;
        public List<string>  Errors   = new List<string>();
        public List<string>  Warnings = new List<string>();

        public bool HasErrors => Errors.Count > 0;

        public void AddError(string msg)   => Errors.Add(msg);
        public void AddWarning(string msg) => Warnings.Add(msg);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Editor window  (UXML-free for maximum Unity-version compatibility —
    //  IMGUI so it works in Unity 2022.3 without extra setup)
    // ─────────────────────────────────────────────────────────────────────────

    public class PoolTypeGeneratorWindow : EditorWindow
    {
        private PoolTypeGeneratorSettingsSO _settings;
        private GenerationResult            _lastResult;
        private Vector2                     _scrollPos;

        [MenuItem("MidManStudio/Pool Type Generator")]
        public static void Open()
        {
            var w = GetWindow<PoolTypeGeneratorWindow>("Pool Type Generator");
            w.minSize = new Vector2(480, 360);
        }

        private void OnEnable()
        {
            _settings = PoolTypeGeneratorCore.FindSettings();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            DrawHeader();
            EditorGUILayout.Space(8);
            DrawSettings();
            EditorGUILayout.Space(8);
            DrawProviderSummary();
            EditorGUILayout.Space(8);
            DrawActions();
            EditorGUILayout.Space(8);
            DrawResults();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("MidManStudio — Pool Type Generator",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Reads all PoolTypeProvider assets in the project and writes " +
                "the shared enum files.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawSettings()
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
                    "Create one via:  MidManStudio > Pool Type Generator Settings",
                    MessageType.Warning);

                if (GUILayout.Button("Create Default Settings"))
                    CreateDefaultSettings();
            }
            else
            {
                var so = new SerializedObject(_settings);
                so.Update();

                EditorGUILayout.PropertyField(so.FindProperty("objectEnumOutputPath"),
                    new GUIContent("Object Enum Output"));
                EditorGUILayout.PropertyField(so.FindProperty("particleEnumOutputPath"),
                    new GUIContent("Particle Enum Output"));
                EditorGUILayout.PropertyField(so.FindProperty("lockFilePath"),
                    new GUIContent("Lock File Path"));
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

        private void DrawProviderSummary()
        {
            EditorGUILayout.LabelField("Detected Providers", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Object providers
            var objGuids = AssetDatabase.FindAssets("t:PoolTypeProviderSO");
            EditorGUILayout.LabelField($"Object Pool Providers: {objGuids.Length}",
                EditorStyles.miniLabel);
            foreach (var g in objGuids)
            {
                var path  = AssetDatabase.GUIDToAssetPath(g);
                var asset = AssetDatabase.LoadAssetAtPath<PoolTypeProviderSO>(path);
                if (asset == null) continue;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    $"  {asset.displayName}  [{asset.packageId}]  " +
                    $"pri={asset.priority}  entries={asset.EntryCount}",
                    EditorStyles.miniLabel);
                if (GUILayout.Button("Select", GUILayout.Width(50)))
                    Selection.activeObject = asset;
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(4);

            // Particle providers
            var partGuids = AssetDatabase.FindAssets("t:ParticlePoolTypeProviderSO");
            EditorGUILayout.LabelField($"Particle Pool Providers: {partGuids.Length}",
                EditorStyles.miniLabel);
            foreach (var g in partGuids)
            {
                var path  = AssetDatabase.GUIDToAssetPath(g);
                var asset = AssetDatabase.LoadAssetAtPath<ParticlePoolTypeProviderSO>(path);
                if (asset == null) continue;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    $"  {asset.displayName}  [{asset.packageId}]  " +
                    $"pri={asset.priority}  entries={asset.EntryCount}",
                    EditorStyles.miniLabel);
                if (GUILayout.Button("Select", GUILayout.Width(50)))
                    Selection.activeObject = asset;
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();

            GUI.enabled = _settings != null;
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
            if (GUILayout.Button("Generate Now", GUILayout.Height(32)))
            {
                _lastResult = PoolTypeGeneratorCore.Generate(_settings);
            }
            GUI.backgroundColor = Color.white;
            GUI.enabled = true;

            if (GUILayout.Button("Open Output Folder", GUILayout.Height(32)))
            {
                var path = _settings != null
                    ? Path.GetDirectoryName(_settings.objectEnumOutputPath)
                    : "Assets/MidManStudio/Generated";
                EditorUtility.RevealInFinder(path ?? "Assets");
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawResults()
        {
            if (_lastResult == null) return;

            EditorGUILayout.LabelField("Last Generation Result", EditorStyles.boldLabel);
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos,
                GUILayout.MaxHeight(200));

            if (_lastResult.Success)
            {
                EditorGUILayout.HelpBox(
                    $"✓ Success!\n" +
                    $"  Object blocks written:   {_lastResult.ObjectBlocksWritten}\n" +
                    $"  Particle blocks written: {_lastResult.ParticleBlocksWritten}",
                    MessageType.Info);
            }

            foreach (var w in _lastResult.Warnings)
                EditorGUILayout.HelpBox(w, MessageType.Warning);

            foreach (var e in _lastResult.Errors)
                EditorGUILayout.HelpBox(e, MessageType.Error);

            EditorGUILayout.EndScrollView();
        }

        private void CreateDefaultSettings()
        {
            const string path = "Assets/MidManStudio/Generated/PoolTypeGeneratorSettings.asset";
            var dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);

            var asset = ScriptableObject.CreateInstance<PoolTypeGeneratorSettingsSO>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            _settings = asset;
            Selection.activeObject = asset;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Auto-generate hook (optional — respects setting)
    // ─────────────────────────────────────────────────────────────────────────

    internal class PoolTypeAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted,
            string[] moved,    string[] movedFrom)
        {
            bool relevant = imported.Concat(deleted).Concat(moved)
                .Any(p => p.EndsWith(".asset") &&
                          (AssetDatabase.GetMainAssetTypeAtPath(p) == typeof(PoolTypeProviderSO) ||
                           AssetDatabase.GetMainAssetTypeAtPath(p) == typeof(ParticlePoolTypeProviderSO) ||
                           AssetDatabase.GetMainAssetTypeAtPath(p) == typeof(PoolTypeGeneratorSettingsSO)));

            if (!relevant) return;

            var settings = PoolTypeGeneratorCore.FindSettings();
            if (settings == null || !settings.autoGenerateOnAssetChange) return;

            // Defer one frame so assets are fully imported before reading them
            EditorApplication.delayCall += () =>
            {
                var result = PoolTypeGeneratorCore.Generate(settings);
                if (result.HasErrors)
                    foreach (var e in result.Errors)
                        Debug.LogError($"[PoolTypeGenerator Auto] {e}");
                else if (result.Warnings.Count > 0)
                    foreach (var w in result.Warnings)
                        Debug.LogWarning($"[PoolTypeGenerator Auto] {w}");
            };
        }
    }
}
#endif
