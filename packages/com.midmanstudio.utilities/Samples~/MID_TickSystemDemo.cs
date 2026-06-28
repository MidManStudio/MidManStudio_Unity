// Visual in-Game-View dashboard demonstrating the Tick Dispatcher, Tick Delay,
// and Timer systems. Attach to any GameObject in a test scene.
//
// WHAT IT SHOWS:
//   Panel A — Tick Rates   : LED indicators for each rate showing fire count
//                            and time-since-last-fire. The LED pulses bright
//                            for ~0.15s each time its rate fires.
//   Panel B — Timers       : CountdownTimer, StopwatchTimer, and
//                            ValueInterpolationTimer with progress bars.
//   Panel C — Tick Delays  : A zero-alloc repeating TickDelay that fires
//                            every 2 seconds (infinite) and a one-shot 5s delay.
//
// REQUIREMENTS:
//   MID_TickDispatcher must be present in the scene (auto-creates if missing).

using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.TickDispatcher;
using MidManStudio.Core.Timers;

namespace MidManStudio.Core.Demos
{
    [AddComponentMenu("MidManStudio/Utilities/Demos/Tick System Demo")]
    public class MID_TickSystemDemo : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Layout")]
        [SerializeField] private Vector2 _startPos    = new Vector2(16f, 16f);
        [SerializeField] private float   _panelWidth  = 420f;
        [SerializeField] private int     _fontSize    = 11;

        [Header("Demo Params")]
        [SerializeField] private float _countdownDuration  = 15f;
        [SerializeField] private float _repeatDelaySeconds = 2f;
        [SerializeField] private float _oneshotDelaySeconds = 5f;

        // ── Tick rate tracking ────────────────────────────────────────────────

        private struct RateEntry
        {
            public TickRate Rate;
            public string   Label;
            public int      Count;
            public float    LastFireTime;
            public float    PulseFade;  // 1=just fired, 0=idle
        }

        private readonly List<RateEntry> _rates = new()
        {
            new RateEntry { Rate = TickRate.Tick_0_1,  Label = "Tick_0_1   (10/sec)" },
            new RateEntry { Rate = TickRate.Tick_0_2,  Label = "Tick_0_2    (5/sec)" },
            new RateEntry { Rate = TickRate.Tick_0_5,  Label = "Tick_0_5    (2/sec)" },
            new RateEntry { Rate = TickRate.Tick_1,    Label = "Tick_1      (1/sec)" },
            new RateEntry { Rate = TickRate.Tick_2,    Label = "Tick_2   (0.5/sec)" },
        };

        // Callbacks — one per rate, stored to avoid GC alloc on subscribe/unsubscribe
        private MID_TickDispatcher.TickCallback[] _callbacks;

        // ── Timers ────────────────────────────────────────────────────────────

        private CountdownTimer           _countdown;
        private StopwatchTimer           _stopwatch;
        private ValueInterpolationTimer  _interpLinear;
        private ValueInterpolationTimer  _interpEased;

        // ── Tick Delay tracking ───────────────────────────────────────────────

        private int   _repeatFireCount;
        private float _repeatNextFire;
        private bool  _oneshotFired;
        private float _oneshotFireTime;

        // Pre-allocated delegates — MUST be static readonly for zero-alloc contract
        private static Action _onRepeatFire;
        private static Action _onOneshotFire;
        private static MID_TickSystemDemo _instance; // for static delegates to reference

        private TickDelayHandle _repeatHandle;
        private TickDelayHandle _oneshotHandle;

        // ── Styles ───────────────────────────────────────────────────────────

        private GUIStyle _sPanel, _sTitle, _sLabel, _sBold, _sSmall, _sBtn;
        private bool     _stylesBuilt;

        // Panel visibility
        private bool _showRates   = true;
        private bool _showTimers  = true;
        private bool _showDelays  = true;
        private bool _demoVisible = true;

        // ── Colours ───────────────────────────────────────────────────────────

