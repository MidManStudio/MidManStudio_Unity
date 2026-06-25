// Shared custom VisualElements and utilities for MidManStudio editor windows.
// — GradientBannerElement : draws a four-corner gradient header (no SVG package needed)
// — OrderBarElement       : draws the execution-order pip visualiser
// — InfoPopupHandler      : manages the floating info-popup panel defined in UXML
// — MidEditorUIHelpers    : UXML / USS asset loading

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
    // Create from C# and insert into the tree — avoids the fully-qualified
    // custom-element syntax in .uxml files while still being reusable.

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
    // Insert into the VisualElement named "order-bar" from C# after UXML clone.
    // Call UpdateData() whenever the managed-script list changes.

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

        /// <summary>Feed updated script data. Call after any order change or list refresh.</summary>
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
    // Manages the floating info panel (name="info-popup") defined in UXML.
    // Call Toggle() from each ? button's clicked callback.

    public sealed class InfoPopupHandler
    {
        private readonly VisualElement _popup;
        private readonly Label         _titleLbl;
        private readonly Label         _bodyLbl;
        private VisualElement          _currentAnchor;

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

        public bool IsAvailable => _popup != null;

        public void Toggle(Button anchor, string title, string body)
        {
            if (_popup == null) return;
            bool sameAnchor = (_currentAnchor == anchor) &&
                              (_popup.style.display == DisplayStyle.Flex);
            if (sameAnchor) { Hide(); return; }
            Show(anchor, title, body);
        }

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

        public void Hide()
        {
            if (_popup != null) _popup.style.display = DisplayStyle.None;
            _currentAnchor = null;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    public static class MidEditorUIHelpers
    {
        /// <summary>Find a .uxml VisualTreeAsset by file name (no extension).</summary>
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

        /// <summary>Find a .uss StyleSheet by file name (no extension).</summary>
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
