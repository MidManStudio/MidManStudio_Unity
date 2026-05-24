// MID_AudioBenchRunner.cs
//
// Runtime benchmark comparing three Unity audio approaches:
//   A) Naive     — AudioSource.PlayOneShot(clip) on a single source
//   B) Pooled    — Manual AudioSource[] pool (circular steal, mirrors game-dev standard)
//   C) Native    — MID_NativeAudioBridge.PlayClip() (Rust DLL or WebGL managed path)
//
// Sections (mirrors MID_TickDelayBenchRunner):
//   GC Allocation  — bytes allocated per call, measured with GC.GetAllocatedBytesForCurrentThread()
//   Throughput     — calls per millisecond for each approach (Stopwatch, main thread)
//   Voice accuracy — schedule N voices, verify active_voice_count() returns N
//
// Open:  MidManStudio > Utilities > Tests > Audio Bench
// Add MID_AudioBenchRunner to any scene GameObject in Play Mode.
//
// IMPORTANT: Assign a short AudioClip (< 1s) in the inspector.
//            MID_NativeAudioBridge must be present in the scene.
//            AudioClip must have Load Type = Decompress On Load.

using System;
using System.Collections;
using System.Diagnostics;
using UnityEngine;
using MidManStudio.Core.Audio;
using MidManStudio.Core.Logging;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MidManStudio.Core.Benchmarks
{
    // ── Result types ──────────────────────────────────────────────────────────

    [Serializable]
    public struct AudioBenchGCResult
    {
        public long NaiveBytesPerCall;
        public long PooledBytesPerCall;
        public long NativeBytesPerCall;
        public int  Iterations;
        public bool Valid;
        public bool WasColdRun;
    }

    [Serializable]
    public struct AudioBenchThroughputResult
    {
        public double NaiveCallsPerMs;
        public double PooledCallsPerMs;
        public double NativeCallsPerMs;
        public int    Iterations;
        public bool   Valid;
        // Ratio: Native / Naive — how many times faster is the native path?
        public double NativeVsNaiveRatio => NaiveCallsPerMs > 0 ? NativeCallsPerMs / NaiveCallsPerMs : 0;
    }

    [Serializable]
    public struct AudioBenchVoiceResult
    {
        public int  ScheduledCount;
        public int  ActiveCount;
        public bool MatchesExpected;
        public bool Valid;
    }

    // ─────────────────────────────────────────────────────────────────────────

    public class MID_AudioBenchRunner : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Configuration")]
        [Tooltip("Short clip for comparison testing (< 1 second, Decompress On Load).")]
        public AudioClip TestClip;
        public int GCIterations       = 500;
        public int ThroughputIterations = 2000;
        public int WarmupCount        = 50;

        [Header("References")]
        [SerializeField] private MID_NativeAudioBridge _bridge;
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Results  (read-only)")]
        public AudioBenchGCResult         GCResult;
        public AudioBenchThroughputResult ThroughputResult;
        public AudioBenchVoiceResult      VoiceResult;

        public string  StatusMessage = "Idle.";
        public float   Progress;
        public bool    IsRunning;
        public int     RunCount { get; private set; }

        // ── Private — comparison pools ────────────────────────────────────────

        private AudioSource   _naiveSource;       // single AudioSource for PlayOneShot
        private AudioSource[] _manualPool;        // 16-slot manual pool (comparison)
        private int           _manualPoolIdx;
        private const int     POOL_SIZE = 16;

        private Coroutine _active;

        // ── Public API ────────────────────────────────────────────────────────

        public void RunAll()
        {
            if (IsRunning) return;
            StopActive();
            GCResult = default; ThroughputResult = default; VoiceResult = default;
            _active = StartCoroutine(RunAllCo());
        }

        public void RunGCOnly()
        {
            if (IsRunning) return;
            StopActive();
            GCResult = default;
            _active = StartCoroutine(RunGCOnlyCo());
        }

        public void RunThroughputOnly()
        {
            if (IsRunning) return;
            StopActive();
            ThroughputResult = default;
            _active = StartCoroutine(RunThroughputOnlyCo());
        }

        public void RunVoiceOnly()
        {
            if (IsRunning) return;
            StopActive();
            VoiceResult = default;
            _active = StartCoroutine(RunVoiceOnlyCo());
        }

        public void Cancel()
        {
            StopActive();
            IsRunning = false;
            SetStatus("Cancelled.");
            Progress = 0f;
        }

        // ── Unity Lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            // Naive source — one AudioSource, no pool
            var naiveGo = new GameObject("BenchSource_Naive");
            naiveGo.transform.SetParent(transform);
            _naiveSource = naiveGo.AddComponent<AudioSource>();
            _naiveSource.spatialBlend = 0f;
            _naiveSource.playOnAwake  = false;

            // Manual pool — 16 pre-created sources (mirrors typical game-dev solution)
            _manualPool = new AudioSource[POOL_SIZE];
            for (int i = 0; i < POOL_SIZE; i++)
            {
                var go = new GameObject($"BenchSource_Pool_{i:D2}");
                go.transform.SetParent(transform);
                var src = go.AddComponent<AudioSource>();
                src.spatialBlend = 0f;
                src.playOnAwake  = false;
                _manualPool[i] = src;
            }

            // Auto-find bridge if not assigned
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
            SetStatus("All tests complete."); Progress = 1f; IsRunning = false;
        }

        private IEnumerator RunGCOnlyCo()
        {
            IsRunning = true; RunCount++;
            yield return StartCoroutine(WarmUp());
            yield return StartCoroutine(GCInner());
            SetStatus("GC test complete."); Progress = 1f; IsRunning = false;
        }

        private IEnumerator RunThroughputOnlyCo()
        {
            IsRunning = true; RunCount++;
            yield return StartCoroutine(WarmUp());
            yield return StartCoroutine(ThroughputInner());
            SetStatus("Throughput test complete."); Progress = 1f; IsRunning = false;
        }

        private IEnumerator RunVoiceOnlyCo()
        {
            IsRunning = true; RunCount++;
            yield return StartCoroutine(VoiceInner());
            SetStatus("Voice test complete."); Progress = 1f; IsRunning = false;
        }

        // ── Warm-up ───────────────────────────────────────────────────────────

        private IEnumerator WarmUp()
        {
            SetStatus($"Warming up ({WarmupCount} calls per path)…");
            Progress = 0f;

            if (TestClip == null || _bridge == null) { SetStatus("ERROR: Assign TestClip and ensure NativeAudioBridge is in the scene."); yield break; }

            // JIT all three paths
            for (int i = 0; i < WarmupCount; i++)
            {
                _naiveSource.PlayOneShot(TestClip, 0f);
                var s = GetManualPoolSource(); s.clip = TestClip; s.volume = 0f; s.Play();
                _bridge.PlayClip(0, 0f);
                if (i % 10 == 0) yield return null;
            }

            // Wait for any audio to settle, then stop all managed sources
            yield return new WaitForSeconds(TestClip.length + 0.1f);
            _naiveSource.Stop();
            foreach (var src in _manualPool) src.Stop();
            _bridge.ResetAllVoices();

            yield return StartCoroutine(DoGC());
            SetStatus("Warm-up complete."); yield return null;
        }

        // ── GC Test ───────────────────────────────────────────────────────────
        // Measures bytes allocated per call using GC.GetAllocatedBytesForCurrentThread().
        // PlayOneShot: may allocate first call (parameter boxing etc.); pool warm = 0.
        // Pooled src.Play(): 0 after warmup — no argument boxing.
        // bridge.PlayClip(): 0 always — blittable DllImport, no managed alloc.

        private IEnumerator GCInner()
        {
            int n = GCIterations;
            bool isCold = RunCount == 1;

            // ── Naive: PlayOneShot ────────────────────────────────────────────
            SetStatus($"GC — AudioSource.PlayOneShot ({n} calls)…");
            yield return DoGC();

            long naiveBefore = GetThreadBytes();
            for (int i = 0; i < n; i++)
                _naiveSource.PlayOneShot(TestClip, 0.01f);
            long naivePerCall = (GetThreadBytes() - naiveBefore) / n;
            yield return null; Progress = 0.33f;

            MID_Logger.LogInfo(_logLevel,
                $"[GC] PlayOneShot = {naivePerCall} B/call over {n} iters " +
                (isCold ? "(cold)" : "(warm)"),
                nameof(MID_AudioBenchRunner));

            // ── Pooled: manual AudioSource.Play() ─────────────────────────────
            SetStatus($"GC — Manual Pool AudioSource.Play ({n} calls)…");
            yield return DoGC();

            long poolBefore = GetThreadBytes();
            for (int i = 0; i < n; i++)
            {
                var src = GetManualPoolSource();
                src.clip   = TestClip;
                src.volume = 0.01f;
                src.Play();
            }
            long poolPerCall = (GetThreadBytes() - poolBefore) / n;
            yield return null; Progress = 0.66f;

            MID_Logger.LogInfo(_logLevel,
                $"[GC] Pooled src.Play() = {poolPerCall} B/call over {n} iters " +
                (isCold ? "(cold)" : "(warm)"),
                nameof(MID_AudioBenchRunner));

            // ── Native: bridge.PlayClip ───────────────────────────────────────
            SetStatus($"GC — NativeBridge.PlayClip ({n} calls)…");
            yield return DoGC();

            long nativeBefore = GetThreadBytes();
            for (int i = 0; i < n; i++)
                _bridge.PlayClip(0, 0.01f);
            long nativePerCall = (GetThreadBytes() - nativeBefore) / n;
            yield return null; Progress = 1f;

            MID_Logger.LogInfo(_logLevel,
                $"[GC] NativeBridge.PlayClip = {nativePerCall} B/call over {n} iters " +
                (isCold ? "(cold)" : "(warm)"),
                nameof(MID_AudioBenchRunner));

            GCResult = new AudioBenchGCResult
            {
                NaiveBytesPerCall  = naivePerCall,
                PooledBytesPerCall = poolPerCall,
                NativeBytesPerCall = nativePerCall,
                Iterations         = n,
                Valid              = true,
                WasColdRun         = isCold
            };

            SetStatus(
                $"GC — Naive: {naivePerCall}B | Pool: {poolPerCall}B | Native: {nativePerCall}B" +
                (isCold ? "" : "  [warm run]"));
        }

        // ── Throughput Test ───────────────────────────────────────────────────
        // Measures calls/ms for each approach using Stopwatch (main thread).
        // The audio system may not actually play N clips if voices are stolen —
        // this measures the SCHEDULING overhead, not audio completion.

        private IEnumerator ThroughputInner()
        {
            int n = ThroughputIterations;
            var sw = new Stopwatch();

            // Silence everything first
            _naiveSource.Stop();
            foreach (var src in _manualPool) src.Stop();
            _bridge.ResetAllVoices();
            yield return null;

            // ── Naive ──────────────────────────────────────────────────────────
            SetStatus($"Throughput — PlayOneShot ({n} calls)…");
            sw.Restart();
            for (int i = 0; i < n; i++)
                _naiveSource.PlayOneShot(TestClip, 0.001f); // near-silent
            sw.Stop();
            double naiveCpMs = n / sw.Elapsed.TotalMilliseconds;
            yield return null; Progress = 0.33f;

            MID_Logger.LogInfo(_logLevel,
                $"[Throughput] PlayOneShot: {naiveCpMs:F0} calls/ms  ({sw.Elapsed.TotalMilliseconds:F2}ms for {n})",
                nameof(MID_AudioBenchRunner));

            // ── Pooled ────────────────────────────────────────────────────────
            SetStatus($"Throughput — Pooled Play ({n} calls)…");
            sw.Restart();
            for (int i = 0; i < n; i++)
            {
                var src = GetManualPoolSource();
                src.clip   = TestClip;
                src.volume = 0.001f;
                src.Play();
            }
            sw.Stop();
            double poolCpMs = n / sw.Elapsed.TotalMilliseconds;
            yield return null; Progress = 0.66f;

            MID_Logger.LogInfo(_logLevel,
                $"[Throughput] Pooled Play: {poolCpMs:F0} calls/ms  ({sw.Elapsed.TotalMilliseconds:F2}ms for {n})",
                nameof(MID_AudioBenchRunner));

            // ── Native ────────────────────────────────────────────────────────
            SetStatus($"Throughput — NativeBridge.PlayClip ({n} calls)…");
            _bridge.ResetAllVoices();
            sw.Restart();
            for (int i = 0; i < n; i++)
                _bridge.PlayClip(0, 0.001f);
            sw.Stop();
            double nativeCpMs = n / sw.Elapsed.TotalMilliseconds;
            yield return null; Progress = 1f;

            MID_Logger.LogInfo(_logLevel,
                $"[Throughput] NativeBridge: {nativeCpMs:F0} calls/ms  ({sw.Elapsed.TotalMilliseconds:F2}ms for {n})\n" +
                $"  Native is {nativeCpMs / naiveCpMs:F1}× faster than PlayOneShot, " +
                $"{nativeCpMs / poolCpMs:F1}× faster than pooled Play.",
                nameof(MID_AudioBenchRunner));

            ThroughputResult = new AudioBenchThroughputResult
            {
                NaiveCallsPerMs  = naiveCpMs,
                PooledCallsPerMs = poolCpMs,
                NativeCallsPerMs = nativeCpMs,
                Iterations       = n,
                Valid            = true
            };

            SetStatus($"Throughput — Naive: {naiveCpMs:F0}/ms | Pool: {poolCpMs:F0}/ms | Native: {nativeCpMs:F0}/ms");
        }

        // ── Voice Accuracy Test ───────────────────────────────────────────────
        // Verifies that scheduling N voices results in N active voices.
        // Also tests voice stealing: scheduling 20 voices into a 16-slot pool.

        private IEnumerator VoiceInner()
        {
            SetStatus("Voice accuracy test…");

            _bridge.ResetAllVoices();
            yield return null;

            const int TARGET = 8; // schedule 8, should have 8 active
            for (int i = 0; i < TARGET; i++)
                _bridge.PlayClip(0, 0.001f);

            // process_buffer activates pending voices — we need one audio frame.
            // We can't control when OnAudioFilterRead fires, so wait one frame.
            yield return null;
            yield return null; // two frames to be safe

            int active = _bridge.ActiveVoiceCount;

            MID_Logger.LogInfo(_logLevel,
                $"[Voice] Scheduled {TARGET}, active: {active} (expected: {TARGET})",
                nameof(MID_AudioBenchRunner));

            bool matchesExpected = (active == TARGET);

            // Now test voice stealing: schedule more than pool size
            _bridge.ResetAllVoices();
            yield return null;

            const int OVER_FILL = 20; // more than 16-slot pool
            for (int i = 0; i < OVER_FILL; i++)
                _bridge.PlayClip(0, 0.001f);

            yield return null; yield return null;

            int postSteal = _bridge.ActiveVoiceCount;
            MID_Logger.LogInfo(_logLevel,
                $"[Voice] Scheduled {OVER_FILL} (pool size 16), active: {postSteal} " +
                "(steal should cap at 16)",
                nameof(MID_AudioBenchRunner));

            VoiceResult = new AudioBenchVoiceResult
            {
                ScheduledCount    = TARGET,
                ActiveCount       = active,
                MatchesExpected   = matchesExpected,
                Valid             = true
            };

            SetStatus($"Voice — Scheduled {TARGET}, Active {active} " + (matchesExpected ? "✓" : "✗"));
            Progress = 1f;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private AudioSource GetManualPoolSource()
        {
            // Find non-playing; steal oldest if all busy
            for (int i = 0; i < POOL_SIZE; i++)
            {
                int idx = (_manualPoolIdx + i) % POOL_SIZE;
                if (!_manualPool[idx].isPlaying)
                {
                    _manualPoolIdx = (idx + 1) % POOL_SIZE;
                    return _manualPool[idx];
                }
            }
            // All busy — steal from circular position
            var stolen = _manualPool[_manualPoolIdx % POOL_SIZE];
            _manualPoolIdx = (_manualPoolIdx + 1) % POOL_SIZE;
            stolen.Stop();
            return stolen;
        }

        private IEnumerator DoGC()
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            yield return null; yield return null;
        }

        private static long GetThreadBytes()
        {
            try   { return GC.GetAllocatedBytesForCurrentThread(); }
            catch { return GC.GetTotalMemory(false); }
        }

        private void SetStatus(string msg)
        {
            StatusMessage = msg;
            MID_Logger.LogDebug(_logLevel, msg, nameof(MID_AudioBenchRunner));
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
        private bool _fGC = true, _fThroughput = true, _fVoice = true, _fContext = true;

        private static readonly Color ColNaive  = new Color(1.00f, 0.50f, 0.20f, 1f);
        private static readonly Color ColPooled = new Color(0.40f, 0.65f, 1.00f, 1f);
        private static readonly Color ColNative = new Color(0.28f, 0.92f, 0.45f, 1f);
        private static readonly Color ColBarBg  = new Color(0.12f, 0.12f, 0.12f, 0.5f);
        private static readonly Color ColDim    = new Color(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color ColPass   = new Color(0.28f, 0.95f, 0.45f, 1f);
        private static readonly Color ColFail   = new Color(1.00f, 0.35f, 0.35f, 1f);
        private static readonly Color ColWarn   = new Color(1.00f, 0.85f, 0.25f, 1f);

        [MenuItem("MidManStudio/Utilities/Tests/Audio Bench", priority = 122)]
        public static void Open()
        {
            var w = GetWindow<MID_AudioBenchWindow>("Audio Bench");
            w.minSize = new Vector2(540, 620);
        }

        private void OnEnable()  { EditorApplication.update += Repaint; TryFind(); }
        private void OnDisable() { EditorApplication.update -= Repaint; }

        private void TryFind()
        {
            if (_runner == null) _runner = FindObjectOfType<MID_AudioBenchRunner>();
        }

        private void OnGUI()
        {
            TryFind();

            // Toolbar
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
                EditorGUILayout.EndScrollView(); return;
            }

            if (_runner == null)
            {
                EditorGUILayout.HelpBox(
                    "No MID_AudioBenchRunner in scene.\n" +
                    "Add it to any GameObject. Assign a TestClip (Decompress On Load).\n" +
                    "MID_NativeAudioBridge must also be present in the scene.",
                    MessageType.Warning);
                if (GUILayout.Button("Add Runner to Scene", GUILayout.Height(28)))
                {
                    var go = new GameObject("[AudioBenchRunner]");
                    _runner = go.AddComponent<MID_AudioBenchRunner>();
                    Undo.RegisterCreatedObjectUndo(go, "Add Audio Bench Runner");
                    Selection.activeGameObject = go;
                }
                EditorGUILayout.EndScrollView(); return;
            }

            DrawContext();
            DrawSep();
            DrawRunButtons();
            DrawSep();
            DrawGCSection();
            DrawSep();
            DrawThroughputSection();
            DrawSep();
            DrawVoiceSection();
            DrawSep();
            DrawLegend();

            EditorGUILayout.EndScrollView();
        }

        // ── Context ───────────────────────────────────────────────────────────

        private void DrawContext()
        {
            _fContext = EditorGUILayout.BeginFoldoutHeaderGroup(_fContext,
                "What this benchmarks — three Unity audio approaches");
            if (_fContext)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    BenchRow(ColNaive, "A) Naive — AudioSource.PlayOneShot(clip)",
                        "Single AudioSource, called directly. Easiest but most expensive.\n" +
                        "May allocate internally on first call. All overhead inside Unity's audio system.");
                    EditorGUILayout.Space(3);
                    BenchRow(ColPooled, "B) Pooled — Manual AudioSource[] pool",
                        "16 AudioSource components pre-created. Steal from oldest if full.\n" +
                        "Standard game-dev optimization. 0 GC after warmup. Unity audio overhead remains.");
                    EditorGUILayout.Space(3);
                    BenchRow(ColNative, "C) Native — NativeBridge.PlayClip()",
                        "Rust DLL (desktop/mobile) or WebGL managed pool (WebGL).\n" +
                        "schedule_voice: ~10-50 ns (below Criterion resolution, essentially free).\n" +
                        "process_buffer 16-voice 512-samp: 0.0135ms = 0.064% of audio frame budget.\n" +
                        "0 GC allocation always. Peak limiter included in DSP path.");
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ── Run buttons ───────────────────────────────────────────────────────

        private void DrawRunButtons()
        {
            if (_runner.RunCount > 0)
            {
                var old = GUI.color;
                GUI.color = _runner.RunCount == 1 ? ColPass : ColWarn;
                EditorGUILayout.LabelField(
                    _runner.RunCount == 1
                        ? $"Run #{_runner.RunCount}  (cold — most accurate GC results)"
                        : $"Run #{_runner.RunCount}  (warm — GC may show lower due to pool reuse)",
                    EditorStyles.miniBoldLabel);
                GUI.color = old;
            }

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
                if (GUILayout.Button("▶  Run All",        GUILayout.Height(30))) _runner.RunAll();
                GUI.backgroundColor = ColNative * 0.75f;
                if (GUILayout.Button("GC Only",            GUILayout.Height(30))) _runner.RunGCOnly();
                if (GUILayout.Button("Throughput Only",    GUILayout.Height(30))) _runner.RunThroughputOnly();
                if (GUILayout.Button("Voice Only",         GUILayout.Height(30))) _runner.RunVoiceOnly();
                GUI.backgroundColor = new Color(0.85f, 0.25f, 0.25f);
                GUI.enabled = _runner.IsRunning;
                if (GUILayout.Button("■  Cancel",          GUILayout.Height(30))) _runner.Cancel();

                GUI.backgroundColor = oldbg;
                GUI.enabled = true;
            }
            EditorGUILayout.Space(4);
        }

        // ── GC Section ────────────────────────────────────────────────────────

        private void DrawGCSection()
        {
            _fGC = EditorGUILayout.BeginFoldoutHeaderGroup(_fGC,
                "GC Allocation per scheduling call  (0 B = zero-alloc)");
            if (_fGC)
            {
                var res = _runner.GCResult;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (!res.Valid)
                    { EditorGUILayout.HelpBox("Run GC test to see results.", MessageType.Info); }
                    else
                    {
                        if (!res.WasColdRun)
                        {
                            Coloured(ColWarn, "⚠ Warm run — PlayOneShot may show 0B due to pool reuse. " +
                                "Restart Play for cold results. Use Profiler > GC Alloc for ground truth.");
                        }

                        EditorGUILayout.Space(3);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            ColHead("A) Naive", ColNaive); ColHead("B) Pooled", ColPooled); ColHead("C) Native", ColNative);
                        }
                        EditorGUILayout.Space(2);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            ValCell(res.NaiveBytesPerCall,  ColNaive,  "PlayOneShot alloc");
                            ValCell(res.PooledBytesPerCall, ColPooled, "src.Play() alloc");
                            ValCell(res.NativeBytesPerCall, ColNative, "DllImport alloc");
                        }

                        EditorGUILayout.Space(4);
                        long mx = Math.Max(res.NaiveBytesPerCall, Math.Max(res.PooledBytesPerCall, 1));
                        BarRow("Naive",  (float)res.NaiveBytesPerCall  / mx, ColNaive,  $"{res.NaiveBytesPerCall} B");
                        BarRow("Pooled", (float)res.PooledBytesPerCall / mx, ColPooled, $"{res.PooledBytesPerCall} B");
                        BarRow("Native", (float)res.NativeBytesPerCall / mx, ColNative,
                            res.NativeBytesPerCall == 0 ? "0 B  ✓  blittable DllImport" : $"{res.NativeBytesPerCall} B");
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ── Throughput Section ────────────────────────────────────────────────

        private void DrawThroughputSection()
        {
            _fThroughput = EditorGUILayout.BeginFoldoutHeaderGroup(_fThroughput,
                "Scheduling Throughput — calls per millisecond  (higher = better)");
            if (_fThroughput)
            {
                var res = _runner.ThroughputResult;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (!res.Valid)
                    { EditorGUILayout.HelpBox("Run Throughput test to see results.", MessageType.Info); }
                    else
                    {
                        EditorGUILayout.HelpBox(
                            "Measures how many scheduling calls complete per millisecond on the main thread.\n" +
                            "Higher = more impact events your game thread can fire per frame without stalling.\n" +
                            "Note: audio playback is NOT verified here — only the scheduling call cost.",
                            MessageType.None);

                        EditorGUILayout.Space(3);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            ColHead("A) Naive", ColNaive); ColHead("B) Pooled", ColPooled); ColHead("C) Native", ColNative);
                        }
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            ThrptCell(res.NaiveCallsPerMs,  ColNaive);
                            ThrptCell(res.PooledCallsPerMs, ColPooled);
                            ThrptCell(res.NativeCallsPerMs, ColNative);
                        }

                        EditorGUILayout.Space(4);
                        double mx = Math.Max(res.NaiveCallsPerMs, Math.Max(res.PooledCallsPerMs, res.NativeCallsPerMs));
                        if (mx > 0)
                        {
                            BarRow("Naive",  (float)(res.NaiveCallsPerMs  / mx), ColNaive,  $"{res.NaiveCallsPerMs:F0} calls/ms");
                            BarRow("Pooled", (float)(res.PooledCallsPerMs / mx), ColPooled, $"{res.PooledCallsPerMs:F0} calls/ms");
                            BarRow("Native", (float)(res.NativeCallsPerMs / mx), ColNative, $"{res.NativeCallsPerMs:F0} calls/ms");
                        }

                        if (res.NativeVsNaiveRatio > 0)
                        {
                            EditorGUILayout.Space(4);
                            var old = GUI.color; GUI.color = ColNative;
                            EditorGUILayout.LabelField(
                                $"Native is {res.NativeVsNaiveRatio:F1}× faster than naive PlayOneShot scheduling.",
                                EditorStyles.miniBoldLabel);
                            GUI.color = old;
                        }
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ── Voice Section ─────────────────────────────────────────────────────

        private void DrawVoiceSection()
        {
            _fVoice = EditorGUILayout.BeginFoldoutHeaderGroup(_fVoice,
                "Voice Accuracy — scheduled vs active voice count");
            if (_fVoice)
            {
                var res = _runner.VoiceResult;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (!res.Valid)
                    { EditorGUILayout.HelpBox("Run Voice test to see results.", MessageType.Info); }
                    else
                    {
                        bool ok = res.MatchesExpected;
                        var old = GUI.color;
                        GUI.color = ok ? ColPass : ColFail;
                        EditorGUILayout.LabelField(
                            ok ? $"✓ Scheduled {res.ScheduledCount} → Active {res.ActiveCount}  (match)"
                               : $"✗ Scheduled {res.ScheduledCount} → Active {res.ActiveCount}  (MISMATCH — check audio thread timing)",
                            EditorStyles.miniBoldLabel);
                        GUI.color = old;

                        EditorGUILayout.Space(2);
                        GUI.color = ColDim;
                        EditorGUILayout.LabelField(
                            "Note: active_voice_count() polls pending triggers via atomics.\n" +
                            "Voices become active on the next process_buffer call (audio thread).\n" +
                            "A 1-frame gap between schedule and count is expected and correct.",
                            EditorStyles.wordWrappedMiniLabel);
                        GUI.color = old;
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ── Legend ────────────────────────────────────────────────────────────

        private void DrawLegend()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                Sw(ColNaive);  EditorGUILayout.LabelField("A) PlayOneShot", EditorStyles.miniLabel, GUILayout.Width(100));
                Sw(ColPooled); EditorGUILayout.LabelField("B) Pool Play",   EditorStyles.miniLabel, GUILayout.Width(80));
                Sw(ColNative); EditorGUILayout.LabelField("C) Native",      EditorStyles.miniLabel, GUILayout.Width(60));
                GUILayout.FlexibleSpace();
                Sw(ColPass);   EditorGUILayout.LabelField("✓ pass",  EditorStyles.miniLabel, GUILayout.Width(50));
                Sw(ColFail);   EditorGUILayout.LabelField("✗ fail",  EditorStyles.miniLabel, GUILayout.Width(50));
            }
        }

        // ── Drawing helpers ───────────────────────────────────────────────────

        private float CW => (position.width - 28f) / 3f;

        private void ColHead(string t, Color c) { var o = GUI.color; GUI.color = c; EditorGUILayout.LabelField(t, EditorStyles.miniBoldLabel, GUILayout.Width(CW)); GUI.color = o; }

        private void ValCell(long bytes, Color col, string sub)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(CW)))
            {
                var old = GUI.color; GUI.color = bytes == 0 ? ColPass : col;
                EditorGUILayout.LabelField(bytes == 0 ? "0 B  ✓" : $"{bytes} B", EditorStyles.boldLabel);
                GUI.color = ColDim;
                EditorGUILayout.LabelField(sub, EditorStyles.miniLabel);
                GUI.color = old;
            }
        }

        private void ThrptCell(double cpMs, Color col)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(CW)))
            {
                var old = GUI.color; GUI.color = col;
                EditorGUILayout.LabelField($"{cpMs:F0} calls/ms", EditorStyles.boldLabel);
                GUI.color = ColDim;
                EditorGUILayout.LabelField($"{1000.0 / cpMs:F3} µs/call", EditorStyles.miniLabel);
                GUI.color = old;
            }
        }

        private void BarRow(string label, float f, Color col, string tip)
        {
            f = Mathf.Clamp01(f);
            using (new EditorGUILayout.HorizontalScope())
            {
                var old = GUI.color; GUI.color = ColDim;
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(60));
                GUI.color = old;

                Rect r = EditorGUILayout.GetControlRect(false, 14, GUILayout.ExpandWidth(true));
                r.y += 2; r.height = 10;
                EditorGUI.DrawRect(r, ColBarBg);
                if (f > 0.002f) { Rect fill = r; fill.width = Mathf.Max(r.width * f, 2f); EditorGUI.DrawRect(fill, col); }
                else { Rect tick = r; tick.width = 4f; EditorGUI.DrawRect(tick, ColPass); }

                GUI.color = ColDim;
                EditorGUILayout.LabelField(tip, EditorStyles.miniLabel, GUILayout.Width(260));
                GUI.color = old;
            }
        }

        private void BenchRow(Color col, string title, string desc)
        {
            var old = GUI.color; GUI.color = col;
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
            GUI.color = ColDim;
            EditorGUILayout.LabelField(desc, EditorStyles.wordWrappedMiniLabel);
            GUI.color = old;
        }

        private static void Coloured(Color c, string text) { var o = GUI.color; GUI.color = c; EditorGUILayout.LabelField(text, EditorStyles.wordWrappedMiniLabel); GUI.color = o; }
        private static void Sw(Color c) { Rect r = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12)); r.y += 3; r.height = 8; r.width = 8; EditorGUI.DrawRect(r, c); GUILayout.Space(2); }
        private static void DrawSep() { Rect r = EditorGUILayout.GetControlRect(false, 1); EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.35f)); EditorGUILayout.Space(4); }
    }

#endif // UNITY_EDITOR
}
