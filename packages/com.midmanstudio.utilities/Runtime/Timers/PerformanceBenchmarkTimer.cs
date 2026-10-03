// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/timers.md, section "PerformanceBenchmarkTimer.cs"
// ============================================================================
// Microbenchmark utility for measuring managed code performance.
// Handles warmup, GC collection, per-iteration timing, and memory tracking.
// PerformanceBenchmarkRunner is a MonoBehaviour wrapper for scene use.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using MidManStudio.Core.Logging;
using Debug = UnityEngine.Debug;

namespace MidManStudio.Core.Timers
{
    /// <summary>
    /// Microbenchmarks a managed-code action: warms it up (JIT + cache),
    /// forces a GC pass, then times and memory-samples a fixed number of
    /// iterations. Not a managed wrapper over native profiling tools; this
    /// measures wall-clock time via <see cref="Stopwatch"/> and GC-reported
    /// memory delta, which is adequate for comparing two managed approaches
    /// but not a substitute for the Unity Profiler for native/engine cost.
    /// </summary>
    public class PerformanceBenchmarkTimer
    {
        #region Result

        /// <summary>Aggregated stats from one <see cref="RunBenchmark"/> call.</summary>
        public struct BenchmarkResult
        {
            /// <summary>Iterations actually recorded (may be less than requested if the action threw).</summary>
            public int    Iterations;
            /// <summary>Sum of every iteration's time, in milliseconds.</summary>
            public double TotalTimeMs;
            /// <summary>Mean iteration time, in milliseconds.</summary>
            public double AverageTimeMs;
            /// <summary>Fastest iteration, in milliseconds.</summary>
            public double MinTimeMs;
            /// <summary>Slowest iteration, in milliseconds.</summary>
            public double MaxTimeMs;
            /// <summary>Population standard deviation of iteration times, in milliseconds.</summary>
            public double StandardDeviation;
            /// <summary>Total managed memory delta across the whole run, in bytes (see <see cref="RunBenchmark"/> for why this can only be approximate).</summary>
            public long   TotalMemoryAllocated;
            /// <summary><see cref="TotalMemoryAllocated"/> divided by <see cref="Iterations"/>.</summary>
            public long   AverageMemoryPerIteration;
            /// <summary>How many iterations threw an exception (still counted and timed; see <see cref="RunBenchmark"/>).</summary>
            public int    ExceptionCount;

            /// <summary>Multi-line human-readable summary, rich-text tagged for the Unity console.</summary>
            public override string ToString() =>
                $"<b>Benchmark Results:</b>\n" +
                $"Iterations:   {Iterations}\n" +
                $"Total Time:   {TotalTimeMs:F3} ms\n" +
                $"Average Time: {AverageTimeMs:F3} ms\n" +
                $"Min Time:     {MinTimeMs:F3} ms\n" +
                $"Max Time:     {MaxTimeMs:F3} ms\n" +
                $"Std Dev:      {StandardDeviation:F3} ms\n" +
                $"Total Memory: {TotalMemoryAllocated / 1024f:F2} KB\n" +
                $"Avg Mem/Iter: {AverageMemoryPerIteration} bytes" +
                (ExceptionCount > 0 ? $"\n⚠ Exceptions: {ExceptionCount}" : "");

            /// <summary>Single CSV line: Iterations,TotalTimeMs,AverageTimeMs,MinTimeMs,MaxTimeMs,StandardDeviation,TotalMemoryAllocated,AverageMemoryPerIteration,ExceptionCount.</summary>
            public string ToCSV() =>
                $"{Iterations},{TotalTimeMs:F3},{AverageTimeMs:F3},{MinTimeMs:F3}," +
                $"{MaxTimeMs:F3},{StandardDeviation:F3}," +
                $"{TotalMemoryAllocated},{AverageMemoryPerIteration},{ExceptionCount}";
        }

        #endregion

        #region Fields

        private readonly Stopwatch    _sw             = new();
        private readonly List<double> _iterationTimes = new();

        #endregion

        #region Public API

