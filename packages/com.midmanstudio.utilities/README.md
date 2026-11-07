# com.midmanstudio.utilities

**MidMan Studio Utilities** v1.0.0. Core runtime utilities for Unity 2022.3+.
No game-specific dependencies. Foundation for all MidManStudio packages.

Audio (audio manager, native audio bridge, limiter) is not in this package. It is in `com.midmanstudio.audio`.

## Requirements

| Dependency | Version |
|---|---|
| Unity | 2022.3 LTS |
| com.unity.burst | 1.8.9+ |
| com.unity.collections | 2.2.1+ |
| com.unity.mathematics | 1.3.1+ |

## Installation

**Via git URL** (Unity Package Manager > Add package from git URL):

```
https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.utilities#utilities/v1.0.0
```

**Via local file path** (development, in `manifest.json`):

```
"com.midmanstudio.utilities": "file:../../packages/com.midmanstudio.utilities"
```

Dependencies are resolved automatically by UPM.

## Documentation

| Where | What |
|---|---|
| This file | Quick start for each system |
| [APICATALOG.md](APICATALOG.md) | Hand-written API reference, one section per system. Parts of it use old type names, so check a name against the per-system pages below if it does not compile |
| [docs/com.midmanstudio.utilities.md](docs/com.midmanstudio.utilities.md) | Index of the per-system pages: how each file works, its limits and its fix history |
| [Documentation~/scenesetup.md](Documentation~/scenesetup.md) | Step-by-step test scene set-up |
| [CHANGELOG.md](CHANGELOG.md) | What is in 1.0.0 and the known issues |

## What's Included

| System | Namespace | Description |
|---|---|---|
| Tick Dispatcher | MidManStudio.Core.TickDispatcher | Interval-based Update replacement |
| Tick Delay | MidManStudio.Core.TickDispatcher | Zero-alloc delayed/repeating actions, NGO-ready |
| Logger | MidManStudio.Core.Logging | Level-gated coloured console logger |
| Singletons | MidManStudio.Core.Singleton | MonoBehaviour + pure C# singleton bases |
| Observable Values | MidManStudio.Core.ObservableValues | Reactive value containers with auto-cleanup |
| Events | MidManStudio.Core.Events | SO event channels + typed static event bus |
| Pool System | MidManStudio.Core.Pools | Object, particle, and trail renderer pools |
| Pool Type Generator | MidManStudio.Core.Pools.Generator | Code-generates shared pool enums from SO providers |
| FX System | MidManStudio.Core.FX | CPU particle and flipbook effect manager. Sound is played by `MID_FXAudioRelay` in `com.midmanstudio.audio` |
| FX Type Generator | MidManStudio.Core.FX.Generator | Code-generates EffectCategory/EffectType enums |
| Timers | MidManStudio.Core.Timers | Countdown, stopwatch, interpolation, stepped, network |
| Performance | MidManStudio.Core.Performance | `MID_FpsCounter` and `MID_FrameTimeStats` |
| Library System | MidManStudio.Core.Libraries | Keyed ScriptableObject asset registry |
| Scene Management | MidManStudio.Core.SceneManagement | Async loader + transition controller |
| UI State System | MidManStudio.Core.UIState | Per-context [Flags] enum state machine |
| UI Components | MidManStudio.Core.UI | Animated button with no tween dependency |
| Auto Reference | MidManStudio.Core.AutoReference | Attribute-driven wiring of component fields |
| Helpers | MidManStudio.Core.HelperFunctions | String, UI, JSON, reflection utilities |
| Sequential Runner | MidManStudio.Core.SequentialProcessing | Priority lane async task runner with retry |
| Hierarchy Arranger | MidManStudio.Core.HierarchyArranger | Hierarchy ordering and separator markers |
| Sticky Note | MidManStudio.Core.Notes | In-Game-View overlay note for scene setup docs |
| Editor Tools | MidManStudio.Core.EditorUtils | Logger manager, generators, script and tilemap windows, dependency injector |

## Quick Start

### Tick Dispatcher

Replaces per-MonoBehaviour Update(). A system that only needs 5 checks/sec should not run 60x/sec.

**Rate selection guide:**

| Rate | Fires/sec | Use for |
|---|---|---|
| Tick_0_05 | 20 | Fast weapon systems, projectile checks |
| Tick_0_1 | 10 | Recommended minimum: fast AI, cooldowns |
| Tick_0_2 | 5 | Standard: enemy AI, ability systems |
| Tick_0_5 | 2 | Area checks, perception |
| Tick_1 | 1 | Health regen, UI numbers |
| Tick_2 | 0.5 | Distant object updates |
| Tick_5 | 0.2 | Spawners, wave logic |

Warning: Tick_0_01 and Tick_0_02 fire faster than a typical frame, so they save nothing at normal fps. Never use them for gameplay logic.

### Tick Delay: Zero-Alloc Delayed Actions