        private static readonly Color ColBg      = new Color(0.08f, 0.08f, 0.10f, 0.93f);
        private static readonly Color ColHeader  = new Color(0.13f, 0.13f, 0.17f, 1.00f);
        private static readonly Color ColBorder  = new Color(0.25f, 0.25f, 0.30f, 1.00f);
        private static readonly Color ColGreen   = new Color(0.22f, 0.92f, 0.38f, 1.00f);
        private static readonly Color ColOrange  = new Color(1.00f, 0.60f, 0.10f, 1.00f);
        private static readonly Color ColBlue    = new Color(0.30f, 0.60f, 1.00f, 1.00f);
        private static readonly Color ColYellow  = new Color(1.00f, 0.90f, 0.20f, 1.00f);
        private static readonly Color ColRed     = new Color(1.00f, 0.28f, 0.28f, 1.00f);
        private static readonly Color ColDim     = new Color(0.55f, 0.55f, 0.60f, 1.00f);
        private static readonly Color ColWhite   = new Color(0.90f, 0.90f, 0.92f, 1.00f);

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void OnEnable()
        {
            // Static delegate setup (zero-alloc contract)
            _instance      = this;
            _onRepeatFire  ??= OnRepeatFireStatic;
            _onOneshotFire ??= OnOneshotFireStatic;

            // Subscribe to all tracked tick rates
            _callbacks = new MID_TickDispatcher.TickCallback[_rates.Count];
            for (int i = 0; i < _rates.Count; i++)
            {
                int captured = i;
                // We create one closure per rate only once, at subscribe time.
                // For true zero-alloc in hot code, callers should pre-allocate
                // static delegates. Here we allocate once per enable which is
                // acceptable for a demo/dev tool.
                var rate = _rates[captured].Rate;
                _callbacks[captured] = (float dt) => OnRateTick(captured, dt);
                MID_TickDispatcher.Subscribe(rate, _callbacks[captured]);
            }

            // Timers
            _countdown   = new CountdownTimer(_countdownDuration);
            _stopwatch   = new StopwatchTimer();
            _interpLinear = new ValueInterpolationTimer(0f, 1f, 3f, InterpolationMode.Linear);
            _interpEased  = new ValueInterpolationTimer(0f, 1f, 3f, InterpolationMode.EaseInOut);

            _countdown.OnTimerComplete   += OnCountdownComplete;
            _interpLinear.OnInterpolationComplete += RestartLinear;
            _interpEased.OnInterpolationComplete  += RestartEased;

            _countdown.Start();
            _stopwatch.Start();
            _interpLinear.Start();
            _interpEased.Start();

            // TickDelay
            _repeatFireCount = 0;
            _oneshotFired    = false;
            _repeatNextFire  = Time.time + _repeatDelaySeconds;

            _repeatHandle  = MID_TickDelay.RepeatForever(
                _repeatDelaySeconds, _onRepeatFire, TickRate.Tick_0_2);
            _oneshotHandle = MID_TickDelay.After(
                _oneshotDelaySeconds, _onOneshotFire, TickRate.Tick_0_2);
        }

        private void OnDisable()
        {
            // Unsubscribe all tick callbacks
            if (_callbacks != null)
                for (int i = 0; i < _rates.Count; i++)
                    MID_TickDispatcher.Unsubscribe(_rates[i].Rate, _callbacks[i]);

            _callbacks = null;

            // Cancel pending delays
            _repeatHandle.Cancel();
            _oneshotHandle.Cancel();

            // Clean up timer events
            if (_countdown  != null) _countdown.OnTimerComplete -= OnCountdownComplete;
            if (_interpLinear != null) _interpLinear.OnInterpolationComplete -= RestartLinear;
            if (_interpEased  != null) _interpEased.OnInterpolationComplete  -= RestartEased;

            if (_instance == this) _instance = null;
        }

        // ── Per-frame tick & timer update ─────────────────────────────────────

        private void Update()
        {
            float dt = Time.deltaTime;

            // Update timer pulse fades per rate
            for (int i = 0; i < _rates.Count; i++)
            {
                var e = _rates[i];
                e.PulseFade = Mathf.Max(0f, e.PulseFade - dt / 0.15f);
                _rates[i]   = e;
            }

            // Tick timers
            _countdown?.Tick(dt);
            _stopwatch?.Tick(dt);
            _interpLinear?.Tick(dt);
            _interpEased?.Tick(dt);
        }

        // ── Tick callbacks ────────────────────────────────────────────────────

        private void OnRateTick(int index, float dt)
        {
            var e = _rates[index];
            e.Count++;
            e.LastFireTime = Time.time;
            e.PulseFade    = 1f;
            _rates[index]  = e;
        }

