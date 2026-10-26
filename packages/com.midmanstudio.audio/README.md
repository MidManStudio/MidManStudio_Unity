# com.midmanstudio.audio

**MidMan Studio Audio** v1.0.0. Audio for Unity 2022.3+: music with crossfade and pitch glide, SFX by string id or
by generated enum, audio categories with a mixer group and volume, a pooled-voice clip player, pooled spatial audio
objects, a peak limiter for the final mix, and a relay that plays audio for `GlobalFXManager` effects.

## Requirements

| Dependency | Version |
|---|---|
| Unity | 2022.3 LTS |
| `com.midmanstudio.utilities` | 1.0.0 |

## Installation

Open Window > Package Manager > + > Add package from git URL, and paste:

```
https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.audio#audio/v1.0.0
```

This package depends on other MidManStudio packages. Unity does not resolve git dependencies between packages, so add these first, in this order:

- `com.midmanstudio.utilities`:
  ```
  https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.utilities#utilities/v1.0.0
  ```

To work against a local checkout instead, add this to `Packages/manifest.json`:

```json
"com.midmanstudio.audio": "file:../path/to/packages/com.midmanstudio.audio"
```

## What's Included

| System | Namespace | Description |
|---|---|---|
| `MID_AudioManager` | `MidManStudio.Core.Audio` | Music (crossfade, pitch glide, enable flag) and SFX, by string id or generated enum |
| `MID_AudioCatalogSO` | `MidManStudio.Core.Audio` | Maps generated audio ids to clips, and categories to a mixer group and volume |
| `AudioIdProviderSO`, `AudioCategoryProviderSO` | `MidManStudio.Core.Audio.Generator` | Name lists that become the generated `AudioId` and `AudioCategory` enums |
| Audio Type Generator | `MidManStudio.Core.EditorUtils.Audio` | Editor window that writes the enums and a lock file that keeps their numbers stable |
| `MID_AudioLibrarySO` | `MidManStudio.Core.Audio` | String-id library of clips for the string API |
| `MID_NativeAudioBridge` | `MidManStudio.Core.Audio` | Pooled voices (4 to 32) that play clips by index without allocating |
| `MID_SpawnableAudio` | `MidManStudio.Core.Audio` | Pooled one-shot, looping and projectile-flight audio objects |
| `MID_AudioLimiter` | `MidManStudio.Core.Audio` | Peak limiter on the AudioListener (Rust on desktop and mobile, managed on WebGL) |
| `MID_FXAudioRelay` | `MidManStudio.Core.Audio` | Plays a bridge clip for each effect that `GlobalFXManager` emits |

## Quick Start: audio by enum

1. Create an id provider: `Assets > Create > MidManStudio > Audio > Audio Id Provider`. Set `packageId` (for
   example `com.mygame`), `priority` 100 or more, and add names such as `SwordHit` and `TavernTheme`. A category
   provider works the same way. The package already provides the categories `None`, `Music`, `Sfx`, `Ui`,
   `Ambience` and `Voice`.
2. Open `MidManStudio > Audio > Audio Type Generator` and click `Generate Now`. It writes `AudioId.cs`,
   `AudioCategory.cs` and `AudioTypeLock.json` under `Assets/MidManStudio/Generated/Audio/`. Commit all three. The
   lock file keeps the numbers stable.
3. Create a catalog: `Assets > Create > MidManStudio > Audio > Audio Catalog`. Add a row per clip (id, category,
   clip, volume) and, optionally, a row per category (mixer group, volume). Add the catalog to the `Catalogs` list
   on `MID_AudioManager`.
4. Play:

```csharp
var audio = MID_AudioManager.Instance;
audio.PlayMusic(AudioId.TavernTheme);
audio.PlaySFX(AudioId.SwordHit);
audio.PlayRandomSFX(AudioCategory.Sfx);
audio.SetCategoryVolume(AudioCategory.Music, 0.5f);
audio.SetCategoryMuted(AudioCategory.Voice, true);
```

String ids still work: `PlaySFX(string id)` plays from the `MID_AudioLibrarySO` assigned to the manager.

The generated files compile into the assembly of the folder they are in. Under `Assets` without an assembly
definition, that is `Assembly-CSharp`. Game code that sits inside its own assembly definition cannot reference
it, so point the output paths in the `AudioTypeGeneratorSettings` asset at a folder inside that assembly.

## Documentation

- `docs/com.midmanstudio.audio.md`: the playback classes, CI workflows and the history of fixes.
- `docs/com.midmanstudio.audio/enum-ids.md`: generated ids and categories, how numbers are assigned, and the
  generator.
- `APICATALOG.md`: the public API as tables.
- `CHANGELOG.md`

## Native library

`MID_AudioLimiter` calls the `mid_audio` Rust library. Prebuilt libraries are included for macOS, Windows, Linux,
Android and iOS. WebGL has no native library and uses the managed limiter.
