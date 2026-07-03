// SHOWCASE — MID_TickDispatcher + MID_TickDelay + Timer classes running together
// continuously. Drop on any GameObject in its own scene and press Play. Watch the
// Console: a periodic summary line plus one-off milestone logs (delay fired,
// finite repeat sequence complete, countdown cycle restarted, pulse ping-ponging).

using System;
using UnityEngine;
using MidManStudio.Core.TickDispatcher;
using MidManStudio.Core.Timers;

namespace MidManStudio.Core.Tests.TickDispatcher
{
    public class MID_TickTimerShowcase : MonoBehaviour
    {
        [Header("Tick Dispatcher")]
        [SerializeField] private TickRate _fastRate    = TickRate.Tick_0_2;
        [SerializeField] private TickRate _slowRate    = TickRate.Tick_1;
        [SerializeField] private TickRate _summaryRate = TickRate.Tick_2;

        [Header("Tick Delay")]
        [SerializeField] private float _oneShotDelay         = 3f;
        [SerializeField] private float _finiteRepeatInterval = 1.5f;
        [SerializeField] private int   _finiteRepeatCount    = 4;
        [SerializeField] private float _foreverRepeatInterval = 5f;

        [Header("Timers")]
        [SerializeField] private float _countdownDuration = 4f;
        [SerializeField] private float _pulseDuration      = 2f;

        // Continuously-incrementing counters — visible proof the systems stay alive.
        private int _fastTicks;
        private int _slowTicks;
        private int _finiteRepeatsFired;
        private int _countdownCycles;

        private CountdownTimer          _countdown;
        private StopwatchTimer          _stopwatch;
        private ValueInterpolationTimer _pulse;

        private TickDelayHandle _foreverHandle;

        // Pre-allocated delegates — MID_TickDelay's zero-alloc contract requires this.
        private Action _onOneShot;
        private Action _onFiniteRepeatFire;
        private Action _onForeverRepeat;

        private MID_TickDispatcher.TickCallback _onFastTick;
        private MID_TickDispatcher.TickCallback _onSlowTick;
        private MID_TickDispatcher.TickCallback _onSummaryTick;

        private void Awake()
        {
            _onOneShot          = OnOneShotFired;
            _onFiniteRepeatFire = OnFiniteRepeatFired;
            _onForeverRepeat    = OnForeverRepeatFired;

            _onFastTick    = OnFastTick;
            _onSlowTick    = OnSlowTick;
            _onSummaryTick = OnSummaryTick;
        }

        private void Start()
        {
            Debug.Log("<color=cyan><b>━━━ Tick/Timer Showcase started ━━━</b></color>");

            // ── TickDispatcher: direct subscriptions at three different rates ────
            MID_TickDispatcher.Subscribe(_fastRate, _onFastTick);
            MID_TickDispatcher.Subscribe(_slowRate, _onSlowTick);
            MID_TickDispatcher.Subscribe(_summaryRate, _onSummaryTick);

            // ── TickDelay: one-shot, finite repeat, infinite repeat ──────────────
            MID_TickDelay.After(_oneShotDelay, _onOneShot);
            MID_TickDelay.Repeat(_finiteRepeatInterval, _finiteRepeatCount, _onFiniteRepeatFire);
            _foreverHandle = MID_TickDelay.RepeatForever(_foreverRepeatInterval, _onForeverRepeat);

            // ── Timers: ticked manually from Update() below ──────────────────────
            _countdown = new CountdownTimer(_countdownDuration);
            _countdown.OnTimerComplete += OnCountdownComplete;
            _countdown.Start();

            _stopwatch = new StopwatchTimer();
            _stopwatch.Start();

            _pulse = new ValueInterpolationTimer(0f, 1f, _pulseDuration, InterpolationMode.EaseInOut);
            _pulse.OnInterpolationComplete += OnPulseComplete;
            _pulse.StartPingPong();
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            _countdown.Tick(dt);
            _stopwatch.Tick(dt);
            _pulse.Tick(dt);
        }

        private void OnDestroy()
        {
            MID_TickDispatcher.Unsubscribe(_fastRate, _onFastTick);
            MID_TickDispatcher.Unsubscribe(_slowRate, _onSlowTick);
            MID_TickDispatcher.Unsubscribe(_summaryRate, _onSummaryTick);
            _foreverHandle.Cancel();
        }

        // ── TickDispatcher callbacks ─────────────────────────────────────────────

        private void OnFastTick(float dt) => _fastTicks++;
        private void OnSlowTick(float dt) => _slowTicks++;

        private void OnSummaryTick(float dt)
        {
            Debug.Log(
                $"<color=yellow>[Summary]</color> fastTicks={_fastTicks} slowTicks={_slowTicks} " +
                $"pendingDelays={MID_TickDelay.ActiveCount} " +
                $"stopwatch={_stopwatch.GetTime():F1}s " +
                $"countdown={_countdown.Progress:P0} " +
                $"pulse={_pulse.CurrentValue:F2}");
        }

        // ── TickDelay callbacks ──────────────────────────────────────────────────

        private void OnOneShotFired() =>
            Debug.Log($"<color=lime>✓ One-shot delay fired after {_oneShotDelay}s.</color>");

        private void OnFiniteRepeatFired()
        {
            _finiteRepeatsFired++;
            Debug.Log($"<color=lime>✓ Finite repeat fired ({_finiteRepeatsFired}/{_finiteRepeatCount}).</color>");
            if (_finiteRepeatsFired >= _finiteRepeatCount)
                Debug.Log("<color=lime>✓ Finite repeat sequence complete — no more fires expected.</color>");
        }

        private void OnForeverRepeatFired() =>
            Debug.Log("<color=cyan>↻ Forever-repeat heartbeat.</color>");

        // ── Timer callbacks ──────────────────────────────────────────────────────

        private void OnCountdownComplete()
        {
            _countdownCycles++;
            Debug.Log($"<color=orange>⏱ Countdown cycle {_countdownCycles} complete — restarting.</color>");
            _countdown.Reset();
            _countdown.Start();
        }

        private void OnPulseComplete()
        {
            Debug.Log("<color=magenta>◆ Pulse cycle complete — restarting.</color>");
            _pulse.StartPingPong();
        }
    }
}
