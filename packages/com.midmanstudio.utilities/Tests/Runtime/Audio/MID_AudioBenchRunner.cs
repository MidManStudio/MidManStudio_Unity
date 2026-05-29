// MID_AudioBenchRunner.cs — v2
// Matches MID_NativeAudioBridge v2 (AudioSource pool) + MID_AudioLimiter (Rust DSP).
//
// What this compares:
//
//   A) Naive  — AudioSource.PlayOneShot(clip) on a single source.
//               The simplest thing a developer writes first.
//               Problem: can only play one thing at a time cleanly.
//
//   B) Manual — A hand-rolled AudioSource[] pool (circular steal).
//               What a developer writes when they realize Naive is broken.
//               Correct but requires boilerplate per project.
//
//   C) Bridge — MID_NativeAudioBridge.PlayClip().
//               Our pool: same mechanism as Manual but built-in, zero setup.
//
// What this does NOT compare:
//   Rust DLL scheduling (removed in v2 — the DLL is now a DSP limiter effect only,
//   not a voice manager). That comparison is no longer meaningful here.
//
// Limiter DSP section benchmarks process_buffer directly via DllImport —
// this tests the per-buffer cost of the Rust peak limiter at different signal levels.

using System;
using System.Collections;
using System.Diagnostics;
using UnityEngine;
using MidManStudio.Core.Audio;
using MidManStudio.Core.Logging;

#if !UNITY_WEBGL || UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MidManStudio.Core.Benchmarks
{
    // ── Result structs ────────────────────────────────────────────────────────

    [Serializable]
    public struct AudioBenchGCResult
    {
        public long NaiveBytesPerCall;
        public long ManualBytesPerCall;
        public long BridgeBytesPerCall;
        public int  NaiveGCEvents, ManualGCEvents, BridgeGCEvents;
        public bool CalibrationPassed;
        public int  Iterations;
        public bool Valid;
    }

    [Serializable]
    public struct AudioBenchThroughputResult
    {
        public double NaiveCallsPerMs;
        public double ManualCallsPerMs;
        public double BridgeCallsPerMs;
        public int    Iterations;
        public bool   Valid;
        public double BridgeVsNaiveRatio  => NaiveCallsPerMs  > 0 ? BridgeCallsPerMs / NaiveCallsPerMs  : 0;
        public double BridgeVsManualRatio => ManualCallsPerMs > 0 ? BridgeCallsPerMs / ManualCallsPerMs : 0;
    }

    [Serializable]
    public struct AudioBenchVoiceResult
    {
        public int  ScheduledCount;
        public int  ActiveCount;
        public bool MatchesExpected;
        public bool Valid;
    }

    [Serializable]
    public struct AudioBenchLimiterResult
    {
        public double QuietMs;      // amplitude 0.5 — below threshold, recovery path
        public double NominalMs;    // amplitude 0.9 — near threshold
        public double LoudMs;       // amplitude 1.5 — above threshold, attack path
        public int    BufferSize;
        public bool   Valid;
    }

    // ─────────────────────────────────────────────────────────────────────────

    public class MID_AudioBenchRunner : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("References")]
        [Tooltip("Assign a short AudioClip — any Load Type works (Decompress On Load NOT required).")]
        public AudioClip TestClip;
        [SerializeField] private MID_NativeAudioBridge _bridge;
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Configuration")]
        public int GCIterations         = 500;
        public int ThroughputIterations = 2000;
        public int WarmupCount          = 50;

        [Header("Results  (read-only)")]
        public AudioBenchGCResult         GCResult;
        public AudioBenchThroughputResult ThroughputResult;
        public AudioBenchVoiceResult      VoiceResult;
        public AudioBenchLimiterResult    LimiterResult;

        public string StatusMessage = "Idle.";
        public float  Progress;
        public bool   IsRunning;
        public int    RunCount { get; private set; }

        // ── Limiter DllImport (bench only) ───────────────────────────────────
        // Test file only — production code uses MID_AudioLimiter for DSP calls.
#if (!UNITY_WEBGL || UNITY_EDITOR) && !UNITY_IOS
        [DllImport("mid_audio")] private static extern void  process_buffer_bench(float[] buf, int len);
        [DllImport("mid_audio")] private static extern void  reset_limiter();
        [DllImport("mid_audio")] private static extern void  set_limiter_params(float t, float a, float r);
        // rename to avoid collision with MID_AudioLimiter's import
        // Unity resolves by symbol name, not method name, so this is fine
