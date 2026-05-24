// MID_NativeAudioBridge.cs
// Bridges Unity's audio DSP thread to the mid_audio Rust DLL.
//
// SETUP:
//   1. Attach this to a persistent GameObject (your Managers prefab).
//   2. Attach a standard AudioSource to the same GameObject.
//      Set the AudioSource clip to null. Play On Awake = false. Loop = false.
//      The AudioSource must exist — OnAudioFilterRead needs it present,
//      even with no clip assigned. Set it to Play() so the filter fires.
//   3. Call UploadClip() at startup for each impact/muzzle/shell sound.
//   4. Call ScheduleImpact() / ScheduleMuzzle() / ScheduleShell() from
//      your game code when events fire.
//
// THREADING NOTE:
//   - UploadClip, ScheduleImpact, Reset → main thread only
//   - OnAudioFilterRead → audio thread (Unity calls this ~every 20ms)
//   - The Rust DLL handles the thread boundary with atomics internally
//
// GC NOTE:
//   Unity's stop-the-world GC pauses the audio thread too.
//   To reduce stutters: set Project Settings > Audio > DSP Buffer Size to Best Latency,
//   and ensure your game code avoids GC allocations during heavy combat.

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Audio
{
    [RequireComponent(typeof(AudioSource))]
    public class MID_NativeAudioBridge : MonoBehaviour
    {
        // ── DllImport ─────────────────────────────────────────────────────────

#if UNITY_IOS && !UNITY_EDITOR
        private const string LIB = "__Internal";
#else
        private const string LIB = "mid_audio";
#endif

        [DllImport(LIB)] private static extern int   upload_pcm_clip(IntPtr pcmData, int sampleCount);
        [DllImport(LIB)] private static extern void  schedule_voice(int bankSlot, float volume01);
        [DllImport(LIB)] private static extern void  process_buffer(float[] buffer, int length);
        [DllImport(LIB)] private static extern void  reset_audio_state();
        [DllImport(LIB)] private static extern int   active_voice_count();

        // ── Inspector ─────────────────────────────────────────────────────────

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Clips to upload at startup")]
        [Tooltip("Assign AudioClips here. They will be decoded and uploaded to " +
                 "the Rust DLL's PCM bank at Awake. Index = bank slot used in ScheduleVoice.")]
        [SerializeField] private AudioClip[] _clips;

        // ── State ─────────────────────────────────────────────────────────────

        public static MID_NativeAudioBridge Instance { get; private set; }

        // Bank slot assignments set during Awake
        private int[] _bankSlots;

        // Clip names for editor diagnostics
        private string[] _clipNames;

        // Pre-allocated decode buffer — reused across uploads to avoid GC
        private float[] _decodeBuffer;

        // Runtime diagnostic (read from editor window if desired)
        [SerializeField, HideInInspector] private int _activeVoices;

        // ── Unity Lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            UploadAllClips();

            // The AudioSource must be playing for OnAudioFilterRead to fire,
            // even with no clip assigned — start it silently.
            var src = GetComponent<AudioSource>();
            src.clip       = null;
            src.loop       = true;
            src.volume     = 1f;
            src.spatialBlend = 0f;
            src.Play();

            MID_Logger.LogInfo(_logLevel, "NativeAudioBridge ready.",
                nameof(MID_NativeAudioBridge), nameof(Awake));
        }

        private void Update()
        {
            // Diagnostic only — safe to poll from main thread
#if UNITY_EDITOR
            _activeVoices = active_voice_count();
#endif
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            reset_audio_state();
            Instance = null;
        }

        // ── Audio Thread ──────────────────────────────────────────────────────

        // Called by Unity on the audio DSP thread ~every 20ms.
        // DO NOT allocate here. DO NOT call Unity APIs here.
        private void OnAudioFilterRead(float[] data, int channels)
        {
            process_buffer(data, data.Length);
        }

        // ── Public API — main thread only ─────────────────────────────────────

        /// <summary>
        /// Play a clip by its index in the _clips array.
        /// Call from game code on the main thread (e.g. projectile hit handler).
        /// </summary>
        public void PlayClip(int clipIndex, float volume = 1f)
        {
            if (_bankSlots == null || clipIndex < 0 || clipIndex >= _bankSlots.Length) return;
            int slot = _bankSlots[clipIndex];
            if (slot < 0)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Clip [{clipIndex}] was not uploaded successfully.",
                    nameof(MID_NativeAudioBridge));
                return;
            }
            schedule_voice(slot, Mathf.Clamp01(volume));
        }

        /// <summary>Returns the number of currently active Rust audio voices.</summary>
        public int ActiveVoiceCount => active_voice_count();

        // ── Private ───────────────────────────────────────────────────────────

        private void UploadAllClips()
        {
            if (_clips == null || _clips.Length == 0) return;

            _bankSlots  = new int[_clips.Length];
            _clipNames  = new string[_clips.Length];

            for (int i = 0; i < _clips.Length; i++)
            {
                _clipNames[i]  = _clips[i] != null ? _clips[i].name : "null";
                _bankSlots[i]  = UploadClip(_clips[i]);

                MID_Logger.LogDebug(_logLevel,
                    $"Clip [{i}] '{_clipNames[i]}' → bank slot {_bankSlots[i]}",
                    nameof(MID_NativeAudioBridge));
            }
        }

        private unsafe int UploadClip(AudioClip clip)
        {
            if (clip == null) return -1;

            int sampleCount = clip.samples * clip.channels;

            // Reuse or grow decode buffer — one alloc at startup, never during play
            if (_decodeBuffer == null || _decodeBuffer.Length < sampleCount)
                _decodeBuffer = new float[sampleCount];

            if (!clip.GetData(_decodeBuffer, 0))
            {
                MID_Logger.LogError(_logLevel,
                    $"Failed to decode clip '{clip.name}'. " +
                    "Make sure Load Type is Decompress On Load.",
                    nameof(MID_NativeAudioBridge));
                return -1;
            }

            // Pin the managed array and pass a raw pointer to Rust
            fixed (float* ptr = _decodeBuffer)
            {
                return upload_pcm_clip((IntPtr)ptr, sampleCount);
            }
        }
    }
}
