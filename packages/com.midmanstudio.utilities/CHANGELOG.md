# Changelog — com.midmanstudio.utilities

All notable changes documented here.  
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

---

## [1.0.0] — Initial Release

### Core Systems

#### Tick Dispatcher (`MidManStudio.Core.TickDispatcher`)
- `MID_TickDispatcher` — zero-allocation, subscriber-based interval tick system.
  Replaces per-MonoBehaviour `Update()`. Single `Update()` dispatches to all subscribers
  at their configured rate. Empty buckets cost nothing.
- `MID_NativeTickDispatcher` — Burst-compiled native tick dispatcher for fully
  data-oriented workloads with 500+ subscribers doing real math.
- `TickRate` enum with 9 rates from `Tick_0_01` (100/sec) to `Tick_5` (0.2/sec).
  Rates below `Tick_0_1` documented as danger zone — fire faster than a typical frame.
- Death spiral protection with configurable max-ticks-per-frame guard.
- Editor-only live bucket monitor in the Inspector during Play mode.

#### Tick Delay (`MidManStudio.Core.TickDispatcher`)
- `MID_TickDelay` — zero-allocation delayed action system built on `MID_TickDispatcher`.
- Pool-based slot allocation — no heap allocation on hot path after initialisation.
- Minimum rate enforcement: rates faster than `Tick_0_1` are clamped with a warning.
  Fast rates fire faster than a frame — dispatcher overhead exceeds any benefit.
- `TickDelayHandle` — generation-stamped cancellable handle. Stale handles are safe.
- `After()`, `Repeat()`, `RepeatForever()` — all cancellable, all zero-alloc when
  called with a pre-allocated `static readonly Action` delegate.
- Designed for Netcode for GameObjects: no `IEnumerator`, main-thread execution,
  works directly inside `ServerRpc`/`ClientRpc` method bodies.
- Zero-alloc contract: `static readonly Action _cb = MyMethod; MID_TickDelay.After(1f, _cb);`
- Configurable pool capacity via `MID_TickDelay.PoolCapacity` before first call.

#### Logger (`MidManStudio.Core.Logging`)
- `MID_Logger` — level-gated singleton logger. Prefix token coloured in Editor;
  message body always plain text so log files and console detail pane are readable.
- `MID_LogLevel` enum: `None`, `Error`, `Info`, `Debug`, `Verbose`.
- `MID_LoggerSettings` — ScriptableObject for project-wide default level.
- `MID_LoggerEditorWindow` — bulk manage log levels across all scene MonoBehaviours.
  Supports search, group by GameObject, bulk set, and export to console.

#### Singletons (`MidManStudio.Core.Singleton`)
- `Singleton<T>` — MonoBehaviour singleton base with optional `DontDestroyOnLoad`.
  Auto-creates if not found. Handles duplicates. `SingletonLifecycle` interface for
  `OnSceneChange` callbacks.
- `StaticContentSingleton<T>` — thread-safe double-checked-locking singleton for
  plain C# classes. Supports `IStaticSingletonInitializable` and `IDisposable`.

#### Observable Values (`MidManStudio.Core.ObservableValues`)
- `MID_SusValue<T>` — generic observable value. Two subscription types:
  `OnValueChanged` (fires only on actual change) and `OnAnyUpdate` (fires every set).
  Duplicate-safe via `HashSet`. Optional validation predicate. Implicit conversion to `T`.
- `ManagedSusValue<T>` — extends `MID_SusValue<T>`. Auto-registers with
  `SusValueManager`. Subscriptions cleared automatically when owner `GameObject` is destroyed.
- `SusValueManager : Singleton` — tracks all managed values. Bulk-clear by owner
  or clear all. `SusValueOwnerWatcher` internal MonoBehaviour watches for owner destruction.

#### Events (`MidManStudio.Core.Events`)
- `MID_GameEvent : ScriptableObject` — decoupled SO event channel. Zero coupling
  between sender and receiver. Snapshot-based invocation list (safe to unsubscribe during raise).
- `MID_GameEventListener : MonoBehaviour` — self-registers on Enable, deregisters on Disable.
  Fires a `UnityEvent` response.
- `MID_DelayedGameEventListener` — extends `MID_GameEventListener`. Fires immediate
  response then a delayed response via `MID_TickDelay`. Zero allocation.
- `MID_EventBus<T>` — typed static event bus. One channel per `IMIDEvent` type.
  Thread-safe subscribe/unsubscribe. Exceptions in handlers caught and logged — other
  handlers still fire. Per-channel log level control.
- `MID_EventBusRegistry` — bulk-clear all registered channels on scene unload.
- `MID_EventUtilities` — duplicate-safe subscribe/unsubscribe helpers for plain `Action` fields.

### Pool Systems

#### Object Pools (`MidManStudio.Core.Pools`)
- `LocalObjectPool : Singleton` — pool manager for non-particle GameObjects.
  Keyed by generated `PoolableObjectType` enum (raw `int` internally).
  Auto-registration for unknown types. Prefab-to-type collision detection on init.
  Auto-chains to `LocalParticlePool` on `CallInitializePool()`.
