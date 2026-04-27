// MID_AudioManager.cs
// Generic singleton audio manager.
// Handles music (with crossfade and pitch glide) and SFX (one-shot, pitched).
// All game-specific hooks (FP_SettingsManager, analytics, etc.) removed.
//
// MUSIC ENABLE/DISABLE:
//   Subscribe to OnMusicEnabledChanged or call SetMusicEnabled(bool) directly.
//
// MIXER GROUPS:
//   Assign _musicMixerGroup and _sfxMixerGroup in the inspector.
//   The mixer owns all user-facing volume control via exposed parameters.
//   Per-clip volume scalars are a secondary fine-tune only.

using System.Collections;
using UnityEngine;
using UnityEngine.Audio;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;

namespace MidManStudio.Core.Audio
{
    public class MID_AudioManager : Singleton<MID_AudioManager>
    {
        #region Serialized Fields

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Audio Libraries")]
        [SerializeField] private MID_AudioLibrarySO _musicLibrary;
        [SerializeField] private MID_AudioLibrarySO _sfxLibrary;

        [Header("Sources")]
        [SerializeField] private AudioSource _musicSource;
        [SerializeField] private AudioSource _sfxSource;

        [Header("Mixer Groups")]
        [Tooltip("Music AudioMixerGroup. Exposes 'MusicVol' parameter.")]
        [SerializeField] private AudioMixerGroup _musicMixerGroup;

        [Tooltip("SFX AudioMixerGroup. Exposes 'SFXVol' parameter.")]
        [SerializeField] private AudioMixerGroup _sfxMixerGroup;

        [Header("Volume")]
        [Range(0f, 1f)]
        [SerializeField] private float _masterVolume = 1f;

        [Header("Music Behaviour")]
        [SerializeField] private float  _crossFadeDuration      = 0.5f;
        [SerializeField] private float  _pitchTransitionDuration = 0.4f;
        [SerializeField] private bool   _playMusicOnStart        = false;
        [SerializeField] private string _startMusicId            = "";

        #endregion

        #region Events

        /// <summary>
        /// Subscribe to be notified when music enabled state changes.
        /// Raised by SetMusicEnabled().
        /// </summary>
        public event System.Action<bool> OnMusicEnabledChanged;

        #endregion

        #region Private State

        private bool      _musicEnabled = true;
        private string    _currentMusicId;
        private Coroutine _fadeCoroutine;
        private Coroutine _pitchCoroutine;

        #endregion

        #region Properties

        public float MasterVolume    => _masterVolume;
        public bool  IsMusicPlaying  => _musicSource != null && _musicSource.isPlaying;
        public bool  IsMusicEnabled  => _musicEnabled;
        public string CurrentMusicId => _currentMusicId;

        #endregion

        #region Unity Lifecycle

        protected override void Awake()
        {
            base.Awake();
            EnsureSources();
            _musicLibrary?.BuildLookup();
            _sfxLibrary?.BuildLookup();
        }

        private void Start()
        {
            if (_playMusicOnStart && _musicEnabled &&
                !string.IsNullOrEmpty(_startMusicId))
                PlayMusic(_startMusicId, fade: false);
        }

        #endregion

        #region Public — Music

        /// <summary>Play a music track by ID. Crossfades from current track if fade=true.</summary>
        public void PlayMusic(string id, bool fade = true)
        {
            if (_musicLibrary == null)
            {
                MID_Logger.LogWarning(_logLevel, "No music library assigned.",
                    nameof(MID_AudioManager), nameof(PlayMusic));
                return;
            }

            if (!_musicLibrary.TryGet(id, out var entry))
            {
                MID_Logger.LogWarning(_logLevel, $"Music track '{id}' not found.",
                    nameof(MID_AudioManager), nameof(PlayMusic));
                return;
            }

            bool sameTrack  = id == _currentMusicId;
            _currentMusicId = id;

            if (!_musicEnabled)
            {
                MID_Logger.LogInfo(_logLevel, $"Music disabled — queued: {id}",
                    nameof(MID_AudioManager));
                return;
            }

            if (sameTrack && _musicSource.isPlaying) return;

            if (fade && gameObject.activeInHierarchy)
            {
                if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = StartCoroutine(CrossFade(entry));
            }
            else
            {
                _musicSource.Stop();
                _musicSource.clip   = entry.clip;
                _musicSource.volume = ScaledVolume(entry.volume);
                _musicSource.loop   = true;
                _musicSource.Play();
            }

            MID_Logger.LogInfo(_logLevel, $"Playing music: {id}",
                nameof(MID_AudioManager));
        }

