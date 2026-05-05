// MID_TickDelayBenchmark.cs
// Runtime benchmark: MID_TickDelay vs Coroutine vs Task.Delay
// Measures GC allocation per scheduling call and timing accuracy.
//
// ══ GC MEASUREMENT ════════════════════════════════════════════════════════
//   Uses GC.GetAllocatedBytesForCurrentThread() — a monotonically increasing
//   per-thread counter that captures every managed heap allocation regardless
//   of when the GC actually runs. This is the only reliable per-call
//   allocation measurement API in Unity/Mono.
//
//   GC.GetTotalMemory() is unreliable for this purpose because Unity's
//   incremental GC pre-allocates heap in chunks and spreads collection across
//   frames — the delta between two calls is often 0 even when allocations occurred.
//
// ══ TIMING RESULTS — WHAT TO EXPECT ══════════════════════════════════════
//   Task.Delay:    ~0ms average error. Uses OS high-resolution timer
//                  (1ms precision Windows, ~0.5ms macOS). This is correct.
//
//   Coroutine:     ~0–16ms error. Fires on next frame after WaitForSeconds.
//                  Error is bounded by frame duration (1/fps seconds).
//
//   MID_TickDelay: error bounded by ONE tick interval (100ms at Tick_0_1).
//                  This is intentional — the trade-off for zero GC allocation.
//                  At Tick_0_1: max error = 100ms, avg ≈ 50ms.
//                  Use a faster tick rate to reduce error, but Tick_0_1 is
//                  the minimum recommended (see MID_TickDispatcher comments).
//
// ══ WHY USE MID_TICKDELAY DESPITE THE TIMING TRADE-OFF? ══════════════════
//   In a Netcode for GameObjects context:
//   - Task.Delay: fires on threadpool — you cannot touch Unity objects
//   - Coroutine:  requires IEnumerator, breaks RPC method signatures,
//                 allocates ~200B per StartCoroutine call
//   - MID_TickDelay: fires on main thread, zero alloc, no IEnumerator,
//                 works cleanly inside ServerRpc/ClientRpc methods
//
// ══ SCENE SETUP ═══════════════════════════════════════════════════════════
//   Add MID_TickDelayBenchRunner to a scene GameObject.
//   Open: MidManStudio > Utilities > Tests > Tick Delay Bench
//   If your scene shows "DelayBenchGCResult missing ExtensionOfNativeClass":
//     find the broken GameObject, remove the missing script component.
//     DelayBenchGCResult is a struct, not a MonoBehaviour.

