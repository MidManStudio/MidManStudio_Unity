// TimerBenchmarkRunner.cs
// Runtime tests and benchmarks for Timer.cs and PerformanceBenchmarkTimer.cs.
//
// USAGE:
//   1. Add this component to any scene GameObject.
//   2. Press Play — tests run automatically if RunOnStart = true.
//   3. Or right-click the component and select "Run All Tests".
//   Results are printed to the Console with clear PASS / FAIL markers.
//
// ASSEMBLY:
//   Lives in MidManStudio.Utilities.Tests (Tests/Runtime/).
//   Unity resolves it at play time even though autoReferenced = false —
//   just add the MonoBehaviour to your scene GameObject normally.

using System;
using System.Collections;
using UnityEngine;
using MidManStudio.Core.Timers;

namespace MidManStudio.Core.Tests
{
    public class TimerBenchmarkRunner : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Test Settings")]
        [Tooltip("Run all tests automatically when entering Play mode.")]
        [SerializeField] private bool _runOnStart = true;

        [Tooltip("Frame-timing tolerance in seconds for coroutine-based timing tests.\n" +
                 "Increase if you see false failures on slow machines.")]
        [SerializeField] private float _timingTolerance = 0.06f; // 60 ms

        [Header("Benchmark Settings")]
        [SerializeField] private int _benchIterations = 2000;
        [SerializeField] private int _benchWarmup     = 100;

        // ── State ─────────────────────────────────────────────────────────────

        private int _passed;
        private int _failed;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Start()
        {
            if (_runOnStart)
                StartCoroutine(RunAllCo());
        }

        [ContextMenu("Run All Tests")]
        public void RunAllFromContextMenu() => StartCoroutine(RunAllCo());

        // ── Master coroutine ──────────────────────────────────────────────────

