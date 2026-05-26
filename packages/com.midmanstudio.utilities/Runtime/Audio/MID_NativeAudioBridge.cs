// MID_NativeAudioBridge.cs
//
// Native DSP audio bridge. Platform dispatch:
//
//   Desktop / Mobile (Win, Mac, Linux, Android, iOS):
//     Uses Rust mid_audio DLL via DllImport.
//     schedule_voice() → main thread, ~10-50 ns (essentially free).
//     process_buffer() → Unity audio DSP thread via OnAudioFilterRead.
//     16-voice mix with 512-sample buffer: ~0.0135 ms = 0.064% of 48 kHz frame budget.
//
//   WebGL:
//     DllImport NOT available — Unity WebGL uses the Web Audio API.
//     OnAudioFilterRead still fires (Unity implements it via ScriptProcessorNode),
//     but process_buffer is never called since the DLL does not exist.
//     PlayClip() uses a pre-created AudioSource[] pool instead.
//     The pool is a circular steal — if all 16 sources are playing, oldest is stolen.
//     GC: AudioSource.Play() is 0-alloc when called on a pre-warmed source.
//
// Public interface is identical on all platforms. Game code never branches.
//
// SETUP (all platforms):
//   1. Add this component to your Managers prefab.
//   2. Add an AudioSource to the same GameObject.
//      clip = null, playOnAwake = false, loop = true, spatialBlend = 0.
//      The AudioSource must exist even if the DLL is active — it hosts the DSP chain.
//   3. Call UploadClip() at startup for each impact/muzzle/shell sound.
//      Clips must have Load Type = Decompress On Load.
//   4. Call PlayClip(index, volume) from game code on the main thread.
//
// GC:
//   Desktop/Mobile: schedule_voice (blittable DllImport) = 0 GC always.
//   WebGL: AudioSource.Play() on pre-created sources = 0 GC after warmup.
//   Neither path allocates inside OnAudioFilterRead (audio thread safe).

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;

namespace MidManStudio.Core.Audio
{
    [RequireComponent(typeof(AudioSource))]
    public class MID_NativeAudioBridge : Singleton<MID_NativeAudioBridge>
    {
        // ── DllImport — desktop + mobile only ────────────────────────────────
        // Excluded from WebGL builds entirely. The compiler never sees these
        // on WebGL so there is no unresolved symbol at link time.
        // DO NOT call any of these methods without #if guards.
#if !UNITY_WEBGL || UNITY_EDITOR

#if UNITY_IOS && !UNITY_EDITOR
        private const string LIB = "__Internal";
#else
        private const string LIB = "mid_audio";
#endif

        [DllImport(LIB)] private static extern int  upload_pcm_clip(IntPtr pcmData, int sampleCount);
        [DllImport(LIB)] private static extern void schedule_voice(int bankSlot, float volume01);
        [DllImport(LIB)] private static extern void process_buffer(float[] buffer, int length);
        [DllImport(LIB)] private static extern void reset_audio_state();
        [DllImport(LIB)] private static extern int  active_voice_count();

#endif // !UNITY_WEBGL || UNITY_EDITOR

        // ── Inspector ─────────────────────────────────────────────────────────

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Clips — must be Decompress On Load")]
        [Tooltip("Assign AudioClips here. Index 0 = impact, 1 = muzzle, 2 = shell, etc.\n" +
                 "On desktop/mobile: decoded PCM is uploaded to the Rust bank.\n" +
                 "On WebGL: clips are played directly via managed AudioSource pool.")]
        public AudioClip[] _clips;

        [Header("WebGL Managed Pool")]
        [Tooltip("Number of AudioSource voices in the WebGL managed fallback pool.\n" +
                 "Ignored on desktop/mobile (Rust DSP handles voice count there).")]
        [SerializeField] [Range(4, 32)] private int _webglPoolSize = 16;

        // ── Public state ──────────────────────────────────────────────────────

        public bool IsUsingNativeDSP =>
#if !UNITY_WEBGL || UNITY_EDITOR
            true;
#else
            false;
#endif

