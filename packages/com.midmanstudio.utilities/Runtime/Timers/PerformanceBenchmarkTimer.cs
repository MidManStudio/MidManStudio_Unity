// PerformanceBenchmarkTimer.cs
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
    public class PerformanceBenchmarkTimer
    {
        #region Result

        public struct BenchmarkResult
        {
            public int    Iterations;
            public double TotalTimeMs;
            public double AverageTimeMs;
            public double MinTimeMs;
            public double MaxTimeMs;
            public double StandardDeviation;
            public long   TotalMemoryAllocated;
            public long   AverageMemoryPerIteration;
            public int    ExceptionCount;

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

        public BenchmarkResult RunBenchmark<T>(Action<T> action, T parameter,
            int iterations, int warmup = 10) =>
            RunBenchmark(() => action(parameter), iterations, warmup);

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

        public static BenchmarkResult QuickBenchmark(Action action,
            int iterations = 1000, int warmup = 10) =>
            new PerformanceBenchmarkTimer().RunBenchmark(action, iterations, warmup);

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
