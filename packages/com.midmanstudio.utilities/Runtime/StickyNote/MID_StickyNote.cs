// UGUI-based sticky note overlay. Builds its own self-contained Canvas at
// runtime AND in the editor ([ExecuteAlways]) so it appears correctly in the
// Game View both in and out of Play Mode.
//
// WHY THIS IS UGUI, NOT IMGUI (OnGUI):
//   OnGUI() simply does not run in the Game View outside Play Mode — even
//   with [ExecuteAlways]. That attribute enables Update()/OnEnable() in edit
//   mode, but never the IMGUI render pass. RectTransform-driven UI has no
//   such limitation: it renders through the normal Canvas pipeline, which
//   the Game View draws regardless of Play state.
//
//   OnGUI also mixes Screen.width/height (physical pixels) with
//   Event.current.mousePosition (scaled by EditorGUIUtility.pixelsPerPoint
//   on HiDPI/Retina displays) — a classic cause of "position drifts and
//   doesn't land where it should." RectTransform anchoring has no such
//   mismatch.
//
// DRAG / CLICK LIMITATION (expected, not a bug):
//   Dragging, minimizing, and closing go through the standard UGUI
//   EventSystem (IBeginDragHandler/IDragHandler), which only processes
//   input during Play Mode — exactly like every Button in your project.
//   Position and appearance preview correctly in edit mode; interaction
//   requires Play Mode.
//
// EDIT-MODE BUILD TIMING:
//   OnEnable() (which [ExecuteAlways] also fires in edit mode) defers the
//   actual Rebuild() by one editor tick via EditorApplication.delayCall.
//   Building synchronously inside OnEnable — which itself can be firing as
//   part of Unity's own message-sending pass (domain reload, scene load,
//   prefab stage entry) — triggers "SendMessage cannot be called during
//   Awake, CheckConsistency, or OnValidate" the moment AddComponent runs on
//   the new Canvas/EventSystem. Deferring runs the build outside that pass.
//   Play mode doesn't have this restriction and needs zero-frame init, so
//   it stays synchronous there.
//
// EVENTSYSTEM OWNERSHIP:
//   The auto-created EventSystem is reference-counted across every
//   MID_StickyNote instance that ends up using it, and only destroyed once
//   none remain. A pre-existing user-placed EventSystem is never touched.