        // ── Timer callbacks ───────────────────────────────────────────────────

        private void OnCountdownComplete()
        {
            _countdown?.Reset(_countdownDuration);
            _countdown?.Start();
        }

        private void RestartLinear()
        {
            _interpLinear?.Reset();
            _interpLinear?.Start();
        }

        private void RestartEased()
        {
            _interpEased?.Reset();
            _interpEased?.Start();
        }

        // ── TickDelay callbacks (static for zero-alloc) ───────────────────────

        private static void OnRepeatFireStatic()
        {
            if (_instance == null) return;
            _instance._repeatFireCount++;
            _instance._repeatNextFire = Time.time + _instance._repeatDelaySeconds;
        }

        private static void OnOneshotFireStatic()
        {
            if (_instance == null) return;
            _instance._oneshotFired    = true;
            _instance._oneshotFireTime = Time.time;
        }

        // ── IMGUI Dashboard ───────────────────────────────────────────────────

        private void OnGUI()
        {
            if (!Application.isPlaying) return;

            EnsureStyles();

            float x = _startPos.x;
            float y = _startPos.y;

            // ── Toggle button ─────────────────────────────────────────────────
            Rect toggleBtn = new Rect(x, y, 140f, 22f);
            FillRect(toggleBtn, ColHeader);
            FillRect(new Rect(toggleBtn.x, toggleBtn.yMax, toggleBtn.width, 1f), ColBorder);
            GUI.contentColor = ColWhite;
            if (GUI.Button(toggleBtn, $"⚡ Tick Demo  {(_demoVisible ? "▾" : "▸")}", _sBtn))
                _demoVisible = !_demoVisible;
            GUI.contentColor = Color.white;

            if (!_demoVisible) return;

            y += 26f;

            y = DrawTickRatesPanel(x, y);
            y += 6f;
            y = DrawTimersPanel(x, y);
            y += 6f;
            y = DrawDelaysPanel(x, y);
        }

        // ── Panel: Tick Rates ─────────────────────────────────────────────────

        private float DrawTickRatesPanel(float x, float y)
        {
            float rowH  = 22f;
            float hdrH  = 24f;
            float padV  = 6f;
            float totalH = hdrH + (_rates.Count * rowH) + padV * 2f;

            DrawPanelBg(x, y, _panelWidth, totalH);

            // Header
            DrawPanelHeader(x, y, _panelWidth, "⏱  TICK RATES", ref _showRates);
            y += hdrH;

            if (!_showRates) return y + 4f;

            y += padV;

            float col0 = x + 10f;              // LED
            float col1 = x + 32f;              // Name
            float col2 = x + _panelWidth * 0.56f; // Count
            float col3 = x + _panelWidth * 0.76f; // Since

            // Column headers
            GUI.contentColor = ColDim;
            GUI.Label(new Rect(col1, y, 120f, 14f), "Rate",  _sSmall);
            GUI.Label(new Rect(col2, y, 60f,  14f), "Fires", _sSmall);
            GUI.Label(new Rect(col3, y, 80f,  14f), "Last",  _sSmall);
            GUI.contentColor = Color.white;
            y += 14f;

            for (int i = 0; i < _rates.Count; i++)
            {
                var e = _rates[i];

                // LED circle
                float pulse = e.PulseFade;
                Color ledCol = Color.Lerp(
                    new Color(0.15f, 0.45f, 0.18f, 1f),  // idle
                    ColGreen,                              // fired
                    pulse);
                FillRect(new Rect(col0, y + 3f, 14f, 14f), new Color(0,0,0,0.4f));
                FillRect(new Rect(col0 + 1f, y + 4f, 12f, 12f), ledCol);

                // Rate name
                GUI.contentColor = pulse > 0.05f ? ColGreen : ColWhite;
                GUI.Label(new Rect(col1, y + 2f, 180f, 18f), e.Label, _sLabel);

                // Fire count
                GUI.contentColor = ColOrange;
                GUI.Label(new Rect(col2, y + 2f, 60f, 18f),
                    e.Count.ToString("N0"), _sBold);

                // Time since last fire
                float since = e.LastFireTime > 0f ? Time.time - e.LastFireTime : -1f;
                GUI.contentColor = since >= 0f ? ColDim : new Color(0.3f, 0.3f, 0.35f);
                GUI.Label(new Rect(col3, y + 2f, 80f, 18f),
                    since >= 0f ? $"{since:F2}s" : "—", _sLabel);

                GUI.contentColor = Color.white;
                y += rowH;
            }

            return y + padV;
        }

