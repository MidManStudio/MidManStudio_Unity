using UnityEngine;
using System;
using System.Diagnostics;
using System.Collections.Generic;

namespace MidManStudio.Core.Timers
{
    /// <summary>
    /// Advanced benchmark timer for performance testing
    /// Extends the timer system with iteration-based benchmarking
    /// </summary>
    public class PerformanceBenchmarkTimer
    {
        #region Benchmark Result
        public struct BenchmarkResult
        {
            public int iterations;
            public double totalTimeMs;
            public double averageTimeMs;
            public double minTimeMs;
            public double maxTimeMs;
            public double standardDeviation;
            public long totalMemoryAllocated;
            public long averageMemoryPerIteration;

            public override string ToString()
            {
                return $"<b>Benchmark Results:</b>\n" +
                       $"Iterations: {iterations}\n" +
                       $"Total Time: {totalTimeMs:F3} ms\n" +
                       $"Average Time: {averageTimeMs:F3} ms\n" +
                       $"Min Time: {minTimeMs:F3} ms\n" +
                       $"Max Time: {maxTimeMs:F3} ms\n" +
                       $"Std Deviation: {standardDeviation:F3} ms\n" +
                       $"Total Memory: {totalMemoryAllocated / 1024f:F2} KB\n" +
                       $"Avg Memory/Iter: {averageMemoryPerIteration:F2} bytes";
            }

            public string ToCSV()
            {
                return $"{iterations},{totalTimeMs:F3},{averageTimeMs:F3},{minTimeMs:F3}," +
                       $"{maxTimeMs:F3},{standardDeviation:F3},{totalMemoryAllocated}," +
                       $"{averageMemoryPerIteration}";
            }
        }
        #endregion

        #region Private Variables
        private Stopwatch stopwatch;
        private List<double> iterationTimes;
        private long startMemory;
        private long endMemory;
        #endregion

        #region Constructor
        public PerformanceBenchmarkTimer()
        {
            stopwatch = new Stopwatch();
            iterationTimes = new List<double>();
        }
        #endregion

        #region Benchmark Methods
        /// <summary>
        /// Run a benchmark with specified iterations
        /// </summary>
        /// <param name="action">Action to benchmark</param>
        /// <param name="iterations">Number of times to run the action</param>
        /// <param name="warmupIterations">Warmup runs before actual benchmark (default: 10)</param>
        /// <returns>Benchmark results</returns>
        public BenchmarkResult RunBenchmark(Action action, int iterations, int warmupIterations = 10)
        {
            if (action == null)
            {
                UnityEngine.Debug.LogError("Cannot benchmark null action");
                return default;
            }

            if (iterations <= 0)
            {
                UnityEngine.Debug.LogError("Iterations must be greater than 0");
                return default;
            }

            // Warm up (allows JIT compilation and cache warming)
            for (int i = 0; i < warmupIterations; i++)
            {
                action();
            }

            // Force garbage collection before benchmark
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Reset tracking
            iterationTimes.Clear();
            startMemory = GC.GetTotalMemory(false);

            // Run benchmark
            stopwatch.Restart();

            for (int i = 0; i < iterations; i++)
            {
                long iterStartTime = stopwatch.ElapsedTicks;

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"Exception during benchmark iteration {i}: {ex.Message}");
                    continue;
                }

                long iterEndTime = stopwatch.ElapsedTicks;
                double iterationTimeMs = (iterEndTime - iterStartTime) * 1000.0 / Stopwatch.Frequency;
                iterationTimes.Add(iterationTimeMs);
            }

            stopwatch.Stop();
            endMemory = GC.GetTotalMemory(false);