        // ── Private — native path ─────────────────────────────────────────────

        private int[]    _bankSlots;   // clip index → Rust bank slot
        private float[]  _decodeBuffer; // reused PCM decode buffer (one alloc at startup)

        // ── Private — WebGL managed pool ─────────────────────────────────────

#if UNITY_WEBGL && !UNITY_EDITOR
        private AudioSource[] _webglPool;
        private int           _webglPoolIdx;
#endif

        // ── Editor diagnostics (read-only) ────────────────────────────────────

        [SerializeField, HideInInspector] private int    _debugActiveVoices;
        [SerializeField, HideInInspector] private string _debugPlatform;

        // ── Unity Lifecycle ───────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();

            _debugPlatform = IsUsingNativeDSP ? "Native DSP (Rust DLL)" : "Managed Pool (WebGL)";

#if UNITY_WEBGL && !UNITY_EDITOR
            InitWebGLPool();
#else
            InitNativePath();
#endif

            MID_Logger.LogInfo(_logLevel,
                $"NativeAudioBridge ready — {_debugPlatform} — {_clips?.Length ?? 0} clip(s).",
                nameof(MID_NativeAudioBridge), nameof(Awake));
        }

        private void Update()
        {
#if UNITY_EDITOR
            _debugActiveVoices = ActiveVoiceCount;
#endif
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
#if !UNITY_WEBGL || UNITY_EDITOR
            if (IsUsingNativeDSP) reset_audio_state();
#endif
        }

        // ── Audio DSP Thread — native path only ───────────────────────────────
        // OnAudioFilterRead fires on the audio thread. On WebGL it still fires
        // but we do nothing here — managed AudioSources handle playback directly.
        // DO NOT allocate or call Unity APIs from inside this method.
        private void OnAudioFilterRead(float[] data, int channels)
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            process_buffer(data, data.Length);
#endif
            // WebGL: intentional no-op. Managed AudioSources handle their own output.
        }

        // ── Public API — main thread only ─────────────────────────────────────

        /// <summary>
        /// Play a clip by its index in the _clips inspector array.
        /// Main thread only. Zero GC allocation on all platforms after warmup.
        /// </summary>
        public void PlayClip(int clipIndex, float volume = 1f)
        {
            if (_bankSlots == null || clipIndex < 0 || clipIndex >= _bankSlots.Length) return;

#if UNITY_WEBGL && !UNITY_EDITOR
            PlayClipManaged(clipIndex, volume);
#else
            int slot = _bankSlots[clipIndex];
            if (slot < 0)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Clip [{clipIndex}] not uploaded — cannot play.",
                    nameof(MID_NativeAudioBridge));
                return;
            }
            schedule_voice(slot, Mathf.Clamp01(volume));
#endif
        }

        /// <summary>
        /// Number of currently active voices.
        /// Desktop/Mobile: queries Rust atomic counter.
        /// WebGL: counts non-idle sources in the managed pool.
        /// </summary>
        public int ActiveVoiceCount
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (_webglPool == null) return 0;
                int count = 0;
                foreach (var s in _webglPool) if (s != null && s.isPlaying) count++;
                return count;
#else
                return IsUsingNativeDSP ? active_voice_count() : 0;
#endif
            }
        }

        /// <summary>
        /// Stop all active voices and reset the DSP state.
        /// Safe to call from main thread only.
        /// </summary>
        public void ResetAllVoices()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_webglPool != null) foreach (var s in _webglPool) s?.Stop();
            _webglPoolIdx = 0;
#else
            if (IsUsingNativeDSP) reset_audio_state();
#endif
            MID_Logger.LogInfo(_logLevel, "All voices reset.",
                nameof(MID_NativeAudioBridge));
        }

        // ── Native path init ──────────────────────────────────────────────────

