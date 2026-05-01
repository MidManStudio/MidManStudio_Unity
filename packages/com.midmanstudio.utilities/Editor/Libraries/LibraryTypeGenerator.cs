// LibraryTypeGenerator.cs
// Reads all LibraryTypeProviderSO assets and writes LibraryId.cs and LibraryItemId.cs.
// Replaces magic string keys with typed enum members.
// Open via: MidManStudio > Utilities > Library Type Generator

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Libraries.Generator;

namespace MidManStudio.Core.Editor.Libraries
{
    public static class LibraryTypeGeneratorCore
    {
        // ── Settings ─────────────────────────────────────────────────────────

        // Paths inside the utilities package — change if your layout differs
        private const string LibraryIdPath =
    "packages/com.midmanstudio.utilities/Runtime/Libraries/Generated/LibraryId.cs";

private const string LibraryItemPath =
    "packages/com.midmanstudio.utilities/Runtime/Libraries/Generated/LibraryItemId.cs";
        private const string Namespace       = "MidManStudio.Core.Libraries";

        // ── Entry Point ───────────────────────────────────────────────────────

        public static (bool success, List<string> errors, List<string> warnings)
            Generate()
        {
            var errors   = new List<string>();
            var warnings = new List<string>();

            var providers = CollectProviders();

            if (!Validate(providers, errors, warnings))
                return (false, errors, warnings);

            var sorted = providers
                .OrderBy(p => p.priority)
                .ThenBy(p => p.packageId)
                .ToList();

            WriteLibraryIdEnum(sorted);
            WriteLibraryItemIdEnum(sorted, warnings);

            AssetDatabase.Refresh();
            return (true, errors, warnings);
        }

        // ── Collection ────────────────────────────────────────────────────────

        private static List<LibraryTypeProviderSO> CollectProviders()
        {
            var list  = new List<LibraryTypeProviderSO>();
            var guids = AssetDatabase.FindAssets("t:LibraryTypeProviderSO");
            foreach (var g in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<LibraryTypeProviderSO>(
                    AssetDatabase.GUIDToAssetPath(g));
                if (asset != null) list.Add(asset);
            }
            return list;
        }

        // ── Validation ────────────────────────────────────────────────────────