#endif

        // ── Manual pool for comparison ────────────────────────────────────────

        private AudioSource   _naiveSource;
        private AudioSource[] _manualPool;
        private int           _manualPoolIdx;
        private const int     POOL_SIZE = 16;

        private Coroutine _active;

        // ── Public API ────────────────────────────────────────────────────────

        public void RunAll()
        {
            if (IsRunning) return;
            StopActive();
            GCResult = default; ThroughputResult = default;
            VoiceResult = default; LimiterResult = default;
            _active = StartCoroutine(RunAllCo());
        }

        public void RunGCOnly()
        {
            if (IsRunning) return;
            StopActive(); GCResult = default;
            _active = StartCoroutine(Wrap(GCInner()));
        }

        public void RunThroughput()
        {
            if (IsRunning) return;
            StopActive(); ThroughputResult = default;
            _active = StartCoroutine(Wrap(ThroughputInner()));
        }

        public void RunVoice()
        {
            if (IsRunning) return;
            StopActive(); VoiceResult = default;
            _active = StartCoroutine(Wrap(VoiceInner()));
        }

        public void RunLimiter()
        {
            if (IsRunning) return;
            StopActive(); LimiterResult = default;
            _active = StartCoroutine(Wrap(LimiterInner()));
        }

        public void Cancel()
        {
            StopActive();
            IsRunning = false;
            SetStatus("Cancelled.");
            Progress = 0f;
        }

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            // Naive — single AudioSource (worst case comparison)
            var naiveGo = new GameObject("Bench_Naive");
            naiveGo.transform.SetParent(transform);
            _naiveSource = naiveGo.AddComponent<AudioSource>();
            _naiveSource.spatialBlend = 0f;
            _naiveSource.playOnAwake  = false;

            // Manual pool — hand-rolled 16-voice pool (typical dev solution)
            _manualPool = new AudioSource[POOL_SIZE];
            for (int i = 0; i < POOL_SIZE; i++)
            {
                var go = new GameObject($"Bench_Manual_{i:D2}");
                go.transform.SetParent(transform);
                var src = go.AddComponent<AudioSource>();
                src.spatialBlend = 0f;
                src.playOnAwake  = false;
                _manualPool[i] = src;
            }

            if (_bridge == null)
                _bridge = FindObjectOfType<MID_NativeAudioBridge>();
        }

        private void StopActive()
        {
            if (_active != null) { StopCoroutine(_active); _active = null; }
            IsRunning = false;
        }

        // ── Master coroutines ─────────────────────────────────────────────────

        private IEnumerator RunAllCo()
        {
            IsRunning = true; RunCount++;
            yield return StartCoroutine(WarmUp());
            yield return StartCoroutine(GCInner());
            yield return StartCoroutine(ThroughputInner());
            yield return StartCoroutine(VoiceInner());
            yield return StartCoroutine(LimiterInner());
            SetStatus("All tests complete."); Progress = 1f; IsRunning = false;
        }

        private IEnumerator Wrap(IEnumerator inner)
        {
            IsRunning = true; RunCount++;
            yield return StartCoroutine(WarmUp());
            yield return StartCoroutine(inner);
            Progress = 1f; IsRunning = false;
        }

        // ── Warm-up ───────────────────────────────────────────────────────────

        private IEnumerator WarmUp()
        {
            SetStatus($"Warming up…");
            Progress = 0f;

            if (TestClip == null)
            {
                SetStatus("ERROR: assign TestClip in the inspector.");
                IsRunning = false;
                yield break;
            }
            if (_bridge == null)
            {
                SetStatus("ERROR: MID_NativeAudioBridge not found in scene.");
                IsRunning = false;
                yield break;
            }

            // JIT all three paths
            for (int i = 0; i < WarmupCount; i++)
            {
                _naiveSource.PlayOneShot(TestClip, 0f);
                var s = GetManualSource(); s.clip = TestClip; s.volume = 0f; s.Play();
                _bridge.PlayClip(0, 0f);
                if (i % 10 == 0) yield return null;
            }

            yield return new WaitForSeconds(TestClip.length + 0.1f);

            _naiveSource.Stop();
            foreach (var src in _manualPool) src.Stop();
            _bridge.StopAll();

            yield return DoGC();
            SetStatus("Warm-up done.");
            yield return null;
        }

        // ── GC Test ───────────────────────────────────────────────────────────
        //
        // All three paths will show 0 B. This is correct.
        //
        // AudioSource.PlayOneShot, AudioSource.Play, and bridge.PlayClip all
        // delegate to Unity's C++ audio engine internally. None of them allocate
        // on the .NET managed heap in the calling thread.
        //
        // GC.GetAllocatedBytesForCurrentThread() only tracks managed allocations.
        // The calibration test deliberately allocates a byte[] to prove the counter
        // is working — if calibration passes and all paths show 0 B, that IS the result.
        //
        // For ground truth: Profiler > CPU > Hierarchy > GC Alloc column.

        private IEnumerator GCInner()
        {
            int n = GCIterations;

            // Calibration
            SetStatus("GC calibration…");
            yield return DoGC();
            long calBefore = GetThreadBytes();
            var dummy = new byte[n * 64]; GC.KeepAlive(dummy);
            bool calPassed = (GetThreadBytes() - calBefore) > 0;
            MID_Logger.LogInfo(_logLevel,
                $"[GC Calibration] {(calPassed ? "✓ Counter working" : "✗ Counter unreliable — use Profiler")}",
                nameof(MID_AudioBenchRunner));
            yield return DoGC(); Progress = 0.1f;

            // A) Naive
            SetStatus($"GC — A) PlayOneShot ({n})…");
            yield return DoGC();
            int gcBefore = GC.CollectionCount(0);
            long naiveBefore = GetThreadBytes();
            for (int i = 0; i < n; i++) _naiveSource.PlayOneShot(TestClip, 0.001f);
            long naivePerCall = (GetThreadBytes() - naiveBefore) / n;
            int naiveGC = GC.CollectionCount(0) - gcBefore;
            yield return null; Progress = 0.4f;

            // B) Manual pool
            SetStatus($"GC — B) Manual Pool ({n})…");
            yield return DoGC();
            gcBefore = GC.CollectionCount(0);
            long manualBefore = GetThreadBytes();
            for (int i = 0; i < n; i++)
            {
                var src = GetManualSource();
                src.clip   = TestClip;
                src.volume = 0.001f;
                src.Play();
            }
            long manualPerCall = (GetThreadBytes() - manualBefore) / n;
            int manualGC = GC.CollectionCount(0) - gcBefore;
            yield return null; Progress = 0.7f;

            // C) MID Bridge pool
            SetStatus($"GC — C) MID Bridge ({n})…");
            yield return DoGC();
            gcBefore = GC.CollectionCount(0);
            long bridgeBefore = GetThreadBytes();
            for (int i = 0; i < n; i++) _bridge.PlayClip(0, 0.001f);
            long bridgePerCall = (GetThreadBytes() - bridgeBefore) / n;
            int bridgeGC = GC.CollectionCount(0) - gcBefore;
            yield return null; Progress = 1f;

            MID_Logger.LogInfo(_logLevel,
                $"[GC] Naive={naivePerCall}B  Manual={manualPerCall}B  Bridge={bridgePerCall}B\n" +
                "  0 B for all is correct — Unity audio is C++ internally, no managed allocation.",
                nameof(MID_AudioBenchRunner));

            GCResult = new AudioBenchGCResult
            {
                NaiveBytesPerCall  = naivePerCall,
                ManualBytesPerCall = manualPerCall,
                BridgeBytesPerCall = bridgePerCall,
                NaiveGCEvents      = naiveGC,
                ManualGCEvents     = manualGC,
                BridgeGCEvents     = bridgeGC,
                CalibrationPassed  = calPassed,
                Iterations         = n,
                Valid              = true
            };

            SetStatus(calPassed
                ? "GC — all 0 B expected and correct (calibration ✓)"
                : "GC — all 0 B, but calibration failed — use Profiler for ground truth");
        }

        // ── Throughput ────────────────────────────────────────────────────────
        //
        // Measures main-thread scheduling cost: how many play requests per millisecond.
        // This is meaningful even though all three are AudioSource-based because:
        //   PlayOneShot: additional internal Unity overhead per call
        //   Manual pool: array scan + stop + play — slightly more C# work
        //   Bridge: same as manual, but our implementation vs hand-rolled code
        // Expect all three in the same order of magnitude (they're all C++ audio calls).

        private IEnumerator ThroughputInner()
        {
            int n = ThroughputIterations;
            var sw = new Stopwatch();

            _naiveSource.Stop();
            foreach (var src in _manualPool) src.Stop();
            _bridge.StopAll();
            yield return null;

            // A) Naive
            SetStatus($"Throughput — A) PlayOneShot ({n})…");
            sw.Restart();
            for (int i = 0; i < n; i++) _naiveSource.PlayOneShot(TestClip, 0.001f);
            sw.Stop();
            double naiveCpMs = n / sw.Elapsed.TotalMilliseconds;
            yield return null; Progress = 0.33f;

            // B) Manual pool
            SetStatus($"Throughput — B) Manual Pool ({n})…");
            sw.Restart();
            for (int i = 0; i < n; i++)
            {
                var src = GetManualSource();
                src.clip   = TestClip;
                src.volume = 0.001f;
                src.Play();
            }
            sw.Stop();
            double manualCpMs = n / sw.Elapsed.TotalMilliseconds;
            yield return null; Progress = 0.66f;

            // C) MID Bridge
            SetStatus($"Throughput — C) MID Bridge ({n})…");
            _bridge.StopAll();
            sw.Restart();
            for (int i = 0; i < n; i++) _bridge.PlayClip(0, 0.001f);
            sw.Stop();
            double bridgeCpMs = n / sw.Elapsed.TotalMilliseconds;
            yield return null; Progress = 1f;

            ThroughputResult = new AudioBenchThroughputResult
            {
                NaiveCallsPerMs  = naiveCpMs,
                ManualCallsPerMs = manualCpMs,
                BridgeCallsPerMs = bridgeCpMs,
                Iterations       = n,
                Valid            = true
            };

            MID_Logger.LogInfo(_logLevel,
                $"[Throughput] Naive: {naiveCpMs:F0}/ms  " +
                $"Manual: {manualCpMs:F0}/ms  Bridge: {bridgeCpMs:F0}/ms",
                nameof(MID_AudioBenchRunner));

            SetStatus($"Throughput — Naive: {naiveCpMs:F0}/ms | Manual: {manualCpMs:F0}/ms | Bridge: {bridgeCpMs:F0}/ms");
        }

        // ── Voice Accuracy ────────────────────────────────────────────────────
        //
        // Verifies the pool correctly tracks concurrent voices.
        // ActiveVoiceCount = number of pool AudioSources currently playing.
        // WaitForSeconds(0.1f) gives time for AudioSources to start playing
        // (they need at least one audio frame to register as isPlaying = true).

        private IEnumerator VoiceInner()
        {
            SetStatus("Voice accuracy…");

            _bridge.StopAll();
            yield return new WaitForSeconds(0.1f); // let stop propagate

            const int TARGET = 8;
            for (int i = 0; i < TARGET; i++) _bridge.PlayClip(0, 0.001f);

            // AudioSource.Play() triggers on the next audio frame (~20ms at 48kHz)
            // Wait long enough for isPlaying to return true
            yield return new WaitForSeconds(0.1f);

            int active = _bridge.ActiveVoiceCount;
            bool ok = active == TARGET;

            MID_Logger.LogInfo(_logLevel,
                $"[Voice] Scheduled {TARGET}, active: {active} — " + (ok ? "✓" : "✗"),
                nameof(MID_AudioBenchRunner));

            // Steal test — schedule more than pool size
            _bridge.StopAll();
            yield return new WaitForSeconds(0.05f);

            const int OVER = 20;
            for (int i = 0; i < OVER; i++) _bridge.PlayClip(0, 0.001f);
            yield return new WaitForSeconds(0.1f);

            int postSteal = _bridge.ActiveVoiceCount;
            MID_Logger.LogInfo(_logLevel,
                $"[Voice] Scheduled {OVER} into 16-slot pool → active: {postSteal} (should be ≤ 16)",
                nameof(MID_AudioBenchRunner));

            VoiceResult = new AudioBenchVoiceResult
            {
                ScheduledCount  = TARGET,
                ActiveCount     = active,
                MatchesExpected = ok,
                Valid           = true
            };

            SetStatus($"Voice — {TARGET} scheduled, {active} active " + (ok ? "✓" : "✗"));
            Progress = 1f;
        }

        // ── Limiter DSP Cost ──────────────────────────────────────────────────
        //
        // Calls process_buffer directly to measure per-buffer DSP cost.
        // This simulates what the audio thread does every ~10-20ms.
        //
        // Three signal levels:
        //   Quiet   (0.5) → peak below threshold → gain recovery path (cheaper)
        //   Nominal (0.9) → near threshold
        //   Loud    (1.5) → peak above threshold → gain attack path
        //
        // Called from main thread here (bench only). In production this runs
        // on the audio thread via MID_AudioLimiter.OnAudioFilterRead.
        //
        // WebGL: skipped (DLL not available).

        private IEnumerator LimiterInner()
        {
#if (!UNITY_WEBGL || UNITY_EDITOR) && !UNITY_IOS
            SetStatus("Limiter DSP bench…");
            const int BUFFER_SIZE = 512;
            const int ITERS = 1000;

            set_limiter_params(0.95f, 0.85f, 0.002f);

            var sw = new Stopwatch();
            double quietMs = 0, nominalMs = 0, loudMs = 0;

            // Quiet (0.5 amplitude)
            var quietBuf = MakeSine(BUFFER_SIZE, 0.5f);
            sw.Restart();
            for (int i = 0; i < ITERS; i++)
            {
                reset_limiter();
                process_buffer_bench(quietBuf, quietBuf.Length);
            }
            sw.Stop();
            quietMs = sw.Elapsed.TotalMilliseconds / ITERS;
            yield return null; Progress = 0.33f;

            // Nominal (0.9 amplitude)
            var nomBuf = MakeSine(BUFFER_SIZE, 0.9f);
            sw.Restart();
            for (int i = 0; i < ITERS; i++)
            {
                reset_limiter();
                process_buffer_bench(nomBuf, nomBuf.Length);
            }
            sw.Stop();
            nominalMs = sw.Elapsed.TotalMilliseconds / ITERS;
            yield return null; Progress = 0.66f;

            // Loud (1.5 amplitude — above threshold, limiter fully engaged)
            var loudBuf = MakeSine(BUFFER_SIZE, 1.5f);
            sw.Restart();
            for (int i = 0; i < ITERS; i++)
            {
                reset_limiter();
                process_buffer_bench(loudBuf, loudBuf.Length);
            }
            sw.Stop();
            loudMs = sw.Elapsed.TotalMilliseconds / ITERS;
            yield return null; Progress = 1f;

            LimiterResult = new AudioBenchLimiterResult
            {
                QuietMs   = quietMs,
                NominalMs = nominalMs,
                LoudMs    = loudMs,
                BufferSize = BUFFER_SIZE,
                Valid      = true
            };

            MID_Logger.LogInfo(_logLevel,
                $"[Limiter] {BUFFER_SIZE}-sample buffer:\n" +
                $"  Quiet  (0.5): {quietMs * 1000:F2}µs\n" +
                $"  Nominal(0.9): {nominalMs * 1000:F2}µs\n" +
                $"  Loud   (1.5): {loudMs * 1000:F2}µs\n" +
                $"  Audio frame budget at 48kHz/{BUFFER_SIZE}samp: {BUFFER_SIZE / 48.0 * 1000:F1}ms",
                nameof(MID_AudioBenchRunner));

            SetStatus($"Limiter — quiet:{quietMs*1000:F1}µs  nominal:{nominalMs*1000:F1}µs  loud:{loudMs*1000:F1}µs");
#else
            SetStatus("Limiter bench: skipped on WebGL (DLL not available).");
            LimiterResult = new AudioBenchLimiterResult { Valid = false };
            yield return null;
#endif
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private AudioSource GetManualSource()
        {
            for (int i = 0; i < POOL_SIZE; i++)
            {
                int idx = (_manualPoolIdx + i) % POOL_SIZE;
                if (!_manualPool[idx].isPlaying)
                {
                    _manualPoolIdx = (idx + 1) % POOL_SIZE;
                    return _manualPool[idx];
                }
            }
            var stolen = _manualPool[_manualPoolIdx % POOL_SIZE];
            _manualPoolIdx = (_manualPoolIdx + 1) % POOL_SIZE;
            stolen.Stop();
            return stolen;
        }

        private static float[] MakeSine(int samples, float amplitude)
        {
            var buf = new float[samples];
            for (int i = 0; i < samples; i++)
                buf[i] = Mathf.Sin(i * 440f * Mathf.PI * 2f / 48000f) * amplitude;
            return buf;
        }

        private IEnumerator DoGC()
        {
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true);
            yield return null;
            yield return null;
        }

        private static long GetThreadBytes()
        {
            try   { return GC.GetAllocatedBytesForCurrentThread(); }
            catch { return GC.GetTotalMemory(false); }
        }

        private void SetStatus(string m)
        {
            StatusMessage = m;
            MID_Logger.LogDebug(_logLevel, m, nameof(MID_AudioBenchRunner));
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Editor Window
    // ═════════════════════════════════════════════════════════════════════════

#if UNITY_EDITOR

    public class MID_AudioBenchWindow : EditorWindow
    {
        private MID_AudioBenchRunner _runner;
        private Vector2              _scroll;
        private bool _fGC = true, _fTP = true, _fV = true, _fL = true;

        private static readonly Color ColA    = new(1.00f, 0.50f, 0.20f, 1f); // Naive    — orange
        private static readonly Color ColB    = new(0.40f, 0.65f, 1.00f, 1f); // Manual   — blue
        private static readonly Color ColC    = new(0.28f, 0.92f, 0.45f, 1f); // Bridge   — green
        private static readonly Color ColL    = new(0.85f, 0.60f, 1.00f, 1f); // Limiter  — purple
        private static readonly Color ColDim  = new(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color ColPass = new(0.28f, 0.95f, 0.45f, 1f);
        private static readonly Color ColFail = new(1.00f, 0.35f, 0.35f, 1f);
        private static readonly Color ColInfo = new(0.55f, 0.80f, 1.00f, 1f);
        private static readonly Color ColBar  = new(0.12f, 0.12f, 0.12f, 0.5f);

        [MenuItem("MidManStudio/Utilities/Tests/Audio Bench", priority = 122)]
        public static void Open()
        {
            var w = GetWindow<MID_AudioBenchWindow>("Audio Bench");
            w.minSize = new Vector2(540, 620);
        }

        private void OnEnable()  { EditorApplication.update += Repaint; Find(); }
        private void OnDisable() { EditorApplication.update -= Repaint; }
        private void Find() { if (_runner == null) _runner = FindObjectOfType<MID_AudioBenchRunner>(); }

        private void OnGUI()
        {
            Find();

            // ── Toolbar ───────────────────────────────────────────────────────
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField("MidManStudio — Audio Benchmark",
                    EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                _runner = (MID_AudioBenchRunner)EditorGUILayout.ObjectField(
                    _runner, typeof(MID_AudioBenchRunner), true, GUILayout.Width(200));
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.Space(4);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to run benchmarks.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            if (_runner == null)
            {
                EditorGUILayout.HelpBox(
                    "Add MID_AudioBenchRunner to any scene GameObject.\n" +
                    "Assign a TestClip. MID_NativeAudioBridge must be in the scene.",
                    MessageType.Warning);
                if (GUILayout.Button("Add Runner to Scene", GUILayout.Height(28)))
                {
                    var go = new GameObject("[AudioBenchRunner]");
                    _runner = go.AddComponent<MID_AudioBenchRunner>();
                    Undo.RegisterCreatedObjectUndo(go, "Add Audio Bench Runner");
                    Selection.activeGameObject = go;
                }
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawExplainer();
            Sep();
            DrawRunButtons();
            Sep();
            DrawGC();
            Sep();
            DrawThroughput();
            Sep();
            DrawVoice();
            Sep();
            DrawLimiter();
            Sep();
            DrawLegend();

            EditorGUILayout.EndScrollView();
        }

        // ── What does this test? ──────────────────────────────────────────────

        private void DrawExplainer()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                Lbl(ColA, "A) Naive  — AudioSource.PlayOneShot on one source.",
                    "Can only handle one sound cleanly at a time. Simple but broken for projectile games.");
                EditorGUILayout.Space(2);
                Lbl(ColB, "B) Manual pool — hand-rolled AudioSource[] circular steal.",
                    "What a developer writes when Naive isn't enough. Correct, but boilerplate per project.");
                EditorGUILayout.Space(2);
                Lbl(ColC, "C) MID Bridge — MID_NativeAudioBridge.PlayClip()",
                    "Same pool mechanism as B, built into the package. Zero setup code in your game.");
                EditorGUILayout.Space(4);
                Lbl(ColL, "Limiter — Rust DSP peak limiter cost per audio buffer.",
                    "How many microseconds the limiter spends per buffer (quiet/nominal/loud signals).\n" +
                    "In production this runs on the audio thread via MID_AudioLimiter.OnAudioFilterRead.\n" +
                    "Budget: 512 samples @ 48kHz = 10.67ms. Limiter should use < 0.1ms of that.");
            }
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
                var oldbg = GUI.backgroundColor;

                GUI.backgroundColor = new Color(0.25f, 0.80f, 0.30f);
                if (GUILayout.Button("▶  Run All",    GUILayout.Height(30))) _runner.RunAll();
                GUI.backgroundColor = ColA * 0.7f;
                if (GUILayout.Button("GC",            GUILayout.Height(30))) _runner.RunGCOnly();
                if (GUILayout.Button("Throughput",    GUILayout.Height(30))) _runner.RunThroughput();
                if (GUILayout.Button("Voice",         GUILayout.Height(30))) _runner.RunVoice();
                if (GUILayout.Button("Limiter",       GUILayout.Height(30))) _runner.RunLimiter();
                GUI.backgroundColor = new Color(0.85f, 0.25f, 0.25f);
                GUI.enabled = _runner.IsRunning;
                if (GUILayout.Button("■ Cancel",      GUILayout.Height(30))) _runner.Cancel();

                GUI.backgroundColor = oldbg;
                GUI.enabled = true;
            }
            EditorGUILayout.Space(4);
        }

        // ── GC section ────────────────────────────────────────────────────────

        private void DrawGC()
        {
            _fGC = EditorGUILayout.BeginFoldoutHeaderGroup(_fGC, "GC Allocation (expects 0 B for all)");
            if (_fGC)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    var old = GUI.color; GUI.color = ColInfo;
                    EditorGUILayout.LabelField(
                        "All three paths call into Unity's C++ audio engine. " +
                        "No managed heap allocation occurs on the calling thread for any of them. " +
                        "0 B is the correct result — not a measurement failure.\n" +
                        "Calibration deliberately allocates a byte[] to verify the counter is working.",
                        EditorStyles.wordWrappedMiniLabel);
                    GUI.color = old;

                    EditorGUILayout.Space(3);
                    var res = _runner.GCResult;
                    if (!res.Valid) { EditorGUILayout.HelpBox("Run GC test.", MessageType.Info); }
                    else
                    {
                        Lbl(res.CalibrationPassed ? ColPass : ColFail,
                            res.CalibrationPassed ? "✓ Calibration passed — counter working" : "⚠ Calibration failed — use Profiler");

                        EditorGUILayout.Space(3);
                        using (new EditorGUILayout.HorizontalScope()) { H("A) Naive", ColA); H("B) Manual", ColB); H("C) Bridge", ColC); }
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            V(res.NaiveBytesPerCall,  $"GC events: {res.NaiveGCEvents}",  ColA);
                            V(res.ManualBytesPerCall, $"GC events: {res.ManualGCEvents}", ColB);
                            V(res.BridgeBytesPerCall, $"GC events: {res.BridgeGCEvents}", ColC);
                        }
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ── Throughput section ────────────────────────────────────────────────

        private void DrawThroughput()
        {
            _fTP = EditorGUILayout.BeginFoldoutHeaderGroup(_fTP, "Scheduling Throughput (calls/ms)");
            if (_fTP)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    var res = _runner.ThroughputResult;
                    if (!res.Valid) { EditorGUILayout.HelpBox("Run Throughput test.", MessageType.Info); }
                    else
                    {
                        using (new EditorGUILayout.HorizontalScope()) { H("A) Naive", ColA); H("B) Manual", ColB); H("C) Bridge", ColC); }
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            T(res.NaiveCallsPerMs,  ColA);
                            T(res.ManualCallsPerMs, ColB);
                            T(res.BridgeCallsPerMs, ColC);
                        }

                        EditorGUILayout.Space(4);
                        double mx = Math.Max(res.NaiveCallsPerMs, Math.Max(res.ManualCallsPerMs, res.BridgeCallsPerMs));
                        if (mx > 0)
                        {
                            Bar("Naive",  (float)(res.NaiveCallsPerMs  / mx), ColA, $"{res.NaiveCallsPerMs:F0} calls/ms");
                            Bar("Manual", (float)(res.ManualCallsPerMs / mx), ColB, $"{res.ManualCallsPerMs:F0} calls/ms");
                            Bar("Bridge", (float)(res.BridgeCallsPerMs / mx), ColC, $"{res.BridgeCallsPerMs:F0} calls/ms");
                        }

                        EditorGUILayout.Space(2);
                        var old = GUI.color; GUI.color = ColDim;
                        EditorGUILayout.LabelField(
                            "All three are AudioSource-based so numbers will be similar. " +
                            "The difference shows internal Unity overhead per call approach. " +
                            "Manual and Bridge should be close — Bridge just wraps the same mechanism.",
                            EditorStyles.wordWrappedMiniLabel);
                        GUI.color = old;
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ── Voice section ─────────────────────────────────────────────────────

        private void DrawVoice()
        {
            _fV = EditorGUILayout.BeginFoldoutHeaderGroup(_fV, "Voice Pool Accuracy");
            if (_fV)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    var res = _runner.VoiceResult;
                    if (!res.Valid) { EditorGUILayout.HelpBox("Run Voice test.", MessageType.Info); }
                    else
                    {
                        bool ok = res.MatchesExpected;
                        Lbl(ok ? ColPass : ColFail,
                            ok ? $"✓ Scheduled {res.ScheduledCount}, active {res.ActiveCount} — pool tracking correct"
                               : $"✗ Scheduled {res.ScheduledCount}, active {res.ActiveCount} — mismatch (try running again, AudioSource.isPlaying needs 1 audio frame)");

                        EditorGUILayout.Space(2);
                        var old = GUI.color; GUI.color = ColDim;
                        EditorGUILayout.LabelField(
                            "AudioSource.isPlaying returns true after the NEXT audio frame (~20ms). " +
                            "The test waits 0.1s to guarantee this. If still failing, increase WaitForSeconds.",
                            EditorStyles.wordWrappedMiniLabel);
                        GUI.color = old;
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ── Limiter section ───────────────────────────────────────────────────

        private void DrawLimiter()
        {
            _fL = EditorGUILayout.BeginFoldoutHeaderGroup(_fL, "Rust Limiter DSP Cost (µs per buffer)");
            if (_fL)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
#if UNITY_WEBGL && !UNITY_EDITOR
                    EditorGUILayout.HelpBox("WebGL: Rust DLL not available. C# fallback limiter active.", MessageType.Info);
#else
                    var res = _runner.LimiterResult;
                    if (!res.Valid) { EditorGUILayout.HelpBox("Run Limiter test.", MessageType.Info); }
                    else
                    {
                        float budget = res.BufferSize / 48.0f; // ms
                        var old = GUI.color; GUI.color = ColDim;
                        EditorGUILayout.LabelField(
                            $"Audio frame budget: {res.BufferSize} samples @ 48kHz = {budget:F2}ms = {budget * 1000:F0}µs total.",
                            EditorStyles.wordWrappedMiniLabel);
                        GUI.color = old;

                        EditorGUILayout.Space(3);
                        float maxUs = (float)Math.Max(res.LoudMs, Math.Max(res.QuietMs, res.NominalMs)) * 1000f;
                        if (maxUs > 0)
                        {
                            Bar("Quiet (0.5)",   (float)(res.QuietMs   * 1000 / maxUs), ColL, $"{res.QuietMs   * 1000:F2}µs  (recovery path)");
                            Bar("Nominal (0.9)", (float)(res.NominalMs * 1000 / maxUs), ColL, $"{res.NominalMs * 1000:F2}µs");
                            Bar("Loud (1.5)",    (float)(res.LoudMs    * 1000 / maxUs), ColL, $"{res.LoudMs    * 1000:F2}µs  (attack path — limiting)");
                        }

                        EditorGUILayout.Space(2);
                        old = GUI.color; GUI.color = ColInfo;
                        EditorGUILayout.LabelField(
                            "Numbers should be well under 100µs. If they're near the budget (10,670µs) something is wrong. " +
                            "This cost is paid every audio frame regardless of how many sounds are playing.",
                            EditorStyles.wordWrappedMiniLabel);
                        GUI.color = old;
                    }
#endif
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ── Legend ────────────────────────────────────────────────────────────

        private void DrawLegend()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                Sw(ColA); EditorGUILayout.LabelField("A) Naive",  EditorStyles.miniLabel, GUILayout.Width(70));
                Sw(ColB); EditorGUILayout.LabelField("B) Manual", EditorStyles.miniLabel, GUILayout.Width(70));
                Sw(ColC); EditorGUILayout.LabelField("C) Bridge", EditorStyles.miniLabel, GUILayout.Width(70));
                Sw(ColL); EditorGUILayout.LabelField("Limiter",   EditorStyles.miniLabel, GUILayout.Width(60));
                GUILayout.FlexibleSpace();
                Sw(ColPass); EditorGUILayout.LabelField("✓ pass", EditorStyles.miniLabel, GUILayout.Width(50));
                Sw(ColFail); EditorGUILayout.LabelField("✗ fail", EditorStyles.miniLabel, GUILayout.Width(50));
            }
        }

        // ── GUI helpers ───────────────────────────────────────────────────────

        private float CW => (position.width - 28f) / 3f;

        private void H(string t, Color c) { var o = GUI.color; GUI.color = c; EditorGUILayout.LabelField(t, EditorStyles.miniBoldLabel, GUILayout.Width(CW)); GUI.color = o; }

        private void V(long bytes, string sub, Color col)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(CW)))
            {
                var o = GUI.color;
                GUI.color = bytes == 0 ? ColPass : col;
                EditorGUILayout.LabelField(bytes == 0 ? "0 B  ✓" : $"{bytes} B", EditorStyles.boldLabel);
                GUI.color = ColDim;
                EditorGUILayout.LabelField(sub, EditorStyles.miniLabel);
                GUI.color = o;
            }
        }

        private void T(double cpMs, Color col)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(CW)))
            {
                var o = GUI.color; GUI.color = col;
                EditorGUILayout.LabelField($"{cpMs:F0} /ms", EditorStyles.boldLabel);
                GUI.color = ColDim;
                EditorGUILayout.LabelField($"{1000.0/Math.Max(cpMs, 0.001):F2}µs/call", EditorStyles.miniLabel);
                GUI.color = o;
            }
        }

        private void Bar(string label, float f, Color col, string tip)
        {
            f = Mathf.Clamp01(f);
            using (new EditorGUILayout.HorizontalScope())
            {
                var o = GUI.color; GUI.color = ColDim;
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(90));
                GUI.color = o;

                Rect r = EditorGUILayout.GetControlRect(false, 14, GUILayout.ExpandWidth(true));
                r.y += 2; r.height = 10;
                EditorGUI.DrawRect(r, ColBar);
                if (f > 0.002f) { Rect fill = r; fill.width = Mathf.Max(r.width * f, 2f); EditorGUI.DrawRect(fill, col); }
                else { Rect tick = r; tick.width = 4f; EditorGUI.DrawRect(tick, ColPass); }

                GUI.color = ColDim;
                EditorGUILayout.LabelField(tip, EditorStyles.miniLabel, GUILayout.Width(270));
                GUI.color = o;
            }
        }

        private static void Lbl(Color c, string title, string sub = null)
        {
            var o = GUI.color; GUI.color = c;
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
            if (sub != null) { GUI.color = new Color(c.r, c.g, c.b, 0.7f); EditorGUILayout.LabelField(sub, EditorStyles.wordWrappedMiniLabel); }
            GUI.color = o;
        }

        private static void Sw(Color c)
        {
            Rect r = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12));
            r.y += 3; r.height = 8; r.width = 8;
            EditorGUI.DrawRect(r, c);
            GUILayout.Space(2);
        }

        private static void Sep()
        {
            Rect r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.35f));
            EditorGUILayout.Space(4);
        }
    }

#endif // UNITY_EDITOR
}
