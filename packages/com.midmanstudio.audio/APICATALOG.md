# com.midmanstudio.audio: API Catalog
**MidMan Studio Audio** v1.0.0. Namespace `MidManStudio.Core.Audio` unless noted.

## `MID_AudioManager` (Singleton)
| Member | Description |
|---|---|
| `void PlayMusic(string id, bool fade = true)` | Crossfades to the track from the music library. Queues the id while music is disabled. |
| `void StopMusic(bool fade = true)`, `PauseMusic()`, `ResumeMusic()` | |
| `void SetMusicEnabled(bool enabled)` | Raises `OnMusicEnabledChanged`, plays the remembered or start track when enabled, fades out when disabled |
| `void PlaySFX(string id)` | One-shot from the SFX library on the SFX source |
| `void PlaySFXPitched(string id, float pitch)` | Library SFX at a custom pitch |
| `void PlayClipDirect(AudioClip clip, float volume = 1f)` | One-shot of a clip that is not in a library |
| `void PlayClipDirectPitched(AudioClip clip, float pitch, float volume = 1f)` | Temporary AudioSource through the SFX mixer group. Pitch is clamped to 0.1 to 3. |
| `void SetMasterVolume(float v)` | 0 to 1. Re-applies master x the playing track's clip volume to the music source |
| `void SetMusicPitch(float targetPitch, bool instant = false)` | Glide over the configured duration unless `instant` |
| `event Action<bool> OnMusicEnabledChanged` | |
| `MasterVolume`, `IsMusicPlaying`, `IsMusicEnabled`, `CurrentMusicId` | |
| `int CurrentMusicAudioId` | Numeric id of the enum track that is playing, or -1 |

Play by generated enum. `T` is any `unmanaged, System.Enum`, normally the generated `AudioId`; `TCategory` is
normally `AudioCategory`. Each method has an int twin named with the suffix `ById`.

| Member | Description |
|---|---|
| `void PlayMusic<T>(T id, bool fade = true)` | Music by audio id. The clip's category decides the mixer group and volume scale. Twin: `PlayMusicById(int id, bool fade = true)` |
| `void PlaySFX<T>(T id)` | One-shot through the category's mixer group. Skipped when the category is muted. Twin: `PlaySFXById(int id)` |
| `void PlaySFXPitched<T>(T id, float pitch)` | Temporary AudioSource through the category group, or the SFX group when the category has none. Twin: `PlaySFXPitchedById(int id, float pitch)` |
| `void PlayRandomSFX<TCategory>(TCategory category)` | Random clip of the category, never the same twice in a row. Twin: `PlayRandomSFXInCategoryById(int category)` |
| `bool TryGetClip<T>(T id, out AudioClip clip)` | Looks up a clip without playing it. Twin: `TryGetClipById(int id, out AudioClip clip)` |
| `void SetCategoryVolume<T>(T category, float volume)` | Runtime scale from 0 to 1 on top of the catalog volume. Twin: `SetCategoryVolumeById(int category, float volume)` |
| `float GetCategoryVolume<T>(T category)` | 1 until set. Twin: `GetCategoryVolumeById(int category)` |
| `void SetCategoryMuted<T>(T category, bool muted)` | Twin: `SetCategoryMutedById(int category, bool muted)` |
| `bool IsCategoryMuted<T>(T category)` | Twin: `IsCategoryMutedById(int category)` |
| `void AddCatalog(MID_AudioCatalogSO catalog)`, `void RemoveCatalog(MID_AudioCatalogSO catalog)` | Change the merged catalogs at runtime |
| `void RebuildCatalogLookup()` | Call after editing a catalog at runtime. Awake, `AddCatalog` and `RemoveCatalog` call it |

Final volume of an enum sound: clip volume x category volume in the catalog x runtime category scale x master volume.
A wrong-kind enum (for example `AudioCategory` passed to `PlaySFX<T>`) logs a warning once per type in the editor and in
development builds.

## `MID_AudioCatalogSO` (ScriptableObject)
Create it from `MidManStudio/Audio/Audio Catalog`. Assign one or more to `MID_AudioManager`.

| Member | Description |
|---|---|
| `IReadOnlyList<MID_AudioCategorySettings> Categories` | Mixer group and volume per category |
| `IReadOnlyList<MID_AudioClipEntry> Clips` | One row per playable clip |
| `int Validate(List<string> problems)` | Adds each problem found and returns the count. Context menu: `Validate Catalog` |

`MID_AudioCategorySettings`: `int category`, `AudioMixerGroup mixerGroup`, `float volume` (0 to 1).
`MID_AudioClipEntry`: `int id`, `int category`, `AudioClip clip`, `float volume` (0 to 1). Both `id` and
`category` are plain ints drawn as enum popups.

## Generated enums
`AudioId` and `AudioCategory`, written by the Audio Type Generator (`MidManStudio > Audio > Audio Type Generator`),
by default to `Assets/MidManStudio/Generated/Audio/`. Namespace `MidManStudio.Core.Audio` (a setting). Both carry
`[MID_AudioEnum(MID_AudioEnumKind.Id)]` or `[MID_AudioEnum(MID_AudioEnumKind.Category)]`.

