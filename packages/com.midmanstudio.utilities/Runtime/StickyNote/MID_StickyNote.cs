// MID_StickyNote.cs
// Sticky note overlay component for the Game View.
// Attach to any GameObject in a scene to show instructions, setup notes,
// or tutorial content. Supports a text list, .txt file import, themes,
// drag-to-reposition, minimize, and close.
//
// Works in Play Mode (Game View) and in Edit Mode (Game View tab must be open).
// [ExecuteAlways] ensures OnGUI fires outside of play mode too.

using System.Collections.Generic;
using System.Text;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Notes
{
    [AddComponentMenu("MidManStudio/Utilities/Sticky Note")]
    [ExecuteAlways]
    public class MID_StickyNote : MonoBehaviour
    {
        // ── Enums ─────────────────────────────────────────────────────────────

        public enum NoteTheme { Yellow, Blue, Green, Pink, Dark }

        public enum NoteAnchor { TopLeft, TopRight, BottomLeft, BottomRight, Free }

        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Content")]
        [Tooltip("Title shown in the header bar.")]
        [SerializeField] private string _title = "Scene Notes";

        [Tooltip("Text entries shown in the note body. Each entry is a separate line.")]
        [MID_NamedList("")]
        [SerializeField] private List<string> _notes = new List<string>
        {
            "Welcome to this scene!",
            "• Step 1: Do something here.",
            "• Step 2: Then do this.",
        };

        [Tooltip("Optional .txt file. Content is appended after the notes list.")]
        [SerializeField] private TextAsset _textFile;

        [Header("Appearance")]
        [SerializeField] private NoteTheme _theme         = NoteTheme.Yellow;
        [SerializeField] [Range(9, 20)] private int _fontSize = 12;
        [SerializeField] [Range(160f, 600f)] private float _width    = 300f;
        [SerializeField] [Range(80f,  800f)] private float _maxBodyHeight = 380f;

        [Header("Position")]
        [Tooltip("Initial screen corner. Once dragged, the note is free-floating.")]
        [SerializeField] private NoteAnchor _anchor  = NoteAnchor.TopLeft;
        [SerializeField] private Vector2    _margin  = new Vector2(16f, 16f);
        [SerializeField] private bool       _draggable = true;

        [Header("Behaviour")]
        [SerializeField] private bool _startVisible  = true;
        [SerializeField] private bool _showInEditMode = true;

        // ── Theme palette ─────────────────────────────────────────────────────
        // Indices match NoteTheme enum: 0=Yellow 1=Blue 2=Green 3=Pink 4=Dark

        private static readonly Color[] s_CHead = {
            new Color(0.94f, 0.82f, 0.08f, 1f),   // Yellow
            new Color(0.18f, 0.42f, 0.80f, 1f),   // Blue
            new Color(0.14f, 0.60f, 0.22f, 1f),   // Green
            new Color(0.82f, 0.22f, 0.50f, 1f),   // Pink
            new Color(0.10f, 0.10f, 0.14f, 1f),   // Dark
        };

        private static readonly Color[] s_CBody = {
            new Color(0.99f, 0.96f, 0.57f, 0.96f),
            new Color(0.73f, 0.84f, 0.97f, 0.96f),
            new Color(0.72f, 0.94f, 0.73f, 0.96f),
            new Color(0.97f, 0.76f, 0.84f, 0.96f),
            new Color(0.17f, 0.17f, 0.21f, 0.97f),
        };

        private static readonly Color[] s_CText = {
            new Color(0.12f, 0.08f, 0.01f, 1f),
            new Color(0.06f, 0.09f, 0.22f, 1f),
            new Color(0.04f, 0.16f, 0.06f, 1f),
            new Color(0.20f, 0.04f, 0.10f, 1f),
            new Color(0.88f, 0.88f, 0.90f, 1f),
        };

        private static readonly Color s_CShadow = new Color(0f, 0f, 0f, 0.18f);

        // ── Runtime state ─────────────────────────────────────────────────────

        private bool    _visible;
        private bool    _minimized;
        private Rect    _rect;
        private Vector2 _scroll;
        private bool    _dirty      = true;
        private string  _cachedBody = "";
        private bool    _anchored   = true;  // true until first drag

        // Drag state
        private bool    _dragging;
        private Vector2 _dragStart;
        private Vector2 _rectStart;

        // Styles — built lazily inside OnGUI
        private GUIStyle _sTitle;
        private GUIStyle _sBody;
        private GUIStyle _sBtn;
        private GUIStyle _sTab;
        private bool     _stylesReady;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void OnEnable()
        {
            _visible      = _startVisible;
            _minimized    = false;
            _dirty        = true;
            _stylesReady  = false;
            _anchored     = true;
            ResetPosition();
        }

        private void OnValidate()
        {
            _dirty       = true;
            _stylesReady = false;
            if (_anchored) ResetPosition();
        }

        private void ResetPosition()
        {
            float sw = Screen.width  > 0 ? Screen.width  : 1920f;
            float sh = Screen.height > 0 ? Screen.height : 1080f;
            float x, y;

            switch (_anchor)
            {
                case NoteAnchor.TopRight:
                    x = sw - _width - _margin.x; y = _margin.y; break;
                case NoteAnchor.BottomLeft:
                    x = _margin.x; y = sh - _maxBodyHeight - 60f - _margin.y; break;
                case NoteAnchor.BottomRight:
                    x = sw - _width - _margin.x; y = sh - _maxBodyHeight - 60f - _margin.y; break;
                default: // TopLeft / Free
                    x = _margin.x; y = _margin.y; break;
            }

            _rect = new Rect(x, y, _width, 0f);
        }

        // ── Body text ─────────────────────────────────────────────────────────

        private void RebuildBody()
        {
            if (!_dirty) return;
            _dirty = false;

            var sb = new StringBuilder();
            if (_notes != null)
            {
                foreach (var n in _notes)
                    if (!string.IsNullOrEmpty(n)) sb.AppendLine(n);
            }

            if (_textFile != null)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(_textFile.text);
            }

            _cachedBody = sb.ToString().TrimEnd('\r', '\n');
        }

        // ── IMGUI ─────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            bool inPlay = Application.isPlaying;
            if (!inPlay && !_showInEditMode) return;

            EnsureStyles();
            RebuildBody();

            int t = (int)_theme;
            Event e = Event.current;

            // ── HIDDEN — show a small pin icon to reopen ──────────────────────
            if (!_visible)
            {
                float px = Mathf.Clamp(_rect.x, 0f, Screen.width  - 28f);
                float py = Mathf.Clamp(_rect.y, 0f, Screen.height - 28f);
                Rect pin = new Rect(px, py, 28f, 28f);
                FillRect(pin, s_CHead[t]);
                GUI.color = Color.white;
                if (GUI.Button(pin, "📌", _sBtn)) _visible = true;
                GUI.color = Color.white;
                return;
            }

            // ── MINIMIZED — show title bar only ───────────────────────────────
            if (_minimized)
            {
                float tx = Mathf.Clamp(_rect.x, 0f, Screen.width  - _width);
                float ty = Mathf.Clamp(_rect.y, 0f, Screen.height - 28f);
                Rect  tb = new Rect(tx, ty, _width, 28f);
                FillRect(new Rect(tx + 3f, ty + 3f, _width, 28f), s_CShadow);
                FillRect(tb, s_CHead[t]);
                GUI.contentColor = Color.white;
                GUI.Label(new Rect(tx + 8f, ty + 5f, _width - 50f, 18f),
                    $"📌 {_title}", _sTab);
                if (GUI.Button(new Rect(tb.xMax - 23f, ty + 4f, 20f, 20f), "▲", _sBtn))
                    _minimized = false;
                GUI.contentColor = Color.white;

                // Drag minimized bar
                if (_draggable)
                {
                    if (e.type == EventType.MouseDown && tb.Contains(e.mousePosition))
                    { _dragging = true; _dragStart = e.mousePosition; _rectStart = new Vector2(tx, ty); e.Use(); }
                }
                if (e.type == EventType.MouseUp) _dragging = false;
                if (_dragging && e.type == EventType.MouseDrag)
                { _rect.position = _rectStart + ((Vector2)e.mousePosition - _dragStart); e.Use(); }
                return;
            }

            // ── FULL NOTE ─────────────────────────────────────────────────────

            // Calculate heights
            var content   = new GUIContent(_cachedBody);
            float innerW  = _width - 12f;
            float textH   = string.IsNullOrEmpty(_cachedBody)
                ? _sBody.lineHeight
                : _sBody.CalcHeight(content, innerW);
            bool  doScroll = textH > _maxBodyHeight;
            float bodyH    = doScroll ? _maxBodyHeight : textH + 8f;
            float totalH   = 28f + bodyH + 8f;

            _rect.width  = _width;
            _rect.height = totalH;
            _rect.x      = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width  - _width));
            _rect.y      = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - totalH));

            Rect header  = new Rect(_rect.x, _rect.y, _width, 28f);
            Rect bodyBg  = new Rect(_rect.x, _rect.y + 28f, _width, bodyH + 8f);

            // Shadow
            FillRect(new Rect(_rect.x + 4f, _rect.y + 4f, _width, totalH), s_CShadow);

            // Header
            FillRect(header, s_CHead[t]);
            GUI.contentColor = Color.white;
            GUI.Label(new Rect(header.x + 8f, header.y + 5f, _width - 54f, 18f),
                _title, _sTitle);

            // Minimize / Close
            if (GUI.Button(new Rect(header.xMax - 46f, header.y + 4f, 20f, 20f), "–", _sBtn))
                _minimized = true;
            if (GUI.Button(new Rect(header.xMax - 23f, header.y + 4f, 20f, 20f), "✕", _sBtn))
                _visible = false;

            GUI.contentColor = Color.white;

            // Body background
            FillRect(bodyBg, s_CBody[t]);

            // Scrollable body content
            Rect scrollView   = new Rect(_rect.x + 4f, _rect.y + 32f, _width - 8f, bodyH);
            Rect scrollContent = new Rect(0f, 0f, innerW - (doScroll ? 16f : 0f), textH + 8f);

            _scroll = GUI.BeginScrollView(scrollView, _scroll, scrollContent, false, doScroll);
            GUI.contentColor = s_CText[t];
            GUI.Label(new Rect(4f, 4f, scrollContent.width - 4f, textH), content, _sBody);
            GUI.contentColor = Color.white;
            GUI.EndScrollView();

            // ── Drag header ───────────────────────────────────────────────────

            if (_draggable)
            {
                Rect dragZone = new Rect(header.x, header.y, _width - 50f, 28f);
                if (e.type == EventType.MouseDown && dragZone.Contains(e.mousePosition))
                {
                    _dragging   = true;
                    _anchored   = false;
                    _dragStart  = e.mousePosition;
                    _rectStart  = _rect.position;
                    e.Use();
                }
            }

            if (e.type == EventType.MouseUp)   _dragging = false;
            if (_dragging && e.type == EventType.MouseDrag)
            {
                _rect.position = _rectStart + ((Vector2)e.mousePosition - _dragStart);
                e.Use();
            }
        }

        // ── Style builder ─────────────────────────────────────────────────────

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;
            int sz = Mathf.Max(9, _fontSize);

            _sTitle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = sz + 1,
                fontStyle = FontStyle.Bold,
            };
            _sTitle.normal.textColor = Color.white;

            _sBody = new GUIStyle(GUI.skin.label)
            {
                fontSize = sz,
                wordWrap = true,
                richText = true,
            };
            _sBody.normal.textColor = Color.black;

            _sBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize = sz,
                padding  = new RectOffset(2, 2, 1, 1),
            };

            _sTab = new GUIStyle(GUI.skin.label)
            {
                fontSize  = sz,
                fontStyle = FontStyle.Bold,
            };
            _sTab.normal.textColor = Color.white;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void FillRect(Rect r, Color c)
        {
            Color prev = GUI.color;
            GUI.color  = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color  = prev;
        }

        // ── Public API ────────────────────────────────────────────────────────

        public bool IsVisible   => _visible;
        public bool IsMinimized => _minimized;

        public void Show()     { _visible = true;  _minimized = false; }
        public void Hide()     { _visible = false; }
        public void Toggle()   { _visible = !_visible; }
        public void Minimize() { _minimized = true; }
        public void Restore()  { _minimized = false; }

        public void SetTitle(string title)
        {
            _title = title;
        }

        public void SetNotes(List<string> notes)
        {
            _notes = new List<string>(notes ?? new List<string>());
            _dirty = true;
        }

        public void AddNote(string note)
        {
            _notes ??= new List<string>();
            _notes.Add(note);
            _dirty = true;
        }

        public void ClearNotes()
        {
            _notes?.Clear();
            _dirty = true;
        }

        public void SetTextFile(TextAsset file)
        {
            _textFile = file;
            _dirty    = true;
        }

        // Editor-only: expose theme data so the custom inspector can preview colours
#if UNITY_EDITOR
        public static Color GetHeaderColor(NoteTheme t) => s_CHead[(int)t];
        public static Color GetBodyColor(NoteTheme t)   => s_CBody[(int)t];
        public static Color GetTextColor(NoteTheme t)   => s_CText[(int)t];
#endif
    }
}
