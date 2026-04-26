// NativeDispatcherStressBench.cs
// Tests the ONLY scenario where MID_NativeTickDispatcher genuinely beats managed:
// 500+ static [BurstCompile] callbacks all doing meaningful SIMD math.
// Below this threshold the managed dispatcher wins — see file header in
// MID_NativeTickDispatcher for the full threshold analysis.

using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MidManStudio.Core.Benchmarks
{
   
    // ─────────────────────────────────────────────────────────────────────────

    public class NativeDispatcherStressBench : MonoBehaviour
    {
        [Header("Config")]
        public int Iterations = 2000;
        public int SubscriberCount = 30; // capped to available callbacks above (30)

        [Header("Results (read-only)")]
        public double ManagedAvgMs;
        public double NativeAvgMs;
        public double NativeSpeedup;
        public string Verdict;

        private static readonly MID_NativeTickDispatcher.NativeTickDelegate[] _allCbs =
        {
            StressCbs.C000,StressCbs.C001,StressCbs.C002,StressCbs.C003,StressCbs.C004,
            StressCbs.C005,StressCbs.C006,StressCbs.C007,StressCbs.C008,StressCbs.C009,
            StressCbs.C010,StressCbs.C011,StressCbs.C012,StressCbs.C013,StressCbs.C014,
            StressCbs.C015,StressCbs.C016,StressCbs.C017,StressCbs.C018,StressCbs.C019,
            StressCbs.C020,StressCbs.C021,StressCbs.C022,StressCbs.C023,StressCbs.C024,
            StressCbs.C025,StressCbs.C026,StressCbs.C027,StressCbs.C028,StressCbs.C029,
        };

        void Start() => RunNativeStress();

        [ContextMenu("Run Native Stress Bench")]
        public void RunNativeStress()
        {
            int n = Mathf.Clamp(SubscriberCount, 1, _allCbs.Length);
            int iter = Iterations;
            const float dt = 0.1f;

            // ── Compile FunctionPointers ──────────────────────────────────────
            var fps = new Unity.Burst.FunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>[n];
            try
            {
                for (int i = 0; i < n; i++)
                    fps[i] = Unity.Burst.BurstCompiler
                        .CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(_allCbs[i]);
            }
            catch (Exception e)
            {
                Debug.LogError($"[NativeStressBench] Burst compile failed: {e.Message}");
                return;
            }

            // Warm-up
            for (int w = 0; w < 200; w++)
            {
                for (int i = 0; i < n; i++) fps[i].Invoke(dt);
                for (int i = 0; i < n; i++) _allCbs[i](dt);
            }

            // ── Bench Managed (Action<float> delegate array) ──────────────────
            var actions = new Action<float>[n];
            for (int i = 0; i < n; i++) { int ci = i; actions[i] = d => _allCbs[ci](d); }

            var sw = Stopwatch.StartNew();
            for (int it = 0; it < iter; it++)
                for (int i = 0; i < n; i++) actions[i](dt);
            sw.Stop();
            ManagedAvgMs = sw.Elapsed.TotalMilliseconds / iter;

            // ── Bench Native (Burst FunctionPointer array) ────────────────────
            sw.Restart();
            for (int it = 0; it < iter; it++)
                for (int i = 0; i < n; i++) fps[i].Invoke(dt);
            sw.Stop();
            NativeAvgMs = sw.Elapsed.TotalMilliseconds / iter;

            NativeSpeedup = ManagedAvgMs / NativeAvgMs;
            bool nativeWins = NativeAvgMs < ManagedAvgMs;
            Verdict = nativeWins
                ? $"Native wins {NativeSpeedup:F2}× — {n} Burst subscribers IS above the useful threshold"
                : $"Managed wins {1.0 / NativeSpeedup:F2}× — {n} Burst subscribers is below useful threshold (~500+)";

            Debug.Log(
                $"[NativeStressBench] {n} subscribers × {iter} iterations\n" +
                $"  Managed avg: {ManagedAvgMs * 1000:F1}µs/dispatch\n" +
                $"  Native  avg: {NativeAvgMs * 1000:F1}µs/dispatch\n" +
                $"  → {Verdict}");
        }
    }
}