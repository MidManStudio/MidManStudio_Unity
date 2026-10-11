# com.midmanstudio.audio

## 1.0.0

- First release as its own package (not yet tagged). The audio code moved here from `com.midmanstudio.utilities`.
  The namespace `MidManStudio.Core.Audio` is unchanged.
- **Moved:** `MID_AudioManager`, `MID_AudioLibrarySO`, `MID_NativeAudioBridge`, `MID_AudioLimiter`,
  `MID_SpawnableAudio`, the audio inspectors, the audio benchmark runner and the `mid_audio` native libraries.
- **Added:** `MID_FXAudioRelay` (effect audio for `GlobalFXManager`) and `AudioPoolProviderBootstrapper`.
- **Changed:** `PoolableObjectType.SpawnableAudio` is now defined by this package and has the value 200 (it was 0).
  The create-asset menu is `MidManStudio/Audio/Audio Library` and the benchmark window is under
  `MidManStudio > Audio > Tests`.
- See `docs/com.midmanstudio.audio.md`.
