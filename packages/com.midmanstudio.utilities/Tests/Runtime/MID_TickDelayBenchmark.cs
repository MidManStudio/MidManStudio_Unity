// MID_TickDelayBenchmark.cs
// Attach to any GameObject, call RunFullBenchmark() from inspector context menu
// or call BenchmarkRunner.RunAll() from code after scene loads.
// Measures: GC allocations, timing accuracy (+/- ms), max concurrent throughput.
//
// BASELINE TARGETS (pass/fail thresholds):
//   GC alloc after warmup:          0 bytes per After() call
//   Timing accuracy:                within 1 tick interval of requested delay
//   100 concurrent delays fired:    < 0.5ms total invoke overhead
//   Task.Delay equivalent:          ~300-500 bytes GC per call (for comparison)
//   Coroutine equivalent:           ~96-256 bytes GC per call (for comparison)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Profiling;
using MidManStudio.Core.TickDispatcher;
using MidManStudio.Core.Logging;
using Debug = UnityEngine.Debug;

public class MID_TickDelayBenchmark : MonoBehaviour
{
    #region Configuration
    [Header("Benchmark Settings")]
    [SerializeField] private int _warmupIterations = 10;
    [SerializeField] private int _testIterations   = 100;
    [SerializeField] private float _testDelaySeconds = 0.5f;
    [SerializeField] private TickRate _testTickRate  = TickRate.Tick_0_1;
    [SerializeField] private bool _runOnStart        = false;
    [SerializeField] private bool _logVerbose        = true;
    #endregion

    #region Baseline Targets
    // Adjust these to match your acceptable thresholds
    private const long   GC_ALLOC_TARGET_BYTES     = 0;     // zero after warmup
    private const double TIMING_ACCURACY_MS         = 150.0; // within 1 tick of Tick_0_1
    private const double MAX_INVOKE_OVERHEAD_MS      = 0.5;   // for 100 concurrent delays
    #endregion

    #region Internal State
    private ProfilerRecorder _gcAllocRecorder;
    private readonly List<BenchmarkResult> _results = new List<BenchmarkResult>();

    private struct BenchmarkResult
    {
        public string  TestName;
        public long    GCAllocBytes;
        public double  AvgTimingErrorMs;
        public double  MaxTimingErrorMs;
        public double  InvokeOverheadMs;
        public bool    Passed;
        public string  FailReason;
    }
    #endregion

    #region Unity Lifecycle
    private void Start()
    {
        if (_runOnStart) StartCoroutine(RunFullBenchmarkCoroutine());
    }

    private void OnDisable()
    {
        if (_gcAllocRecorder.Valid) _gcAllocRecorder.Dispose();
    }
    #endregion

    #region Public API
    [ContextMenu("Run Full Benchmark")]
    public void RunFullBenchmark()
    {
        StartCoroutine(RunFullBenchmarkCoroutine());
    }
    #endregion

    #region Benchmark Runner
    private IEnumerator RunFullBenchmarkCoroutine()
    {
        _results.Clear();
        Log("=== MID_TickDelay Benchmark Start ===");
        Log($"Warmup: {_warmupIterations}  |  Test iterations: {_testIterations}  |  Delay: {_testDelaySeconds}s  |  Rate: {_testTickRate}");

        // Warm up the pool so first allocations don't skew results
        yield return StartCoroutine(WarmUpPool());
        Log("Pool warm-up complete.");

        // --- Test 1: GC allocation per After() call ---
        yield return StartCoroutine(TestGCAllocation());

        // --- Test 2: Timing accuracy ---
        yield return StartCoroutine(TestTimingAccuracy());

        // --- Test 3: Concurrent throughput ---
        yield return StartCoroutine(TestConcurrentThroughput());

        // --- Test 4: Comparison baselines (Task.Delay / Coroutine) ---
        yield return StartCoroutine(TestTaskDelayBaseline());
        yield return StartCoroutine(TestCoroutineBaseline());

        PrintReport();
    }