The zero-GC alternative to StartCoroutine and Task.Delay. Works inside Netcode for GameObjects RPCs where IEnumerator is forbidden. The fastest rate it accepts is Tick_0_1, and faster rates are raised to it.

**Trade-off comparison:**

|  | MID_TickDelay | Coroutine | Task.Delay |
|---|---|---|---|
| GC allocation | **0 B per call** while the pool has room | roughly 80-400 B/call | roughly 120 B cold |
| Thread | **Main** | Main | Threadpool, unsafe for Unity APIs |
| IEnumerator | **Not needed** | Required, breaks RPC signatures | Not needed |
| Cancellation | **TickDelayHandle** | StopCoroutine | CancellationToken (+alloc) |
| Timing error | 0-100ms at Tick_0_1 | 0-16ms at 60fps | ~0-2ms (OS timer) |

The Coroutine and Task.Delay allocation figures are rough. Check your own case in the Profiler.

The pool holds 64 pending delays by default (`MID_TickDelay.PoolCapacity`). When it is full, a new call is dropped and no warning is printed. Raise `PoolCapacity` before the first call if you need more.

### Logger

Bulk-manage log levels across all scene objects: MidManStudio > Utilities > Logger Manager.

**Log levels:**

| Level | Output |
|---|---|
| None | No output |
| Error | Errors only |
| Info | Info + warnings + errors (production recommended) |
| Debug | Debug + info + warnings + errors |
| Verbose | Everything |

### Object Pool

1. Open MidManStudio > Utilities > Pool Type Generator
2. Create a Pool Type Provider SO, add entries, click Generate Now
3. Add LocalObjectPool to a persistent GameObject, assign prefabs in inspector
4. Initialize at game start via LocalObjectPool.Instance.CallInitializePool()

### FX System

Unified CPU-based visual effects. Wrap any in-scene ParticleSystem in an FXEntry and trigger by category/type.

**Setup:**

1. Place GlobalFXManager on a persistent GameObject
2. Add FXEntry items in the inspector, one per (EffectCategory, EffectType) pair
3. Assign in-scene ParticleSystem references. They must have **Simulation Space = World**
4. For sound, install `com.midmanstudio.audio` and add `MID_FXAudioRelay`. `GlobalFXManager` raises the static event `CategoryAudioRequested` and the relay plays the clip

**Adding custom categories/types:**

1. Create EffectCategoryProviderSO or EffectTypeProviderSO (right-click Project)
2. Set priority 100 or higher for game code (0 = utilities reserved, 10 = projectile reserved)
3. MidManStudio > Utilities > Effect Type Generator > Generate Now

### Observable Values

Use `MID_SusValue<T>` for plain values (manual cleanup) or `ManagedSusValue<T>` to have subscriptions cleared automatically when the owner GameObject is destroyed. Create them in `Awake` or `Start`, not in field initialisers.

### UI State System

**Components:**

| Component | Purpose |
|---|---|
| MID_UIStateManager | Drives panel show/hide for one context |
| MID_UIStateVisibility | Shows element when context flags match |
| MID_UIStateButton | Transitions context on click |
| MID_UIStateBackButton | Navigates back + state-based visibility |
| MID_UIElement | Base CanvasGroup show/hide with child propagation |

### Library System

1. Right-click > MidManStudio > Utilities > Library Item (Basic) to create items
2. Right-click > MidManStudio > Utilities > Library to create a library and add items
3. Add MID_LibraryRegistry to a persistent GameObject, assign library
4. Retrieve via MID_LibraryRegistry.Instance.GetItem(...)

### Sticky Note

In-Game-View overlay for scene documentation, setup instructions, or tutorial content. Works in Play Mode and Edit Mode. Supports drag-to-reposition, minimize, and close.

## Pool Type Generator

Writes PoolableObjectType.cs and PoolableParticleType.cs under Runtime/PoolSystems/Generated/.

### Adding your own pool types

1. MidManStudio > Utilities > Pool Type Generator > + Object Provider
2. Set packageId (e.g. com.mygame), priority 100 or higher, add entry names (PascalCase)
3. Generate Now

### Block ranges

Each provider gets a block of at least 100 numbers, in priority order.

| Priority | Package | Block |
|---|---|---|
| 0 | com.midmanstudio.utilities | 0-99 |
| 10 | com.midmanstudio.projectilesystem | 100-199 |
| 20 | com.midmanstudio.audio | 200-299 |
| 100+ | Your game | after the blocks above |

**Pinning entries:** Unpinned entries are stabilised by the lock file (Assets/MidManStudio/Generated/Pools/PoolTypeLock.json). **Commit the lock file to source control.**

## Editor Tools

