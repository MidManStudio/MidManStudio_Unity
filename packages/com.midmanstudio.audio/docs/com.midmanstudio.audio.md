# com.midmanstudio.audio

## Overview

Audio package for Unity 2022.3+: music with crossfade and pitch glide, SFX playback by library id, a pooled-voice
clip player, pooled spatial audio objects, a peak limiter for the final mix, and a relay that plays audio for
`GlobalFXManager` effects. 9 source files: 6 in `Runtime/`, 2 in `Editor/` and 1 in `Tests/Runtime/`. That is
small enough for a single doc file.

The package was split out of `com.midmanstudio.utilities`, where these files lived under `Runtime/Audio`. The
namespace `MidManStudio.Core.Audio` did not change, so code that used the types only needs a reference to the
`MidManStudio.Audio` assembly. This package depends on utilities (logging, the singleton base, object pools,
the named-list attribute and `GlobalFXManager`). Utilities does not depend on this package.

The native `mid_audio` Rust crate stays in `rust_lib/mid_audio`. Its prebuilt libraries are committed under
`Runtime/Plugins/Native/Audio`.

## Modules

### `MID_AudioLimiter.cs`
**What it does:** Component for the AudioListener GameObject that limits peaks in the final mixed output. It
applies the limiter in `OnAudioFilterRead`. Desktop and Android call the `mid_audio` library
(`process_buffer`, `set_limiter_params`, `set_limiter_enabled`, `reset_limiter`, `get_limiter_gain`). iOS
imports the same functions from `__Internal`. WebGL uses a managed limiter with the same threshold, attack and
release parameters.

**How it works:**
- `threshold` (default 0.95) is the peak that triggers gain reduction. `attack` (default 0.85) is the gain
  multiplier applied per audio buffer while limiting. `release` (default 0.002) is the gain increment per buffer
  while recovering. The parameters are pushed to the library from `Awake` and `OnValidate`, and only in play mode.
- In the editor, `Update` reads `get_limiter_gain` so the inspector can show the live gain.
- `OnDestroy` resets the limiter state in play mode.

**Decisions:**
- The component requires an `AudioListener` and has to sit on that GameObject. `OnAudioFilterRead` on the
  listener receives the mix of every AudioSource in the scene. On any other GameObject it would only see that
  object's own AudioSource.
- The managed fallback exists because the native library is not built for WebGL (see the workflow below).

### `MID_AudioManager.cs`
**What it does:** Singleton for music and SFX. Music plays by id from a `MID_AudioLibrarySO` with crossfade,
pause and resume, an enabled flag that raises `OnMusicEnabledChanged`, and a pitch glide. SFX play by id from a
second library, directly from a clip, or at a custom pitch. Both outputs can be routed to AudioMixerGroups.

**How it works:**
- `PlayMusic` remembers the requested id even while music is disabled, and `SetMusicEnabled(true)` then plays
  the remembered id (or the start id).
- Crossfades, fades and the pitch glide run as coroutines on unscaled time, so they continue while
  `Time.timeScale` is 0.
- `PlayClipDirectPitched` creates a temporary GameObject with its own AudioSource, routes it through the SFX
  mixer group and destroys it after the clip has finished.
- Per-clip volume is a scalar on top of the master volume. The mixer owns the user-facing volume.

**Decisions:**
- The class is generic. It has no references to a settings manager or analytics.
- Pitched one-shots allocate a GameObject per call. They are meant for occasional use; hot paths should use
  `MID_NativeAudioBridge.PlayClip`.

### `MID_NativeAudioBridge.cs`
**What it does:** Singleton that owns a pool of AudioSources (4 to 32, default 16) and plays clips from an
inspector array by index. `PlayClip(index, volume)` takes a free voice. When every voice is busy it stops the
voice at the current pool position and reuses it, which is the oldest in circular order.

**How it works:**
- The voices are child GameObjects created in `Awake`, each with its own AudioSource. All of them share the
  bridge's `spatialBlend` (0 is 2D).
- `PlayClip` makes no allocations. `PlayClipDirect` plays a clip that is not in the array through the same pool.
- Any clip load type works; Decompress On Load is not required.

**Decisions:**
- Despite the name, this class makes no native calls. The `mid_audio` library only backs `MID_AudioLimiter`. The
  name is kept so existing scenes and prefabs keep working.