            // Calculate results
            return CalculateResults(iterations);
        }

        /// <summary>
        /// Run a benchmark with a parameterized action
        /// </summary>
        public BenchmarkResult RunBenchmark<T>(Action<T> action, T parameter, int iterations, int warmupIterations = 10)
        {
            return RunBenchmark(() => action(parameter), iterations, warmupIterations);
        }

        /// <summary>
        /// Run a benchmark that returns a value (useful for preventing compiler optimization)
        /// </summary>
        public BenchmarkResult RunBenchmark<TResult>(Func<TResult> func, int iterations, int warmupIterations = 10)
        {
            TResult result = default;
            return RunBenchmark(() => { result = func(); }, iterations, warmupIterations);
        }

        /// <summary>
        /// Compare two methods and return results for both
        /// </summary>
        public (BenchmarkResult methodA, BenchmarkResult methodB, string comparison) CompareMethods(
            Action methodA,
            Action methodB,
            int iterations,
            string methodAName = "Method A",
            string methodBName = "Method B")
        {
            UnityEngine.Debug.Log($"<color=yellow>Starting comparison: {methodAName} vs {methodBName}</color>");

            var resultA = RunBenchmark(methodA, iterations);

            // Short delay between benchmarks
            System.Threading.Thread.Sleep(100);
            GC.Collect();

            var resultB = RunBenchmark(methodB, iterations);

            // Calculate comparison
            double timeDifference = resultB.averageTimeMs - resultA.averageTimeMs;
            double percentDifference = (timeDifference / resultA.averageTimeMs) * 100.0;

            string comparison = $"<b>Comparison Results:</b>\n" +
                              $"{methodAName}: {resultA.averageTimeMs:F3} ms average\n" +
                              $"{methodBName}: {resultB.averageTimeMs:F3} ms average\n" +
                              $"Difference: {timeDifference:F3} ms ({percentDifference:F1}%)\n" +
                              $"{(timeDifference < 0 ? methodBName + " is SLOWER" : methodAName + " is SLOWER")}";

            return (resultA, resultB, comparison);
        }
        #endregion

        #region Calculation
        private BenchmarkResult CalculateResults(int iterations)
        {
            double totalTime = 0;
            double minTime = double.MaxValue;
            double maxTime = 0;

            foreach (double time in iterationTimes)
            {
                totalTime += time;
                if (time < minTime) minTime = time;
                if (time > maxTime) maxTime = time;
            }

            double averageTime = totalTime / iterationTimes.Count;

            // Calculate standard deviation
            double variance = 0;
            foreach (double time in iterationTimes)
            {
                variance += Math.Pow(time - averageTime, 2);
            }
            variance /= iterationTimes.Count;
            double stdDev = Math.Sqrt(variance);

            // Memory calculations
            long memoryAllocated = endMemory - startMemory;
            long avgMemoryPerIter = memoryAllocated / iterations;

            return new BenchmarkResult
            {
                iterations = iterations,
                totalTimeMs = totalTime,
                averageTimeMs = averageTime,
                minTimeMs = minTime,
                maxTimeMs = maxTime,
                standardDeviation = stdDev,
                totalMemoryAllocated = memoryAllocated,
                averageMemoryPerIteration = avgMemoryPerIter
            };
        }
        #endregion

        #region Static Convenience Methods
        /// <summary>
        /// Quick benchmark without creating timer instance
        /// </summary>
        public static BenchmarkResult QuickBenchmark(Action action, int iterations = 1000, int warmup = 10)
        {
            var timer = new PerformanceBenchmarkTimer();
            return timer.RunBenchmark(action, iterations, warmup);
        }

        /// <summary>
        /// Quick comparison without creating timer instance
        /// </summary>
        public static (BenchmarkResult methodA, BenchmarkResult methodB, string comparison) QuickCompare(
            Action methodA,
            Action methodB,
            int iterations = 1000,
            string nameA = "Method A",
            string nameB = "Method B")
        {
            var timer = new PerformanceBenchmarkTimer();
            return timer.CompareMethods(methodA, methodB, iterations, nameA, nameB);
        }
        #endregion
    }

    /// <summary>
    /// MonoBehaviour wrapper for easy use in Unity
    /// </summary>
    public class PerformanceBenchmarkRunner : MonoBehaviour
    {
        [Header("Benchmark Configuration")]
        [SerializeField] private int iterations = 1000;
        [SerializeField] private int warmupIterations = 10;
        [SerializeField] private bool logToConsole = true;
        [SerializeField] private bool logToDebugPanel = true;

        private PerformanceBenchmarkTimer benchmarkTimer;

        private void Awake()
        {
            benchmarkTimer = new PerformanceBenchmarkTimer();
        }

        /// <summary>
        /// Run a benchmark and optionally log results
        /// </summary>
        public PerformanceBenchmarkTimer.BenchmarkResult RunBenchmark(Action action, string benchmarkName = "Benchmark")
        {
            var result = benchmarkTimer.RunBenchmark(action, iterations, warmupIterations);

            if (logToConsole)
            {
                UnityEngine.Debug.Log($"<color=cyan><b>{benchmarkName}</b></color>\n{result}");
            }

            if (logToDebugPanel && DynamicDebugPanel.Instance != null)
            {
                DynamicDebugPanel.Instance.AddLog($"{benchmarkName}: {result.averageTimeMs:F3} ms avg");
            }

            return result;
        }

        /// <summary>
        /// Compare two methods
        /// </summary>
        public void CompareMethods(Action methodA, Action methodB, string nameA = "Method A", string nameB = "Method B")
        {
            var (resultA, resultB, comparison) = benchmarkTimer.CompareMethods(
                methodA, methodB, iterations, nameA, nameB);

            if (logToConsole)
            {
                UnityEngine.Debug.Log($"<color=green>{comparison}</color>");
            }

            if (logToDebugPanel && DynamicDebugPanel.Instance != null)
            {
                DynamicDebugPanel.Instance.AddLog(comparison);
            }
        }

        #region Example Usage
        [ContextMenu("Run Example Benchmark")]
        private void RunExampleBenchmark()
        {
            // Example: Benchmark string concatenation vs StringBuilder
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            Action stringConcat = () =>
            {
                string result = "";
                for (int i = 0; i < 100; i++)
                {
                    result += "test";
                }
            };

            Action stringBuilder = () =>
            {
                sb.Clear();
                for (int i = 0; i < 100; i++)
                {
                    sb.Append("test");
                }
            };

            CompareMethods(stringConcat, stringBuilder, "String Concat", "StringBuilder");
        }

        [ContextMenu("Benchmark GameObject Instantiation")]
        private void BenchmarkInstantiation()
        {
            GameObject prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);

            Action instantiate = () =>
            {
                GameObject obj = Instantiate(prefab);
                Destroy(obj);
            };

            RunBenchmark(instantiate, "GameObject Instantiation");

            Destroy(prefab);
        }
        #endregion
    }
}