using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MidManStudio.Core.Notes
{
    [AddComponentMenu("MidManStudio/Utilities/Sticky Note")]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class MID_StickyNote : MonoBehaviour
    {
        public enum NoteTheme  { Yellow, Blue, Green, Pink, Dark }
        public enum NoteAnchor { TopLeft, TopRight, BottomLeft, BottomRight, Center, Free }

        // ── Inspector — Content ──────────────────────────────────────────────

        [Header("Content")]
        [SerializeField] private string _title = "Scene Notes";

        [SerializeField] private List<string> _notes = new List<string>
        {
            "Welcome to this scene!",
            "• Step 1: Do something here.",
            "• Step 2: Then do this.",
        };

        [Tooltip("Optional .txt file. Content is appended after the notes list.")]
        [SerializeField] private TextAsset _textFile;

        // ── Inspector — Appearance ───────────────────────────────────────────

        [Header("Appearance")]
        [SerializeField] private NoteTheme _theme = NoteTheme.Yellow;
        [SerializeField] [Range(9, 28)] private int _fontSize = 14;

        [Tooltip("Override the theme's default body text color.")]
        [SerializeField] private bool  _useCustomTextColor = false;
        [SerializeField] private Color _customTextColor    = Color.black;

        [SerializeField] [Range(160f, 700f)] private float _width         = 320f;
        [SerializeField] [Range(80f,  900f)] private float _maxBodyHeight = 360f;
        [Tooltip("Inner padding added above/below the note text.")]
        [SerializeField] [Range(2f, 24f)]    private float _cornerPadding = 8f;

        // ── Inspector — Layout / Position ────────────────────────────────────

        [Header("Position")]
        [SerializeField] private NoteAnchor _anchor    = NoteAnchor.TopLeft;
        [SerializeField] private Vector2    _margin    = new Vector2(16f, 16f);
        [Tooltip("Drag the header to reposition. Only works in Play Mode — " +
                 "same limitation as any other UI element in Unity.")]
        [SerializeField] private bool       _draggable = true;

        [Tooltip("Sorting order of the auto-created Canvas. Higher draws on top of other UI.")]
        [SerializeField] private int _sortingOrder = 500;

        // ── Inspector — Behaviour ─────────────────────────────────────────────

        [Header("Behaviour")]
        [SerializeField] private bool _startVisible    = true;
        [Tooltip("Build and preview the note in the Game View while not in Play Mode. " +
                 "Dragging/closing still require Play Mode either way.")]
        [SerializeField] private bool _buildInEditMode = true;

        // ── Theme palette ─────────────────────────────────────────────────────

        private static readonly Color[] s_CHead = {
            new Color(0.95f, 0.80f, 0.10f, 1f),
            new Color(0.20f, 0.45f, 0.82f, 1f),
            new Color(0.16f, 0.62f, 0.24f, 1f),
            new Color(0.84f, 0.24f, 0.52f, 1f),
            new Color(0.12f, 0.12f, 0.16f, 1f),
        };
        private static readonly Color[] s_CBody = {
            new Color(0.99f, 0.96f, 0.60f, 1f),
            new Color(0.78f, 0.87f, 0.98f, 1f),
            new Color(0.76f, 0.95f, 0.77f, 1f),
            new Color(0.97f, 0.78f, 0.86f, 1f),
            new Color(0.18f, 0.18f, 0.22f, 1f),
        };
        private static readonly Color[] s_CText = {
            new Color(0.14f, 0.10f, 0.02f, 1f),
            new Color(0.07f, 0.10f, 0.24f, 1f),
            new Color(0.05f, 0.18f, 0.07f, 1f),
            new Color(0.22f, 0.05f, 0.11f, 1f),
            new Color(0.90f, 0.90f, 0.92f, 1f),
        };
        private static readonly Color[] s_CHeadText = {
            new Color(0.18f, 0.12f, 0.0f, 1f),
            Color.white, Color.white, Color.white, Color.white,
        };

        private const float HeaderHeight = 30f;

        // ── Built hierarchy refs ──────────────────────────────────────────────

        private Canvas        _canvas;
        private CanvasScaler  _scaler;
        private RectTransform _panel;
        private RectTransform _header;
        private Image          _headerImg;
        private Text            _titleText;
        private Button           _minBtn;
        private Button           _closeBtn;
        private RectTransform _body;
        private Image           _bodyImg;
        private ScrollRect       _scrollRect;
        private RectTransform _content;
        private Text             _bodyText;

        private RectTransform _pinButton;
        private Button          _pinBtnComp;

        private bool _visible;
        private bool _minimized;
        private bool _built;
        private bool _contentDirty = true;
        private string _cachedBody = "";

        private const string CanvasNamePrefix = "StickyNoteCanvas_";

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void OnEnable()
        {
            _visible      = _startVisible;
            _minimized    = false;
            _contentDirty = true;

            if (!ShouldBeBuilt()) return;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                // See file header — deferred to dodge the SendMessage-during-a-
                // message-pass reentrancy warning.
                EditorApplication.delayCall += DeferredRebuildIfNeeded;
                return;
            }
#endif
            Rebuild();
        }

#if UNITY_EDITOR
        private void DeferredRebuildIfNeeded()
        {
            if (this == null) return;   // destroyed before delayCall fired
            if (_built) return;         // Update() or another path already built it
            if (!ShouldBeBuilt()) return;
            Rebuild();
        }
#endif

        private void OnDisable() => DestroyBuiltHierarchy();
        private void OnDestroy() => DestroyBuiltHierarchy();

        private void OnValidate()
        {
            _contentDirty = true;
            if (!_built) return;
            ApplyAnchor();
            ApplyTheme();
            ApplyLayoutSettings();
            RefreshBodyText();
        }

        private void Update()
        {
            bool should = ShouldBeBuilt();
            if (!should)
            {
                if (_built) DestroyBuiltHierarchy();
                return;
            }
            if (!_built) { Rebuild(); return; }
            if (_contentDirty) RefreshBodyText();
        }

        private bool ShouldBeBuilt() =>
            (Application.isPlaying || _buildInEditMode) && gameObject.scene.IsValid();

        // ── Build ─────────────────────────────────────────────────────────────

        private void Rebuild()
        {
            DestroyBuiltHierarchy();
            BuildCanvas();
            BuildPanel();
            BuildHeader();
            BuildBody();
            BuildPinButton();

            ApplyAnchor();          // fix panel anchors to a point BEFORE sizing
            ApplyTheme();
            ApplyLayoutSettings();  // sizeDelta now actually has effect
            RefreshBodyText();
            ApplyVisibilityState();

            _built = true;
        }

        /// <summary>Manually force a full rebuild — exposed for the inspector's "Rebuild" button.</summary>
        public void RebuildNow() => Rebuild();

        private void DestroyBuiltHierarchy()
        {
            if (_canvas != null)
            {
                if (Application.isPlaying) Destroy(_canvas.gameObject);
                else                        DestroyImmediate(_canvas.gameObject);
            }
            _canvas = null; _panel = null; _header = null; _body = null;
            _content = null; _pinButton = null;

            ReleaseOwnedEventSystemRef();
            _built = false;
        }

        private void BuildCanvas()
        {
            var go = new GameObject(CanvasNamePrefix + GetInstanceID(), typeof(RectTransform));
            go.transform.SetParent(transform, false);
            go.hideFlags = HideFlags.DontSave; // never written into the scene file

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = _sortingOrder;

            _scaler = go.AddComponent<CanvasScaler>();
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            go.AddComponent<GraphicRaycaster>();

            EnsureEventSystem();
        }

        // If your project uses the new Input System exclusively, place your own
        // EventSystem + InputSystemUIInputModule in the scene ahead of time — we
        // detect and reuse any existing EventSystem instead of creating one, and
        // never touch a user-placed one. When we DO create one, it's reference-
        // counted across every MID_StickyNote instance that used it, and only
        // destroyed once none remain — this is what stopped duplicate
        // EventSystems from accumulating.
        private static EventSystem s_ownedEventSystem;
        private static int         s_ownedEventSystemRefCount;
        private bool _ownsEventSystemRef;

        private void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;

            if (s_ownedEventSystem != null)
            {
                ClaimOwnedEventSystemRef();
                return;
            }

            // Inactive-inclusive scan is more reliable than a single current-only
            // check across edit-mode edge cases (prefab stage entry, freshly-
            // recompiled domain) — if literally anything exists, leave it alone.
            var existing = FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (existing != null && existing.Length > 0) return;

            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            go.hideFlags = HideFlags.DontSave;
            s_ownedEventSystem = go.GetComponent<EventSystem>();
            ClaimOwnedEventSystemRef();
        }

        private void ClaimOwnedEventSystemRef()
        {
            if (_ownsEventSystemRef) return;
            _ownsEventSystemRef = true;
            s_ownedEventSystemRefCount++;
        }

        private void ReleaseOwnedEventSystemRef()
        {
            if (!_ownsEventSystemRef) return;
            _ownsEventSystemRef = false;
            s_ownedEventSystemRefCount = Mathf.Max(0, s_ownedEventSystemRefCount - 1);

            if (s_ownedEventSystemRefCount == 0 && s_ownedEventSystem != null)
            {
                if (Application.isPlaying) Destroy(s_ownedEventSystem.gameObject);
                else                        DestroyImmediate(s_ownedEventSystem.gameObject);
                s_ownedEventSystem = null;
            }
        }

        private void BuildPanel()
        {
            _panel = CreateRect("NotePanel", _canvas.transform);
            _panel.gameObject.AddComponent<CanvasGroup>();
        }

        private void BuildHeader()
        {
            _header = CreateRect("Header", _panel);
            _header.anchorMin = new Vector2(0f, 1f);
            _header.anchorMax = new Vector2(1f, 1f);
            _header.pivot     = new Vector2(0.5f, 1f);
            _header.anchoredPosition = Vector2.zero;
            _header.sizeDelta = new Vector2(0f, HeaderHeight);

            _headerImg = _header.gameObject.AddComponent<Image>();

            // Dragging the header moves the panel — only fires while EventSystem
            // is processing input, i.e. Play Mode.
            var drag = _header.gameObject.AddComponent<MID_StickyNoteDragHandler>();
            drag.Target    = _panel;
            drag.Canvas    = _canvas;
            drag.IsEnabled = () => _draggable;
            drag.OnDragged += () => { _anchor = NoteAnchor.Free; };

            var titleRect = CreateRect("Title", _header);
            titleRect.anchorMin = new Vector2(0f, 0f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.offsetMin = new Vector2(10f, 0f);
            titleRect.offsetMax = new Vector2(-58f, 0f);
            _titleText = titleRect.gameObject.AddComponent<Text>();
            _titleText.font      = GetDefaultFont();
            _titleText.alignment = TextAnchor.MiddleLeft;
            _titleText.fontStyle = FontStyle.Bold;
            _titleText.raycastTarget = false;

            _minBtn = CreateHeaderButton("MinBtn", "–", new Vector2(-50f, 0f));
            _minBtn.onClick.AddListener(() => SetMinimized(!_minimized));

            _closeBtn = CreateHeaderButton("CloseBtn", "×", new Vector2(-26f, 0f));
            _closeBtn.onClick.AddListener(Hide);
        }

        private Button CreateHeaderButton(string name, string label, Vector2 anchoredOffsetFromRight)
        {
            var rect = CreateRect(name, _header);
            rect.anchorMin = new Vector2(1f, 0.5f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot     = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(22f, 22f);
            rect.anchoredPosition = anchoredOffsetFromRight;

            var img = rect.gameObject.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.18f);

            var btn = rect.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.32f);
            colors.pressedColor     = new Color(1f, 1f, 1f, 0.45f);
            btn.colors = colors;

            var lblRect = CreateRect("Label", rect);
            lblRect.anchorMin = Vector2.zero;
            lblRect.anchorMax = Vector2.one;
            var lbl = lblRect.gameObject.AddComponent<Text>();
            lbl.font      = GetDefaultFont();
            lbl.text      = label;
            lbl.alignment = TextAnchor.MiddleCenter;
            lbl.fontStyle = FontStyle.Bold;
            lbl.color     = Color.white;
            lbl.raycastTarget = false;

            return btn;
        }

        private void BuildBody()
        {
            _body = CreateRect("Body", _panel);
            _body.anchorMin = Vector2.zero;
            _body.anchorMax = Vector2.one;
            _body.offsetMax = new Vector2(0f, -HeaderHeight); // sits below header

            _bodyImg = _body.gameObject.AddComponent<Image>();

            _scrollRect = _body.gameObject.AddComponent<ScrollRect>();
            _scrollRect.horizontal        = false;
            _scrollRect.vertical          = true;
            _scrollRect.movementType      = ScrollRect.MovementType.Clamped;
            _scrollRect.scrollSensitivity = 25f;

            var viewport = CreateRect("Viewport", _body);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(4f, 4f);
            viewport.offsetMax = new Vector2(-4f, -4f);
            viewport.gameObject.AddComponent<RectMask2D>();

            _content = CreateRect("Content", viewport);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot     = new Vector2(0.5f, 1f);
            _content.anchoredPosition = Vector2.zero;

            var fitter = _content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            _bodyText = _content.gameObject.AddComponent<Text>();
            _bodyText.font               = GetDefaultFont();
            _bodyText.alignment          = TextAnchor.UpperLeft;
            _bodyText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _bodyText.verticalOverflow   = VerticalWrapMode.Overflow;
            _bodyText.raycastTarget      = false;

            _scrollRect.viewport = viewport;
            _scrollRect.content  = _content;
        }

        private void BuildPinButton()
        {
            _pinButton = CreateRect("PinButton", _canvas.transform);
            _pinButton.sizeDelta = new Vector2(30f, 30f);

            var img = _pinButton.gameObject.AddComponent<Image>();
            _pinBtnComp = _pinButton.gameObject.AddComponent<Button>();
            _pinBtnComp.targetGraphic = img;
            _pinBtnComp.onClick.AddListener(Show);

            var lblRect = CreateRect("Label", _pinButton);
            lblRect.anchorMin = Vector2.zero;
            lblRect.anchorMax = Vector2.one;
            var lbl = lblRect.gameObject.AddComponent<Text>();
            lbl.font      = GetDefaultFont();
            lbl.text      = "📌";
            lbl.alignment = TextAnchor.MiddleCenter;
            lbl.fontSize  = 16;
            lbl.color     = Color.white;
            lbl.raycastTarget = false;

            var pinAnchor = _anchor == NoteAnchor.Free ? NoteAnchor.TopLeft : _anchor;
            ApplyAnchorTo(_pinButton, pinAnchor, _margin);
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            return rt;
        }

        private static Font GetDefaultFont() =>
            Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ??
            Font.CreateDynamicFontFromOSFont("Arial", 14);

        // ── Theme / layout application ───────────────────────────────────────

        private void ApplyTheme()
        {
            int t = (int)_theme;
            if (_headerImg != null) _headerImg.color = s_CHead[t];
            if (_bodyImg   != null) _bodyImg.color   = s_CBody[t];

            if (_titleText != null)
            {
                _titleText.color    = s_CHeadText[t];
                _titleText.fontSize = _fontSize + 2;
                _titleText.text     = _title;
            }

            if (_bodyText != null)
            {
                _bodyText.color    = _useCustomTextColor ? _customTextColor : s_CText[t];
                _bodyText.fontSize = _fontSize;
            }

            if (_pinButton != null)
            {
                var img = _pinButton.GetComponent<Image>();
                if (img != null) img.color = s_CHead[t];
            }
        }

        private void ApplyLayoutSettings()
        {
            if (_panel == null) return;

            float bodyContentHeight = _bodyText != null
                ? _bodyText.preferredHeight + (_cornerPadding * 2f)
                : 40f;

            float clampedBodyHeight = Mathf.Clamp(bodyContentHeight, 40f, _maxBodyHeight);
            _panel.sizeDelta = new Vector2(_width, HeaderHeight + clampedBodyHeight);
        }

        // ── Anchoring ─────────────────────────────────────────────────────────

        private void ApplyAnchor()
        {
            if (_panel != null) ApplyAnchorTo(_panel, _anchor, _margin);
        }

        private static void ApplyAnchorTo(RectTransform rt, NoteAnchor anchor, Vector2 margin)
        {
            switch (anchor)
            {
                case NoteAnchor.TopLeft:
                    rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
                    rt.pivot = new Vector2(0f, 1f);
                    rt.anchoredPosition = new Vector2(margin.x, -margin.y);
                    break;
                case NoteAnchor.TopRight:
                    rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
                    rt.pivot = new Vector2(1f, 1f);
                    rt.anchoredPosition = new Vector2(-margin.x, -margin.y);
                    break;
                case NoteAnchor.BottomLeft:
                    rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
                    rt.pivot = new Vector2(0f, 0f);
                    rt.anchoredPosition = new Vector2(margin.x, margin.y);
                    break;
                case NoteAnchor.BottomRight:
                    rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
                    rt.pivot = new Vector2(1f, 0f);
                    rt.anchoredPosition = new Vector2(-margin.x, margin.y);
                    break;
                case NoteAnchor.Center:
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = Vector2.zero;
                    break;
                case NoteAnchor.Free:
                    // Leave anchors/pivot/position exactly as the user left them after dragging.
                    break;
            }
        }

        // ── Body text ─────────────────────────────────────────────────────────

        private void RefreshBodyText()
        {
            _contentDirty = false;
            if (_bodyText == null) return;

            var sb = new StringBuilder();
            if (_notes != null)
                foreach (var n in _notes)
                    if (!string.IsNullOrEmpty(n)) sb.AppendLine(n);

            if (_textFile != null)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(_textFile.text);
            }

            _cachedBody    = sb.ToString().TrimEnd('\r', '\n');
            _bodyText.text = string.IsNullOrEmpty(_cachedBody) ? "(no notes)" : _cachedBody;

            if (_content != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(_content);

            ApplyLayoutSettings();
        }

        // ── Visibility / minimize ─────────────────────────────────────────────

        private void ApplyVisibilityState()
        {
            if (_panel != null)      _panel.gameObject.SetActive(_visible);
            if (_pinButton != null)  _pinButton.gameObject.SetActive(!_visible);
            if (_body != null)       _body.gameObject.SetActive(_visible && !_minimized);

            if (_visible && !_minimized) ApplyLayoutSettings();
        }

        // ── Public API ────────────────────────────────────────────────────────

        public bool IsVisible   => _visible;
        public bool IsMinimized => _minimized;

        public void Show()   { _visible = true;  ApplyVisibilityState(); }
        public void Hide()   { _visible = false; ApplyVisibilityState(); }
        public void Toggle() { if (_visible) Hide(); else Show(); }

        public void SetMinimized(bool minimized) { _minimized = minimized; ApplyVisibilityState(); }
        public void Minimize() => SetMinimized(true);
        public void Restore()  => SetMinimized(false);

        public void SetTitle(string title)
        {
            _title = title;
            if (_titleText != null) _titleText.text = title;
        }

        public void SetNotes(List<string> notes)
        {
            _notes = new List<string>(notes ?? new List<string>());
            _contentDirty = true;
        }

        public void AddNote(string note)
        {
            _notes ??= new List<string>();
            _notes.Add(note);
            _contentDirty = true;
        }

        public void ClearNotes()
        {
            _notes?.Clear();
            _contentDirty = true;
        }

        public void SetTextFile(TextAsset file)
        {
            _textFile = file;
            _contentDirty = true;
        }

        public void SetTextColor(Color c)
        {
            _useCustomTextColor = true;
            _customTextColor    = c;
            if (_bodyText != null) _bodyText.color = c;
        }

        public void UseThemeTextColor()
        {
            _useCustomTextColor = false;
            ApplyTheme();
        }

        public void ResetToAnchor() => ApplyAnchor();

