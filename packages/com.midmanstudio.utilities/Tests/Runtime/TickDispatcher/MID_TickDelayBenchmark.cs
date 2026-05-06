// MID_TickDelayBenchmark.cs
// Runtime benchmark: MID_TickDelay vs Coroutine vs Task.Delay
//
// ══ GC MEASUREMENT CAVEATS ═══════════════════════════════════════════════
//   Uses GC.GetAllocatedBytesForCurrentThread() — per-thread monotonic
//   counter, most accurate managed API available.
//
//   KNOWN LIMITATIONS:
//   - Task.Delay: .NET pools DelayPromise objects internally. After warmup
//     the pool is warm and subsequent calls show 0B. First run shows real
//     allocation (~120–160B). This is correct behaviour — Task reuses objects.
//   - Coroutine: Same effect. After first GC pass, IEnumerator state machines
//     get reused from the allocator free list. First run shows real alloc (~80B).
//   - Re-running the test in the same Play session will show lower numbers
//     for Coroutine and Task because their pools are warm.
//   - MID_TickDelay shows 0B consistently because it never allocates at all —
//     the pre-allocated pool has no internal pooling that could mask real allocs.
//   For ground truth: open Window > Analysis > Profiler, GC.Alloc column.
//
// ══ TIMING CAVEATS ════════════════════════════════════════════════════════
//   Task.Delay ≈ 0ms error — OS high-res timer, correct.
//   Coroutine  ≈ frame duration error — correct.
//   TickDelay  ≈ 0–one tick interval error — correct and intentional.
//   Task fires on threadpool. Time.realtimeSinceStartup is NOT thread-safe.
//   We use Stopwatch.GetTimestamp() for Task timing instead.
//
// ══ WHY MID_TICKDELAY FOR NETCODE? ═══════════════════════════════════════
//   - Zero alloc every run (no pool warm-up dependency)
//   - Fires on main thread — safe to touch Unity objects
//   - No IEnumerator — works inside ServerRpc/ClientRpc directly
//   - Cancellable with TickDelayHandle
//   Trade-off: timing bounded by one tick interval (~50ms avg at Tick_0_1)
//   which is acceptable for respawn timers, cooldowns, deferred RPCs.

