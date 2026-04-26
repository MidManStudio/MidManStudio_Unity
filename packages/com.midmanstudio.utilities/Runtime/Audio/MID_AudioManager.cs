using System.Collections;
using UnityEngine;
using UnityEngine.Audio;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;
using ForeignProtocol.InGame.Managers;

namespace MidManStudio.Core.Audio
{
    public class MID_AudioManager : Singleton<MID_AudioManager>
    {
        #region Serialized Fields

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Libraries")]
        [SerializeField] private MID_AudioLibrarySO _musicLibrary;
        [SerializeField] private MID_AudioLibrarySO _sfxLibrary;

        [Header("Sources")]
        [SerializeField] private AudioSource _musicSource;
        [SerializeField] private AudioSource _sfxSource;

        [Header("Mixer Groups")]
        [Tooltip("Drag the 'Music' AudioMixerGroup here. All music sources are routed\n" +
                 "through this group so the MusicVol exposed param controls their volume.")]
        [SerializeField] private AudioMixerGroup _musicMixerGroup;

        [Tooltip("Drag the 'SFX' AudioMixerGroup here. All SFX sources — including\n" +
                 "dynamically spawned one-shots — are routed through this group so the\n" +
                 "SFXVol exposed param controls their volume.")]
        [SerializeField] private AudioMixerGroup _sfxMixerGroup;

        [Header("Initial Volume")]
        [Range(0f, 1f)][SerializeField] private float _masterVolume = 1f;

        [Header("Music")]
        [SerializeField] private float _crossFadeDuration = 0.5f;
        [SerializeField] private bool _playMusicOnStart = false;
        [SerializeField] private string _startMusicId = "gameplay";

        [Header("Pitch Transition")]
        [SerializeField] private float _pitchTransitionDuration = 0.4f;

        #endregion

        #region Private Fields

        private Coroutine _fadeCoroutine;
        private Coroutine _pitchCoroutine;
        private string _currentMusicId;

        #endregion

        #region Properties

        public float MasterVolume => _masterVolume;
        public bool IsMusicPlaying => _musicSource != null && _musicSource.isPlaying;

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
            if (FP_SettingsManager.HasInstance)
                FP_SettingsManager.Instance.OnMusicChanged += OnMusicEnabledChanged;

            // Source volume is fixed at 1 — the mixer groups own all gain control.
            _musicSource.volume = _masterVolume;

            if (_playMusicOnStart && MusicIsEnabled())
            {
                string id = ResolveStartMusicId();
                if (!string.IsNullOrEmpty(id))
                    PlayMusic(id, fade: false);
            }
        }

        private void OnDestroy()
        {
            if (FP_SettingsManager.HasInstance)
                FP_SettingsManager.Instance.OnMusicChanged -= OnMusicEnabledChanged;
        }

        #endregion