#if UNITY_EDITOR
        public static Color GetHeaderColor(NoteTheme t) => s_CHead[(int)t];
        public static Color GetBodyColor(NoteTheme t)   => s_CBody[(int)t];
        public static Color GetTextColor(NoteTheme t)   => s_CText[(int)t];
#endif
    }

    // ── Drag handler ──────────────────────────────────────────────────────────
    // Lives on the header. Moves Target's anchoredPosition by the pointer delta,
    // compensated for Canvas scale factor so dragging feels 1:1 regardless of
    // CanvasScaler settings. Only active while EventSystem processes input
    // (Play Mode) — same as every other draggable UI element in Unity.

    [AddComponentMenu("")]
    public class MID_StickyNoteDragHandler : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public RectTransform Target;
        public Canvas        Canvas;
        public System.Action OnDragged;
        public System.Func<bool> IsEnabled;

        public void OnBeginDrag(PointerEventData eventData) { }

        public void OnDrag(PointerEventData eventData)
        {
            if (Target == null) return;
            if (IsEnabled != null && !IsEnabled()) return;

            float scale = Canvas != null && Canvas.scaleFactor > 0f ? Canvas.scaleFactor : 1f;
            Target.anchoredPosition += eventData.delta / scale;
            OnDragged?.Invoke();
        }

        public void OnEndDrag(PointerEventData eventData) { }
    }
}
