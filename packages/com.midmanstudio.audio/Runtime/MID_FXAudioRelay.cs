// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio.md, section "MID_FXAudioRelay.cs"
// ============================================================================
using UnityEngine;
using MidManStudio.Core.FX;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Audio
{
    /// <summary>
    /// Plays a MID_NativeAudioBridge clip for each effect that GlobalFXManager emits. GlobalFXManager only
    /// raises <see cref="GlobalFXManager.CategoryAudioRequested"/>; this component maps the category to a
    /// clip index and plays it. Put it on any active GameObject, for example the Managers prefab.
    /// A clip index of -1 skips audio for that category.
    /// </summary>
    [DisallowMultipleComponent]
    public class MID_FXAudioRelay : MonoBehaviour
    {
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Tooltip("Bridge that plays the clips. Left empty, the scene's MID_NativeAudioBridge instance is used.")]
        [SerializeField] private MID_NativeAudioBridge _audio;

        [Header("Clip index in the bridge, per category (-1 = no audio)")]
        [SerializeField] private int _impactClipIndex    = 0;
        [SerializeField] private int _muzzleClipIndex    = 1;
        [SerializeField] private int _shellClipIndex     = 2;
        [SerializeField] private int _explosionClipIndex = -1;

        private void OnEnable()  => GlobalFXManager.CategoryAudioRequested += OnCategoryAudioRequested;
        private void OnDisable() => GlobalFXManager.CategoryAudioRequested -= OnCategoryAudioRequested;

        private void OnCategoryAudioRequested(EffectCategory category, float volume)
        {
            int clipIndex = ClipIndexFor(category);
            if (clipIndex < 0) return;

            if (_audio == null && MID_NativeAudioBridge.HasInstance)
                _audio = MID_NativeAudioBridge.Instance;

            if (_audio == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "No MID_NativeAudioBridge in the scene, effect audio skipped.",
                    nameof(MID_FXAudioRelay));
                return;
            }

            _audio.PlayClip(clipIndex, volume);
        }

        private int ClipIndexFor(EffectCategory category)
        {
            switch (category)
            {
                case EffectCategory.Impact:        return _impactClipIndex;
                case EffectCategory.MuzzleFlash:   return _muzzleClipIndex;
                case EffectCategory.ShellEjection: return _shellClipIndex;
                case EffectCategory.Explosion:     return _explosionClipIndex;
                default:                           return -1;
            }
        }
    }
}