        public void StopMusic(bool fade = true)
        {
            _currentMusicId = null;
            if (fade && gameObject.activeInHierarchy)
            {
                if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = StartCoroutine(FadeOut());
            }
            else
            {
                _musicSource.Stop();
            }
        }

        public void PauseMusic()  => _musicSource.Pause();
        public void ResumeMusic() => _musicSource.UnPause();

        /// <summary>
        /// Enable or disable music playback.
        /// Fires OnMusicEnabledChanged. Crossfades in/out.
        /// </summary>
        public void SetMusicEnabled(bool enabled)
        {
            if (_musicEnabled == enabled) return;
            _musicEnabled = enabled;
            OnMusicEnabledChanged?.Invoke(enabled);

            if (enabled)
            {
                string id = _currentMusicId ?? _startMusicId;
                if (!string.IsNullOrEmpty(id)) PlayMusic(id, fade: true);
            }
            else
            {
                StopMusic(fade: true);
            }
        }

        #endregion

        #region Public — SFX

        public void PlaySFX(string id)
        {
            if (_sfxLibrary == null) return;
            if (!_sfxLibrary.TryGet(id, out var entry))
            {
                MID_Logger.LogWarning(_logLevel, $"SFX '{id}' not found.",
                    nameof(MID_AudioManager));
                return;
            }
            _sfxSource.PlayOneShot(entry.clip, entry.volume * _masterVolume);
        }

        public void PlayClipDirect(AudioClip clip, float volume = 1f)
        {
            if (clip == null) return;
            _sfxSource.PlayOneShot(clip, volume * _masterVolume);
        }

        public void PlaySFXPitched(string id, float pitch)
        {
            if (_sfxLibrary == null) return;
            if (!_sfxLibrary.TryGet(id, out var entry)) return;
            PlayClipDirectPitched(entry.clip, pitch, entry.volume);
        }

        /// <summary>
        /// Plays a one-shot at a custom pitch via a temporary AudioSource.
        /// Routes through the SFX mixer group so SFXVol affects it.
        /// </summary>
        public void PlayClipDirectPitched(AudioClip clip, float pitch, float volume = 1f)
        {
            if (clip == null) return;

            var go  = new GameObject("SFX_Pitched");
            go.transform.SetParent(transform);
            var src = go.AddComponent<AudioSource>();

            if (_sfxMixerGroup != null)
                src.outputAudioMixerGroup = _sfxMixerGroup;

            src.spatialBlend = 0f;
            src.playOnAwake  = false;
            src.loop         = false;
            src.pitch        = Mathf.Clamp(pitch, 0.1f, 3f);
            src.volume       = Mathf.Clamp01(volume * _masterVolume);
            src.clip         = clip;
            src.Play();

            Destroy(go, clip.length / Mathf.Max(0.1f, src.pitch) + 0.1f);
        }

        #endregion

        #region Public — Volume & Pitch

        public void SetMasterVolume(float v)
        {
            _masterVolume = Mathf.Clamp01(v);
            if (_musicSource != null) _musicSource.volume = _masterVolume;
        }

        public void SetMusicPitch(float targetPitch, bool instant = false)
        {
            if (_musicSource == null) return;
            targetPitch = Mathf.Clamp(targetPitch, 0.1f, 3f);

            if (instant || !gameObject.activeInHierarchy)
            {
                if (_pitchCoroutine != null)
                {
                    StopCoroutine(_pitchCoroutine);
                    _pitchCoroutine = null;
                }
                _musicSource.pitch = targetPitch;
                return;
            }

            if (_pitchCoroutine != null) StopCoroutine(_pitchCoroutine);
            _pitchCoroutine = StartCoroutine(GlidePitch(targetPitch));
        }

