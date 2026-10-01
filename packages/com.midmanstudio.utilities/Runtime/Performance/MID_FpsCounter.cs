// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/performance.md, section "MID_FpsCounter.cs"
// ============================================================================
using System;
using System.Text;
using UnityEngine;

namespace MidManStudio.Core.Performance
{
    public enum MID_FpsCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    /// <summary>
    /// On-screen frame-rate readout. Drop it on any GameObject. Frame times
    /// come from Time.unscaledDeltaTime (real time, unaffected by timeScale),
    /// go into a rolling window, and the displayed numbers refresh a few
    /// times a second instead of every frame so they stay readable. Shows
    /// current/average FPS, frame time, and optionally best/worst frame and
    /// the 1% low. Draws with IMGUI so it needs no Canvas or font asset; use
    /// <see cref="TextUpdated"/> to drive your own UI text instead.
    /// </summary>
    [DisallowMultipleComponent]
    public class MID_FpsCounter : MonoBehaviour
    {
        [Header("Sampling")]
        [Tooltip("Frames kept in the rolling window used for average, best, worst and 1% low.")]
        [SerializeField, Min(10)] private int _sampleWindow = 300;
        [Tooltip("Seconds (real time) between readout refreshes.")]
        [SerializeField, Min(0.05f)] private float _refreshInterval = 0.5f;
        [Tooltip("Frames ignored after the counter starts, so scene-load hitches do not skew the window.")]
        [SerializeField, Min(0)] private int _warmupFrames = 10;

        [Header("Display")]
        [Tooltip("Draw the readout with IMGUI. Turn off if you only want the data or the TextUpdated event.")]
        [SerializeField] private bool _drawOverlay = true;
        [SerializeField] private bool _startVisible = true;
        [Tooltip("Key that shows or hides the overlay. None disables the key.")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.F3;
        [SerializeField] private MID_FpsCorner _corner = MID_FpsCorner.TopLeft;
        [SerializeField, Min(8)] private int _fontSize = 16;
        [Tooltip("Scale the font and margins up on high-DPI screens (phones) so the overlay stays readable.")]
        [SerializeField] private bool _autoDpiScale = true;
        [SerializeField] private bool _showFrameTime = true;
        [SerializeField] private bool _showBestWorst = true;
        [SerializeField] private bool _showOnePercentLow = true;
        [Tooltip("Respect the device safe area (notches, rounded corners).")]
        [SerializeField] private bool _useSafeArea = true;

        [Header("Colors")]
        [Tooltip("Target frame rate used to pick the color. 0 uses Application.targetFrameRate, or 60 if that is unset.")]
        [SerializeField, Min(0)] private int _targetFps = 0;
        [SerializeField] private Color _goodColor = new Color(0.35f, 1f, 0.45f, 1f);
        [SerializeField] private Color _warnColor = new Color(1f, 0.85f, 0.25f, 1f);
        [SerializeField] private Color _badColor  = new Color(1f, 0.35f, 0.3f, 1f);

        [Header("Build")]
        [Tooltip("Disable the counter in release builds (Debug.isDebugBuild is false).")]
        [SerializeField] private bool _developmentBuildOnly = false;

        private readonly StringBuilder _sb = new StringBuilder(128);
        private MID_FrameTimeStats _stats;
        private string _text = string.Empty;
        private Color _currentColor;
        private bool _visible;
        private float _nextRefresh;
        private int _framesSeen;
        private float _currentFrameMs;

        private GUIStyle _style;
        private int _styleFontSize = -1;
        private Vector2 _textSize;

        /// <summary>Raised each time the readout text is rebuilt.</summary>
        public event Action<string> TextUpdated;

        public bool  Visible        { get => _visible; set => _visible = value; }
        public float CurrentFps     => _currentFrameMs > 0f ? 1000f / _currentFrameMs : 0f;
        public float AverageFps     => _stats != null ? _stats.AverageFps : 0f;
        public float WorstFrameMs   => _stats != null ? _stats.WorstMs : 0f;
        public float OnePercentLowFps => _stats != null ? _stats.OnePercentLowFps : 0f;
        public string DisplayText   => _text;

        public void Toggle() => _visible = !_visible;

        /// <summary>Clears the window and restarts the warmup. Call after a scene load or a mode change.</summary>
        public void ResetStats()
        {
            if (_stats == null) _stats = new MID_FrameTimeStats(_sampleWindow);
            _stats.Clear();
            _framesSeen = 0;
            _nextRefresh = 0f;
            _currentFrameMs = 0f;
        }

        private void Awake()
        {
            if (_developmentBuildOnly && !Debug.isDebugBuild)
            {
                enabled = false;
                return;
            }
            _stats = new MID_FrameTimeStats(_sampleWindow);
            _visible = _startVisible;
        }

        private void OnEnable()
        {
            if (_stats != null) ResetStats();
        }

        private void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (_toggleKey != KeyCode.None && Input.GetKeyDown(_toggleKey)) _visible = !_visible;
#endif
            _framesSeen++;
            if (_framesSeen <= _warmupFrames) return;

            _currentFrameMs = Time.unscaledDeltaTime * 1000f;
            _stats.Add(_currentFrameMs);

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + _refreshInterval;
            Refresh();
        }

