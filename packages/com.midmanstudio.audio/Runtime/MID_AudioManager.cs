// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio.md, section "MID_AudioManager.cs"
// ============================================================================
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;

namespace MidManStudio.Core.Audio
{
    /// <summary>
    /// Generic singleton audio manager.
    /// Handles music (with crossfade and pitch glide) and SFX (one-shot, pitched).
    ///
    /// TWO WAYS TO PLAY:
    ///   By string id from the two MID_AudioLibrarySO libraries (PlayMusic("id"), PlaySFX("id")).
    ///   By generated enum from one or more MID_AudioCatalogSO catalogs (PlayMusic(AudioId.X),
    ///   PlaySFX(AudioId.X)). The enum overloads accept any unmanaged enum, so the manager works
    ///   no matter where the AudioId and AudioCategory enums are generated.
    ///
    /// CATEGORIES:
    ///   A category in a catalog carries a mixer group and a volume scale. At runtime a category
    ///   can also be scaled and muted. Final volume = clip x category x runtime scale x master.
    ///
    /// MUSIC ENABLE/DISABLE:
    ///   Subscribe to OnMusicEnabledChanged or call SetMusicEnabled(bool) directly.
    ///
    /// MIXER GROUPS:
    ///   Assign _musicMixerGroup and _sfxMixerGroup in the inspector.
    ///   The mixer owns all user-facing volume control via exposed parameters.
    ///   Per-clip volume scalars are a secondary fine-tune only.
    /// </summary>
    public class MID_AudioManager : Singleton<MID_AudioManager>
    {
        #region Constants

        private const int NoKey = -1;           // no enum music track is current
        private const int LegacyCategory = -1;  // tracks played by string id belong to no category

        #endregion

        #region Serialized Fields

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Audio Libraries")]
        [SerializeField] private MID_AudioLibrarySO _musicLibrary;
        [SerializeField] private MID_AudioLibrarySO _sfxLibrary;

        [Header("Audio Catalogs")]
        [Tooltip("Catalogs that map generated AudioId and AudioCategory values to clips. " +
                 "They are merged in order. The first catalog that defines an id or a category wins.")]
        [SerializeField] private List<MID_AudioCatalogSO> _catalogs = new List<MID_AudioCatalogSO>();

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

        [Tooltip("Start music by audio id. Used instead of the string id when it is not None (0).")]
        [MID_AudioId]
        [SerializeField] private int _startMusicAudioId = 0;

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
        private int       _currentMusicKey = NoKey;
        private int       _currentMusicCategory = LegacyCategory;
        private Coroutine _fadeCoroutine;
        private float     _currentMusicVolume = 1f;   // per-clip volume of the track now playing
        private Coroutine _pitchCoroutine;

        private bool _catalogBuilt;
        private readonly Dictionary<int, MID_AudioClipEntry> _idLookup =
            new Dictionary<int, MID_AudioClipEntry>();
        private readonly Dictionary<int, MID_AudioCategorySettings> _categoryLookup =
            new Dictionary<int, MID_AudioCategorySettings>();
        private readonly Dictionary<int, List<MID_AudioClipEntry>> _categoryClips =
            new Dictionary<int, List<MID_AudioClipEntry>>();
        private readonly Dictionary<int, float> _categoryVolume = new Dictionary<int, float>();
        private readonly HashSet<int> _mutedCategories = new HashSet<int>();
        private readonly Dictionary<int, int> _lastRandomIndex = new Dictionary<int, int>();
        private readonly Dictionary<AudioMixerGroup, AudioSource> _groupSources =
            new Dictionary<AudioMixerGroup, AudioSource>();

        #endregion

        #region Properties

        public float MasterVolume    => _masterVolume;
        public bool  IsMusicPlaying  => _musicSource != null && _musicSource.isPlaying;
        public bool  IsMusicEnabled  => _musicEnabled;
        public string CurrentMusicId => _currentMusicId;

        /// <summary>Numeric audio id of the music track played by enum or id, or -1 when none is.</summary>
        public int CurrentMusicAudioId => _currentMusicKey;

        #endregion

        #region Unity Lifecycle

        protected override void Awake()
        {
            base.Awake();
            EnsureSources();
            _musicLibrary?.BuildLookup();
            _sfxLibrary?.BuildLookup();
            RebuildCatalogLookup();
        }

        private void Start()
        {
            if (!_playMusicOnStart || !_musicEnabled) return;

            if (_startMusicAudioId != 0)
                PlayMusicById(_startMusicAudioId, fade: false);
            else if (!string.IsNullOrEmpty(_startMusicId))
                PlayMusic(_startMusicId, fade: false);
        }