        private static bool Validate(List<LibraryTypeProviderSO> providers,
            List<string> errors, List<string> warnings)
        {
            // Duplicate packageIds
            var dupePackages = providers
                .GroupBy(p => p.packageId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            foreach (var d in dupePackages)
                errors.Add($"Duplicate packageId '{d}'. Each provider must be unique.");

            // Gather all library names and item names for cross-provider collision check
            var allLibNames  = new List<(string name, string pkg)>();
            var allItemNames = new List<(string name, string pkg)>();

            foreach (var p in providers)
            {
                foreach (var lib in p.libraries ?? new())
                {
                    if (string.IsNullOrWhiteSpace(lib.libraryName))
                    { errors.Add($"[{p.packageId}] Library with empty name."); continue; }

                    if (!IsValidIdentifier(lib.libraryName))
                        errors.Add($"[{p.packageId}] '{lib.libraryName}' is not a valid C# identifier.");

                    allLibNames.Add((lib.libraryName, p.packageId));

                    foreach (var item in lib.itemNames ?? new())
                    {
                        if (string.IsNullOrWhiteSpace(item))
                        { warnings.Add($"[{p.packageId}/{lib.libraryName}] Empty item name skipped."); continue; }

                        if (!IsValidIdentifier(item))
                            errors.Add($"[{p.packageId}/{lib.libraryName}] '{item}' is not a valid C# identifier.");

                        // Full item key is LibraryName_ItemName
                        allItemNames.Add(($"{lib.libraryName}_{item}", p.packageId));
                    }
                }
            }

            // Duplicate library names
            foreach (var dupe in allLibNames.GroupBy(x => x.name).Where(g => g.Count() > 1))
                errors.Add($"Duplicate library name '{dupe.Key}' across packages: " +
                           string.Join(", ", dupe.Select(x => x.pkg)));

            // Duplicate item keys
            foreach (var dupe in allItemNames.GroupBy(x => x.name).Where(g => g.Count() > 1))
                errors.Add($"Duplicate item key '{dupe.Key}' across packages: " +
                           string.Join(", ", dupe.Select(x => x.pkg)));

            return errors.Count == 0;
        }

        // ── Writers ───────────────────────────────────────────────────────────

        private static void WriteLibraryIdEnum(List<LibraryTypeProviderSO> providers)
        {
            var sb = new StringBuilder();
            AppendHeader(sb, "LibraryId");
            sb.AppendLine($"namespace {Namespace}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>Library registry keys. AUTO-GENERATED — do not edit.</summary>");
            sb.AppendLine("    public enum LibraryId");
            sb.AppendLine("    {");

            int value = 0;
            foreach (var p in providers)
            {
                sb.AppendLine($"        // ── {p.displayName}  [{p.packageId}]  priority={p.priority} ──");

                if (p.libraries == null || p.libraries.Count == 0)
                {
                    sb.AppendLine("        // (no libraries defined)");
                    sb.AppendLine();
                    continue;
                }

                foreach (var lib in p.libraries)
                {
                    if (string.IsNullOrWhiteSpace(lib.libraryName)) continue;
                    string comment = string.IsNullOrWhiteSpace(lib.comment)
                        ? "" : $" // {lib.comment}";
                    sb.AppendLine($"        {lib.libraryName} = {value++},{comment}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            WriteFile(LibraryIdPath, sb.ToString());
        }

        private static void WriteLibraryItemIdEnum(List<LibraryTypeProviderSO> providers,
            List<string> warnings)
        {
            var sb = new StringBuilder();
            AppendHeader(sb, "LibraryItemId");
            sb.AppendLine($"namespace {Namespace}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>Library item keys. AUTO-GENERATED — do not edit.</summary>");
            sb.AppendLine("    public enum LibraryItemId");
            sb.AppendLine("    {");

            int value = 0;
            foreach (var p in providers)
            {
                sb.AppendLine($"        // ── {p.displayName}  [{p.packageId}] ──");

                foreach (var lib in p.libraries ?? new())
                {
                    if (string.IsNullOrWhiteSpace(lib.libraryName)) continue;
                    if (lib.itemNames == null || lib.itemNames.Count == 0)
                    {
                        sb.AppendLine($"        // {lib.libraryName}: (no items)");
                        continue;
                    }

                    sb.AppendLine($"        // {lib.libraryName}");
                    foreach (var item in lib.itemNames)
                    {
                        if (string.IsNullOrWhiteSpace(item)) continue;
                        sb.AppendLine($"        {lib.libraryName}_{item} = {value++},");
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            WriteFile(LibraryItemPath, sb.ToString());
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private static void AppendHeader(StringBuilder sb, string typeName)
        {
            sb.AppendLine("// AUTO-GENERATED by MidManStudio Library Type Generator.");
            sb.AppendLine("// DO NOT edit manually.");
            sb.AppendLine("// Regenerate via: MidManStudio > Utilities > Library Type Generator");
            sb.AppendLine($"// Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
        }

        private static void WriteFile(string path, string content)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, Encoding.UTF8);
            Debug.Log($"[LibraryTypeGenerator] Wrote → {path}");
        }

        private static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!char.IsLetter(name[0]) && name[0] != '_') return false;
            foreach (var c in name) if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }
    }

    // ── Editor Window ─────────────────────────────────────────────────────────

    public class LibraryTypeGeneratorWindow : EditorWindow
    {
        private Vector2 _scroll;
        private List<string> _lastErrors   = new();
        private List<string> _lastWarnings = new();
        private bool         _lastSuccess;
        private bool         _ranOnce;

        [MenuItem("MidManStudio/Utilities/Library Type Generator")]
        public static void Open()
        {
            var w = GetWindow<LibraryTypeGeneratorWindow>("Library Type Generator");
            w.minSize = new Vector2(480, 340);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("MidManStudio — Library Type Generator",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Generates LibraryId and LibraryItemId enums from LibraryTypeProviderSO assets.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(8);
            DrawProviderList();
            EditorGUILayout.Space(8);

            var old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.3f, 0.85f, 0.3f);
            if (GUILayout.Button("⚙  Generate Now", GUILayout.Height(36)))
            {
                var (success, errors, warnings) = LibraryTypeGeneratorCore.Generate();
                _lastSuccess  = success;
                _lastErrors   = errors;
                _lastWarnings = warnings;
                _ranOnce      = true;
            }
            GUI.backgroundColor = old;

            if (!_ranOnce) return;

            EditorGUILayout.Space(6);
            if (_lastSuccess)
                EditorGUILayout.HelpBox("✓ Generation successful.", MessageType.Info);

            foreach (var w in _lastWarnings)
                EditorGUILayout.HelpBox(w, MessageType.Warning);
            foreach (var e in _lastErrors)
                EditorGUILayout.HelpBox(e, MessageType.Error);
        }

        private void DrawProviderList()
        {
            EditorGUILayout.LabelField("Discovered Providers", EditorStyles.boldLabel);
            var guids = AssetDatabase.FindAssets("t:LibraryTypeProviderSO");

            if (guids.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No LibraryTypeProviderSO assets found.\n" +
                    "Create one via: MidManStudio > Utilities > Library Type Provider",
                    MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(140));
            foreach (var g in guids)
            {
                var path  = AssetDatabase.GUIDToAssetPath(g);
                var asset = AssetDatabase.LoadAssetAtPath<LibraryTypeProviderSO>(path);
                if (asset == null) continue;

                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField(
                    $"[{asset.priority:D3}]  {asset.displayName}  ({asset.packageId})" +
                    $"  —  {asset.LibraryCount} lib(s)  /  {asset.TotalItemCount()} item(s)",
                    EditorStyles.miniLabel);
                if (GUILayout.Button("Select", GUILayout.Width(50)))
                    Selection.activeObject = asset;
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
#endif
