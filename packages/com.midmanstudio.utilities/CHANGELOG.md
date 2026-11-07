# com.midmanstudio.utilities

## 1.0.0

First tagged release. Design decisions, limits and fix history per file are in `docs/com.midmanstudio.utilities.md` and the pages it links.

### Changes in this release

- **Stable numbers in the FX and pool generators** - the Effect Type Generator and the Pool Type Generator no longer renumber existing entries when an entry is added before them, an entry is removed, or the list is reordered. The numbers of removed entries stay reserved. Each lock file now also stores the block ranges and the retired numbers. Lock files in the old format still work and no number changes on the first run. A name that is not a valid C# member name is now an error, and a lock file that cannot be read stops the run. Shared logic in `EnumBlockLogic`, with Edit Mode tests. The logic types are public, and the projectile config generator in `com.midmanstudio.projectilesystem` uses them. See `docs/com.midmanstudio.utilities/enum-generators.md`.
- **Audio moved out** - the audio manager, audio library, native audio bridge, limiter, spawnable audio, their inspectors, the audio benchmark and the `mid_audio` native libraries now live in `com.midmanstudio.audio`. The namespace `MidManStudio.Core.Audio` is unchanged. `PoolableObjectType.SpawnableAudio` moved to the audio package and has the value 200 instead of 0. `GlobalFXManager` no longer holds an audio bridge reference or clip indices; it raises the static event `CategoryAudioRequested`, which `MID_FXAudioRelay` in the audio package listens to. See `docs/com.midmanstudio.utilities/fx.md` and `docs/com.midmanstudio.utilities/pool-systems.md`.
- **FPS counter** - `MID_FpsCounter` on-screen frame-rate readout and `MID_FrameTimeStats` rolling frame-time window (average, best, worst, 1% low). See `docs/com.midmanstudio.utilities/performance.md`.
- **Auto Reference system** - attribute-driven auto-wiring of Component/GameObject/interface fields on MonoBehaviours. Scans self + children (+ optional external search root) and disambiguates multi-candidate fields via a fuzzy name-match scorer (Levenshtein + token Jaccard + substring bonus, no external dependency). Full reference: [APICATALOG.md section 19](./APICATALOG.md#19-auto-reference).
  - `[MID_AutoRefable(autoAddComponent: false)]` - class attribute opting a script into scanning.
  - `[MID_NoAutoRef]` - field-level opt-out.
  - `MID_AutoRef` - runtime component; run modes `Manual` / `Awake` / `Start` / `OnValidate`.
  - `MID_AutoReferenceResolver` - static core resolver, callable standalone.
  - `MID_AutoRefComponentWatcher` - editor-only watcher that auto-adds `MID_AutoRef` when an `autoAddComponent: true` script is added to a GameObject. Duplicate-safe: existence check + `[DisallowMultipleComponent]` backstop.
  - `MidManStudio > Utilities > Auto Reference` - bulk scan/resolve window with an "Auto-Add Missing Components on Scan" toggle (off by default).
  - Custom inspector for `MID_AutoRef` with a one-click "Resolve Now" button.

### Systems in the package

- **Tick Dispatcher** - shared interval dispatcher replacing per-MonoBehaviour `Update()`. Nine tick rates from `Tick_0_01` (100/sec) to `Tick_5` (0.2/sec).
- **Tick Delay** - zero-allocation delayed and repeating actions built on the Tick Dispatcher. `After` / `Repeat` / `RepeatForever` / `Cancel` / `CancelAll`, pool-based, cancellable handles.
- **Logger** (`MID_Logger`) - leveled logging singleton.
- **Singletons** - MonoSingleton base classes.
- **Observable Values** - change-notifying value wrappers.
- **Events** - lightweight event bus.
- **Pool System** - generic object/particle pools, trail renderer pool, enum-refactored pool type registration.
- **FX System** - effect playback/pooling utilities.
- **Timers** - `CountdownTimer`, `StopwatchTimer`, `ValueInterpolationTimer` (linear/eased/custom-curve, ping-pong support), `SteppedValueTimer`, `NetworkTimer`.
- **Library System** - data library / registry pattern with generator tooling.
- **Scene Management** - async scene loader, transition controller and scene type generator.
- **UI State System** - state-driven UI panel management.
- **UI Components** - reusable UGUI components.
- **Helper Functions** (`MID_HelperFunctions`) - general-purpose static helpers.
- **Sequential Processing** - ordered async/coroutine step execution.
- **Sticky Note** (`MID_StickyNote`) - UGUI-based, edit-mode-visible scene note overlay with theming, anchoring, minimize/close, and drag support (Play Mode).
- **Editor Tools** - Script Utilities Window, Script Execution Order Window, Sprite-to-Tilemap Converter, ScriptableObject Viewer, Hierarchy Arranger, `SceneDependencyInjector`, a package update checker, and supporting shared UI Toolkit helpers (`MidEditorUIHelpers`, `GradientBannerElement`).
- **Samples** - Benchmarks, Auto Ref, PoolSystem, Ui State, Tick Test and Visual Effects. The Benchmarks scene uses the audio package for its audio part, and the Visual Effects sample needs `com.midmanstudio.audio` to compile.
- **Tests** - Tick Delay and Tick Dispatcher bench windows, a timer test suite component and Edit Mode tests for the enum numbering. See `docs/com.midmanstudio.utilities/tests.md`.

### Known issues

These were found by reading the code and are not fixed in 1.0.0. Most were not run in Unity. Each one has a "Not fixed" entry with the reasoning in the "Fixes and Problems" section of the page named in brackets.

**Editor**
- The package update checker is not reliable. It reads the repository-wide latest release, and the tags are `<package>/vX.Y.Z`, so it always reports "up to date". For a git install it reads the installed version from a project-relative path and gets 0.0.0. The install URL it shows lacks the package prefix in the tag. It sends one request to the GitHub API and has no setting to turn it off. The request is described in the README. [editor-helpers]
- `SceneDependencyInjector` is in the Editor assembly. Unity may not allow attaching it to a GameObject. Not tested. [editor-helpers]
- Script Execution Order window: removing a script alone does not show Apply, and the up and down buttons do nothing for scripts that share the same order. Sprite to Tilemap Converter: two different sprites with the same name share one tile asset. [editor-windows]
- The generators for libraries, scenes and UI states write to paths that assume this repository's layout, and the FX and pool generators default to them. In a project that installs the package from git those folders do not exist, so the files go to a `packages` folder next to `Assets`, where Unity does not compile them. The FX and pool generators have a settings asset where the paths can be changed. The library, scene and UI state generators have no setting. [libraries, scenemanagement, uistate, fx, pool-systems]
- The library, scene and UI state generators do not keep numbers stable. Adding or removing an entry before the end renumbers the rest, and numbers already saved in scenes and prefabs then point at other entries. The effect, pool and audio generators are not affected. [libraries, uistate]

**Runtime**
- `MID_TickDelay`: when the pool is full (64 pending delays by default) the new call is dropped and no warning is printed, because the warning is logged at a level that the logger suppresses. [tickdispatcher]
- `MID_TickDispatcher`: more than 512 subscribers on one rate makes the update throw every frame. With Enter Play Mode Options and domain reload turned off, a second play session gets no instance. [tickdispatcher]
- `MID_SusValue`: the any-update event fires before the value is stored, and changing the subscriber set from inside a callback throws. [helpers]
- `MID_HelperFunctions.GetStructOrClassMemberValues` has no depth guard and can overflow the stack on types such as `Vector3` with their `normalized` property. [helpers]
- `UIState`: a back step can leave the "going back" flag set, mask 0 means hidden on one component and visible on another, and two states with the same name are not detected. [uistate]
- `SceneManagement`: `Task.Delay` is used for waits, which is reported not to complete in WebGL builds. `OnTransitionComplete(int)` is never called. [scenemanagement]
- `Pools`: no protection against a double return in the particle pool, and the root scale of a pooled object is reset on every return. [pool-systems]
- `StickyNote`: `ResetToAnchor()` does nothing after a drag, and minimizing does not resize the panel. [stickynote]
- `SteppedValueTimer` takes at most one step per tick, and `BenchmarkResult.ToCSV()` depends on the machine's number format. [timers]

**Tests and samples**
- The Tick Dispatcher Bench does not call `MID_TickDispatcher`, so its "dispatcher is N times faster" figure is not a measurement of the dispatcher. The Tick Delay Bench makes 500 calls against a 64 slot pool by default, so its allocation result is not valid at the default settings, and its runner class name does not match its file name. [tests]
- The samples use the old Input class, and three log messages in the Tick Test sample show `{...}` instead of values. [samples]