| Tool | Open via |
|---|---|
| Logger Manager | MidManStudio > Utilities > Logger Manager |
| Pool Type Generator | MidManStudio > Utilities > Pool Type Generator |
| Library Type Generator | MidManStudio > Utilities > Library Type Generator |
| Scene Type Generator | MidManStudio > Utilities > Scene Type Generator |
| UI State Context Generator | MidManStudio > Utilities > UI State Context Generator |
| Effect Type Generator | MidManStudio > Utilities > Effect Type Generator |
| Auto Reference | MidManStudio > Utilities > Auto Reference |
| Hierarchy Arranger | MidManStudio > Utilities > Hierarchy Arranger |
| Scriptable Object Viewer | MidManStudio > Utilities > Scriptable Object Viewer |
| Script Utilities | MidManStudio > Utilities > Script Utilities |
| Script Execution Order | MidManStudio > Utilities > Script Execution Order |
| Sprite to Tilemap Converter | MidManStudio > Utilities > Sprite to Tilemap Converter |
| Check for Updates | MidManStudio > Utilities > Check for Updates (see below) |
| Tick Delay Benchmark | MidManStudio > Utilities > Tests > Tick Delay Bench |
| Tick Dispatcher Benchmark | MidManStudio > Utilities > Tests > Tick Dispatcher Bench |
| Scene Dependency Injector | Add the SceneDependencyInjector component to a scene GameObject (see Known issues) |

The audio benchmark is in the audio package: MidManStudio > Audio > Tests > Audio Bench.

### Update check

The package has an update checker. It sends one HTTPS GET request to `https://api.github.com/repos/MidManStudio/MidManStudio_Unity/releases/latest` with the User-Agent `MidManStudio-UpdateChecker/<version>`, and nothing else. It runs when you open the window or press Check Now. It also runs silently after a script reload when the last successful check is more than 24 hours old. It stores `MidManStudio.UpdateChecker.LastCheck`, `.LastResult` and `.SkipTag` in EditorPrefs. There is no setting to turn it off in 1.0.0.

In 1.0.0 its result is not reliable. It reads the latest release of the whole repository, and this repository tags packages as `<package>/vX.Y.Z`, so it reports "up to date". When the package is installed from git it also reads the installed version from the wrong path. Treat it as informational.

## Samples

Import from Package Manager > MidMan Studio Utilities > Samples.

| Sample | What it shows | Needs |
|---|---|---|
| Benchmarks | Tick, timer and audio benchmark scene | `com.midmanstudio.audio` for the audio part |
| Auto Ref | Auto Reference demos | |
| PoolSystem | Local object pool and particle pool | |
| Ui State | UI state manager, menu to menu | |
| Tick Test | Tick dispatcher, tick delay and timers running together | |
| Visual Effects | Global FX manager with native and spawnable audio | `com.midmanstudio.audio`. Without it the sample script does not compile |

## Known issues

The full list is in [CHANGELOG.md](CHANGELOG.md). Per-file details are in the "Fixes and Problems" section of each page under `docs/com.midmanstudio.utilities/`. The ones most likely to matter:

* The generators write to output paths that assume this repository's layout (fixed in code for libraries, scenes and UI states, and default settings for FX and pools). The library, scene and UI state generators also do not keep numbers stable when entries are added or removed. See CHANGELOG.
* `SceneDependencyInjector` is in the Editor assembly. Unity may not let you attach a script from an Editor-only assembly to a GameObject. This has not been tested, so check it in your project.
* The Tick Dispatcher Bench does not call `MID_TickDispatcher`, and the Tick Delay Bench can run past the default pool size. See `docs/com.midmanstudio.utilities/tests.md`.

## Assembly Structure

Your game assembly sees MidManStudio.Utilities automatically (autoReferenced: true). Reference MidManStudio.Utilities.Editor explicitly only if you use pool/library generators or custom inspector code in your own editor assembly.

## Generated File Locations

All generated files live under Generated/ subfolders inside the package runtime:

* Runtime/PoolSystems/Generated/
* Runtime/FXSystems/Generated/
* Runtime/Libraries/Generated/
* Runtime/SceneManagement/Generated/
* Runtime/UIState/Generated/

Lock files (project-level, not inside the package):

* Assets/MidManStudio/Generated/Pools/PoolTypeLock.json
* Assets/MidManStudio/Generated/FX/EffectTypeLock.json

**Always commit lock files to source control.** They prevent enum value shifts on regeneration.

## Persistent Manager Setup

Recommended hierarchy for a Managers prefab (DontDestroyOnLoad):

1. MID_Logger
2. MID_TickDispatcher
3. SusValueManager
4. LocalObjectPool
5. LocalParticlePool
6. TrailRendererPool
7. GlobalFXManager
8. MID_LibraryRegistry
9. MID_UIStateManager

With `com.midmanstudio.audio` installed, add MID_AudioManager and MID_NativeAudioBridge too.

**For isolated scene testing:** use SceneDependencyInjector instead of a bootstrap scene. Assign required manager prefabs in Required Dependencies and press Play.

## License

MIT, see LICENSE.md.

Copyright © 2026 Abdulhamid Manman Suleiman / MidMan Studio