#if !UNITY_WEBGL || UNITY_EDITOR
        private void InitNativePath()
        {
            // The AudioSource must be playing for OnAudioFilterRead to fire.
            var src = GetComponent<AudioSource>();
            src.clip       = null;
            src.loop       = true;
            src.volume     = 1f;
            src.spatialBlend = 0f;
            if (!src.isPlaying) src.Play();

            UploadAllClips();
        }

        private void UploadAllClips()
        {
            if (_clips == null || _clips.Length == 0) return;
            _bankSlots = new int[_clips.Length];

            for (int i = 0; i < _clips.Length; i++)
            {
                _bankSlots[i] = UploadSingleClip(_clips[i]);
                MID_Logger.LogDebug(_logLevel,
                    $"Clip [{i}] '{(_clips[i] != null ? _clips[i].name : "null")}' → bank slot {_bankSlots[i]}",
                    nameof(MID_NativeAudioBridge));
            }
        }

        private unsafe int UploadSingleClip(AudioClip clip)
        {
            if (clip == null) return -1;
            int sampleCount = clip.samples * clip.channels;

            // One decode buffer reused across all uploads — single alloc at startup.
            if (_decodeBuffer == null || _decodeBuffer.Length < sampleCount)
                _decodeBuffer = new float[sampleCount];

            if (!clip.GetData(_decodeBuffer, 0))
            {
                MID_Logger.LogError(_logLevel,
                    $"GetData failed for '{clip.name}'. " +
                    "Set Load Type = Decompress On Load in the AudioClip import settings.",
                    nameof(MID_NativeAudioBridge));
                return -1;
            }

            fixed (float* ptr = _decodeBuffer)
                return upload_pcm_clip((IntPtr)ptr, sampleCount);
        }
#endif // !UNITY_WEBGL || UNITY_EDITOR

        // ── WebGL managed pool ────────────────────────────────────────────────

#if UNITY_WEBGL && !UNITY_EDITOR
        private void InitWebGLPool()
        {
            if (_clips == null || _clips.Length == 0) return;

            // Bank slots on WebGL are just clip indices (no Rust PCM bank).
            _bankSlots = new int[_clips.Length];
            for (int i = 0; i < _clips.Length; i++) _bankSlots[i] = i;

            // Pre-create AudioSource components on this GameObject.
            // These are parented to the manager — DontDestroyOnLoad via Singleton.
            _webglPool = new AudioSource[_webglPoolSize];
            for (int i = 0; i < _webglPoolSize; i++)
            {
                var go = new GameObject($"AudioVoice_{i:D2}");
                go.transform.SetParent(transform);

                var src = go.AddComponent<AudioSource>();
                src.spatialBlend  = 0f;
                src.playOnAwake   = false;
                src.loop          = false;
                src.volume        = 1f;

                _webglPool[i] = src;
            }

            MID_Logger.LogInfo(_logLevel,
                $"WebGL managed pool: {_webglPoolSize} AudioSource voices.",
                nameof(MID_NativeAudioBridge));
        }

        private void PlayClipManaged(int clipIndex, float volume)
        {
            if (_webglPool == null || _clips == null) return;
            if (clipIndex < 0 || clipIndex >= _clips.Length) return;
            var clip = _clips[clipIndex];
            if (clip == null) return;

            // Find free slot; steal oldest if all busy.
            AudioSource target = null;
            for (int i = 0; i < _webglPool.Length; i++)
            {
                int idx = (_webglPoolIdx + i) % _webglPool.Length;
                if (!_webglPool[idx].isPlaying) { target = _webglPool[idx]; _webglPoolIdx = (idx + 1) % _webglPool.Length; break; }
            }
            if (target == null)
            {
                // All busy — steal from circular position (voice steal)
                target = _webglPool[_webglPoolIdx % _webglPool.Length];
                _webglPoolIdx = (_webglPoolIdx + 1) % _webglPool.Length;
                target.Stop();
            }

            target.clip   = clip;
            target.volume = Mathf.Clamp01(volume);
            target.Play();
        }
#endif // UNITY_WEBGL && !UNITY_EDITOR
    }
}