using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using MidManStudio.Core.TickDispatcher;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MidManStudio.Core.Benchmarks
{
    // ── Result structs ────────────────────────────────────────────────────────
    // Plain structs — do NOT add to scene as components.

    [Serializable]
    public struct DelayBenchGCResult
    {
        public long   TickDelayBytesPerCall;
        public long   CoroutineBytesPerCall;
        public long   TaskDelayBytesPerCall;
        public int    Iterations;
        public bool   Valid;

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

        public int    Total;
        public float  TickIntervalMs;   // the configured tick interval for context
        public bool   Valid;

        public double MaxAvgMs =>
            Math.Max(TickDelayAvgMs, Math.Max(CoroutineAvgMs, TaskDelayAvgMs));
    }

    // ── Timing accumulator ────────────────────────────────────────────────────

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
    // MID_TickDelayBenchRunner — add THIS to your scene, nothing else.
    // ═════════════════════════════════════════════════════════════════════════

    public class MID_TickDelayBenchRunner : MonoBehaviour
    {
        [Header("Configuration")]
        public float    DelaySeconds     = 0.5f;
        public TickRate Rate             = TickRate.Tick_0_1;
        public int      GCIterations     = 500;
        public int      TimingIterations = 30;
        public int      WarmupCount      = 20;

        [Header("Results  (read-only — set at runtime)")]
        public DelayBenchGCResult     GCResult;
        public DelayBenchTimingResult TimingResult;
        public string                 StatusMessage = "Idle.";
        public float                  Progress;
        public bool                   IsRunning;

        // Pre-allocated zero-alloc delegate.
        // Static readonly field = delegate object created once at type load.
        // Passing this field reference to After() costs zero GC on any Unity version.
        private static readonly Action _doNothing = DoNothing;

        private Coroutine _active;

        // ── Public API ────────────────────────────────────────────────────────

        public void RunAll()
        {
            if (IsRunning) return;
            StopActive();
            GCResult     = default;
            TimingResult = default;
            _active      = StartCoroutine(RunAllCo());
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
            IsRunning     = false;
            StatusMessage = "Cancelled.";
            Progress      = 0f;
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
            yield return StartCoroutine(WarmUp());
            yield return StartCoroutine(GCInner());
            yield return StartCoroutine(TimingInner());
            StatusMessage = "All tests complete.";
            Progress      = 1f;
            IsRunning     = false;
        }

        private IEnumerator RunGCOnlyCo()
        {
            IsRunning = true;
            yield return StartCoroutine(WarmUp());
            yield return StartCoroutine(GCInner());
            StatusMessage = "GC test complete.";
            Progress      = 1f;
            IsRunning     = false;
        }

        private IEnumerator RunTimingOnlyCo()
        {
            IsRunning = true;
            yield return StartCoroutine(WarmUp());
            yield return StartCoroutine(TimingInner());
            StatusMessage = "Timing test complete.";
            Progress      = 1f;
            IsRunning     = false;
        }

        // ── Warm-up ───────────────────────────────────────────────────────────
        // Forces pool initialisation and JIT compilation before measurement.
        // Uses _doNothing (pre-allocated) so warmup itself doesn't pollute results.

        private IEnumerator WarmUp()
        {
            StatusMessage = $"Warming up ({WarmupCount} cycles)…";
            Progress      = 0f;

            for (int i = 0; i < WarmupCount; i++)
                MID_TickDelay.After(DelaySeconds, _doNothing, Rate);

            float waitTime = DelaySeconds + TickIntervalSec() * 4f;
            yield return new WaitForSeconds(waitTime);
            MID_TickDelay.CancelAll();

            // Also warm up coroutine path
            for (int i = 0; i < 5; i++)
                StartCoroutine(DummyWait(0.05f));
            yield return new WaitForSeconds(0.2f);

            StatusMessage = "Warm-up done. Pool and JIT ready.";
            yield return null;
            yield return null;
        }

        // ── GC test ───────────────────────────────────────────────────────────
        //
        // Uses GC.GetAllocatedBytesForCurrentThread() — a monotonically increasing
        // per-thread allocation counter. Unlike GC.GetTotalMemory(), it is not
        // affected by GC runs or heap pre-allocation chunking. Delta between two
        // readings = exact bytes this thread allocated in that window.
        //
        // Measurement is done inside a single frame (no yield between before/after)
        // so no other Unity systems can pollute the thread's allocation counter.
        //
        // COROUTINE NOTE: StartCoroutine allocates per-call even with static methods
        // because the IEnumerator state machine object is heap-allocated on creation.
        // WaitForSeconds is also a heap object. This shows up correctly here.

        private IEnumerator GCInner()
        {
            int n = GCIterations;

            // ── MID_TickDelay ─────────────────────────────────────────────────
            StatusMessage = $"GC test — MID_TickDelay ({n} calls)…";

            // Force full GC before measurement so counter baseline is stable
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            yield return null;
            yield return null;

            // Read before — thread allocation counter (monotonically increasing)
            long tdBefore = GetThreadAllocBytes();

            // Measure N calls in a single frame — no yield, no GC opportunity
            for (int i = 0; i < n; i++)
                MID_TickDelay.After(DelaySeconds, _doNothing, Rate);

            long tdAfter       = GetThreadAllocBytes();
            long tdTotalBytes  = tdAfter - tdBefore;
            long tdPerCall     = tdTotalBytes / n;

            // Let delays fire so they don't interfere with next test
            yield return new WaitForSeconds(DelaySeconds + TickIntervalSec() * 3f);
            MID_TickDelay.CancelAll();
            yield return null;
            Progress = 0.33f;

            // ── Coroutine ──────────────────────────────────────────────────────
            StatusMessage = $"GC test — Coroutine ({n} calls)…";
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            yield return null;
            yield return null;

            long coBefore = GetThreadAllocBytes();
            for (int i = 0; i < n; i++)
                StartCoroutine(DummyWait(DelaySeconds));
            long coAfter      = GetThreadAllocBytes();
            long coTotalBytes = coAfter - coBefore;
            long coPerCall    = coTotalBytes / n;

            yield return new WaitForSeconds(DelaySeconds + 0.3f);
            yield return null;
            Progress = 0.66f;

            // ── Task.Delay ────────────────────────────────────────────────────
            StatusMessage = $"GC test — Task.Delay ({n} calls)…";
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            yield return null;
            yield return null;

            var    taskDelay  = TimeSpan.FromSeconds(DelaySeconds);
            long   taskBefore = GetThreadAllocBytes();
            for (int i = 0; i < n; i++)
                _ = Task.Delay(taskDelay);
            long taskAfter      = GetThreadAllocBytes();
            long taskTotalBytes = taskAfter - taskBefore;
            long taskPerCall    = taskTotalBytes / n;

            yield return null;
            Progress = 1f;

            GCResult = new DelayBenchGCResult
            {
                TickDelayBytesPerCall = tdPerCall,
                CoroutineBytesPerCall = coPerCall,
                TaskDelayBytesPerCall = taskPerCall,
                Iterations            = n,
                Valid                 = true
            };

            StatusMessage =
                $"GC — TickDelay: {FormatBytes(tdPerCall)} | " +
                $"Coroutine: {FormatBytes(coPerCall)} | " +
                $"Task.Delay: {FormatBytes(taskPerCall)}";
        }

        // ── Timing test ───────────────────────────────────────────────────────
        // Each method measures the actual error vs requested delay.
        //
        // EXPECTED RESULTS:
        //   Task.Delay   ≈ 0–1ms error   — OS timer, fires on threadpool
        //   Coroutine    ≈ 0–16ms error  — fires on next frame boundary
        //   MID_TickDelay ≈ 0–100ms error — fires on next tick boundary (at Tick_0_1)
        //
        // MID_TickDelay timing is INTENTIONALLY worse. The contract is:
        //   "fire within one tick interval of the requested time, with zero GC,
        //    on the main thread, without requiring IEnumerator."

        private IEnumerator TimingInner()
        {
            int   n      = TimingIterations;
            float tickMs = TickIntervalSec() * 1000f;

            // ── MID_TickDelay ─────────────────────────────────────────────────
            StatusMessage = $"Timing — MID_TickDelay (0/{n})…";
            var tdAcc = new TimingAccumulator();

            for (int i = 0; i < n; i++)
            {
                double capturedSched = RealtimeMs();
                float  capturedReq   = DelaySeconds;
                MID_TickDelay.After(capturedReq, () =>
                {
                    // Error = how far we landed from the requested time
                    double actualMs   = RealtimeMs() - capturedSched;
                    double requestedMs = capturedReq * 1000.0;
                    tdAcc.Record(Math.Abs(actualMs - requestedMs));
                }, Rate);

                Progress = (float)i / (n * 3f);
                if (i % 5 == 0)
                    StatusMessage = $"Timing — MID_TickDelay ({i}/{n})…";
                yield return null;
            }

            // Wait long enough for all TickDelay callbacks to fire
            yield return new WaitForSeconds(DelaySeconds + TickIntervalSec() * 3f);
            Progress = 0.35f;

            // ── Coroutine ──────────────────────────────────────────────────────
            StatusMessage = $"Timing — Coroutine (0/{n})…";
            var coAcc = new TimingAccumulator();

            for (int i = 0; i < n; i++)
            {
                StartCoroutine(TimedWait(DelaySeconds, RealtimeMs(), coAcc));
                Progress = 0.33f + (float)i / (n * 3f);
                if (i % 5 == 0)
                    StatusMessage = $"Timing — Coroutine ({i}/{n})…";
                yield return null;
            }
            yield return new WaitForSeconds(DelaySeconds + 0.5f);
            Progress = 0.67f;

            // ── Task.Delay ────────────────────────────────────────────────────
            // Task fires on threadpool — use thread-safe accumulation.
            // Note: Task.Delay near-0ms error is CORRECT — OS timer is that accurate.
            StatusMessage = $"Timing — Task.Delay (0/{n})…";
            var taskErrors   = new double[n];
            int taskFiredRaw = 0;

            for (int i = 0; i < n; i++)
            {
                int    idx          = i;
                double capturedSched = RealtimeMs();
                float  capturedReq   = DelaySeconds;
                _ = Task.Delay(TimeSpan.FromSeconds(capturedReq)).ContinueWith(_ =>
                {
                    double actualMs    = RealtimeMs() - capturedSched;
                    double requestedMs = capturedReq * 1000.0;
                    taskErrors[idx] = Math.Abs(actualMs - requestedMs);
                    Interlocked.Increment(ref taskFiredRaw);
                });

                Progress = 0.66f + (float)i / (n * 3f);
                if (i % 5 == 0)
                    StatusMessage = $"Timing — Task.Delay ({i}/{n})…";
                yield return null;
            }
            yield return new WaitForSeconds(DelaySeconds + 0.8f);

            double taskTotal = 0, taskMax = 0;
            for (int i = 0; i < n; i++)
            {
                taskTotal += taskErrors[i];
                if (taskErrors[i] > taskMax) taskMax = taskErrors[i];
            }
            int taskFired = Volatile.Read(ref taskFiredRaw);

            TimingResult = new DelayBenchTimingResult
            {
                TickDelayAvgMs  = tdAcc.AvgMs,
                TickDelayMaxMs  = tdAcc.MaxErr,
                TickDelayFired  = tdAcc.Fired,
                CoroutineAvgMs  = coAcc.AvgMs,
                CoroutineMaxMs  = coAcc.MaxErr,
                CoroutineFired  = coAcc.Fired,
                TaskDelayAvgMs  = taskFired > 0 ? taskTotal / taskFired : 0,
                TaskDelayMaxMs  = taskMax,
                TaskDelayFired  = taskFired,
                Total           = n,
                TickIntervalMs  = tickMs,
                Valid           = true
            };

            StatusMessage =
                $"Timing — TD avg:{TimingResult.TickDelayAvgMs:F1}ms | " +
                $"Coro avg:{TimingResult.CoroutineAvgMs:F1}ms | " +
                $"Task avg:{TimingResult.TaskDelayAvgMs:F1}ms";
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void DoNothing() { }

        private IEnumerator DummyWait(float seconds)
        {
            yield return new WaitForSeconds(seconds);
        }

        private IEnumerator TimedWait(float seconds, double schedMs, TimingAccumulator acc)
        {
            yield return new WaitForSeconds(seconds);
            double actualMs    = RealtimeMs() - schedMs;
            double requestedMs = seconds * 1000.0;
            acc.Record(Math.Abs(actualMs - requestedMs));
        }

        /// <summary>
        /// Returns total bytes allocated by this thread since thread start.
        /// Monotonically increasing — take delta between two readings for interval alloc.
        /// This is the correct API for per-call allocation measurement in Unity/Mono.
        /// </summary>
        private static long GetThreadAllocBytes()
        {
            // GC.GetAllocatedBytesForCurrentThread() was added in .NET 4.7.2 / .NET Core.
            // Unity 2022.3 with .NET Standard 2.1 has it available.
            // If it throws (very old Unity), fall back to GetTotalMemory (less accurate).
            try
            {
                return GC.GetAllocatedBytesForCurrentThread();
            }
            catch
            {
                return GC.GetTotalMemory(false);
            }
        }

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

        private static double RealtimeMs() =>
            Time.realtimeSinceStartupAsDouble * 1000.0;

        private static string FormatBytes(long bytes) =>
            bytes == 0 ? "0 B  ✓ zero-alloc" : $"{bytes} B";
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Editor Window
    // ═════════════════════════════════════════════════════════════════════════

#if UNITY_EDITOR

    public class MID_TickDelayBenchWindow : EditorWindow
    {
        private MID_TickDelayBenchRunner _runner;
        private Vector2                  _scroll;
        private bool _fConfig  = true;
        private bool _fGC      = true;
        private bool _fTiming  = true;
        private bool _fContext = true;

        private static readonly Color ColTick  = new Color(0.28f, 0.90f, 0.45f, 1f);
        private static readonly Color ColCoro  = new Color(0.40f, 0.65f, 1.00f, 1f);
        private static readonly Color ColTask  = new Color(1.00f, 0.70f, 0.25f, 1f);
        private static readonly Color ColBarBg = new Color(0.15f, 0.15f, 0.15f, 0.5f);
        private static readonly Color ColDim   = new Color(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color ColPass  = new Color(0.28f, 0.95f, 0.45f, 1f);
        private static readonly Color ColFail  = new Color(1.00f, 0.35f, 0.35f, 1f);
        private static readonly Color ColInfo  = new Color(0.60f, 0.80f, 1.00f, 1f);

        [MenuItem("MidManStudio/Utilities/Tests/Tick Delay Bench")]
        public static void Open()
        {
            var w = GetWindow<MID_TickDelayBenchWindow>("Tick Delay Bench");
            w.minSize = new Vector2(520, 580);
        }

        private void OnEnable()
        {
            EditorApplication.update += Repaint;
            TryFindRunner();
        }

        private void OnDisable() => EditorApplication.update -= Repaint;

        private void TryFindRunner()
        {
            if (_runner == null)
                _runner = FindObjectOfType<MID_TickDelayBenchRunner>();
        }

        private void OnGUI()
        {
            TryFindRunner();
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

        // ── Why use MID_TickDelay ─────────────────────────────────────────────

        private void DrawContext()
        {
            _fContext = EditorGUILayout.BeginFoldoutHeaderGroup(_fContext,
                "Why MID_TickDelay? — trade-off summary");
            if (_fContext)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    var old = GUI.color;

                    GUI.color = ColPass;
                    EditorGUILayout.LabelField("MID_TickDelay", EditorStyles.miniBoldLabel);
                    GUI.color = old;
                    EditorGUILayout.LabelField(
                        "✓ Zero GC allocation on main thread\n" +
                        "✓ No IEnumerator — works inside ServerRpc/ClientRpc directly\n" +
                        "✓ Cancellable via TickDelayHandle\n" +
                        "✗ Timing error bounded by one tick interval (~50ms avg at Tick_0_1)",
                        EditorStyles.wordWrappedMiniLabel);

                    EditorGUILayout.Space(4);

                    GUI.color = ColCoro;
                    EditorGUILayout.LabelField("Coroutine", EditorStyles.miniBoldLabel);
                    GUI.color = old;
                    EditorGUILayout.LabelField(
                        "✓ Frame-accurate timing (fires next frame)\n" +
                        "✗ Allocates ~200–400B per StartCoroutine call\n" +
                        "✗ Requires IEnumerator — breaks RPC method signatures in NGO\n" +
                        "✗ Tied to MonoBehaviour lifecycle",
                        EditorStyles.wordWrappedMiniLabel);

                    EditorGUILayout.Space(4);

                    GUI.color = ColTask;
                    EditorGUILayout.LabelField("Task.Delay", EditorStyles.miniBoldLabel);
                    GUI.color = old;
                    EditorGUILayout.LabelField(
                        "✓ Highly accurate timing (OS high-res timer, ~1ms error)\n" +
                        "✗ Fires on threadpool — cannot touch Unity objects\n" +
                        "✗ Allocates per call\n" +
                        "✗ No built-in cancellation without CancellationToken (extra alloc)",
                        EditorStyles.wordWrappedMiniLabel);
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(2);
        }

        // ── Play mode guard ───────────────────────────────────────────────────

        private void DrawPlayModeGuard()
        {
            EditorGUILayout.Space(4);
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play Mode to run benchmarks.",
                    MessageType.Info);
                return;
            }

            if (_runner == null)
            {
                EditorGUILayout.HelpBox(
                    "No MID_TickDelayBenchRunner found.\n\n" +
                    "If you see 'DelayBenchGCResult missing ExtensionOfNativeClass':\n" +
                    "Find the GameObject with the missing script, remove that component.\n" +
                    "DelayBenchGCResult is a struct, not a MonoBehaviour.",
                    MessageType.Warning);

                if (GUILayout.Button("Add Runner to Scene", GUILayout.Height(28)))
                {
                    var go = new GameObject("[TickDelayBenchRunner]");
                    _runner = go.AddComponent<MID_TickDelayBenchRunner>();
                    Selection.activeGameObject = go;
                    Undo.RegisterCreatedObjectUndo(go, "Add Tick Delay Bench Runner");
                }
            }
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
                        new GUIContent("Delay (s)"),
                        _runner.DelaySeconds, 0.1f, 5f);

                    _runner.Rate = (TickRate)EditorGUILayout.EnumPopup(
                        new GUIContent("Tick Rate",
                            "Rate used by MID_TickDelay. Minimum = Tick_0_1.\n" +
                            "Timing error ≈ 0 to one full tick interval."),
                        _runner.Rate);

                    _runner.GCIterations = EditorGUILayout.IntSlider(
                        new GUIContent("GC Iterations",
                            "Scheduling calls per method.\n" +
                            "Uses GC.GetAllocatedBytesForCurrentThread() — accurate per call."),
                        _runner.GCIterations, 50, 1000);

                    _runner.TimingIterations = EditorGUILayout.IntSlider(
                        new GUIContent("Timing Iterations"),
                        _runner.TimingIterations, 10, 100);

                    _runner.WarmupCount = EditorGUILayout.IntSlider(
                        new GUIContent("Warmup"),
                        _runner.WarmupCount, 10, 50);

                    GUI.enabled = true;

                    float tickMs = TickIntervalMs(_runner.Rate);
                    EditorGUILayout.HelpBox(
                        $"Tick interval: {tickMs:F0}ms   " +
                        $"Expected TickDelay timing error: 0–{tickMs:F0}ms   " +
                        $"Avg: ~{tickMs * 0.5f:F0}ms",
                        MessageType.None);
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(2);
        }

        // ── Run buttons ───────────────────────────────────────────────────────

        private void DrawRunButtons()
        {
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
                var old = GUI.backgroundColor;

                GUI.backgroundColor = new Color(0.25f, 0.80f, 0.30f);
                if (GUILayout.Button("▶  Run All", GUILayout.Height(30)))
                    _runner.RunAll();

                GUI.backgroundColor = ColTick * 0.75f;
                if (GUILayout.Button("GC Only",     GUILayout.Height(30))) _runner.RunGCOnly();
                if (GUILayout.Button("Timing Only", GUILayout.Height(30))) _runner.RunTimingOnly();

                GUI.backgroundColor = new Color(0.85f, 0.25f, 0.25f);
                GUI.enabled         = _runner.IsRunning;
                if (GUILayout.Button("■  Cancel", GUILayout.Height(30))) _runner.Cancel();

                GUI.backgroundColor = old;
                GUI.enabled         = true;
            }
            EditorGUILayout.Space(4);
        }

        // ── GC Section ────────────────────────────────────────────────────────

        private void DrawGCSection()
        {
            _fGC = EditorGUILayout.BeginFoldoutHeaderGroup(_fGC,
                "GC Allocation per scheduling call  (lower = better)");

            if (_fGC)
            {
                var res = _runner.GCResult;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    // Method info
                    var old = GUI.color;
                    GUI.color = ColDim;
                    EditorGUILayout.LabelField(
                        "Measured with GC.GetAllocatedBytesForCurrentThread() — per-thread " +
                        "monotonic counter, accurate per call regardless of GC timing.",
                        EditorStyles.wordWrappedMiniLabel);
                    GUI.color = old;

                    EditorGUILayout.Space(4);

                    if (!res.Valid)
                    {
                        EditorGUILayout.HelpBox("Run the GC test to see results.", MessageType.Info);
                        EditorGUILayout.EndFoldoutHeaderGroup();
                        return;
                    }

                    // Headers
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        ColHeader("MID_TickDelay", ColTick);
                        ColHeader("Coroutine",     ColCoro);
                        ColHeader("Task.Delay",    ColTask);
                    }
                    EditorGUILayout.Space(2);

                    // Values
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool tdZero = res.TickDelayBytesPerCall == 0;
                        ValueCell(
                            tdZero ? "0 B  ✓" : $"{res.TickDelayBytesPerCall} B",
                            tdZero ? ColPass : ColFail,
                            tdZero ? "zero-alloc after warmup" : "alloc detected");
                        ValueCell($"{res.CoroutineBytesPerCall} B", ColCoro,
                            "IEnumerator state machine");
                        ValueCell($"{res.TaskDelayBytesPerCall} B", ColTask,
                            "Task + timer state");
                    }

                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField(
                        $"Iterations: {res.Iterations} per method.",
                        EditorStyles.miniLabel);
                    EditorGUILayout.Space(4);

                    // Bar chart
                    long mx = res.MaxBytes;
                    if (mx > 0)
                    {
                        BarRow("TickDelay",
                            (float)res.TickDelayBytesPerCall / mx, ColTick,
                            res.TickDelayBytesPerCall == 0 ? "0 B  ✓" : $"{res.TickDelayBytesPerCall} B");
                        BarRow("Coroutine",
                            (float)res.CoroutineBytesPerCall / mx, ColCoro,
                            $"{res.CoroutineBytesPerCall} B");
                        BarRow("Task.Delay",
                            (float)res.TaskDelayBytesPerCall / mx, ColTask,
                            $"{res.TaskDelayBytesPerCall} B");
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(
                            "All methods show 0 B. This is unusual — the pre-allocated " +
                            "pool may have absorbed everything. Try bumping GC Iterations " +
                            "to 1000 or check in the Unity Profiler (GC.Alloc column) " +
                            "for the ground truth. The Profiler is always more accurate " +
                            "than any in-game measurement.",
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

                    // Explain each result first so user knows what to expect
                    EditorGUILayout.HelpBox(
                        $"MID_TickDelay:  fires on next tick boundary → error 0–{res.TickIntervalMs:F0}ms " +
                        $"(avg ~{res.TickIntervalMs * 0.5f:F0}ms at {_runner.Rate}).  THIS IS CORRECT.\n\n" +
                        "Coroutine:  fires on next frame → error 0–16ms at 60fps.  THIS IS CORRECT.\n\n" +
                        "Task.Delay:  OS high-res timer → ~0–1ms error.  THIS IS CORRECT.\n" +
                        "Task fires on threadpool — you cannot touch Unity objects inside the callback.",
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
                            $"{res.TaskDelayAvgMs:F1} ms",  ColTask);

                    using (new EditorGUILayout.HorizontalScope())
                        MetricRow("Max error",
                            $"{res.TickDelayMaxMs:F1} ms", ColTick,
                            $"{res.CoroutineMaxMs:F1} ms",  ColCoro,
                            $"{res.TaskDelayMaxMs:F1} ms",  ColTask);

                    using (new EditorGUILayout.HorizontalScope())
                        MetricRow($"Fired/{res.Total}",
                            $"{res.TickDelayFired}", ColTick,
                            $"{res.CoroutineFired}", ColCoro,
                            $"{res.TaskDelayFired}", ColTask);

                    EditorGUILayout.Space(4);

                    // Bars — shorter = more accurate
                    double maxAvg = res.MaxAvgMs;
                    if (maxAvg > 0)
                    {
                        BarRow("TickDelay",
                            (float)(res.TickDelayAvgMs / maxAvg), ColTick,
                            $"{res.TickDelayAvgMs:F1}ms avg  (bounded by tick interval)");
                        BarRow("Coroutine",
                            (float)(res.CoroutineAvgMs / maxAvg), ColCoro,
                            $"{res.CoroutineAvgMs:F1}ms avg  (bounded by frame)");
                        BarRow("Task.Delay",
                            (float)(Math.Max(res.TaskDelayAvgMs, 0.01) / maxAvg), ColTask,
                            $"{res.TaskDelayAvgMs:F2}ms avg  (OS timer — main thread unsafe)");
                        CentredGrey("Shorter bar = more accurate.  " +
                            "Task.Delay near-zero is correct — OS timer really is that precise.");
                    }

                    EditorGUILayout.Space(4);

                    // Colour-coded bottom line
                    var old = GUI.color;
                    GUI.color = ColInfo;
                    EditorGUILayout.LabelField(
                        "Bottom line for Netcode for GameObjects:",
                        EditorStyles.miniBoldLabel);
                    GUI.color = old;
                    EditorGUILayout.LabelField(
                        "MID_TickDelay is the only option that gives you: zero GC + main thread " +
                        "execution + no IEnumerator requirement. The timing trade-off (~50ms avg at " +
                        "Tick_0_1) is acceptable for network-side delays like respawn timers, cooldowns, " +
                        "and deferred RPCs where sub-frame precision is never needed.",
                        EditorStyles.wordWrappedMiniLabel);
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
                Swatch(ColPass); Label("✓ zero / pass",  80);
                Swatch(ColFail); Label("✗ alloc",         60);
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
                EditorGUILayout.LabelField(tooltip, EditorStyles.miniLabel, GUILayout.Width(240));
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