    // ── Warm Up ────────────────────────────────────────────────────────────
    private IEnumerator WarmUpPool()
    {
        int fired = 0;
        for (int i = 0; i < _warmupIterations; i++)
        {
            MID_TickDelay.After(_testDelaySeconds, () => fired++, _testTickRate);
        }

        // Wait long enough for all warmup delays to fire
        float wait = _testDelaySeconds + 0.5f;
        yield return new WaitForSeconds(wait);

        if (_logVerbose)
            Log($"Warmup fired {fired}/{_warmupIterations} delays.");
    }

    // ── Test 1: GC Allocation ───────────────────────────────────────────────
    private IEnumerator TestGCAllocation()
    {
        Log("--- Test 1: GC Allocation ---");

        // One frame settle
        yield return null;

        _gcAllocRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc");
        yield return null; // Let recorder initialise

        long before = _gcAllocRecorder.LastValue;

        for (int i = 0; i < _testIterations; i++)
        {
            MID_TickDelay.After(_testDelaySeconds, DoNothing, _testTickRate);
        }

        yield return null;
        long after = _gcAllocRecorder.LastValue;
        _gcAllocRecorder.Dispose();

        long totalAlloc = after - before;
        long perCall    = totalAlloc / _testIterations;

        bool passed     = perCall <= GC_ALLOC_TARGET_BYTES;

        _results.Add(new BenchmarkResult
        {
            TestName      = "GC Alloc per After() call",
            GCAllocBytes  = perCall,
            Passed        = passed,
            FailReason    = passed ? "" : $"Expected <= {GC_ALLOC_TARGET_BYTES}B, got {perCall}B"
        });

        Log($"GC alloc per call: {perCall} bytes  {(passed ? "PASS" : "FAIL")}");

        // Cancel outstanding delays so they don't fire mid other tests
        MID_TickDelay.CancelAll();
        yield return new WaitForSeconds(0.1f);
    }

    // ── Test 2: Timing Accuracy ─────────────────────────────────────────────
    private IEnumerator TestTimingAccuracy()
    {
        Log("--- Test 2: Timing Accuracy ---");

        double totalError = 0;
        double maxError   = 0;
        int    fired      = 0;

        for (int i = 0; i < _testIterations; i++)
        {
            float  requestedDelay = _testDelaySeconds;
            double scheduleTime   = GetRealtimeMs();

            MID_TickDelay.After(requestedDelay, () =>
            {
                double fireTime = GetRealtimeMs();
                double error    = Math.Abs(fireTime - scheduleTime - (requestedDelay * 1000.0));
                totalError += error;
                if (error > maxError) maxError = error;
                fired++;
            }, _testTickRate);

            // Stagger by 1 frame to avoid pool exhaustion
            yield return null;
        }

        // Wait for all to fire
        yield return new WaitForSeconds(_testDelaySeconds + 1.0f);

        double avgError = fired > 0 ? totalError / fired : double.MaxValue;
        bool   passed   = avgError <= TIMING_ACCURACY_MS && maxError <= TIMING_ACCURACY_MS * 2.0;

        _results.Add(new BenchmarkResult
        {
            TestName          = "Timing accuracy",
            AvgTimingErrorMs  = avgError,
            MaxTimingErrorMs  = maxError,
            Passed            = passed,
            FailReason        = passed ? "" : $"Avg error {avgError:F1}ms exceeds target {TIMING_ACCURACY_MS}ms"
        });

        Log($"Timing — Fired: {fired}/{_testIterations}  Avg error: {avgError:F1}ms  Max: {maxError:F1}ms  {(passed ? "PASS" : "FAIL")}");
    }

    // ── Test 3: Concurrent Throughput ───────────────────────────────────────
    private IEnumerator TestConcurrentThroughput()
    {
        Log("--- Test 3: Concurrent Throughput (100 simultaneous) ---");

        int    concurrentCount = Mathf.Min(100, MID_TickDelay.PoolCapacity - 10);
        int    fired           = 0;
        var    sw              = new Stopwatch();

        // Schedule all at once
        for (int i = 0; i < concurrentCount; i++)
        {
            MID_TickDelay.After(_testDelaySeconds, () =>
            {
                if (!sw.IsRunning) sw.Start();
                fired++;
                if (fired == concurrentCount) sw.Stop();
            }, _testTickRate);
        }

        yield return new WaitForSeconds(_testDelaySeconds + 1.0f);

        double invokeMs = sw.Elapsed.TotalMilliseconds;
        bool   passed   = invokeMs <= MAX_INVOKE_OVERHEAD_MS;

        _results.Add(new BenchmarkResult
        {
            TestName         = $"Concurrent throughput ({concurrentCount} delays)",
            InvokeOverheadMs = invokeMs,
            Passed           = passed,
            FailReason       = passed ? "" : $"Invoke overhead {invokeMs:F3}ms exceeds target {MAX_INVOKE_OVERHEAD_MS}ms"
        });

        Log($"Throughput — Fired: {fired}/{concurrentCount}  Invoke window: {invokeMs:F3}ms  {(passed ? "PASS" : "FAIL")}");
    }