        private void OnDisable()
        {
            _fadeCoroutine = null;   // deactivating the GameObject stops its coroutines
        }

        #endregion

        #region Public — Catalogs

        /// <summary>Adds a catalog at runtime, for example one that ships with a package.</summary>
        public void AddCatalog(MID_AudioCatalogSO catalog)
        {
            if (catalog == null || _catalogs.Contains(catalog)) return;
            _catalogs.Add(catalog);
            RebuildCatalogLookup();
        }

        /// <summary>Removes a catalog added in the inspector or by AddCatalog.</summary>
        public void RemoveCatalog(MID_AudioCatalogSO catalog)
        {
            if (_catalogs.Remove(catalog)) RebuildCatalogLookup();
        }

        /// <summary>
        /// Rebuilds the id and category lookups from the assigned catalogs. Called automatically
        /// by Awake, AddCatalog and RemoveCatalog. Call it after editing a catalog at runtime.
        /// </summary>
        public void RebuildCatalogLookup()
        {
            _idLookup.Clear();
            _categoryLookup.Clear();
            _categoryClips.Clear();
            _lastRandomIndex.Clear();

            foreach (MID_AudioCatalogSO catalog in _catalogs)
            {
                if (catalog == null) continue;

                foreach (MID_AudioCategorySettings c in catalog.Categories)
                {
                    if (c == null) continue;
                    if (_categoryLookup.ContainsKey(c.category))
                    {
                        MID_Logger.LogWarning(_logLevel,
                            $"Category {c.category} has settings in more than one catalog. Using the first ('{catalog.name}' is ignored for it).",
                            nameof(MID_AudioManager), nameof(RebuildCatalogLookup));
                        continue;
                    }
                    _categoryLookup[c.category] = c;
                }

                foreach (MID_AudioClipEntry e in catalog.Clips)
                {
                    if (e == null || e.clip == null) continue;
                    if (_idLookup.ContainsKey(e.id))
                    {
                        MID_Logger.LogWarning(_logLevel,
                            $"Audio id {e.id} is defined in more than one catalog. Using the first ('{catalog.name}' is ignored for it).",
                            nameof(MID_AudioManager), nameof(RebuildCatalogLookup));
                        continue;
                    }
                    _idLookup[e.id] = e;

                    List<MID_AudioClipEntry> list;
                    if (!_categoryClips.TryGetValue(e.category, out list))
                    {
                        list = new List<MID_AudioClipEntry>();
                        _categoryClips[e.category] = list;
                    }
                    list.Add(e);
                }
            }
            _catalogBuilt = true;
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

            StartMusic(id, NoKey, entry.clip, entry.volume, LegacyCategory, _musicMixerGroup, fade);
        }

        /// <summary>
        /// Play a music track by generated audio id, for example <c>PlayMusic(AudioId.TavernTheme)</c>.
        /// The clip's category decides the mixer group and the volume scale.
        /// </summary>
        public void PlayMusic<T>(T id, bool fade = true) where T : unmanaged, System.Enum
        {
            WarnIfWrongKind<T>(MID_AudioEnumKind.Id, nameof(PlayMusic));
            PlayMusicById(MID_EnumKey<T>.ToInt(id), fade);
        }

        /// <summary>Play a music track by the numeric value of its audio id.</summary>
        public void PlayMusicById(int id, bool fade = true)
        {
            MID_AudioClipEntry entry;
            if (!TryGetEntry(id, nameof(PlayMusicById), out entry)) return;
            StartMusic(null, id, entry.clip, entry.volume, entry.category, MusicGroupFor(entry.category), fade);
        }