### `MID_SpawnableAudio.cs`
**What it does:** Pooled audio object with three modes. `PlayOneShot` plays a clip once and returns to the pool
when it ends. `PlayLooping` loops a clip and can follow a transform without being parented to it.
`PlaySequential` loops a flight clip while following a target, and `TriggerCollision` switches to the collision
clip, plays it once, then returns to the pool.

**How it works:**
- Following is done in `Update` by copying the target position plus an offset. If the target is destroyed or
  deactivated the object returns to the pool.
- `Return` goes through `LocalPoolReturn` when the object has one. Otherwise it asks `LocalObjectPool` to return
  it under `PoolableObjectType.SpawnableAudio`. If neither is available it deactivates itself.

**Decisions:**
- The prefab is registered in the `LocalObjectPool` inspector with the `SpawnableAudio` type. The pool type is
  defined by this package (see `AudioPoolProviderBootstrapper.cs`).

### `MID_AudioLibrarySO.cs`
**What it does:** `MID_AudioEntry` (id, clip, volume) and `MID_AudioLibrarySO`, a ScriptableObject that maps
string ids to entries. `TryGet`, `HasClip` and `Count` build the lookup on first use.

**How it works:**
- Ids are compared case-insensitively. Entries with an empty id or no clip are skipped, and the first entry
  wins when two share an id.
- The lookup is rebuilt in `OnEnable` and whenever the dictionary is missing.

**Decisions:**
- The create-asset menu entry is `MidManStudio/Audio/Audio Library`. It was `MidManStudio/Utilities/Audio
  Library` when the file lived in utilities. Existing library assets are not affected.

### `MID_FXAudioRelay.cs`
**What it does:** Plays a `MID_NativeAudioBridge` clip for each effect that `GlobalFXManager` emits. It subscribes
to `GlobalFXManager.CategoryAudioRequested` while enabled, maps the effect category to a clip index (impact 0,
muzzle flash 1, shell ejection 2, explosion -1 by default), and calls `PlayClip` with the volume from the
effect. A clip index of -1 skips audio for that category. Categories added by game code have no clip index and
play nothing.

**How it works:**
- The bridge reference is optional. When empty, the relay uses the scene's `MID_NativeAudioBridge` instance as
  soon as one exists.

**Decisions:**
- `GlobalFXManager` lives in utilities and cannot reference this package. It exposes a static C# event and the
  relay listens to it. A direct call instead of `MID_EventBus<T>` is used because `Raise` allocates a snapshot
  array on every call, and effects are triggered many times per frame.
- This replaces the bridge reference and the four clip-index fields that were serialized on `GlobalFXManager`.
  Those values stay unused in existing scenes. Add the relay once per scene and enter the indices there.

### `MID_NativeAudioBridgeEditor.cs`
**What it does:** Hosts two custom inspectors. `MID_NativeAudioBridgeEditor` shows a clip slot table (name,
channels, length, load type, a play button per clip), a play-all and stop-all pair, a live voice pool monitor and
a collapsible setup guide. `MID_AudioLimiterEditor` shows the live gain in play mode and warns when the
component is not on an AudioListener GameObject.

**Decisions:**
- Both inspectors stay in one file. They are small and always change together with the two components.

### `AudioPoolProviderBootstrapper.cs`
**What it does:** Creates `Assets/MidManStudio/Audio/PoolProviders/ObjectPoolTypeProvider_Audio.asset` after
import when it is missing. The provider has package id `com.midmanstudio.audio`, priority 20, and one entry,
`SpawnableAudio`, pinned to offset 0 of its block.

**Decisions:**
- The pool type generator orders blocks by priority and gives each block 100 values. Utilities has priority 0
  (values 0 to 99) and the projectile system has priority 10 (values 100 to 199). Priority 20 puts the audio
  block at 200 to 299 and leaves the projectile values unchanged. In the enum, `SpawnableAudio` is therefore 200,
  not 0 as it was under utilities.
- The bootstrapper only creates the asset. Adding the entry to the generated enum needs the Pool Type Generator
  to run (`MidManStudio > Utilities > Pool Type Generator`).

### `MID_AudioBenchRunner.cs` (Tests)
**What it does:** In-editor benchmark runner and window (`MidManStudio > Audio > Tests > Audio Bench`). It
compares three ways of playing a clip: a single AudioSource with `PlayOneShot` (naive), a hand-written
AudioSource pool with circular steal (manual), and `MID_NativeAudioBridge.PlayClip` (bridge). The runs measure
allocations per call, throughput, voice-count behaviour, and the cost of `process_buffer` at three signal levels
(0.5, 0.9 and 1.5 amplitude). This is a manual tool, not an automated test suite.

