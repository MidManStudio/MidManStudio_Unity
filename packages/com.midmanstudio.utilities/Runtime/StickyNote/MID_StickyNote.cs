// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/stickynote.md, section "MID_StickyNote.cs"
// ============================================================================
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
    /// <summary>
    /// A scene note drawn as an overlay in the Game View. The component builds its own
    /// Screen Space Overlay canvas as a child of its GameObject: a header with a title,
    /// minimize and close buttons, a scrolling body, and a pin button that brings the note
    /// back after it was closed. The canvas uses <c>HideFlags.DontSave</c>, so it is not
    /// written into the scene file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The note is built in Play Mode, and in Edit Mode when <c>Build in Edit Mode</c> is on
    /// (the default) and the GameObject is in a loaded scene. Position, theme, size and text
    /// update live in Edit Mode.
    /// </para>
    /// <para>
    /// Dragging the header, minimize and close are ordinary UGUI event handlers, so they need
    /// the EventSystem to process input and are intended for Play Mode only. The Show, Hide
    /// and Minimize methods do not check the play state.
    /// </para>
    /// <para>
    /// If no EventSystem exists the note creates one (with a <c>StandaloneInputModule</c>) and
    /// removes it again once the notes that hold it are gone. An EventSystem you placed
    /// yourself is never touched. If the project uses only the Input System package, place
    /// your own EventSystem with the matching input module before the note builds.
    /// </para>
    /// <para>
    /// Visibility and minimize state are runtime state. <c>OnEnable</c> resets them to
    /// <c>Visible on Start</c> and not minimized. The component is not stripped from player
    /// builds, so disable or remove it before shipping.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var note = GetComponent&lt;MID_StickyNote&gt;();
    /// note.SetTitle("Controls");
    /// note.SetNotes(new List&lt;string&gt; { "WASD to move", "F to fire" });
    /// note.Minimize();
    /// </code>
    /// </example>
    [AddComponentMenu("MidManStudio/Utilities/Sticky Note")]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class MID_StickyNote : MonoBehaviour
    {
        /// <summary>Colour set for the note: Yellow, Blue, Green, Pink or Dark. The enum value indexes the four private palette arrays, so a new theme needs an entry in each.</summary>
        public enum NoteTheme  { Yellow, Blue, Green, Pink, Dark }

        /// <summary>
        /// Where the panel is placed. The four corners put the panel pivot on that corner and offset it
        /// by the margin. <c>Center</c> ignores the margin. <c>Free</c> applies nothing: anchors, pivot and
        /// position stay as they are, and it is set automatically when the header is dragged.
        /// </summary>
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
                // Edit Mode builds one editor tick later. OnEnable can run inside
                // Unity's own message pass (domain reload, scene load, prefab stage),
                // where AddComponent logs "SendMessage cannot be called during Awake,
                // CheckConsistency, or OnValidate". Play Mode builds synchronously.
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

            ApplyAnchor();          // anchors must be a single point before sizing, or sizeDelta is added to the parent's size
            ApplyTheme();
            ApplyLayoutSettings();
            RefreshBodyText();
            ApplyVisibilityState();

            _built = true;
        }

        /// <summary>
        /// Destroys and rebuilds the whole canvas hierarchy. Used by the inspector's Rebuild button.
        /// It does not check whether the note should be built, so with <c>Build in Edit Mode</c> off
        /// the next <c>Update</c> in Edit Mode removes the result again.
        /// </summary>
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

        // An existing EventSystem is reused and never touched. If the project uses
        // only the Input System package, place your own EventSystem with an
        // InputSystemUIInputModule in the scene first: the one created here uses
        // StandaloneInputModule. A created EventSystem is reference counted. A note
        // holds a reference when it creates the EventSystem, or when it finds the
        // owned one while EventSystem.current is null. The EventSystem is destroyed
        // when the last holder releases it.
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

            // Placed once, at build time. Later anchor or margin changes move the
            // panel (OnValidate) but not the pin button until the next rebuild.
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

            // Panel height = header + (preferred text height + 2 x padding), with the
            // body part clamped to 40..Max Body Height. The padding only adds height:
            // the text starts at the top of the viewport, so the extra space sits below it.
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

        /// <summary>True while the note panel is shown. False while it is closed and only the pin button is visible.</summary>
        public bool IsVisible   => _visible;

        /// <summary>True while the body is collapsed and only the header is shown.</summary>
        public bool IsMinimized => _minimized;

        /// <summary>Shows the panel and hides the pin button.</summary>
        public void Show()   { _visible = true;  ApplyVisibilityState(); }

        /// <summary>Hides the panel and shows the pin button. The hierarchy is kept, not destroyed.</summary>
        public void Hide()   { _visible = false; ApplyVisibilityState(); }

        /// <summary>Calls <see cref="Hide"/> when visible, otherwise <see cref="Show"/>.</summary>
        public void Toggle() { if (_visible) Hide(); else Show(); }

        /// <summary>Collapses the body (true) or expands it (false). The panel keeps its full height, only the header stays visible.</summary>
        public void SetMinimized(bool minimized) { _minimized = minimized; ApplyVisibilityState(); }

        /// <summary>Same as <c>SetMinimized(true)</c>.</summary>
        public void Minimize() => SetMinimized(true);

        /// <summary>Same as <c>SetMinimized(false)</c>.</summary>
        public void Restore()  => SetMinimized(false);

        /// <summary>Sets the header text immediately and stores it as the new title.</summary>
        public void SetTitle(string title)
        {
            _title = title;
            if (_titleText != null) _titleText.text = title;
        }

        /// <summary>Replaces the note list with a copy of <paramref name="notes"/> (null gives an empty list). The text refreshes on the next <c>Update</c>.</summary>
        public void SetNotes(List<string> notes)
        {
            _notes = new List<string>(notes ?? new List<string>());
            _contentDirty = true;
        }

        /// <summary>Appends one entry to the note list. The text refreshes on the next <c>Update</c>.</summary>
        public void AddNote(string note)
        {
            _notes ??= new List<string>();
            _notes.Add(note);
            _contentDirty = true;
        }

        /// <summary>Removes all entries from the note list. An assigned text file is kept.</summary>
        public void ClearNotes()
        {
            _notes?.Clear();
            _contentDirty = true;
        }

        /// <summary>Sets the optional text file whose content is appended after the note list. Null removes it.</summary>
        public void SetTextFile(TextAsset file)
        {
            _textFile = file;
            _contentDirty = true;
        }

        /// <summary>Overrides the theme's body text colour. It stays in effect until <see cref="UseThemeTextColor"/> is called.</summary>
        public void SetTextColor(Color c)
        {
            _useCustomTextColor = true;
            _customTextColor    = c;
            if (_bodyText != null) _bodyText.color = c;
        }

        /// <summary>Drops the custom text colour and uses the theme's body text colour again.</summary>
        public void UseThemeTextColor()
        {
            _useCustomTextColor = false;
            ApplyTheme();
        }

        /// <summary>
        /// Re-applies the configured anchor and margin to the panel. Does nothing while the anchor
        /// is <see cref="NoteAnchor.Free"/>, which includes after the note has been dragged.
        /// </summary>
        public void ResetToAnchor() => ApplyAnchor();

