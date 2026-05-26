// MID_AudioLimiter.cs
//
// Applies the Rust peak limiter to the final mixed audio output.
//
// SETUP — CRITICAL:
//   This component MUST be on the AudioListener GameObject.
//   OnAudioFilterRead on the AudioListener receives the FINAL MIXED output
//   from ALL AudioSources in the scene. On any other GameObject, it only
//   processes that GameObject's own AudioSource output.
//
//   Typical setup:
//     Main Camera (has AudioListener) → also add MID_AudioLimiter here.
//     OR: a dedicated AudioListener GameObject → add MID_AudioLimiter there.
//
// WEBGL:
//   Rust DLL unavailable. Falls back to a simple C# peak limiter with the
//   same attack/release behaviour. Identical Inspector interface.
//
// PLATFORM DISPATCH:
//   Desktop/Mobile: Rust DLL via DllImport — no managed allocation in DSP path.
//   WebGL: Pure C# limiter — slightly higher GC risk but functionally identical.

using System.Runtime.InteropServices;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Audio
{
    [RequireComponent(typeof(AudioListener))]
    public class MID_AudioLimiter : MonoBehaviour
    {
        // ── DllImport — desktop + mobile only ────────────────────────────────
#if !UNITY_WEBGL || UNITY_EDITOR

#if UNITY_IOS && !UNITY_EDITOR
        private const string LIB = "__Internal";
#else
        private const string LIB = "mid_audio";
#endif

        [DllImport(LIB)] private static extern void  process_buffer(float[] buf, int length);
        [DllImport(LIB)] private static extern void  set_limiter_params(float threshold, float attack, float release);
        [DllImport(LIB)] private static extern void  set_limiter_enabled(byte enabled);
        [DllImport(LIB)] private static extern void  reset_limiter();
        [DllImport(LIB)] private static extern float get_limiter_gain();

#endif

        // ── Inspector ─────────────────────────────────────────────────────────

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Limiter Parameters")]
        [Tooltip("Peak level that triggers gain reduction. 0.95 = -0.45 dBFS (default).")]
        [SerializeField] [Range(0.1f, 1.0f)]  private float _threshold = 0.95f;

        [Tooltip("Gain multiplier per audio buffer when limiting.\n" +
                 "0.85 = fast (default). 0.99 = slow/transparent.")]
        [SerializeField] [Range(0.01f, 0.999f)] private float _attack   = 0.85f;

        [Tooltip("Gain recovery increment per audio buffer.\n" +
                 "0.002 = default. Lower = slower recovery (less pumping).")]
        [SerializeField] [Range(0.0001f, 0.05f)] private float _release = 0.002f;

        [Tooltip("Bypass limiter entirely. Audio passes through unmodified.")]
        [SerializeField] private bool _enabled = true;

        // ── Live diagnostics (read-only in Inspector) ─────────────────────────

        [Header("Live  (read-only in Play Mode)")]
        [SerializeField] [Range(0f, 1f)] private float _currentGain = 1f;

        // ── WebGL C# fallback state ───────────────────────────────────────────
#if UNITY_WEBGL && !UNITY_EDITOR
        private float _csGain    = 1.0f;
        private float _csRelease; // cached from _release each OnValidate/Awake
        private float _csAttack;
        private float _csThreshold;
#endif

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            ApplyParams();
            MID_Logger.LogInfo(_logLevel,
                "AudioLimiter ready on AudioListener. " +
#if UNITY_WEBGL && !UNITY_EDITOR
                "WebGL: C# fallback limiter active.",
#else
                "Rust DSP limiter active.",
#endif
                nameof(MID_AudioLimiter));
        }

        private void OnValidate() => ApplyParams();

        private void Update()
        {
#if UNITY_EDITOR
            // Poll gain for inspector display
#if !UNITY_WEBGL
            if (_enabled) _currentGain = get_limiter_gain();
            else          _currentGain = 1f;
#else
            _currentGain = _csGain;
#endif
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            if (Application.isPlaying) reset_limiter();
#endif
        }

        // ── Audio DSP thread ──────────────────────────────────────────────────

        private void OnAudioFilterRead(float[] data, int channels)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // C# fallback — same algorithm as Rust but in managed code
            if (!_enabled) return;
            float peak = 0f;
            float g = _csGain;
            for (int i = 0; i < data.Length; i++)
            {
                float v = data[i] * g;
                float a = v < 0 ? -v : v;
                if (a > peak) peak = a;
                data[i] = v;
            }
            _csGain = peak > _csThreshold
                ? System.Math.Max(g * _csAttack,   0.05f)
                : System.Math.Min(g + _csRelease,  1.0f);
#else
            // Rust DSP — no managed allocation on this path
            process_buffer(data, data.Length);
#endif
        }

        // ── Private ───────────────────────────────────────────────────────────

        private void ApplyParams()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            if (!Application.isPlaying) return;
            set_limiter_params(_threshold, _attack, _release);
            set_limiter_enabled(_enabled ? (byte)1 : (byte)0);
#else
            _csThreshold = _threshold;
            _csAttack    = _attack;
            _csRelease   = _release;
#endif
        }
    }
}