        public void StopMusic(bool fade = true)
        {
            _currentMusicId  = null;
            _currentMusicKey = NoKey;
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
                if (_currentMusicKey != NoKey)
                {
                    PlayMusicById(_currentMusicKey, fade: true);
                    return;
                }

                string id = _currentMusicId ?? _startMusicId;
                if (!string.IsNullOrEmpty(id)) PlayMusic(id, fade: true);
                else if (_startMusicAudioId != 0) PlayMusicById(_startMusicAudioId, fade: true);
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

        /// <summary>Play a sound effect by generated audio id, for example <c>PlaySFX(AudioId.SwordHit)</c>.</summary>
        public void PlaySFX<T>(T id) where T : unmanaged, System.Enum
        {
            WarnIfWrongKind<T>(MID_AudioEnumKind.Id, nameof(PlaySFX));
            PlaySFXById(MID_EnumKey<T>.ToInt(id));
        }

        /// <summary>
        /// Play a sound effect by the numeric value of its audio id. It plays through the mixer
        /// group of the clip's category, and is skipped when that category is muted.
        /// </summary>
        public void PlaySFXById(int id)
        {
            MID_AudioClipEntry entry;
            if (!TryGetEntry(id, nameof(PlaySFXById), out entry)) return;

            float gain = CategoryGain(entry.category);
            if (gain <= 0f) return;
            GetSfxSource(CategoryGroup(entry.category))
                .PlayOneShot(entry.clip, entry.volume * gain * _masterVolume);
        }

        /// <summary>
        /// Play a random clip from a category, for example <c>PlayRandomSFX(AudioCategory.Sfx)</c>.
        /// The same clip is not picked twice in a row when the category has more than one.
        /// </summary>
        public void PlayRandomSFX<TCategory>(TCategory category) where TCategory : unmanaged, System.Enum
        {
            WarnIfWrongKind<TCategory>(MID_AudioEnumKind.Category, nameof(PlayRandomSFX));
            PlayRandomSFXInCategoryById(MID_EnumKey<TCategory>.ToInt(category));
        }

        /// <summary>Play a random clip from the category with the given numeric value.</summary>
        public void PlayRandomSFXInCategoryById(int category)
        {
            EnsureCatalog();
            List<MID_AudioClipEntry> list;
            if (!_categoryClips.TryGetValue(category, out list) || list.Count == 0)
            {
                MID_Logger.LogWarning(_logLevel, $"No clips in audio category {category}.",
                    nameof(MID_AudioManager), nameof(PlayRandomSFXInCategoryById));
                return;
            }

            float gain = CategoryGain(category);
            if (gain <= 0f) return;

            MID_AudioClipEntry entry = list[PickIndex(category, list.Count)];
            GetSfxSource(CategoryGroup(category))
                .PlayOneShot(entry.clip, entry.volume * gain * _masterVolume);
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

        /// <summary>Play a sound effect by generated audio id at a custom pitch.</summary>
        public void PlaySFXPitched<T>(T id, float pitch) where T : unmanaged, System.Enum
        {
            WarnIfWrongKind<T>(MID_AudioEnumKind.Id, nameof(PlaySFXPitched));
            PlaySFXPitchedById(MID_EnumKey<T>.ToInt(id), pitch);
        }

        /// <summary>Play a sound effect by the numeric value of its audio id at a custom pitch.</summary>
        public void PlaySFXPitchedById(int id, float pitch)
        {
            MID_AudioClipEntry entry;
            if (!TryGetEntry(id, nameof(PlaySFXPitchedById), out entry)) return;

            float gain = CategoryGain(entry.category);
            if (gain <= 0f) return;

            AudioMixerGroup group = CategoryGroup(entry.category);
            if (group == null) group = _sfxMixerGroup;
            PlayPitched(entry.clip, pitch, entry.volume * gain, group);
        }

        /// <summary>
        /// Plays a one-shot at a custom pitch via a temporary AudioSource.
        /// Routes through the SFX mixer group so SFXVol affects it.
        /// </summary>
        public void PlayClipDirectPitched(AudioClip clip, float pitch, float volume = 1f)
        {
            PlayPitched(clip, pitch, volume, _sfxMixerGroup);
        }

        /// <summary>Looks up the clip for a generated audio id without playing it.</summary>
        public bool TryGetClip<T>(T id, out AudioClip clip) where T : unmanaged, System.Enum
        {
            WarnIfWrongKind<T>(MID_AudioEnumKind.Id, nameof(TryGetClip));
            return TryGetClipById(MID_EnumKey<T>.ToInt(id), out clip);
        }

        /// <summary>Looks up the clip for a numeric audio id without playing it.</summary>
        public bool TryGetClipById(int id, out AudioClip clip)
        {
            EnsureCatalog();
            MID_AudioClipEntry entry;
            if (_idLookup.TryGetValue(id, out entry))
            {
                clip = entry.clip;
                return true;
            }
            clip = null;
            return false;
        }

        #endregion

        #region Public — Categories

        /// <summary>
        /// Sets the runtime volume scale (0 to 1) of a category. It multiplies the volume set in the
        /// catalog and applies to the music that is playing in that category right away.
        /// </summary>
        public void SetCategoryVolume<T>(T category, float volume) where T : unmanaged, System.Enum
        {
            WarnIfWrongKind<T>(MID_AudioEnumKind.Category, nameof(SetCategoryVolume));
            SetCategoryVolumeById(MID_EnumKey<T>.ToInt(category), volume);
        }

        /// <summary>Sets the runtime volume scale of the category with the given numeric value.</summary>
        public void SetCategoryVolumeById(int category, float volume)
        {
            _categoryVolume[category] = Mathf.Clamp01(volume);
            RefreshMusicVolume();
        }

        /// <summary>The runtime volume scale of a category. It is 1 until SetCategoryVolume is called.</summary>
        public float GetCategoryVolume<T>(T category) where T : unmanaged, System.Enum
        {
            WarnIfWrongKind<T>(MID_AudioEnumKind.Category, nameof(GetCategoryVolume));
            return GetCategoryVolumeById(MID_EnumKey<T>.ToInt(category));
        }

        /// <summary>The runtime volume scale of the category with the given numeric value.</summary>
        public float GetCategoryVolumeById(int category)
        {
            float v;
            return _categoryVolume.TryGetValue(category, out v) ? v : 1f;
        }

        /// <summary>Mutes or unmutes a category. Muted sound effects are skipped and muted music is silent.</summary>
        public void SetCategoryMuted<T>(T category, bool muted) where T : unmanaged, System.Enum
        {
            WarnIfWrongKind<T>(MID_AudioEnumKind.Category, nameof(SetCategoryMuted));
            SetCategoryMutedById(MID_EnumKey<T>.ToInt(category), muted);
        }

        /// <summary>Mutes or unmutes the category with the given numeric value.</summary>
        public void SetCategoryMutedById(int category, bool muted)
        {
            if (muted) _mutedCategories.Add(category);
            else _mutedCategories.Remove(category);
            RefreshMusicVolume();
        }

        /// <summary>True when the category is muted.</summary>
        public bool IsCategoryMuted<T>(T category) where T : unmanaged, System.Enum
        {
            WarnIfWrongKind<T>(MID_AudioEnumKind.Category, nameof(IsCategoryMuted));
            return IsCategoryMutedById(MID_EnumKey<T>.ToInt(category));
        }

        /// <summary>True when the category with the given numeric value is muted.</summary>
        public bool IsCategoryMutedById(int category)
        {
            return _mutedCategories.Contains(category);
        }

        #endregion

        #region Public — Volume & Pitch

        public void SetMasterVolume(float v)
        {
            _masterVolume = Mathf.Clamp01(v);
            RefreshMusicVolume();
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

        #region Private — Music

        private void StartMusic(string stringId, int key, AudioClip clip, float clipVolume,
            int category, AudioMixerGroup group, bool fade)
        {
            bool sameTrack = stringId != null
                ? stringId == _currentMusicId
                : _currentMusicId == null && key == _currentMusicKey;
            _currentMusicId  = stringId;
            _currentMusicKey = key;

            string label = stringId != null ? stringId : "audio id " + key;

            if (!_musicEnabled)
            {
                MID_Logger.LogInfo(_logLevel, $"Music disabled — queued: {label}",
                    nameof(MID_AudioManager));
                return;
            }

            if (sameTrack && _musicSource.isPlaying) return;

            if (fade && gameObject.activeInHierarchy)
            {
                if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = StartCoroutine(CrossFade(clip, clipVolume, category, group));
            }
            else
            {
                _musicSource.Stop();
                ApplyMusicGroup(group);
                _musicSource.clip   = clip;
                _currentMusicVolume = clipVolume;
                _currentMusicCategory = category;
                _musicSource.volume = MusicTargetVolume();
                _musicSource.loop   = true;
                _musicSource.Play();
            }

            MID_Logger.LogInfo(_logLevel, $"Playing music: {label}",
                nameof(MID_AudioManager));
        }

        private void ApplyMusicGroup(AudioMixerGroup group)
        {
            if (_musicSource.outputAudioMixerGroup != group)
                _musicSource.outputAudioMixerGroup = group;
        }

        private AudioMixerGroup MusicGroupFor(int category)
        {
            AudioMixerGroup group = CategoryGroup(category);
            return group != null ? group : _musicMixerGroup;
        }

        // Volume the music source should have when no fade is running.
        private float MusicTargetVolume()
        {
            return ScaledVolume(_currentMusicVolume * CategoryGain(_currentMusicCategory));
        }

        private void RefreshMusicVolume()
        {
            if (_musicSource != null && _fadeCoroutine == null)
                _musicSource.volume = MusicTargetVolume();
        }

        #endregion

        #region Private — Catalog lookups

        private void EnsureCatalog()
        {
            if (!_catalogBuilt) RebuildCatalogLookup();
        }

        private bool TryGetEntry(int id, string method, out MID_AudioClipEntry entry)
        {
            EnsureCatalog();
            if (_idLookup.TryGetValue(id, out entry)) return true;

            string hint = id == 0 ? " (0 is normally AudioId.None, an unassigned field)" : "";
            MID_Logger.LogWarning(_logLevel, $"Audio id {id} is not in any catalog{hint}.",
                nameof(MID_AudioManager), method);
            return false;
        }

        // Category volume from the catalog times the runtime scale; 0 when muted.
        private float CategoryGain(int category)
        {
            if (category < 0) return 1f;
            if (_mutedCategories.Contains(category)) return 0f;

            float gain = 1f;
            MID_AudioCategorySettings settings;
            if (_categoryLookup.TryGetValue(category, out settings)) gain = settings.volume;

            float scale;
            if (_categoryVolume.TryGetValue(category, out scale)) gain *= scale;
            return gain;
        }

        private AudioMixerGroup CategoryGroup(int category)
        {
            MID_AudioCategorySettings settings;
            return _categoryLookup.TryGetValue(category, out settings) ? settings.mixerGroup : null;
        }

        private int PickIndex(int category, int count)
        {
            int index = Random.Range(0, count);
            int last;
            if (count > 1 && _lastRandomIndex.TryGetValue(category, out last) && index == last)
                index = (index + 1 + Random.Range(0, count - 1)) % count;
            _lastRandomIndex[category] = index;
            return index;
        }

        // The SFX source for a mixer group. A group other than the default needs its own source.
        private AudioSource GetSfxSource(AudioMixerGroup group)
        {
            if (group == null || group == _sfxMixerGroup) return _sfxSource;

            AudioSource source;
            if (_groupSources.TryGetValue(group, out source) && source != null) return source;

            var go = new GameObject("SFX_" + group.name);
            go.transform.SetParent(transform);
            source = go.AddComponent<AudioSource>();
            source.loop         = false;
            source.playOnAwake  = false;
            source.spatialBlend = 0f;
            source.outputAudioMixerGroup = group;
            _groupSources[group] = source;
            return source;
        }

        private void PlayPitched(AudioClip clip, float pitch, float volume, AudioMixerGroup group)
        {
            if (clip == null) return;

            var go  = new GameObject("SFX_Pitched");
            go.transform.SetParent(transform);
            var src = go.AddComponent<AudioSource>();

            if (group != null)
                src.outputAudioMixerGroup = group;

            src.spatialBlend = 0f;
            src.playOnAwake  = false;
            src.loop         = false;
            src.pitch        = Mathf.Clamp(pitch, 0.1f, 3f);
            src.volume       = Mathf.Clamp01(volume * _masterVolume);
            src.clip         = clip;
            src.Play();

            Destroy(go, clip.length / Mathf.Max(0.1f, src.pitch) + 0.1f);
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void WarnIfWrongKind<T>(MID_AudioEnumKind expected, string method) where T : unmanaged, System.Enum
        {
            if (!MID_EnumKey<T>.ShouldWarnWrongKind(expected)) return;
            MID_Logger.LogWarning(_logLevel,
                $"{typeof(T).Name} is not an audio {expected} enum. Pass the generated {(expected == MID_AudioEnumKind.Id ? "AudioId" : "AudioCategory")} value instead.",
                nameof(MID_AudioManager), method);
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

        private IEnumerator CrossFade(AudioClip next, float nextVolume, int nextCategory, AudioMixerGroup nextGroup)
        {
            float startVol = _musicSource.volume;
            for (float t = 0f; t < _crossFadeDuration; t += Time.unscaledDeltaTime)
            {
                _musicSource.volume = Mathf.Lerp(startVol, 0f, t / _crossFadeDuration);
                yield return null;
            }

            _musicSource.Stop();
            ApplyMusicGroup(nextGroup);
            _musicSource.clip   = next;
            _musicSource.volume = 0f;
            _musicSource.loop   = true;
            _musicSource.Play();
            _currentMusicVolume   = nextVolume;
            _currentMusicCategory = nextCategory;

            for (float t = 0f; t < _crossFadeDuration; t += Time.unscaledDeltaTime)
            {
                _musicSource.volume = Mathf.Lerp(0f, MusicTargetVolume(), t / _crossFadeDuration);
                yield return null;
            }
            _musicSource.volume = MusicTargetVolume();
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