        #region Public — Music

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
                MID_Logger.LogWarning(_logLevel, $"Music '{id}' not found.",
                    nameof(MID_AudioManager), nameof(PlayMusic));
                return;
            }

            bool sameTrack = id == _currentMusicId;
            _currentMusicId = id;

            if (!MusicIsEnabled())
            {
                MID_Logger.LogInfo(_logLevel, $"Music disabled — queued: {id}",
                    nameof(MID_AudioManager), nameof(PlayMusic));
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
                _musicSource.clip = entry.clip;
                _musicSource.volume = NeutralVolume(entry.volume);
                _musicSource.loop = true;
                _musicSource.Play();
            }

            MID_Logger.LogInfo(_logLevel, $"Playing music: {id}",
                nameof(MID_AudioManager), nameof(PlayMusic));
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

        public void PauseMusic() => _musicSource.Pause();
        public void ResumeMusic() => _musicSource.UnPause();

        #endregion

        #region Public — SFX

        public void PlaySFX(string id)
        {
            if (_sfxLibrary == null)
            {
                MID_Logger.LogWarning(_logLevel, "No SFX library assigned.",
                    nameof(MID_AudioManager), nameof(PlaySFX));
                return;
            }

            if (!_sfxLibrary.TryGet(id, out var entry))
            {
                MID_Logger.LogWarning(_logLevel, $"SFX '{id}' not found.",
                    nameof(MID_AudioManager), nameof(PlaySFX));
                return;
            }

            // Per-clip volume only — SFXVol mixer param handles the user-facing gain.
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
            if (!_sfxLibrary.TryGet(id, out var entry))
            {
                MID_Logger.LogWarning(_logLevel, $"SFX '{id}' not found.",
                    nameof(MID_AudioManager), nameof(PlaySFXPitched));
                return;
            }
            PlayClipDirectPitched(entry.clip, pitch, entry.volume);
        }

        /// <summary>
        /// Spawns a temporary AudioSource for pitched one-shots and routes it through
        /// the SFX mixer group so the SFXVol param affects it like every other SFX.
        /// </summary>
        public void PlayClipDirectPitched(AudioClip clip, float pitch, float volume = 1f)
        {
            if (clip == null) return;

            var go = new GameObject("SFX_Pitched_Temp");
            go.transform.SetParent(transform);
            var src = go.AddComponent<AudioSource>();

            // Route through the SFX mixer group — this is what makes SFXVol affect it.
            if (_sfxMixerGroup != null)
                src.outputAudioMixerGroup = _sfxMixerGroup;

            src.spatialBlend = 0f;
            src.playOnAwake = false;
            src.loop = false;
            src.pitch = Mathf.Clamp(pitch, 0.1f, 3f);
            // Mixer controls the SFX gain; only apply per-clip and master scalar here.
            src.volume = volume * _masterVolume;
            src.clip = clip;
            src.Play();

            Destroy(go, clip.length / Mathf.Max(0.1f, src.pitch) + 0.1f);
        }

        #endregion

        #region Public — Master Volume

        public void SetMasterVolume(float v)
        {
            _masterVolume = Mathf.Clamp01(v);
            if (_musicSource != null) _musicSource.volume = _masterVolume;
        }

        // Kept for API compatibility — volume is now owned by the mixer.
        public void SetMusicVolume(float v) { }
        public void SetSFXVolume(float v) { }

        #endregion

        #region Public — Pitch

        public void SetMusicPitch(float targetPitch, bool instant = false)
        {
            if (_musicSource == null) return;

            if (instant || !gameObject.activeInHierarchy)
            {
                if (_pitchCoroutine != null) { StopCoroutine(_pitchCoroutine); _pitchCoroutine = null; }
                _musicSource.pitch = Mathf.Clamp(targetPitch, 0.1f, 3f);
                return;
            }

            if (_pitchCoroutine != null) StopCoroutine(_pitchCoroutine);
            _pitchCoroutine = StartCoroutine(GlidePitch(targetPitch));
        }

        #endregion

        #region Private — Music Enable Toggle

        private bool MusicIsEnabled() =>
            !FP_SettingsManager.HasInstance || FP_SettingsManager.Instance.MusicEnabled;

        private void OnMusicEnabledChanged(bool enabled)
        {
            if (enabled)
            {
                string id = string.IsNullOrEmpty(_currentMusicId)
                    ? ResolveStartMusicId()
                    : _currentMusicId;

                if (string.IsNullOrEmpty(id)) return;
                if (!_musicLibrary.TryGet(id, out var entry)) return;

                if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = StartCoroutine(FadeIn(entry));

                MID_Logger.LogInfo(_logLevel, $"Music enabled — fading in: {id}",
                    nameof(MID_AudioManager), nameof(OnMusicEnabledChanged));
            }
            else
            {
                if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = StartCoroutine(FadeOut());

                MID_Logger.LogInfo(_logLevel, "Music disabled — fading out.",
                    nameof(MID_AudioManager), nameof(OnMusicEnabledChanged));
            }
        }

        #endregion

        #region Private

        private string ResolveStartMusicId()
        {
            if (FP_SettingsManager.HasInstance)
            {
                var sm = FP_SettingsManager.Instance;
                if (sm.GameplayMusicOptions != null && sm.GameplayMusicOptions.Count > 0)
                {
                    string selected = sm.SelectedGameplayMusicId;
                    if (!string.IsNullOrEmpty(selected)) return selected;
                }
            }
            return _startMusicId;
        }

        private IEnumerator GlidePitch(float target)
        {
            float start = _musicSource.pitch;
            float elapsed = 0f;
            float safeDur = Mathf.Max(_pitchTransitionDuration, 0.01f);
            target = Mathf.Clamp(target, 0.1f, 3f);

            while (elapsed < safeDur)
            {
                elapsed += Time.unscaledDeltaTime;
                _musicSource.pitch = Mathf.Lerp(start, target, elapsed / safeDur);
                yield return null;
            }
            _musicSource.pitch = target;
            _pitchCoroutine = null;
        }

        private void EnsureSources()
        {
            if (_musicSource == null)
            {
                var go = new GameObject("MusicSource");
                go.transform.SetParent(transform);
                _musicSource = go.AddComponent<AudioSource>();
            }
            _musicSource.loop = true;
            _musicSource.playOnAwake = false;
            _musicSource.spatialBlend = 0f;
            // Route through the Music mixer group so MusicVol param takes effect.
            if (_musicMixerGroup != null)
                _musicSource.outputAudioMixerGroup = _musicMixerGroup;

            if (_sfxSource == null)
            {
                var go = new GameObject("SFXSource");
                go.transform.SetParent(transform);
                _sfxSource = go.AddComponent<AudioSource>();
            }
            _sfxSource.loop = false;
            _sfxSource.playOnAwake = false;
            _sfxSource.spatialBlend = 0f;
            _sfxSource.clip = null;
            // Route through the SFX mixer group so SFXVol param takes effect.
            if (_sfxMixerGroup != null)
                _sfxSource.outputAudioMixerGroup = _sfxMixerGroup;
        }

        /// <summary>
        /// Per-clip volume scalar — masterVolume only.
        /// The mixer group owns the user-facing gain; _musicVolume is not needed here.
        /// </summary>
        private float NeutralVolume(float clipVol) => clipVol * _masterVolume;

        private IEnumerator FadeOut()
        {
            float startVol = _musicSource.volume;
            for (float t = 0; t < _crossFadeDuration; t += Time.unscaledDeltaTime)
            {
                _musicSource.volume = Mathf.Lerp(startVol, 0f, t / _crossFadeDuration);
                yield return null;
            }
            _musicSource.Stop();
            _musicSource.volume = startVol;
            _fadeCoroutine = null;
        }

        private IEnumerator FadeIn(MID_AudioEntry entry)
        {
            if (_musicSource.clip != entry.clip || !_musicSource.isPlaying)
            {
                _musicSource.Stop();
                _musicSource.clip = entry.clip;
                _musicSource.volume = 0f;
                _musicSource.loop = true;
                _musicSource.Play();
            }

            float targetVol = NeutralVolume(entry.volume);
            for (float t = 0; t < _crossFadeDuration; t += Time.unscaledDeltaTime)
            {
                _musicSource.volume = Mathf.Lerp(0f, targetVol, t / _crossFadeDuration);
                yield return null;
            }
            _musicSource.volume = targetVol;
            _fadeCoroutine = null;
        }

        private IEnumerator CrossFade(MID_AudioEntry next)
        {
            float startVol = _musicSource.volume;
            for (float t = 0; t < _crossFadeDuration; t += Time.unscaledDeltaTime)
            {
                _musicSource.volume = Mathf.Lerp(startVol, 0f, t / _crossFadeDuration);
                yield return null;
            }

            _musicSource.Stop();
            _musicSource.clip = next.clip;
            _musicSource.volume = 0f;
            _musicSource.loop = true;
            _musicSource.Play();

            float targetVol = NeutralVolume(next.volume);
            for (float t = 0; t < _crossFadeDuration; t += Time.unscaledDeltaTime)
            {
                _musicSource.volume = Mathf.Lerp(0f, targetVol, t / _crossFadeDuration);
                yield return null;
            }
            _musicSource.volume = targetVol;
            _fadeCoroutine = null;
        }

        #endregion
    }
}