    // ── Test 4a: Task.Delay Baseline ────────────────────────────────────────
    private IEnumerator TestTaskDelayBaseline()
    {
        Log("--- Baseline: Task.Delay GC alloc (comparison only, no pass/fail) ---");
        yield return null;

        _gcAllocRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc");
        yield return null;

        long before = _gcAllocRecorder.LastValue;

        for (int i = 0; i < _testIterations; i++)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(_testDelaySeconds));
        }

        yield return null;
        long after = _gcAllocRecorder.LastValue;
        _gcAllocRecorder.Dispose();

        long perCall = (after - before) / _testIterations;
        Log($"Task.Delay GC alloc per call: {perCall} bytes  (BASELINE — not a failure)");

        _results.Add(new BenchmarkResult
        {
            TestName     = "Task.Delay baseline (comparison)",
            GCAllocBytes = perCall,
            Passed       = true // baseline, not scored
        });
    }

    // ── Test 4b: Coroutine Baseline ─────────────────────────────────────────
    private IEnumerator TestCoroutineBaseline()
    {
        Log("--- Baseline: Coroutine GC alloc (comparison only, no pass/fail) ---");
        yield return null;

        _gcAllocRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc");
        yield return null;

        long before = _gcAllocRecorder.LastValue;

        for (int i = 0; i < _testIterations; i++)
        {
            StartCoroutine(DummyCoroutine());
        }

        yield return null;
        long after = _gcAllocRecorder.LastValue;
        _gcAllocRecorder.Dispose();

        long perCall = (after - before) / _testIterations;
        Log($"Coroutine GC alloc per call: {perCall} bytes  (BASELINE — not a failure)");

        _results.Add(new BenchmarkResult
        {
            TestName     = "Coroutine baseline (comparison)",
            GCAllocBytes = perCall,
            Passed       = true
        });
    }
    #endregion

    #region Report
    private void PrintReport()
    {
        int passed = 0, failed = 0;

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("\n╔══════════════════════════════════════════════════════╗");
        sb.AppendLine("║           MID_TickDelay Benchmark Report             ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════╝");

        foreach (var r in _results)
        {
            string status = r.Passed ? "✓ PASS" : "✗ FAIL";
            sb.AppendLine($"  {status}  {r.TestName}");

            if (r.GCAllocBytes > 0)
                sb.AppendLine($"          GC alloc: {r.GCAllocBytes} bytes/call");
            if (r.AvgTimingErrorMs > 0)
                sb.AppendLine($"          Avg timing error: {r.AvgTimingErrorMs:F1}ms  Max: {r.MaxTimingErrorMs:F1}ms");
            if (r.InvokeOverheadMs > 0)
                sb.AppendLine($"          Invoke overhead: {r.InvokeOverheadMs:F3}ms");
            if (!r.Passed)
                sb.AppendLine($"          FAIL: {r.FailReason}");

            if (r.Passed) passed++; else failed++;
        }

        sb.AppendLine($"\n  Result: {passed} passed, {failed} failed");
        sb.AppendLine("══════════════════════════════════════════════════════");

        Debug.Log(sb.ToString());
    }
    #endregion

    #region Helpers
    private static void DoNothing() { }
    private IEnumerator DummyCoroutine() { yield return new WaitForSeconds(0.5f); }
    private static double GetRealtimeMs() => Time.realtimeSinceStartupAsDouble * 1000.0;

    private void Log(string msg)
    {
        if (_logVerbose)
            Debug.Log($"[Benchmark] {msg}");
    }
    #endregion
}
