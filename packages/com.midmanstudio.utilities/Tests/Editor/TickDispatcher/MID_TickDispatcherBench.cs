// MID_TickDispatcherBench.cs
// Managed vs Native tick dispatcher comparison + simulated MonoBehaviour.Update baseline.
//
// Open: Window > MID > Tick Dispatcher Bench
// Attach MID_TickDispatcherBench component to a scene GameObject.
//
// Three sections:
//   Single-Rate — variable subscriber count at one tick rate
//   Multi-Rate  — all rates active simultaneously (realistic game load)
//   Heavy       — callbacks doing real math work (sin/cos)
//
// Each section compares three paths:
//   Update sim  — N objects iterated directly (simulates raw Update() loops)
//   Managed     — MID_TickDispatcher-style Action<float>[] dispatch
//   Native      — MID_NativeTickDispatcher-style FunctionPointer<T>.Invoke dispatch
//
// BURST FIX NOTES:
//   [BurstCompile] must be on BOTH the class AND the method.
//   Classes must be public (not internal) for Burst to scan them.
//   Burst callbacks MUST NOT access managed static fields (NativeArray, etc.)
//   All work in Burst callbacks must be pure math or NativeArray passed by param.
//   FunctionPointers are compiled ONCE in WarmUp and reused — never per-iteration.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MidManStudio.Core.Benchmarks
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Burst callbacks — public class, [BurstCompile] on class AND method,
    //  pure math only — no managed static access, no NativeArray.
    //  [BurstCompile] at class level is required by Unity 2021.3+ for
    //  CompileFunctionPointer to find them as known entry points.
    // ─────────────────────────────────────────────────────────────────────────

    [BurstCompile]
    public static class TickBenchEmptyCallbacks
    {
        [BurstCompile] public static void S0(float dt) { }
        [BurstCompile] public static void S1(float dt) { }
        [BurstCompile] public static void S2(float dt) { }
        [BurstCompile] public static void S3(float dt) { }
        [BurstCompile] public static void S4(float dt) { }
        [BurstCompile] public static void S5(float dt) { }
        [BurstCompile] public static void S6(float dt) { }
        [BurstCompile] public static void S7(float dt) { }
        [BurstCompile] public static void S8(float dt) { }
        [BurstCompile] public static void S9(float dt) { }
    }

    // Heavy: pure sin/cos — no managed memory, fully Burst-compilable.
    [BurstCompile]
    public static class TickBenchHeavyCallbacks
    {
        const int UNITS = 50;

        [BurstCompile]
        public static void S0(float dt) { float a = 0; for (int i = 0; i < UNITS; i++) a += math.sin(i * 0.1f + dt); if (a > 1e9f) Debug.Log("never"); }
        [BurstCompile]
        public static void S1(float dt) { float a = 0; for (int i = 0; i < UNITS; i++) a += math.sin(i * 0.1f + dt); if (a > 1e9f) Debug.Log("never"); }
        [BurstCompile]
        public static void S2(float dt) { float a = 0; for (int i = 0; i < UNITS; i++) a += math.sin(i * 0.1f + dt); if (a > 1e9f) Debug.Log("never"); }
        [BurstCompile]
        public static void S3(float dt) { float a = 0; for (int i = 0; i < UNITS; i++) a += math.sin(i * 0.1f + dt); if (a > 1e9f) Debug.Log("never"); }
        [BurstCompile]
        public static void S4(float dt) { float a = 0; for (int i = 0; i < UNITS; i++) a += math.sin(i * 0.1f + dt); if (a > 1e9f) Debug.Log("never"); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Simulated MonoBehaviour — models raw Update() loop cost.
    //  This is the baseline that tick dispatchers save CPU against.
    // ─────────────────────────────────────────────────────────────────────────

    public class SimulatedUpdateBehaviour
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateEmpty() { }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateHeavy()
        {
            float a = 0f;
            for (int i = 0; i < 50; i++) a += Mathf.Sin(i * 0.1f) * Mathf.Cos(i * 0.05f);
            if (a > 1e9f) Debug.Log("never");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Result types
    // ─────────────────────────────────────────────────────────────────────────

    [Serializable]
    public struct TickBenchResult
    {
        public string Label;
        public double AvgMs, MinMs, MaxMs, StdDevMs;
        public int Subs, Iters;
        public bool HasData => AvgMs > 0;
        public string Summary => HasData
            ? $"avg {AvgMs:F5}ms  min {MinMs:F5}ms  max {MaxMs:F5}ms  σ{StdDevMs:F5}ms"
            : "not run";
    }

    [Serializable]
    public struct TickScenarioResult
    {
        public string Name;
        public TickBenchResult UpdateSim;   // simulated direct Update() loop
        public TickBenchResult Managed;     // Action<float>[] loop (dispatcher model)
        public TickBenchResult Native;      // FunctionPointer<T>.Invoke loop

        public bool BothDispatchersRan => Managed.HasData && Native.HasData;
        public bool NativeWins => BothDispatchersRan && Native.AvgMs < Managed.AvgMs;
        public double DispatcherSpeedup => BothDispatchersRan && Native.AvgMs > 0
                                           ? Managed.AvgMs / Native.AvgMs : 0;
        // How much cheaper is the dispatcher vs Update at same frequency?
        public double UpdateVsManaged => UpdateSim.HasData && Managed.HasData && Managed.AvgMs > 0
                                          ? UpdateSim.AvgMs / Managed.AvgMs : 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  MonoBehaviour
    // ─────────────────────────────────────────────────────────────────────────

    public class MID_TickDispatcherBench : MonoBehaviour
    {
        [Header("Config")]
        public int[] SubCounts = { 1, 10, 50, 100, 200 };
        public TickRate BenchRate = TickRate.Tick_0_1;
        public int Iterations = 5000;

        [Header("Multi-Rate")]
        public int MultiSubsPerRate = 20;
        public TickRate[] MultiRates = {
            TickRate.Tick_0_05, TickRate.Tick_0_1, TickRate.Tick_0_2,
            TickRate.Tick_0_5,  TickRate.Tick_1,
        };

        [Header("Results")]
        public List<TickScenarioResult> SingleResults = new();
        public TickScenarioResult MultiResult;
        public TickScenarioResult HeavyResult;

        // Pre-compiled function pointers (compiled once in WarmUp).
        Unity.Burst.FunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>[] _emptyFPs;
        Unity.Burst.FunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>[] _heavyFPs;
        bool _burstReady;

        void Awake() => WarmUp();
        void OnDestroy() { /* no NativeArrays to clean up */ }

        // ── Public API ────────────────────────────────────────────────────────

        public void RunAll()
        {
            RunSingle(); RunMulti(); RunHeavy();
            LogSummary();
        }

        public void RunSingle()
        {
            SingleResults.Clear();
            float iv = Interval(BenchRate);
            foreach (int n in SubCounts)
                SingleResults.Add(RunScenario($"{BenchRate} ×{n}", iv, n, heavy: false));
        }

        public void RunMulti()
        {
            float simDur = 1f;
            MultiResult = RunMultiRateScenario(MultiRates, MultiSubsPerRate, simDur);
        }

        public void RunHeavy()
        {
            int n = Mathf.Min(5, _heavyFPs?.Length ?? 0);
            HeavyResult = RunScenario($"Heavy ×{n}", Interval(BenchRate), n, heavy: true);
            HeavyResult.Name = $"Heavy ({n} subs, 50 work units)";
        }

        // ── Core single-rate scenario ─────────────────────────────────────────

        TickScenarioResult RunScenario(string name, float interval, int subCount, bool heavy)
        {
            subCount = heavy ? Mathf.Min(subCount, 5) : Mathf.Min(subCount, 10);

            return new TickScenarioResult
            {
                Name = name,
                UpdateSim = MeasureUpdateSim(subCount, heavy),
                Managed = MeasureManaged(interval, subCount, heavy),
                Native = _burstReady
                             ? MeasureNative(interval, subCount, heavy)
                             : new TickBenchResult { Label = "Burst not ready" },
            };
        }

        // ── Update simulation — models Unity's internal Update() dispatch ─────
        //
        // Unity iterates an internal C++ list and calls each MonoBehaviour.Update()
        // via native-to-managed boundary crossing. We can't reproduce the native
        // crossing here, but we can measure the managed-side iteration cost
        // (the part tick dispatchers actually save).

        TickBenchResult MeasureUpdateSim(int n, bool heavy)
        {
            var objs = new SimulatedUpdateBehaviour[n];
            for (int i = 0; i < n; i++) objs[i] = new SimulatedUpdateBehaviour();

            int iters = Iterations;
            double freq = Stopwatch.Frequency;
            double[] t = new double[iters];
            var sw = new Stopwatch();

            // Warmup
            for (int w = 0; w < 200; w++)
                for (int j = 0; j < n; j++) { if (heavy) objs[j].UpdateHeavy(); else objs[j].UpdateEmpty(); }

            for (int i = 0; i < iters; i++)
            {
                sw.Restart();
                for (int j = 0; j < n; j++) { if (heavy) objs[j].UpdateHeavy(); else objs[j].UpdateEmpty(); }
                sw.Stop();
                t[i] = sw.ElapsedTicks / freq * 1000.0;
            }
            return Stats("Update sim", n, iters, t);
        }

        // ── Managed dispatch — Action<float>[] loop, same model as MID_TickDispatcher

        TickBenchResult MeasureManaged(float interval, int n, bool heavy)
        {
            var cbs = new Action<float>[n];
            for (int i = 0; i < n; i++)
            {
                int ci = i;
                if (heavy) cbs[i] = dt => ManagedHeavy(dt);
                else cbs[i] = dt => { };
            }

            int iters = Iterations;
            double freq = Stopwatch.Frequency;
            double[] t = new double[iters];
            var sw = new Stopwatch();

            for (int w = 0; w < 200; w++) for (int j = 0; j < n; j++) cbs[j](interval);

            for (int i = 0; i < iters; i++)
            {
                sw.Restart();
                for (int j = 0; j < n; j++) cbs[j](interval);
                sw.Stop();
                t[i] = sw.ElapsedTicks / freq * 1000.0;
            }
            return Stats("Managed", n, iters, t);
        }

        // ── Native dispatch — pre-compiled FunctionPointer<T>.Invoke loop ─────

        TickBenchResult MeasureNative(float interval, int n, bool heavy)
        {
            var fps = heavy ? _heavyFPs : _emptyFPs;
            int slots = Mathf.Min(n, fps.Length);

            int iters = Iterations;
            double freq = Stopwatch.Frequency;
            double[] t = new double[iters];
            var sw = new Stopwatch();

            for (int w = 0; w < 200; w++) for (int j = 0; j < slots; j++) fps[j].Invoke(interval);

            for (int i = 0; i < iters; i++)
            {
                sw.Restart();
                for (int j = 0; j < slots; j++) fps[j].Invoke(interval);
                sw.Stop();
                t[i] = sw.ElapsedTicks / freq * 1000.0;
            }
            return Stats("Native", slots, iters, t);
        }

        // ── Multi-rate scenario ───────────────────────────────────────────────

        TickScenarioResult RunMultiRateScenario(TickRate[] rates, int subsPerRate, float simDur)
        {
            int n = Mathf.Min(subsPerRate, 10);
            float simDt = 0.016f;
            int steps = (int)(simDur / simDt);

            float[] ivs = new float[rates.Length];
            float[] timers = new float[rates.Length];
            for (int r = 0; r < rates.Length; r++) ivs[r] = Interval(rates[r]);

            // Update sim — N objects per rate, iterate all each step regardless of rate
            // (this is what raw Update() does — fires every frame)
            var objs = new SimulatedUpdateBehaviour[rates.Length * n];
            for (int i = 0; i < objs.Length; i++) objs[i] = new SimulatedUpdateBehaviour();

            // Managed
            var mcbs = new Action<float>[rates.Length * n];
            for (int i = 0; i < mcbs.Length; i++) mcbs[i] = dt => { };

            double freq = Stopwatch.Frequency;

            // ── Update sim: all N*rates fire every step
            double uTotal = 0; long uMin = long.MaxValue, uMax = 0;
            var sw = new Stopwatch();
            Array.Clear(timers, 0, timers.Length);
            for (int s = 0; s < steps; s++)
            {
                sw.Restart();
                for (int o = 0; o < objs.Length; o++) objs[o].UpdateEmpty();
                sw.Stop();
                long tk = sw.ElapsedTicks;
                uTotal += tk; if (tk < uMin) uMin = tk; if (tk > uMax) uMax = tk;
            }

            // ── Managed: only fire rate bucket when timer expires
            double mTotal = 0; long mMin = long.MaxValue, mMax = 0;
            Array.Clear(timers, 0, timers.Length);
            for (int s = 0; s < steps; s++)
            {
                sw.Restart();
                for (int r = 0; r < rates.Length; r++)
                {
                    timers[r] += simDt;
                    if (timers[r] >= ivs[r])
                    {
                        timers[r] -= ivs[r];
                        for (int j = r * n; j < r * n + n; j++) mcbs[j](ivs[r]);
                    }
                }
                sw.Stop();
                long tk = sw.ElapsedTicks;
                mTotal += tk; if (tk < mMin) mMin = tk; if (tk > mMax) mMax = tk;
            }

            // ── Native: same pattern with function pointers
            double nTotal = 0; long nMin = long.MaxValue, nMax = 0;
            if (_burstReady)
            {
                int fpSlots = Mathf.Min(n, _emptyFPs.Length);
                Array.Clear(timers, 0, timers.Length);
                for (int s = 0; s < steps; s++)
                {
                    sw.Restart();
                    for (int r = 0; r < rates.Length; r++)
                    {
                        timers[r] += simDt;
                        if (timers[r] >= ivs[r])
                        {
                            timers[r] -= ivs[r];
                            for (int j = 0; j < fpSlots; j++) _emptyFPs[j].Invoke(ivs[r]);
                        }
                    }
                    sw.Stop();
                    long tk = sw.ElapsedTicks;
                    nTotal += tk; if (tk < nMin) nMin = tk; if (tk > nMax) nMax = tk;
                }
            }

            double ms(long ticks) => ticks / freq * 1000.0;

            return new TickScenarioResult
            {
                Name = $"Multi-rate {rates.Length}r ×{n}",
                UpdateSim = new TickBenchResult { Label = "Update sim", AvgMs = ms(uMin) * 0 + uTotal / freq * 1000.0 / steps, MinMs = ms(uMin), MaxMs = ms(uMax), Subs = objs.Length, Iters = steps },
                Managed = new TickBenchResult { Label = "Managed", AvgMs = ms(mMin) * 0 + mTotal / freq * 1000.0 / steps, MinMs = ms(mMin), MaxMs = ms(mMax), Subs = rates.Length * n, Iters = steps },
                Native = _burstReady ? new TickBenchResult { Label = "Native", AvgMs = nTotal / freq * 1000.0 / steps, MinMs = ms(nMin), MaxMs = ms(nMax), Subs = rates.Length * Mathf.Min(n, _emptyFPs.Length), Iters = steps } : default,
            };
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static TickBenchResult Stats(string label, int subs, int n, double[] t)
        {
            double sum = 0, mn = double.MaxValue, mx = 0;
            for (int i = 0; i < n; i++) { sum += t[i]; if (t[i] < mn) mn = t[i]; if (t[i] > mx) mx = t[i]; }
            double avg = sum / n, v = 0;
            for (int i = 0; i < n; i++) v += (t[i] - avg) * (t[i] - avg);
            return new TickBenchResult
            {
                Label = label,
                AvgMs = avg,
                MinMs = mn,
                MaxMs = mx,
                StdDevMs = Math.Sqrt(v / n),
                Subs = subs,
                Iters = n
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ManagedHeavy(float dt)
        {
            float a = 0f;
            for (int i = 0; i < 50; i++) a += Mathf.Sin(i * 0.1f + dt) * Mathf.Cos(i * 0.05f);
            if (a > 1e9f) Debug.Log("never");
        }

        static float Interval(TickRate r) => r switch
        {
            TickRate.Tick_0_01 => 0.01f,
            TickRate.Tick_0_02 => 0.02f,
            TickRate.Tick_0_05 => 0.05f,
            TickRate.Tick_0_1 => 0.1f,
            TickRate.Tick_0_2 => 0.2f,
            TickRate.Tick_0_5 => 0.5f,
            TickRate.Tick_1 => 1.0f,
            TickRate.Tick_2 => 2.0f,
            TickRate.Tick_5 => 5.0f,
            _ => 0.1f,
        };

        void WarmUp()
        {
            try
            {
                // Compile ALL function pointers once here.
                // Never call CompileFunctionPointer inside a measurement loop.
                _emptyFPs = new[] {
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchEmptyCallbacks.S0),
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchEmptyCallbacks.S1),
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchEmptyCallbacks.S2),
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchEmptyCallbacks.S3),
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchEmptyCallbacks.S4),
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchEmptyCallbacks.S5),
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchEmptyCallbacks.S6),
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchEmptyCallbacks.S7),
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchEmptyCallbacks.S8),
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchEmptyCallbacks.S9),
                };
                _heavyFPs = new[] {
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchHeavyCallbacks.S0),
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchHeavyCallbacks.S1),
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchHeavyCallbacks.S2),
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchHeavyCallbacks.S3),
                    Unity.Burst.BurstCompiler.CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(TickBenchHeavyCallbacks.S4),
                };
                // Fire each once to ensure JIT is done before any measurement.
                foreach (var fp in _emptyFPs) fp.Invoke(0.1f);
                foreach (var fp in _heavyFPs) fp.Invoke(0.1f);
                _burstReady = true;
                Debug.Log("[TickBench] Burst warm-up OK.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TickBench] Burst warm-up failed: {e.Message}. Native bench disabled.");
            }
        }

        void LogSummary()
        {
            Debug.Log("=== Tick Dispatcher Bench ===");
            foreach (var r in SingleResults) Log(r);
            Log(MultiResult);
            Log(HeavyResult);
        }

        static void Log(TickScenarioResult r)
        {
            if (!r.Managed.HasData) return;
            string disp = r.BothDispatchersRan
                ? $"  dispatcher speedup: {(r.NativeWins ? "Native" : "Managed")} {r.DispatcherSpeedup:F2}×"
                : "";
            string vsUpdate = r.UpdateSim.HasData && r.UpdateVsManaged > 0
                ? $"  managed {r.UpdateVsManaged:F2}× faster than Update sim"
                : "";
            Debug.Log($"[{r.Name}]\n" +
                      $"  UpdateSim: {r.UpdateSim.Summary}\n" +
                      $"  Managed:   {r.Managed.Summary}\n" +
                      $"  Native:    {r.Native.Summary}" +
                      disp + vsUpdate);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Editor Window
    // ─────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR

    public class MID_TickDispatcherBenchWindow : EditorWindow
    {
        MID_TickDispatcherBench _bench;
        Vector2 _scroll;
        bool _fS = true, _fM = true, _fH = false;

        static readonly Color UpdateCol = new Color(0.90f, 0.55f, 0.20f, 1f);
        static readonly Color ManagedCol = new Color(0.55f, 0.75f, 1.00f, 1f);
        static readonly Color NativeCol = new Color(0.22f, 0.88f, 0.60f, 1f);
        static readonly Color WinCol = new Color(0.28f, 0.95f, 0.45f, 1f);
        static readonly Color DimCol = new Color(0.50f, 0.50f, 0.50f, 1f);
        static readonly Color BarBg = new Color(0.15f, 0.15f, 0.15f, 0.4f);

        [MenuItem("Window/MID/Tick Dispatcher Bench")]
        static void Open() =>
            GetWindow<MID_TickDispatcherBenchWindow>("Tick Dispatcher Bench").minSize = new Vector2(560, 360);

        void OnEnable() { EditorApplication.update += Repaint; Find(); }
        void OnDisable() { EditorApplication.update -= Repaint; }
        void Find() { if (_bench == null) _bench = FindObjectOfType<MID_TickDispatcherBench>(); }

        void OnGUI()
        {
            Find();
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField("MID Tick Dispatcher Bench",
                    EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                _bench = (MID_TickDispatcherBench)EditorGUILayout.ObjectField(
                    _bench, typeof(MID_TickDispatcherBench), true, GUILayout.Width(200));
                if (GUILayout.Button("Run All", EditorStyles.toolbarButton, GUILayout.Width(58))) _bench?.RunAll();
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(46)))
                {
                    if (_bench != null) { _bench.SingleResults.Clear(); _bench.MultiResult = default; _bench.HeavyResult = default; }
                }
            }

            if (_bench == null) { EditorGUILayout.HelpBox("Add MID_TickDispatcherBench to a scene GO.", MessageType.Info); return; }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawSection("Single-Rate", ref _fS, _bench.SingleResults, _bench.RunSingle);
            DrawSingleScenario("Multi-Rate  (5 rates simultaneously)", ref _fM, _bench.MultiResult, _bench.RunMulti);
            DrawSingleScenario("Heavy callbacks  (50 sin/cos ops each)", ref _fH, _bench.HeavyResult, _bench.RunHeavy);
            EditorGUILayout.EndScrollView();
        }

        void DrawSection(string title, ref bool fold, List<TickScenarioResult> results, Action run)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                fold = EditorGUILayout.Foldout(fold, title, true, EditorStyles.foldoutHeader);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Run", EditorStyles.toolbarButton, GUILayout.Width(36))) run?.Invoke();
            }
            if (!fold) return;
            EditorGUI.indentLevel++;
            // Header row
            using (new EditorGUILayout.HorizontalScope())
            {
                Lbl("Subs", 44); Lbl("Update sim", 100); Lbl("Managed", 100); Lbl("Native", 100); Lbl("M/N speedup", 90);
                GUILayout.FlexibleSpace();
            }
            foreach (var r in results) DrawRow(r);
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        void DrawSingleScenario(string title, ref bool fold, TickScenarioResult r, Action run)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                fold = EditorGUILayout.Foldout(fold, title, true, EditorStyles.foldoutHeader);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Run", EditorStyles.toolbarButton, GUILayout.Width(36))) run?.Invoke();
            }
            if (!fold) return;
            EditorGUI.indentLevel++;
            DrawDetailRow(r);
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        void DrawRow(TickScenarioResult r)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                Lbl(r.Managed.Subs.ToString(), 44);
                CL($"{r.UpdateSim.AvgMs * 1000:F1}µs", 100, UpdateCol, r.UpdateSim.HasData);
                CL($"{r.Managed.AvgMs * 1000:F1}µs", 100, ManagedCol, r.Managed.HasData);
                CL(r.Native.HasData ? $"{r.Native.AvgMs * 1000:F1}µs" : "—", 100, NativeCol, r.Native.HasData);

                if (r.BothDispatchersRan)
                    CL($"{(r.NativeWins ? "N" : "M")} {r.DispatcherSpeedup:F2}×", 90,
                        r.NativeWins ? WinCol : ManagedCol, true);
                else
                    DL("—", 90);

                // Mini bar: max = Update sim
                float maxVal = r.UpdateSim.HasData ? (float)r.UpdateSim.AvgMs : (float)r.Managed.AvgMs;
                if (maxVal > 0)
                {
                    Bar((float)(r.UpdateSim.AvgMs / maxVal), UpdateCol, 36);
                    GUILayout.Space(1);
                    Bar((float)(r.Managed.AvgMs / maxVal), ManagedCol, 36);
                    GUILayout.Space(1);
                    if (r.Native.HasData) Bar((float)(r.Native.AvgMs / maxVal), NativeCol, 36);
                }
                GUILayout.FlexibleSpace();
            }
        }

        void DrawDetailRow(TickScenarioResult r)
        {
            void Row(string lbl, TickBenchResult res, Color col)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    CL(lbl, 90, col, res.HasData);
                    if (res.HasData) EditorGUILayout.LabelField(res.Summary, EditorStyles.miniLabel);
                    else DL("not run");
                }
            }
            Row("Update sim", r.UpdateSim, UpdateCol);
            Row("Managed", r.Managed, ManagedCol);
            Row("Native", r.Native, NativeCol);
            if (r.BothDispatchersRan)
            {
                var old = GUI.color; GUI.color = r.NativeWins ? WinCol : ManagedCol;
                EditorGUILayout.LabelField($"Dispatcher: {(r.NativeWins ? "Native" : "Managed")} wins {r.DispatcherSpeedup:F2}×", EditorStyles.miniBoldLabel);
                GUI.color = old;
            }
            if (r.UpdateSim.HasData && r.Managed.HasData)
                DL($"Managed dispatcher is {r.UpdateVsManaged:F2}× cheaper than equivalent Update() frequency");
        }

        void Bar(float f, Color c, float w)
        {
            f = Mathf.Clamp01(f);
            Rect r = GUILayoutUtility.GetRect(w, 10, GUILayout.Width(w));
            r.y += 3; r.height = 6;
            EditorGUI.DrawRect(r, BarBg);
            Rect fill = r; fill.width *= f;
            EditorGUI.DrawRect(fill, c);
        }

        void Lbl(string t, float w) => EditorGUILayout.LabelField(t, GUILayout.Width(w));
        void CL(string t, float w, Color c, bool on) { var o = GUI.color; GUI.color = on ? c : DimCol; EditorGUILayout.LabelField(t, GUILayout.Width(w)); GUI.color = o; }
        void DL(string t, float w = 0) { var o = GUI.color; GUI.color = DimCol; if (w > 0) EditorGUILayout.LabelField(t, EditorStyles.miniLabel, GUILayout.Width(w)); else EditorGUILayout.LabelField(t, EditorStyles.miniLabel); GUI.color = o; }
    }

#endif
}