#if UNITY_EDITOR
        /// <summary>Editor only. Header colour of a theme, used by the inspector preview.</summary>
        public static Color GetHeaderColor(NoteTheme t) => s_CHead[(int)t];

        /// <summary>Editor only. Body colour of a theme, used by the inspector preview.</summary>
        public static Color GetBodyColor(NoteTheme t)   => s_CBody[(int)t];

        /// <summary>Editor only. Default body text colour of a theme, used by the inspector preview.</summary>
        public static Color GetTextColor(NoteTheme t)   => s_CText[(int)t];
#endif
    }

    // ── Drag handler ──────────────────────────────────────────────────────────

    /// <summary>
    /// Added to the note's header at build time. Moves <see cref="Target"/> by the pointer delta,
    /// divided by the canvas scale factor so the drag follows the pointer 1:1. It receives events
    /// only while the EventSystem processes input, which is Play Mode. It does not clamp the
    /// position to the screen. Not meant to be added by hand (hidden from the Add Component menu).
    /// </summary>
    [AddComponentMenu("")]
    public class MID_StickyNoteDragHandler : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        /// <summary>The panel that is moved.</summary>
        public RectTransform Target;

        /// <summary>The canvas whose scale factor is used to convert the pointer delta. A missing canvas or a scale factor of 0 or less counts as 1.</summary>
        public Canvas        Canvas;

        /// <summary>Invoked after every drag step. The note uses it to switch its anchor to <see cref="MID_StickyNote.NoteAnchor.Free"/>.</summary>
        public System.Action OnDragged;

        /// <summary>Optional gate. When set and it returns false, dragging is ignored. The note passes its Draggable setting.</summary>
        public System.Func<bool> IsEnabled;

        /// <summary>Required by <see cref="IBeginDragHandler"/>. Does nothing.</summary>
        public void OnBeginDrag(PointerEventData eventData) { }

        /// <summary>Moves <see cref="Target"/> by the pointer delta and invokes <see cref="OnDragged"/>.</summary>
        public void OnDrag(PointerEventData eventData)
        {
            if (Target == null) return;
            if (IsEnabled != null && !IsEnabled()) return;

            float scale = Canvas != null && Canvas.scaleFactor > 0f ? Canvas.scaleFactor : 1f;
            Target.anchoredPosition += eventData.delta / scale;
            OnDragged?.Invoke();
        }

        /// <summary>Required by <see cref="IEndDragHandler"/>. Does nothing.</summary>
        public void OnEndDrag(PointerEventData eventData) { }
    }
}