        // ── Panel: Timers ─────────────────────────────────────────────────────

        private float DrawTimersPanel(float x, float y)
        {
            float hdrH  = 24f;
            float rowH  = 36f;
            float padV  = 8f;
            float totalH = hdrH + (3 * rowH) + padV * 2f;

            DrawPanelBg(x, y, _panelWidth, totalH);
            DrawPanelHeader(x, y, _panelWidth, "⏲  TIMERS", ref _showTimers);
            y += hdrH + padV;

            if (!_showTimers) return y + 4f;

            float barW = _panelWidth - 20f;

            // Countdown Timer
            if (_countdown != null)
            {
                float prog = 1f - _countdown.Progress;
                float rem  = _countdown.IsFinished ? 0f
                    : _countdownDuration * (1f - _countdown.Progress);
                DrawTimerRow(x + 10f, y, barW,
                    $"CountdownTimer   {rem:F1}s / {_countdownDuration:F0}s",
                    prog, ColRed, ColOrange);
                y += rowH;
            }

            // Stopwatch Timer
            if (_stopwatch != null)
            {
                float elapsed = _stopwatch.GetTime();
                float swProg  = (elapsed % 10f) / 10f;  // cycles every 10s for visual
                DrawTimerRow(x + 10f, y, barW,
                    $"StopwatchTimer   {elapsed:F1}s  (bar = mod 10s)",
                    swProg, ColBlue, new Color(0.5f, 0.8f, 1f));
                y += rowH;
            }

            // Two interpolation timers side by side
            float halfW = (barW - 6f) * 0.5f;
            if (_interpLinear != null)
            {
                DrawTimerRow(x + 10f, y, halfW,
                    $"Linear  {_interpLinear.CurrentValue:F2}",
                    _interpLinear.Progress, ColGreen,
                    new Color(0.4f, 1f, 0.5f));
            }
            if (_interpEased != null)
            {
                DrawTimerRow(x + 10f + halfW + 6f, y, halfW,
                    $"EaseInOut  {_interpEased.CurrentValue:F2}",
                    _interpEased.Progress, ColYellow,
                    new Color(1f, 0.95f, 0.5f));
            }
            y += rowH;

            return y + padV;
        }

        private void DrawTimerRow(float x, float y, float w,
            string label, float progress, Color barColor, Color textColor)
        {
            // Label
            GUI.contentColor = textColor;
            GUI.Label(new Rect(x, y, w, 16f), label, _sSmall);
            GUI.contentColor = Color.white;

            // Bar background
            Rect bgRect = new Rect(x, y + 16f, w, 10f);
            FillRect(bgRect, new Color(0.06f, 0.06f, 0.08f, 1f));

            // Bar fill
            float fillW = Mathf.Clamp01(progress) * w;
            if (fillW > 0f)
            {
                FillRect(new Rect(bgRect.x, bgRect.y, fillW, bgRect.height), barColor);
                // Bright leading edge
                FillRect(new Rect(bgRect.x + fillW - 2f, bgRect.y, 2f, bgRect.height),
                    Color.Lerp(barColor, Color.white, 0.6f));
            }

            // Border
            DrawRectOutline(bgRect, ColBorder);
        }

        // ── Panel: Tick Delays ────────────────────────────────────────────────

