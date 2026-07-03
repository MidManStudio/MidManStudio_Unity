# com.midmanstudio.utilities — API Catalog
**MidMan Studio Utilities** v1.0.0  
Last updated: 2026-07-03

> All discrepancy fixes from the audit are applied here.  
> ⚠ marks corrected entries. Removed entries that had no implementation.

---

## Table of Contents

1. [Tick Dispatcher](#1-tick-dispatcher)
2. [Tick Delay](#2-tick-delay)
3. [Logger](#3-logger)
4. [Singletons](#4-singletons)
5. [Observable Values](#5-observable-values)
6. [Events](#6-events)
7. [Pool System](#7-pool-system)
8. [Audio](#8-audio)
9. [FX System](#9-fx-system)
10. [Timers](#10-timers)
11. [Library System](#11-library-system)
12. [Scene Management](#12-scene-management)
13. [UI State System](#13-ui-state-system)
14. [UI Components](#14-ui-components)
15. [Helper Functions](#15-helper-functions)
16. [Sequential Processing](#16-sequential-processing)
17. [Sticky Note](#17-sticky-note)
18. [Editor Tools](#18-editor-tools)
19. [Auto Reference](#19-auto-reference)

---

## 1. Tick Dispatcher

**Namespace:** `MidManStudio.Core.TickDispatcher`  
**File:** `Runtime/TickDispatcher/MID_TickDispatcher.cs`  
**Type:** MonoSingleton

Replaces per-MonoBehaviour `Update()` with a shared interval dispatcher.
Subscribe once per tick rate — multiple systems sharing the same rate cost one dispatcher invocation.

### `MID_TickDispatcher`

```
delegate void TickCallback(float deltaTime)
```

| Method | Returns | Description |
|---|---|---|
| `Subscribe(TickRate rate, TickCallback callback)` | `void` | Register a callback to fire at the given interval |
| `Unsubscribe(TickRate rate, TickCallback callback)` | `void` | Deregister — always call in OnDisable/OnDestroy |
| `GetTickRateValue(TickRate rate)` | `float` | Returns the interval in seconds for a given rate |

> **Zero-alloc rule:** Always store callbacks as `private static readonly TickCallback _cb = MyMethod;`  
> A method group expression (`Subscribe(rate, MyMethod)`) allocates a new delegate object every call.

---

### `TickRate` Enum

`Runtime/TickDispatcher/TickRate.cs`

| Value | Interval | Fires/sec | Recommended use |
|---|---|---|---|
| `Tick_0_01` | 0.01s | 100 | ⚠ Faster than 60fps — no gameplay value |
| `Tick_0_02` | 0.02s | 50 | ⚠ Faster than 60fps — no gameplay value |
| `Tick_0_05` | 0.05s | 20 | Fast weapon systems, rapid-fire checks |
| `Tick_0_1` | 0.1s | 10 | Fast AI, cooldown tracking — recommended minimum |
| `Tick_0_2` | 0.2s | 5 | Standard AI, ability systems ← most common |
| `Tick_0_5` | 0.5s | 2 | Area-of-effect checks, perception |
| `Tick_1` | 1.0s | 1 | Health regen, stats display |
| `Tick_2` | 2.0s | 0.5 | Distant object updates |
| `Tick_5` | 5.0s | 0.2 | Spawner logic, wave managers |

---

### `MID_NativeTickDispatcher`

**File:** `Runtime/TickDispatcher/MID_NativeTickDispatcher.cs`

Burst-compiled `IJob` wrapper. Runs accumulation logic on a worker thread; callbacks still fire on main thread. No public API — `MID_TickDispatcher` uses it internally.  
Requires `Unity.Burst` and `Unity.Collections` in the asmdef.

---

### `TickDelayHandle` (struct)

**File:** `Runtime/TickDispatcher/TickDelayHandle.cs`

| Member | Type | Description |
|---|---|---|
| `IsValid` | `bool` | True if handle refers to a live entry |
| `IsComplete` | `bool` | True if the delay has already fired |
| `Cancel()` | `void` | Cancels the pending action; safe if already fired |

---

## 2. Tick Delay

**Namespace:** `MidManStudio.Core.TickDispatcher`  
**File:** `Runtime/TickDispatcher/MID_TickDelay.cs`  
**Type:** Static class

Zero-alloc alternative to `StartCoroutine` and `Task.Delay`.
Runs on the main thread; safe to call any Unity API in the callback.
Works inside NGO ServerRpc/ClientRpc where `IEnumerator` signatures are forbidden.

### `MID_TickDelay`

| Method | Returns | Description |
|---|---|---|
| `After(float delay, Action callback, TickRate rate)` | `TickDelayHandle` | Fire callback once after delay seconds |
| `Every(float interval, Action callback, TickRate rate, int repeatCount = -1)` | `TickDelayHandle` | Repeat at interval; -1 = infinite |
| `Cancel(TickDelayHandle handle)` | `void` | Cancel a specific handle |
| `CancelAll()` | `void` | Cancel all pending delays |

> **Zero-alloc rule:** Cache `Action` as `private static readonly Action _cb = MyMethod;`  
> Passing a method group expression allocates a new `Action` object on the heap every call.

---

## 3. Logger

**Namespace:** `MidManStudio.Core.Logging`  
**File:** `Runtime/Logging/MID_Logger.cs`  
**Type:** Static class

Level-gated coloured console logger. Each MonoBehaviour controls its own `MID_LogLevel` field —
no global mute, no reflection overhead. All output is stripped from release builds when level = `None`.

### `MID_Logger`

| Method | Description |
|---|---|
| `LogInfo(MID_LogLevel level, string message, string className = "", string methodName = "")` | Logs if level >= Info |
| `LogWarning(MID_LogLevel level, string message, string className = "", string methodName = "")` | Logs if level >= Info |
| `LogError(MID_LogLevel level, string message, string className = "", string methodName = "", Exception ex = null)` | Logs if level >= Error |
| `LogDebug(MID_LogLevel level, string message, string className = "", string methodName = "")` | Logs if level >= Debug |
| `LogVerbose(MID_LogLevel level, string message, string className = "", string methodName = "")` | Logs if level == Verbose |

### `MID_LogLevel` Enum

| Value | Outputs |
|---|---|
| `None` | Nothing — use in released builds |
| `Error` | Errors only |
| `Info` | Info + warnings + errors ← recommended for production |
| `Debug` | Debug + info + warnings + errors |
| `Verbose` | Everything |

---

## 4. Singletons

**Namespace:** `MidManStudio.Core.Singleton`  
**Files:** `Runtime/Singletons/`

### `MID_Singleton<T>` (abstract class)

Pure C# singleton. No MonoBehaviour, no scene dependency. Safe to use from any thread.

| Member | Type | Description |
|---|---|---|
| `Instance` | `T` | Returns or creates the singleton instance |
| `HasInstance` | `bool` | True if instance has been created |

### `MID_MonoSingleton<T>` (abstract MonoBehaviour)

MonoBehaviour singleton with optional DontDestroyOnLoad.

| Member | Type | Description |
|---|---|---|
| `Instance` | `T` | Returns existing or finds instance in scene |
| `HasInstance` | `bool` | True if a live instance exists |
| `DontDestroyOnLoad` | `bool` | Inspector — persists across scene loads |

> **Warning:** `MID_Singleton<T>.Instance` can create instances. Never call from finalizers (GC thread).

---

## 5. Observable Values

**Namespace:** `MidManStudio.Core.ObservableValues`  
**Files:** `Runtime/ObservableValues/`

### `MID_SusValue<T>`

Generic reactive value container. Fires callbacks only when value actually changes (equality check).

| Member | Type | Description |
|---|---|---|
| `Value` | `T` | Get/set. Fires ValueChanged callbacks only if new value differs |
| `SubscribeToValueChanged(Action<T, T> callback)` | `void` | Callback receives (oldValue, newValue) |
| `SubscribeToAnyUpdate(Action<T> callback)` | `void` | Fires on every `set`, even if value is same |
| `Unsubscribe(Action<T, T> callback)` | `void` | Remove ValueChanged subscription |
| `UnsubscribeFromAnyUpdate(Action<T> callback)` | `void` | Remove AnyUpdate subscription |
| `ClearAllSubscriptions()` | `void` | Remove all subscriptions for this value |

---

### `ManagedSusValue<T>` (extends MID_SusValue\<T\>)

Auto-cleanup on owner destroy. Registered with `SusValueManager` by string ID.

| Member | Type | Description |
|---|---|---|
| Constructor `(T initialValue, string id, GameObject owner)` | — | Registers with SusValueManager |
| `ClearAllForOwner(GameObject owner)` | `static void` | Unregisters and clears all values tied to this owner |

> ⚠ **FIX applied:** Finalizer removed. Was calling `SusValueManager.Instance` from GC thread (unsafe).  
> Cleanup is now exclusively via `OnDestroy` → `ClearAllForOwner`.

---

### `SusValueManager` (MonoSingleton)

Registry for ManagedSusValues. Handles cleanup when owners are destroyed.

| Method | Returns | Description |
|---|---|---|
| `RegisterValue(string id, object value)` | `void` | Called by ManagedSusValue constructor |
| `UnregisterValue(string id)` | `void` | Called by ClearAllForOwner |
| `GetValue<T>(string id)` | `ManagedSusValue<T>` | Retrieve a registered value by ID |

---

## 6. Events

**Namespace:** `MidManStudio.Core.Events`  
**Files:** `Runtime/Events/`

### `MID_GameEventSO` (ScriptableObject)

Inspector-wired event channel. Create via `Right-click > MidManStudio > Utilities > Game Event`.

| Method | Description |
|---|---|
| `Raise()` | Notifies all registered listeners |
| `RegisterListener(MID_GameEventListener listener)` | Called automatically by listener on OnEnable |
| `UnregisterListener(MID_GameEventListener listener)` | Called automatically by listener on OnDisable |

---

### `MID_GameEventListener` (MonoBehaviour)

| Inspector Field | Type | Description |
|---|---|---|
| `GameEvent` | `MID_GameEventSO` | The event to listen to |
| `Response` | `UnityEvent` | Invoked when event is raised |

| Method | Description |
|---|---|
| `OnEventRaised()` | Invokes Response — also callable directly from code |

---

### `MID_DelayedGameEventListener` (MonoBehaviour)

Same as MID_GameEventListener but fires Response after a tick-based delay.

| Inspector Field | Type | Description |
|---|---|---|
| `GameEvent` | `MID_GameEventSO` | The event to listen to |
| `Delay` | `float` | Delay in seconds before response fires |
| `TickRate` | `TickRate` | Resolution of the delay |
| `Response` | `UnityEvent` | Invoked after delay |

> ⚠ **FIX applied:** `_fireDelayedDelegate` is now cached in `Awake()`.  
> Previous code: `MID_TickDelay.After(_delay, FireDelayed, _tickRate)` allocated a delegate per-raise.

---

### `MID_TypedEventBus` (static class)

Generic in-memory event bus. No ScriptableObject required. Keyed by event type `T`.

| Method | Returns | Description |
|---|---|---|
| `Subscribe<T>(Action<T> handler)` | `void` | Register a handler for event type T |
| `Unsubscribe<T>(Action<T> handler)` | `void` | Remove handler |
| `Publish<T>(T eventData)` | `void` | Fire all handlers for type T |
| `Clear<T>()` | `void` | Remove all handlers for type T |

```csharp
// Define event struct in your game code:
public struct PlayerDiedEvent { public int PlayerId; }

// Subscribe:
MID_TypedEventBus.Subscribe<PlayerDiedEvent>(OnPlayerDied);

// Publish:
MID_TypedEventBus.Publish(new PlayerDiedEvent { PlayerId = 1 });
```

---

## 7. Pool System

**Namespace:** `MidManStudio.Core.Pools`  
**Files:** `Runtime/PoolSystems/`

Pool type enums are **code-generated** — see [Editor Tools §18](#18-editor-tools).  
Generated files live in `Runtime/PoolSystems/Generated/`.

---

### `LocalObjectPool` (MonoSingleton)

Main GameObject pool. Chains to `LocalParticlePool` on `CallInitializePool()`.

| Method | Returns | Description |
|---|---|---|
| `CallInitializePool()` | `void` | Initialize all registered types — call once at game start |
| `GetObject(PoolableObjectType type, Vector3 position, Quaternion rotation)` | `GameObject` | Spawn from pool; activates and positions the object |
| `ReturnObject(GameObject obj, PoolableObjectType type)` | `void` | Return to pool; deactivates the object |
| `AddType(PoolableObjectType type, GameObject prefab, int initialSize, int maxSize)` | `void` | Register a new type at runtime |
| `IsRegistered(PoolableObjectType type)` | `bool` | Check if type has a pool entry |
| `GetPoolSize(PoolableObjectType type)` | `int` | Total capacity (active + inactive) |
| `GetActiveCount(PoolableObjectType type)` | `int` | Currently active object count |

> ⚠ `int typeId` overloads listed in previous catalog **do not exist** — removed.  
> Only `PoolableObjectType` enum overloads are implemented.

---

### `LocalParticlePool` (MonoSingleton)

ParticleSystem pool. Initialized by `LocalObjectPool.CallInitializePool()`.

| Method | Returns | Description |
|---|---|---|
| `CallInitializePool()` | `void` | Initialize all registered particle types |
| `GetParticle(PoolableParticleType type, Vector3 position, Quaternion rotation)` | `ParticleSystem` | Spawn particle from pool |
| `ReturnParticle(ParticleSystem ps, PoolableParticleType type)` | `void` | Return particle to pool |
| `IsRegistered(PoolableParticleType type)` | `bool` | Check if type has a particle pool entry |

---

### `TrailRendererPool` (MonoSingleton)

Dedicated pool for TrailRenderers — requires separate management because clearing a trail takes a frame.

| Method | Returns | Description |
|---|---|---|
| `GetTrail(Vector3 startPosition)` | `TrailRenderer` | Spawn trail from pool, positioned at start |
| `ReturnTrail(TrailRenderer trail)` | `void` | Clears trail data and returns to pool |

---

### `LocalPoolReturn` (MonoBehaviour)

Auto-added to spawned objects by `LocalObjectPool`. Can also be called manually.

| Member | Type | Description |
|---|---|---|
| `PoolType` | `PoolableObjectType` | Set by pool on spawn — do not reassign |
| `ReturnToPool()` | `void` | Returns this object to the pool |

---

### `IPoolable` (interface)

Implement on any component attached to a pooled prefab.

| Method | Description |
|---|---|
| `OnSpawn()` | Called when object is retrieved from pool |
| `OnReturn()` | Called just before object is returned to pool |

---

### `PoolableObjectType` (generated enum)

**File:** `Runtime/PoolSystems/Generated/PoolableObjectType.cs`  
**Namespace:** `MidManStudio.Core.Pools`

Generated by Pool Type Generator. Do not edit manually — re-run generator after changes.  
Values are stabilised by `Assets/MidManStudio/Generated/Pools/PoolTypeLock.json`.

---

### `PoolableParticleType` (generated enum)

**File:** `Runtime/PoolSystems/Generated/PoolableParticleType.cs`  
**Namespace:** `MidManStudio.Core.Pools`

Same generation rules as `PoolableObjectType`.

---

### Pool Type Provider SOs

| SO | Create via | Purpose |
|---|---|---|
| `PoolableObjectTypeProviderSO` | Right-click > MidManStudio > Utilities > Pool Object Type Provider | List of GameObject pool types for one package/priority block |
| `PoolableParticleTypeProviderSO` | Right-click > MidManStudio > Utilities > Pool Particle Type Provider | List of ParticleSystem pool types for one package/priority block |
| `PoolTypeGeneratorSettingsSO` | Right-click > MidManStudio > Utilities > Pool Type Generator Settings | Root SO — holds all providers, controls output paths |

**Priority blocks:**

| Priority range | Reserved for |
|---|---|
| 0–99 | `com.midmanstudio.utilities` |
| 100–199 | `com.midmanstudio.projectilesystem` |
| 200+ | Game code |

---

## 8. Audio

**Namespace:** `MidManStudio.Core.Audio`  
**Files:** `Runtime/Audio/`

---

### `MID_AudioManager` (MonoSingleton)

Music crossfade/pitch control + SFX clip-name dispatch.

| Method/Event | Returns | Description |
|---|---|---|
| `PlayMusic(string clipName, float fadeDuration = 1f)` | `void` | Crossfade to named music clip |
| `StopMusic(float fadeDuration = 1f)` | `void` | Fade out current music |
| `SetMusicEnabled(bool enabled)` | `void` | Toggle music on/off; persisted in PlayerPrefs |
| `SetSFXEnabled(bool enabled)` | `void` | Toggle SFX on/off; persisted in PlayerPrefs |
| `SetMusicVolume(float volume)` | `void` | 0–1 master music volume |
| `SetSFXVolume(float volume)` | `void` | 0–1 master SFX volume |
| `PlaySFX(string clipName)` | `void` | Play named SFX clip at default pitch |
| `PlaySFXPitched(string clipName, float pitch)` | `void` | Play named SFX clip at given pitch |
| `event Action<bool> OnMusicEnabledChanged` | — | Fired when music enabled state changes |
| `event Action<bool> OnSFXEnabledChanged` | — | Fired when SFX enabled state changes |

---

### `MID_AudioLimiter` (MonoBehaviour)

DSP peak limiter on the final mixed output.  
**Attach to the same GameObject as the `AudioListener`.**

| Platform | Implementation |
|---|---|
| Windows / macOS / Android / Linux | Rust DSP via native plugin — zero managed allocation in DSP path |
| WebGL | Pure C# `OnAudioFilterRead` fallback — same threshold/attack/release behaviour |

| Inspector Field | Type | Description |
|---|---|---|
| `Threshold` | `float` | Peak limit in linear gain (default: 0.95) |
| `AttackMs` | `float` | Attack time in milliseconds |
| `ReleaseMs` | `float` | Release time in milliseconds |

---

### `MID_NativeAudioBridge` (MonoSingleton)

16-voice `AudioSource` steal pool. Circular voice stealing — oldest voice is reused when all 16 are active.  
Accepts any AudioClip load type — `Decompress On Load` is **not** required.

| Method | Returns | Description |
|---|---|---|
| `PlayClip(int clipIndex, float volume = 1f)` | `void` | Play clip at index in inspector list; steals oldest voice if full |
| `PlayClipAt(int clipIndex, Vector3 position, float volume = 1f)` | `void` | Positional one-shot |
| `StopAll()` | `void` | Stop all 16 voices immediately |

---

### `MID_AudioLibrarySO` (ScriptableObject)

**File:** `Runtime/Audio/MID_AudioLibrarySO.cs`  ← ⚠ FIX: moved from `Libraries/Configs/`  
**Namespace:** `MidManStudio.Core.Audio`

Named clip registry. Assign to `MID_AudioManager` in inspector.

| Member | Type | Description |
|---|---|---|
| `musicClips` | `List<AudioClip>` | Inspector list of music clips |
| `sfxClips` | `List<AudioClip>` | Inspector list of SFX clips |
| `GetMusicClip(string name)` | `AudioClip` | Lookup by clip name |
| `GetSFXClip(string name)` | `AudioClip` | Lookup by clip name |

---

## 9. FX System

**Namespace:** `MidManStudio.Core.FX` ← ⚠ FIX: was `MidManStudio.Core.Audio`  
**Files:** `Runtime/FXSystems/`

Unified CPU particle + audio effect system.  
All `ParticleSystem` objects must have **Simulation Space = World**.

---

### `GlobalFXManager` (MonoSingleton)

**Namespace:** `MidManStudio.Core.FX`

| Method | Returns | Description |
|---|---|---|
| `TriggerImpact(EffectType type, Vector3 position, Vector3 normal)` | `void` | Play impact effect at surface hit |
| `TriggerImpact(Vector3 position, Vector3 normal)` | `void` | Overload — uses default impact type |
| `TriggerMuzzleFlash(EffectType type, Vector3 position, Vector3 direction)` | `void` | Play muzzle flash effect |
| `TriggerMuzzleFlash(Vector3 position, Vector3 direction)` | `void` | Overload — uses default muzzle type |
| `EjectShell(EffectType type, Vector3 position, Vector3 velocity)` | `void` | Eject shell casing effect |
| `EjectShell(Vector3 position, Vector3 velocity)` | `void` | Overload — uses default shell type |
| `TriggerEffect(EffectCategory category, EffectType type, Vector3 position, Quaternion rotation)` | `void` | Generic trigger for any registered category/type pair |

| Inspector Field | Type | Description |
|---|---|---|
| `FxEntries` | `List<FXEntry>` | All registered effect bindings |
| `AudioBridge` | `MID_NativeAudioBridge` | Optional — for per-category audio |

---

### `FXEntry` (serializable class)

| Field | Type | Description |
|---|---|---|
| `category` | `EffectCategory` | Which category this entry belongs to |
| `type` | `EffectType` | Specific type within category |
| `particleSystem` | `ParticleSystem` | In-scene PS reference — must be World space |
| `audioClip` | `AudioClip` | Optional clip played via AudioBridge |

---

### `EffectCategory` (generated enum)

**File:** `Runtime/FXSystems/Generated/EffectCategory.cs`  
**Namespace:** `MidManStudio.Core.FX`

Generated by FX Type Generator. Provides high-level grouping (Impact, MuzzleFlash, ShellEject, etc.).

---

### `EffectType` (generated enum)

**File:** `Runtime/FXSystems/Generated/EffectType.cs`  
**Namespace:** `MidManStudio.Core.FX`

Generated by FX Type Generator. Provides specific variants (MetalSurface, ConcreteImpact, MediumMuzzle, BrassShell, etc.).

### FX Type Provider SOs

| SO | Create via | Purpose |
|---|---|---|
| `EffectCategoryProviderSO` | Right-click > MidManStudio > Utilities > FX Category Provider | Category list for one package/priority block |
| `EffectTypeProviderSO` | Right-click > MidManStudio > Utilities > FX Type Provider | Type list for one package/priority block |

**Priority blocks:**

| Priority | Reserved for |
|---|---|
| 0–9 | `com.midmanstudio.utilities` |
| 10–99 | `com.midmanstudio.projectilesystem` |
| 100+ | Game code |

---

## 10. Timers

**Namespace:** `MidManStudio.Core.Timers`  
**Files:** `Runtime/Timers/`

All timers are plain C# classes — no MonoBehaviour. Drive them with `MID_TickDispatcher` or `Update`.

---

### `MID_CountdownTimer`

| Member | Type | Description |
|---|---|---|
| `Start(float duration)` | `void` | Begin countdown from duration seconds |
| `Pause()` | `void` | Pause without reset |
| `Resume()` | `void` | Resume from paused time |
| `Reset()` | `void` | Stop and reset to duration |
| `Tick(float deltaTime)` | `void` | Advance timer — call from dispatcher or Update |
| `TimeRemaining` | `float` | Seconds left |
| `IsRunning` | `bool` | True if counting |
| `IsComplete` | `bool` | True if reached zero |
| `event Action OnComplete` | — | Fired once when timer hits zero |
| `event Action<float> OnTick` | — | Fired every Tick call with remaining time |

---

### `MID_Stopwatch`

| Member | Type | Description |
|---|---|---|
| `Start()` | `void` | Begin elapsed time tracking |
| `Stop()` | `void` | Halt without reset |
| `Reset()` | `void` | Stop and zero out |
| `Tick(float deltaTime)` | `void` | Advance — call from dispatcher or Update |
| `ElapsedTime` | `float` | Seconds elapsed since Start |
| `IsRunning` | `bool` | True if running |

---

### `MID_InterpolationTimer`

| Member | Type | Description |
|---|---|---|
| `Start(float duration, AnimationCurve curve = null)` | `void` | Begin 0→1 interpolation over duration |
| `Tick(float deltaTime)` | `void` | Advance — call from dispatcher or Update |
| `Progress` | `float` | 0–1, evaluated through optional AnimationCurve |
| `IsComplete` | `bool` | True when Progress reaches 1 |
| `event Action<float> OnProgress` | — | Fired every Tick with current Progress |
| `event Action OnComplete` | — | Fired once at completion |

---

### `MID_SteppedTimer`

| Member | Type | Description |
|---|---|---|
| `Start(float interval, int steps)` | `void` | Begin N-step timer firing every interval seconds |
| `Tick(float deltaTime)` | `void` | Advance — call from dispatcher or Update |
| `CurrentStep` | `int` | Step index (0 to steps-1) |
| `IsComplete` | `bool` | True after all steps fired |
| `event Action<int> OnStep` | — | Fired on each step with step index |
| `event Action OnComplete` | — | Fired after final step |

---

### `MID_NetworkTimer` (NGO-compatible)

Synced timer backed by a `NetworkVariable<double>`. Server-authoritative.

| Member | Type | Description |
|---|---|---|
| `StartSynced(float duration)` | `void` | Server only — begin synced countdown |
| `SyncedTime` | `double` | NetworkVariable-backed remaining time |
| `IsComplete` | `bool` | True when SyncedTime <= 0 |
| `event Action OnServerComplete` | — | Server only — fired at completion |

---

## 11. Library System

**Namespace:** `MidManStudio.Core.Libraries`  
**Files:** `Runtime/Libraries/`

Keyed ScriptableObject asset registry. Retrieve any SO by library name + item name.

---

### `MID_LibraryRegistry` (MonoSingleton)

| Method | Returns | Description |
|---|---|---|
| `GetItem<T>(string libraryId, string itemId)` | `T` | Retrieve typed item by library + item string ID |
| `GetLibrary(string libraryId)` | `MID_LibrarySO` | Retrieve the whole library SO |
| `GetAllItems<T>(string libraryId)` | `List<T>` | All items in a library cast to T |
| `HasItem(string libraryId, string itemId)` | `bool` | Check existence without throwing |

---

### `MID_LibrarySO` (ScriptableObject)

Create via `Right-click > MidManStudio > Utilities > Library`

| Field | Type | Description |
|---|---|---|
| `libraryId` | `string` | Unique string key for this library |
| `items` | `List<MID_LibraryItemSO>` | All items in the library |

---

### `MID_LibraryItemSO` (abstract ScriptableObject)

Base class for all library items.

| Field | Type | Description |
|---|---|---|
| `itemId` | `string` | Unique string key within the library |

---

### `MID_BasicLibraryItemSO` (extends MID_LibraryItemSO)

Create via `Right-click > MidManStudio > Utilities > Library Item (Basic)`

| Field | Type | Description |
|---|---|---|
| `displayName` | `string` | Human-readable name |
| `icon` | `Sprite` | Item icon |
| `description` | `string` | Item description |

---

### `LibraryId` (generated enum)

**File:** `Runtime/Libraries/Generated/LibraryId.cs` ← ⚠ FIX: was `Libraries/Generator/`  
**Namespace:** `MidManStudio.Core.Libraries`

---

### `LibraryItemId` (generated enum)

**File:** `Runtime/Libraries/Generated/LibraryItemId.cs` ← ⚠ FIX: was `Libraries/Generator/`  
**Namespace:** `MidManStudio.Core.Libraries`

---

## 12. Scene Management

**Namespace:** `MidManStudio.Core.SceneManagement`  
**Files:** `Runtime/SceneManagement/`

---

### `MID_SceneManager` (MonoSingleton)

| Method/Event | Returns | Description |
|---|---|---|
| `LoadScene(SceneId id, bool additive = false)` | `void` | Synchronous scene load |
| `LoadSceneAsync(SceneId id, bool additive = false)` | `AsyncOperation` | Async load with progress tracking |
| `UnloadScene(SceneId id)` | `void` | Unload additively loaded scene |
| `GetCurrentSceneId()` | `SceneId` | Returns enum for the active scene |
| `event Action<SceneId> OnSceneLoaded` | — | Fired after scene finishes loading |
| `event Action<SceneId> OnSceneUnloaded` | — | Fired after scene is unloaded |

---

### `MID_SceneTransitionController` (MonoBehaviour)

| Inspector Field | Type | Description |
|---|---|---|
| `FadeInDuration` | `float` | Seconds to fade in on load |
| `FadeOutDuration` | `float` | Seconds to fade out before unload |
| `FadeColor` | `Color` | Colour of the fade overlay |

| Method | Description |
|---|---|
| `TriggerTransition(SceneId targetScene)` | Fade out → load → fade in |

---

### `SceneId` (generated enum)

**File:** `Runtime/SceneManagement/Generated/SceneId.cs`  
Reflects scenes in the current Build Settings.

---

### `SceneRegistry` (generated static class)

**File:** `Runtime/SceneManagement/Generated/SceneRegistry.cs`

| Method | Returns | Description |
|---|---|---|
| `GetBuildIndex(SceneId id)` | `int` | Build index for scene |
| `GetSceneName(SceneId id)` | `string` | Scene name string |

---

## 13. UI State System

**Namespace:** `MidManStudio.Core.UIState`  
**Files:** `Runtime/UIState/`

Stack-based `[Flags]` enum state machine for UI panels.
Each context (e.g. MainMenu, HUD, Pause) has its own independent manager + generated enum.

---

### `MID_UIStateManager` (MonoBehaviour)

One per screen context. Drives show/hide of all visibility components in its context.

| Member | Type | Description |
|---|---|---|
| `ContextName` | `string` | Inspector — must match the generated context name |
| `ChangeState(int state)` | `void` | Push state onto stack; fires all visibility updates |
| `PushState(int state)` | `void` | Same as ChangeState — explicit push |
| `PopState()` | `void` | Remove top state and return to previous |
| `GoBack()` | `void` | Alias for PopState |
| `GetCurrentState()` | `int` | Current state flags value |
| `event Action<int> OnStateChanged` | — | Fired on every state change with new state |

---

### `MID_UIStateContext` (ScriptableObject)

Create via `Right-click > MidManStudio > Utilities > UI State Context`

| Field | Type | Description |
|---|---|---|
| `contextName` | `string` | Name used as enum class name (e.g. "Menu" → `MenuUIState`) |
| `stateNames` | `List<string>` | Each entry becomes a `[Flags]` enum value |

---

### `MID_UIStateVisibility` (MonoBehaviour)

| Inspector Field | Type | Description |
|---|---|---|
| `Manager` | `MID_UIStateManager` | The state manager this element listens to |
| `VisibleInStates` | `int` | Show when current state matches any of these flags |
| `InvisibleInStates` | `int` | Hide when current state matches any of these flags |

| Method | Description |
|---|---|
| `Refresh(int currentState)` | Called by MID_UIStateManager — also callable manually |

---

### `MID_UIStateButton` (MonoBehaviour)

| Inspector Field | Type | Description |
|---|---|---|
| `Manager` | `MID_UIStateManager` | The state manager to push to |
| `TargetState` | `int` | State value to push on click |

| Method | Description |
|---|---|
| `OnClick()` | Calls `Manager.ChangeState(TargetState)` — wire to Button.onClick |

---

### `MID_UIStateBackButton` (MonoBehaviour) ← ⚠ FIX: was missing from catalog

Combines GoBack navigation with state-based visibility (only visible in certain states).

| Inspector Field | Type | Description |
|---|---|---|
| `Manager` | `MID_UIStateManager` | The state manager to pop |
| `VisibleInStates` | `int` | Button is only interactable/visible in these states |

| Method | Description |
|---|---|
| `OnClick()` | Calls `Manager.GoBack()` — wire to Button.onClick |
| `Refresh(int currentState)` | Called by manager on state change to update visibility |

---

### Generated State Enums

**File:** `Runtime/UIState/Generated/{Context}UIState.cs`

```csharp
// Generated from MID_UIStateContext SO with contextName = "Menu"
// and stateNames = ["MainMenu", "Settings", "Credits"]
[Flags]
public enum MenuUIState
{
    None      = 0,
    MainMenu  = 1,
    Settings  = 2,
    Credits   = 4
}
```

---

## 14. UI Components

**Namespace:** `MidManStudio.Core.UI`  
**Files:** `Runtime/UI/`

---

### `MID_UIElement` (MonoBehaviour)

CanvasGroup-based animated show/hide base component. No tween library dependency.
State-system visibility components inherit from this.

| Member | Type | Description |
|---|---|---|
| `Show(bool instant = false)` | `void` | Fade in CanvasGroup; propagates to children if enabled |
| `Hide(bool instant = false)` | `void` | Fade out CanvasGroup; propagates to children if enabled |
| `Toggle(bool instant = false)` | `void` | Switch between show/hide |
| `IsVisible` | `bool` | True if alpha > 0 and interactable |

| Inspector Field | Type | Description |
|---|---|---|
| `ShowDuration` | `float` | Fade in duration in seconds |
| `HideDuration` | `float` | Fade out duration in seconds |
| `AnimationCurve` | `AnimationCurve` | Optional alpha curve override |
| `PropagateToChildren` | `bool` | If true, Show/Hide cascades to child MID_UIElements |

---

## 15. Helper Functions

**Namespace:** `MidManStudio.Core.HelperFunctions`  
**File:** `Runtime/HelperFunctions/MID_HelperFunctions.cs`  
**Type:** Static class

---

### `MID_HelperFunctions`

**Transform / GameObject**

| Method | Returns | Description |
|---|---|---|
| `DestroyObjChildren(Transform holder)` | `void` | ⚠ FIX: was `KillObjChildren` — destroys all child GameObjects |
| `DestroyMultipleParentsChildren(List<Transform> holders)` | `void` | ⚠ FIX: was `KillMultipleParentsChildren` — destroys children across multiple parents |
| `GetOrAddComponent<T>(GameObject go)` | `T` | Returns existing component or adds one |
| `FindDeepChild(Transform parent, string name)` | `Transform` | Recursive child search by name |
| `SetLayerRecursively(GameObject go, int layer)` | `void` | Set layer on GameObject and all descendants |
| `IsInLayerMask(GameObject go, LayerMask mask)` | `bool` | Check if GameObject's layer is in a LayerMask |

**Math / Angle**

| Method | Returns | Description |
|---|---|---|
| `ClampAngle(float angle, float min, float max)` | `float` | Clamp an angle (handles 360° wrap-around) |

**Serialization**

| Method | Returns | Description |
|---|---|---|
| `ToJson<T>(T obj)` | `string` | Serialize to JSON via JsonUtility |
| `FromJson<T>(string json)` | `T` | Deserialize from JSON via JsonUtility |

**Reflection**

| Method | Returns | Description |
|---|---|---|
| `GetAllSubclasses<T>()` | `List<Type>` | Find all non-abstract subclasses of T in loaded assemblies |

---

## 16. Sequential Processing

**Namespace:** `MidManStudio.Core.SequentialProcessing`  
**Files:** `Runtime/SequentialProcessing/`

Priority-lane async task runner. Tasks execute one at a time, ordered by priority.
Supports configurable retry with delay on failure.

---

### `MID_SequentialRunner` (MonoSingleton)

| Method | Returns | Description |
|---|---|---|
| `Enqueue(MID_SequentialTask task, int priority = 0)` | `TaskHandle` | Add task to queue; higher priority = executes first |
| `Cancel(TaskHandle handle)` | `void` | Remove task if not yet started |
| `CancelAll()` | `void` | Clear entire queue (running task completes) |
| `IsProcessing` | `bool` | True if a task is currently executing |
| `QueueCount` | `int` | Number of tasks waiting |

---

### `MID_SequentialTask` (abstract class)

| Member | Type | Description |
|---|---|---|
| `Execute()` | `abstract IEnumerator` | Override with task coroutine logic |
| `OnSuccess()` | `virtual void` | Called on completion — override to handle success |
| `OnFailure()` | `virtual void` | Called after all retries exhausted |
| `MaxRetries` | `int` | Number of retry attempts on failure (default: 0) |
| `RetryDelay` | `float` | Seconds between retries |

---

## 17. Sticky Note

**Namespace:** `MidManStudio.Core.Notes`  
**File:** `Runtime/StickyNote/MID_StickyNote.cs`  
**Type:** MonoBehaviour

In-Game-View overlay rendered in both Edit Mode and Play Mode.
Attach to any scene GameObject for scene setup documentation or tutorial callouts.

| Inspector Field | Type | Description |
|---|---|---|
| `Title` | `string` | Bold header text |
| `Notes` | `List<string>` | Bullet list of notes |
| `TextFile` | `TextAsset` | Optional .txt file; overrides Notes list if assigned |
| `Theme` | `enum` | Yellow, Blue, Green, Pink, Dark |
| `AnchorPosition` | `enum` | TopLeft, TopRight, BottomLeft, BottomRight, Center |

**Runtime behaviour:** Drag to reposition. Minimize button collapses to title bar. Close button hides (does not destroy).

**Edit-mode build safety (2026-07-03):**
- The auto-created `EventSystem` is now reference-counted across every `MID_StickyNote`
  instance that uses it, and only destroyed once none remain. A pre-existing user-placed
  `EventSystem` is never touched. Fixes duplicate EventSystems accumulating in the scene.
- Building the Canvas hierarchy from `OnEnable()` in edit mode is now deferred one editor
  tick via `EditorApplication.delayCall`, avoiding Unity's "SendMessage cannot be called
  during Awake, CheckConsistency, or OnValidate" warning. Play Mode is unaffected — it stays
  synchronous.

---

## 18. Editor Tools

All tools open via the `MidManStudio > Utilities` menu.

---

### Logger Manager

`MidManStudio > Utilities > Logger Manager`

Scans scene for all MonoBehaviours with a `MID_LogLevel` field.  
Allows bulk-setting levels — useful before a build or profiling session.

---

### Pool Type Generator

`MidManStudio > Utilities > Pool Type Generator`

Writes `Runtime/PoolSystems/Generated/PoolableObjectType.cs` and `PoolableParticleType.cs`.

**Workflow:**
1. Create `PoolableObjectTypeProviderSO` (right-click menu) for your package
2. Set `packageId`, `priority` (≥100 for game code), add entry names in PascalCase
3. Assign provider to `PoolTypeGeneratorSettingsSO`
4. Click **Generate Now**

**Pinning entries (prevent offset shifts):**
```
entryName      = "BossEnemy"
explicitOffset = 5        // always blockStart + 5 regardless of list order
```

Unpinned entries are stabilised by `Assets/MidManStudio/Generated/Pools/PoolTypeLock.json`.

---

### Library Type Generator

`MidManStudio > Utilities > Library Type Generator`

Writes `Runtime/Libraries/Generated/LibraryId.cs` and `LibraryItemId.cs`.

---

### Scene Type Generator

`MidManStudio > Utilities > Scene Type Generator`

Reads current Build Settings → writes `Runtime/SceneManagement/Generated/SceneId.cs` and `SceneRegistry.cs`.  
Re-run every time scenes are added/removed from build settings.

---

### UI State Context Generator

`MidManStudio > Utilities > UI State Context Generator`

Reads `MID_UIStateContext` SOs → writes `Runtime/UIState/Generated/{Context}UIState.cs` files.  
One file per context.

---

### Effect Type Generator

`MidManStudio > Utilities > Effect Type Generator`

Writes `Runtime/FXSystems/Generated/EffectCategory.cs` and `EffectType.cs`.  
Stabilised by `Assets/MidManStudio/Generated/FX/EffectTypeLock.json`.

---

### Script Utilities

`MidManStudio > Utilities > Script Utilities`

Misc helpers: create MonoSingleton from template, strip comments, namespace renamer.

---

### Scene Dependency Injector

**Component:** `SceneDependencyInjector` (MonoBehaviour, Editor assembly)

Add to any scene GameObject. Checks for required manager singletons on play; instantiates any that are missing.  
Eliminates the need for a dedicated bootstrap scene during isolated scene testing.

| Inspector Field | Type | Description |
|---|---|---|
| `Required Dependencies` | `List<GameObject>` | Prefabs to instantiate if their singleton is not found in scene |

---

### Auto Reference Window

`MidManStudio > Utilities > Auto Reference`

Bulk scan/resolve tool for the Auto Reference system — see [§19](#19-auto-reference) for
the full attribute/component/resolver reference. Scans open scenes for
`[MID_AutoRefable]` scripts, bulk-adds `MID_AutoRef` to objects missing it (manually or
automatically on scan, off by default), and bulk-resolves with a color-coded results log.

---

### Benchmarks

| Tool | Menu path | What it tests |
|---|---|---|
| Tick Delay Benchmark | `MidManStudio > Utilities > Tests > Tick Delay Bench` | Allocation profile + timing accuracy of MID_TickDelay |
| Tick Dispatcher Benchmark | `MidManStudio > Utilities > Tests > Tick Dispatcher Bench` | Subscriber overhead at each TickRate |
| Audio Benchmark | `MidManStudio > Utilities > Tests > Audio Bench` | MID_NativeAudioBridge voice steal performance |

---

## 19. Auto Reference

**Namespace:** `MidManStudio.Core.AutoReference` (runtime) / `MidManStudio.Core.EditorUtils.AutoReference` (editor)  
**Files:** `Runtime/AutoReference/*`, `Editor/AutoReference/*`  
**Type:** Attribute + static resolver + MonoBehaviour + EditorWindow

Auto-fills a MonoBehaviour's Component/GameObject/interface reference fields by searching
self, children, and (optionally) an external search root — no attribute required per
field. Multi-candidate fields are disambiguated by fuzzy name match against the field
name. Pure reflection, zero external dependencies.

### Attributes

| Attribute | Target | Description |
|---|---|---|
| `[MID_AutoRefable(bool autoAddComponent = false)]` | Class | Opts a MonoBehaviour into resolver scanning. `autoAddComponent: true` makes the editor auto-add `MID_AutoRef` to any GameObject that receives this script. |
| `[MID_NoAutoRef]` | Field | Opts a single field out of scanning — never touched regardless of type match. |

### `MID_AutoRefOptions`

`Runtime/AutoReference/MID_AutoRefOptions.cs` — `[Serializable]`, shared by `MID_AutoRef` and the bulk window.

| Field | Type | Default | Description |
|---|---|---|---|
| `includeChildren` | `bool` | `true` | Search children recursively |
| `includeInactiveChildren` | `bool` | `true` | Include inactive children in the search |
| `includeExternalRoot` | `bool` | `false` | Also search a detached hierarchy (e.g. an unparented Canvas) |
| `externalSearchRoot` | `Transform` | `null` | Root searched when `includeExternalRoot` is on |
| `overwriteExisting` | `bool` | `false` | Overwrite fields that already have a value — off by default, never clobbers manual edits |
| `runMode` | `MID_AutoRefRunMode` | `Manual` | `Manual` / `Awake` / `Start` / `OnValidate` |
| `logUnresolved` | `bool` | `true` | Log a warning for fields with zero candidates |
| `logAmbiguousResolved` | `bool` | `true` | Log a line whenever a multi-candidate field is resolved by name match |

### `MID_AutoReferenceResolver`

`Runtime/AutoReference/MID_AutoReferenceResolver.cs` — static.

| Method | Returns | Description |
|---|---|---|
| `Resolve(GameObject target, MID_AutoRefOptions options)` | `List<MID_AutoRefFieldResult>` | Resolves every `[MID_AutoRefable]` script on `target`. Safe in edit or play mode. |
| `IsAutoRefable(Type type)` | `bool` | True if the type (or a base type) carries `[MID_AutoRefable]` |

`MID_AutoRefFieldResult.Outcome` is one of `Assigned`, `AmbiguousResolved`, `NoCandidates`, `SkippedAlreadySet`.

> **Undo/dirtying:** wrapped in `#if UNITY_EDITOR` inside the runtime assembly — same pattern as `MID_Logger`. Skipped automatically in builds and during Play Mode.  
> **Logging:** falls back to plain `Debug.Log`/`LogWarning` in edit mode rather than `MID_Logger`, to avoid waking `MID_Logger`'s auto-instantiating singleton during editor-only operations.

### `MID_AutoRef` (runtime component)

`Runtime/AutoReference/MID_AutoRef.cs`

| Member | Description |
|---|---|
| `Options` | Exposes the instance's `MID_AutoRefOptions` |
| `ResolveNow()` | `[ContextMenu]`-exposed manual resolve — also the entry point used by `Awake`/`Start`/`OnValidate` run modes |

`[DisallowMultipleComponent]` — Unity enforces at most one `MID_AutoRef` per GameObject regardless of how it was added (manual, watcher, or bulk window).

`runMode = OnValidate` resolves automatically whenever the component is added or its own inspector values change — no button needed. The actual work is deferred via `EditorApplication.delayCall` since Unity disallows some operations synchronously inside `OnValidate`.

### `MID_AutoRefComponentWatcher` (editor-only)

`Editor/AutoReference/MID_AutoRefComponentWatcher.cs` — `[InitializeOnLoad]`, hooks `ObjectFactory.componentWasAdded`.

Auto-adds `MID_AutoRef` when a script carrying `[MID_AutoRefable(autoAddComponent: true)]` is added to a GameObject that doesn't already have one. If the resulting component's `runMode` is `OnValidate`, also triggers an initial resolve. Duplicate-safe: existence check + `[DisallowMultipleComponent]` backstop.

### `MID_AutoReferenceWindow`

`MidManStudio > Utilities > Auto Reference` — `Editor/AutoReference/MID_AutoReferenceWindow.cs`, UI Toolkit (UXML/USS).

| Action | Description |
|---|---|
| Scan Scene | Finds every GameObject in open scenes carrying a `[MID_AutoRefable]` script |
| Add Missing Components | Bulk-adds `MID_AutoRef` to scanned targets that don't have one |
| Auto-Add Missing Components on Scan | Toggle, off by default — runs the above automatically after every scan |
| Resolve Selected / Resolve All | Runs the resolver against the selected or full target list, with a color-coded results log (assigned / ambiguous / unresolved) |

### Custom Inspector

`Editor/AutoReference/MID_AutoRefEditor.cs` — adds a green→orange gradient "RESOLVE NOW" button to `MID_AutoRef`'s inspector (mesh-painted, since USS has no gradient support), plus a one-line summary of the last run.

### `MID_NameMatcher`

`Runtime/AutoReference/MID_NameMatcher.cs` — static, no external dependency.

| Method | Returns | Description |
|---|---|---|
| `Score(string fieldName, string candidateName)` | `float` (0–1) | Composite of normalized Levenshtein similarity (45%), camelCase/underscore token Jaccard overlap (35%), and a substring-containment bonus (+0.25) |

Used only when a field has 2+ type-matching candidates. Ties (equal score) resolve to the first candidate found, which follows Unity's `GetComponentsInChildren` depth-first-from-self order.
