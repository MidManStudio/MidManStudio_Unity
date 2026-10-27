// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/pool-systems.md, section "PoolTypeGenerator.cs"
// ============================================================================
// Reads all ObjectPoolTypeProviderSO / ParticlePoolTypeProviderSO / NetworkPoolTypeProviderSO
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
using MidManStudio.Core.EditorUtils.Generators;

namespace MidManStudio.Core.Pools.Generator
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Lock file
    // ─────────────────────────────────────────────────────────────────────────

    // One group of three lists per enum: the numbers in use, the block ranges, and the numbers
    // of removed entries. A lock file written before blocks and retired numbers existed only
    // has the three *Entries lists. The other lists stay empty when it is read.
    [Serializable]
    internal class LockFile
    {
        public List<EnumLockEntry> objectEntries = new();
        public List<EnumLockBlock> objectBlocks  = new();
        public List<EnumLockEntry> objectRetired = new();

        public List<EnumLockEntry> particleEntries = new();
        public List<EnumLockBlock> particleBlocks  = new();
        public List<EnumLockEntry> particleRetired = new();

        public List<EnumLockEntry> networkEntries = new();
        public List<EnumLockBlock> networkBlocks  = new();
        public List<EnumLockEntry> networkRetired = new();

        // JsonUtility can leave a list null when the file has the key with no value.
        public void Normalize()
        {
            objectEntries   ??= new();
            objectBlocks    ??= new();
            objectRetired   ??= new();
            particleEntries ??= new();
            particleBlocks  ??= new();
            particleRetired ??= new();
            networkEntries  ??= new();
            networkBlocks   ??= new();
            networkRetired  ??= new();
        }

        public EnumLockSection ObjectSection() =>
            new EnumLockSection { entries = objectEntries, blocks = objectBlocks, retired = objectRetired };

        public EnumLockSection ParticleSection() =>
            new EnumLockSection { entries = particleEntries, blocks = particleBlocks, retired = particleRetired };

        public EnumLockSection NetworkSection() =>
            new EnumLockSection { entries = networkEntries, blocks = networkBlocks, retired = networkRetired };

        public void SetObject(EnumLockSection s)
        {
            objectEntries = s.entries;
            objectBlocks  = s.blocks;
            objectRetired = s.retired;
        }

        public void SetParticle(EnumLockSection s)
        {
            particleEntries = s.entries;
            particleBlocks  = s.blocks;
            particleRetired = s.retired;
        }

        public void SetNetwork(EnumLockSection s)
        {
            networkEntries = s.entries;
            networkBlocks  = s.blocks;
            networkRetired = s.retired;
        }
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

            var lockFile = LoadLockFile(settings.lockFilePath, result);
            if (lockFile == null) return result;

            // All three enums are worked out before any file is written, so an error in one
            // leaves the files and the lock file as they were.
            var objectProviders = CollectObjectProviders();
            LogProviders("Object", objectProviders);
            var objectBlocks = EnumBlockLogic.Assign(
                objectProviders, settings.minimumBlockSize, lockFile.ObjectSection(),
                result.Errors, result.Warnings, "Object", out var newObject);

            var particleProviders = CollectParticleProviders();
            LogProviders("Particle", particleProviders);
            var particleBlocks = EnumBlockLogic.Assign(
                particleProviders, settings.minimumBlockSize, lockFile.ParticleSection(),
                result.Errors, result.Warnings, "Particle", out var newParticle);

            // Loaded via SerializedObject reflection — no netcode assembly reference needed.
            var networkProviders = CollectNetworkProviders();
            LogProviders("NetworkObject", networkProviders);
            var networkBlocks = EnumBlockLogic.Assign(
                networkProviders, settings.minimumBlockSize, lockFile.NetworkSection(),
                result.Errors, result.Warnings, "NetworkObject", out var newNetwork);

            if (result.HasErrors) return result;

            try
            {
                WriteEnumFile(objectBlocks, settings.objectEnumOutputPath,
                    settings.generatedNamespace, "PoolableObjectType",
                    "Object pool type IDs. AUTO-GENERATED — do not edit manually.");
                WriteEnumFile(particleBlocks, settings.particleEnumOutputPath,
                    settings.generatedNamespace, "PoolableParticleType",
                    "Particle pool type IDs. AUTO-GENERATED — do not edit manually.");
                WriteEnumFile(networkBlocks, settings.networkEnumOutputPath,
                    settings.generatedNamespace, "PoolableNetworkObjectType",
                    "Network object pool type IDs. AUTO-GENERATED — do not edit manually.");

                lockFile.SetObject(newObject);
                lockFile.SetParticle(newParticle);
                lockFile.SetNetwork(newNetwork);
                SaveLockFile(lockFile, settings.lockFilePath);
            }
            catch (Exception e)
            {
                result.AddError("Could not write the generated files: " + e.Message);
                return result;
            }

            result.ObjectBlocksWritten   = objectBlocks.Count;
            result.ParticleBlocksWritten = particleBlocks.Count;
            result.NetworkBlocksWritten  = networkBlocks.Count;
            AssetDatabase.Refresh();
            result.Success = true;
            return result;
        }

        // ── Provider collection ───────────────────────────────────────────────

        private static List<EnumProviderData> CollectObjectProviders()
        {
            var list  = new List<EnumProviderData>();
            var guids = AssetDatabase.FindAssets("t:ObjectPoolTypeProviderSO");
            foreach (var g in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<ObjectPoolTypeProviderSO>(
                    AssetDatabase.GUIDToAssetPath(g));
                if (asset == null) continue;
                list.Add(ToProvider(asset.packageId, asset.displayName, asset.priority, asset.entries));
            }
            return list;
        }

        private static List<EnumProviderData> CollectParticleProviders()
        {
            var list  = new List<EnumProviderData>();
            var guids = AssetDatabase.FindAssets("t:ParticlePoolTypeProviderSO");
            foreach (var g in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<ParticlePoolTypeProviderSO>(
                    AssetDatabase.GUIDToAssetPath(g));
                if (asset == null) continue;
                list.Add(ToProvider(asset.packageId, asset.displayName, asset.priority, asset.entries));
            }
            return list;
        }

        // Reflection-based — no direct reference to NetworkPoolTypeProviderSO type.
        // Works regardless of whether com.midmanstudio.netcode is in the project.
        private static List<EnumProviderData> CollectNetworkProviders()
        {
            var list  = new List<EnumProviderData>();
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

                var data = new EnumProviderData
                {
                    PackageId   = packageId,
                    DisplayName = displayName,
                    Priority    = priority
                };
                if (entriesProp != null)
                {
                    for (int i = 0; i < entriesProp.arraySize; i++)
                    {
                        var elem    = entriesProp.GetArrayElementAtIndex(i);
                        var eName   = elem.FindPropertyRelative("entryName")?.stringValue    ?? "";
                        var comment = elem.FindPropertyRelative("comment")?.stringValue      ?? "";
                        var offset  = elem.FindPropertyRelative("explicitOffset")?.intValue  ?? -1;
                        data.Entries.Add(new EnumEntryData(eName, comment, offset));
                    }
                }
                list.Add(data);
            }
            return list;
        }

        private static EnumProviderData ToProvider(string packageId, string displayName, int priority,
            List<PoolEntryDefinition> entries)
        {
            var data = new EnumProviderData { PackageId = packageId, DisplayName = displayName, Priority = priority };
            if (entries == null) return data;
            foreach (var e in entries)
                if (e != null) data.Entries.Add(new EnumEntryData(e.entryName, e.comment, e.explicitOffset));
            return data;
        }

        private static void LogProviders(string kind, List<EnumProviderData> providers)
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

        // ── File writing ──────────────────────────────────────────────────────

        private static void WriteEnumFile(
            List<EnumResolvedBlock> blocks, string outputPath,
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
                    $"        // ── {EnumBlockLogic.OneLine(blk.DisplayName)}  [{EnumBlockLogic.OneLine(blk.PackageId)}]" +
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
                        string pinTag  = entry.Pinned ? " //[pinned]" : "";
                        string comment = EnumBlockLogic.OneLine(entry.Comment);
                        string text    = comment.Length == 0 ? pinTag : $" // {comment}{pinTag}";
                        sb.AppendLine($"        {entry.Name} = {entry.Value},{text}");
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

        private static LockFile LoadLockFile(string path, GenerationResult result)
        {
            if (!File.Exists(path)) return new LockFile();
            try
            {
                var file = JsonUtility.FromJson<LockFile>(File.ReadAllText(path));
                if (file != null) { file.Normalize(); return file; }
            }
            catch (Exception)
            {
                // Falls through to the error below.
            }
            // Starting over would renumber every pool type, so stop and let the owner decide.
            result.AddError($"The lock file '{path}' could not be read. Fix it or delete it, then generate again. " +
                            "Deleting it lets all values be assigned again.");
            return null;
        }

        private static void SaveLockFile(LockFile lf, string path)
        {
            EnsureDirectory(path);
            File.WriteAllText(path, JsonUtility.ToJson(lf, prettyPrint: true), Encoding.UTF8);
            Debug.Log($"[PoolTypeGenerator] Lock file saved → {path}");
        }

        private static void EnsureDirectory(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
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

[MenuItem("MidManStudio/Utilities/Pool Type Generator",       priority = 101)]
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

            DrawProviderGroup<ObjectPoolTypeProviderSO>(
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
                CreateProvider<ObjectPoolTypeProviderSO>("PoolTypeProvider_MyGame.asset");
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
            "ObjectPoolTypeProviderSO",
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
