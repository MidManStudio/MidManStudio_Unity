// NativeDispatcherStressBench.cs
// Correctly tests the Burst dispatch loop vs managed delegate loop.
//
// THE PREVIOUS VERSION WAS WRONG: it called fps[i].Invoke() inside a
// managed for-loop. That means every iteration crosses back into managed
// code between invocations — exactly the overhead the native dispatcher
// avoids. The correct test is to compile the *dispatch loop itself* with
// Burst and invoke that once per frame-tick, passing the whole FP array.
//
// That is what MID_NativeTickDispatcher.BurstDispatchImpl does internally.
// This bench replicates that pattern in a standalone, self-contained way.

using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;
using MidManStudio.Core.TickDispatcher;

namespace MidManStudio.Core.Benchmarks
{
    public class NativeDispatcherStressBench : MonoBehaviour
    {
        [Header("Config")]
        public int Iterations = 3000;

        [Range(1, 30)]
        [Tooltip("Capped to the number of compiled StressCbs callbacks (30).")]
        public int SubscriberCount = 30;

        [Header("Results (read-only)")]
        public double ManagedAvgMs;
        public double NativeSingleInvokeAvgMs;  // managed loop + fp.Invoke per call
        public double BurstLoopAvgMs;           // single Burst-compiled dispatch call
        public double BurstVsManaged;
        public double BurstVsNativeSingle;
        public string Verdict;

        // ── Burst-compiled dispatch loop (mirrors MID_NativeTickDispatcher internals) ──
        // This is the actual thing we want to measure: a single native code call
        // that iterates the whole FP array without ever touching managed code.
        private delegate void BurstDispatchDelegate(
            IntPtr fps, int count, float deltaTime);

        [BurstCompile]
        private static unsafe void BurstDispatchLoop(IntPtr fps, int count, float dt)
        {
            var ptr = (Unity.Burst.FunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>*)fps;
            for (int i = 0; i < count; i++) ptr[i].Invoke(dt);
        }

        private Unity.Burst.FunctionPointer<BurstDispatchDelegate> _burstLoop;

        private static readonly MID_NativeTickDispatcher.NativeTickDelegate[] _allCbs =
        {
            StressCbs.C000, StressCbs.C001, StressCbs.C002, StressCbs.C003, StressCbs.C004,
            StressCbs.C005, StressCbs.C006, StressCbs.C007, StressCbs.C008, StressCbs.C009,
            StressCbs.C010, StressCbs.C011, StressCbs.C012, StressCbs.C013, StressCbs.C014,
            StressCbs.C015, StressCbs.C016, StressCbs.C017, StressCbs.C018, StressCbs.C019,
            StressCbs.C020, StressCbs.C021, StressCbs.C022, StressCbs.C023, StressCbs.C024,
            StressCbs.C025, StressCbs.C026, StressCbs.C027, StressCbs.C028, StressCbs.C029,
        };

        private bool _ready;

        private void Start() => WarmUp();

        [ContextMenu("Run Bench")]
        public void RunNativeStress()
        {
            if (!_ready) { Debug.LogError("[StressBench] Not ready — warm-up failed."); return; }

            int n = Mathf.Clamp(SubscriberCount, 1, _allCbs.Length);
            int iter = Iterations;
            const float dt = 0.1f;
            double freq = Stopwatch.Frequency;

            // ── Pre-compile FPs ──────────────────────────────────────────────
            var fps = new Unity.Burst.FunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>[n];
            for (int i = 0; i < n; i++)
                fps[i] = Unity.Burst.BurstCompiler
                    .CompileFunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>(_allCbs[i]);

            // ── Managed baseline: Action<float> delegate array ───────────────
            var actions = new Action<float>[n];
            for (int i = 0; i < n; i++) { int ci = i; actions[i] = d => _allCbs[ci](d); }

            // ── Warm-up all paths ────────────────────────────────────────────
            for (int w = 0; w < 500; w++)
            {
                for (int i = 0; i < n; i++) actions[i](dt);
                for (int i = 0; i < n; i++) fps[i].Invoke(dt);
                unsafe
                {
                    fixed (Unity.Burst.FunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>* ptr = fps)
                        _burstLoop.Invoke((IntPtr)ptr, n, dt);
                }
            }

            // ── Path A: Managed Action<float>[] loop ─────────────────────────
            var sw = Stopwatch.StartNew();
            for (int it = 0; it < iter; it++)
                for (int i = 0; i < n; i++) actions[i](dt);
            sw.Stop();
            ManagedAvgMs = sw.Elapsed.TotalMilliseconds / iter;

            // ── Path B: Managed loop + fp.Invoke (old bench pattern) ─────────
            // Still managed overhead between each invocation.
            sw.Restart();
            for (int it = 0; it < iter; it++)
                for (int i = 0; i < n; i++) fps[i].Invoke(dt);
            sw.Stop();
            NativeSingleInvokeAvgMs = sw.Elapsed.TotalMilliseconds / iter;

            // ── Path C: Single Burst-compiled dispatch loop call ─────────────
            // This is what MID_NativeTickDispatcher actually does.
            // One managed→native boundary crossing per dispatch, not N.
            sw.Restart();
            unsafe
            {
                fixed (Unity.Burst.FunctionPointer<MID_NativeTickDispatcher.NativeTickDelegate>* ptr = fps)
                {
                    for (int it = 0; it < iter; it++)
                        _burstLoop.Invoke((IntPtr)ptr, n, dt);
                }
            }
            sw.Stop();
            BurstLoopAvgMs = sw.Elapsed.TotalMilliseconds / iter;

            BurstVsManaged = ManagedAvgMs / BurstLoopAvgMs;
            BurstVsNativeSingle = NativeSingleInvokeAvgMs / BurstLoopAvgMs;

            bool burstWins = BurstLoopAvgMs < ManagedAvgMs;
            Verdict = burstWins
                ? $"Burst loop wins {BurstVsManaged:F2}× vs managed | {BurstVsNativeSingle:F2}× vs single-invoke pattern"
                : $"Managed wins at n={n} — Burst overhead dominates below threshold. " +
                  $"Try n=50+ with heavier work.";

            Debug.Log(
                $"[StressBench] n={n}, iters={iter}\n" +
                $"  A) Managed Action[] loop:       {ManagedAvgMs * 1000:F2}µs/dispatch\n" +
                $"  B) Managed loop + fp.Invoke():  {NativeSingleInvokeAvgMs * 1000:F2}µs/dispatch\n" +
                $"  C) Single Burst dispatch loop:  {BurstLoopAvgMs * 1000:F2}µs/dispatch\n" +
                $"  → {Verdict}\n\n" +
                $"  NOTE: Path B is what the OLD bench measured. Path C is what the\n" +
                $"  actual dispatcher does. The difference between B and C is the\n" +
                $"  managed->native boundary cost (N crossings vs 1 crossing).\n" +
                $"  Path A is the baseline the dispatcher saves CPU against.");
        }

        private void WarmUp()
        {
            try
            {
                _burstLoop = Unity.Burst.BurstCompiler
                    .CompileFunctionPointer<BurstDispatchDelegate>(BurstDispatchLoop);
                _ready = true;
                Debug.Log("[StressBench] Burst warm-up OK. Call RunBench from context menu or via code.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[StressBench] Burst compile failed: {e.Message}. " +
                               "Ensure com.unity.burst is installed.");
            }
        }
    }
}
