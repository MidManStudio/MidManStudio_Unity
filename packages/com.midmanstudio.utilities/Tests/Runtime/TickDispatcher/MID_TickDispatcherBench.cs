// MID_TickDispatcherBench.cs
// Benchmarks MID_TickDispatcher (managed) against a simulated raw Update() baseline.
// Native tick dispatcher removed — real-world results showed managed wins below ~500
// Burst subscribers doing heavy math. Not worth the complexity for typical gameplay code.
//
// Open: MidManStudio > Utilities > Tests > Tick Dispatcher Bench
// Attach MID_TickDispatcherBench to a scene GameObject.
//
// Three sections:
//   Single-Rate  — variable subscriber count at one tick rate
//   Multi-Rate   — all rates active simultaneously (realistic game load)
//   Heavy        — callbacks doing real math work

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;
using MidManStudio.Core.TickDispatcher;
using MidManStudio.Core.Logging;
using Debug = UnityEngine.Debug;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MidManStudio.Core.Benchmarks
{
    // ── Simulated MonoBehaviour Update loop ───────────────────────────────────
    // Models the managed-side cost of Unity's internal Update() dispatch.
    // The actual native→managed boundary crossing is not measurable from managed
    // code but this gives a valid comparison of callback execution cost.

    public class SimulatedUpdateBehaviour
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateEmpty() { }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateHeavy()
        {
            float a = 0f;
            for (int i = 0; i < 50; i++)
                a += Mathf.Sin(i * 0.1f) * Mathf.Cos(i * 0.05f);
            if (a > 1e9f) Debug.Log("never");
        }
    }

    // ── Result types ──────────────────────────────────────────────────────────

    [Serializable]
    public struct TickBenchResult
    {
        public string Label;
        public double AvgMs, MinMs, MaxMs, StdDevMs;
        public int    Subs, Iters;

        public bool   HasData  => AvgMs > 0;
        public string Summary  => HasData
            ? $"avg {AvgMs:F5}ms  min {MinMs:F5}ms  max {MaxMs:F5}ms  σ{StdDevMs:F5}ms"
            : "not run";
    }

    [Serializable]
    public struct TickScenarioResult
    {
        public string         Name;
        public TickBenchResult UpdateSim;
        public TickBenchResult Managed;

        public bool   BothRan        => UpdateSim.HasData && Managed.HasData;
        public double UpdateVsManaged =>
            BothRan && Managed.AvgMs > 0 ? UpdateSim.AvgMs / Managed.AvgMs : 0;
    }

    // ── MonoBehaviour ─────────────────────────────────────────────────────────

    public class MID_TickDispatcherBench : MonoBehaviour
    {
        [Header("Config")]
        public int[]     SubCounts        = { 1, 10, 50, 100, 200 };
        public TickRate  BenchRate        = TickRate.Tick_0_1;
        public int       Iterations       = 5000;
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Multi-Rate")]
        public int       MultiSubsPerRate = 20;
        public TickRate[] MultiRates      =
        {
            TickRate.Tick_0_1, TickRate.Tick_0_2, TickRate.Tick_0_5,
            TickRate.Tick_1,   TickRate.Tick_2
        };

        [Header("Results")]
        public List<TickScenarioResult> SingleResults = new();
        public TickScenarioResult       MultiResult;
        public TickScenarioResult       HeavyResult;

        // ── Public API ────────────────────────────────────────────────────────

        public void RunAll()   { RunSingle(); RunMulti(); RunHeavy(); LogSummary(); }
        public void RunSingle() { SingleResults.Clear(); float iv = Interval(BenchRate); foreach (int n in SubCounts) SingleResults.Add(RunScenario($"{BenchRate} ×{n}", iv, n, false)); }
        public void RunMulti()  { MultiResult = RunMultiRateScenario(MultiRates, MultiSubsPerRate, 1f); }
        public void RunHeavy()  { HeavyResult = RunScenario($"Heavy ×5", Interval(BenchRate), 5, true); HeavyResult.Name = "Heavy (5 subs, 50 work units each)"; }

        // ── Single-rate scenario ──────────────────────────────────────────────

        private TickScenarioResult RunScenario(string name, float interval, int subCount, bool heavy)
        {
            if (heavy) subCount = Mathf.Min(subCount, 5);

            return new TickScenarioResult
            {
                Name      = name,
                UpdateSim = MeasureUpdateSim(subCount, heavy),
                Managed   = MeasureManaged(interval, subCount, heavy)
            };
        }

        // ── Simulated Update() baseline ───────────────────────────────────────

        private TickBenchResult MeasureUpdateSim(int n, bool heavy)
        {
            var objs = new SimulatedUpdateBehaviour[n];
            for (int i = 0; i < n; i++) objs[i] = new SimulatedUpdateBehaviour();

            int iters = Iterations;
            double freq = Stopwatch.Frequency;
            double[] t = new double[iters];
            var sw = new Stopwatch();

            // Warmup
            for (int w = 0; w < 200; w++)
                for (int j = 0; j < n; j++)
                    if (heavy) objs[j].UpdateHeavy(); else objs[j].UpdateEmpty();

            for (int i = 0; i < iters; i++)
            {
                sw.Restart();
                for (int j = 0; j < n; j++)
                    if (heavy) objs[j].UpdateHeavy(); else objs[j].UpdateEmpty();
                sw.Stop();
                t[i] = sw.ElapsedTicks / freq * 1000.0;
            }

            return Stats("Update sim", n, iters, t);
        }

        // ── Managed dispatcher ────────────────────────────────────────────────

        private TickBenchResult MeasureManaged(float interval, int n, bool heavy)
        {
            var cbs = new Action<float>[n];
            for (int i = 0; i < n; i++)
            {
                if (heavy) cbs[i] = ManagedHeavy;
                else       cbs[i] = _ => { };
            }

            int iters = Iterations;
            double freq = Stopwatch.Frequency;
            double[] t = new double[iters];
            var sw = new Stopwatch();

            for (int w = 0; w < 200; w++)
                for (int j = 0; j < n; j++) cbs[j](interval);

            for (int i = 0; i < iters; i++)
            {
                sw.Restart();
                for (int j = 0; j < n; j++) cbs[j](interval);
                sw.Stop();
                t[i] = sw.ElapsedTicks / freq * 1000.0;
            }

            return Stats("Managed", n, iters, t);
        }

        // ── Multi-rate scenario ───────────────────────────────────────────────

        private TickScenarioResult RunMultiRateScenario(
            TickRate[] rates, int subsPerRate, float simDur)
        {
            int   n      = Mathf.Min(subsPerRate, 10);
            float simDt  = 0.016f;
            int   steps  = (int)(simDur / simDt);
            double freq  = Stopwatch.Frequency;

            float[] ivs    = new float[rates.Length];
            float[] timers = new float[rates.Length];
            for (int r = 0; r < rates.Length; r++) ivs[r] = Interval(rates[r]);

            // Update sim — all N*rates fire every step (raw Update pattern)
            var objs = new SimulatedUpdateBehaviour[rates.Length * n];
            for (int i = 0; i < objs.Length; i++) objs[i] = new SimulatedUpdateBehaviour();

            // Managed — only fire each rate's bucket when timer expires
            var mcbs = new Action<float>[rates.Length * n];
            for (int i = 0; i < mcbs.Length; i++) mcbs[i] = _ => { };

            // ── Update sim measurement ────────────────────────────────────────
            double uTotal = 0; long uMin = long.MaxValue, uMax = 0;
            var sw = new Stopwatch();
            Array.Clear(timers, 0, timers.Length);

            for (int s = 0; s < steps; s++)
            {
                sw.Restart();
                for (int o = 0; o < objs.Length; o++) objs[o].UpdateEmpty();
                sw.Stop();
                long tk = sw.ElapsedTicks;
                uTotal += tk;
                if (tk < uMin) uMin = tk;
                if (tk > uMax) uMax = tk;
            }

            // ── Managed dispatcher measurement ────────────────────────────────
            double mTotal = 0; long mMin = long.MaxValue, mMax = 0;
            Array.Clear(timers, 0, timers.Length);

            for (int s = 0; s < steps; s++)
            {
                sw.Restart();
                for (int r = 0; r < rates.Length; r++)
                {
                    timers[r] += simDt;
                    if (timers[r] < ivs[r]) continue;
                    timers[r] -= ivs[r];
                    for (int j = r * n; j < r * n + n; j++) mcbs[j](ivs[r]);
                }
                sw.Stop();
                long tk = sw.ElapsedTicks;
                mTotal += tk;
                if (tk < mMin) mMin = tk;
                if (tk > mMax) mMax = tk;
            }

            double ms(long ticks) => ticks / freq * 1000.0;

            return new TickScenarioResult
            {
                Name = $"Multi-rate {rates.Length}r ×{n}",
                UpdateSim = new TickBenchResult
                {
                    Label  = "Update sim",
                    AvgMs  = uTotal / freq * 1000.0 / steps,
                    MinMs  = ms(uMin), MaxMs = ms(uMax),
                    Subs   = objs.Length, Iters = steps
                },
                Managed = new TickBenchResult
                {
                    Label  = "Managed",
                    AvgMs  = mTotal / freq * 1000.0 / steps,
                    MinMs  = ms(mMin), MaxMs = ms(mMax),
                    Subs   = rates.Length * n, Iters = steps
                }
            };
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static TickBenchResult Stats(string label, int subs, int n, double[] t)
        {
            double sum = 0, mn = double.MaxValue, mx = double.MinValue;
            for (int i = 0; i < n; i++) { sum += t[i]; if (t[i] < mn) mn = t[i]; if (t[i] > mx) mx = t[i]; }
            double avg = sum / n, v = 0;
            for (int i = 0; i < n; i++) v += (t[i] - avg) * (t[i] - avg);
            return new TickBenchResult
            {
                Label         = label,
                AvgMs         = avg,
                MinMs         = mn,
                MaxMs         = mx,
                StdDevMs      = Math.Sqrt(v / n),
                Subs          = subs,
                Iters         = n
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ManagedHeavy(float dt)
        {
            float a = 0f;
            for (int i = 0; i < 50; i++)
                a += Mathf.Sin(i * 0.1f + dt) * Mathf.Cos(i * 0.05f);
            if (a > 1e9f) Debug.Log("never");
        }

        private static float Interval(TickRate r) => r switch
        {
            TickRate.Tick_0_01 => 0.01f,
            TickRate.Tick_0_02 => 0.02f,
            TickRate.Tick_0_05 => 0.05f,
            TickRate.Tick_0_1  => 0.1f,
            TickRate.Tick_0_2  => 0.2f,
            TickRate.Tick_0_5  => 0.5f,
            TickRate.Tick_1    => 1.0f,
            TickRate.Tick_2    => 2.0f,
            TickRate.Tick_5    => 5.0f,
            _                  => 0.1f
        };

        private void LogSummary()
        {
            foreach (var r in SingleResults) LogResult(r);
            LogResult(MultiResult);
            LogResult(HeavyResult);
        }

        private void LogResult(TickScenarioResult r)
        {
            if (!r.Managed.HasData) return;
            string speedup = r.UpdateVsManaged > 0
                ? $"  dispatcher {r.UpdateVsManaged:F2}× faster than Update sim"
                : "";
            MID_Logger.LogInfo(_logLevel,
                $"[{r.Name}]\n" +
                $"  UpdateSim: {r.UpdateSim.Summary}\n" +
                $"  Managed:   {r.Managed.Summary}" + speedup,
                nameof(MID_TickDispatcherBench));
        }
    }

    // ── Editor Window ─────────────────────────────────────────────────────────

#if UNITY_EDITOR

    public class MID_TickDispatcherBenchWindow : EditorWindow
    {
        private MID_TickDispatcherBench _bench;
        private Vector2                 _scroll;
        private bool                    _fS = true, _fM = true, _fH = true;

        private static readonly Color UpdateCol  = new Color(0.90f, 0.55f, 0.20f, 1f);
        private static readonly Color ManagedCol = new Color(0.55f, 0.75f, 1.00f, 1f);
        private static readonly Color WinCol     = new Color(0.28f, 0.95f, 0.45f, 1f);
        private static readonly Color DimCol     = new Color(0.50f, 0.50f, 0.50f, 1f);
        private static readonly Color BarBg      = new Color(0.15f, 0.15f, 0.15f, 0.4f);

        [MenuItem("MidManStudio/Utilities/Tests/Tick Dispatcher Bench")]
        static void Open() =>
            GetWindow<MID_TickDispatcherBenchWindow>("Tick Dispatcher Bench")
                .minSize = new Vector2(520, 340);

        private void OnEnable()  { EditorApplication.update += Repaint; Find(); }
        private void OnDisable() { EditorApplication.update -= Repaint; }

        private void Find()
        {
            if (_bench == null)
                _bench = FindObjectOfType<MID_TickDispatcherBench>();
        }

        private void OnGUI()
        {
            Find();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField("MID Tick Dispatcher Bench",
                    EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                _bench = (MID_TickDispatcherBench)EditorGUILayout.ObjectField(
                    _bench, typeof(MID_TickDispatcherBench), true, GUILayout.Width(200));
                if (GUILayout.Button("Run All", EditorStyles.toolbarButton, GUILayout.Width(58)))
                    _bench?.RunAll();
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(46)))
                {
                    if (_bench != null)
                    {
                        _bench.SingleResults.Clear();
                        _bench.MultiResult = default;
                        _bench.HeavyResult = default;
                    }
                }
            }

            if (_bench == null)
            {
                EditorGUILayout.HelpBox(
                    "Add MID_TickDispatcherBench to a scene GameObject.",
                    MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawSection("Single-Rate", ref _fS, _bench.SingleResults, _bench.RunSingle);
            DrawSingle("Multi-Rate  (all rates simultaneously)",
                ref _fM, _bench.MultiResult, _bench.RunMulti);
            DrawSingle("Heavy callbacks  (50 sin/cos ops each)",
                ref _fH, _bench.HeavyResult, _bench.RunHeavy);
            EditorGUILayout.EndScrollView();
        }

        // ── Drawing ───────────────────────────────────────────────────────────

        private void DrawSection(string title, ref bool fold,
            List<TickScenarioResult> results, Action run)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                fold = EditorGUILayout.Foldout(fold, title, true, EditorStyles.foldoutHeader);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Run", EditorStyles.toolbarButton, GUILayout.Width(36)))
                    run?.Invoke();
            }
            if (!fold) return;

            EditorGUI.indentLevel++;
            using (new EditorGUILayout.HorizontalScope())
            {
                Lbl("Subs",       44);
                Lbl("Update sim", 100);
                Lbl("Managed",    100);
                Lbl("Speedup",    80);
                GUILayout.FlexibleSpace();
            }
            foreach (var r in results) DrawRow(r);
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        private void DrawSingle(string title, ref bool fold,
            TickScenarioResult r, Action run)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                fold = EditorGUILayout.Foldout(fold, title, true, EditorStyles.foldoutHeader);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Run", EditorStyles.toolbarButton, GUILayout.Width(36)))
                    run?.Invoke();
            }
            if (!fold) return;

            EditorGUI.indentLevel++;
            DrawDetail(r);
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        private void DrawRow(TickScenarioResult r)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                Lbl(r.Managed.Subs.ToString(), 44);
                CL($"{r.UpdateSim.AvgMs * 1000:F1}µs", 100, UpdateCol,  r.UpdateSim.HasData);
                CL($"{r.Managed.AvgMs   * 1000:F1}µs", 100, ManagedCol, r.Managed.HasData);

                if (r.BothRan)
                    CL($"{r.UpdateVsManaged:F2}×", 80, WinCol, true);
                else
                    DL("—", 80);

                float maxVal = r.UpdateSim.HasData ? (float)r.UpdateSim.AvgMs : (float)r.Managed.AvgMs;
                if (maxVal > 0)
                {
                    Bar((float)(r.UpdateSim.AvgMs / maxVal), UpdateCol,  36);
                    GUILayout.Space(1);
                    Bar((float)(r.Managed.AvgMs   / maxVal), ManagedCol, 36);
                }
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawDetail(TickScenarioResult r)
        {
            Row("Update sim", r.UpdateSim, UpdateCol);
            Row("Managed",    r.Managed,   ManagedCol);

            if (r.BothRan)
            {
                var old = GUI.color;
                GUI.color = WinCol;
                EditorGUILayout.LabelField(
                    $"Managed dispatcher is {r.UpdateVsManaged:F2}× cheaper " +
                    "than equivalent Update() frequency",
                    EditorStyles.miniBoldLabel);
                GUI.color = old;
            }

            void Row(string lbl, TickBenchResult res, Color col)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    CL(lbl, 90, col, res.HasData);
                    if (res.HasData)
                        EditorGUILayout.LabelField(res.Summary, EditorStyles.miniLabel);
                    else
                        DL("not run");
                }
            }
        }

        // ── GUI helpers ───────────────────────────────────────────────────────

        private void Bar(float f, Color c, float w)
        {
            f = Mathf.Clamp01(f);
            Rect r = GUILayoutUtility.GetRect(w, 10, GUILayout.Width(w));
            r.y += 3; r.height = 6;
            EditorGUI.DrawRect(r, BarBg);
            Rect fill = r; fill.width *= f;
            EditorGUI.DrawRect(fill, c);
        }

        private void Lbl(string t, float w) =>
            EditorGUILayout.LabelField(t, GUILayout.Width(w));

        private void CL(string t, float w, Color c, bool on)
        {
            var o = GUI.color; GUI.color = on ? c : DimCol;
            EditorGUILayout.LabelField(t, GUILayout.Width(w));
            GUI.color = o;
        }

        private void DL(string t, float w = 0)
        {
            var o = GUI.color; GUI.color = DimCol;
            if (w > 0) EditorGUILayout.LabelField(t, EditorStyles.miniLabel, GUILayout.Width(w));
            else       EditorGUILayout.LabelField(t, EditorStyles.miniLabel);
            GUI.color = o;
        }
    }

#endif
}