**How it works:**
- The limiter section imports `process_buffer` from `mid_audio` itself, under the C# name
  `process_buffer_bench` with an explicit `EntryPoint`, so it does not collide with the import in
  `MID_AudioLimiter`.
- The limiter section is skipped on WebGL and on iOS.

**Decisions:**
- The menu path moved from `MidManStudio > Utilities > Tests > Audio Bench` to `MidManStudio > Audio > Tests >
  Audio Bench` with the package.

## CI and Workflows

- `.github/workflows/build-audio-libs.yml` builds the native `mid_audio` library and commits the results to
  `packages/com.midmanstudio.audio/Runtime/Plugins/Native/Audio/`. It runs on a push to `master` that touches
  `rust_lib/mid_audio/**`, or by hand. A push that contains `[skip ci]` skips it, and the workflow's own commit
  carries that tag, so run it by hand after a change that must rebuild the libraries. Platforms: macOS (universal
  x86_64 and arm64 dylib), Windows (x64 DLL, MinGW cross-compile), Linux (x64 .so), Android (arm64-v8a,
  armeabi-v7a and x86_64 through cargo-ndk, symbol-validated), iOS (device arm64 and a simulator fat static
  library). WebGL is not built.
- `.github/workflows/bench-audio.yml` runs the Criterion benchmarks of the `mid_audio` crate and commits the
  results under `.github/bench-results/`.
- `.github/workflows/release-packages.yml` releases this package from `packages/com.midmanstudio.audio` on an
  `audio/v*` tag or by hand.

**Decisions:**
- The Linux library is built with editor compatibility off. It is a CI artifact only; this avoids a segfault from
  `dlclose` and thread-local storage when the Unity editor quits.
- The libraries are rebuilt clean on every run, with no cargo or target caching (see Fixes and Problems).

## Fixes and Problems

### `build-audio-libs.yml`
**Fix:** An earlier version cached `target/` keyed only on `Cargo.toml`. When the exact key missed, a
`restore-keys` prefix fallback could restore a build of an older version of the crate (from before the rewrite
from a PCM voice bank to a limiter-only library). That produced stale .so files of 300 KB or more on arm64-v8a
and x86_64 that had the correct symbol names and a plausible size but ran the old code. The crate is about 150
lines, has no dependencies and builds in under 10 seconds per target, so the workflow now builds clean every
time.

### `MID_AudioBenchRunner.cs`
**Fix:** The limiter benchmark imported `process_buffer_bench` without an `EntryPoint`, so the runtime looked for
a symbol with that name and threw `EntryPointNotFoundException`. The import now sets
`EntryPoint = "process_buffer"`, which maps it to the real export.

**Fix:** The benchmark used to include a comparison against scheduling voices in the Rust library. The library
is now only a limiter and does not manage voices, so the comparison was removed.

### `MID_AudioManager.cs`
**Fix:** The class carried hooks to a game-specific settings manager and to analytics. They were removed to make
it generic.

**Problem:** `SetMasterVolume` assigns the master value straight to the music source volume. The playing track's
per-clip volume scalar is not applied until the next track starts. Not changed in this move.

**Problem:** The private coroutine `FadeIn` is not called anywhere. Not changed in this move.

### `MID_AudioLibrarySO.cs`
**Fix:** A ScriptableObject survives scene changes, but its non-serialized dictionary does not survive a domain
reload while the built flag stays true, which caused a null reference. The lookup now checks that the dictionary
exists as well as the flag.

### Move out of `com.midmanstudio.utilities`
**Change:** `PoolableObjectType.SpawnableAudio` moved from the utilities block (value 0) to the audio block
(value 200). A `LocalObjectPool` entry that was saved with the old value needs its type picked again after the
enum is regenerated.

**Change:** `GlobalFXManager` no longer holds a `MID_NativeAudioBridge` reference or clip indices. Effect audio
goes through `MID_FXAudioRelay`.

**Fix:** The audio section of the utilities `APICATALOG.md` listed members that do not exist: `SetSFXEnabled`,
`SetSFXVolume`, `SetMusicVolume`, `OnSFXEnabledChanged`, `PlayClipAt`, and clip lists named `musicClips` and
`sfxClips` on the library. This package's `APICATALOG.md` was written from the source.

**Problem:** `.github/workflows/bench-audio.yml` is set to trigger on the `main` branch, while
`build-audio-libs.yml` uses `master`. Its description also mentions DSP mixing and voice scheduling. Neither was
checked or changed in this move.
