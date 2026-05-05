// MID_TickDelayBenchmark.cs
// Runtime benchmark: MID_TickDelay vs Coroutine vs Task.Delay
// Measures GC allocation per scheduling call and timing accuracy.
//
// ══ SCENE SETUP ═══════════════════════════════════════════════════════════
//   Add ONLY MID_TickDelayBenchRunner to a scene GameObject.
//   DelayBenchGCResult and DelayBenchTimingResult are plain structs —
//   they CANNOT be added as components. If your scene log shows:
//     "'DelayBenchGCResult' is missing ExtensionOfNativeClass"
//   open that scene, find the broken GameObject and delete the orphaned
//   missing script component.
//
// ══ ZERO-ALLOC RULES (confirmed via IL2CPP & Mono) ═══════════════════════
//   MID_TickDelay.After() itself has zero internal allocation after pool init.
//   But: passing a method group (MyMethod) to Action parameter allocates
//   on Unity 2019.2+ because static delegate caching was removed.
//   Fix: pre-allocate as static readonly field → field reference = zero alloc.
//   The benchmark uses static readonly delegates for this reason.
//
// ══ OPENING THE WINDOW ════════════════════════════════════════════════════
//   Enter Play Mode, then: MidManStudio > Utilities > Tests > Tick Delay Bench
//   The window auto-finds the runner. Click "Add Runner to Scene" if prompted.

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
    // These are VALUE TYPES (structs). Do not add them to a scene as components.

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

        public int  Total;
        public bool Valid;

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
        public int      GCIterations     = 200;
        public int      TimingIterations = 50;
        public int      WarmupCount      = 20;

        [Header("Results  (read-only — set at runtime)")]
        public DelayBenchGCResult     GCResult;
        public DelayBenchTimingResult TimingResult;
        public string                 StatusMessage = "Idle.";
        public float                  Progress;
        public bool                   IsRunning;

        // ── Pre-allocated zero-alloc delegates ───────────────────────────────
        // Static readonly → delegate object created once at type load.
        // Passing these to After() costs zero GC on Unity 2019.2+ (no caching
        // of method groups, but field references to existing delegate objects ARE free).
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
        // Uses the pre-allocated delegate — no closure allocation during warmup.
        // This also forces MID_TickDelay pool initialisation so GC test is clean.

        private IEnumerator WarmUp()
        {
            StatusMessage = $"Warming up ({WarmupCount} cycles)…";
            Progress      = 0f;

            // Use pre-allocated delegate for warmup too
            int fired = 0;
            Action countFired = () => fired++;   // one-time allocation is OK for warmup

            for (int i = 0; i < WarmupCount; i++)
                MID_TickDelay.After(DelaySeconds, countFired, Rate);

            // Wait for all warmup delays to fire
            float waitTime = DelaySeconds + TickIntervalSec() * 4f;
            yield return new WaitForSeconds(waitTime);

            StatusMessage = $"Warm-up done ({fired}/{WarmupCount} fired). Pool ready.";
            yield return null;
            yield return null;
        }

        // ── GC test ───────────────────────────────────────────────────────────
        // Measures bytes allocated PER scheduling call to After()/StartCoroutine()/Task.Delay().
        // The measurement window is a single frame (no yield between before/after) to
        // prevent background GC from polluting the delta.
        //
        // KEY: uses pre-allocated static readonly delegates to isolate the cost of
        // the scheduling call itself, not delegate creation. A result of 0 B for
        // MID_TickDelay means the scheduling call is truly zero-alloc after pool init.

        private IEnumerator GCInner()
        {
            int n = GCIterations;

            // ── MID_TickDelay ─────────────────────────────────────────────────
            StatusMessage = $"GC test — MID_TickDelay ({n} calls)…";
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            yield return null;
            yield return null; // let GC settle

            // Measure inside a single frame — no yield between before/after
            long before = GC.GetTotalMemory(false);
            for (int i = 0; i < n; i++)
                MID_TickDelay.After(DelaySeconds, _doNothing, Rate);
            long after    = GC.GetTotalMemory(false);
            long tickDiff = Math.Max(0L, after - before);
            long tickPerCall = tickDiff / n;

            // Let delays fire so they don't affect coroutine test
            yield return new WaitForSeconds(DelaySeconds + TickIntervalSec() * 3f);
            MID_TickDelay.CancelAll();
            yield return null;
            Progress = 0.33f;

            // ── Coroutine ──────────────────────────────────────────────────────
            StatusMessage = $"GC test — Coroutine ({n} calls)…";
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            yield return null;
            yield return null;

            before = GC.GetTotalMemory(false);
            for (int i = 0; i < n; i++)
                StartCoroutine(DummyWait(DelaySeconds));
            after     = GC.GetTotalMemory(false);
            long coroDiff = Math.Max(0L, after - before);
            long coroPerCall = coroDiff / n;

            yield return new WaitForSeconds(DelaySeconds + 0.3f);
            yield return null;
            Progress = 0.66f;

            // ── Task.Delay ────────────────────────────────────────────────────
            StatusMessage = $"GC test — Task.Delay ({n} calls)…";
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            yield return null;
            yield return null;

            var taskDelay = TimeSpan.FromSeconds(DelaySeconds);
            before = GC.GetTotalMemory(false);
            for (int i = 0; i < n; i++)
                _ = Task.Delay(taskDelay);
            after    = GC.GetTotalMemory(false);
            long taskDiff = Math.Max(0L, after - before);
            long taskPerCall = taskDiff / n;

            yield return null;
            Progress = 1f;

            GCResult = new DelayBenchGCResult
            {
                TickDelayBytesPerCall = tickPerCall,
                CoroutineBytesPerCall = coroPerCall,
                TaskDelayBytesPerCall = taskPerCall,
                Iterations            = n,
                Valid                 = true
            };

            StatusMessage =
                $"GC — TickDelay: {FormatBytes(tickPerCall)} | " +
                $"Coroutine: {FormatBytes(coroPerCall)} | " +
                $"Task.Delay: {FormatBytes(taskPerCall)}";
        }

        // ── Timing test ───────────────────────────────────────────────────────

        private IEnumerator TimingInner()
        {
            int n = TimingIterations;

            // ── MID_TickDelay ─────────────────────────────────────────────────
            StatusMessage = $"Timing — MID_TickDelay (0/{n})…";
            var tdAcc = new TimingAccumulator();

            for (int i = 0; i < n; i++)
            {
                double sched = RealtimeMs();
                float  req   = DelaySeconds;
                // Capture sched+req into local pre-allocated action via closure.
                // Closure allocates here — this is expected and unavoidable for
                // per-iteration timestamp capture. The GC test above uses the clean path.
                double capturedSched = sched;
                float  capturedReq   = req;
                MID_TickDelay.After(req, () =>
                {
                    tdAcc.Record(Math.Abs(RealtimeMs() - capturedSched - capturedReq * 1000.0));
                }, Rate);

                Progress = (float)i / (n * 3f);
                if (i % 5 == 0)
                    StatusMessage = $"Timing — MID_TickDelay ({i}/{n})…";
                yield return null;
            }
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
                Valid          = true
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
            acc.Record(Math.Abs(RealtimeMs() - schedMs - seconds * 1000.0));
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
            bytes == 0 ? "0 B  ✓" : $"{bytes} B";
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

        // Colour palette
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
            w.minSize = new Vector2(500, 540);
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
                DrawConfig();
                DrawRunButtons();
                DrawSeparator();
                DrawGCSection();
                DrawSeparator();
                DrawTimingSection();
                DrawSeparator();
                DrawZeroAllocNote();
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
                EditorGUILayout.HelpBox(
                    "Enter Play Mode to run benchmarks.\n" +
                    "Benchmarks use coroutines and require an active Unity scene.",
                    MessageType.Info);
                return;
            }

            if (_runner == null)
            {
                EditorGUILayout.HelpBox(
                    "No MID_TickDelayBenchRunner found in the scene.\n" +
                    "Add it to a scene GameObject.\n\n" +
                    "If you see an error about 'DelayBenchGCResult missing ExtensionOfNativeClass':\n" +
                    "open your scene, find the GameObject with the missing script, and delete that component.\n" +
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
                        new GUIContent("Delay (s)", "How long each test delay waits."),
                        _runner.DelaySeconds, 0.1f, 5f);

                    _runner.Rate = (TickRate)EditorGUILayout.EnumPopup(
                        new GUIContent("Tick Rate",
                            "Rate bucket used by MID_TickDelay. Min = Tick_0_1.\n" +
                            "Faster rates are clamped — they provide no benefit."),
                        _runner.Rate);

                    _runner.GCIterations = EditorGUILayout.IntSlider(
                        new GUIContent("GC Iterations",
                            "Scheduling calls per method in the GC allocation test.\n" +
                            "Higher = more accurate bytes-per-call average."),
                        _runner.GCIterations, 50, 500);

                    _runner.TimingIterations = EditorGUILayout.IntSlider(
                        new GUIContent("Timing Iterations",
                            "Number of delays fired per method in the timing accuracy test."),
                        _runner.TimingIterations, 10, 200);

                    _runner.WarmupCount = EditorGUILayout.IntSlider(
                        new GUIContent("Warmup",
                            "Delays fired before measurement to initialise the pool and JIT."),
                        _runner.WarmupCount, 10, 50);

                    GUI.enabled = true;

                    float tickMs = TickIntervalMs(_runner.Rate);
                    EditorGUILayout.HelpBox(
                        $"At {_runner.Rate} the tick fires every {tickMs:F0}ms.\n" +
                        $"Maximum possible MID_TickDelay timing error ≈ {tickMs:F0}ms.",
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
                "GC Allocation per scheduling call  (lower = better, 0 B = ✓ zero alloc)");

            if (_fGC)
            {
                var res = _runner.GCResult;

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (!res.Valid)
                    {
                        EditorGUILayout.HelpBox("Run the GC test to see results.", MessageType.Info);
                        EditorGUILayout.EndFoldoutHeaderGroup();
                        return;
                    }

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
                        bool tdZero = res.TickDelayBytesPerCall == 0;
                        ValueCell(
                            tdZero ? "0 B  ✓" : $"{res.TickDelayBytesPerCall} B",
                            tdZero ? ColPass : ColFail,
                            tdZero ? "Zero alloc — pool working" : "Alloc detected — check warmup");
                        ValueCell($"{res.CoroutineBytesPerCall} B", ColCoro, "baseline");
                        ValueCell($"{res.TaskDelayBytesPerCall} B", ColTask, "baseline");
                    }

                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField(
                        $"Measured over {res.Iterations} calls each method. " +
                        "TickDelay uses pre-allocated static delegates.",
                        EditorStyles.miniLabel);

                    EditorGUILayout.Space(4);

                    // Bar chart — only show if there's something to compare
                    long mx = res.MaxBytes;
                    if (mx > 0)
                    {
                        BarRow("TickDelay",  (float)res.TickDelayBytesPerCall / mx, ColTick,
                            res.TickDelayBytesPerCall == 0 ? "0 B  ✓" : $"{res.TickDelayBytesPerCall} B");
                        BarRow("Coroutine",  (float)res.CoroutineBytesPerCall / mx, ColCoro,
                            $"{res.CoroutineBytesPerCall} B");
                        BarRow("Task.Delay", (float)res.TaskDelayBytesPerCall / mx, ColTask,
                            $"{res.TaskDelayBytesPerCall} B");
                    }
                    else
                    {
                        // All three are 0 — very unusual but handle it
                        BarRow("TickDelay",  0f, ColPass, "0 B  ✓");
                        BarRow("Coroutine",  0f, ColCoro, "0 B");
                        BarRow("Task.Delay", 0f, ColTask, "0 B");
                        EditorGUILayout.HelpBox(
                            "All methods show 0 B. This can happen if the GC collected before/after " +
                            "measurement. Try increasing GC Iterations.",
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
                "Timing Accuracy — error vs requested delay  (lower = better)");

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

                    double maxAvg = res.MaxAvgMs;
                    if (maxAvg > 0)
                    {
                        BarRow("TickDelay",  (float)(res.TickDelayAvgMs / maxAvg), ColTick,
                            $"{res.TickDelayAvgMs:F1} ms avg");
                        BarRow("Coroutine",  (float)(res.CoroutineAvgMs / maxAvg), ColCoro,
                            $"{res.CoroutineAvgMs:F1} ms avg");
                        BarRow("Task.Delay", (float)(res.TaskDelayAvgMs / maxAvg), ColTask,
                            $"{res.TaskDelayAvgMs:F1} ms avg");

                        CentredGrey("Bar width = relative avg error  (shorter bar = more accurate)");
                    }

                    EditorGUILayout.Space(4);

                    float tickMs = _runner != null ? TickIntervalMs(_runner.Rate) : 100f;
                    EditorGUILayout.HelpBox(
                        $"MID_TickDelay maximum error = one tick interval ({tickMs:F0}ms).\n" +
                        "Coroutine fires on the next frame after WaitForSeconds elapses.\n" +
                        "Task.Delay fires at OS timer precision but on a threadpool thread.\n\n" +
                        "Trade-off: MID_TickDelay is the only zero-alloc option. " +
                        "Use Coroutine when sub-frame accuracy matters more than GC.",
                        MessageType.Info);
                }
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ── Zero-alloc usage note ─────────────────────────────────────────────

        private void DrawZeroAllocNote()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var old = GUI.color; GUI.color = ColPass;
                EditorGUILayout.LabelField("How to use MID_TickDelay with zero GC",
                    EditorStyles.miniBoldLabel);
                GUI.color = old;

                EditorGUILayout.LabelField(
                    "// ✗ Allocates every call (method group → new delegate each time in Unity 2019.2+):\n" +
                    "MID_TickDelay.After(1f, MyMethod);\n\n" +
                    "// ✓ Zero alloc — pre-allocate delegate once:\n" +
                    "private static readonly Action _cb = MyMethod;\n" +
                    "MID_TickDelay.After(1f, _cb);",
                    EditorStyles.helpBox,
                    GUILayout.MinHeight(80));
            }
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
            EditorGUILayout.LabelField(text, EditorStyles.miniBoldLabel,
                GUILayout.Width(ColWidth));
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
                    Rect fill = r; fill.width = Mathf.Max(fill.width * fraction, 2f);
                    EditorGUI.DrawRect(fill, col);
                }
                else if (fraction == 0f)
                {
                    // Show a tiny green tick mark for zero
                    Rect tick = r; tick.width = 4f;
                    EditorGUI.DrawRect(tick, ColPass);
                }

                GUI.color = ColDim;
                EditorGUILayout.LabelField(tooltip, EditorStyles.miniLabel, GUILayout.Width(120));
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