        /// <summary>
        /// Run a benchmark with warmup, GC collection, and per-iteration timing.
        /// An iteration that throws is still counted and timed (so the
        /// iteration count and GC delta stay consistent with the loop that
        /// actually ran); see <see cref="BenchmarkResult.ExceptionCount"/>.
        /// The memory delta is measured once across the whole run (before vs.
        /// after), not per iteration, so a GC pass triggered mid-run by
        /// something other than this benchmark will skew it — this is an
        /// approximation, not an exact per-iteration allocation count.
        /// </summary>
        public BenchmarkResult RunBenchmark(Action action, int iterations,
            int warmupIterations = 10)
        {
            if (action == null)
            {
                Debug.LogError("[PerformanceBenchmarkTimer] Cannot benchmark null action.");
                return default;
            }
            if (iterations <= 0)
            {
                Debug.LogError("[PerformanceBenchmarkTimer] Iterations must be > 0.");
                return default;
            }

            // Warmup — JIT compile, cache warm
            for (int i = 0; i < warmupIterations; i++)
            {
                try { action(); } catch { /* ignore warmup exceptions */ }
            }

            // Force GC before measurement
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            _iterationTimes.Clear();
            int exceptionCount = 0;

            long memBefore = GC.GetTotalMemory(false);
            _sw.Restart();

            for (int i = 0; i < iterations; i++)
            {
                long tickStart = _sw.ElapsedTicks;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    exceptionCount++;
                    Debug.LogWarning(
                        $"[PerformanceBenchmarkTimer] Exception at iteration {i}: {ex.Message}");
                    // Still record the time so iteration counts stay consistent
                }
                double ms = (_sw.ElapsedTicks - tickStart) * 1000.0 / Stopwatch.Frequency;
                _iterationTimes.Add(ms);
            }

            _sw.Stop();
            // Use Math.Abs — GC may have run during bench making delta negative
            long memDelta = Math.Abs(GC.GetTotalMemory(false) - memBefore);

            return CalculateResults(exceptionCount, memDelta);
        }

        /// <summary>Overload of <see cref="RunBenchmark(Action, int, int)"/> for a one-argument action, binding <paramref name="parameter"/> once up front.</summary>
        public BenchmarkResult RunBenchmark<T>(Action<T> action, T parameter,
            int iterations, int warmup = 10) =>
            RunBenchmark(() => action(parameter), iterations, warmup);

        /// <summary>Overload of <see cref="RunBenchmark(Action, int, int)"/> for a function with a return value; the result is discarded (only the timing is kept).</summary>
        public BenchmarkResult RunBenchmark<TResult>(Func<TResult> func,
            int iterations, int warmup = 10)
        {
            TResult _ = default;
            return RunBenchmark(() => { _ = func(); }, iterations, warmup);
        }

        /// <summary>
        /// Run two benchmarks and produce a comparison string.
        /// </summary>
        public (BenchmarkResult a, BenchmarkResult b, string comparison) CompareMethods(
            Action methodA, Action methodB, int iterations,
            string nameA = "Method A", string nameB = "Method B")
        {
            Debug.Log($"<color=yellow>Comparing: {nameA} vs {nameB}</color>");

            var resultA = RunBenchmark(methodA, iterations);
            System.Threading.Thread.Sleep(100);
            GC.Collect();
            var resultB = RunBenchmark(methodB, iterations);

            double diff = resultB.AverageTimeMs - resultA.AverageTimeMs;
            double pct  = resultA.AverageTimeMs > 0
                ? diff / resultA.AverageTimeMs * 100.0
                : 0;

            string winner  = diff < 0 ? nameB : nameA;
            string loser   = diff < 0 ? nameA : nameB;
            string compare =
                $"<b>Comparison:</b>\n" +
                $"{nameA}: {resultA.AverageTimeMs:F3} ms avg\n" +
                $"{nameB}: {resultB.AverageTimeMs:F3} ms avg\n" +
                $"Δ {Math.Abs(diff):F3} ms ({Math.Abs(pct):F1}%)\n" +
                $"→ {winner} is faster ({loser} is slower)";

            return (resultA, resultB, compare);
        }

        #endregion

        #region Private