        private IEnumerator RunAllCo()
        {
            _passed = 0;
            _failed = 0;

            Header("MidManStudio Timer Test Suite");
            yield return null; // let the console flush before heavy tests

            yield return StartCoroutine(TestCountdownTimer());
            yield return StartCoroutine(TestStopwatchTimer());
            yield return StartCoroutine(TestNetworkTimer());
            yield return StartCoroutine(TestValueInterpolationTimer());
            yield return StartCoroutine(TestSteppedValueTimer());
            yield return StartCoroutine(TestPerformanceBenchmarkTimer());

            Header($"Results: {_passed} PASSED  |  {_failed} FAILED");

            if (_failed == 0)
                Debug.Log("<color=lime><b>All timer tests passed.</b></color>");
            else
                Debug.LogError($"<color=red><b>{_failed} timer test(s) failed — see above.</b></color>");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CountdownTimer
        // ─────────────────────────────────────────────────────────────────────

        private IEnumerator TestCountdownTimer()
        {
            Section("CountdownTimer");

            // ── Basic countdown & callback ────────────────────────────────────
            {
                const float duration = 0.2f;
                var timer = new CountdownTimer(duration);

                bool startFired    = false;
                bool completeFired = false;
                timer.OnTimerStart    += () => startFired    = true;
                timer.OnTimerComplete += () => completeFired = true;

                timer.Start();
                Expect("OnTimerStart fires on Start()", startFired);
                Expect("IsRunning true after Start()", timer.IsRunning);
                Expect("IsFinished false right after Start()", !timer.IsFinished);
                Expect("Progress ~1 at start", Approx(timer.Progress, 1f));

                yield return TickUntilOrTimeout(timer, duration + _timingTolerance);

                Expect("IsFinished true after duration", timer.IsFinished);
                Expect("OnTimerComplete fired", completeFired);
                Expect("IsRunning false after completion", !timer.IsRunning);
                Expect("Progress is 0 after completion", Approx(timer.Progress, 0f));
            }

            // ── Reset ─────────────────────────────────────────────────────────
            {
                var timer = new CountdownTimer(0.1f);
                timer.Start();
                yield return TickUntilOrTimeout(timer, 0.3f);

                timer.Reset();
                Expect("IsFinished false after Reset()", !timer.IsFinished);
                Expect("Progress ~1 after Reset()", Approx(timer.Progress, 1f));
            }

            // ── Reset with new duration ───────────────────────────────────────
            {
                var timer = new CountdownTimer(0.05f);
                timer.Start();
                timer.Tick(0.06f); // finish it
                Expect("IsFinished before Reset(newTime)", timer.IsFinished);

                timer.Reset(0.5f);
                Expect("IsFinished false after Reset(newTime)", !timer.IsFinished);
                Expect("Progress ~1 with new duration", Approx(timer.Progress, 1f));
            }

            // ── Pause / Resume ────────────────────────────────────────────────
            {
                var timer = new CountdownTimer(1.0f);
                timer.Start();
                timer.Tick(0.3f);

                float progressAtPause = timer.Progress;
                timer.Pause();
                Expect("IsRunning false after Pause()", !timer.IsRunning);

                timer.Tick(0.3f); // should not advance while paused
                Expect("Progress unchanged while paused",
                    Approx(timer.Progress, progressAtPause));

                timer.Resume();
                Expect("IsRunning true after Resume()", timer.IsRunning);

                timer.Tick(0.9f); // enough to finish
                Expect("Timer finishes after Resume()", timer.IsFinished);
            }

            // ── Stop ─────────────────────────────────────────────────────────
            {
                bool completeFired = false;
                var timer = new CountdownTimer(1.0f);
                timer.OnTimerComplete += () => completeFired = true;
                timer.Start();
                timer.Stop();
                timer.Tick(2.0f); // should not fire complete
                Expect("OnTimerComplete NOT fired after Stop()", !completeFired);
                Expect("IsFinished false after Stop() + Tick past duration",
                    !timer.IsFinished);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  StopwatchTimer
        // ─────────────────────────────────────────────────────────────────────

        private IEnumerator TestStopwatchTimer()
        {
            Section("StopwatchTimer");

            // ── Accumulates time ──────────────────────────────────────────────
            {
                var sw = new StopwatchTimer();
                sw.Start();

                float waitSec = 0.15f;
                float elapsed = 0f;
                while (elapsed < waitSec) { sw.Tick(Time.deltaTime); elapsed += Time.deltaTime; yield return null; }

                float measured = sw.GetTime();
                Expect($"GetTime() ~{waitSec:F2}s within ±{_timingTolerance:F2}s (got {measured:F3}s)",
                    Mathf.Abs(measured - waitSec) < _timingTolerance);
            }

            // ── Pause stops accumulation ──────────────────────────────────────
            {
                var sw = new StopwatchTimer();
                sw.Start();
                sw.Tick(0.1f);
                float before = sw.GetTime();
                sw.Pause();
                sw.Tick(0.5f);
                Expect("GetTime() unchanged while paused", Approx(sw.GetTime(), before));
            }

            // ── Reset zeroes time, keeps running ──────────────────────────────
            {
                var sw = new StopwatchTimer();
                sw.Start();
                sw.Tick(0.2f);
                sw.Reset();
                Expect("GetTime() is 0 after Reset()", Approx(sw.GetTime(), 0f));
                Expect("IsRunning true after Reset()", sw.IsRunning);

                sw.Tick(0.05f);
                Expect("Continues accumulating after Reset()", sw.GetTime() > 0f);
            }

            // ── Stop halts ────────────────────────────────────────────────────
            {
                var sw = new StopwatchTimer();
                sw.Start();
                sw.Tick(0.1f);
                sw.Stop();
                float atStop = sw.GetTime();
                sw.Tick(0.5f);
                Expect("GetTime() unchanged after Stop()", Approx(sw.GetTime(), atStop));
                Expect("IsRunning false after Stop()", !sw.IsRunning);
            }

            yield return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  NetworkTimer
        // ─────────────────────────────────────────────────────────────────────

        private IEnumerator TestNetworkTimer()
        {
            Section("NetworkTimer");

            // ── Tick rate conversion ──────────────────────────────────────────
            {
                var nt = new NetworkTimer(60f);
                float expected = 1f / 60f;
                Expect($"minTimeBtwTicks = 1/60 (got {nt.minTimeBtwTicks:F5})",
                    Approx(nt.minTimeBtwTicks, expected, 0.0001f));
            }

            // ── ShouldTick() fires once per interval ──────────────────────────
            {
                var nt = new NetworkTimer(10f); // 10 ticks/sec, 0.1s each
                float iv = nt.minTimeBtwTicks;

                // Should not tick before interval
                nt.Update(iv * 0.5f);
                Expect("ShouldTick() false before full interval", !nt.ShouldTick());

                // Should tick at interval
                nt.Update(iv * 0.5f); // total = iv
                Expect("ShouldTick() true at interval", nt.ShouldTick());
                Expect("CurrentTick incremented to 1", nt.currentTick == 1);

                // Should not tick again immediately
                Expect("ShouldTick() false immediately after", !nt.ShouldTick());
            }

            // ── Multiple ticks in one frame ───────────────────────────────────
            {
                var nt = new NetworkTimer(10f);
                nt.Update(nt.minTimeBtwTicks * 3.5f); // should yield 3 ticks
                int ticks = 0;
                while (nt.ShouldTick()) ticks++;
                Expect($"3 ticks from 3.5× interval (got {ticks})", ticks == 3);
                Expect("CurrentTick == 3", nt.currentTick == 3);
            }

            // ── LerpFraction ──────────────────────────────────────────────────
            {
                var nt = new NetworkTimer(10f);
                nt.Update(nt.minTimeBtwTicks * 0.75f);
                Expect($"LerpFraction ~0.75 (got {nt.lerpFraction:F2})",
                    Approx(nt.lerpFraction, 0.75f, 0.02f));
            }

            // ── Reset ─────────────────────────────────────────────────────────
            {
                var nt = new NetworkTimer(10f);
                nt.Update(nt.minTimeBtwTicks);
                nt.ShouldTick();
                nt.Reset();
                Expect("CurrentTick == 0 after Reset()", nt.currentTick == 0);
                Expect("ShouldTick() false after Reset()", !nt.ShouldTick());
                Expect("LerpFraction == 0 after Reset()", Approx(nt.lerpFraction, 0f));
            }

            // ── Boundary: zero tick rate clamps to 60fps ──────────────────────
            {
                var nt = new NetworkTimer(0f); // invalid rate
                Expect("Zero tick rate uses fallback (1/60)",
                    Approx(nt.minTimeBtwTicks, 1f / 60f, 0.001f));
            }

            yield return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ValueInterpolationTimer
        // ─────────────────────────────────────────────────────────────────────

        private IEnumerator TestValueInterpolationTimer()
        {
            Section("ValueInterpolationTimer");

            // ── Linear 0→1 ────────────────────────────────────────────────────
            {
                const float duration = 0.2f;
                var vit = new ValueInterpolationTimer(0f, 1f, duration);

                bool started = false, completed = false;
                float lastValue = 0f;
                vit.OnInterpolationStart    += () => started    = true;
                vit.OnInterpolationComplete += () => completed  = true;
                vit.OnValueChanged          += v  => lastValue  = v;

                vit.Start();
                Expect("OnInterpolationStart fires on Start()", started);
                Expect("IsRunning true after Start()", vit.IsRunning);
                Expect("CurrentValue is startValue before Tick()", Approx(vit.CurrentValue, 0f));

                yield return TickVITUntilOrTimeout(vit, duration + _timingTolerance);

                Expect("OnInterpolationComplete fired", completed);
                Expect("IsRunning false after completion", !vit.IsRunning);
                Expect($"CurrentValue is endValue (got {lastValue:F3})", Approx(lastValue, 1f));
                Expect("Progress is 1 at completion", Approx(vit.Progress, 1f));
            }

            // ── Midpoint correctness (linear) ─────────────────────────────────
            {
                var vit = new ValueInterpolationTimer(0f, 100f, 1.0f);
                vit.Start();
                vit.Tick(0.5f); // exactly halfway
                Expect($"Linear midpoint ~50 (got {vit.CurrentValue:F2})",
                    Approx(vit.CurrentValue, 50f, 1f));
            }

            // ── EaseIn mode ───────────────────────────────────────────────────
            // EaseIn: curve = t*t. At t=0.5, t²=0.25, value = Lerp(0,10,0.25) = 2.5
            {
                var vit = new ValueInterpolationTimer(0f, 10f, 1.0f, InterpolationMode.EaseIn);
                vit.Start();
                vit.Tick(0.5f);
                Expect($"EaseIn midpoint ~2.5 (got {vit.CurrentValue:F2})",
                    Approx(vit.CurrentValue, 2.5f, 0.3f));
            }

            // ── EaseOut mode ──────────────────────────────────────────────────
            // EaseOut: curve = t*(2-t). At t=0.5, 0.5*(1.5)=0.75, value ~= 7.5
            {
                var vit = new ValueInterpolationTimer(0f, 10f, 1.0f, InterpolationMode.EaseOut);
                vit.Start();
                vit.Tick(0.5f);
                Expect($"EaseOut midpoint ~7.5 (got {vit.CurrentValue:F2})",
                    Approx(vit.CurrentValue, 7.5f, 0.3f));
            }

            // ── Reconfigure ───────────────────────────────────────────────────
            {
                var vit = new ValueInterpolationTimer(0f, 10f, 0.5f);
                vit.Start();
                vit.Tick(0.2f);
                vit.Reconfigure(5f, 20f, 1.0f);
                Expect("Reconfigure resets CurrentValue to new start",
                    Approx(vit.CurrentValue, 5f));
            }

            // ── Stop halts interpolation ──────────────────────────────────────
            {
                var vit = new ValueInterpolationTimer(0f, 1f, 0.3f);
                vit.Start();
                vit.Tick(0.1f);
                float atStop = vit.CurrentValue;
                vit.Stop();
                vit.Tick(0.5f);
                Expect("CurrentValue unchanged after Stop()",
                    Approx(vit.CurrentValue, atStop));
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SteppedValueTimer
        // ─────────────────────────────────────────────────────────────────────

        private IEnumerator TestSteppedValueTimer()
        {
            Section("SteppedValueTimer");

            // ── Ascending, 4 equal steps ──────────────────────────────────────
            {
                // 0→1 in steps of 0.25 every 0.04s → 4 steps, done in 0.16s
                const float start = 0f, end = 1f, step = 0.25f, interval = 0.04f;
                var svt = new SteppedValueTimer(start, end, step, interval);

                int  stepsFired   = 0;
                bool completed    = false;
                float lastValue   = start;

                svt.OnStepComplete += () => stepsFired++;
                svt.OnComplete     += () => completed = true;
                svt.OnValueChanged += v  => lastValue = v;

                svt.Start();
                Expect("IsRunning true after Start()", svt.IsRunning);

                float elapsed = 0f;
                float timeout = 2f;
                while (!completed && elapsed < timeout)
                {
                    svt.Tick(Time.deltaTime);
                    elapsed += Time.deltaTime;
                    yield return null;
                }

                Expect("OnComplete fired", completed);
                Expect($"Final value is {end} (got {lastValue:F3})", Approx(lastValue, end));
                Expect($"Step count is 4 (got {stepsFired})", stepsFired == 4);
                Expect("IsRunning false after completion", !svt.IsRunning);
                Expect("Progress == 1 after completion", Approx(svt.Progress, 1f));
            }

            // ── Descending ────────────────────────────────────────────────────
            {
                const float start = 1f, end = 0f, step = 0.5f, interval = 0.05f;
                var svt = new SteppedValueTimer(start, end, step, interval);
                bool completed = false;
                svt.OnComplete += () => completed = true;
                svt.Start();

                // Manual tick to drive it to completion without frame dependency
                int safety = 0;
                while (!completed && safety++ < 200)
                    svt.Tick(interval);

                Expect("Descending timer completes", completed);
                Expect($"Descending final value ~{end} (got {svt.CurrentValue:F3})",
                    Approx(svt.CurrentValue, end));
            }

            // ── Reset ─────────────────────────────────────────────────────────
            {
                var svt = new SteppedValueTimer(0f, 1f, 0.5f, 0.05f);
                svt.Start();
                svt.Tick(0.1f); // advance two steps
                svt.Reset();
                Expect("CurrentValue resets to start after Reset()",
                    Approx(svt.CurrentValue, 0f));
            }

            yield return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PerformanceBenchmarkTimer
        // ─────────────────────────────────────────────────────────────────────

        private IEnumerator TestPerformanceBenchmarkTimer()
        {
            Section("PerformanceBenchmarkTimer");
            yield return null; // frame gap so console shows section header

            var bench = new PerformanceBenchmarkTimer();

            // ── Iteration count is exact ──────────────────────────────────────
            {
                var result = bench.RunBenchmark(() => { }, _benchIterations, _benchWarmup);
                Expect($"Iterations == {_benchIterations} (got {result.Iterations})",
                    result.Iterations == _benchIterations);
            }

            // ── Timing invariants ─────────────────────────────────────────────
            {
                var result = bench.RunBenchmark(() => { }, _benchIterations, _benchWarmup);
                Expect("TotalTimeMs >= 0",        result.TotalTimeMs    >= 0);
                Expect("AverageTimeMs >= 0",       result.AverageTimeMs  >= 0);
                Expect("MinTimeMs <= AverageTimeMs",
                    result.MinTimeMs <= result.AverageTimeMs + 0.0001);
                Expect("MaxTimeMs >= AverageTimeMs",
                    result.MaxTimeMs >= result.AverageTimeMs - 0.0001);
                Expect("MaxTimeMs >= MinTimeMs",   result.MaxTimeMs >= result.MinTimeMs);
                Expect("StandardDeviation >= 0",   result.StandardDeviation >= 0);
                Expect("ExceptionCount == 0",      result.ExceptionCount == 0);

                Debug.Log($"[PBT] Empty action:   avg={result.AverageTimeMs * 1000:F3}µs  " +
                          $"min={result.MinTimeMs * 1000:F3}µs  max={result.MaxTimeMs * 1000:F3}µs  " +
                          $"σ={result.StandardDeviation * 1000:F3}µs");
            }

            // ── Exception counting ────────────────────────────────────────────
            {
                int callCount = 0;
                var result = bench.RunBenchmark(() =>
                {
                    callCount++;
                    if (callCount % 2 == 0) throw new InvalidOperationException("test");
                }, 100, 0);
                Expect("ExceptionCount captured (>0 for alternating throw)",
                    result.ExceptionCount > 0);
                Expect("Iterations still equals requested count despite exceptions",
                    result.Iterations == 100);
            }

            // ── Work benchmark: result has meaningful data ─────────────────────
            {
                int iterations = Mathf.Max(100, _benchIterations / 10);
                var result = bench.RunBenchmark(() =>
                {
                    float x = 0f;
                    for (int i = 0; i < 200; i++) x += Mathf.Sin(i * 0.05f);
                    if (x > 1e9f) Debug.Log("sink"); // prevent optimisation
                }, iterations, _benchWarmup);

                Expect("Math work: AverageTimeMs > 0", result.AverageTimeMs > 0);
                Debug.Log($"[PBT] 200× Sin/tick: avg={result.AverageTimeMs * 1000:F3}µs  " +
                          $"total={result.TotalTimeMs:F2}ms  " +
                          $"mem/iter={result.AverageMemoryPerIteration}B");
            }

            // ── QuickBenchmark convenience ────────────────────────────────────
            {
                var result = PerformanceBenchmarkTimer.QuickBenchmark(
                    () => { int x = 0; for (int i = 0; i < 10; i++) x++; },
                    500, 20);
                Expect("QuickBenchmark produces valid result",
                    result.Iterations == 500 && result.AverageTimeMs >= 0);
            }

            // ── Method comparison ─────────────────────────────────────────────
            {
                var (rA, rB, comparison) = PerformanceBenchmarkTimer.QuickCompare(
                    () => { int   s = 0; for (int i = 0; i < 100; i++) s += i; },
                    () => { float f = 0f; for (int i = 0; i < 100; i++) f += i; },
                    300, "Int accumulate", "Float accumulate");

                Expect("Compare: result A has data",     rA.Iterations == 300);
                Expect("Compare: result B has data",     rB.Iterations == 300);
                Expect("Compare: summary string non-empty",
                    !string.IsNullOrWhiteSpace(comparison));

                Debug.Log($"[PBT] Comparison:\n{comparison}");
            }

            // ── CSV output ────────────────────────────────────────────────────
            {
                var result = bench.RunBenchmark(() => { }, 50, 10);
                string csv = result.ToCSV();
                string[] cols = csv.Split(',');
                Expect($"CSV has 9 columns (got {cols.Length})", cols.Length == 9);
                Expect("CSV first column is iteration count",
                    int.TryParse(cols[0], out int n) && n == 50);
            }

            yield return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Tick helpers (avoid WaitForSeconds which doesn't drive our timers)
        // ─────────────────────────────────────────────────────────────────────

        /// Drive a Timer.Tick() each frame until IsFinished or timeout.
        private IEnumerator TickUntilOrTimeout(CountdownTimer timer, float timeout)
        {
            float elapsed = 0f;
            while (!timer.IsFinished && elapsed < timeout)
            {
                float dt = Time.deltaTime;
                timer.Tick(dt);
                elapsed += dt;
                yield return null;
            }
        }

        /// Drive a ValueInterpolationTimer.Tick() each frame until not running.
        private IEnumerator TickVITUntilOrTimeout(ValueInterpolationTimer vit, float timeout)
        {
            float elapsed = 0f;
            while (vit.IsRunning && elapsed < timeout)
            {
                float dt = Time.deltaTime;
                vit.Tick(dt);
                elapsed += dt;
                yield return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Assertion & formatting helpers
        // ─────────────────────────────────────────────────────────────────────

        private void Expect(string label, bool condition)
        {
            if (condition)
            {
                _passed++;
                Debug.Log($"  <color=lime>✓</color> {label}");
            }
            else
            {
                _failed++;
                Debug.LogError($"  <color=red>✗ FAIL: {label}</color>");
            }
        }

        private static bool Approx(float a, float b, float tol = 0.01f)
            => Mathf.Abs(a - b) <= tol;

        private static void Section(string title)
            => Debug.Log($"<color=cyan><b>── {title} ──</b></color>");

        private static void Header(string title)
            => Debug.Log($"<color=yellow><b>━━━ {title} ━━━</b></color>");
    }
}
