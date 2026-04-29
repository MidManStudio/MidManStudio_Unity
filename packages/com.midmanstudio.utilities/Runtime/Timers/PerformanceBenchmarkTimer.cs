using UnityEngine;
using System;
using System.Collections.Generic;
using Debug = UnityEngine.Debug;
using System.Diagnostics;
using MidManStudio.Core.EditorTools;
namespace MidManStudio.Core.Timers
{
    public class PerformanceBenchmarkTimer
    {
        #region Benchmark Result

        public struct BenchmarkResult
        {
            public int    iterations;
            public double totalTimeMs;
            public double averageTimeMs;
            public double minTimeMs;
            public double maxTimeMs;
            public double standardDeviation;
            public long   totalMemoryAllocated;
            public long   averageMemoryPerIteration;

            public override string ToString() =>
                $"<b>Benchmark Results:</b>\n" +
                $"Iterations: {iterations}\n" +
                $"Total Time: {totalTimeMs:F3} ms\n" +
                $"Average Time: {averageTimeMs:F3} ms\n" +
                $"Min Time: {minTimeMs:F3} ms\n" +
                $"Max Time: {maxTimeMs:F3} ms\n" +
                $"Std Deviation: {standardDeviation:F3} ms\n" +
                $"Total Memory: {totalMemoryAllocated / 1024f:F2} KB\n" +
                $"Avg Memory/Iter: {averageMemoryPerIteration:F2} bytes";

            public string ToCSV() =>
                $"{iterations},{totalTimeMs:F3},{averageTimeMs:F3},{minTimeMs:F3}," +
                $"{maxTimeMs:F3},{standardDeviation:F3},{totalMemoryAllocated}," +
                $"{averageMemoryPerIteration}";
        }

        #endregion

        #region Private Variables

        private readonly Stopwatch    _stopwatch;
        private readonly List<double> _iterationTimes;
        private long _startMemory;
        private long _endMemory;

        #endregion

        #region Constructor

        public PerformanceBenchmarkTimer()
        {
            _stopwatch      = new Stopwatch();
            _iterationTimes = new List<double>();
        }

        #endregion

        #region Benchmark Methods

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

            for (int i = 0; i < warmupIterations; i++) action();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            _iterationTimes.Clear();
            _startMemory = GC.GetTotalMemory(false);

            _stopwatch.Restart();
            for (int i = 0; i < iterations; i++)
            {
                long iterStart = _stopwatch.ElapsedTicks;
                try   { action(); }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[PerformanceBenchmarkTimer] Exception at iteration {i}: {ex.Message}");
                    continue;
                }
                double ms = (_stopwatch.ElapsedTicks - iterStart)
                            * 1000.0 / Stopwatch.Frequency;
                _iterationTimes.Add(ms);
            }
            _stopwatch.Stop();
            _endMemory = GC.GetTotalMemory(false);