- `LocalParticlePool : Singleton` — pool manager for particle GameObjects.
  Stops and clears all particle systems on return. Config validation on init.
- `LocalPoolReturn : MonoBehaviour` — auto-returns to `LocalObjectPool` after delay.
  Added automatically to every pooled instance. Resets `AudioSource`, `Animator`,
  and `TrailRenderer` on return.
- `LocalParticleReturn : MonoBehaviour` — auto-returns to `LocalParticlePool` after
  `maxLifetime` seconds via coroutine.
- `TrailRendererPool : Singleton` — generic slot-based `TrailRenderer` pool.
  Any moving entity can acquire a slot. Two-pass eviction: free slots first,
  then LRU from fading slots. Natural fade-out on `Release()`.
- `UIParticlePoolManager : MonoBehaviour` — manages UI-layer `ParticleSystem`
  effects by string key. Supports Play and Emit modes. Runtime registration.
- `BasicPoolConfig` / `ParticlePoolConfig` — inspector-visible pool entry configs.

#### Pool Type Generator (Editor)
- `PoolTypeGeneratorCore` — reads all `PoolTypeProviderSO` /
  `ParticlePoolTypeProviderSO` assets and writes three generated enum files.
  Lock file prevents value shifts on regeneration. Pinned offsets always win.
- `PoolTypeProviderSO` / `ParticlePoolTypeProviderSO` — one asset per package.
  Priority-based block assignment. Min block size configurable. Overflow detection.
- `PoolTypeGeneratorSettingsSO` — output paths, lock file path, namespace, block size,
  auto-generate on asset change.
- `PoolTypeGeneratorWindow` — editor window with provider discovery, settings UI,
  and per-provider ping. Shows object, particle, and network provider groups.
- `PoolTypeAssetPostprocessor` — auto-regenerates when provider assets change
  (if `autoGenerateOnAssetChange` is enabled in settings).
- `PackagePoolProviderBootstrapper` — `[InitializeOnLoad]` creates default provider
  assets for the utilities package on first import.

### Audio

#### Audio System (`MidManStudio.Core.Audio`)
- `MID_AudioManager : Singleton` — music (crossfade, pitch glide) + SFX
  (one-shot, pitched). Routes through `AudioMixerGroup` when assigned.
  `SetMusicEnabled(bool)` with crossfade. `OnMusicEnabledChanged` event.
  No game-specific dependencies.
- `MID_SpawnableAudio : MonoBehaviour` — pooled audio object. Supports one-shot,
  looping-follow (no parenting), and sequential (flight→collision) modes.
- `MID_AudioLibrarySO : ScriptableObject` — string-keyed clip registry.
  Case-insensitive lookup. Lazy build on first access. Invalidates on domain reload.

### Timers (`MidManStudio.Core.Timers`)
- `CountdownTimer` — counts down, fires `OnTimerComplete`.
- `StopwatchTimer` — counts up.
- `ValueInterpolationTimer` — interpolates a float from start to end.
  Modes: `Linear`, `EaseIn`, `EaseOut`, `EaseInOut`, `Custom` (AnimationCurve).
  Supports ping-pong.
- `SteppedValueTimer` — moves a float in discrete steps at a fixed interval.
- `TimerFactory` — static helpers for dissolve, alpha fade, and ping-pong timers.
- `PerformanceBenchmarkTimer` — microbenchmark utility with warmup, GC collection,
  per-iteration timing, and standard deviation. `CompareMethods` for A/B comparison.
- `PerformanceBenchmarkRunner : MonoBehaviour` — scene wrapper for benchmarks.

### Library System (`MidManStudio.Core.Libraries`)
- `MID_LibrarySO : ScriptableObject` — named collection of `MID_LibraryItemSO` assets.
  Case-insensitive string-keyed lookup. Lazy build. Domain-reload safe.
- `MID_LibraryItemSO : ScriptableObject` (abstract) — base for all library items.
  `ItemId` defaults to asset file name if blank. Subclass to add custom fields.
- `MID_BasicLibraryItemSO : MID_LibraryItemSO` — concrete ready-to-use item with
  `displayName`, `description`, `icon`, and `tags[]`. Create via
  `MidManStudio > Utilities > Library Item (Basic)`.
- `MID_LibraryRegistry : Singleton` — registers libraries, retrieves items by
  string key or generated enum keys (`LibraryId`, `LibraryItemId`).
- `LibraryTypeProviderSO` — contributes to generated `LibraryId` / `LibraryItemId` enums.
- `LibraryTypeGeneratorCore` + `LibraryTypeGeneratorWindow` — editor tool.
  Validates duplicate names, duplicate IDs, invalid C# identifiers.

### Scene Management (`MidManStudio.Core.SceneManagement`)
- `ISceneLoader` — common interface for regular and network loaders.
- `MID_SceneLoader : Singleton` — async single and additive loading with progress.
  Internet check for `InternetRequired` scenes via reflection (no hard netcode dep).
