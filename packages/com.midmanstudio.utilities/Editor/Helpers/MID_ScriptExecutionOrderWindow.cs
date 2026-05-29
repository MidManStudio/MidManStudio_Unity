// MID_ScriptExecutionOrderWindow.cs
// Enhanced Unity Script Execution Order manager.
// Replaces Edit > Project Settings > Script Execution Order with:
//   • Search by name / namespace / assembly
//   • Namespace filter dropdown + group-by-namespace toggle
//   • Visual order-indicator bar with colored pips and hover tooltips
//   • Drag-and-drop reordering via ≡ handle (disabled when filter/group active)
//   • ↑ / ↓ buttons as reliable fallback
//   • Direct order-number editing with dirty-state highlighting
//   • Auto-number at configurable step intervals
//   • Script browser to add any MonoBehaviour to the managed list
//   • Apply / Discard workflow — nothing written to PlayerSettings until Apply
// Open via: MidManStudio > Utilities > Script Execution Order

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Core.EditorTools
{
    public class MID_ScriptExecutionOrderWindow : EditorWindow
    {
        // ── Inner type ────────────────────────────────────────────────────────

        private sealed class ScriptEntry
        {
            public MonoScript Script;
            public string     TypeName;
            public string     Namespace;
            public string     Assembly;
            public int        SavedOrder;          // current value in PlayerSettings
            public int        EditOrder;           // pending value (may differ)
            public bool       IsDirty => EditOrder != SavedOrder;
        }

        // ── Constants ─────────────────────────────────────────────────────────

        private const float ROW_H = 22f;   // pixel height per managed-list row

        // ── State ─────────────────────────────────────────────────────────────

        private List<ScriptEntry> _managed   = new();   // scripts with explicit order
        private List<ScriptEntry> _unmanaged = new();   // scripts at default order (0)
        private bool              _hasChanges;

        // Toolbar
        private string   _search     = "";
        private string[] _nsOptions  = { "All Namespaces" };
        private int      _nsIdx      = 0;
        private bool     _groupByNs  = false;
        private bool     _browserVis = false;
        private int      _autoStep   = 100;

        // Drag (disabled when search / filter / group is active)
        private bool _drag;
        private int  _dragSrc = -1;
        private int  _dragDst = -1;

        // Layout helpers
        private Vector2 _managedScroll;
        private Vector2 _browserScroll;
        private Rect    _managedListRect;   // captured after EndScrollView during Repaint

        // Styles
        private bool     _stylesBuilt;
        private GUIStyle _handleStyle;

        // ── Palette ───────────────────────────────────────────────────────────

        private static readonly Color C_Before   = new Color(0.40f, 0.76f, 1.00f, 1f);   // negative order
        private static readonly Color C_Default  = new Color(0.55f, 0.55f, 0.55f, 1f);   // zero
        private static readonly Color C_After    = new Color(1.00f, 0.60f, 0.25f, 1f);   // positive order
        private static readonly Color C_Dirty    = new Color(1.00f, 0.90f, 0.25f, 1f);   // unsaved change
        private static readonly Color C_MMS      = new Color(0.28f, 0.90f, 0.45f, 1f);   // MidManStudio
        private static readonly Color C_BarBg    = new Color(0.08f, 0.08f, 0.08f, 0.85f);
        private static readonly Color C_Drop     = new Color(0.35f, 0.90f, 0.35f, 1f);
        private static readonly Color C_Handle   = new Color(0.42f, 0.42f, 0.42f, 1f);
        private static readonly Color C_RowNorm  = new Color(0.13f, 0.13f, 0.13f, 0.40f);
        private static readonly Color C_RowDirty = new Color(0.28f, 0.23f, 0.04f, 0.70f);

        // ── Menu & lifecycle ──────────────────────────────────────────────────

        [MenuItem("MidManStudio/Utilities/Script Execution Order", priority = 116)]
        public static void Open()
        {
            var w = GetWindow<MID_ScriptExecutionOrderWindow>("Script Exec Order");
            w.minSize = new Vector2(660, 500);
        }

        private void OnEnable() => Scan();

        private void OnFocus()
        {
            // Re-sync if the user changed things in Project Settings directly
            if (!_hasChanges) Scan();
        }

        // ── Main GUI ──────────────────────────────────────────────────────────

        private void OnGUI()
        {
            BuildStyles();
            DrawToolbar();
            Sep();
            DrawOrderBar();
            DrawManagedList();
            Sep();

            if (GUILayout.Button(
                _browserVis ? "▲  Hide Script Browser" : "▼  Browse & Add Scripts",
                GUILayout.Height(22)))
                _browserVis = !_browserVis;

            if (_browserVis)
            {
                Sep();
                DrawBrowser();
            }

            Sep();
            DrawFooter();

            // Drag overlay drawn last so it's on top of everything
            DrawDragOverlay();

            if (_drag) Repaint();
        }

        // ── Toolbar ───────────────────────────────────────────────────────────

        private void DrawToolbar()
        {
            EditorGUILayout.Space(5);

            using (new EditorGUILayout.HorizontalScope())
            {
                var oc = GUI.color; GUI.color = C_MMS;
                EditorGUILayout.LabelField("Script Execution Order",
                    EditorStyles.boldLabel, GUILayout.Width(215));
                GUI.color = oc;

                GUILayout.FlexibleSpace();

                // Search
                _search = EditorGUILayout.TextField(_search,
                    EditorStyles.toolbarSearchField, GUILayout.Width(175));
                if (GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(20)))
                    _search = "";

                GUILayout.Space(5);

                // Namespace filter
                _nsIdx = EditorGUILayout.Popup(_nsIdx, _nsOptions,
                    EditorStyles.toolbarPopup, GUILayout.Width(148));

                GUILayout.Space(4);

                _groupByNs = GUILayout.Toggle(_groupByNs, "Group NS",
                    EditorStyles.toolbarButton, GUILayout.Width(68));

                GUILayout.Space(4);

                if (GUILayout.Button("⟳", EditorStyles.toolbarButton, GUILayout.Width(24)))
                    Scan();
            }

            if (_hasChanges)
            {
                var ob = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.75f, 0.60f, 0.10f, 1f);
                EditorGUILayout.HelpBox(
                    "Unsaved changes — click Apply to write to PlayerSettings.",
                    MessageType.Warning);
                GUI.backgroundColor = ob;
            }
        }

        // ── Order indicator bar ───────────────────────────────────────────────

        private void DrawOrderBar()
        {
            if (_managed.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No scripts have an explicit execution order.\n" +
                    "Open the Script Browser below and click  +  to add scripts.",
                    MessageType.Info);
                return;
            }

            Rect bar = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.DrawRect(bar, C_BarBg);

            int lo = _managed.Min(e => e.EditOrder);
            int hi = _managed.Max(e => e.EditOrder);
            if (lo == hi) { lo -= 100; hi += 100; }

            // Zero reference line
            float zt = Mathf.InverseLerp(lo, hi, 0f);
            EditorGUI.DrawRect(
                new Rect(bar.x + zt * bar.width - 0.5f, bar.y, 1, bar.height), C_Default);

            // One pip per script with hover tooltip
            var ev = Event.current;
            foreach (var e in _managed)
            {
                float t   = Mathf.InverseLerp(lo, hi, e.EditOrder);
                float cx  = bar.x + t * bar.width;
                Color col = e.EditOrder < 0 ? C_Before
                          : e.EditOrder > 0 ? C_After : C_Default;
                if (e.IsDirty) col = C_Dirty;

                EditorGUI.DrawRect(new Rect(cx - 2, bar.y + 3, 4, bar.height - 6), col);

                if (new Rect(cx - 6, bar.y, 12, bar.height).Contains(ev.mousePosition))
                {
                    GUI.Label(new Rect(cx - 50, bar.yMax + 2, 130, 14),
                        $"{e.TypeName} [{e.EditOrder}]", EditorStyles.miniLabel);
                    Repaint();
                }
            }

            // Direction labels
            var oc = GUI.color;
            GUI.color = C_Before;
            GUI.Label(new Rect(bar.x + 3, bar.y + 3, 58, 12), "← earlier", EditorStyles.miniLabel);
            GUI.color = C_After;
            GUI.Label(new Rect(bar.xMax - 50, bar.y + 3, 50, 12), "later →", EditorStyles.miniLabel);
            GUI.color = oc;
        }

        // ── Managed list ──────────────────────────────────────────────────────

        private void DrawManagedList()
        {
            EditorGUILayout.Space(3);

            // Column headers
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                CL("≡",           C_Handle,  22);
                CL("Order",       C_Default, 62);
                CL("Script",      C_Default, 175);
                CL("Namespace",   C_Default, 155);
                CL("Assembly",    C_Default, 120);
                GUILayout.FlexibleSpace();
                CL("↑  ↓   ↗  ×", C_Default, 95);
            }

            _managedScroll = EditorGUILayout.BeginScrollView(
                _managedScroll, GUILayout.ExpandHeight(false), GUILayout.MaxHeight(295));

            var view = Filtered(_managed);

            if (view.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    string.IsNullOrWhiteSpace(_search) && _nsIdx == 0
                        ? "No scripts with explicit execution order.\n" +
                          "Use the Script Browser below to add scripts."
                        : "No results match the current filter.",
                    MessageType.Info);
            }
            else if (_groupByNs)
            {
                foreach (var grp in view.GroupBy(e => e.Namespace).OrderBy(g => g.Key))
                {
                    var oc = GUI.color; GUI.color = C_MMS;
                    EditorGUILayout.LabelField(
                        string.IsNullOrEmpty(grp.Key) ? "(No Namespace)" : grp.Key,
                        EditorStyles.miniBoldLabel);
                    GUI.color = oc;
                    var sub = grp.OrderBy(e => e.EditOrder).ToList();
                    for (int i = 0; i < sub.Count; i++)
                        DrawManagedRow(sub[i], i, sub);
                }
            }
            else
            {
                for (int i = 0; i < view.Count; i++)
                    DrawManagedRow(view[i], i, view);
            }

            EditorGUILayout.EndScrollView();

            // Capture list rect for drag math (valid only during Repaint)
            if (Event.current.type == EventType.Repaint)
                _managedListRect = GUILayoutUtility.GetLastRect();
        }

        // ── Managed row ───────────────────────────────────────────────────────

        private void DrawManagedRow(ScriptEntry e, int rowIdx, List<ScriptEntry> ctx)
        {
            // Drag is disabled when search / filter / group is active
            bool canDrag   = string.IsNullOrEmpty(_search) && _nsIdx == 0 && !_groupByNs;
            int  globalIdx = _managed.IndexOf(e);

            Rect rowRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(ROW_H - 2));
            EditorGUI.DrawRect(rowRect, e.IsDirty ? C_RowDirty : C_RowNorm);

            // Drag initiation on the handle zone (first 22px of row)
            if (canDrag &&
                Event.current.type == EventType.MouseDown &&
                Event.current.button == 0 &&
                new Rect(rowRect.x, rowRect.y, 22, rowRect.height)
                    .Contains(Event.current.mousePosition))
            {
                _drag    = true;
                _dragSrc = globalIdx;
                _dragDst = globalIdx;
                GUIUtility.hotControl = 0;
                Event.current.Use();
            }

            // ── ≡ drag handle ─────────────────────────────────────────────────
            var oc = GUI.color;
            GUI.color = !canDrag                        ? new Color(0.25f, 0.25f, 0.25f, 1f)
                      : _drag && _dragSrc == globalIdx  ? C_MMS
                      :                                   C_Handle;
            EditorGUILayout.LabelField("≡", _handleStyle, GUILayout.Width(22));
            GUI.color = oc;

            // ── Order number ──────────────────────────────────────────────────
            Color orderCol = e.EditOrder < 0 ? C_Before
                           : e.EditOrder > 0 ? C_After : C_Default;
            oc = GUI.color; GUI.color = e.IsDirty ? C_Dirty : orderCol;
            EditorGUI.BeginChangeCheck();
            int newOrd = EditorGUILayout.DelayedIntField(e.EditOrder, GUILayout.Width(62));
            if (EditorGUI.EndChangeCheck())
            {
                e.EditOrder = newOrd;
                _hasChanges = _managed.Any(x => x.IsDirty);
                SortManaged();
            }
            GUI.color = oc;

            // ── Script name ───────────────────────────────────────────────────
            oc = GUI.color; GUI.color = e.IsDirty ? C_Dirty : Color.white;
            EditorGUILayout.LabelField(e.TypeName, EditorStyles.miniLabel, GUILayout.Width(175));

            // ── Namespace ─────────────────────────────────────────────────────
            GUI.color = C_Default;
            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(e.Namespace) ? "—" : e.Namespace,
                EditorStyles.miniLabel, GUILayout.Width(155));

            // ── Assembly ──────────────────────────────────────────────────────
            GUI.color = e.Assembly.Contains("MidManStudio") ? C_MMS : C_Default;
            EditorGUILayout.LabelField(ShortenAsm(e.Assembly),
                EditorStyles.miniLabel, GUILayout.Width(120));
            GUI.color = oc;

            GUILayout.FlexibleSpace();

            // ── ↑ ↓ ──────────────────────────────────────────────────────────
            GUI.enabled = rowIdx > 0;
            if (GUILayout.Button("↑", EditorStyles.miniButton, GUILayout.Width(20)))
                Swap(ctx, rowIdx, rowIdx - 1);
            GUI.enabled = rowIdx < ctx.Count - 1;
            if (GUILayout.Button("↓", EditorStyles.miniButton, GUILayout.Width(20)))
                Swap(ctx, rowIdx, rowIdx + 1);
            GUI.enabled = true;

            // ── Ping ──────────────────────────────────────────────────────────
            if (GUILayout.Button("↗", EditorStyles.miniButton, GUILayout.Width(22)))
            {
                Selection.activeObject = e.Script;
                EditorGUIUtility.PingObject(e.Script);
            }

            // ── Remove ────────────────────────────────────────────────────────
            oc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.75f, 0.20f, 0.20f, 1f);
            if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22)))
                RemoveFromManaged(e);
            GUI.backgroundColor = oc;

            EditorGUILayout.EndHorizontal();
        }

        // ── Drag overlay ──────────────────────────────────────────────────────

        private void DrawDragOverlay()
        {
            if (!_drag) return;
            var ev = Event.current;

            // Update drop index from mouse position
            if (ev.type == EventType.MouseDrag || ev.type == EventType.MouseMove)
            {
                _dragDst = ComputeDropIdx(ev.mousePosition.y);
                ev.Use();
            }
            else if (ev.type == EventType.MouseUp && ev.button == 0)
            {
                CommitDrag();
                ev.Use();
                return;
            }
            else if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape)
            {
                _drag    = false;
                _dragSrc = _dragDst = -1;
                ev.Use();
                return;
            }

            // Horizontal drop indicator line
            if (_managedListRect.height > 1)
            {
                float lineY = Mathf.Clamp(
                    _managedListRect.y + _dragDst * ROW_H - _managedScroll.y,
                    _managedListRect.y,
                    _managedListRect.yMax);

                EditorGUI.DrawRect(
                    new Rect(_managedListRect.x + 24, lineY - 1,
                             _managedListRect.width - 26, 2f),
                    C_Drop);
            }

            // Floating ghost label at cursor
            if (_dragSrc >= 0 && _dragSrc < _managed.Count)
            {
                string label = $"↕  {_managed[_dragSrc].TypeName}";
                Rect ghost   = new Rect(ev.mousePosition.x + 14,
                                        ev.mousePosition.y - 8, 190, 16);
                EditorGUI.DrawRect(
                    new Rect(ghost.x - 3, ghost.y - 2, ghost.width + 6, ghost.height + 4),
                    new Color(0.07f, 0.07f, 0.07f, 0.92f));
                GUI.Label(ghost, label, EditorStyles.miniLabel);
            }
        }

        private int ComputeDropIdx(float mouseY)
        {
            if (_managedListRect.height < 1) return _managed.Count;
            float rel = mouseY - _managedListRect.y + _managedScroll.y;
            return Mathf.Clamp(Mathf.RoundToInt(rel / ROW_H), 0, _managed.Count);
        }

        private void CommitDrag()
        {
            _drag = false;
            if (_dragSrc < 0 || _dragSrc >= _managed.Count ||
                _dragDst == _dragSrc || _dragDst == _dragSrc + 1)
            { _dragSrc = _dragDst = -1; return; }

            var entry = _managed[_dragSrc];
            _managed.RemoveAt(_dragSrc);
            int ins = Mathf.Clamp(
                _dragDst > _dragSrc ? _dragDst - 1 : _dragDst,
                0, _managed.Count);
            _managed.Insert(ins, entry);

            // Re-number to make the new order unambiguous
            AutoNumber();
            _dragSrc = _dragDst = -1;
        }

        // ── Script browser ────────────────────────────────────────────────────

        private void DrawBrowser()
        {
            EditorGUILayout.Space(2);
            var oc = GUI.color; GUI.color = C_Default;
            EditorGUILayout.LabelField(
                "Click  +  to add a MonoBehaviour to the explicit execution order list.",
                EditorStyles.wordWrappedMiniLabel);
            GUI.color = oc;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                CL("Script",    C_Default, 210);
                CL("Namespace", C_Default, 168);
                CL("Assembly",  C_Default, 135);
                GUILayout.FlexibleSpace();
                CL("↗  +",     C_Default, 50);
            }

            _browserScroll = EditorGUILayout.BeginScrollView(
                _browserScroll, GUILayout.MaxHeight(138));

            var view = Filtered(_unmanaged);
            if (view.Count == 0)
                EditorGUILayout.HelpBox(
                    "No results. Try clearing the filter, or all MonoBehaviours " +
                    "may already be in the managed list.",
                    MessageType.Info);
            else
                foreach (var e in view)
                    DrawBrowserRow(e);

            EditorGUILayout.EndScrollView();
        }

        private void DrawBrowserRow(ScriptEntry e)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(e.TypeName,
                    EditorStyles.miniLabel, GUILayout.Width(210));

                var oc = GUI.color;
                GUI.color = C_Default;
                EditorGUILayout.LabelField(
                    string.IsNullOrEmpty(e.Namespace) ? "—" : e.Namespace,
                    EditorStyles.miniLabel, GUILayout.Width(168));
                GUI.color = e.Assembly.Contains("MidManStudio") ? C_MMS : C_Default;
                EditorGUILayout.LabelField(ShortenAsm(e.Assembly),
                    EditorStyles.miniLabel, GUILayout.Width(135));
                GUI.color = oc;

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("↗", EditorStyles.miniButton, GUILayout.Width(22)))
                    EditorGUIUtility.PingObject(e.Script);

                oc = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.20f, 0.62f, 0.20f, 1f);
                if (GUILayout.Button("+", EditorStyles.miniButton, GUILayout.Width(22)))
                    AddToManaged(e);
                GUI.backgroundColor = oc;
            }
        }

        // ── Footer ────────────────────────────────────────────────────────────

        private void DrawFooter()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var oc = GUI.color; GUI.color = C_Default;
                EditorGUILayout.LabelField(
                    $"Managed: {_managed.Count}   |   Unmanaged: {_unmanaged.Count}",
                    EditorStyles.miniLabel, GUILayout.Width(195));
                GUI.color = oc;

                GUILayout.Space(8);
                EditorGUILayout.LabelField("Step:", GUILayout.Width(35));
                _autoStep = EditorGUILayout.IntField(_autoStep, GUILayout.Width(55));
                if (GUILayout.Button("Auto Number", GUILayout.Width(100)))
                    AutoNumber();

                GUILayout.FlexibleSpace();

                if (_hasChanges)
                {
                    var ob = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.22f, 0.68f, 0.22f, 1f);
                    if (GUILayout.Button("✓  Apply", GUILayout.Height(26), GUILayout.Width(90)))
                        ApplyChanges();
                    GUI.backgroundColor = new Color(0.65f, 0.22f, 0.22f, 1f);
                    if (GUILayout.Button("✕  Discard", GUILayout.Height(26), GUILayout.Width(90)))
                        Scan(); // full re-scan resets all pending edits
                    GUI.backgroundColor = ob;
                }
                else
                {
                    var oc = GUI.color;
                    GUI.color = new Color(0.30f, 0.82f, 0.30f, 0.85f);
                    EditorGUILayout.LabelField("✓ Saved", EditorStyles.miniBoldLabel);
                    GUI.color = oc;
                }
            }
            EditorGUILayout.Space(4);
        }

        // ── Data operations ───────────────────────────────────────────────────

        private void Scan()
        {
            _managed.Clear();
            _unmanaged.Clear();
            _drag       = false;
            _dragSrc    = _dragDst = -1;
            _hasChanges = false;

            var guids = AssetDatabase.FindAssets("t:MonoScript");
            foreach (var guid in guids)
            {
                string path   = AssetDatabase.GUIDToAssetPath(guid);
                var    script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null) continue;

                var type = script.GetClass();
                if (type == null || type.IsAbstract) continue;
                if (!typeof(MonoBehaviour).IsAssignableFrom(type)) continue;

                int    ord = PlayerSettings.GetScriptExecutionOrder(script);
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

                if (ord != 0) _managed.Add(entry);
                else          _unmanaged.Add(entry);
            }

            SortManaged();
            _unmanaged = _unmanaged
                .OrderBy(e => e.Namespace)
                .ThenBy(e => e.TypeName)
                .ToList();

            // Rebuild namespace dropdown options
            var nsSet = new HashSet<string>();
            foreach (var e in _managed)   nsSet.Add(e.Namespace);
            foreach (var e in _unmanaged) nsSet.Add(e.Namespace);
            _nsOptions = new[] { "All Namespaces" }
                .Concat(nsSet.OrderBy(n => n))
                .ToArray();
            if (_nsIdx >= _nsOptions.Length) _nsIdx = 0;
        }

        private void SortManaged() =>
            _managed = _managed
                .OrderBy(e => e.EditOrder)
                .ThenBy(e => e.TypeName)
                .ToList();

        private List<ScriptEntry> Filtered(List<ScriptEntry> src)
        {
            string nsF = _nsIdx > 0 && _nsIdx < _nsOptions.Length
                ? _nsOptions[_nsIdx] : "";
            string q = _search.ToLowerInvariant();

            return src.Where(e =>
            {
                bool ns = string.IsNullOrEmpty(nsF) || e.Namespace == nsF;
                bool sq = string.IsNullOrEmpty(q) ||
                          e.TypeName.ToLowerInvariant().Contains(q) ||
                          e.Namespace.ToLowerInvariant().Contains(q) ||
                          e.Assembly.ToLowerInvariant().Contains(q);
                return ns && sq;
            }).ToList();
        }

        private void AddToManaged(ScriptEntry e)
        {
            int newOrd = _managed.Count > 0
                ? _managed.Max(x => x.EditOrder) + _autoStep
                : _autoStep;
            e.EditOrder = newOrd;
            _unmanaged.Remove(e);
            _managed.Add(e);
            SortManaged();
            _hasChanges = true;
        }

        private void RemoveFromManaged(ScriptEntry e)
        {
            e.EditOrder = 0;
            _managed.Remove(e);
            _unmanaged.Add(e);
            _unmanaged = _unmanaged
                .OrderBy(x => x.Namespace)
                .ThenBy(x => x.TypeName)
                .ToList();
            _hasChanges = true;
        }

        private void Swap(List<ScriptEntry> ctx, int a, int b)
        {
            // Swap the EditOrder values of the two entries so visual position
            // (determined by order number) is exchanged. SortManaged() then
            // re-sorts the backing list to reflect the new numbers.
            (ctx[a].EditOrder, ctx[b].EditOrder) = (ctx[b].EditOrder, ctx[a].EditOrder);

            // If the two orders were equal (shouldn't happen in normal use but can
            // occur with manually set values), nudge the "lower" entry by 1 to
            // guarantee distinct ordering without re-numbering everything.
            if (ctx[a].EditOrder == ctx[b].EditOrder)
                ctx[b].EditOrder = ctx[a].EditOrder + 1;

            SortManaged();
            _hasChanges = true;
        }

        private void AutoNumber()
        {
            // Centre around 0: negative half executes before default, positive after.
            int count = _managed.Count;
            if (count == 0) return;
            int mid = count / 2;
            for (int i = 0; i < count; i++)
                _managed[i].EditOrder = (i - mid) * _autoStep;
            SortManaged();
            _hasChanges = true;
        }

        private void ApplyChanges()
        {
            // Write managed scripts
            foreach (var e in _managed)
            {
                PlayerSettings.SetScriptExecutionOrder(e.Script, e.EditOrder);
                e.SavedOrder = e.EditOrder;
            }

            // Zero out scripts that were removed from the managed list this session
            foreach (var e in _unmanaged.Where(e => e.SavedOrder != 0))
            {
                PlayerSettings.SetScriptExecutionOrder(e.Script, 0);
                e.SavedOrder = 0;
            }

            AssetDatabase.SaveAssets();
            _hasChanges = false;
            Debug.Log($"[MID_ScriptExecOrder] Applied {_managed.Count} explicit order(s).");
        }

        // ── Style helpers ─────────────────────────────────────────────────────

        private void BuildStyles()
        {
            if (_stylesBuilt) return;
            _handleStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize  = 14,
                fontStyle = FontStyle.Bold
            };
            _stylesBuilt = true;
        }

        private static void CL(string t, Color c, float w)
        {
            var oc = GUI.color; GUI.color = c;
            EditorGUILayout.LabelField(t, EditorStyles.miniBoldLabel, GUILayout.Width(w));
            GUI.color = oc;
        }

        private static void Sep()
        {
            Rect r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0.45f, 0.45f, 0.45f, 0.35f));
            EditorGUILayout.Space(2);
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
