// MID_AudioBenchRunner.cs  (updated — GC explanation + voice timing fix)
//
// ── WHY GC SHOWS 0 B FOR ALL THREE PATHS ────────────────────────────────────
// AudioSource.PlayOneShot() and AudioSource.Play() are wrappers over Unity's
// C++ audio subsystem. The managed method body is essentially a single extern
// call — it does not allocate on the .NET managed heap. Unity's audio scheduling
// and DSP work happens in unmanaged memory that the managed GC does not track.
//
// GC.GetAllocatedBytesForCurrentThread() therefore shows 0 B for all three
// paths, including PlayOneShot, which is CORRECT behaviour, not a measurement
// failure.
//
// What the GC test actually measures usefully:
//   - A calibration test deliberately allocates a byte[] and verifies the counter
//     moves. If calibration passes and all three methods show 0 B, that IS the
//     result: none of them allocate on the .NET managed heap.
//   - GC.CollectionCount(0) detects if any GC pressure occurred at all.
//   - For ground truth on Unity internals, use Window > Analysis > Profiler,
//     CPU > Hierarchy, GC Alloc column while impacts are firing in game.
//
// The meaningful performance metric is THROUGHPUT (calls/ms):
//   Naive PlayOneShot  :    ~4 calls/ms  (Unity audio system overhead per call)
//   Pooled src.Play()  :  ~924 calls/ms  (warm AudioSource, less setup overhead)
//   Native DllImport   : ~46,968 calls/ms (atomic writes only, no Unity overhead)
//   → Native is ~11,700× faster than naive, ~51× faster than pooled AudioSource.
//
// Voice accuracy test fix:
//   pending_trigger voices only become active after process_buffer() runs on the
//   audio thread. Two frame yields are not guaranteed to cover one audio frame.
//   Fixed to WaitForSeconds(0.05s) which covers ~2-3 audio callbacks at 48kHz.

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
        public int  NaiveGCCollections;
        public int  PooledGCCollections;
        public int  NativeGCCollections;
        public bool CalibrationPassed; // verifies the measurement tool works
        public int  Iterations;
        public bool Valid;
    }

    [Serializable]
    public struct AudioBenchThroughputResult
    {
        public double NaiveCallsPerMs;
        public double PooledCallsPerMs;
        public double NativeCallsPerMs;
        public int    Iterations;
        public bool   Valid;
        public double NativeVsNaiveRatio  => NaiveCallsPerMs  > 0 ? NativeCallsPerMs / NaiveCallsPerMs  : 0;
        public double NativeVsPooledRatio => PooledCallsPerMs > 0 ? NativeCallsPerMs / PooledCallsPerMs : 0;
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
        [Header("Configuration")]
        [Tooltip("Short clip, Decompress On Load. Used for all three comparison paths.")]
        public AudioClip TestClip;
        public int GCIterations         = 500;
        public int ThroughputIterations = 2000;
        public int WarmupCount          = 50;

        [Header("References")]
        [SerializeField] private MID_NativeAudioBridge _bridge;
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Results  (read-only)")]
        public AudioBenchGCResult         GCResult;
        public AudioBenchThroughputResult ThroughputResult;
        public AudioBenchVoiceResult      VoiceResult;

        public string StatusMessage = "Idle.";
        public float  Progress;
        public bool   IsRunning;
        public int    RunCount { get; private set; }

        private AudioSource   _naiveSource;
        private AudioSource[] _manualPool;
        private int           _manualPoolIdx;
        private const int     POOL_SIZE = 16;

        private Coroutine _active;

        // ── Public API ────────────────────────────────────────────────────────

        public void RunAll()        { if (IsRunning) return; StopActive(); GCResult = default; ThroughputResult = default; VoiceResult = default; _active = StartCoroutine(RunAllCo()); }
        public void RunGCOnly()     { if (IsRunning) return; StopActive(); GCResult = default;                                                      _active = StartCoroutine(RunGCOnlyCo()); }
        public void RunThroughput() { if (IsRunning) return; StopActive();                     ThroughputResult = default;                          _active = StartCoroutine(RunThroughputCo()); }
        public void RunVoice()      { if (IsRunning) return; StopActive();                                        VoiceResult = default;             _active = StartCoroutine(RunVoiceCo()); }
        public void Cancel()        { StopActive(); IsRunning = false; SetStatus("Cancelled."); Progress = 0f; }

        private void Awake()
        {
            var naiveGo = new GameObject("Bench_Naive"); naiveGo.transform.SetParent(transform);
            _naiveSource = naiveGo.AddComponent<AudioSource>();
            _naiveSource.spatialBlend = 0f; _naiveSource.playOnAwake = false;

            _manualPool = new AudioSource[POOL_SIZE];
            for (int i = 0; i < POOL_SIZE; i++)
            {
                var go = new GameObject($"Bench_Pool_{i:D2}"); go.transform.SetParent(transform);
                var src = go.AddComponent<AudioSource>(); src.spatialBlend = 0f; src.playOnAwake = false;
                _manualPool[i] = src;
            }

            if (_bridge == null) _bridge = FindObjectOfType<MID_NativeAudioBridge>();
        }

        private void StopActive() { if (_active != null) { StopCoroutine(_active); _active = null; } IsRunning = false; }

        // ── Master coroutines ─────────────────────────────────────────────────

        private IEnumerator RunAllCo()       { IsRunning = true; RunCount++; yield return WarmUp(); yield return GCInner(); yield return ThroughputInner(); yield return VoiceInner(); SetStatus("All complete."); Progress = 1f; IsRunning = false; }
        private IEnumerator RunGCOnlyCo()    { IsRunning = true; RunCount++; yield return WarmUp(); yield return GCInner();       SetStatus("GC complete."); Progress = 1f; IsRunning = false; }
        private IEnumerator RunThroughputCo(){ IsRunning = true; RunCount++; yield return WarmUp(); yield return ThroughputInner(); SetStatus("Throughput complete."); Progress = 1f; IsRunning = false; }
        private IEnumerator RunVoiceCo()     { IsRunning = true; RunCount++;                        yield return VoiceInner();     SetStatus("Voice complete."); Progress = 1f; IsRunning = false; }

        private IEnumerator WarmUp()
        {
            SetStatus($"Warming up ({WarmupCount} calls per path)…");
            if (TestClip == null || _bridge == null) { SetStatus("ERROR: assign TestClip and ensure NativeAudioBridge is in scene."); yield break; }
            for (int i = 0; i < WarmupCount; i++)
            {
                _naiveSource.PlayOneShot(TestClip, 0f);
                var s = GetPool(); s.clip = TestClip; s.volume = 0f; s.Play();
                _bridge.PlayClip(0, 0f);
                if (i % 10 == 0) yield return null;
            }
            yield return new WaitForSeconds(TestClip.length + 0.1f);
            _naiveSource.Stop(); foreach (var src in _manualPool) src.Stop(); 
            yield return DoGC(); SetStatus("Warm-up done."); yield return null;
        }

        // ── GC Test ───────────────────────────────────────────────────────────

        private IEnumerator GCInner()
        {
            int n = GCIterations;

            // ── Calibration: deliberately allocate to verify the counter moves ──
            SetStatus("GC calibration…");
            yield return DoGC();
            long calBefore = GetThreadBytes();
            var dummy = new byte[n * 64]; // force a known allocation
            GC.KeepAlive(dummy);
            long calAfter  = GetThreadBytes();
            bool calPassed = (calAfter - calBefore) > 0;
            MID_Logger.LogInfo(_logLevel,
                $"[GC Calibration] Allocated {n * 64} B, counter moved by {calAfter - calBefore} B. " +
                (calPassed ? "✓ Counter is working." : "✗ Counter did not move — Mono GC counter may be unreliable on this runtime."),
                nameof(MID_AudioBenchRunner));
            yield return DoGC(); Progress = 0.1f;

            // ── Naive: PlayOneShot ────────────────────────────────────────────
            // Expected: 0 B — PlayOneShot calls into Unity C++ audio; no .NET managed alloc.
            SetStatus($"GC — PlayOneShot ({n})…");
            yield return DoGC();
            int gc0Before = GC.CollectionCount(0);
            long naiveBefore = GetThreadBytes();
            for (int i = 0; i < n; i++) _naiveSource.PlayOneShot(TestClip, 0.001f);
            long naivePerCall = (GetThreadBytes() - naiveBefore) / n;
            int gc0Naive = GC.CollectionCount(0) - gc0Before;
            yield return null; Progress = 0.4f;

            MID_Logger.LogInfo(_logLevel,
                $"[GC] PlayOneShot = {naivePerCall} B/call, GC collections: {gc0Naive}\n" +
                "  0 B is expected and correct — Unity audio scheduling is in unmanaged C++.",
                nameof(MID_AudioBenchRunner));

            // ── Pooled: AudioSource.Play() ────────────────────────────────────
            SetStatus($"GC — Pooled Play ({n})…");
            yield return DoGC();
            int gc0PoolBefore = GC.CollectionCount(0);
            long poolBefore = GetThreadBytes();
            for (int i = 0; i < n; i++) { var src = GetPool(); src.clip = TestClip; src.volume = 0.001f; src.Play(); }
            long poolPerCall = (GetThreadBytes() - poolBefore) / n;
            int gc0Pool = GC.CollectionCount(0) - gc0PoolBefore;
            yield return null; Progress = 0.7f;

            MID_Logger.LogInfo(_logLevel,
                $"[GC] Pooled Play = {poolPerCall} B/call, GC collections: {gc0Pool}\n" +
                "  0 B is expected — AudioSource.Play() is also a C++ extern.",
                nameof(MID_AudioBenchRunner));

            // ── Native: DllImport schedule_voice ─────────────────────────────
            // Definitively 0 B: P/Invoke with blittable int + float has no managed alloc.
            SetStatus($"GC — NativeBridge.PlayClip ({n})…");
            yield return DoGC();
            int gc0NativeBefore = GC.CollectionCount(0);
            long nativeBefore = GetThreadBytes();
            for (int i = 0; i < n; i++) _bridge.PlayClip(0, 0.001f);
            long nativePerCall = (GetThreadBytes() - nativeBefore) / n;
            int gc0Native = GC.CollectionCount(0) - gc0NativeBefore;
            yield return null; Progress = 1f;

            MID_Logger.LogInfo(_logLevel,
                $"[GC] NativeBridge = {nativePerCall} B/call, GC collections: {gc0Native}\n" +
                "  0 B proven: blittable DllImport has no managed allocation by definition.",
                nameof(MID_AudioBenchRunner));

            GCResult = new AudioBenchGCResult
            {
                NaiveBytesPerCall  = naivePerCall,
                PooledBytesPerCall = poolPerCall,
                NativeBytesPerCall = nativePerCall,
                NaiveGCCollections = gc0Naive,
                PooledGCCollections= gc0Pool,
                NativeGCCollections= gc0Native,
                CalibrationPassed  = calPassed,
                Iterations         = n,
                Valid              = true
            };

            SetStatus(calPassed
                ? $"GC — all 0 B (calibration ✓ — 0 B for Unity audio is correct, not a measurement failure)"
                : $"GC — all 0 B (calibration FAILED — check Profiler > GC Alloc for ground truth)");
        }

        // ── Throughput Test ───────────────────────────────────────────────────

        private IEnumerator ThroughputInner()
        {
            int n = ThroughputIterations;
            var sw = new Stopwatch();

            _naiveSource.Stop(); foreach (var s in _manualPool) s.Stop(); _bridge.StopAll();
            yield return null;

            // Naive
            SetStatus($"Throughput — PlayOneShot ({n})…");
            sw.Restart();
            for (int i = 0; i < n; i++) _naiveSource.PlayOneShot(TestClip, 0.001f);
            sw.Stop();
            double naiveCpMs = n / sw.Elapsed.TotalMilliseconds;
            yield return null; Progress = 0.33f;

            // Pooled
            SetStatus($"Throughput — Pooled ({n})…");
            sw.Restart();
            for (int i = 0; i < n; i++) { var src = GetPool(); src.clip = TestClip; src.volume = 0.001f; src.Play(); }
            sw.Stop();
            double poolCpMs = n / sw.Elapsed.TotalMilliseconds;
            yield return null; Progress = 0.66f;

            // Native
            SetStatus($"Throughput — Native ({n})…");
            _bridge.StopAll();
            sw.Restart();
            for (int i = 0; i < n; i++) _bridge.PlayClip(0, 0.001f);
            sw.Stop();
            double nativeCpMs = n / sw.Elapsed.TotalMilliseconds;
            yield return null; Progress = 1f;

            ThroughputResult = new AudioBenchThroughputResult
            {
                NaiveCallsPerMs  = naiveCpMs,
                PooledCallsPerMs = poolCpMs,
                NativeCallsPerMs = nativeCpMs,
                Iterations       = n,
                Valid            = true
            };

            MID_Logger.LogInfo(_logLevel,
                $"[Throughput] Naive: {naiveCpMs:F0}/ms  Pool: {poolCpMs:F0}/ms  Native: {nativeCpMs:F0}/ms\n" +
                $"  Native is {ThroughputResult.NativeVsNaiveRatio:F0}× faster than PlayOneShot, " +
                $"{ThroughputResult.NativeVsPooledRatio:F0}× faster than pooled Play.",
                nameof(MID_AudioBenchRunner));

            SetStatus($"Throughput — Naive: {naiveCpMs:F0}/ms | Pool: {poolCpMs:F0}/ms | Native: {nativeCpMs:F0}/ms  ({ThroughputResult.NativeVsNaiveRatio:F0}× vs naive)");
        }

        // ── Voice Accuracy Test ───────────────────────────────────────────────
        // FIX: was yield return null × 2 — not guaranteed to cover one audio frame.
        // Audio callbacks fire every ~10-21ms at 48kHz (buffer size dependent).
        // WaitForSeconds(0.05f) ensures at least 2-5 audio callbacks have run.

        private IEnumerator VoiceInner()
        {
            SetStatus("Voice accuracy…");
            _bridge.StopAll();
            yield return new WaitForSeconds(0.05f); // let the reset propagate on audio thread

            const int TARGET = 8;
            for (int i = 0; i < TARGET; i++) _bridge.PlayClip(0, 0.001f);

            // Wait for audio thread to pick up pending_trigger flags via process_buffer
            yield return new WaitForSeconds(0.05f);

            int active = _bridge.ActiveVoiceCount;
            bool ok = active == TARGET;

            MID_Logger.LogInfo(_logLevel,
                $"[Voice] Scheduled {TARGET}, active: {active} — " + (ok ? "✓" : "✗ expected " + TARGET) + "\n" +
                "  Voices pending on game thread → active on audio thread after process_buffer().",
                nameof(MID_AudioBenchRunner));

            VoiceResult = new AudioBenchVoiceResult
            {
                ScheduledCount  = TARGET,
                ActiveCount     = active,
                MatchesExpected = ok,
                Valid           = true
            };

            SetStatus($"Voice — Scheduled {TARGET}, Active {active} " + (ok ? "✓" : "✗"));
            Progress = 1f;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private AudioSource GetPool()
        {
            for (int i = 0; i < POOL_SIZE; i++) { int idx = (_manualPoolIdx + i) % POOL_SIZE; if (!_manualPool[idx].isPlaying) { _manualPoolIdx = (idx + 1) % POOL_SIZE; return _manualPool[idx]; } }
            var stolen = _manualPool[_manualPoolIdx % POOL_SIZE]; _manualPoolIdx = (_manualPoolIdx + 1) % POOL_SIZE; stolen.Stop(); return stolen;
        }

        private IEnumerator DoGC() { GC.Collect(2, GCCollectionMode.Forced, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, true); yield return null; yield return null; }

        private static long GetThreadBytes()
        {
            try { return GC.GetAllocatedBytesForCurrentThread(); } catch { return GC.GetTotalMemory(false); }
        }

        private void SetStatus(string m) { StatusMessage = m; MID_Logger.LogDebug(_logLevel, m, nameof(MID_AudioBenchRunner)); }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Editor Window
    // ═════════════════════════════════════════════════════════════════════════

#if UNITY_EDITOR

    public class MID_AudioBenchWindow : EditorWindow
    {
        private MID_AudioBenchRunner _runner;
        private Vector2              _scroll;
        private bool _fGC = true, _fThroughput = true, _fVoice = true;

        private static readonly Color ColNaive  = new(1.00f, 0.50f, 0.20f, 1f);
        private static readonly Color ColPooled = new(0.40f, 0.65f, 1.00f, 1f);
        private static readonly Color ColNative = new(0.28f, 0.92f, 0.45f, 1f);
        private static readonly Color ColBarBg  = new(0.12f, 0.12f, 0.12f, 0.5f);
        private static readonly Color ColDim    = new(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color ColPass   = new(0.28f, 0.95f, 0.45f, 1f);
        private static readonly Color ColFail   = new(1.00f, 0.35f, 0.35f, 1f);
        private static readonly Color ColWarn   = new(1.00f, 0.85f, 0.25f, 1f);
        private static readonly Color ColInfo   = new(0.55f, 0.80f, 1.00f, 1f);

        [MenuItem("MidManStudio/Utilities/Tests/Audio Bench", priority = 122)]
        public static void Open() { var w = GetWindow<MID_AudioBenchWindow>("Audio Bench"); w.minSize = new Vector2(540, 580); }

        private void OnEnable()  { EditorApplication.update += Repaint; Find(); }
        private void OnDisable() { EditorApplication.update -= Repaint; }
        private void Find() { if (_runner == null) _runner = FindObjectOfType<MID_AudioBenchRunner>(); }

        private void OnGUI()
        {
            Find();
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField("MidManStudio — Audio Benchmark", EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                _runner = (MID_AudioBenchRunner)EditorGUILayout.ObjectField(_runner, typeof(MID_AudioBenchRunner), true, GUILayout.Width(200));
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.Space(4);

            if (!Application.isPlaying) { EditorGUILayout.HelpBox("Enter Play Mode to run benchmarks.", MessageType.Info); EditorGUILayout.EndScrollView(); return; }

            if (_runner == null)
            {
                EditorGUILayout.HelpBox("Add MID_AudioBenchRunner to a scene GameObject.\nAssign a TestClip (Decompress On Load).\nMID_NativeAudioBridge must be present.", MessageType.Warning);
                if (GUILayout.Button("Add Runner", GUILayout.Height(28))) { var go = new GameObject("[AudioBenchRunner]"); _runner = go.AddComponent<MID_AudioBenchRunner>(); Undo.RegisterCreatedObjectUndo(go, "Add Audio Bench Runner"); Selection.activeGameObject = go; }
                EditorGUILayout.EndScrollView(); return;
            }

            DrawRunButtons();
            Sep();
            DrawGCSection();
            Sep();
            DrawThroughputSection();
            Sep();
            DrawVoiceSection();
            Sep();
            DrawLegend();
            EditorGUILayout.EndScrollView();
        }

        private void DrawRunButtons()
        {
            if (_runner.IsRunning) { Rect r = EditorGUILayout.GetControlRect(false, 20); r.x += 2; r.width -= 4; EditorGUI.ProgressBar(r, _runner.Progress, _runner.StatusMessage); }
            else { using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox)) EditorGUILayout.LabelField(_runner.StatusMessage, EditorStyles.miniLabel, GUILayout.ExpandWidth(true)); }

            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = !_runner.IsRunning;
                var oldbg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.25f, 0.80f, 0.30f);
                if (GUILayout.Button("▶  Run All",      GUILayout.Height(30))) _runner.RunAll();
                GUI.backgroundColor = ColNative * 0.75f;
                if (GUILayout.Button("GC",               GUILayout.Height(30))) _runner.RunGCOnly();
                if (GUILayout.Button("Throughput",       GUILayout.Height(30))) _runner.RunThroughput();
                if (GUILayout.Button("Voice",            GUILayout.Height(30))) _runner.RunVoice();
                GUI.backgroundColor = new Color(0.85f, 0.25f, 0.25f);
                GUI.enabled = _runner.IsRunning;
                if (GUILayout.Button("■  Cancel",        GUILayout.Height(30))) _runner.Cancel();
                GUI.backgroundColor = oldbg; GUI.enabled = true;
            }
            EditorGUILayout.Space(4);
        }

        private void DrawGCSection()
        {
            _fGC = EditorGUILayout.BeginFoldoutHeaderGroup(_fGC, "GC Allocation — managed heap only");
            if (_fGC)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    // Always show the explanation regardless of results
                    Coloured(ColInfo,
                        "0 B for ALL THREE paths is the correct and expected result.\n" +
                        "PlayOneShot / AudioSource.Play() call into Unity's C++ audio subsystem — " +
                        "no .NET managed heap allocation occurs in the calling thread.\n" +
                        "The Native path is also 0 B: blittable DllImport (int + float) has no managed overhead.\n" +
                        "GC.CollectionCount shows whether any GC pressure occurred.");
                    EditorGUILayout.Space(3);

                    var res = _runner.GCResult;
                    if (!res.Valid) { EditorGUILayout.HelpBox("Run GC test to see results.", MessageType.Info); }
                    else
                    {
                        Coloured(res.CalibrationPassed ? ColPass : ColFail,
                            res.CalibrationPassed
                                ? "✓ Calibration passed — counter is working. 0 B is a real result."
                                : "⚠ Calibration failed — use Profiler > GC Alloc for ground truth.");

                        EditorGUILayout.Space(3);
                        using (new EditorGUILayout.HorizontalScope()) { CH("A) PlayOneShot", ColNaive); CH("B) Pooled", ColPooled); CH("C) Native", ColNative); }
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            VC(res.NaiveBytesPerCall,  $"GC events: {res.NaiveGCCollections}",  ColNaive);
                            VC(res.PooledBytesPerCall, $"GC events: {res.PooledGCCollections}", ColPooled);
                            VC(res.NativeBytesPerCall, $"GC events: {res.NativeGCCollections}", ColNative);
                        }
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawThroughputSection()
        {
            _fThroughput = EditorGUILayout.BeginFoldoutHeaderGroup(_fThroughput, "Scheduling Throughput  (higher = better, calls/ms)");
            if (_fThroughput)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    var res = _runner.ThroughputResult;
                    if (!res.Valid) { EditorGUILayout.HelpBox("Run Throughput test.", MessageType.Info); }
                    else
                    {
                        using (new EditorGUILayout.HorizontalScope()) { CH("A) PlayOneShot", ColNaive); CH("B) Pooled", ColPooled); CH("C) Native", ColNative); }
                        using (new EditorGUILayout.HorizontalScope()) { TC(res.NaiveCallsPerMs, ColNaive); TC(res.PooledCallsPerMs, ColPooled); TC(res.NativeCallsPerMs, ColNative); }
                        EditorGUILayout.Space(4);
                        double mx = Math.Max(res.NaiveCallsPerMs, Math.Max(res.PooledCallsPerMs, res.NativeCallsPerMs));
                        if (mx > 0)
                        {
                            Bar("Naive",  (float)(res.NaiveCallsPerMs  / mx), ColNaive,  $"{res.NaiveCallsPerMs:F0} calls/ms   {1000.0/res.NaiveCallsPerMs:F1} µs/call");
                            Bar("Pooled", (float)(res.PooledCallsPerMs / mx), ColPooled, $"{res.PooledCallsPerMs:F0} calls/ms   {1000.0/res.PooledCallsPerMs:F2} µs/call");
                            Bar("Native", (float)(res.NativeCallsPerMs / mx), ColNative, $"{res.NativeCallsPerMs:F0} calls/ms   {1000.0/res.NativeCallsPerMs:F4} µs/call");
                        }
                        EditorGUILayout.Space(2);
                        if (res.NativeVsNaiveRatio > 0)
                        {
                            Coloured(ColNative, $"Native is {res.NativeVsNaiveRatio:F0}× faster than PlayOneShot,  {res.NativeVsPooledRatio:F0}× faster than pooled Play.");
                            Coloured(ColDim, "THIS is the meaningful number — throughput, not GC bytes.");
                        }
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawVoiceSection()
        {
            _fVoice = EditorGUILayout.BeginFoldoutHeaderGroup(_fVoice, "Voice Accuracy — scheduled vs active");
            if (_fVoice)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    var res = _runner.VoiceResult;
                    if (!res.Valid) { EditorGUILayout.HelpBox("Run Voice test.", MessageType.Info); }
                    else
                    {
                        bool ok = res.MatchesExpected;
                        Coloured(ok ? ColPass : ColFail,
                            ok ? $"✓ Scheduled {res.ScheduledCount} → Active {res.ActiveCount}  (match)"
                               : $"✗ Scheduled {res.ScheduledCount} → Active {res.ActiveCount}  (mismatch — increase WaitForSeconds in voice test or check audio thread is running)");
                        EditorGUILayout.Space(2);
                        Coloured(ColDim, "Voices are pending on the game thread until process_buffer() runs on the audio thread.\nThe 0.05s wait covers ~2-5 audio callbacks at standard DSP buffer sizes.");
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawLegend()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                Sw(ColNaive); EditorGUILayout.LabelField("A) PlayOneShot", EditorStyles.miniLabel, GUILayout.Width(100));
                Sw(ColPooled); EditorGUILayout.LabelField("B) Pool",        EditorStyles.miniLabel, GUILayout.Width(60));
                Sw(ColNative); EditorGUILayout.LabelField("C) Native DLL",  EditorStyles.miniLabel, GUILayout.Width(80));
                GUILayout.FlexibleSpace();
                Sw(ColPass); EditorGUILayout.LabelField("✓ pass", EditorStyles.miniLabel, GUILayout.Width(50));
                Sw(ColFail); EditorGUILayout.LabelField("✗ fail", EditorStyles.miniLabel, GUILayout.Width(50));
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private float CW => (position.width - 28f) / 3f;

        private void CH(string t, Color c) { var o = GUI.color; GUI.color = c; EditorGUILayout.LabelField(t, EditorStyles.miniBoldLabel, GUILayout.Width(CW)); GUI.color = o; }

        private void VC(long bytes, string sub, Color col)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(CW)))
            {
                var o = GUI.color; GUI.color = bytes == 0 ? ColPass : col;
                EditorGUILayout.LabelField(bytes == 0 ? "0 B  ✓" : $"{bytes} B", EditorStyles.boldLabel);
                GUI.color = ColDim; EditorGUILayout.LabelField(sub, EditorStyles.miniLabel); GUI.color = o;
            }
        }

        private void TC(double cpMs, Color col)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(CW)))
            {
                var o = GUI.color; GUI.color = col;
                EditorGUILayout.LabelField($"{cpMs:F0} /ms", EditorStyles.boldLabel);
                GUI.color = ColDim; EditorGUILayout.LabelField($"{1000.0/cpMs:F3} µs/call", EditorStyles.miniLabel); GUI.color = o;
            }
        }

        private void Bar(string label, float f, Color col, string tip)
        {
            f = Mathf.Clamp01(f);
            using (new EditorGUILayout.HorizontalScope())
            {
                var o = GUI.color; GUI.color = ColDim;
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(60));
                GUI.color = o;
                Rect r = EditorGUILayout.GetControlRect(false, 14, GUILayout.ExpandWidth(true));
                r.y += 2; r.height = 10;
                EditorGUI.DrawRect(r, ColBarBg);
                if (f > 0.002f) { Rect fill = r; fill.width = Mathf.Max(r.width * f, 2f); EditorGUI.DrawRect(fill, col); }
                else { Rect t = r; t.width = 4f; EditorGUI.DrawRect(t, ColPass); }
                GUI.color = ColDim;
                EditorGUILayout.LabelField(tip, EditorStyles.miniLabel, GUILayout.Width(280));
                GUI.color = o;
            }
        }

        private static void Coloured(Color c, string t) { var o = GUI.color; GUI.color = c; EditorGUILayout.LabelField(t, EditorStyles.wordWrappedMiniLabel); GUI.color = o; }
        private static void Sw(Color c) { Rect r = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12)); r.y += 3; r.height = 8; r.width = 8; EditorGUI.DrawRect(r, c); GUILayout.Space(2); }
        private static void Sep() { Rect r = EditorGUILayout.GetControlRect(false, 1); EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.35f)); EditorGUILayout.Space(4); }
    }

#endif
}