- `MID_SceneTransitionController` (abstract) — subclass to drive fade/UI animations.
  Override `TransitionIn`, `TransitionOut`, `OnLoadingStarted`, etc.
- `SceneTypeProviderSO` — contributes to generated `SceneId` enum and `SceneRegistry`.
- `SceneTypeGeneratorCore` + `SceneTypeGeneratorWindow` — editor tool.
  Validates duplicate build indices, duplicate enum names.
- `SceneRegistry` (generated static) — `GetName(SceneId)`, `GetDependency(SceneId)`.
- `SceneLoadType` enum: `Single`, `Additive`, `NetworkAdditive`.
- `SceneNetworkDependency` enum: `None`, `InternetRequired`, `NetworkSessionRequired`, `Optional`.
- `MID_SequentialProcessRunner` — async task runner with priority lanes, retry (max 6),
  and optional fallback for each task. `OnAllLanesComplete`, `OnTaskCompleted`, `OnTaskFailed`.

### UI State System (`MidManStudio.Core.UIState`)
- **Per-context model** — each logical UI area has its own `MID_UIStateContext` SO and
  generated `[Flags]` enum. No global flat enum.
- `MID_UIStateContext : ScriptableObject` — state machine for one UI context.
  History stack for back navigation. `ChangeState(int)`, `GoBack()`, `HasFlag(int)`.
  Persists state across scene loads (SO stays in memory).
- `MID_UIStateManager : Singleton` — drives panel show/hide for one context.
  `UIStatePanelConfig` with show/hide arrays and enter/exit `UnityEvent`s.
  Custom inspector shows named enum dropdowns when context is assigned.
- `MID_UIStateVisibility : MonoBehaviour` — shows element when context state
  contains any of the selected flags. Custom inspector shows checkboxes.
- `MID_UIStateButton : MonoBehaviour` — transitions context to target state on click.
  Custom inspector shows single-state dropdown.
- `MID_UIElement : MonoBehaviour` — base show/hide via `CanvasGroup`.
  Propagates visibility events to direct children.
- `UIStateContextProviderSO` — defines one context and its states.
  Generator produces one `[Flags]` enum per context.
- `UIStateContextGeneratorCore` + `UIStateContextGeneratorWindow` — editor tool.
  Validates duplicate context names, invalid identifiers, bit overflow (max 30 states).
- Custom inspectors for all UI state components in `MidManStudio.Utilities.Editor`.

### UI Components (`MidManStudio.Core.UI`)
- `MID_Button : MonoBehaviour` — animated click feedback with coroutine-based animations.
  No external tween library required. Rate-limited to prevent double-click spam.
  Animation types: `ScalePop`, `MoveLeft/Right/Up/Down`, `Bounce`, `Pulse`,
  `Shake`, `Rotate`, `FadeFlash`. Layout group aware.

### Helper Functions (`MidManStudio.Core.HelperFunctions`)
- `MID_HelperFunctions` — static utilities: GameObject child management, CanvasGroup,
  colour parsing, string formatting (sentence/camel/pascal/kebab/snake case),
  string validation, reflection debug dump, JSON/XML serialisation (Unity JsonUtility,
  no Newtonsoft dependency).
- `MID_HelperFunctionsWithType<T>` — generic functional helpers: `Map`, `Filter`,
  `Reduce`, `GroupBy`, `AnyMatch`, `AllMatch`, `PrintValues`.
- `MID_NamedListAttribute` + `IArrayElementTitle` + `IArrayElementColor` — inspector
  list element naming and colour tinting.
- `NamedListDrawer` — custom `PropertyDrawer` for `MID_NamedListAttribute`.
  Supports colour backgrounds with left border accent.

### Editor Tools
- `SceneDependencyInjector : MonoBehaviour` — editor-only. Instantiates persistent
  manager prefabs on Play. Removes need for bootstrap scene during isolated testing.
  Custom inspector with dependency list, force-reinject, and cleanup buttons.
- `MID_ScriptUtilitiesWindow` — editor utility window with two tabs:
  - **Script Reader**: browse and read any project script without leaving Unity.
    Parses and highlights XML doc `<summary>` / `<param>` / `<returns>` blocks.
    Search across all scripts. Syntax-aware display with monospace rendering.
  - **Window Priority Visualizer**: reflects all `EditorWindow` subclasses in loaded
    assemblies, extracts `[MenuItem]` priorities, displays sorted list.
    Filter by namespace. Shows menu path and priority for each window.

### Assembly Definitions
- `MidManStudio.Utilities` — runtime assembly. References `Unity.Burst`, `Unity.Collections`.
  `autoReferenced: true`. `allowUnsafeCode: true`.
- `MidManStudio.Utilities.Editor` — editor-only assembly. References `MidManStudio.Utilities`.
  `autoReferenced: false`. `includePlatforms: ["Editor"]`.
- `MidManStudio.Utilities.Tests` — test assembly. References runtime + test runners.
  `autoReferenced: false`. `defineConstraints: ["UNITY_INCLUDE_TESTS"]`.
