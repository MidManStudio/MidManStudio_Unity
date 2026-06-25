// Enhanced execution order manager: real-time search, UXML/USS theming,
// gradient header, order pip visualiser, ⊙ locate, ? info popups, Group NS.
// Requires Unity 2022.3+ (UI Toolkit stable, Painter2D, DynamicHeight ListView).
// Open via: MidManStudio > Utilities > Script Execution Order

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MidManStudio.Core.EditorTools
{
    public class MID_ScriptExecutionOrderWindow : EditorWindow
    {
        // ── Data types ─────────────────────────────────────────────────────────

        private sealed class ScriptEntry
        {
            public MonoScript Script;
            public string     TypeName;
            public string     Namespace;
            public string     Assembly;
            public int        SavedOrder;
            public int        EditOrder;
            public bool       IsDirty => EditOrder != SavedOrder;
            public bool       IsMms   => Assembly.Contains("MidManStudio");
        }

        // Mixed list items: namespace group headers or script entry rows
        private abstract class ListItem { }
        private sealed class HeaderItem : ListItem { public string Ns; }
        private sealed class EntryItem  : ListItem { public ScriptEntry Entry; }

        // ── State ──────────────────────────────────────────────────────────────

        private readonly List<ScriptEntry> _managed       = new();
        private readonly List<ScriptEntry> _unmanaged      = new();
        private readonly List<ListItem>    _viewItems      = new();  // ListView backing (modified in-place)
        private readonly List<ScriptEntry> _browserItems   = new();  // browser backing (modified in-place)

        private bool   _hasChanges;
        private string _search    = "";
        private string _nsFilter  = "";
        private bool   _groupByNs    = false;
        private bool   _browserOpen  = false;

        // ── UI element refs ─────────────────────────────────────────────────────

        private VisualElement    _dirtyBanner;
        private VisualElement    _filterBar;
        private Label            _filterLbl;
        private VisualElement    _barSection;
        private OrderBarElement  _orderBar;
        private VisualElement    _managedEmpty;
        private ListView         _managedList;
        private VisualElement    _browserContainer;
        private VisualElement    _browserEmpty;
        private ListView         _browserList;
        private Label            _statsLbl;
        private Button           _applyBtn;
        private Button           _discardBtn;
        private Label            _savedLbl;
        private IntegerField     _stepField;
        private Button           _groupNsBtn;
        private Label            _browserBtnLbl;
        private InfoPopupHandler _popup;

        // ── Menu ───────────────────────────────────────────────────────────────

        [MenuItem("MidManStudio/Utilities/Script Execution Order", priority = 116)]
        public static void Open()
        {
            var w = GetWindow<MID_ScriptExecutionOrderWindow>("Script Exec Order");
            w.minSize = new Vector2(720, 560);
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        private void OnFocus()
        {
            // Re-scan when the user switches back to this window (unless they have unsaved edits)
            if (!_hasChanges && rootVisualElement.childCount > 0) Scan();
        }

        // ── UI Toolkit entry point ──────────────────────────────────────────────

        public void CreateGUI()
        {
            var uss  = MidEditorUIHelpers.FindUss("MID_ScriptExecutionOrderWindow");
            var uxml = MidEditorUIHelpers.FindUxml("MID_ScriptExecutionOrderWindow");

            if (uxml == null)
            {
                rootVisualElement.Add(new Label(
                    "⚠  MID_ScriptExecutionOrderWindow.uxml not found.\n" +
                    "Place the UXML and USS files in an Editor folder and reimport.")
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
            WireToolbar(tree);
            WireOrderBar(tree);
            WireManagedList(tree);
            WireBrowser(tree);
            WireFooter(tree);
            WireInfoPopups(tree);

            Scan();
        }

        // ── Section wiring ──────────────────────────────────────────────────────

        private void WireHeader(VisualElement root)
        {
            // Inject gradient banner as absolute child of the header element
            var header = root.Q<VisualElement>("header");
            if (header != null)
            {
                var grad = new GradientBannerElement
                {
                    ColorTL = new Color(0.10f, 0.38f, 0.36f, 1f),
                    ColorTR = new Color(0.08f, 0.20f, 0.30f, 1f),
                    ColorBL = new Color(0.07f, 0.07f, 0.09f, 1f),
                    ColorBR = new Color(0.06f, 0.06f, 0.08f, 1f)
                };
                grad.style.position = Position.Absolute;
                grad.style.top = grad.style.left = grad.style.right = grad.style.bottom = 0;
                header.Insert(0, grad);
            }

            root.Q<Button>("refresh-btn")?.RegisterCallback<ClickEvent>(_ => Scan());
        }

        private void WireToolbar(VisualElement root)
        {
            // Search field
            var sf = root.Q<ToolbarSearchField>("search-field");
            sf?.RegisterValueChangedCallback(evt =>
            {
                _search = evt.newValue;
                ApplyFilters();
            });

            // Namespace dropdown
            var nsDrop = root.Q<DropdownField>("ns-filter");
            nsDrop?.RegisterValueChangedCallback(evt =>
            {
                _nsFilter = evt.newValue == "All Namespaces" ? "" : evt.newValue;
                ApplyFilters();
            });

            // Group-by-NS toggle button
            _groupNsBtn = root.Q<Button>("group-ns-btn");
            _groupNsBtn?.RegisterCallback<ClickEvent>(_ =>
            {
                _groupByNs = !_groupByNs;
                _groupNsBtn.EnableInClassList("mid-toggle-btn--on", _groupByNs);
                if (_managedList != null) _managedList.reorderable = !_groupByNs;
                ApplyFilters();
            });

            // Filter bar + clear
            _filterBar = root.Q<VisualElement>("filter-bar");
            _filterLbl = root.Q<Label>("filter-lbl");
            root.Q<Button>("clear-filter-btn")?.RegisterCallback<ClickEvent>(_ =>
            {
                _search = _nsFilter = "";
                sf?.SetValueWithoutNotify("");
                nsDrop?.SetValueWithoutNotify("All Namespaces");
                ApplyFilters();
            });
        }

        private void WireOrderBar(VisualElement root)
        {
            _barSection = root.Q<VisualElement>("bar-section");

            var slot = root.Q<VisualElement>("order-bar");
            if (slot == null) return;

            _orderBar = new OrderBarElement();
            _orderBar.style.flexGrow = 1;
            _orderBar.style.minHeight = 16;
            slot.Add(_orderBar);
        }

        private void WireManagedList(VisualElement root)
        {
            _dirtyBanner  = root.Q<VisualElement>("dirty-banner");
            _managedEmpty = root.Q<VisualElement>("managed-empty");
            _managedList  = root.Q<ListView>("managed-list");

            if (_managedList == null) return;

            _managedList.makeItem          = MakeManagedRow;
            _managedList.bindItem          = BindManagedRow;
            _managedList.itemsSource       = _viewItems;
            _managedList.selectionType     = SelectionType.None;
            _managedList.reorderable       = true;
            _managedList.showBorder        = false;
            _managedList.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;
            _managedList.itemIndexChanged  += OnItemReordered;
        }

        private void WireBrowser(VisualElement root)
        {
            _browserContainer = root.Q<VisualElement>("browser-container");
            _browserBtnLbl    = root.Q<Label>("browser-btn-lbl");
            _browserEmpty     = root.Q<VisualElement>("browser-empty");
            _browserList      = root.Q<ListView>("browser-list");

            root.Q<Button>("browser-btn")?.RegisterCallback<ClickEvent>(_ =>
            {
                _browserOpen = !_browserOpen;
                _browserContainer?.EnableInClassList("mid-browser-container--on", _browserOpen);
                if (_browserBtnLbl != null)
                    _browserBtnLbl.text = _browserOpen
                        ? "▲  Hide Script Browser"
                        : "▼  Browse & Add Scripts";
            });

            if (_browserList == null) return;

            _browserList.makeItem      = MakeBrowserRow;
            _browserList.bindItem      = BindBrowserRow;
            _browserList.itemsSource   = _browserItems;
            _browserList.selectionType = SelectionType.None;
            _browserList.showBorder    = false;
            _browserList.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;
        }

        private void WireFooter(VisualElement root)
        {
            _statsLbl   = root.Q<Label>("stats-lbl");
            _applyBtn   = root.Q<Button>("apply-btn");
            _discardBtn = root.Q<Button>("discard-btn");
            _savedLbl   = root.Q<Label>("saved-lbl");
            _stepField  = root.Q<IntegerField>("step-field");

            _applyBtn?.RegisterCallback<ClickEvent>(_   => ApplyChanges());
            _discardBtn?.RegisterCallback<ClickEvent>(_ => Scan());
            root.Q<Button>("auto-btn")?.RegisterCallback<ClickEvent>(_ => AutoNumber());
        }

        private void WireInfoPopups(VisualElement root)
        {
            _popup = new InfoPopupHandler(root);
            if (!_popup.IsAvailable) return;

            root.Q<Button>("search-help")?.RegisterCallback<ClickEvent>(_ =>
                _popup.Toggle(root.Q<Button>("search-help"), "Search & Namespace Filter",
                    "Type part of a script name, namespace, or assembly to filter in real-time.\n\n" +
                    "Use the Namespace dropdown to narrow the list to a single namespace.\n\n" +
                    "Both filters combine: you can search within a namespace.\n\n" +
                    "Click '✕ Clear all filters' to reset both at once."));

            root.Q<Button>("bar-help")?.RegisterCallback<ClickEvent>(_ =>
                _popup.Toggle(root.Q<Button>("bar-help"), "Execution Order Visualiser",
                    "Each pip (●) represents one managed script.\n\n" +
                    "🔵  Blue   — negative order (runs before Default)\n" +
                    "🟠  Orange — positive order (runs after Default)\n" +
                    "⬜  Grey   — order = 0 (same as Default)\n" +
                    "🟡  Yellow — unsaved change pending\n\n" +
                    "The vertical grey line marks order = 0 (Default).\n\n" +
                    "Hover a pip to see the script name and order value."));
        }

        // ── Managed row — makeItem ──────────────────────────────────────────────

        private VisualElement MakeManagedRow()
        {
            // One wrapper that can render either a namespace header OR a script entry.
            // The correct sub-element is shown/hidden in BindManagedRow.
            var wrapper = new VisualElement();

            // ── Namespace header sub-element ────────────────────────────────────
            var hdr = new VisualElement { name = "ns-hdr" };
            hdr.AddToClassList("mid-ns-grp");
            var hdrLbl = new Label { name = "ns-hdr-lbl" };
            hdrLbl.AddToClassList("mid-ns-grp__lbl");
            hdr.Add(hdrLbl);
            wrapper.Add(hdr);

            // ── Script entry sub-element ────────────────────────────────────────
            var row = new VisualElement { name = "entry-row" };
            row.AddToClassList("mid-row");
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems    = Align.Center;

            // Drag handle (cosmetic — ListView drives actual drag)
            var handleLbl = new Label { text = "⠿" };
            handleLbl.AddToClassList("mid-row__handle");
            row.Add(handleLbl);

            // Order integer field — callback reads userData for current entry
            var orderFld = new IntegerField { name = "order-fld" };
            orderFld.AddToClassList("mid-row__order");
            orderFld.RegisterValueChangedCallback(evt =>
            {
                if (orderFld.userData is not ScriptEntry se) return;
                se.EditOrder = evt.newValue;
                UpdateOrderFieldStyle(orderFld, se);
                row.EnableInClassList("mid-row--dirty", se.IsDirty);
                RefreshOrderBar();
                UpdateDirtyState();
            });
            row.Add(orderFld);

            // Labels
            var nameLbl = new Label { name = "name-lbl" };
            nameLbl.AddToClassList("mid-row__name");
            row.Add(nameLbl);

            var nsLbl = new Label { name = "ns-lbl" };
            nsLbl.AddToClassList("mid-row__ns");
            row.Add(nsLbl);

            var asmLbl = new Label { name = "asm-lbl" };
            asmLbl.AddToClassList("mid-row__asm");
            row.Add(asmLbl);

            // Action buttons — callbacks read button.userData set in BindManagedRow
            var actions = new VisualElement { name = "actions" };
            actions.AddToClassList("mid-row__actions");
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.alignItems    = Align.Center;

            var upBtn  = Btn("up-btn",  "↑", "mid-btn--up",  "Move up (swap order with previous entry)");
            var dnBtn  = Btn("dn-btn",  "↓", "mid-btn--dn",  "Move down (swap order with next entry)");
            var locBtn = Btn("loc-btn", "⊙", "mid-btn--loc", "Locate this script in the Project window");
            var rmBtn  = Btn("rm-btn",  "×", "mid-btn--rm",  "Remove from managed list (resets order to 0)");

            upBtn.RegisterCallback<ClickEvent>(_ =>
            {
                if (upBtn.userData is ScriptEntry se) { MoveManaged(se, -1); RefreshAll(); }
            });
            dnBtn.RegisterCallback<ClickEvent>(_ =>
            {
                if (dnBtn.userData is ScriptEntry se) { MoveManaged(se, +1); RefreshAll(); }
            });
            locBtn.RegisterCallback<ClickEvent>(_ =>
            {
                if (locBtn.userData is ScriptEntry se) LocateScript(se);
            });
            rmBtn.RegisterCallback<ClickEvent>(_ =>
            {
                if (rmBtn.userData is ScriptEntry se) { RemoveFromManaged(se); RefreshAll(); }
            });

            actions.Add(upBtn); actions.Add(dnBtn);
            actions.Add(locBtn); actions.Add(rmBtn);
            row.Add(actions);

            wrapper.Add(row);
            return wrapper;
        }

        // ── Managed row — bindItem ──────────────────────────────────────────────

        private void BindManagedRow(VisualElement wrapper, int index)
        {
            if (index < 0 || index >= _viewItems.Count) return;

            var hdr  = wrapper.Q<VisualElement>("ns-hdr");
            var row  = wrapper.Q<VisualElement>("entry-row");
            var item = _viewItems[index];

            // ── Header row ──────────────────────────────────────────────────────
            if (item is HeaderItem hi)
            {
                hdr.style.display = DisplayStyle.Flex;
                row.style.display = DisplayStyle.None;
                wrapper.Q<Label>("ns-hdr-lbl").text =
                    string.IsNullOrEmpty(hi.Ns) ? "(No Namespace)" : hi.Ns;
                return;
            }

            // ── Entry row ───────────────────────────────────────────────────────
            hdr.style.display = DisplayStyle.None;
            row.style.display = DisplayStyle.Flex;

            if (item is not EntryItem ei) return;
            var se = ei.Entry;

            // Figure out this entry's position among all visible entries (for ↑↓ enable)
            var entries  = _viewItems.OfType<EntryItem>().ToList();
            int entryPos = entries.FindIndex(e => e.Entry == se);

            // Order field
            var orderFld = row.Q<IntegerField>("order-fld");
            orderFld.userData = se;
            orderFld.SetValueWithoutNotify(se.EditOrder);
            UpdateOrderFieldStyle(orderFld, se);

            row.EnableInClassList("mid-row--dirty", se.IsDirty);

            row.Q<Label>("name-lbl").text = se.TypeName;

            var nsLbl = row.Q<Label>("ns-lbl");
            nsLbl.text = string.IsNullOrEmpty(se.Namespace) ? "—" : se.Namespace;

            var asmLbl = row.Q<Label>("asm-lbl");
            asmLbl.text = ShortenAsm(se.Assembly);
            asmLbl.EnableInClassList("mid-row__asm--mms", se.IsMms);

            // Wire buttons to the current entry via userData
            var actions = row.Q<VisualElement>("actions");
            var upBtn   = actions.Q<Button>("up-btn");
            var dnBtn   = actions.Q<Button>("dn-btn");
            var locBtn  = actions.Q<Button>("loc-btn");
            var rmBtn   = actions.Q<Button>("rm-btn");

            upBtn.userData  = se;
            dnBtn.userData  = se;
            locBtn.userData = se;
            rmBtn.userData  = se;

            upBtn.SetEnabled(!_groupByNs && entryPos > 0);
            dnBtn.SetEnabled(!_groupByNs && entryPos < entries.Count - 1);
        }

        // ── Browser row — makeItem ──────────────────────────────────────────────

        private VisualElement MakeBrowserRow()
        {
            var row = new VisualElement();
            row.AddToClassList("mid-browser-row");
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems    = Align.Center;

            var nameLbl = new Label { name = "name-lbl" };
            nameLbl.AddToClassList("mid-row__name");
            row.Add(nameLbl);

            var nsLbl = new Label { name = "ns-lbl" };
            nsLbl.AddToClassList("mid-row__ns");
            row.Add(nsLbl);

            var asmLbl = new Label { name = "asm-lbl" };
            asmLbl.AddToClassList("mid-row__asm");
            asmLbl.style.flexGrow = 1;
            row.Add(asmLbl);

            var locBtn = Btn("loc-btn", "⊙", "mid-btn--loc", "Locate this script in the Project window");
            locBtn.RegisterCallback<ClickEvent>(_ =>
            {
                if (locBtn.userData is ScriptEntry se) LocateScript(se);
            });
            row.Add(locBtn);

            var addBtn = Btn("add-btn", "+", "mid-btn--add", "Add to managed execution order list");
            addBtn.RegisterCallback<ClickEvent>(_ =>
            {
                if (addBtn.userData is ScriptEntry se) { AddToManaged(se); RefreshAll(); }
            });
            row.Add(addBtn);

            return row;
        }

        // ── Browser row — bindItem ──────────────────────────────────────────────

        private void BindBrowserRow(VisualElement row, int index)
        {
            if (index < 0 || index >= _browserItems.Count) return;
            var se = _browserItems[index];

            row.Q<Label>("name-lbl").text = se.TypeName;
            var nsLbl  = row.Q<Label>("ns-lbl");
            nsLbl.text = string.IsNullOrEmpty(se.Namespace) ? "—" : se.Namespace;
            var asmLbl = row.Q<Label>("asm-lbl");
            asmLbl.text = ShortenAsm(se.Assembly);
            asmLbl.EnableInClassList("mid-row__asm--mms", se.IsMms);

            row.Q<Button>("loc-btn").userData = se;
            row.Q<Button>("add-btn").userData = se;
        }

        // ── Data — Scan ─────────────────────────────────────────────────────────

        private void Scan()
        {
            _managed.Clear();
            _unmanaged.Clear();
            _hasChanges = false;

            string[] guids;
            try   { guids = AssetDatabase.FindAssets("t:MonoScript"); }
            catch { guids = Array.Empty<string>(); }

            foreach (var guid in guids)
            {
                var path   = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null) continue;

                var type = script.GetClass();
                if (type == null || type.IsAbstract) continue;
                if (!typeof(MonoBehaviour).IsAssignableFrom(type)) continue;

                int    ord = MonoImporter.GetExecutionOrder(script);
                string ns  = type.Namespace ?? "";
                string asm = type.Assembly?.GetName().Name ?? "";

                var entry = new ScriptEntry
                {
                    Script     = script,
                    TypeName   = type.Name,
                    Namespace  = ns,
                    Assembly   = asm,
                    SavedOrder = ord,
                    EditOrder  = ord
                };

                (ord != 0 ? _managed : _unmanaged).Add(entry);
            }

            SortManaged();
            _unmanaged.Sort((a, b) =>
            {
                int c = string.Compare(a.Namespace, b.Namespace, StringComparison.Ordinal);
                return c != 0 ? c : string.Compare(a.TypeName, b.TypeName, StringComparison.Ordinal);
            });

            RebuildNsDropdown();
            ApplyFilters();
        }

        private void SortManaged() =>
            _managed.Sort((a, b) =>
            {
                int c = a.EditOrder.CompareTo(b.EditOrder);
                return c != 0 ? c : string.Compare(a.TypeName, b.TypeName, StringComparison.Ordinal);
            });

        private void RebuildNsDropdown()
        {
            var nsDrop = rootVisualElement.Q<DropdownField>("ns-filter");
            if (nsDrop == null) return;

            var nsSet = new HashSet<string>();
            foreach (var e in _managed)   nsSet.Add(e.Namespace);
            foreach (var e in _unmanaged) nsSet.Add(e.Namespace);

            var choices = new List<string> { "All Namespaces" };
            choices.AddRange(nsSet.OrderBy(n => n));

            string prev = nsDrop.value;
            nsDrop.choices = choices;
            nsDrop.SetValueWithoutNotify(choices.Contains(prev) ? prev : "All Namespaces");
        }

        // ── Data — Filter & view rebuild ────────────────────────────────────────

        private void ApplyFilters()
        {
            string q     = _search.ToLowerInvariant();
            bool   hasQ  = !string.IsNullOrEmpty(q);
            bool   hasNs = !string.IsNullOrEmpty(_nsFilter);

            bool Matches(ScriptEntry e) =>
                (!hasNs || e.Namespace == _nsFilter) &&
                (!hasQ  || e.TypeName.ToLowerInvariant().Contains(q)  ||
                           e.Namespace.ToLowerInvariant().Contains(q) ||
                           e.Assembly.ToLowerInvariant().Contains(q));

            // ── Rebuild _viewItems in-place (so ListView backing ref stays valid) ─
            _viewItems.Clear();
            if (_groupByNs)
            {
                foreach (var grp in _managed.Where(Matches)
                    .GroupBy(e => e.Namespace)
                    .OrderBy(g => g.Key))
                {
                    _viewItems.Add(new HeaderItem { Ns = grp.Key });
                    foreach (var e in grp.OrderBy(e => e.EditOrder).ThenBy(e => e.TypeName))
                        _viewItems.Add(new EntryItem { Entry = e });
                }
            }
            else
            {
                foreach (var e in _managed.Where(Matches))
                    _viewItems.Add(new EntryItem { Entry = e });
            }

            // ── Rebuild _browserItems in-place ──────────────────────────────────
            _browserItems.Clear();
            _browserItems.AddRange(_unmanaged.Where(Matches));

            // ── Filter bar visibility ───────────────────────────────────────────
            bool hasFilter = hasQ || hasNs;
            _filterBar?.EnableInClassList("mid-filter-bar--on", hasFilter);
            if (_filterLbl != null && hasFilter)
            {
                var parts = new List<string>();
                if (hasQ)  parts.Add($"name \"{_search}\"");
                if (hasNs) parts.Add($"ns \"{_nsFilter}\"");
                _filterLbl.text = "Filtering by: " + string.Join("  ·  ", parts);
            }

            RefreshAll();
        }

        // ── UI refresh ──────────────────────────────────────────────────────────

        private void RefreshAll()
        {
            // Managed list
            bool emptyManaged = _viewItems.Count == 0;
            if (_managedEmpty != null)
                _managedEmpty.style.display = emptyManaged ? DisplayStyle.Flex : DisplayStyle.None;
            if (_managedList != null)
            {
                _managedList.style.display = emptyManaged ? DisplayStyle.None : DisplayStyle.Flex;
                _managedList.reorderable   = !_groupByNs;
                _managedList.RefreshItems();
            }

            // Browser list
            bool emptyBrowser = _browserItems.Count == 0;
            if (_browserEmpty != null)
                _browserEmpty.style.display = emptyBrowser ? DisplayStyle.Flex : DisplayStyle.None;
            if (_browserList != null)
            {
                _browserList.style.display = emptyBrowser ? DisplayStyle.None : DisplayStyle.Flex;
                _browserList.RefreshItems();
            }

            RefreshOrderBar();
            UpdateDirtyState();
            if (_statsLbl != null)
                _statsLbl.text = $"Managed: {_managed.Count}   ·   Unmanaged: {_unmanaged.Count}";
        }

        private void RefreshOrderBar()
        {
            if (_orderBar == null) return;
            bool has = _managed.Count > 0;
            if (_barSection != null)
                _barSection.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            if (has)
                _orderBar.UpdateData(_managed.Select(e => (e.TypeName, e.EditOrder, e.IsDirty, e.IsMms)));
        }

        private void UpdateDirtyState()
        {
            _hasChanges = _managed.Any(e => e.IsDirty);
            _dirtyBanner?.EnableInClassList("mid-dirty-banner--on",       _hasChanges);
            _applyBtn?.EnableInClassList("mid-footer__apply--on",         _hasChanges);
            _discardBtn?.EnableInClassList("mid-footer__discard--on",     _hasChanges);
            if (_savedLbl != null)
                _savedLbl.style.display = _hasChanges ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // ── Data — Order management ─────────────────────────────────────────────

        private void AddToManaged(ScriptEntry e)
        {
            int step = _stepField?.value ?? 100;
            e.EditOrder = _managed.Count > 0 ? _managed.Max(x => x.EditOrder) + step : step;
            _unmanaged.Remove(e);
            _managed.Add(e);
            SortManaged();
            ApplyFilters();
        }

        private void RemoveFromManaged(ScriptEntry e)
        {
            e.EditOrder = 0;
            _managed.Remove(e);
            _unmanaged.Add(e);
            _unmanaged.Sort((a, b) =>
            {
                int c = string.Compare(a.Namespace, b.Namespace, StringComparison.Ordinal);
                return c != 0 ? c : string.Compare(a.TypeName, b.TypeName, StringComparison.Ordinal);
            });
            ApplyFilters();
        }

        private void MoveManaged(ScriptEntry se, int direction)
        {
            // Operates on flat entry list extracted from _viewItems
            var entries = _viewItems.OfType<EntryItem>().Select(i => i.Entry).ToList();
            int idx = entries.FindIndex(e => e == se);
            int dst = idx + direction;
            if (dst < 0 || dst >= entries.Count) return;

            var other = entries[dst];
            (se.EditOrder, other.EditOrder) = (other.EditOrder, se.EditOrder);
            // Guarantee distinct values if they were identical
            if (se.EditOrder == other.EditOrder) other.EditOrder += direction;

            SortManaged();
            ApplyFilters();
        }

        private void OnItemReordered(int oldIdx, int newIdx)
        {
            // The ListView has already reordered _viewItems in-place.
            // Re-assign the original sorted order values to entries
            // in their new visual positions to make the drag permanent.
            var entries = _viewItems.OfType<EntryItem>().Select(i => i.Entry).ToList();
            if (entries.Count == 0) return;

            var sortedOrds = entries.Select(e => e.EditOrder).OrderBy(x => x).ToList();
            for (int i = 0; i < entries.Count; i++)
                entries[i].EditOrder = sortedOrds[i];

            UpdateDirtyState();
            RefreshOrderBar();
            _managedList?.RefreshItems();
        }

        private void AutoNumber()
        {
            if (_managed.Count == 0) return;
            int step = _stepField?.value ?? 100;
            int mid  = _managed.Count / 2;
            for (int i = 0; i < _managed.Count; i++)
                _managed[i].EditOrder = (i - mid) * step;
            SortManaged();
            ApplyFilters();
        }

        private void ApplyChanges()
        {
            foreach (var e in _managed)
            {
                MonoImporter.SetExecutionOrder(e.Script, e.EditOrder);
                e.SavedOrder = e.EditOrder;
            }
            // Zero-out scripts that were removed from the managed list this session
            foreach (var e in _unmanaged.Where(e => e.SavedOrder != 0))
            {
                MonoImporter.SetExecutionOrder(e.Script, 0);
                e.SavedOrder = 0;
            }
            AssetDatabase.SaveAssets();
            _hasChanges = false;
            UpdateDirtyState();
            _managedList?.RefreshItems();
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static Button Btn(string name, string text, string extraClass, string tip)
        {
            var b = new Button { name = name, text = text, tooltip = tip };
            b.AddToClassList("mid-btn");
            b.AddToClassList(extraClass);
            return b;
        }

        private static void UpdateOrderFieldStyle(IntegerField f, ScriptEntry e)
        {
            f.EnableInClassList("mid-row__order--neg",   !e.IsDirty && e.EditOrder < 0);
            f.EnableInClassList("mid-row__order--pos",   !e.IsDirty && e.EditOrder > 0);
            f.EnableInClassList("mid-row__order--dirty", e.IsDirty);
        }

        private static void LocateScript(ScriptEntry e)
        {
            if (e?.Script == null) return;
            Selection.activeObject = e.Script;
            EditorGUIUtility.PingObject(e.Script);
        }

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
}
#endif