        private float DrawDelaysPanel(float x, float y)
        {
            float hdrH  = 24f;
            float padV  = 8f;
            float totalH = hdrH + 70f + padV * 2f;

            DrawPanelBg(x, y, _panelWidth, totalH);
            DrawPanelHeader(x, y, _panelWidth, "⏰  TICK DELAY  (zero-alloc)", ref _showDelays);
            y += hdrH + padV;

            if (!_showDelays) return y + 4f;

            float cx = x + 10f;
            float cw = _panelWidth - 20f;

            // Repeating delay
            float timeUntilNext = Mathf.Max(0f, _repeatNextFire - Time.time);
            float repeatProg    = _repeatDelaySeconds > 0f
                ? 1f - (timeUntilNext / _repeatDelaySeconds) : 0f;

            GUI.contentColor = ColOrange;
            GUI.Label(new Rect(cx, y, cw * 0.5f, 16f),
                "RepeatForever every 2s", _sBold);
            GUI.contentColor = ColWhite;
            GUI.Label(new Rect(cx + cw * 0.5f, y, cw * 0.5f, 16f),
                $"Fired  {_repeatFireCount}×", _sLabel);
            GUI.contentColor = Color.white;

            y += 16f;
            Rect repeatBar = new Rect(cx, y, cw, 10f);
            FillRect(repeatBar, new Color(0.06f, 0.06f, 0.08f));
            float rfW = Mathf.Clamp01(repeatProg) * cw;
            if (rfW > 0) FillRect(new Rect(cx, y, rfW, 10f), ColOrange);
            DrawRectOutline(repeatBar, ColBorder);

            GUI.contentColor = ColDim;
            GUI.Label(new Rect(cx, y + 11f, cw, 12f),
                $"Next fire in {timeUntilNext:F1}s   " +
                $"(TickRate.Tick_0_2, static readonly Action)", _sSmall);
            GUI.contentColor = Color.white;

            y += 30f;

            // One-shot delay
            GUI.contentColor = _oneshotFired ? ColGreen : ColBlue;
            GUI.Label(new Rect(cx, y, cw * 0.6f, 16f),
                $"After({_oneshotDelaySeconds:F0}s)  — one-shot", _sBold);

            GUI.contentColor = _oneshotFired ? ColGreen : ColDim;
            string oneshotStatus = _oneshotFired
                ? $"✓  Fired at {_oneshotFireTime:F1}s"
                : $"⏳ Waiting…";
            GUI.Label(new Rect(cx + cw * 0.6f, y, cw * 0.4f, 16f),
                oneshotStatus, _sBold);
            GUI.contentColor = Color.white;

            y += 18f;
            return y + padV;
        }

        // ── Panel draw helpers ────────────────────────────────────────────────

        private void DrawPanelBg(float x, float y, float w, float h)
        {
            // Shadow
            FillRect(new Rect(x + 3f, y + 3f, w, h), new Color(0, 0, 0, 0.25f));
            // Background
            FillRect(new Rect(x, y, w, h), ColBg);
            // Border
            DrawRectOutline(new Rect(x, y, w, h), ColBorder);
        }

        private void DrawPanelHeader(float x, float y, float w,
            string title, ref bool expanded)
        {
            Rect hdr = new Rect(x, y, w, 24f);
            FillRect(hdr, ColHeader);

            GUI.contentColor = ColYellow;
            GUI.Label(new Rect(x + 8f, y + 4f, w - 40f, 16f), title, _sBold);
            GUI.contentColor = Color.white;

            // Expand/collapse button
            if (GUI.Button(new Rect(x + w - 26f, y + 3f, 22f, 18f),
                expanded ? "▾" : "▸", _sBtn))
                expanded = !expanded;

            // Bottom line
            FillRect(new Rect(x, y + 23f, w, 1f), ColBorder);
        }

        // ── Drawing utilities ─────────────────────────────────────────────────

        private static void FillRect(Rect r, Color c)
        {
            Color prev = GUI.color;
            GUI.color  = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color  = prev;
        }

        private static void DrawRectOutline(Rect r, Color c)
        {
            FillRect(new Rect(r.x,           r.y,            r.width, 1f), c);
            FillRect(new Rect(r.x,           r.yMax - 1f,    r.width, 1f), c);
            FillRect(new Rect(r.x,           r.y,            1f, r.height), c);
            FillRect(new Rect(r.xMax - 1f,   r.y,            1f, r.height), c);
        }

        // ── Style builder ─────────────────────────────────────────────────────

        private void EnsureStyles()
        {
            if (_stylesBuilt) return;
            _stylesBuilt = true;
            int sz = Mathf.Max(9, _fontSize);

            _sLabel = new GUIStyle(GUI.skin.label) { fontSize = sz };
            _sLabel.normal.textColor = ColWhite;

            _sBold = new GUIStyle(GUI.skin.label) { fontSize = sz, fontStyle = FontStyle.Bold };
            _sBold.normal.textColor = ColWhite;

            _sSmall = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(8, sz - 1) };
            _sSmall.normal.textColor = ColDim;

            _sBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize = sz,
                padding  = new RectOffset(3, 3, 2, 2),
            };
        }
    }
}