        private void Refresh()
        {
            _stats.Recalculate();
            if (_stats.Count == 0) return;

            float shownFps = _stats.AverageFps;
            _currentColor = PickColor(shownFps);

            _sb.Length = 0;
            _sb.Append(Mathf.RoundToInt(shownFps)).Append(" FPS");
            if (_showFrameTime)
                _sb.Append("  ").Append(_stats.AverageMs.ToString("F1")).Append(" ms");
            if (_showBestWorst)
            {
                _sb.Append("\nbest ").Append(Mathf.RoundToInt(_stats.BestFps))
                   .Append("  worst ").Append(Mathf.RoundToInt(_stats.WorstFps))
                   .Append(" (").Append(_stats.WorstMs.ToString("F1")).Append(" ms)");
            }
            if (_showOnePercentLow)
                _sb.Append("\n1% low ").Append(Mathf.RoundToInt(_stats.OnePercentLowFps));

            _text = _sb.ToString();
            _styleFontSize = -1; // force text size recompute
            TextUpdated?.Invoke(_text);
        }

        private Color PickColor(float fps)
        {
            float target = _targetFps > 0 ? _targetFps
                         : Application.targetFrameRate > 0 ? Application.targetFrameRate
                         : 60f;
            if (fps >= target * 0.9f) return _goodColor;
            if (fps >= target * 0.5f) return _warnColor;
            return _badColor;
        }

        private void OnGUI()
        {
            if (!_drawOverlay || !_visible || string.IsNullOrEmpty(_text)) return;
            if (Event.current.type != EventType.Repaint) return;

            float scale = 1f;
            if (_autoDpiScale && Screen.dpi > 0f)
                scale = Mathf.Clamp(Screen.dpi / 120f, 1f, 3f);

            int fontSize = Mathf.RoundToInt(_fontSize * scale);
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, richText = false };
                _style.wordWrap = false;
            }
            if (_styleFontSize != fontSize)
            {
                _style.fontSize = fontSize;
                _textSize = _style.CalcSize(new GUIContent(_text));
                _styleFontSize = fontSize;
            }

            float pad = 6f * scale;
            float margin = 8f * scale;
            Rect area = _useSafeArea ? Screen.safeArea : new Rect(0f, 0f, Screen.width, Screen.height);
            // Screen.safeArea is bottom-left origin in pixels, IMGUI is top-left.
            float areaTop = Screen.height - (area.y + area.height);

            float w = _textSize.x + pad * 2f;
            float h = _textSize.y + pad * 2f;
            float x = (_corner == MID_FpsCorner.TopLeft || _corner == MID_FpsCorner.BottomLeft)
                ? area.x + margin
                : area.x + area.width - w - margin;
            float y = (_corner == MID_FpsCorner.TopLeft || _corner == MID_FpsCorner.TopRight)
                ? areaTop + margin
                : areaTop + area.height - h - margin;

            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);

            _style.normal.textColor = _currentColor;
            GUI.color = Color.white;
            GUI.Label(new Rect(x + pad, y + pad, _textSize.x, _textSize.y), _text, _style);
            GUI.color = prev;
        }
    }
}