            return CalculateResults(iterations);
        }

        public BenchmarkResult RunBenchmark<T>(Action<T> action, T parameter,
                                                int iterations, int warmup = 10) =>
            RunBenchmark(() => action(parameter), iterations, warmup);

        public BenchmarkResult RunBenchmark<TResult>(Func<TResult> func,
                                                      int iterations, int warmup = 10)
        {
            TResult r = default;
            return RunBenchmark(() => { r = func(); }, iterations, warmup);
        }

        public (BenchmarkResult methodA, BenchmarkResult methodB, string comparison)
            CompareMethods(Action methodA, Action methodB, int iterations,
                           string methodAName = "Method A", string methodBName = "Method B")
        {
            Debug.Log(
                $"<color=yellow>Starting comparison: {methodAName} vs {methodBName}</color>");

            var resultA = RunBenchmark(methodA, iterations);
            System.Threading.Thread.Sleep(100);
            GC.Collect();
            var resultB = RunBenchmark(methodB, iterations);

            double diff    = resultB.averageTimeMs - resultA.averageTimeMs;
            double pct     = diff / resultA.averageTimeMs * 100.0;
            string compare =
                $"<b>Comparison Results:</b>\n" +
                $"{methodAName}: {resultA.averageTimeMs:F3} ms average\n" +
                $"{methodBName}: {resultB.averageTimeMs:F3} ms average\n" +
                $"Difference: {diff:F3} ms ({pct:F1}%)\n" +
                $"{(diff < 0 ? methodBName + " is SLOWER" : methodAName + " is SLOWER")}";

            return (resultA, resultB, compare);
        }

        #endregion

        #region Calculation

        private BenchmarkResult CalculateResults(int iterations)
        {
            double total = 0, min = double.MaxValue, max = 0;
            foreach (double t in _iterationTimes)
            {
                total += t;
                if (t < min) min = t;
                if (t > max) max = t;
            }
            double avg = total / _iterationTimes.Count;
            double v   = 0;
            foreach (double t in _iterationTimes) v += (t - avg) * (t - avg);

            long mem    = _endMemory - _startMemory;
            long avgMem = mem / iterations;

            return new BenchmarkResult
            {
                iterations                = iterations,
                totalTimeMs               = total,
                averageTimeMs             = avg,
                minTimeMs                 = min,
                maxTimeMs                 = max,
                standardDeviation         = Math.Sqrt(v / _iterationTimes.Count),
                totalMemoryAllocated      = mem,
                averageMemoryPerIteration = avgMem,
            };
        }

        #endregion

        #region Static Convenience

        public static BenchmarkResult QuickBenchmark(Action action,
                                                      int iterations = 1000, int warmup = 10) =>
            new PerformanceBenchmarkTimer().RunBenchmark(action, iterations, warmup);

        public static (BenchmarkResult, BenchmarkResult, string) QuickCompare(
            Action methodA, Action methodB, int iterations = 1000,
            string nameA = "Method A", string nameB = "Method B") =>
            new PerformanceBenchmarkTimer().CompareMethods(
                methodA, methodB, iterations, nameA, nameB);

        #endregion
    }

    // ─────────────────────────────────────────────────────────────────────────

    public class PerformanceBenchmarkRunner : MonoBehaviour
    {
        [Header("Benchmark Configuration")]
        [SerializeField] private int  iterations       = 1000;
        [SerializeField] private int  warmupIterations = 10;
        [SerializeField] private bool logToConsole     = true;
        [SerializeField] private bool logToDebugPanel  = true;

        private PerformanceBenchmarkTimer _benchmarkTimer;

        private void Awake() =>
            _benchmarkTimer = new PerformanceBenchmarkTimer();

        public PerformanceBenchmarkTimer.BenchmarkResult RunBenchmark(
            Action action, string benchmarkName = "Benchmark")
        {
            var result = _benchmarkTimer.RunBenchmark(action, iterations, warmupIterations);

            if (logToConsole)
                Debug.Log($"<color=cyan><b>{benchmarkName}</b></color>\n{result}");

#if UNITY_EDITOR
            if (logToDebugPanel && DynamicDebugPanel.Instance != null)
                DynamicDebugPanel.Instance.AddLog(
                    $"{benchmarkName}: {result.averageTimeMs:F3} ms avg");
#endif
            return result;
        }

        public void CompareMethods(Action methodA, Action methodB,
                                    string nameA = "Method A", string nameB = "Method B")
        {
            var (_, _, comparison) =
                _benchmarkTimer.CompareMethods(methodA, methodB, iterations, nameA, nameB);

            if (logToConsole)
                Debug.Log($"<color=green>{comparison}</color>");

#if UNITY_EDITOR
            if (logToDebugPanel && DynamicDebugPanel.Instance != null)
                DynamicDebugPanel.Instance.AddLog(comparison);
#endif
        }

        [ContextMenu("Run Example Benchmark")]
        private void RunExampleBenchmark()
        {
            var sb = new System.Text.StringBuilder();

            Action stringConcat = () =>
            {
                string r = "";
                for (int i = 0; i < 100; i++) r += "test";
            };

            Action stringBuilder = () =>
            {
                sb.Clear();
                for (int i = 0; i < 100; i++) sb.Append("test");
            };

            CompareMethods(stringConcat, stringBuilder,
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