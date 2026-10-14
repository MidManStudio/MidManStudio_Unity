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
