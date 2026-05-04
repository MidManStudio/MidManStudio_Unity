// MID_TickDelayBench.cs
// Runtime benchmark comparing MID_TickDelay, Coroutine, and Task.Delay.
// Measures GC allocation per call and timing accuracy for all three approaches.
//
// SETUP:
//   Enter Play Mode, then open the window via:
//   MidManStudio > Utilities > Tests > Tick Delay Bench
//   The window will offer to add the runner to the scene automatically.
//
// WHAT IS MEASURED:
//   GC Allocation  — bytes allocated per scheduling call (lower = better).
//                    MID_TickDelay targets 0 bytes after warm-up.
//   Timing Accuracy — average and max deviation from the requested delay.
//                    MID_TickDelay is bounded by one tick interval.
//                    Coroutine and Task.Delay are frame/OS bounded but allocate every call.

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
    // ── Shared result types ───────────────────────────────────────────────────

    [Serializable]
    public struct DelayBenchGCResult
    {
        public long   TickDelayBytes;
        public long   CoroutineBytes;
        public long   TaskDelayBytes;
        public bool   Valid;

        public long   MaxBytes =>
            Math.Max(TickDelayBytes, Math.Max(CoroutineBytes, TaskDelayBytes));
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
        public bool   Valid;

        public double MaxAvgMs =>
            Math.Max(TickDelayAvgMs, Math.Max(CoroutineAvgMs, TaskDelayAvgMs));
    }

    // ── Accumulator helper (avoids ref params in coroutines) ──────────────────

    internal sealed class TimingAccumulator
    {
        public double TotalErr;
        public double MaxErr;
        public int    Fired;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Runtime MonoBehaviour — manages coroutine execution and result storage
    // ═════════════════════════════════════════════════════════════════════════

    public class MID_TickDelayBenchRunner : MonoBehaviour
    {
        [Header("Configuration")]
        public float    DelaySeconds     = 0.5f;
        public TickRate Rate             = TickRate.Tick_0_1;
        public int      GCIterations     = 100;
        public int      TimingIterations = 50;
        public int      WarmupCount      = 10;

        [Header("Results (read-only)")]
        public DelayBenchGCResult     GCResult;
        public DelayBenchTimingResult TimingResult;
        public string                 StatusMessage = "Idle.";
        public float                  Progress;
        public bool                   IsRunning;

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
        }

        // ── Master coroutines ─────────────────────────────────────────────────

        private IEnumerator RunAllCo()
        {
            IsRunning = true;
            yield return StartCoroutine(WarmUp());
            yield return StartCoroutine(GCInner());
            yield return StartCoroutine(TimingInner());
            StatusMessage = "Complete.";
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

        private IEnumerator WarmUp()
        {
            StatusMessage = $"Warming up ({WarmupCount} cycles)…";
            Progress      = 0f;

            int fired = 0;
            for (int i = 0; i < WarmupCount; i++)
                MID_TickDelay.After(DelaySeconds, () => fired++, Rate);

            yield return new WaitForSeconds(DelaySeconds + TickIntervalSec() * 3f);
            StatusMessage = $"Warmup complete ({fired}/{WarmupCount} fired).";
            yield return null;
        }

        // ── GC inner ──────────────────────────────────────────────────────────
        // Each measurement: force full GC, settle one frame, read before/after
        // in the SAME frame so Unity's background allocations don't pollute the delta.

        private IEnumerator GCInner()
        {
            // ---- MID_TickDelay ----
            StatusMessage = $"GC — MID_TickDelay ({GCIterations} calls)…";
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            yield return null; yield return null;

            long before = GC.GetTotalMemory(false);
            for (int i = 0; i < GCIterations; i++)
                MID_TickDelay.After(DelaySeconds, DoNothing, Rate);
            long after = GC.GetTotalMemory(false);

            long tickPerCall = Math.Max(0L, after - before) / GCIterations;

            yield return new WaitForSeconds(DelaySeconds + TickIntervalSec() * 2f);
            MID_TickDelay.CancelAll();
            yield return null;

            Progress = 0.33f;

            // ---- Coroutine ----
            StatusMessage = $"GC — Coroutine ({GCIterations} calls)…";
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            yield return null; yield return null;

            before = GC.GetTotalMemory(false);
            for (int i = 0; i < GCIterations; i++)
                StartCoroutine(DummyWait(DelaySeconds));
            after = GC.GetTotalMemory(false);

            long coroPerCall = Math.Max(0L, after - before) / GCIterations;

            yield return new WaitForSeconds(DelaySeconds + 0.3f);

            Progress = 0.66f;

            // ---- Task.Delay ----
            StatusMessage = $"GC — Task.Delay ({GCIterations} calls)…";
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            yield return null; yield return null;

            before = GC.GetTotalMemory(false);
            for (int i = 0; i < GCIterations; i++)
                _ = Task.Delay(TimeSpan.FromSeconds(DelaySeconds));
            after = GC.GetTotalMemory(false);

            long taskPerCall = Math.Max(0L, after - before) / GCIterations;

            yield return null;

            Progress = 1f;

            GCResult = new DelayBenchGCResult
            {
                TickDelayBytes = tickPerCall,
                CoroutineBytes = coroPerCall,
                TaskDelayBytes = taskPerCall,
                Valid          = true
            };

            StatusMessage =
                $"GC — TickDelay: {tickPerCall}B | Coroutine: {coroPerCall}B | Task: {taskPerCall}B";
        }

        // ── Timing inner ──────────────────────────────────────────────────────

        private IEnumerator TimingInner()
        {
            int n = TimingIterations;

            // ---- MID_TickDelay ----
            StatusMessage = $"Timing — MID_TickDelay (0/{n})…";
            var tdAcc = new TimingAccumulator();

            for (int i = 0; i < n; i++)
            {
                double sched = RealtimeMs();
                float  req   = DelaySeconds;
                MID_TickDelay.After(req, () =>
                {
                    double err = Math.Abs(RealtimeMs() - sched - req * 1000.0);
                    tdAcc.TotalErr += err;
                    if (err > tdAcc.MaxErr) tdAcc.MaxErr = err;
                    tdAcc.Fired++;
                }, Rate);

                Progress = (float)i / (n * 3f);
                if (i % 5 == 0)
                    StatusMessage = $"Timing — MID_TickDelay ({i}/{n})…";
                yield return null;
            }
            yield return new WaitForSeconds(DelaySeconds + TickIntervalSec() * 3f);

            Progress = 0.35f;

            // ---- Coroutine ----
            StatusMessage = $"Timing — Coroutine (0/{n})…";
            var coAcc = new TimingAccumulator();

            for (int i = 0; i < n; i++)
            {
                double sched = RealtimeMs();
                float  req   = DelaySeconds;
                StartCoroutine(TimedWait(req, sched, coAcc));

                Progress = 0.33f + (float)i / (n * 3f);
                if (i % 5 == 0)
                    StatusMessage = $"Timing — Coroutine ({i}/{n})…";
                yield return null;
            }
            yield return new WaitForSeconds(DelaySeconds + 0.5f);

            Progress = 0.67f;

            // ---- Task.Delay ----
            // ContinueWith fires on a threadpool thread; use per-slot arrays to
            // avoid locking, then aggregate on the main thread after waiting.
            StatusMessage = $"Timing — Task.Delay (0/{n})…";
            double[] taskErrors  = new double[n];
            int      taskFiredRaw = 0;

            for (int i = 0; i < n; i++)
            {
                int    idx   = i;
                double sched = RealtimeMs();
                float  req   = DelaySeconds;
                _ = Task.Delay(TimeSpan.FromSeconds(req)).ContinueWith(_ =>
                {
                    taskErrors[idx] = Math.Abs(RealtimeMs() - sched - req * 1000.0);
                    Interlocked.Increment(ref taskFiredRaw);
                });

                Progress = 0.66f + (float)i / (n * 3f);
                if (i % 5 == 0)
                    StatusMessage = $"Timing — Task.Delay ({i}/{n})…";
                yield return null;
            }
            yield return new WaitForSeconds(DelaySeconds + 0.5f);

            // Aggregate task results on main thread
            double taskTotal = 0, taskMax = 0;
            for (int i = 0; i < n; i++)
            {
                taskTotal += taskErrors[i];
                if (taskErrors[i] > taskMax) taskMax = taskErrors[i];
            }
            int taskFired = Volatile.Read(ref taskFiredRaw);

            TimingResult = new DelayBenchTimingResult
            {
                TickDelayAvgMs  = tdAcc.Fired > 0 ? tdAcc.TotalErr / tdAcc.Fired : 0,
                TickDelayMaxMs  = tdAcc.MaxErr,
                TickDelayFired  = tdAcc.Fired,
                CoroutineAvgMs  = coAcc.Fired > 0 ? coAcc.TotalErr / coAcc.Fired : 0,
                CoroutineMaxMs  = coAcc.MaxErr,
                CoroutineFired  = coAcc.Fired,
                TaskDelayAvgMs  = taskFired > 0 ? taskTotal / taskFired : 0,
                TaskDelayMaxMs  = taskMax,
                TaskDelayFired  = taskFired,
                Total           = n,
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
            double err = Math.Abs(RealtimeMs() - schedMs - seconds * 1000.0);
            acc.TotalErr += err;
            if (err > acc.MaxErr) acc.MaxErr = err;
            acc.Fired++;
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

        // ── Colour palette ────────────────────────────────────────────────────
        private static readonly Color ColTick   = new Color(0.28f, 0.90f, 0.45f, 1f);
        private static readonly Color ColCoro   = new Color(0.40f, 0.65f, 1.00f, 1f);
        private static readonly Color ColTask   = new Color(1.00f, 0.70f, 0.25f, 1f);
        private static readonly Color ColBarBg  = new Color(0.15f, 0.15f, 0.15f, 0.5f);
        private static readonly Color ColDim    = new Color(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color ColPass   = new Color(0.28f, 0.95f, 0.45f, 1f);
        private static readonly Color ColFail   = new Color(1.00f, 0.35f, 0.35f, 1f);

        [MenuItem("MidManStudio/Utilities/Tests/Tick Delay Bench")]
        public static void Open()
        {
            var w = GetWindow<MID_TickDelayBenchWindow>("Tick Delay Bench");
            w.minSize = new Vector2(480, 500);
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

        // ── Main GUI ──────────────────────────────────────────────────────────

        private void OnGUI()
        {
            TryFindRunner();
            DrawToolbar();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawPlayModeGuard();

            if (Application.isPlaying)
            {
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

        // ── Play-mode guard / runner finder ───────────────────────────────────

        private void DrawPlayModeGuard()
        {
            EditorGUILayout.Space(4);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play Mode to run benchmarks.\n" +
                    "Benchmarks use coroutines and require an active Unity scene.",
                    MessageType.Info);
                return;
            }

            if (_runner == null)
            {
                EditorGUILayout.HelpBox(
                    "No MID_TickDelayBenchRunner found in the open scene.",
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
            if (_runner == null) return;

            _fConfig = EditorGUILayout.BeginFoldoutHeaderGroup(_fConfig, "Configuration");
            if (_fConfig)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    GUI.enabled = !_runner.IsRunning;

                    _runner.DelaySeconds     = EditorGUILayout.Slider(
                        new GUIContent("Delay (s)", "Duration each delayed action waits before firing."),
                        _runner.DelaySeconds, 0.1f, 5f);

                    _runner.Rate = (TickRate)EditorGUILayout.EnumPopup(
                        new GUIContent("Tick Rate",
                            "Rate used by MID_TickDelay. Determines maximum timing error."),
                        _runner.Rate);

                    _runner.GCIterations = EditorGUILayout.IntSlider(
                        new GUIContent("GC Iterations",
                            "Number of calls to schedule per method in the GC test."),
                        _runner.GCIterations, 10, 500);

                    _runner.TimingIterations = EditorGUILayout.IntSlider(
                        new GUIContent("Timing Iterations",
                            "Number of delays fired per method in the timing accuracy test."),
                        _runner.TimingIterations, 10, 200);

                    _runner.WarmupCount = EditorGUILayout.IntSlider(
                        new GUIContent("Warmup",
                            "Cycles run before measurement to prime the JIT and pool."),
                        _runner.WarmupCount, 5, 50);

                    GUI.enabled = true;

                    float tickMs = TickIntervalMs(_runner.Rate);
                    EditorGUILayout.HelpBox(
                        $"At {_runner.Rate} the tick fires every {tickMs:F0}ms — " +
                        $"maximum possible MID_TickDelay error = {tickMs:F0}ms.",
                        MessageType.None);
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(2);
        }

        // ── Run buttons ───────────────────────────────────────────────────────

        private void DrawRunButtons()
        {
            if (_runner == null) return;

            // Progress bar
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
                if (GUILayout.Button("GC Only", GUILayout.Height(30)))
                    _runner.RunGCOnly();
                if (GUILayout.Button("Timing Only", GUILayout.Height(30)))
                    _runner.RunTimingOnly();

                GUI.backgroundColor = new Color(0.85f, 0.25f, 0.25f);
                GUI.enabled         = _runner.IsRunning;
                if (GUILayout.Button("■  Cancel", GUILayout.Height(30)))
                    _runner.Cancel();

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
                var res = _runner?.GCResult ?? default;

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    // Header row
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        ColHeader("MID_TickDelay", ColTick);
                        ColHeader("Coroutine",     ColCoro);
                        ColHeader("Task.Delay",    ColTask);
                    }

                    EditorGUILayout.Space(2);

                    // Value row
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (res.Valid)
                        {
                            bool tdZero = res.TickDelayBytes == 0;
                            ValueCell($"{res.TickDelayBytes} B",
                                tdZero ? ColPass : ColFail,
                                tdZero ? "✓ Zero alloc" : "! Alloc detected");
                            ValueCell($"{res.CoroutineBytes} B", ColDim, "baseline");
                            ValueCell($"{res.TaskDelayBytes} B", ColDim, "baseline");
                        }
                        else
                        {
                            DimCell("—"); DimCell("—"); DimCell("—");
                        }
                    }

                    EditorGUILayout.Space(4);

                    // Bar chart
                    if (res.Valid && res.MaxBytes > 0)
                    {
                        long mx = res.MaxBytes;
                        BarRow("TickDelay",  (float)res.TickDelayBytes / mx, ColTick,
                            $"{res.TickDelayBytes} B");
                        BarRow("Coroutine",  (float)res.CoroutineBytes / mx, ColCoro,
                            $"{res.CoroutineBytes} B");
                        BarRow("Task.Delay", (float)res.TaskDelayBytes / mx, ColTask,
                            $"{res.TaskDelayBytes} B");
                    }
                    else if (!res.Valid)
                    {
                        CenteredGrey("Run the GC test to see results.");
                    }
                }
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ── Timing Section ────────────────────────────────────────────────────

        private void DrawTimingSection()
        {
            _fTiming = EditorGUILayout.BeginFoldoutHeaderGroup(_fTiming,
                "Timing Accuracy — error vs requested delay  (lower = better)");

            if (_fTiming)
            {
                var res = _runner?.TimingResult ?? default;

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    // Header row
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        ColHeader("MID_TickDelay", ColTick);
                        ColHeader("Coroutine",     ColCoro);
                        ColHeader("Task.Delay",    ColTask);
                    }

                    EditorGUILayout.Space(2);

                    if (res.Valid)
                    {
                        // Avg error
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            MetricRow("Avg error",
                                $"{res.TickDelayAvgMs:F1} ms", ColTick,
                                $"{res.CoroutineAvgMs:F1} ms", ColCoro,
                                $"{res.TaskDelayAvgMs:F1} ms", ColTask);
                        }

                        // Max error
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            MetricRow("Max error",
                                $"{res.TickDelayMaxMs:F1} ms", ColTick,
                                $"{res.CoroutineMaxMs:F1} ms", ColCoro,
                                $"{res.TaskDelayMaxMs:F1} ms", ColTask);
                        }

                        // Fired count
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            MetricRow($"Fired / {res.Total}",
                                $"{res.TickDelayFired}", ColTick,
                                $"{res.CoroutineFired}", ColCoro,
                                $"{res.TaskDelayFired}", ColTask);
                        }

                        EditorGUILayout.Space(4);

                        // Bar chart (width = relative avg error; shorter = more accurate)
                        double maxAvg = res.MaxAvgMs;
                        if (maxAvg > 0)
                        {
                            BarRow("TickDelay",  (float)(res.TickDelayAvgMs  / maxAvg), ColTick,
                                $"{res.TickDelayAvgMs:F1} ms avg");
                            BarRow("Coroutine",  (float)(res.CoroutineAvgMs  / maxAvg), ColCoro,
                                $"{res.CoroutineAvgMs:F1} ms avg");
                            BarRow("Task.Delay", (float)(res.TaskDelayAvgMs  / maxAvg), ColTask,
                                $"{res.TaskDelayAvgMs:F1} ms avg");

                            CenteredGrey("Bar width = relative average error  (shorter = more accurate)");
                        }

                        EditorGUILayout.Space(4);

                        float tickMs = _runner != null ? TickIntervalMs(_runner.Rate) : 100f;
                        EditorGUILayout.HelpBox(
                            $"MID_TickDelay fires on tick boundaries every {tickMs:F0}ms.\n" +
                            $"Maximum possible error = one tick interval ({tickMs:F0}ms).\n\n" +
                            "Coroutine fires on the next frame after WaitForSeconds elapses.\n" +
                            "Task.Delay fires at OS timer precision but on a threadpool thread.\n\n" +
                            "MID_TickDelay trades some timing precision for zero GC allocation.\n" +
                            "Use Coroutine / Task when sub-frame accuracy is required and\n" +
                            "you are willing to accept the allocation cost.",
                            MessageType.Info);
                    }
                    else
                    {
                        CenteredGrey("Run the Timing test to see results.");
                    }
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
                Swatch(ColPass); Label("Pass / zero",    72);
                Swatch(ColFail); Label("Fail / non-zero",90);
            }
        }

        // ── Drawing helpers ───────────────────────────────────────────────────

        private float ColWidth => (position.width - 24f) / 3f;

        private void ColHeader(string text, Color col)
        {
            var old = GUI.color; GUI.color = col;
            EditorGUILayout.LabelField(text, EditorStyles.miniBoldLabel,
                GUILayout.Width(ColWidth));
            GUI.color = old;
        }

        private void ValueCell(string value, Color col, string sub)
        {
            float w = ColWidth;
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(w)))
            {
                var old = GUI.color; GUI.color = col;
                EditorGUILayout.LabelField(value, EditorStyles.boldLabel);
                GUI.color = ColDim;
                EditorGUILayout.LabelField(sub, EditorStyles.miniLabel);
                GUI.color = old;
            }
        }

        private void DimCell(string text)
        {
            var old = GUI.color; GUI.color = ColDim;
            EditorGUILayout.LabelField(text, GUILayout.Width(ColWidth));
            GUI.color = old;
        }

        private void MetricRow(string label,
            string v1, Color c1, string v2, Color c2, string v3, Color c3)
        {
            float lw = 72f;
            float vw = ColWidth - lw - 4f;

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
                    Rect fill = r; fill.width = Mathf.Max(fill.width * fraction, 2f);
                    EditorGUI.DrawRect(fill, col);
                }

                GUI.color = ColDim;
                EditorGUILayout.LabelField(tooltip, EditorStyles.miniLabel, GUILayout.Width(100));
                GUI.color = old;
            }
        }

        private void Swatch(Color col)
        {
            Rect r = EditorGUILayout.GetControlRect(false, 14, GUILayout.Width(14));
            r.y += 2; r.height = 10; r.width = 10;
            EditorGUI.DrawRect(r, col);
            GUILayout.Space(2);
        }

        private static void Label(string text, float width) =>
            EditorGUILayout.LabelField(text, EditorStyles.miniLabel, GUILayout.Width(width));

        private static void CenteredGrey(string text) =>
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