        #endregion

        #region Private — Coroutines

        private IEnumerator GlidePitch(float target)
        {
            float start   = _musicSource.pitch;
            float elapsed = 0f;
            float dur     = Mathf.Max(_pitchTransitionDuration, 0.01f);
            target        = Mathf.Clamp(target, 0.1f, 3f);

            while (elapsed < dur)
            {
                elapsed           += Time.unscaledDeltaTime;
                _musicSource.pitch = Mathf.Lerp(start, target, elapsed / dur);
                yield return null;
            }

            _musicSource.pitch  = target;
            _pitchCoroutine     = null;
        }

        private IEnumerator FadeOut()
        {
            float startVol = _musicSource.volume;
            for (float t = 0f; t < _crossFadeDuration; t += Time.unscaledDeltaTime)
            {
                _musicSource.volume = Mathf.Lerp(startVol, 0f, t / _crossFadeDuration);
                yield return null;
            }
            _musicSource.Stop();
            _musicSource.volume = startVol;
            _fadeCoroutine      = null;
        }

        private IEnumerator FadeIn(MID_AudioEntry entry)
        {
            if (_musicSource.clip != entry.clip || !_musicSource.isPlaying)
            {
                _musicSource.Stop();
                _musicSource.clip   = entry.clip;
                _musicSource.volume = 0f;
                _musicSource.loop   = true;
                _musicSource.Play();
            }

            float target = ScaledVolume(entry.volume);
            for (float t = 0f; t < _crossFadeDuration; t += Time.unscaledDeltaTime)
            {
                _musicSource.volume = Mathf.Lerp(0f, target, t / _crossFadeDuration);
                yield return null;
            }
            _musicSource.volume = target;
            _fadeCoroutine      = null;
        }

        private IEnumerator CrossFade(MID_AudioEntry next)
        {
            float startVol = _musicSource.volume;
            for (float t = 0f; t < _crossFadeDuration; t += Time.unscaledDeltaTime)
            {
                _musicSource.volume = Mathf.Lerp(startVol, 0f, t / _crossFadeDuration);
                yield return null;
            }

            _musicSource.Stop();
            _musicSource.clip   = next.clip;
            _musicSource.volume = 0f;
            _musicSource.loop   = true;
            _musicSource.Play();

            float targetVol = ScaledVolume(next.volume);
            for (float t = 0f; t < _crossFadeDuration; t += Time.unscaledDeltaTime)
            {
                _musicSource.volume = Mathf.Lerp(0f, targetVol, t / _crossFadeDuration);
                yield return null;
            }
            _musicSource.volume = targetVol;
            _fadeCoroutine      = null;
        }

        #endregion

        #region Private — Setup

        private void EnsureSources()
        {
            if (_musicSource == null)
            {
                var go = new GameObject("MusicSource");
                go.transform.SetParent(transform);
                _musicSource = go.AddComponent<AudioSource>();
            }
            _musicSource.loop        = true;
            _musicSource.playOnAwake = false;
            _musicSource.spatialBlend = 0f;
            if (_musicMixerGroup != null)
                _musicSource.outputAudioMixerGroup = _musicMixerGroup;

            if (_sfxSource == null)
            {
                var go = new GameObject("SFXSource");
                go.transform.SetParent(transform);
                _sfxSource = go.AddComponent<AudioSource>();
            }
            _sfxSource.loop        = false;
            _sfxSource.playOnAwake = false;
            _sfxSource.spatialBlend = 0f;
            if (_sfxMixerGroup != null)
                _sfxSource.outputAudioMixerGroup = _sfxMixerGroup;
        }

        private float ScaledVolume(float clipVol) =>
            Mathf.Clamp01(clipVol * _masterVolume);

        #endregion
    }
}