        private BenchmarkResult CalculateResults(int exceptionCount, long memDelta)
        {
            // Use actual recorded count — not the requested iteration count —
            // so stats are correct even if some iterations were skipped.
            int n = _iterationTimes.Count;
            if (n == 0) return default;

            double total = 0, min = double.MaxValue, max = double.MinValue;
            foreach (double t in _iterationTimes)
            {
                total += t;
                if (t < min) min = t;
                if (t > max) max = t;
            }

            double avg = total / n;

            // Population variance (we own the full sample set)
            double variance = 0;
            foreach (double t in _iterationTimes)
                variance += (t - avg) * (t - avg);

            return new BenchmarkResult
            {
                Iterations               = n,
                TotalTimeMs              = total,
                AverageTimeMs            = avg,
                MinTimeMs                = min,
                MaxTimeMs                = max,
                StandardDeviation        = Math.Sqrt(variance / n),
                TotalMemoryAllocated     = memDelta,
                AverageMemoryPerIteration = n > 0 ? memDelta / n : 0,
                ExceptionCount           = exceptionCount
            };
        }

        #endregion

        #region Static Convenience

        /// <summary>One-shot <see cref="RunBenchmark(Action, int, int)"/> without needing to construct a <see cref="PerformanceBenchmarkTimer"/> yourself.</summary>
        public static BenchmarkResult QuickBenchmark(Action action,
            int iterations = 1000, int warmup = 10) =>
            new PerformanceBenchmarkTimer().RunBenchmark(action, iterations, warmup);

        /// <summary>One-shot <see cref="CompareMethods"/> without needing to construct a <see cref="PerformanceBenchmarkTimer"/> yourself.</summary>
        public static (BenchmarkResult, BenchmarkResult, string) QuickCompare(
            Action a, Action b, int iterations = 1000,
            string nameA = "Method A", string nameB = "Method B") =>
            new PerformanceBenchmarkTimer().CompareMethods(a, b, iterations, nameA, nameB);

        #endregion
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MonoBehaviour wrapper for running benchmarks from scene context.
    /// Exposes context menu actions for quick editor testing.
    /// </summary>
    public class PerformanceBenchmarkRunner : MonoBehaviour
    {
        [SerializeField] private MID_LogLevel _logLevel       = MID_LogLevel.Info;
        [SerializeField] private int  _iterations             = 1000;
        [SerializeField] private int  _warmupIterations       = 10;

        private PerformanceBenchmarkTimer _timer;

        private void Awake() => _timer = new PerformanceBenchmarkTimer();

        /// <summary>Runs <paramref name="action"/> through <see cref="PerformanceBenchmarkTimer.RunBenchmark(Action, int, int)"/> using this component's inspector-configured iteration/warmup counts, and logs the result.</summary>
        public PerformanceBenchmarkTimer.BenchmarkResult RunBenchmark(
            Action action, string benchmarkName = "Benchmark")
        {
            var result = _timer.RunBenchmark(action, _iterations, _warmupIterations);

            MID_Logger.LogInfo(_logLevel,
                $"[{benchmarkName}] avg={result.AverageTimeMs:F3}ms " +
                $"min={result.MinTimeMs:F3}ms max={result.MaxTimeMs:F3}ms " +
                $"σ={result.StandardDeviation:F3}ms",
                nameof(PerformanceBenchmarkRunner));

            return result;
        }

        /// <summary>Runs both methods through <see cref="PerformanceBenchmarkTimer.CompareMethods"/> using this component's inspector-configured iteration count, and logs the comparison.</summary>
        public void CompareMethods(Action methodA, Action methodB,
            string nameA = "Method A", string nameB = "Method B")
        {
            var (_, _, comparison) =
                _timer.CompareMethods(methodA, methodB, _iterations, nameA, nameB);
            MID_Logger.LogInfo(_logLevel, comparison, nameof(PerformanceBenchmarkRunner));
        }

        [ContextMenu("Run Example Benchmark (String Concat vs StringBuilder)")]
        private void RunExampleBenchmark()
        {
            var sb = new System.Text.StringBuilder();
            CompareMethods(
                () => { string r = ""; for (int i = 0; i < 100; i++) r += "x"; },
                () => { sb.Clear(); for (int i = 0; i < 100; i++) sb.Append("x"); },
                "String Concat", "StringBuilder");
        }

        [ContextMenu("Benchmark GameObject Instantiation")]
        private void BenchmarkInstantiation()
        {
            var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            RunBenchmark(() =>
            {
                var obj = Instantiate(prefab);
                Destroy(obj);
            }, "GameObject Instantiation");
            Destroy(prefab);
        }
    }
}
