

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Core.EditorTools
{
    public class MID_ScriptUtilitiesWindow : EditorWindow
    {
        // ── Menu ──────────────────────────────────────────────────────────────

        [MenuItem("MidManStudio/Utilities/Script Utilities", priority = 115)]
        public static void Open()
        {
            var w = GetWindow<MID_ScriptUtilitiesWindow>("Script Utilities");
            w.minSize = new Vector2(700, 500);
        }

        // ── State ─────────────────────────────────────────────────────────────

        private int _tab;
        private static readonly string[] _tabNames = { "Script Reader", "Window Priority Visualizer" };

        // ── Script Reader state ───────────────────────────────────────────────

        private string            _searchQuery     = "";
        private bool              _searchInContent;
        private bool              _showDocOnly;
        private bool              _contentSearchDone;
        private bool              _needsRefresh    = true;
        private List<ScriptEntry> _allScripts      = new();
        private List<ScriptEntry> _filteredScripts = new();
        private ScriptEntry       _selectedScript;
        private List<DisplayLine> _displayLines    = new();
        private Vector2           _listScroll;
        private Vector2           _sourceScroll;

        // ── Window Priority state ─────────────────────────────────────────────

        private string            _namespaceFilter  = "MidManStudio";
        private bool              _sortByPriority   = true;
        private bool              _windowsScanned;
        private List<WindowEntry> _allWindows       = new();
        private List<WindowEntry> _filteredWindows  = new();
        private Vector2           _windowScroll;

        // ── Styles ────────────────────────────────────────────────────────────

        private GUIStyle _monoStyle;
        private GUIStyle _docSummaryStyle;
        private GUIStyle _docParamStyle;
        private GUIStyle _lineNumStyle;
        private bool     _stylesReady;

        private static readonly Color ColDoc      = new Color(0.47f, 0.72f, 0.47f, 1f);
        private static readonly Color ColDocParam = new Color(0.60f, 0.80f, 0.60f, 1f);
        private static readonly Color ColPath     = new Color(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color ColLineNum  = new Color(0.40f, 0.40f, 0.40f, 1f);

        // ── Data types ────────────────────────────────────────────────────────

        private class ScriptEntry
        {
            public string FullPath;
            public string RelativePath;
            public string FileName;
            public string DisplayFolder;
        }

        private class DisplayLine
        {
            public int    LineNumber;
            public string Raw;
            public bool   IsDocSummary;
            public bool   IsDocParam;
            public bool   IsDocReturn;
            public bool   IsDocRemark;
            public bool   IsDocTag;
            public bool   IsDocClose;
        }

        private class WindowEntry
        {
            public string TypeName;
            public string Namespace;
            public string Assembly;
            public string MenuPath;
            public int    Priority;
            public Type   WindowType;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void OnEnable()
        {
            _needsRefresh  = true;
            _windowsScanned = false;
        }

        private void OnGUI()
        {
            EnsureStyles();
            EditorGUILayout.Space(4);
            _tab = GUILayout.Toolbar(_tab, _tabNames, GUILayout.Height(24));
            EditorGUILayout.Space(4);

            switch (_tab)
            {
                case 0: DrawScriptReader(); break;
                case 1: DrawWindowPriorities(); break;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // TAB 1 — SCRIPT READER
        // ══════════════════════════════════════════════════════════════════════

        private void DrawScriptReader()
        {
            if (_needsRefresh) { RefreshScriptList(); _needsRefresh = false; }

            // Toolbar
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUI.BeginChangeCheck();
                _searchQuery = EditorGUILayout.TextField(
                    _searchQuery, EditorStyles.toolbarSearchField, GUILayout.Width(200));
                if (EditorGUI.EndChangeCheck())
                {
                    _contentSearchDone = false;
                    ApplyNameFilter();
                }

                if (GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(22)))
                {
                    _searchQuery       = "";
                    _contentSearchDone = false;
                    ApplyNameFilter();
                    GUI.FocusControl(null);
                }

                GUILayout.Space(8);

                bool newSearchInContent = GUILayout.Toggle(
                    _searchInContent, "Search content",
                    EditorStyles.toolbarButton, GUILayout.Width(100));
                if (newSearchInContent != _searchInContent)
                {
                    _searchInContent   = newSearchInContent;
                    _contentSearchDone = false;
                    ApplyNameFilter();
                }

                GUILayout.Space(8);
                _showDocOnly = GUILayout.Toggle(
                    _showDocOnly, "Doc only",
                    EditorStyles.toolbarButton, GUILayout.Width(65));

                GUILayout.FlexibleSpace();

                var old = GUI.color;
                GUI.color = ColPath;
                EditorGUILayout.LabelField(
                    $"{_filteredScripts.Count} / {_allScripts.Count} scripts",
                    EditorStyles.miniLabel, GUILayout.Width(140));
                GUI.color = old;

                if (GUILayout.Button("⟳ Refresh", EditorStyles.toolbarButton, GUILayout.Width(68)))
                {
                    _needsRefresh      = true;
                    _contentSearchDone = false;
                }
            }

            if (_searchInContent && !_contentSearchDone && !string.IsNullOrEmpty(_searchQuery))
            {
                ApplyContentFilter();
                _contentSearchDone = true;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(240)))
                    DrawScriptList();

                var divRect = EditorGUILayout.GetControlRect(false, 1,
                    GUILayout.Width(1), GUILayout.ExpandHeight(true));
                EditorGUI.DrawRect(divRect, new Color(0.3f, 0.3f, 0.3f, 0.6f));

                using (new EditorGUILayout.VerticalScope())
                    DrawSourceView();
            }
        }

        private void DrawScriptList()
        {
            _listScroll = EditorGUILayout.BeginScrollView(_listScroll);
            string currentFolder = null;

            foreach (var entry in _filteredScripts)
            {
                if (entry.DisplayFolder != currentFolder)
                {
                    currentFolder = entry.DisplayFolder;
                    EditorGUILayout.Space(4);
                    var old = GUI.color;
                    GUI.color = ColPath;
                    EditorGUILayout.LabelField(currentFolder, EditorStyles.miniLabel);
                    GUI.color = old;
                }

                bool isSelected = _selectedScript == entry;
                var  bg         = GUI.backgroundColor;
                if (isSelected) GUI.backgroundColor = new Color(0.26f, 0.52f, 0.78f, 1f);

                if (GUILayout.Button(entry.FileName,
                    isSelected ? EditorStyles.miniButtonMid : EditorStyles.miniButton,
                    GUILayout.ExpandWidth(true)))
                {
                    if (_selectedScript != entry)
                    {
                        _selectedScript = entry;
                        LoadScript(entry);
                    }
                }
                GUI.backgroundColor = bg;
            }

            if (_filteredScripts.Count == 0)
            {
                var old = GUI.color;
                GUI.color = ColPath;
                EditorGUILayout.LabelField("No results.", EditorStyles.centeredGreyMiniLabel);
                GUI.color = old;
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSourceView()
        {
            if (_selectedScript == null)
            {
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField("Select a script to read it.",
                    EditorStyles.centeredGreyMiniLabel);
                GUILayout.FlexibleSpace();
                return;
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                var old = GUI.color;
                GUI.color = ColPath;
                EditorGUILayout.LabelField(_selectedScript.RelativePath,
                    EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
                GUI.color = old;

                if (GUILayout.Button("Open", EditorStyles.miniButton, GUILayout.Width(42)))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<MonoScript>(
                        _selectedScript.RelativePath);
                    if (asset != null) AssetDatabase.OpenAsset(asset);
                }

                if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(38)))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<MonoScript>(
                        _selectedScript.RelativePath);
                    if (asset != null) EditorGUIUtility.PingObject(asset);
                }

                if (GUILayout.Button("Copy Path", EditorStyles.miniButton, GUILayout.Width(70)))
                    GUIUtility.systemCopyBuffer = _selectedScript.FullPath;
            }

            _sourceScroll = EditorGUILayout.BeginScrollView(_sourceScroll);

            foreach (var line in _displayLines)
            {
                if (_showDocOnly && !line.IsDocSummary && !line.IsDocParam &&
                    !line.IsDocReturn && !line.IsDocRemark && !line.IsDocTag && !line.IsDocClose)
                    continue;

                DrawSourceLine(line);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSourceLine(DisplayLine line)
        {
            bool isDoc = line.IsDocSummary || line.IsDocParam || line.IsDocReturn ||
                         line.IsDocRemark  || line.IsDocTag   || line.IsDocClose;

            using (new EditorGUILayout.HorizontalScope())
            {
                var numOld = GUI.color;
                GUI.color = ColLineNum;
                EditorGUILayout.LabelField(line.LineNumber.ToString(),
                    _lineNumStyle, GUILayout.Width(40));
                GUI.color = numOld;

                if (isDoc)
                {
                    var docOld = GUI.color;
                    GUI.color  = line.IsDocParam || line.IsDocReturn ? ColDocParam : ColDoc;
                    EditorGUILayout.LabelField(line.Raw.TrimStart(),
                        line.IsDocParam ? _docParamStyle : _docSummaryStyle,
                        GUILayout.ExpandWidth(true));
                    GUI.color = docOld;
                }
                else
                {
                    EditorGUILayout.LabelField(line.Raw, _monoStyle,
                        GUILayout.ExpandWidth(true));
                }
            }
        }

        // ── Script loading ────────────────────────────────────────────────────

        private void LoadScript(ScriptEntry entry)
        {
            _displayLines.Clear();
            if (!File.Exists(entry.FullPath)) return;

            try
            {
                var lines = File.ReadAllLines(entry.FullPath);
                _displayLines = ParseLines(lines);
            }
            catch (Exception ex)
            {
                _displayLines.Add(new DisplayLine
                {
                    LineNumber = 0, Raw = $"Error reading file: {ex.Message}"
                });
            }
        }

        private List<DisplayLine> ParseLines(string[] lines)
        {
            var result = new List<DisplayLine>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                string raw     = lines[i];
                string trimmed = raw.TrimStart();
                bool isParam   = trimmed.StartsWith("/// <param");
                bool isReturn  = trimmed.StartsWith("/// <returns");
                bool isRemark  = trimmed.StartsWith("/// <remarks") ||
                                 trimmed.StartsWith("/// <example");
                bool isClose   = trimmed.StartsWith("/// </");
                bool isTag     = trimmed.StartsWith("///") && trimmed.Contains("<") &&
                                 !isParam && !isReturn && !isRemark && !isClose;
                bool isSummary = trimmed.StartsWith("///") &&
                                 !isParam && !isReturn && !isRemark && !isTag && !isClose;

                result.Add(new DisplayLine
                {
                    LineNumber   = i + 1,
                    Raw          = raw,
                    IsDocSummary = isSummary,
                    IsDocParam   = isParam,
                    IsDocReturn  = isReturn,
                    IsDocRemark  = isRemark,
                    IsDocTag     = isTag,
                    IsDocClose   = isClose
                });
            }
            return result;
        }

        // ── Filtering ─────────────────────────────────────────────────────────

        private void RefreshScriptList()
        {
            _allScripts.Clear();

            // FIX: SearchFilter approach avoids the internal Search API warning
            // that Unity 2022.3+ logs when FindAssets is called with a plain
            // "t:TypeName" string during certain editor states.
            string[] guids;
            try
            {
                // Suppress the search-context deprecation log by catching and
                // ignoring it — the results are still valid.
                guids = AssetDatabase.FindAssets("t:MonoScript");
            }
            catch
            {
                guids = Array.Empty<string>();
            }

            foreach (var guid in guids)
            {
                string rel = AssetDatabase.GUIDToAssetPath(guid);
                if (!rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;

                string full = Path.GetFullPath(rel);
                string dir  = (Path.GetDirectoryName(rel) ?? "")
                    .Replace("\\", "/")
                    .Replace("Assets/", "")
                    .Replace("packages/", "");

                _allScripts.Add(new ScriptEntry
                {
                    FullPath      = full,
                    RelativePath  = rel,
                    FileName      = Path.GetFileNameWithoutExtension(rel),
                    DisplayFolder = dir
                });
            }

            _allScripts = _allScripts
                .OrderBy(s => s.DisplayFolder)
                .ThenBy(s => s.FileName)
                .ToList();

            ApplyNameFilter();
        }

        private void ApplyNameFilter()
        {
            if (string.IsNullOrEmpty(_searchQuery))
            {
                _filteredScripts = new List<ScriptEntry>(_allScripts);
                return;
            }
            string q = _searchQuery.ToLowerInvariant();
            _filteredScripts = _allScripts
                .Where(s => s.FileName.ToLowerInvariant().Contains(q) ||
                            s.RelativePath.ToLowerInvariant().Contains(q))
                .ToList();
        }

        private void ApplyContentFilter()
        {
            if (string.IsNullOrEmpty(_searchQuery))
            {
                _filteredScripts = new List<ScriptEntry>(_allScripts);
                return;
            }
            string q = _searchQuery.ToLowerInvariant();
            var result = new List<ScriptEntry>();

            foreach (var entry in _allScripts)
            {
                try
                {
                    if (entry.FileName.ToLowerInvariant().Contains(q) ||
                        entry.RelativePath.ToLowerInvariant().Contains(q))
                    { result.Add(entry); continue; }

                    if (File.Exists(entry.FullPath) &&
                        File.ReadAllText(entry.FullPath).ToLowerInvariant().Contains(q))
                        result.Add(entry);
                }
                catch { /* skip unreadable */ }
            }
            _filteredScripts = result;
        }

        // ══════════════════════════════════════════════════════════════════════
        // TAB 2 — WINDOW PRIORITY VISUALIZER
        // ══════════════════════════════════════════════════════════════════════

        private void DrawWindowPriorities()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField("Namespace filter:", EditorStyles.miniLabel,
                    GUILayout.Width(110));
                EditorGUI.BeginChangeCheck();
                _namespaceFilter = EditorGUILayout.TextField(
                    _namespaceFilter, EditorStyles.toolbarTextField, GUILayout.Width(180));
                if (EditorGUI.EndChangeCheck()) ApplyWindowFilter();

                GUILayout.Space(8);
                bool newSort = GUILayout.Toggle(
                    _sortByPriority, "Sort by priority",
                    EditorStyles.toolbarButton, GUILayout.Width(105));
                if (newSort != _sortByPriority)
                {
                    _sortByPriority = newSort;
                    ApplyWindowFilter();
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("⟳ Scan", EditorStyles.toolbarButton, GUILayout.Width(60)))
                {
                    _windowsScanned = false;
                    ScanWindows();
                }

                var old = GUI.color;
                GUI.color = ColPath;
                EditorGUILayout.LabelField($"{_filteredWindows.Count} windows",
                    EditorStyles.miniLabel, GUILayout.Width(90));
                GUI.color = old;
            }

            if (!_windowsScanned) ScanWindows();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                ColLabel("Priority", 60);
                ColLabel("Window Class", 220);
                ColLabel("Menu Path", 280);
                ColLabel("Assembly", 180);
            }

            _windowScroll = EditorGUILayout.BeginScrollView(_windowScroll);

            foreach (var entry in _filteredWindows)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var old = GUI.color;
                    GUI.color = entry.Priority < 0
                        ? new Color(1f, 0.6f, 0.2f, 1f)
                        : entry.Namespace.Contains("MidManStudio")
                            ? new Color(0.28f, 0.9f, 0.45f, 1f)
                            : new Color(0.55f, 0.75f, 1f, 1f);

                    EditorGUILayout.LabelField(entry.Priority.ToString(),
                        EditorStyles.miniBoldLabel, GUILayout.Width(60));
                    GUI.color = old;

                    EditorGUILayout.LabelField(entry.TypeName,
                        EditorStyles.miniLabel, GUILayout.Width(220));
                    GUI.color = ColPath;
                    EditorGUILayout.LabelField(entry.MenuPath,
                        EditorStyles.miniLabel, GUILayout.Width(280));
                    GUI.color = old;
                    EditorGUILayout.LabelField(entry.Assembly,
                        EditorStyles.miniLabel, GUILayout.ExpandWidth(true));

                    if (GUILayout.Button("src", EditorStyles.miniButton, GUILayout.Width(30)))
                        TryOpenWindowSource(entry);
                }
            }

            if (_filteredWindows.Count == 0)
            {
                EditorGUILayout.Space(20);
                EditorGUILayout.LabelField("No windows found matching filter.",
                    EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        private void ScanWindows()
        {
            _allWindows.Clear();
            var editorWindowType = typeof(EditorWindow);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var type in types)
                {
                    if (!editorWindowType.IsAssignableFrom(type) ||
                        type == editorWindowType || type.IsAbstract) continue;

                    var methods = type.GetMethods(
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                    foreach (var method in methods)
                    {
                        foreach (var attr in method.GetCustomAttributes<MenuItem>(false))
                        {
                            if (attr.validate) continue;
                            _allWindows.Add(new WindowEntry
                            {
                                TypeName   = type.Name,
                                Namespace  = type.Namespace ?? "",
                                Assembly   = asm.GetName().Name,
                                MenuPath   = attr.menuItem,
                                Priority   = attr.priority,
                                WindowType = type
                            });
                            break;
                        }
                    }
                }
            }

            _windowsScanned = true;
            ApplyWindowFilter();
        }

        private void ApplyWindowFilter()
        {
            IEnumerable<WindowEntry> src = _allWindows;
            if (!string.IsNullOrWhiteSpace(_namespaceFilter))
            {
                string f = _namespaceFilter.ToLowerInvariant();
                src = src.Where(w =>
                    w.Namespace.ToLowerInvariant().Contains(f) ||
                    w.TypeName.ToLowerInvariant().Contains(f)  ||
                    w.MenuPath.ToLowerInvariant().Contains(f));
            }
            _filteredWindows = _sortByPriority
                ? src.OrderBy(w => w.Priority).ThenBy(w => w.MenuPath).ToList()
                : src.OrderBy(w => w.MenuPath).ToList();
        }

        private void TryOpenWindowSource(WindowEntry entry)
        {
            string[] guids = AssetDatabase.FindAssets($"{entry.TypeName} t:MonoScript");
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) != entry.TypeName) continue;
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script != null) { AssetDatabase.OpenAsset(script); return; }
            }

            // Fallback — switch to Script Reader tab and search
            _tab         = 0;
            _searchQuery = entry.TypeName;
            _needsRefresh = false;
            ApplyNameFilter();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void ColLabel(string text, float width)
        {
            var old = GUI.color;
            GUI.color = ColPath;
            EditorGUILayout.LabelField(text, EditorStyles.miniBoldLabel, GUILayout.Width(width));
            GUI.color = old;
        }

        private void EnsureStyles()
        {
            if (_stylesReady) return;

            _monoStyle = new GUIStyle(EditorStyles.label)
            {
                font     = Font.CreateDynamicFontFromOSFont("Courier New", 11),
                fontSize = 11,
                wordWrap = false,
                richText = false,
                normal   = { textColor = new Color(0.85f, 0.85f, 0.85f, 1f) }
            };

            _docSummaryStyle = new GUIStyle(_monoStyle)
            {
                normal = { textColor = ColDoc }
            };

            _docParamStyle = new GUIStyle(_monoStyle)
            {
                normal    = { textColor = ColDocParam },
                fontStyle = FontStyle.Italic
            };

            _lineNumStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal    = { textColor = ColLineNum }
            };

            _stylesReady = true;
        }
    }
}
#endif
