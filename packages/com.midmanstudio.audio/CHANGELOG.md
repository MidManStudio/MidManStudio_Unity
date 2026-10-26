# com.midmanstudio.audio

## 1.0.0

- First release as its own package (not yet tagged). The audio code moved here from `com.midmanstudio.utilities`.
  The namespace `MidManStudio.Core.Audio` is unchanged.
- **Moved:** `MID_AudioManager`, `MID_AudioLibrarySO`, `MID_NativeAudioBridge`, `MID_AudioLimiter`,
  `MID_SpawnableAudio`, the audio inspectors, the audio benchmark runner and the `mid_audio` native libraries.
- **Added:** `MID_FXAudioRelay` (effect audio for `GlobalFXManager`) and `AudioPoolProviderBootstrapper`.
- **Added:** audio ids and audio categories as generated enums, like the FX and pool type enums.
  `AudioIdProviderSO` and `AudioCategoryProviderSO` list the names, the Audio Type Generator
  (`MidManStudio > Audio > Audio Type Generator`) writes `AudioId.cs`, `AudioCategory.cs` and a lock file, and
  `MID_AudioCatalogSO` maps each id to a clip and each category to a mixer group and a volume. Inspector fields
  marked `[MID_AudioId]` or `[MID_AudioCategory]` are drawn as enum popups.
- **Added:** enum playback on `MID_AudioManager`: `PlayMusic<T>`, `PlaySFX<T>`, `PlaySFXPitched<T>`,
  `PlayRandomSFX<TCategory>`, `TryGetClip<T>`, runtime category volume and mute, `AddCatalog`, `RemoveCatalog`
  and an int-taking twin of each generic method. The string-id methods are unchanged.
- **Added:** the generator keeps numbers stable. An entry keeps its number when entries are inserted above it,
  blocks keep their place when a provider is added, and the number of a removed entry is not reused.
- **Changed:** `PoolableObjectType.SpawnableAudio` is now defined by this package and has the value 200 (it was 0).
  The create-asset menu is `MidManStudio/Audio/Audio Library` and the benchmark window is under
  `MidManStudio > Audio > Tests`.
- See `docs/com.midmanstudio.audio.md` and `docs/com.midmanstudio.audio/enum-ids.md`.
- **Fixed:** `MID_AudioManager.SetMasterVolume` dropped the playing track's clip volume. The unused `FadeIn` coroutine was removed, and a stale fade handle is cleared when the GameObject is deactivated.
- **Fixed:** `bench-audio.yml` now triggers on `master` and describes the limiter benchmarks.
