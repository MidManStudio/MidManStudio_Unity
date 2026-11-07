// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/editor-helpers.md, section "MidEditorUIElements.cs"
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MidManStudio.Core.EditorTools
{
    // ── Gradient Banner ────────────────────────────────────────────────────────

    /// <summary>
    /// A four-corner gradient drawn as one mesh (no SVG package needed). Create it from C# and insert it into the
    /// tree, which avoids the fully-qualified custom-element syntax in .uxml files. The four colour fields can be
    /// changed after construction. It ignores the mouse.
    /// </summary>
    public sealed class GradientBannerElement : VisualElement
    {
        // Default palette — override after construction if needed
        public Color ColorTL = new Color(0.17f, 0.39f, 0.38f, 1f); // dark teal
        public Color ColorTR = new Color(0.10f, 0.22f, 0.30f, 1f); // dark blue
        public Color ColorBL = new Color(0.08f, 0.08f, 0.10f, 1f); // near-black
        public Color ColorBR = new Color(0.07f, 0.07f, 0.09f, 1f); // near-black

        public GradientBannerElement()
        {
            pickingMode            = PickingMode.Ignore;
            generateVisualContent += Paint;
        }

        private void Paint(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width < 1f || r.height < 1f) return;

            // ctx.Allocate → 4 verts, 2 triangles, bilinear colour interpolation = gradient
            var m = ctx.Allocate(4, 6);
            float z = Vertex.nearZ;

            m.SetNextVertex(new Vertex { position = new Vector3(0f,      0f,       z), tint = ColorTL });
            m.SetNextVertex(new Vertex { position = new Vector3(r.width, 0f,       z), tint = ColorTR });
            m.SetNextVertex(new Vertex { position = new Vector3(0f,      r.height, z), tint = ColorBL });
            m.SetNextVertex(new Vertex { position = new Vector3(r.width, r.height, z), tint = ColorBR });

            m.SetNextIndex(0); m.SetNextIndex(1); m.SetNextIndex(2);
            m.SetNextIndex(2); m.SetNextIndex(1); m.SetNextIndex(3);
        }
    }

    // ── Order Bar ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A strip with one dot per script, placed by its execution order between the lowest and highest value (a zero
    /// line is drawn). Dots are yellow when changed and unsaved, blue before zero, orange after zero and grey at
    /// zero. Hovering shows the name and order of the nearest dot as a tooltip. Insert it from C# into the element
    /// named "order-bar" after cloning the UXML, and call <see cref="UpdateData"/> whenever the list changes.
    /// </summary>
    public sealed class OrderBarElement : VisualElement
    {
        private readonly struct Pip
        {
            public readonly string TypeName;
            public readonly int    Order;
            public readonly bool   Dirty;
            public Pip(string n, int o, bool d) { TypeName = n; Order = o; Dirty = d; }
        }

        private readonly List<Pip> _pips = new();
        private int _lo = -200, _hi = 200;

        // Palette
        private static readonly Color ColBefore  = new Color(0.40f, 0.65f, 1.00f, 1f);
        private static readonly Color ColAfter   = new Color(1.00f, 0.60f, 0.25f, 1f);
        private static readonly Color ColDefault = new Color(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color ColDirty   = new Color(1.00f, 0.90f, 0.25f, 1f);
        private static readonly Color ColBg      = new Color(0.07f, 0.07f, 0.08f, 1.00f);
        private static readonly Color ColZero    = new Color(0.55f, 0.55f, 0.55f, 0.70f);
        private static readonly Color ColMms     = new Color(0.28f, 0.90f, 0.45f, 0.90f);

        public OrderBarElement()
        {
            generateVisualContent += Paint;
            RegisterCallback<MouseMoveEvent>(OnHover);
        }

        /// <summary>
        /// Feed updated script data. Call after any order change or list refresh. The <c>isMms</c> flag of each
        /// item is accepted but not used.
        /// </summary>
        public void UpdateData(IEnumerable<(string name, int order, bool dirty, bool isMms)> items)
        {
            _pips.Clear();
            _lo = int.MaxValue; _hi = int.MinValue;
            foreach (var (n, o, d, _) in items)
            {
                _pips.Add(new Pip(n, o, d));
                if (o < _lo) _lo = o;
                if (o > _hi) _hi = o;
            }
            if (_pips.Count == 0) { _lo = -200; _hi = 200; }
            else if (_lo == _hi)  { _lo -= 100; _hi += 100; }
            MarkDirtyRepaint();
        }

        private void Paint(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width < 1f) return;

            var p = ctx.painter2D;

            // Background
            p.fillColor = ColBg;
            p.BeginPath();
            p.MoveTo(new Vector2(0f,      0f));
            p.LineTo(new Vector2(r.width, 0f));
            p.LineTo(new Vector2(r.width, r.height));
            p.LineTo(new Vector2(0f,      r.height));
            p.ClosePath();
            p.Fill();

            // Zero reference line
            float zt = Mathf.InverseLerp(_lo, _hi, 0);
            float zx = zt * r.width;
            p.strokeColor = ColZero;
            p.lineWidth   = 1f;
            p.BeginPath();
            p.MoveTo(new Vector2(zx, 2f));
            p.LineTo(new Vector2(zx, r.height - 2f));
            p.Stroke();

            // Direction labels (rendered as tiny pips at edges)
            float cy = r.height * 0.5f;

            // Script pips
            foreach (var pip in _pips)
            {
                float t  = Mathf.InverseLerp(_lo, _hi, pip.Order);
                float cx = Mathf.Clamp(t * r.width, 2f, r.width - 2f);
                Color col = pip.Dirty   ? ColDirty
                          : pip.Order < 0 ? ColBefore
                          : pip.Order > 0 ? ColAfter
                          :                 ColDefault;
                p.fillColor = col;
                p.BeginPath();
                p.Arc(new Vector2(cx, cy), 3.5f, 0f, 360f);
                p.Fill();
            }
        }

        private void OnHover(MouseMoveEvent evt)
        {
            if (_pips.Count == 0) return;
            float mx = evt.localMousePosition.x;
            float w  = contentRect.width;
            if (w < 1f) return;

            string best = string.Empty;
            float  dist = 14f;
            foreach (var pip in _pips)
            {
                float t  = Mathf.InverseLerp(_lo, _hi, pip.Order);
                float cx = t * w;
                float d  = Mathf.Abs(mx - cx);
                if (d < dist) { dist = d; best = $"{pip.TypeName}  [{pip.Order}]"; }
            }
            if (tooltip != best) tooltip = best;
        }
    }

    // ── Info Popup Handler ─────────────────────────────────────────────────────

    /// <summary>
    /// Manages the floating info panel defined in UXML: an element named "info-popup" holding labels named
    /// "info-popup-title" and "info-popup-body". Call <see cref="Toggle"/> from each help button's clicked callback.
    /// A click anywhere outside the panel hides it. If the UXML has no "info-popup" element, every call does
    /// nothing and <see cref="IsAvailable"/> is false. The two labels are not checked, so a panel without them
    /// throws when shown.
    /// </summary>
    public sealed class InfoPopupHandler
    {
        private readonly VisualElement _popup;
        private readonly Label         _titleLbl;
        private readonly Label         _bodyLbl;
        private VisualElement          _currentAnchor;

        /// <param name="root">The root of the window that holds the info-popup element.</param>
        public InfoPopupHandler(VisualElement root)
        {
            _popup    = root.Q<VisualElement>("info-popup");
            _titleLbl = root.Q<Label>("info-popup-title");
            _bodyLbl  = root.Q<Label>("info-popup-body");

            if (_popup == null) return;

            // Dismiss when clicking anywhere outside the popup
            root.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (_popup.style.display == DisplayStyle.None) return;
                if (!_popup.worldBound.Contains(evt.mousePosition)) Hide();
            }, TrickleDown.TrickleDown);
        }

        /// <summary>True when the UXML contains the info-popup element.</summary>
        public bool IsAvailable => _popup != null;

        /// <summary>Shows the popup under the button, or hides it when it is already showing for that same button.</summary>
        public void Toggle(Button anchor, string title, string body)
        {
            if (_popup == null) return;
            bool sameAnchor = (_currentAnchor == anchor) &&
                              (_popup.style.display == DisplayStyle.Flex);
            if (sameAnchor) { Hide(); return; }
            Show(anchor, title, body);
        }

        /// <summary>Shows the popup under the anchor, after the next layout pass, kept inside the window width.</summary>
        public void Show(VisualElement anchor, string title, string body)
        {
            if (_popup == null) return;
            _currentAnchor = anchor;
            _titleLbl.text = title;
            _bodyLbl.text  = body;

            _popup.style.display  = DisplayStyle.Flex;
            _popup.style.position = Position.Absolute;

            // Defer positioning until after layout pass
            _popup.schedule.Execute(() =>
            {
                if (anchor.panel == null || _popup.parent == null) return;
                var root = _popup.parent;
                Vector2 world = anchor.LocalToWorld(new Vector2(0f, anchor.layout.height + 5f));
                Vector2 local = root.WorldToLocal(world);
                float   maxL  = Mathf.Max(8f, root.layout.width - _popup.layout.width - 8f);
                _popup.style.left = Mathf.Clamp(local.x, 8f, maxL);
                _popup.style.top  = local.y;
                _popup.BringToFront();
            });
        }

        /// <summary>Hides the popup.</summary>
        public void Hide()
        {
            if (_popup != null) _popup.style.display = DisplayStyle.None;
            _currentAnchor = null;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads UXML and USS files by file name. The search covers the whole project, packages included, and returns
    /// the first asset whose file name matches exactly. Nothing is cached.
    /// </summary>
    public static class MidEditorUIHelpers
    {
        /// <summary>Find a .uxml VisualTreeAsset by file name (no extension). Logs a warning and returns null when none is found.</summary>
        public static VisualTreeAsset FindUxml(string baseName)
        {
            foreach (var guid in AssetDatabase.FindAssets($"{baseName} t:VisualTreeAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == baseName)
                    return AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
            }
            Debug.LogWarning($"[MidEditorUIHelpers] Could not find UXML: {baseName}.uxml — check it is in an Editor folder.");
            return null;
        }

        /// <summary>Find a .uss StyleSheet by file name (no extension). Logs a warning and returns null when none is found.</summary>
        public static StyleSheet FindUss(string baseName)
        {
            foreach (var guid in AssetDatabase.FindAssets($"{baseName} t:StyleSheet"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == baseName)
                    return AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            }
            Debug.LogWarning($"[MidEditorUIHelpers] Could not find USS: {baseName}.uss — check it is in an Editor folder.");
            return null;
        }
    }
}
#endif
