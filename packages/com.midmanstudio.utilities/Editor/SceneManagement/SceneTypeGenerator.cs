// SceneTypeGenerator.cs
// Generates SceneId.cs and SceneRegistry.cs from all SceneTypeProviderSO assets.
// Open via: MidManStudio > Utilities > Scene Type Generator

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.SceneManagement.Generator;

namespace MidManStudio.Core.Editor.SceneManagement
{
    public static class SceneTypeGeneratorCore
    {
        private const string SceneIdPath =
            "packages/com.midmanstudio.utilities/Runtime/SceneManagement/Generated/SceneId.cs";
        private const string RegistryPath =
            "packages/com.midmanstudio.utilities/Runtime/SceneManagement/Generated/SceneRegistry.cs";
        private const string Namespace = "MidManStudio.Core.SceneManagement";

        public static (bool success, List<string> errors, List<string> warnings) Generate()
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

            WriteSceneIdEnum(sorted);
            WriteSceneRegistry(sorted);

            AssetDatabase.Refresh();
            return (true, errors, warnings);
        }

        private static List<SceneTypeProviderSO> CollectProviders()
        {
            var list  = new List<SceneTypeProviderSO>();
            var guids = AssetDatabase.FindAssets("t:SceneTypeProviderSO");
            foreach (var g in guids)
            {
                var a = AssetDatabase.LoadAssetAtPath<SceneTypeProviderSO>(
                    AssetDatabase.GUIDToAssetPath(g));
                if (a != null) list.Add(a);
            }
            return list;
        }

        private static bool Validate(List<SceneTypeProviderSO> providers,
            List<string> errors, List<string> warnings)
        {
            // Duplicate packageIds
            foreach (var d in providers.GroupBy(p => p.packageId).Where(g => g.Count() > 1))
                errors.Add($"Duplicate packageId '{d.Key}'.");

            // All scene entries flat
            var all = providers
                .SelectMany(p => (p.scenes ?? new()).Select(s => (s, p.packageId)))
                .ToList();

            // Duplicate build indices
            foreach (var d in all.GroupBy(x => x.s.buildIndex).Where(g => g.Count() > 1))
                errors.Add($"Duplicate buildIndex {d.Key} — " +
                           string.Join(", ", d.Select(x => $"{x.packageId}/{x.s.enumName}")));

            // Duplicate enum names (excluding blank/none)
            foreach (var d in all
                .Where(x => !string.IsNullOrWhiteSpace(x.s.enumName))
                .GroupBy(x => x.s.enumName)
                .Where(g => g.Count() > 1))
                errors.Add($"Duplicate enum name '{d.Key}'.");

            // Invalid identifiers
            foreach (var (s, pkg) in all)
            {
                if (string.IsNullOrWhiteSpace(s.enumName))
                { warnings.Add($"[{pkg}] Scene with buildIndex={s.buildIndex} has no enumName — skipped."); }
                else if (!IsValidIdentifier(s.enumName))
                    errors.Add($"[{pkg}] '{s.enumName}' is not a valid C# identifier.");

                if (string.IsNullOrWhiteSpace(s.sceneName))
                    warnings.Add($"[{pkg}] '{s.enumName}' has no sceneName — will be empty string in registry.");
            }

            return errors.Count == 0;
        }