| Default member | Value |
|---|---|
| `AudioId.None` | 0 |
| `AudioCategory.None`, `Music`, `Sfx`, `Ui`, `Ambience`, `Voice` | 0, 1, 2, 3, 4, 5 |

## Providers and attributes
| Type | Description |
|---|---|
| `AudioIdProviderSO`, `AudioCategoryProviderSO` | Namespace `MidManStudio.Core.Audio.Generator`. Fields `packageId`, `displayName`, `priority`, `entries`. Menu: `MidManStudio/Audio/Audio Id Provider` and `Audio Category Provider` |
| `AudioEntryDefinition` | `entryName`, `comment`, `explicitOffset` (-1 auto, 0 or more pins the offset in the block) |
| `AudioTypeGeneratorSettingsSO` | Output paths, lock file path, block sizes (category 100, id 1000), namespace, `autoGenerateOnAssetChange` (off) |
| `[MID_AudioId]`, `[MID_AudioCategory]` | Put on an `int` field to draw it as a popup of the generated enum |
| `MID_AudioEnumKind`, `MID_AudioEnumAttribute` | Marks a generated enum as the id or the category enum |

## `MID_AudioLibrarySO` (ScriptableObject)
| Member | Description |
|---|---|
| `void BuildLookup()` | Called automatically. Case-insensitive ids, first duplicate wins, entries without id or clip are skipped |
| `bool TryGet(string id, out MID_AudioEntry entry)` | |
| `bool HasClip(string id)`, `int Count` | |

`MID_AudioEntry`: `string id`, `AudioClip clip`, `float volume` (0 to 1).

## `MID_NativeAudioBridge` (Singleton)
| Member | Description |
|---|---|
| `void PlayClip(int clipIndex, float volume = 1f)` | Plays the clip at that index. Ignores an out-of-range index. Steals the oldest voice when the pool is full. No allocations. |
| `void PlayClipDirect(AudioClip clip, float volume = 1f)` | Plays a clip that is not in the array |
| `void StopAll()` | |
| `AudioClip GetClip(int index)` | Null when out of range |
| `ActiveVoiceCount`, `ClipCount` | |

## `MID_AudioLimiter` (MonoBehaviour, requires AudioListener)
Inspector fields: `threshold` (0.1 to 1, default 0.95), `attack` (0.01 to 0.999, default 0.85), `release`
(0.0001 to 0.05, default 0.002), `enabled`. The limiter reads the fields; there is no public method API.

## `MID_SpawnableAudio` (MonoBehaviour, requires AudioSource)
| Member | Description |
|---|---|
| `void PlayOneShot(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f)` | Returns to the pool when the clip ends |
| `void PlayLooping(AudioClip clip, Vector3 position, Transform followTarget = null, Vector3 followOffset = default, float volume = 1f, float pitch = 1f)` | Follows the target without parenting |
| `void PlaySequential(AudioClip flyingClip, AudioClip collisionClip, Vector3 position, Transform followTarget, float volume = 1f, float pitch = 1f)` | Loops the flight clip while following |
| `void TriggerCollision(float volume = 1f)` | Switches to the collision clip |
| `void Return()` | Returns to the pool at once |

## `MID_FXAudioRelay` (MonoBehaviour)
Listens to `MidManStudio.Core.FX.GlobalFXManager.CategoryAudioRequested` while enabled. Fields: optional
`MID_NativeAudioBridge`, and the clip index per category: impact (0), muzzle flash (1), shell ejection (2),
explosion (-1). -1 skips audio.

## Editor
| Type | Description |
|---|---|
| `MID_NativeAudioBridgeEditor`, `MID_AudioLimiterEditor` | Custom inspectors (namespace `MidManStudio.Core.EditorUtils.Audio`) |
| `AudioPoolProviderBootstrapper` | Creates the audio pool provider asset. Menu: `MidManStudio > Audio > Internal > Recreate Audio Pool Provider` |
| `AudioTypeGeneratorWindow`, `AudioTypeGeneratorCore` | The generator window and its `Generate(settings)` entry point (namespace `MidManStudio.Core.EditorUtils.Audio`) |
| `AudioEnumProviderBootstrapper` | Creates the default id and category providers and the settings asset. Menu: `MidManStudio > Audio > Internal > Recreate Default Audio Providers` |
| `MID_AudioIdDrawer`, `MID_AudioCategoryDrawer` | Property drawers for `[MID_AudioId]` and `[MID_AudioCategory]` |
| `MID_AudioEnumLookup` | Editor-only list of the names and values of the generated enums |
| `MID_AudioBenchWindow` | `MidManStudio > Audio > Tests > Audio Bench` |

## Native library
Exports used by `MID_AudioLimiter`: `process_buffer(float*, int)`, `set_limiter_params(float, float, float)`,
`set_limiter_enabled(byte)`, `reset_limiter()`, `get_limiter_gain()`.

| Platform | Library |
|---|---|
| macOS | `macOS/libmid_audio.dylib` (universal) |
| Windows | `Windows/mid_audio.dll` |
| Linux | `Linux/libmid_audio.so` |
| Android | `Android/{arm64-v8a,armeabi-v7a,x86_64}/libmid_audio.so` |
| iOS | `iOS/libmid_audio.a` (device), `iOS/libmid_audio_sim.a` (simulator) |
| WebGL | none, managed fallback |
