// MID_NativeAudioBridge.cs  — v2: AudioSource Pool
//
// WHAT CHANGED FROM v1 AND WHY:
//
// Removed: AudioClip.GetData() → PCM upload → Rust voice mixing
//   GetData() requires Decompress On Load AND the clip to be fully loaded in memory
//   at the exact moment Awake() runs. Unity loads clips asynchronously; the call
//   races against the loader and produces "data larger than clip loaded" even with
//   correct import settings. Bypassing Unity's hardware audio decoders also doubles
//   memory and breaks streaming clips entirely. Wrong tool for a general-purpose package.
//
// Added: Standard AudioSource pool (16 voices, circular steal)
//   Unity's AudioSource handles decoding, 3D spatialization, and AudioMixer routing.
//   No clip format requirements — works with Compressed In Memory, Streaming, anything.
//   PlayClip() is O(pool_size) worst-case to find a free source (typically O(1)).
//
// Rust DLL role: see MID_AudioLimiter.cs
//   The limiter runs in OnAudioFilterRead on the AudioListener — not here.
//   This class has no DllImport at all. It's pure C# AudioSource management.
//
// SETUP:
//   1. Add MID_NativeAudioBridge to your Managers prefab.
//   2. Assign AudioClips in the _clips inspector array (any Load Type works).
//   3. Add MID_AudioLimiter to your AudioListener GameObject (Camera or dedicated).
//   4. Call PlayClip(index, volume) from game code.
//
// WEBGL:
//   Works identically — AudioSource pool is pure C# with no platform restrictions.
//   MID_AudioLimiter skips the Rust DLL on WebGL (C# fallback limiter instead).

using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;

namespace MidManStudio.Core.Audio
{
    public class MID_NativeAudioBridge : Singleton<MID_NativeAudioBridge>
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Clips  (any Load Type — Decompress On Load NOT required)")]
        [Tooltip("Assign AudioClips here. Index matches the clipIndex parameter of PlayClip().\n" +
                 "0 = impact, 1 = muzzle, 2 = shell ejection, etc.")]
        [SerializeField] private AudioClip[] _clips;

        [Header("Voice Pool")]
        [Tooltip("Number of simultaneous voices. Oldest voice is stolen when pool is full.")]
        [SerializeField] [Range(4, 32)] private int _poolSize = 16;

        [Tooltip("Spatial blend for all pool voices. 0 = 2D (ignores position). 1 = full 3D.")]
        [SerializeField] [Range(0f, 1f)] private float _spatialBlend = 0f;

        // ── State ─────────────────────────────────────────────────────────────

        private AudioSource[] _pool;
        private int           _poolIdx;

        // Inspector diagnostics
        [SerializeField, HideInInspector] private int _activeVoiceCount;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();
            BuildPool();
            MID_Logger.LogInfo(_logLevel,
                $"NativeAudioBridge ready — {_poolSize}-voice pool, {_clips?.Length ?? 0} clip(s).",
                nameof(MID_NativeAudioBridge));
        }

        private void Update()
        {
#if UNITY_EDITOR
            _activeVoiceCount = ActiveVoiceCount;
#endif
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Play a clip from the pool.
        /// clipIndex matches the _clips inspector array (0 = impact, 1 = muzzle, etc.)
        /// volume 0.0–1.0. Main thread only. Zero GC allocation.
        /// </summary>
        public void PlayClip(int clipIndex, float volume = 1f)
        {
            if (_pool == null || _clips == null) return;
            if (clipIndex < 0 || clipIndex >= _clips.Length) return;

            var clip = _clips[clipIndex];
            if (clip == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"PlayClip({clipIndex}): clip is null.",
                    nameof(MID_NativeAudioBridge));
                return;
            }

            var src = GetPoolSource();
            src.clip         = clip;
            src.volume       = Mathf.Clamp01(volume);
            src.spatialBlend = _spatialBlend;
            src.Play();
        }

        /// <summary>
        /// Play a specific AudioClip directly (without requiring it to be in the _clips array).
        /// Useful for runtime-generated or dynamically loaded clips.
        /// </summary>
        public void PlayClipDirect(AudioClip clip, float volume = 1f)
        {
            if (_pool == null || clip == null) return;
            var src = GetPoolSource();
            src.clip         = clip;
            src.volume       = Mathf.Clamp01(volume);
            src.spatialBlend = _spatialBlend;
            src.Play();
        }

        /// <summary>Number of pool voices currently playing.</summary>
        public int ActiveVoiceCount
        {
            get
            {
                if (_pool == null) return 0;
                int count = 0;
                foreach (var s in _pool) if (s != null && s.isPlaying) count++;
                return count;
            }
        }

        /// <summary>Stop all active voices immediately.</summary>
        public void StopAll()
        {
            if (_pool == null) return;
            foreach (var s in _pool) if (s != null && s.isPlaying) s.Stop();
            MID_Logger.LogInfo(_logLevel, "All voices stopped.", nameof(MID_NativeAudioBridge));
        }

        /// <summary>Returns the AudioClip at the given index (null if out of range).</summary>
        public AudioClip GetClip(int index) =>
            _clips != null && index >= 0 && index < _clips.Length ? _clips[index] : null;

        /// <summary>Total number of clip slots.</summary>
        public int ClipCount => _clips?.Length ?? 0;

        // ── Private — pool management ─────────────────────────────────────────

        private void BuildPool()
        {
            _pool = new AudioSource[_poolSize];
            for (int i = 0; i < _poolSize; i++)
            {
                var go = new GameObject($"AudioVoice_{i:D2}");
                go.transform.SetParent(transform);

                var src = go.AddComponent<AudioSource>();
                src.playOnAwake  = false;
                src.loop         = false;
                src.spatialBlend = _spatialBlend;
                src.volume       = 1f;

                _pool[i] = src;
            }
        }

        /// <summary>
        /// Returns the next available AudioSource.
        /// Prefers non-playing sources; steals the current circular-position slot if all busy.
        /// </summary>
        private AudioSource GetPoolSource()
        {
            // Scan from current position for a free slot
            for (int i = 0; i < _poolSize; i++)
            {
                int idx = (_poolIdx + i) % _poolSize;
                if (!_pool[idx].isPlaying)
                {
                    _poolIdx = (idx + 1) % _poolSize;
                    return _pool[idx];
                }
            }

            // All busy — steal from current position (oldest in circular sequence)
            var stolen = _pool[_poolIdx];
            stolen.Stop();
            _poolIdx = (_poolIdx + 1) % _poolSize;
            return stolen;
        }
    }
}