        private static void WriteSceneIdEnum(List<SceneTypeProviderSO> providers)
        {
            var sb = new StringBuilder();
            AppendHeader(sb);
            sb.AppendLine($"namespace {Namespace}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>Scene build indices. AUTO-GENERATED — do not edit.</summary>");
            sb.AppendLine("    public enum SceneId");
            sb.AppendLine("    {");
            sb.AppendLine("        None = -1,");
            sb.AppendLine();

            foreach (var p in providers)
            {
                sb.AppendLine($"        // ── {p.displayName}  [{p.packageId}]  priority={p.priority} ──");
                foreach (var s in (p.scenes ?? new()).Where(s => !string.IsNullOrWhiteSpace(s.enumName)))
                {
                    string c = string.IsNullOrWhiteSpace(s.comment) ? "" : $" // {s.comment}";
                    sb.AppendLine($"        {s.enumName} = {s.buildIndex},{c}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            WriteFile(SceneIdPath, sb.ToString());
        }

        private static void WriteSceneRegistry(List<SceneTypeProviderSO> providers)
        {
            var all = providers
                .SelectMany(p => (p.scenes ?? new())
                    .Where(s => !string.IsNullOrWhiteSpace(s.enumName)))
                .ToList();

            var sb = new StringBuilder();
            AppendHeader(sb);
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine();
            sb.AppendLine($"namespace {Namespace}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>Baked scene metadata. AUTO-GENERATED — do not edit.</summary>");
            sb.AppendLine("    public static class SceneRegistry");
            sb.AppendLine("    {");
            sb.AppendLine("        private static readonly Dictionary<int, string> _names = new()");
            sb.AppendLine("        {");
            sb.AppendLine("            { -1, \"\" },");
            foreach (var s in all)
                sb.AppendLine($"            {{ {s.buildIndex}, \"{s.sceneName}\" }},");
            sb.AppendLine("        };");
            sb.AppendLine();
            sb.AppendLine("        private static readonly Dictionary<int, SceneNetworkDependency> _deps = new()");
            sb.AppendLine("        {");
            sb.AppendLine("            { -1, SceneNetworkDependency.None },");
            foreach (var s in all)
                sb.AppendLine($"            {{ {s.buildIndex}, SceneNetworkDependency.{s.networkDependency} }},");
            sb.AppendLine("        };");
            sb.AppendLine();
            sb.AppendLine("        public static string GetName(SceneId id) =>");
            sb.AppendLine("            _names.TryGetValue((int)id, out var n) ? n : \"\";");
            sb.AppendLine("        public static string GetName(int id) =>");
            sb.AppendLine("            _names.TryGetValue(id, out var n) ? n : \"\";");
            sb.AppendLine("        public static SceneNetworkDependency GetDependency(SceneId id) =>");
            sb.AppendLine("            _deps.TryGetValue((int)id, out var d) ? d : SceneNetworkDependency.None;");
            sb.AppendLine("        public static SceneNetworkDependency GetDependency(int id) =>");
            sb.AppendLine("            _deps.TryGetValue(id, out var d) ? d : SceneNetworkDependency.None;");
            sb.AppendLine("        public static bool IsKnown(int id) => _names.ContainsKey(id);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            WriteFile(RegistryPath, sb.ToString());
        }

        private static void AppendHeader(StringBuilder sb)
        {
            sb.AppendLine("// AUTO-GENERATED by MidManStudio Scene Type Generator.");
            sb.AppendLine("// DO NOT edit manually.");
            sb.AppendLine("// Regenerate via: MidManStudio > Utilities > Scene Type Generator");
            sb.AppendLine($"// Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
        }

        private static void WriteFile(string path, string content)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, Encoding.UTF8);
            Debug.Log($"[SceneTypeGenerator] Wrote → {path}");
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

    public class SceneTypeGeneratorWindow : EditorWindow
    {
        private List<string> _errors   = new();
        private List<string> _warnings = new();
        private bool         _ranOnce;
        private bool         _lastSuccess;
        private Vector2      _scroll;

        [MenuItem("MidManStudio/Utilities/Scene Type Generator")]
        public static void Open()
        {
            var w = GetWindow<SceneTypeGeneratorWindow>("Scene Type Generator");
            w.minSize = new Vector2(480, 320);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("MidManStudio — Scene Type Generator",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Generates SceneId.cs and SceneRegistry.cs from SceneTypeProviderSO assets.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(8);
            DrawProviderList();
            EditorGUILayout.Space(8);

            var old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.3f, 0.85f, 0.3f);
            if (GUILayout.Button("⚙  Generate Now", GUILayout.Height(36)))
            {
                var (ok, errs, warns) = SceneTypeGeneratorCore.Generate();
                _lastSuccess = ok; _errors = errs; _warnings = warns; _ranOnce = true;
            }
            GUI.backgroundColor = old;

            if (!_ranOnce) return;
            EditorGUILayout.Space(6);
            if (_lastSuccess)
                EditorGUILayout.HelpBox("✓ Generation successful.", MessageType.Info);
            foreach (var w in _warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
            foreach (var e in _errors)   EditorGUILayout.HelpBox(e, MessageType.Error);
        }

        private void DrawProviderList()
        {
            EditorGUILayout.LabelField("Discovered Providers", EditorStyles.boldLabel);
            var guids = AssetDatabase.FindAssets("t:SceneTypeProviderSO");
            if (guids.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No SceneTypeProviderSO assets found.\n" +
                    "Create one via: MidManStudio > Utilities > Scene Type Provider",
                    MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(120));
            foreach (var g in guids)
            {
                var a = AssetDatabase.LoadAssetAtPath<SceneTypeProviderSO>(
                    AssetDatabase.GUIDToAssetPath(g));
                if (a == null) continue;
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField(
                    $"[{a.priority:D3}]  {a.displayName}  ({a.packageId})  —  {a.SceneCount} scene(s)",
                    EditorStyles.miniLabel);
                if (GUILayout.Button("Select", GUILayout.Width(50)))
                    Selection.activeObject = a;
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
#endif
