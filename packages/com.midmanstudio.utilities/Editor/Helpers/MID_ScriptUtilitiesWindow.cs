// Two-tab editor utility:
//   Tab 0 — Script Reader:   browse, search, and read any project C# script.
//   Tab 1 — Window Priority: reflect all EditorWindow MenuItem priorities.
// Requires Unity 2022.3+.
// Open via: MidManStudio > Utilities > Script Utilities

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MidManStudio.Core.EditorTools
{
    public class MID_ScriptUtilitiesWindow : EditorWindow
    {
        // ── Data types ─────────────────────────────────────────────────────────

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

            public bool IsAnyDoc => IsDocSummary || IsDocParam || IsDocReturn
                                 || IsDocRemark  || IsDocTag   || IsDocClose;
        }

        private class WindowEntry
        {
            public string TypeName;
            public string Namespace;
            public string Assembly;
            public string MenuPath;
            public int    Priority;
        }

        // Mixed items for the script list: folder headers + script rows
        private abstract class SRItem { }
        private sealed class  SRFolderItem : SRItem { public string Folder; }
        private sealed class  SRScriptItem : SRItem { public ScriptEntry Entry; }

        // ── State — Script Reader ───────────────────────────────────────────────

        private string            _srSearch          = "";
        private bool              _srSearchInContent;
        private bool              _srDocOnly;
        private bool              _srNeedsRefresh    = true;
        private List<ScriptEntry> _allScripts        = new();
        private List<SRItem>      _srItems           = new();     // ListView backing
        private List<DisplayLine> _allSourceLines    = new();     // full parsed lines
        private List<DisplayLine> _filteredLines     = new();     // ListView backing
        private ScriptEntry       _selectedScript;

        // ── State — Window Priority ─────────────────────────────────────────────

        private string            _wpFilter          = "MidManStudio";
        private bool              _wpSortByPriority  = true;
        private bool              _wpScanned;
        private List<WindowEntry> _allWindows        = new();
        private List<WindowEntry> _wpItems           = new();     // ListView backing

        // ── UI element refs ─────────────────────────────────────────────────────

        private int            _activeTab;
        private VisualElement  _tabContent0;
        private VisualElement  _tabContent1;
        private Button         _tabBtn0;
        private Button         _tabBtn1;

        // Script Reader
        private Label          _srStats;
        private Button         _contentSearchBtn;
        private Button         _docOnlyBtn;
        private VisualElement  _sourceEmpty;
        private VisualElement  _pathBar;
        private Label          _pathLbl;
        private ListView       _scriptList;
        private ListView       _sourceList;

        // Window Priority
        private Label          _wpStats;
        private Button         _sortPriBtn;
        private VisualElement  _wpEmpty;
        private ListView       _windowList;

        private InfoPopupHandler _popup;

        // Cached monospace font for source display
        private static Font s_MonoFont;
        private static Font MonoFont => s_MonoFont ??=
            Font.CreateDynamicFontFromOSFont(
                new[] { "Consolas", "Courier New", "Lucida Console", "monospace" }, 11);

        // ── Menu ───────────────────────────────────────────────────────────────

        [MenuItem("MidManStudio/Utilities/Script Utilities", priority = 115)]
        public static void Open()
        {
            var w = GetWindow<MID_ScriptUtilitiesWindow>("Script Utilities");
            w.minSize = new Vector2(720, 520);
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        private void OnFocus()
        {
            if (!_srNeedsRefresh && rootVisualElement.childCount > 0)
                Scan();
        }

        // ── CreateGUI ───────────────────────────────────────────────────────────

        public void CreateGUI()
        {
            var uxml = MidEditorUIHelpers.FindUxml("MID_ScriptUtilitiesWindow");
            var uss  = MidEditorUIHelpers.FindUss("MID_ScriptUtilitiesWindow");

            if (uxml == null)
            {
                rootVisualElement.Add(new Label(
                    "⚠  MID_ScriptUtilitiesWindow.uxml not found.\n" +
                    "Place UXML and USS in an Editor folder and reimport.")
                {
                    style =
                    {
                        whiteSpace = WhiteSpace.Normal,
                        color      = new StyleColor(new Color(1f, 0.8f, 0.2f)),
                        marginTop  = 20, marginLeft = 12
                    }
                });
                return;
            }

            var tree = uxml.Instantiate();
            tree.style.flexGrow = 1;
            rootVisualElement.Add(tree);

            if (uss != null) rootVisualElement.styleSheets.Add(uss);

            WireHeader(tree);
            WireTabs(tree);
            WireScriptReader(tree);
            WireWindowPriority(tree);
            WireInfoPopups(tree);

            Scan();
            if (!_wpScanned) ScanWindows();
        }

        // ── Header ─────────────────────────────────────────────────────────────

        private void WireHeader(VisualElement root)
        {
            var header = root.Q<VisualElement>("header");
            if (header == null) return;

            var grad = new GradientBannerElement
            {
                ColorTL = new Color(0.08f, 0.22f, 0.34f, 1f),
                ColorTR = new Color(0.14f, 0.10f, 0.32f, 1f),
                ColorBL = new Color(0.06f, 0.06f, 0.09f, 1f),
                ColorBR = new Color(0.06f, 0.06f, 0.08f, 1f)
            };
            grad.style.position = Position.Absolute;
            grad.style.top = grad.style.left = grad.style.right = grad.style.bottom = 0;
            header.Insert(0, grad);
        }

        // ── Tab switching ───────────────────────────────────────────────────────

        private void WireTabs(VisualElement root)
        {
            _tabContent0 = root.Q<VisualElement>("tab-content-0");
            _tabContent1 = root.Q<VisualElement>("tab-content-1");
            _tabBtn0     = root.Q<Button>("tab-btn-0");
            _tabBtn1     = root.Q<Button>("tab-btn-1");

            _tabBtn0?.RegisterCallback<ClickEvent>(_ => SwitchTab(0));
            _tabBtn1?.RegisterCallback<ClickEvent>(_ => SwitchTab(1));
        }

        private void SwitchTab(int idx)
        {
            _activeTab = idx;

            _tabContent0?.style.SetIsDisplayed(idx == 0);
            _tabContent1?.style.SetIsDisplayed(idx == 1);

            _tabBtn0?.EnableInClassList("su-tab--on", idx == 0);
            _tabBtn1?.EnableInClassList("su-tab--on", idx == 1);

            if (idx == 1 && !_wpScanned) ScanWindows();
        }

        // ── Script Reader — wiring ──────────────────────────────────────────────

        private void WireScriptReader(VisualElement root)
        {
            _srStats         = root.Q<Label>("sr-stats");
            _contentSearchBtn = root.Q<Button>("content-search-btn");
            _docOnlyBtn      = root.Q<Button>("doc-only-btn");
            _sourceEmpty     = root.Q<VisualElement>("source-empty");
            _pathBar         = root.Q<VisualElement>("path-bar");
            _pathLbl         = root.Q<Label>("path-lbl");
            _scriptList      = root.Q<ListView>("script-list");
            _sourceList      = root.Q<ListView>("source-list");

            // ── TwoPaneSplitView — configure from C# for safety ─────────────────
            var split = root.Q<TwoPaneSplitView>("sr-split");
            if (split != null)
            {
                split.fixedPaneIndex           = 0;
                split.fixedPaneInitialDimension = 240f;
                split.orientation              = TwoPaneSplitViewOrientation.Horizontal;
            }

            // ── Search field ─────────────────────────────────────────────────────
            var sf = root.Q<ToolbarSearchField>("sr-search");
            sf?.RegisterValueChangedCallback(evt =>
            {
                _srSearch = evt.newValue;
                ApplySrFilter();
            });

            // ── Content-search toggle ────────────────────────────────────────────
            _contentSearchBtn?.RegisterCallback<ClickEvent>(_ =>
            {
                _srSearchInContent = !_srSearchInContent;
                _contentSearchBtn.EnableInClassList("mid-toggle-btn--on", _srSearchInContent);
                ApplySrFilter();
            });

            // ── Doc-only toggle ──────────────────────────────────────────────────
            _docOnlyBtn?.RegisterCallback<ClickEvent>(_ =>
            {
                _srDocOnly = !_srDocOnly;
                _docOnlyBtn.EnableInClassList("mid-toggle-btn--on", _srDocOnly);
                RebuildFilteredLines();
            });

            // ── Refresh ──────────────────────────────────────────────────────────
            root.Q<Button>("sr-refresh-btn")?.RegisterCallback<ClickEvent>(_ => Scan());

            // ── Script ListView ───────────────────────────────────────────────────
            if (_scriptList != null)
            {
                _scriptList.makeItem      = MakeScriptRow;
                _scriptList.bindItem      = BindScriptRow;
                _scriptList.itemsSource   = _srItems;
                _scriptList.selectionType = SelectionType.Single;
                _scriptList.showBorder    = false;
                _scriptList.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
                _scriptList.fixedItemHeight      = 22;
                _scriptList.selectionChanged += OnScriptSelected;
            }

            // ── Source ListView ───────────────────────────────────────────────────
            if (_sourceList != null)
            {
                _sourceList.makeItem      = MakeSourceRow;
                _sourceList.bindItem      = BindSourceRow;
                _sourceList.itemsSource   = _filteredLines;
                _sourceList.selectionType = SelectionType.None;
                _sourceList.showBorder    = false;
                _sourceList.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
                _sourceList.fixedItemHeight      = 18;
            }

            // ── Path bar buttons ─────────────────────────────────────────────────
            root.Q<Button>("open-btn")?.RegisterCallback<ClickEvent>(_ =>
            {
                if (_selectedScript == null) return;
                var asset = AssetDatabase.LoadAssetAtPath<MonoScript>(_selectedScript.RelativePath);
                if (asset != null) AssetDatabase.OpenAsset(asset);
            });
            root.Q<Button>("ping-btn")?.RegisterCallback<ClickEvent>(_ =>
            {
                if (_selectedScript == null) return;
                var asset = AssetDatabase.LoadAssetAtPath<MonoScript>(_selectedScript.RelativePath);
                if (asset != null) { Selection.activeObject = asset; EditorGUIUtility.PingObject(asset); }
            });
            root.Q<Button>("copy-btn")?.RegisterCallback<ClickEvent>(_ =>
            {
                if (_selectedScript != null)
                    GUIUtility.systemCopyBuffer = _selectedScript.FullPath;
            });
        }

        // ── Script Reader — makeItem / bindItem ─────────────────────────────────

        private VisualElement MakeScriptRow()
        {
            var wrapper = new VisualElement();

            // Folder header sub-element
            var folderRow = new VisualElement { name = "folder-row" };
            folderRow.AddToClassList("su-folder-row");
            var folderLbl = new Label { name = "folder-lbl" };
            folderLbl.AddToClassList("su-folder-lbl");
            folderRow.Add(folderLbl);
            wrapper.Add(folderRow);

            // Script entry sub-element
            var scriptRow = new VisualElement { name = "script-row" };
            scriptRow.AddToClassList("su-script-row");
            scriptRow.style.flexDirection = FlexDirection.Row;
            scriptRow.style.alignItems    = Align.Center;

            var nameLbl = new Label { name = "name-lbl" };
            nameLbl.AddToClassList("su-script-name");
            scriptRow.Add(nameLbl);

            // ⊙ Locate button — fades in on hover via USS
            var locBtn = new Button { text = "⊙", name = "loc-btn", tooltip = "Locate in Project window" };
            locBtn.AddToClassList("su-script-loc");
            locBtn.RegisterCallback<ClickEvent>(_ =>
            {
                if (locBtn.userData is ScriptEntry se)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<MonoScript>(se.RelativePath);
                    if (asset != null) { Selection.activeObject = asset; EditorGUIUtility.PingObject(asset); }
                }
            });
            scriptRow.Add(locBtn);

            wrapper.Add(scriptRow);
            return wrapper;
        }

        private void BindScriptRow(VisualElement wrapper, int index)
        {
            if (index < 0 || index >= _srItems.Count) return;
            var item      = _srItems[index];
            var folderRow = wrapper.Q<VisualElement>("folder-row");
            var scriptRow = wrapper.Q<VisualElement>("script-row");

            if (item is SRFolderItem fi)
            {
                folderRow.style.display = DisplayStyle.Flex;
                scriptRow.style.display = DisplayStyle.None;
                wrapper.Q<Label>("folder-lbl").text = fi.Folder;
                return;
            }

            folderRow.style.display = DisplayStyle.None;
            scriptRow.style.display = DisplayStyle.Flex;

            if (item is not SRScriptItem si) return;
            var se = si.Entry;

            wrapper.Q<Label>("name-lbl").text = se.FileName;
            wrapper.Q<Button>("loc-btn").userData = se;

            bool selected = _selectedScript == se;
            scriptRow.EnableInClassList("su-script-row--selected", selected);
        }

        private void OnScriptSelected(IEnumerable<object> selection)
        {
            var first = selection.FirstOrDefault();
            if (first is SRScriptItem si)
            {
                LoadScript(si.Entry);
                // Refresh all rows to update selected highlight
                _scriptList?.RefreshItems();
            }
            else if (first is SRFolderItem)
            {
                // Deselect folder headers immediately
                _scriptList?.ClearSelection();
            }
        }

        // ── Source line — makeItem / bindItem ───────────────────────────────────

        private VisualElement MakeSourceRow()
        {
            var row = new VisualElement();
            row.AddToClassList("su-line-row");
            row.style.flexDirection = FlexDirection.Row;

            var lineNum = new Label { name = "line-num" };
            lineNum.AddToClassList("su-line-num");
            lineNum.style.unityFont = MonoFont;

            var code = new Label { name = "line-code" };
            code.AddToClassList("su-line-code");
            code.style.unityFont = MonoFont;

            row.Add(lineNum);
            row.Add(code);
            return row;
        }

        private void BindSourceRow(VisualElement row, int index)
        {
            if (index < 0 || index >= _filteredLines.Count) return;
            var line = _filteredLines[index];

            row.Q<Label>("line-num").text  = line.LineNumber.ToString();
            var codeLbl = row.Q<Label>("line-code");
            codeLbl.text = line.Raw;

            // Reset all doc classes
            codeLbl.RemoveFromClassList("su-line-doc-summary");
            codeLbl.RemoveFromClassList("su-line-doc-param");
            codeLbl.RemoveFromClassList("su-line-doc-return");
            codeLbl.RemoveFromClassList("su-line-doc-remark");
            codeLbl.RemoveFromClassList("su-line-doc-tag");

            if      (line.IsDocParam)   codeLbl.AddToClassList("su-line-doc-param");
            else if (line.IsDocReturn)  codeLbl.AddToClassList("su-line-doc-return");
            else if (line.IsDocRemark)  codeLbl.AddToClassList("su-line-doc-remark");
            else if (line.IsDocTag)     codeLbl.AddToClassList("su-line-doc-tag");
            else if (line.IsDocSummary) codeLbl.AddToClassList("su-line-doc-summary");
            // else: plain code — base class colour applies
        }

        // ── Window Priority — wiring ────────────────────────────────────────────

        private void WireWindowPriority(VisualElement root)
        {
            _wpStats   = root.Q<Label>("wp-stats");
            _sortPriBtn = root.Q<Button>("sort-pri-btn");
            _wpEmpty   = root.Q<VisualElement>("wp-empty");
            _windowList = root.Q<ListView>("window-list");

            var wf = root.Q<ToolbarSearchField>("wp-filter");
            wf?.SetValueWithoutNotify(_wpFilter);
            wf?.RegisterValueChangedCallback(evt =>
            {
                _wpFilter = evt.newValue;
                ApplyWpFilter();
            });

            _sortPriBtn?.RegisterCallback<ClickEvent>(_ =>
            {
                _wpSortByPriority = !_wpSortByPriority;
                _sortPriBtn.EnableInClassList("mid-toggle-btn--on", _wpSortByPriority);
                ApplyWpFilter();
            });

            root.Q<Button>("wp-scan-btn")?.RegisterCallback<ClickEvent>(_ =>
            {
                _wpScanned = false;
                ScanWindows();
            });

            if (_windowList == null) return;
            _windowList.makeItem      = MakeWindowRow;
            _windowList.bindItem      = BindWindowRow;
            _windowList.itemsSource   = _wpItems;
            _windowList.selectionType = SelectionType.None;
            _windowList.showBorder    = false;
            _windowList.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
            _windowList.fixedItemHeight      = 24;
        }

        // ── Window row — makeItem / bindItem ────────────────────────────────────

        private VisualElement MakeWindowRow()
        {
            var row = new VisualElement();
            row.AddToClassList("wp-row");
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems    = Align.Center;

            var pri = new Label { name = "pri" };
            pri.AddToClassList("wp-pri");
            row.Add(pri);

            var cls = new Label { name = "cls" };
            cls.AddToClassList("wp-class");
            row.Add(cls);

            var menu = new Label { name = "menu" };
            menu.AddToClassList("wp-menu");
            row.Add(menu);

            var asm = new Label { name = "asm" };
            asm.AddToClassList("wp-asm");
            row.Add(asm);

            var srcBtn = new Button { text = "src", name = "src-btn", tooltip = "Open source file in Script Reader" };
            srcBtn.AddToClassList("wp-src-btn");
            srcBtn.RegisterCallback<ClickEvent>(_ =>
            {
                if (srcBtn.userData is WindowEntry we) TryOpenWindowSource(we);
            });
            row.Add(srcBtn);

            return row;
        }

        private void BindWindowRow(VisualElement row, int index)
        {
            if (index < 0 || index >= _wpItems.Count) return;
            var we = _wpItems[index];

            var priLbl = row.Q<Label>("pri");
            priLbl.text = we.Priority.ToString();
            priLbl.RemoveFromClassList("wp-pri--negative");
            priLbl.RemoveFromClassList("wp-pri--mms");
            if (we.Assembly.Contains("MidManStudio")) priLbl.AddToClassList("wp-pri--mms");
            else if (we.Priority < 0)                 priLbl.AddToClassList("wp-pri--negative");

            row.Q<Label>("cls").text  = we.TypeName;
            row.Q<Label>("menu").text = we.MenuPath;

            var asmLbl = row.Q<Label>("asm");
            asmLbl.text = ShortenAsm(we.Assembly);
            asmLbl.EnableInClassList("wp-asm--mms", we.Assembly.Contains("MidManStudio"));

            row.Q<Button>("src-btn").userData = we;
        }

        // ── Info popups ─────────────────────────────────────────────────────────

        private void WireInfoPopups(VisualElement root)
        {
            _popup = new InfoPopupHandler(root);
            if (!_popup.IsAvailable) return;

            root.Q<Button>("sr-search-help")?.RegisterCallback<ClickEvent>(_ =>
                _popup.Toggle(root.Q<Button>("sr-search-help"), "Script Search",
                    "Type part of a file name or folder path to filter in real-time.\n\n" +
                    "Enable 'Search content' to also search inside file bodies.\n" +
                    "Content search scans every file — it is slower on large projects and runs once per query change.\n\n" +
                    "Click ⟳ to re-scan after adding new scripts to the project."));

            root.Q<Button>("doc-only-help")?.RegisterCallback<ClickEvent>(_ =>
                _popup.Toggle(root.Q<Button>("doc-only-help"), "Doc Only Mode",
                    "When active, the source viewer shows only XML documentation comment lines:\n\n" +
                    "  /// <summary>  (green)\n" +
                    "  /// <param     (light green, italic)\n" +
                    "  /// <returns   (light green, italic)\n" +
                    "  /// <remarks   (green)\n\n" +
                    "Useful for quickly scanning a file's public API without reading the implementation."));

            root.Q<Button>("wp-help")?.RegisterCallback<ClickEvent>(_ =>
                _popup.Toggle(root.Q<Button>("wp-help"), "Window Priority Visualizer",
                    "Scans all loaded assemblies for EditorWindow subclasses that have a [MenuItem] attribute.\n\n" +
                    "Priority values control ordering inside Unity's menu bar:\n" +
                    "  < 0     — appears above the first separator\n" +
                    "  0–99    — top group\n" +
                    "  100–199 — second group  (separated by a divider)\n" +
                    "  ≥ 200   — further groups\n\n" +
                    "🟢 Green  = MidManStudio assemblies\n" +
                    "🟠 Orange = negative priority\n" +
                    "🔵 Blue   = everything else\n\n" +
                    "Click 'src' to jump to that window's source in the Script Reader tab."));
        }

        // ── Script Reader — data ────────────────────────────────────────────────

        private void Scan()
        {
            _allScripts.Clear();
            _srNeedsRefresh = false;

            string[] guids;
            try   { guids = AssetDatabase.FindAssets("t:MonoScript"); }
            catch { guids = Array.Empty<string>(); }

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

            ApplySrFilter();
        }

        private void ApplySrFilter()
        {
            IEnumerable<ScriptEntry> src = _allScripts;

            if (!string.IsNullOrEmpty(_srSearch))
            {
                string q = _srSearch.ToLowerInvariant();
                if (_srSearchInContent)
                {
                    src = src.Where(s =>
                    {
                        if (s.FileName.ToLowerInvariant().Contains(q)  ||
                            s.RelativePath.ToLowerInvariant().Contains(q)) return true;
                        try   { return File.Exists(s.FullPath) &&
                                       File.ReadAllText(s.FullPath).ToLowerInvariant().Contains(q); }
                        catch { return false; }
                    });
                }
                else
                {
                    src = src.Where(s =>
                        s.FileName.ToLowerInvariant().Contains(q) ||
                        s.RelativePath.ToLowerInvariant().Contains(q));
                }
            }

            // Rebuild _srItems (mixed folder + script items) in-place
            _srItems.Clear();
            string lastFolder = null;
            foreach (var entry in src)
            {
                if (entry.DisplayFolder != lastFolder)
                {
                    lastFolder = entry.DisplayFolder;
                    _srItems.Add(new SRFolderItem { Folder = lastFolder });
                }
                _srItems.Add(new SRScriptItem { Entry = entry });
            }

            int scriptCount = _srItems.OfType<SRScriptItem>().Count();
            if (_srStats != null)
                _srStats.text = $"{scriptCount} / {_allScripts.Count} scripts";

            _scriptList?.RefreshItems();
        }

        private void LoadScript(ScriptEntry entry)
        {
            if (entry == null) return;
            _selectedScript = entry;

            _allSourceLines.Clear();
            _filteredLines.Clear();

            if (File.Exists(entry.FullPath))
            {
                try
                {
                    var lines = File.ReadAllLines(entry.FullPath);
                    _allSourceLines = ParseLines(lines);
                }
                catch (Exception ex)
                {
                    _allSourceLines.Add(new DisplayLine
                    {
                        LineNumber = 0,
                        Raw        = $"// Error reading file: {ex.Message}"
                    });
                }
            }

            RebuildFilteredLines();

            // Show source panel UI elements
            _sourceEmpty?.style.SetIsDisplayed(false);
            _pathBar?.style.SetIsDisplayed(true);
            _sourceList?.style.SetIsDisplayed(true);

            if (_pathLbl != null)
                _pathLbl.text = entry.RelativePath;
        }

        private void RebuildFilteredLines()
        {
            _filteredLines.Clear();
            _filteredLines.AddRange(_srDocOnly
                ? _allSourceLines.Where(l => l.IsAnyDoc)
                : _allSourceLines);
            _sourceList?.RefreshItems();
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

        // ── Window Priority — data ──────────────────────────────────────────────

        private void ScanWindows()
        {
            _allWindows.Clear();
            var editorWindowType = typeof(EditorWindow);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try   { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var type in types)
                {
                    if (!editorWindowType.IsAssignableFrom(type) ||
                        type == editorWindowType || type.IsAbstract) continue;

                    foreach (var method in type.GetMethods(
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        foreach (var attr in method.GetCustomAttributes<MenuItem>(false))
                        {
                            if (attr.validate) continue;
                            _allWindows.Add(new WindowEntry
                            {
                                TypeName  = type.Name,
                                Namespace = type.Namespace ?? "",
                                Assembly  = asm.GetName().Name ?? "",
                                MenuPath  = attr.menuItem,
                                Priority  = attr.priority
                            });
                            break;
                        }
                    }
                }
            }

            _wpScanned = true;
            ApplyWpFilter();
        }

        private void ApplyWpFilter()
        {
            IEnumerable<WindowEntry> src = _allWindows;

            if (!string.IsNullOrWhiteSpace(_wpFilter))
            {
                string f = _wpFilter.ToLowerInvariant();
                src = src.Where(w =>
                    w.Namespace.ToLowerInvariant().Contains(f) ||
                    w.TypeName.ToLowerInvariant().Contains(f)  ||
                    w.MenuPath.ToLowerInvariant().Contains(f));
            }

            _wpItems.Clear();
            _wpItems.AddRange(_wpSortByPriority
                ? src.OrderBy(w => w.Priority).ThenBy(w => w.MenuPath)
                : src.OrderBy(w => w.MenuPath));

            bool empty = _wpItems.Count == 0;
            _wpEmpty?.style.SetIsDisplayed(empty);
            _windowList?.style.SetIsDisplayed(!empty);

            if (_wpStats != null)
                _wpStats.text = $"{_wpItems.Count} window{(_wpItems.Count == 1 ? "" : "s")} matching filter";

            _windowList?.RefreshItems();
        }

        private void TryOpenWindowSource(WindowEntry entry)
        {
            // Find the script file, load it in the Script Reader tab, and switch to it
            string[] guids = AssetDatabase.FindAssets($"{entry.TypeName} t:MonoScript");
            ScriptEntry found = null;

            foreach (var g in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                if (Path.GetFileNameWithoutExtension(path) != entry.TypeName) continue;

                string full = Path.GetFullPath(path);
                string dir  = (Path.GetDirectoryName(path) ?? "").Replace("\\", "/")
                    .Replace("Assets/", "").Replace("packages/", "");
                found = new ScriptEntry
                {
                    FullPath     = full,
                    RelativePath = path,
                    FileName     = entry.TypeName,
                    DisplayFolder = dir
                };
                break;
            }

            if (found == null) return;

            // Switch to Script Reader tab, set search to narrow to this file
            SwitchTab(0);
            var sf = rootVisualElement.Q<ToolbarSearchField>("sr-search");
            sf?.SetValueWithoutNotify(entry.TypeName);
            _srSearch = entry.TypeName;
            ApplySrFilter();
            LoadScript(found);
            _scriptList?.RefreshItems();
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static string ShortenAsm(string asm)
        {
            if (string.IsNullOrEmpty(asm)) return "—";
            return asm
                .Replace("MidManStudio.", "MMS.")
                .Replace("Assembly-CSharp", "Game")
                .Replace(".Utilities", ".Utils")
                .Replace("-Editor", ".Ed");
        }
    }

    // ── StyleHelper extension ──────────────────────────────────────────────────
    // Avoids repetitive ternary expressions on display style.

    internal static class StyleExtensions
    {
        public static void SetIsDisplayed(this IStyle style, bool visible) =>
            style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }
}
#endif