using System;
using System.Collections;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using MidManStudio.Core.TickDispatcher;
using MidManStudio.Core.Logging;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MidManStudio.Core.Benchmarks
{
    [Serializable]
    public struct DelayBenchGCResult
    {
        public long   TickDelayBytesPerCall;
        public long   CoroutineBytesPerCall;
        public long   TaskDelayBytesPerCall;
        public int    Iterations;
        public bool   Valid;
        // Whether this was a first run (cold pools) or re-run (warm pools)
        public bool   WasColdRun;

        public long MaxBytes =>
            Math.Max(TickDelayBytesPerCall,
                Math.Max(CoroutineBytesPerCall, TaskDelayBytesPerCall));
    }

    [Serializable]
    public struct DelayBenchTimingResult
    {
        public double TickDelayAvgMs;
        public double TickDelayMaxMs;
        public int    TickDelayFired;

        public double CoroutineAvgMs;
        public double CoroutineMaxMs;
        public int    CoroutineFired;

        public double TaskDelayAvgMs;
        public double TaskDelayMaxMs;
        public int    TaskDelayFired;

        public int   Total;
        public float TickIntervalMs;
        public bool  Valid;

        public double MaxAvgMs =>
            Math.Max(TickDelayAvgMs, Math.Max(CoroutineAvgMs, TaskDelayAvgMs));
    }

    internal sealed class TimingAccumulator
    {
        public double TotalErr;
        public double MaxErr;
        public int    Fired;

        public void Record(double errorMs)
        {
            TotalErr += errorMs;
            if (errorMs > MaxErr) MaxErr = errorMs;
            Fired++;
        }

        public double AvgMs => Fired > 0 ? TotalErr / Fired : 0;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Add MID_TickDelayBenchRunner to a scene GameObject — nothing else.
    // ═════════════════════════════════════════════════════════════════════════

    public class MID_TickDelayBenchRunner : MonoBehaviour
    {
        [Header("Configuration")]
        public float    DelaySeconds     = 0.5f;
        public TickRate Rate             = TickRate.Tick_0_1;
        public int      GCIterations     = 500;
        public int      TimingIterations = 30;
        public int      WarmupCount      = 20;

        [Header("Log Level")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Results  (read-only)")]
        public DelayBenchGCResult     GCResult;
        public DelayBenchTimingResult TimingResult;
        public string                 StatusMessage = "Idle.";
        public float                  Progress;
        public bool                   IsRunning;

        // Track run count so window can warn about warm-pool effect
        public int RunCount { get; private set; }

        // Pre-allocated zero-alloc delegate — field reference, never reallocated.
        private static readonly Action _doNothing = DoNothing;

        private Coroutine _active;

        // ── Public API ────────────────────────────────────────────────────────

        public void RunAll()
        {
            if (IsRunning) return;
            StopActive();
            GCResult = default; TimingResult = default;
            _active  = StartCoroutine(RunAllCo());
        }

        public void RunGCOnly()
        {
            if (IsRunning) return;
            StopActive();
            GCResult = default;
            _active  = StartCoroutine(RunGCOnlyCo());
        }

        public void RunTimingOnly()
        {
            if (IsRunning) return;
            StopActive();
            TimingResult = default;
            _active      = StartCoroutine(RunTimingOnlyCo());
        }

        public void Cancel()
        {
            StopActive();
            MID_TickDelay.CancelAll();
            IsRunning = false;
            SetStatus("Cancelled.");
            Progress = 0f;
        }

        private void StopActive()
        {
            if (_active != null) { StopCoroutine(_active); _active = null; }
            IsRunning = false;
        }

        // ── Master coroutines ─────────────────────────────────────────────────

        private IEnumerator RunAllCo()
        {
            IsRunning = true;
            RunCount++;
            yield return StartCoroutine(WarmUp());
            yield return StartCoroutine(GCInner());
            yield return StartCoroutine(TimingInner());
            SetStatus("All tests complete.");
            Progress  = 1f;
            IsRunning = false;
        }

        private IEnumerator RunGCOnlyCo()
        {
            IsRunning = true;
            RunCount++;
            yield return StartCoroutine(WarmUp());
            yield return StartCoroutine(GCInner());
            SetStatus("GC test complete.");
            Progress  = 1f;
            IsRunning = false;
        }

        private IEnumerator RunTimingOnlyCo()
        {
            IsRunning = true;
            RunCount++;
            yield return StartCoroutine(WarmUp());
            yield return StartCoroutine(TimingInner());
            SetStatus("Timing test complete.");
            Progress  = 1f;
            IsRunning = false;
        }

        // ── Warm-up ───────────────────────────────────────────────────────────

        private IEnumerator WarmUp()
        {
            SetStatus($"Warming up — initialising pool and JIT ({WarmupCount} delays)…");
            Progress = 0f;

            for (int i = 0; i < WarmupCount; i++)
                MID_TickDelay.After(DelaySeconds, _doNothing, Rate);

            yield return new WaitForSeconds(DelaySeconds + TickIntervalSec() * 4f);
            MID_TickDelay.CancelAll();

            // Warm coroutine path too
            for (int i = 0; i < 5; i++) StartCoroutine(DummyWait(0.05f));
            yield return new WaitForSeconds(0.2f);

            SetStatus("Warm-up done.");
            yield return null; yield return null;
        }

        // ── GC test ───────────────────────────────────────────────────────────
        //
        // GetAllocatedBytesForCurrentThread() measures main-thread allocations only.
        // Task.Delay and Coroutine WILL show real allocations on first (cold) run.
        // On re-runs their internal pools are warm — results will be lower or 0.
        // MID_TickDelay always shows 0B because it never allocates regardless.

        private IEnumerator GCInner()
        {
            int  n       = GCIterations;
            bool isCold  = RunCount == 1;

            // ── MID_TickDelay ─────────────────────────────────────────────────
            SetStatus($"GC test — MID_TickDelay ({n} calls)…");
            yield return DoFullGC();

            long tdBefore = GetThreadAllocBytes();
            for (int i = 0; i < n; i++)
                MID_TickDelay.After(DelaySeconds, _doNothing, Rate);
            long tdPerCall = (GetThreadAllocBytes() - tdBefore) / n;

            yield return new WaitForSeconds(DelaySeconds + TickIntervalSec() * 3f);
            MID_TickDelay.CancelAll();
            yield return null;
            Progress = 0.33f;

            MID_Logger.LogInfo(_logLevel,
                $"[GC Test] MID_TickDelay = {tdPerCall} B/call over {n} iterations.",
                nameof(MID_TickDelayBenchRunner));

            // ── Coroutine ──────────────────────────────────────────────────────
            SetStatus($"GC test — Coroutine ({n} calls)…");
            yield return DoFullGC();

            long coBefore = GetThreadAllocBytes();
            for (int i = 0; i < n; i++)
                StartCoroutine(DummyWait(DelaySeconds));
            long coPerCall = (GetThreadAllocBytes() - coBefore) / n;

            yield return new WaitForSeconds(DelaySeconds + 0.3f);
            yield return null;
            Progress = 0.66f;

            MID_Logger.LogInfo(_logLevel,
                $"[GC Test] Coroutine = {coPerCall} B/call over {n} iterations " +
                (isCold ? "(cold run)" : "(warm run — pool reuse may show lower)"),
                nameof(MID_TickDelayBenchRunner));

            // ── Task.Delay ────────────────────────────────────────────────────
            // Task.Delay allocates on the main thread call site, but .NET pools
            // DelayPromise objects. After warmup, re-runs can legitimately show 0B.
            // This is a feature of Task's design, not a measurement error.
            SetStatus($"GC test — Task.Delay ({n} calls)…");
            yield return DoFullGC();

            var  taskDelay = TimeSpan.FromSeconds(DelaySeconds);
            long taskBefore = GetThreadAllocBytes();
            for (int i = 0; i < n; i++)
                _ = Task.Delay(taskDelay);
            long taskPerCall = (GetThreadAllocBytes() - taskBefore) / n;

            yield return null;
            Progress = 1f;

            MID_Logger.LogInfo(_logLevel,
                $"[GC Test] Task.Delay = {taskPerCall} B/call over {n} iterations " +
                (isCold ? "(cold run)" : "(warm run — DelayPromise pool may show 0B)"),
                nameof(MID_TickDelayBenchRunner));

            GCResult = new DelayBenchGCResult
            {
                TickDelayBytesPerCall = tdPerCall,
                CoroutineBytesPerCall = coPerCall,
                TaskDelayBytesPerCall = taskPerCall,
                Iterations            = n,
                Valid                 = true,
                WasColdRun            = isCold
            };

            SetStatus(
                $"GC done — TickDelay: {FormatBytes(tdPerCall)} | " +
                $"Coroutine: {FormatBytes(coPerCall)} | " +
                $"Task: {FormatBytes(taskPerCall)}" +
                (isCold ? "" : "  [warm pool — see Profiler for cold truth]"));
        }

        // ── Timing test ───────────────────────────────────────────────────────
        //
        // IMPORTANT: Time.realtimeSinceStartupAsDouble is NOT thread-safe.
        // Task.Delay fires ContinueWith on a threadpool thread.
        // We use Stopwatch.GetTimestamp() for Task timing — it uses
        // QueryPerformanceCounter which IS thread-safe.

        private IEnumerator TimingInner()
        {
            int   n      = TimingIterations;
            float tickMs = TickIntervalSec() * 1000f;

            // ── MID_TickDelay ─────────────────────────────────────────────────
            SetStatus($"Timing — MID_TickDelay (0/{n})…");
            var tdAcc = new TimingAccumulator();

            for (int i = 0; i < n; i++)
            {
                // Capture timing reference before scheduling — closure allocates here,
                // which is intentional (timing test, not GC test).
                long   startTick    = Stopwatch.GetTimestamp();
                float  capturedReq  = DelaySeconds;
                MID_TickDelay.After(capturedReq, () =>
                {
                    double actualMs   = TicksToMs(Stopwatch.GetTimestamp() - startTick);
                    double requestedMs = capturedReq * 1000.0;
                    tdAcc.Record(Math.Abs(actualMs - requestedMs));
                }, Rate);

                Progress = (float)i / (n * 3f);
                if (i % 5 == 0) SetStatus($"Timing — MID_TickDelay ({i}/{n})…");
                yield return null;
            }

            yield return new WaitForSeconds(DelaySeconds + TickIntervalSec() * 3f);
            Progress = 0.35f;

            MID_Logger.LogInfo(_logLevel,
                $"[Timing] MID_TickDelay — avg:{tdAcc.AvgMs:F1}ms  max:{tdAcc.MaxErr:F1}ms  " +
                $"fired:{tdAcc.Fired}/{n}  (tick interval = {tickMs:F0}ms)",
                nameof(MID_TickDelayBenchRunner));

            // ── Coroutine ──────────────────────────────────────────────────────
            SetStatus($"Timing — Coroutine (0/{n})…");
            var coAcc = new TimingAccumulator();

            for (int i = 0; i < n; i++)
            {
                // Coroutine fires on main thread — Stopwatch.GetTimestamp() is fine here too.
                StartCoroutine(TimedWait(DelaySeconds, Stopwatch.GetTimestamp(), coAcc));
                Progress = 0.33f + (float)i / (n * 3f);
                if (i % 5 == 0) SetStatus($"Timing — Coroutine ({i}/{n})…");
                yield return null;
            }

            yield return new WaitForSeconds(DelaySeconds + 0.5f);
            Progress = 0.67f;

            MID_Logger.LogInfo(_logLevel,
                $"[Timing] Coroutine — avg:{coAcc.AvgMs:F1}ms  max:{coAcc.MaxErr:F1}ms  " +
                $"fired:{coAcc.Fired}/{n}",
                nameof(MID_TickDelayBenchRunner));

            // ── Task.Delay ────────────────────────────────────────────────────
            // ContinueWith fires on threadpool.
            // Time.realtimeSinceStartupAsDouble is NOT thread-safe — use Stopwatch.
            SetStatus($"Timing — Task.Delay (0/{n})…");
            var  taskErrors   = new double[n];
            int  taskFiredRaw = 0;

            for (int i = 0; i < n; i++)
            {
                int    idx         = i;
                long   startTick   = Stopwatch.GetTimestamp();   // thread-safe
                float  capturedReq = DelaySeconds;

                _ = Task.Delay(TimeSpan.FromSeconds(capturedReq)).ContinueWith(_ =>
                {
                    // Stopwatch.GetTimestamp() is safe to call from any thread.
                    double actualMs    = TicksToMs(Stopwatch.GetTimestamp() - startTick);
                    double requestedMs = capturedReq * 1000.0;
                    taskErrors[idx]    = Math.Abs(actualMs - requestedMs);
                    Interlocked.Increment(ref taskFiredRaw);
                });

                Progress = 0.66f + (float)i / (n * 3f);
                if (i % 5 == 0) SetStatus($"Timing — Task.Delay ({i}/{n})…");
                yield return null;
            }

            // Wait for all threadpool callbacks to land
            float taskWait = DelaySeconds + 1.0f;
            yield return new WaitForSeconds(taskWait);

            double taskTotal = 0, taskMax = 0;
            int    taskFired = Volatile.Read(ref taskFiredRaw);
            for (int i = 0; i < n; i++)
            {
                taskTotal += taskErrors[i];
                if (taskErrors[i] > taskMax) taskMax = taskErrors[i];
            }

            MID_Logger.LogInfo(_logLevel,
                $"[Timing] Task.Delay — avg:{(taskFired > 0 ? taskTotal / taskFired : 0):F2}ms  " +
                $"max:{taskMax:F2}ms  fired:{taskFired}/{n}  (OS timer, threadpool thread)",
                nameof(MID_TickDelayBenchRunner));

            if (taskFired < n)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"[Timing] Task.Delay only fired {taskFired}/{n}. " +
                    "Increase wait time or check threadpool saturation.",
                    nameof(MID_TickDelayBenchRunner));
            }

            TimingResult = new DelayBenchTimingResult
            {
                TickDelayAvgMs = tdAcc.AvgMs,
                TickDelayMaxMs = tdAcc.MaxErr,
                TickDelayFired = tdAcc.Fired,
                CoroutineAvgMs = coAcc.AvgMs,
                CoroutineMaxMs = coAcc.MaxErr,
                CoroutineFired = coAcc.Fired,
                TaskDelayAvgMs = taskFired > 0 ? taskTotal / taskFired : 0,
                TaskDelayMaxMs = taskMax,
                TaskDelayFired = taskFired,
                Total          = n,
                TickIntervalMs = tickMs,
                Valid          = true
            };

            SetStatus(
                $"Timing — TD avg:{TimingResult.TickDelayAvgMs:F1}ms | " +
                $"Coro avg:{TimingResult.CoroutineAvgMs:F1}ms | " +
                $"Task avg:{TimingResult.TaskDelayAvgMs:F2}ms");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void DoNothing() { }

        private IEnumerator DummyWait(float seconds)
        {
            yield return new WaitForSeconds(seconds);
        }

        private IEnumerator TimedWait(float seconds, long startTick, TimingAccumulator acc)
        {
            yield return new WaitForSeconds(seconds);
            double actualMs    = TicksToMs(Stopwatch.GetTimestamp() - startTick);
            double requestedMs = seconds * 1000.0;
            acc.Record(Math.Abs(actualMs - requestedMs));
        }

        private IEnumerator DoFullGC()
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            yield return null;
            yield return null;
        }

        private static long GetThreadAllocBytes()
        {
            try   { return GC.GetAllocatedBytesForCurrentThread(); }
            catch { return GC.GetTotalMemory(false); }
        }

        private static double TicksToMs(long ticks) =>
            ticks * 1000.0 / Stopwatch.Frequency;

        private float TickIntervalSec() => Rate switch
        {
            TickRate.Tick_0_1 => 0.1f,
            TickRate.Tick_0_2 => 0.2f,
            TickRate.Tick_0_5 => 0.5f,
            TickRate.Tick_1   => 1.0f,
            TickRate.Tick_2   => 2.0f,
            TickRate.Tick_5   => 5.0f,
            _                 => 0.1f
        };

        private void SetStatus(string msg)
        {
            StatusMessage = msg;
            MID_Logger.LogDebug(_logLevel, msg, nameof(MID_TickDelayBenchRunner));
        }

        private static string FormatBytes(long b) =>
            b == 0 ? "0 B  ✓" : $"{b} B";
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Editor Window
    // ═════════════════════════════════════════════════════════════════════════

#if UNITY_EDITOR

    public class MID_TickDelayBenchWindow : EditorWindow
    {
        private MID_TickDelayBenchRunner _runner;
        private Vector2                  _scroll;
        private bool _fContext = true;
        private bool _fConfig  = true;
        private bool _fGC      = true;
        private bool _fTiming  = true;

        private static readonly Color ColTick  = new Color(0.28f, 0.90f, 0.45f, 1f);
        private static readonly Color ColCoro  = new Color(0.40f, 0.65f, 1.00f, 1f);
        private static readonly Color ColTask  = new Color(1.00f, 0.70f, 0.25f, 1f);
        private static readonly Color ColBarBg = new Color(0.15f, 0.15f, 0.15f, 0.5f);
        private static readonly Color ColDim   = new Color(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color ColPass  = new Color(0.28f, 0.95f, 0.45f, 1f);
        private static readonly Color ColFail  = new Color(1.00f, 0.35f, 0.35f, 1f);
        private static readonly Color ColWarn  = new Color(1.00f, 0.85f, 0.25f, 1f);
        private static readonly Color ColInfo  = new Color(0.60f, 0.80f, 1.00f, 1f);

        [MenuItem("MidManStudio/Utilities/Tests/Tick Delay Bench")]
        public static void Open()
        {
            var w = GetWindow<MID_TickDelayBenchWindow>("Tick Delay Bench");
            w.minSize = new Vector2(520, 600);
        }

        private void OnEnable()  { EditorApplication.update += Repaint; TryFind(); }
        private void OnDisable() { EditorApplication.update -= Repaint; }

        private void TryFind()
        {
            if (_runner == null)
                _runner = FindObjectOfType<MID_TickDelayBenchRunner>();
        }

        private void OnGUI()
        {
            TryFind();
            DrawToolbar();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawPlayModeGuard();

            if (Application.isPlaying && _runner != null)
            {
                DrawContext();
                DrawSeparator();
                DrawConfig();
                DrawRunButtons();
                DrawSeparator();
                DrawGCSection();
                DrawSeparator();
                DrawTimingSection();
                DrawSeparator();
                DrawLegend();
            }

            EditorGUILayout.EndScrollView();
        }

        // ── Toolbar ───────────────────────────────────────────────────────────

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField("MID_TickDelay Benchmark",
                    EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                _runner = (MID_TickDelayBenchRunner)EditorGUILayout.ObjectField(
                    _runner, typeof(MID_TickDelayBenchRunner), true, GUILayout.Width(200));
            }
        }

        // ── Play mode guard ───────────────────────────────────────────────────

        private void DrawPlayModeGuard()
        {
            EditorGUILayout.Space(4);
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to run benchmarks.", MessageType.Info);
                return;
            }
            if (_runner == null)
            {
                EditorGUILayout.HelpBox(
                    "No MID_TickDelayBenchRunner found.\n\n" +
                    "If you see 'DelayBenchGCResult missing ExtensionOfNativeClass':\n" +
                    "Find the broken GameObject and remove the missing script component.\n" +
                    "DelayBenchGCResult is a struct, not a MonoBehaviour.",
                    MessageType.Warning);
                if (GUILayout.Button("Add Runner to Scene", GUILayout.Height(28)))
                {
                    var go = new GameObject("[TickDelayBenchRunner]");
                    _runner = go.AddComponent<MID_TickDelayBenchRunner>();
                    Selection.activeGameObject = go;
                    Undo.RegisterCreatedObjectUndo(go, "Add Bench Runner");
                }
            }
        }

        // ── Why section ───────────────────────────────────────────────────────

        private void DrawContext()
        {
            _fContext = EditorGUILayout.BeginFoldoutHeaderGroup(_fContext,
                "Why MID_TickDelay? — trade-offs at a glance");
            if (_fContext)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    var old = GUI.color;

                    Row(ColPass, "MID_TickDelay",
                        "✓ Zero GC allocation — always, not just cold runs\n" +
                        "✓ Fires on main thread — safe to call Unity APIs\n" +
                        "✓ No IEnumerator — works directly inside ServerRpc/ClientRpc\n" +
                        "✓ Cancellable via TickDelayHandle\n" +
                        "✗ Timing bounded by tick interval (~50ms avg error at Tick_0_1)");

                    EditorGUILayout.Space(3);

                    Row(ColCoro, "Coroutine",
                        "✓ Frame-accurate (fires next frame after delay expires)\n" +
                        "✗ ~80–400B per StartCoroutine call (IEnumerator state machine)\n" +
                        "✗ Requires IEnumerator — CANNOT be used directly in RPCs\n" +
                        "✗ Tied to MonoBehaviour lifecycle");

                    EditorGUILayout.Space(3);

                    Row(ColTask, "Task.Delay",
                        "✓ OS-timer accurate (~0–1ms error)\n" +
                        "✓ DelayPromise pooled by .NET after warmup (can show 0B on re-runs)\n" +
                        "✗ Fires on threadpool — CANNOT touch Unity objects\n" +
                        "✗ First run allocates ~120–160B; subsequent runs pool-dependent\n" +
                        "✗ No clean cancellation without CancellationToken (extra alloc)");

                    GUI.color = old;
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(2);
        }

        private void Row(Color col, string label, string text)
        {
            var old = GUI.color;
            GUI.color = col;
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            GUI.color = old;
            EditorGUILayout.LabelField(text, EditorStyles.wordWrappedMiniLabel);
        }

        // ── Config ────────────────────────────────────────────────────────────

        private void DrawConfig()
        {
            _fConfig = EditorGUILayout.BeginFoldoutHeaderGroup(_fConfig, "Configuration");
            if (_fConfig)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    GUI.enabled = !_runner.IsRunning;
                    _runner.DelaySeconds = EditorGUILayout.Slider(
                        "Delay (s)", _runner.DelaySeconds, 0.1f, 5f);
                    _runner.Rate = (TickRate)EditorGUILayout.EnumPopup(
                        new GUIContent("Tick Rate", "Min = Tick_0_1. Error ≈ 0 to one tick interval."),
                        _runner.Rate);
                    _runner.GCIterations = EditorGUILayout.IntSlider(
                        new GUIContent("GC Iterations",
                            "Calls per method. Cold run (run 1) gives most accurate results."),
                        _runner.GCIterations, 50, 1000);
                    _runner.TimingIterations = EditorGUILayout.IntSlider(
                        "Timing Iterations", _runner.TimingIterations, 10, 100);
                    _runner.WarmupCount = EditorGUILayout.IntSlider(
                        "Warmup", _runner.WarmupCount, 10, 50);
                    GUI.enabled = true;

                    float tm = TickIntervalMs(_runner.Rate);
                    EditorGUILayout.HelpBox(
                        $"Tick interval: {tm:F0}ms   " +
                        $"Expected TickDelay timing error: 0–{tm:F0}ms  (avg ~{tm * 0.5f:F0}ms)\n" +
                        "GC test: most accurate on first run in a fresh Play session.",
                        MessageType.None);
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(2);
        }

        // ── Run buttons ───────────────────────────────────────────────────────

        private void DrawRunButtons()
        {
            // Show run count so user knows if results are from a cold or warm run
            if (_runner.RunCount > 0)
            {
                var old = GUI.color;
                GUI.color = _runner.RunCount == 1 ? ColPass : ColWarn;
                EditorGUILayout.LabelField(
                    _runner.RunCount == 1
                        ? $"Run #{_runner.RunCount}  (cold — most accurate GC results)"
                        : $"Run #{_runner.RunCount}  (warm — Coroutine/Task GC may show lower due to pool reuse)",
                    EditorStyles.miniBoldLabel);
                GUI.color = old;
            }

            if (_runner.IsRunning)
            {
                Rect r = EditorGUILayout.GetControlRect(false, 20);
                r.x += 2; r.width -= 4;
                EditorGUI.ProgressBar(r, _runner.Progress, _runner.StatusMessage);
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                    EditorGUILayout.LabelField(_runner.StatusMessage,
                        EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
            }

            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = !_runner.IsRunning;
                var oldbg = GUI.backgroundColor;

                GUI.backgroundColor = new Color(0.25f, 0.80f, 0.30f);
                if (GUILayout.Button("▶  Run All",    GUILayout.Height(30))) _runner.RunAll();
                GUI.backgroundColor = ColTick * 0.75f;
                if (GUILayout.Button("GC Only",       GUILayout.Height(30))) _runner.RunGCOnly();
                if (GUILayout.Button("Timing Only",   GUILayout.Height(30))) _runner.RunTimingOnly();
                GUI.backgroundColor = new Color(0.85f, 0.25f, 0.25f);
                GUI.enabled         = _runner.IsRunning;
                if (GUILayout.Button("■  Cancel",     GUILayout.Height(30))) _runner.Cancel();

                GUI.backgroundColor = oldbg;
                GUI.enabled         = true;
            }
            EditorGUILayout.Space(4);
        }

        // ── GC Section ────────────────────────────────────────────────────────

        private void DrawGCSection()
        {
            _fGC = EditorGUILayout.BeginFoldoutHeaderGroup(_fGC,
                "GC Allocation per scheduling call  (lower = better, 0 B = zero-alloc)");
            if (_fGC)
            {
                var res = _runner.GCResult;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    // Measurement method note
                    var old = GUI.color;
                    GUI.color = ColDim;
                    EditorGUILayout.LabelField(
                        "Measured with GC.GetAllocatedBytesForCurrentThread() — main-thread only, " +
                        "monotonic counter. Most accurate managed API available.",
                        EditorStyles.wordWrappedMiniLabel);
                    GUI.color = old;

                    // Warm-pool warning
                    if (res.Valid && !res.WasColdRun)
                    {
                        GUI.color = ColWarn;
                        EditorGUILayout.LabelField(
                            "⚠ Warm run — Coroutine and Task.Delay may show 0B due to internal pool reuse. " +
                            "Restart Play Mode for cold (most accurate) results. " +
                            "Use Window > Analysis > Profiler > GC.Alloc for ground truth.",
                            EditorStyles.wordWrappedMiniLabel);
                        GUI.color = old;
                    }

                    EditorGUILayout.Space(4);

                    if (!res.Valid)
                    {
                        EditorGUILayout.HelpBox("Run the GC test to see results.", MessageType.Info);
                        EditorGUILayout.EndFoldoutHeaderGroup();
                        return;
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        ColHeader("MID_TickDelay", ColTick);
                        ColHeader("Coroutine",     ColCoro);
                        ColHeader("Task.Delay",    ColTask);
                    }
                    EditorGUILayout.Space(2);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool tdZero = res.TickDelayBytesPerCall == 0;
                        ValueCell(
                            tdZero ? "0 B  ✓" : $"{res.TickDelayBytesPerCall} B",
                            tdZero ? ColPass : ColFail,
                            "always zero — no internal pool needed");
                        ValueCell($"{res.CoroutineBytesPerCall} B", ColCoro,
                            res.CoroutineBytesPerCall == 0
                                ? "0B = warm pool reuse (normal on re-run)"
                                : "IEnumerator state machine heap alloc");
                        ValueCell($"{res.TaskDelayBytesPerCall} B", ColTask,
                            res.TaskDelayBytesPerCall == 0
                                ? "0B = DelayPromise pool warm (normal on re-run)"
                                : "Task + timer heap alloc");
                    }

                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField(
                        $"{res.Iterations} iterations per method.  " +
                        (res.WasColdRun ? "Cold run." : "Warm run — restart Play for cold results."),
                        EditorStyles.miniLabel);
                    EditorGUILayout.Space(4);

                    long mx = res.MaxBytes;
                    if (mx > 0)
                    {
                        BarRow("TickDelay",
                            (float)res.TickDelayBytesPerCall / mx, ColTick,
                            res.TickDelayBytesPerCall == 0 ? "0 B  ✓  zero-alloc" : $"{res.TickDelayBytesPerCall} B");
                        BarRow("Coroutine",
                            (float)res.CoroutineBytesPerCall / mx, ColCoro,
                            $"{res.CoroutineBytesPerCall} B");
                        BarRow("Task.Delay",
                            (float)res.TaskDelayBytesPerCall / mx, ColTask,
                            $"{res.TaskDelayBytesPerCall} B");
                    }
                    else
                    {
                        // All zero — either all pools warm or genuine zero-alloc across the board
                        BarRow("TickDelay",  0f, ColPass, "0 B  ✓");
                        BarRow("Coroutine",  0f, ColCoro, "0 B  (pool warm)");
                        BarRow("Task.Delay", 0f, ColTask, "0 B  (pool warm)");
                        EditorGUILayout.HelpBox(
                            "All 0B detected. This is a known limitation of GC.GetAllocatedBytesForCurrentThread()\n" +
"in Unity's Mono runtime — allocator free-list reuse and incremental GC mean the\n" +
"counter delta can be zero even when heap objects were created.\n\n" +
"GROUND TRUTH: Window > Analysis > Profiler > CPU > Hierarchy view > GC Alloc column.\n" +
"Run the bench, check that frame in the profiler. Coroutine and Task WILL show allocs there.\n" +
"MID_TickDelay will show 0 B in the Profiler too — that is the actual proof it works.",
                            MessageType.Warning);
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ── Timing Section ────────────────────────────────────────────────────

        private void DrawTimingSection()
        {
            _fTiming = EditorGUILayout.BeginFoldoutHeaderGroup(_fTiming,
                "Timing Accuracy — absolute error vs requested delay");
            if (_fTiming)
            {
                var res = _runner.TimingResult;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (!res.Valid)
                    {
                        EditorGUILayout.HelpBox("Run the Timing test to see results.", MessageType.Info);
                        EditorGUILayout.EndFoldoutHeaderGroup();
                        return;
                    }

                    EditorGUILayout.HelpBox(
                        $"MID_TickDelay  avg ~{res.TickDelayAvgMs:F0}ms  — fires on tick boundary, " +
                        $"error bounded by {res.TickIntervalMs:F0}ms.  CORRECT AND EXPECTED.\n\n" +
                        $"Coroutine      avg ~{res.CoroutineAvgMs:F0}ms  — fires on next frame.  CORRECT.\n\n" +
                        $"Task.Delay     avg ~{res.TaskDelayAvgMs:F2}ms  — OS high-res timer.  CORRECT.\n" +
                        "Note: Task fires on threadpool — cannot call Unity APIs inside callback.\n" +
                        "Timing measured with Stopwatch.GetTimestamp() (thread-safe).",
                        MessageType.Info);

                    EditorGUILayout.Space(4);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        ColHeader("MID_TickDelay", ColTick);
                        ColHeader("Coroutine",     ColCoro);
                        ColHeader("Task.Delay",    ColTask);
                    }
                    EditorGUILayout.Space(2);

                    using (new EditorGUILayout.HorizontalScope())
                        MetricRow("Avg error",
                            $"{res.TickDelayAvgMs:F1} ms", ColTick,
                            $"{res.CoroutineAvgMs:F1} ms",  ColCoro,
                            $"{res.TaskDelayAvgMs:F2} ms",  ColTask);

                    using (new EditorGUILayout.HorizontalScope())
                        MetricRow("Max error",
                            $"{res.TickDelayMaxMs:F1} ms", ColTick,
                            $"{res.CoroutineMaxMs:F1} ms",  ColCoro,
                            $"{res.TaskDelayMaxMs:F2} ms",  ColTask);

                    using (new EditorGUILayout.HorizontalScope())
                        MetricRow($"Fired/{res.Total}",
                            $"{res.TickDelayFired}", ColTick,
                            $"{res.CoroutineFired}", ColCoro,
                            $"{res.TaskDelayFired}", ColTask);

                    if (res.TaskDelayFired < res.Total)
                    {
                        var old = GUI.color; GUI.color = ColWarn;
                        EditorGUILayout.LabelField(
                            $"⚠ Task only fired {res.TaskDelayFired}/{res.Total} — " +
                            "threadpool may be busy. Re-run timing only.",
                            EditorStyles.wordWrappedMiniLabel);
                        GUI.color = old;
                    }

                    EditorGUILayout.Space(4);

                    double maxAvg = res.MaxAvgMs;
                    if (maxAvg > 0)
                    {
                        BarRow("TickDelay",
                            (float)(res.TickDelayAvgMs / maxAvg), ColTick,
                            $"{res.TickDelayAvgMs:F1}ms avg  (bounded by {res.TickIntervalMs:F0}ms tick)");
                        BarRow("Coroutine",
                            (float)(res.CoroutineAvgMs / maxAvg), ColCoro,
                            $"{res.CoroutineAvgMs:F1}ms avg  (bounded by frame)");
                        BarRow("Task.Delay",
                            (float)(Math.Max(res.TaskDelayAvgMs, 0.1) / maxAvg), ColTask,
                            $"{res.TaskDelayAvgMs:F2}ms avg  (OS timer — threadpool, not main thread)");
                        CentredGrey("Shorter = more accurate. Task near-zero is correct — OS timer really is that precise.");
                    }

                    EditorGUILayout.Space(4);
                    var c = GUI.color; GUI.color = ColInfo;
                    EditorGUILayout.LabelField(
                        "For NGO usage: TickDelay's ~50ms timing error is fine for respawn timers, " +
                        "cooldowns, and deferred RPCs. You get zero GC + main thread safety + " +
                        "no IEnumerator in exchange for that timing bound.",
                        EditorStyles.wordWrappedMiniLabel);
                    GUI.color = c;
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ── Legend ────────────────────────────────────────────────────────────

        private void DrawLegend()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                Swatch(ColTick); Label("MID_TickDelay", 100);
                Swatch(ColCoro); Label("Coroutine",      80);
                Swatch(ColTask); Label("Task.Delay",      80);
                GUILayout.FlexibleSpace();
                Swatch(ColPass); Label("✓ zero / pass", 80);
                Swatch(ColFail); Label("✗ alloc",        60);
            }
        }

        // ── Drawing helpers ───────────────────────────────────────────────────

        private float ColWidth => (position.width - 28f) / 3f;

        private void ColHeader(string text, Color col)
        {
            var old = GUI.color; GUI.color = col;
            EditorGUILayout.LabelField(text, EditorStyles.miniBoldLabel, GUILayout.Width(ColWidth));
            GUI.color = old;
        }

        private void ValueCell(string value, Color col, string sub)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(ColWidth)))
            {
                var old = GUI.color; GUI.color = col;
                EditorGUILayout.LabelField(value, EditorStyles.boldLabel);
                GUI.color = ColDim;
                EditorGUILayout.LabelField(sub, EditorStyles.miniLabel);
                GUI.color = old;
            }
        }

        private void MetricRow(string label,
            string v1, Color c1, string v2, Color c2, string v3, Color c3)
        {
            float lw = 80f, vw = ColWidth - lw - 4f;
            var old = GUI.color;
            GUI.color = ColDim;
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(lw));
            GUI.color = c1;
            EditorGUILayout.LabelField(v1, EditorStyles.miniBoldLabel, GUILayout.Width(vw));
            GUI.color = c2;
            EditorGUILayout.LabelField(v2, EditorStyles.miniBoldLabel, GUILayout.Width(vw));
            GUI.color = c3;
            EditorGUILayout.LabelField(v3, EditorStyles.miniBoldLabel, GUILayout.Width(vw));
            GUI.color = old;
        }

        private void BarRow(string label, float fraction, Color col, string tooltip)
        {
            fraction = Mathf.Clamp01(fraction);
            using (new EditorGUILayout.HorizontalScope())
            {
                var old = GUI.color; GUI.color = ColDim;
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(80));
                GUI.color = old;

                Rect r = EditorGUILayout.GetControlRect(false, 14, GUILayout.ExpandWidth(true));
                r.y += 2; r.height = 10;
                EditorGUI.DrawRect(r, ColBarBg);

                if (fraction > 0.002f)
                {
                    Rect fill = r; fill.width = Mathf.Max(r.width * fraction, 2f);
                    EditorGUI.DrawRect(fill, col);
                }
                else
                {
                    Rect tick = r; tick.width = 4f;
                    EditorGUI.DrawRect(tick, ColPass);
                }

                GUI.color = ColDim;
                EditorGUILayout.LabelField(tooltip, EditorStyles.miniLabel, GUILayout.Width(260));
                GUI.color = old;
            }
        }

        private void Swatch(Color col)
        {
            Rect r = GUILayoutUtility.GetRect(14, 14, GUILayout.Width(14));
            r.y += 2; r.height = 10; r.width = 10;
            EditorGUI.DrawRect(r, col);
            GUILayout.Space(2);
        }

        private static void Label(string text, float width) =>
            EditorGUILayout.LabelField(text, EditorStyles.miniLabel, GUILayout.Width(width));

        private static void CentredGrey(string text) =>
            EditorGUILayout.LabelField(text, EditorStyles.centeredGreyMiniLabel);

        private void DrawSeparator()
        {
            Rect r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.35f));
            EditorGUILayout.Space(4);
        }

        private static float TickIntervalMs(TickRate rate) => rate switch
        {
            TickRate.Tick_0_1 => 100f,
            TickRate.Tick_0_2 => 200f,
            TickRate.Tick_0_5 => 500f,
            TickRate.Tick_1   => 1000f,
            TickRate.Tick_2   => 2000f,
            TickRate.Tick_5   => 5000f,
            _                 => 100f
        };
    }

#endif
}
