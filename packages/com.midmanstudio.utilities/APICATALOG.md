# MidMan Studio Utilities — API Catalog

`com.midmanstudio.utilities` v1.0.0  
Assembly: `MidManStudio.Utilities`  
Namespace root: `MidManStudio.Core`

---

## Table of Contents

1. [Pool System](#1-pool-system)
2. [Pool Type Generator](#2-pool-type-generator)
3. [Tick Dispatcher](#3-tick-dispatcher)
4. [Tick Delay](#4-tick-delay)
5. [Logger](#5-logger)
6. [Singletons](#6-singletons)
7. [Observable Values](#7-observable-values)
8. [Events](#8-events)
9. [Audio](#9-audio)
10. [Timers](#10-timers)
11. [Library System](#11-library-system)
12. [Scene Management](#12-scene-management)
13. [UI State System](#13-ui-state-system)
14. [UI Components](#14-ui-components)
15. [Helper Functions](#15-helper-functions)
16. [Sequential Process Runner](#16-sequential-process-runner)
17. [Editor Tools](#17-editor-tools)
18. [Assembly Definitions](#18-assembly-definitions)

---

## 1. Pool System

Namespace: `MidManStudio.Core.Pools`

### `LocalObjectPool : Singleton<LocalObjectPool>`

Singleton pool manager for non-particle GameObjects.  
Keyed by the generated `PoolableObjectType` enum (or raw `int`).

**Inspector fields**

| Field | Description |
|---|---|
| `poolConfigs` | List of `BasicPoolConfig` — one entry per prefab type |
| `autoRegisterPrewarmCount` | Prewarm count used when auto-registering an unknown type |
| `autoRegisterMaxPoolSize` | Max pool size used when auto-registering |
| `enableAutoRegistration` | If true, unknown types are registered on first access |

**Public API**

```csharp
void CallInitializePool()                     // call once at game start, chains to LocalParticlePool

GameObject GetObject(PoolableObjectType type, Vector3 position, Quaternion rotation)
GameObject GetObject(PoolableObjectType type, Vector2 position, Quaternion rotation)
GameObject GetObject(int typeId, Vector3 position, Quaternion rotation)

void ReturnObject(GameObject obj, PoolableObjectType type)
void ReturnObject(GameObject obj, int typeId)

void AddType(PoolableObjectType type, GameObject prefab, int prewarm = 5, int maxSize = 15)
void AddType(int typeId, GameObject prefab, int prewarm = 5, int maxSize = 15)

bool IsRegistered(PoolableObjectType type)
bool IsRegistered(int typeId)
bool HasBeenInitialized()
void ReturnAllActive()
void ClearPool()
```

---

### `LocalParticlePool : Singleton<LocalParticlePool>`

Singleton pool manager for particle effect GameObjects.  
Initialized automatically by `LocalObjectPool.CallInitializePool()`.

```csharp
void CallInitializePool()

GameObject GetObject(PoolableParticleType type, Vector3 position, Quaternion rotation)
GameObject GetObject(PoolableParticleType type, Vector2 position, Quaternion rotation)
GameObject GetObject(int typeId, Vector3 position, Quaternion rotation)

void ReturnObject(GameObject obj, PoolableParticleType type)
void ReturnObject(GameObject obj, int typeId)

void AddType(PoolableParticleType type, GameObject prefab,
             int prewarm = 10, int maxSize = 30, float lifetime = 5f)

bool IsRegistered(PoolableParticleType type)
bool IsRegistered(int typeId)
bool HasBeenInitialized()
void ClearPool()
```

---

### `LocalPoolReturn : MonoBehaviour`

Auto-returns a pooled object to `LocalObjectPool` after a delay.  
Added automatically by `LocalObjectPool` to every pooled instance.

```csharp
void SetOriginalType(PoolableObjectType type)
void SetAutoReturn(bool enabled)
void SetDuration(float seconds)
void ReturnToPoolNow()
bool IsScheduledForReturn()
float GetDuration()
bool IsAutoReturnEnabled()
PoolableObjectType GetOriginalType()
```

---

### `LocalParticleReturn : MonoBehaviour`

Auto-returns a pooled particle to `LocalParticlePool` after `maxLifetime` seconds.

```csharp
void SetOriginalType(PoolableParticleType type)
void SetMaxLifetime(float seconds)
void ReturnToPool()
void ForceReturn()
PoolableParticleType GetOriginalType()
```

---

### `TrailRendererPool : Singleton<TrailRendererPool>`

Generic slot-based `TrailRenderer` pool for any moving entity.

```csharp
int  Acquire(TrailConfig config, int ownerId = 0)   // returns slot index, -1 if exhausted
void SetPosition(int slot, Vector3 worldPosition)   // call every frame
void Release(int slot)                              // fades naturally then recycles
void ReleaseByOwner(int ownerId)
void ForceRelease(int slot)
bool IsAcquired(int slot)
int  PoolSize { get; }
```

**`TrailConfig` struct**

```csharp
public struct TrailConfig
{
    public Material  Material;
    public Gradient  ColorGradient;
    public float     Time;
    public float     StartWidth;
    public float     EndWidth;
    public int       CapVertices;
    public static TrailConfig Default { get; }
}
```

---

### `UIParticlePoolManager : MonoBehaviour`

Manages UI-layer ParticleSystem effects by string key.

```csharp
void TriggerEffect(string key, int emitCount = 10)
void PlayEffect(string key)
void EmitEffect(string key, int count = 10)
void StopEffect(string key, bool clear = true)
void StopAll(bool clear = true)
bool IsPlaying(string key)
ParticleSystem GetSystem(string key)
void RegisterEffect(UIEffectConfig config)
void UnregisterEffect(string key)
```

---

### `BasicPoolConfig` / `ParticlePoolConfig`

Inspector-visible pool entry. Assign in `LocalObjectPool` / `LocalParticlePool`.

| Field | Description |
|---|---|
| `typeId` | Integer matching a `PoolableObjectType` / `PoolableParticleType` value |
| `displayName` | Inspector label only |
| `prefab` | The prefab to pool |
| `prewarmCount` | Instances pre-created on init |
| `maxPoolSize` | Pool destroys overflow beyond this |
| `defaultLifetime` | (particle only) seconds before auto-return |

---

## 2. Pool Type Generator

Namespace: `MidManStudio.Core.Pools.Generator`  
Assembly: `MidManStudio.Utilities.Editor` (editor only)  
**Open via:** `MidManStudio > Utilities > Pool Type Generator`

---

### `PoolTypeProviderSO : ScriptableObject`

Create via: `MidManStudio > Utilities > Pool Type Provider (Object)`

| Field | Description |
|---|---|
| `packageId` | Unique reverse-domain ID. e.g. `com.mygame` |
| `displayName` | Shown in generator window |
| `priority` | Lower = earlier block. 0 = utilities, 10 = projectile, 100+ = user |
| `entries` | List of `PoolEntryDefinition` |

---

### `ParticlePoolTypeProviderSO : ScriptableObject`

Create via: `MidManStudio > Utilities > Pool Type Provider (Particle)`

Same shape as `PoolTypeProviderSO`. Contributes to `PoolableParticleType`.

---

### `PoolEntryDefinition`

| Field | Type | Description |
|---|---|---|
| `entryName` | `string` | Becomes the enum member name. PascalCase, no spaces |
| `comment` | `string` | Written as `// comment` next to the member |
| `explicitOffset` | `int` | `-1` = auto-assigned. `≥0` = pinned to this offset in the provider's block |

**Pinning** locks the absolute integer value permanently — use for entries referenced by serialised inspector fields.

---

### `PoolTypeGeneratorSettingsSO : ScriptableObject`

Create via: `MidManStudio > Utilities > Pool Type Generator Settings`

| Field | Default |
|---|---|
| `objectEnumOutputPath` | `packages/com.midmanstudio.utilities/Runtime/PoolSystems/Generated/PoolableObjectType.cs` |
| `particleEnumOutputPath` | `packages/com.midmanstudio.utilities/Runtime/PoolSystems/Generated/PoolableParticleType.cs` |
| `lockFilePath` | `Assets/MidManStudio/Generated/Pools/PoolTypeLock.json` |
| `minimumBlockSize` | `100` |
| `generatedNamespace` | `MidManStudio.Core.Pools` |
| `autoGenerateOnAssetChange` | `false` |

**Commit `PoolTypeLock.json` to source control** — it prevents value shifts on regeneration.

---

## 3. Tick Dispatcher

Namespace: `MidManStudio.Core.TickDispatcher`

### `MID_TickDispatcher : MonoBehaviour`

Zero-allocation managed tick dispatcher. Replaces per-MonoBehaviour `Update()`.

```csharp
static bool Subscribe(TickRate tickRate, TickCallback callback)
static bool Unsubscribe(TickRate tickRate, TickCallback callback)
static bool IsSubscribed(TickRate r, TickCallback cb)
static int  GetSubscriberCount(TickRate r)
static bool IsTickRateActive(TickRate r)
static float GetInterval(TickRate r)
static float GetFrequency(TickRate r)
static void ClearSubscribers(TickRate r)
static void ClearAllSubscribers()
static bool IsReady    { get; }
static bool IsQuitting { get; }
```

**Callback signature:** `delegate void TickCallback(float deltaTime)`

**`TickRate` enum — selection guide**

| Member | Interval | Fires/sec | Notes |
|---|---|---|---|
| `Tick_0_01` | 10ms | 100 | ⛔ Never use — fires faster than frame, negative saving |
| `Tick_0_02` | 20ms | 50 | ⚠️ Marginal — only if fps reliably > 50 |
| `Tick_0_05` | 50ms | 20 | Fast minimum — projectiles, weapon systems |
| `Tick_0_1` | 100ms | 10 | ✅ Recommended minimum — fast AI, cooldowns |
| `Tick_0_2` | 200ms | 5 | Standard — enemy AI, ability systems |
| `Tick_0_5` | 500ms | 2 | Slow — area checks, perception |
| `Tick_1` | 1s | 1 | Very slow — health regen, UI numbers |
| `Tick_2` | 2s | 0.5 | Ambient — distant objects |
| `Tick_5` | 5s | 0.2 | Rare — spawners, wave logic |

**Usage pattern:**
```csharp
// Always use static readonly delegate — method group creates new object each call in Unity 2019.2+
private static readonly MID_TickDispatcher.TickCallback _onTick = OnTick;

void OnEnable()  => MID_TickDispatcher.Subscribe(TickRate.Tick_0_2, _onTick);
void OnDisable() => MID_TickDispatcher.Unsubscribe(TickRate.Tick_0_2, _onTick);  // NEVER skip
static void OnTick(float dt) { /* 5x/sec instead of 60x/sec */ }
```

---

### `MID_NativeTickDispatcher : MonoBehaviour`

Burst-compiled native tick dispatcher. Only use for fully data-oriented workloads with 500+ subscribers doing real math.

```csharp
bool Subscribe(TickRate r, NativeTickDelegate callback)
bool Unsubscribe(TickRate r, NativeTickDelegate callback)
bool IsSubscribed(TickRate r, NativeTickDelegate callback)
int  GetSubscriberCount(TickRate r)
void ClearSubscribers(TickRate r)
void ClearAllSubscribers()
```

Rules: method must be `static` + `[BurstCompile]`, must NOT touch managed objects.

---

## 4. Tick Delay

Namespace: `MidManStudio.Core.TickDispatcher`

### `MID_TickDelay` (static class)

Zero-allocation delayed action system built on `MID_TickDispatcher`.

**Minimum rate: `Tick_0_1`** — rates faster than this are automatically clamped with a warning.

**Zero-alloc contract:**
```csharp
// ✗ Allocates every call — method group creates new delegate object in Unity 2019.2+
MID_TickDelay.After(1f, MyMethod);

// ✓ Zero alloc — pre-allocate delegate once as static readonly field
private static readonly Action _onDelay = HandleDelay;
MID_TickDelay.After(1f, _onDelay);
```

**API:**
```csharp
// Schedule once — returns cancellable handle
TickDelayHandle After(float seconds, Action action, TickRate rate = Tick_0_1)

// Repeat N times
TickDelayHandle Repeat(float intervalSeconds, int times, Action action, TickRate rate = Tick_0_1)

// Repeat until cancelled
TickDelayHandle RepeatForever(float intervalSeconds, Action action, TickRate rate = Tick_0_1)

static void Cancel(TickDelayHandle handle)
static void CancelAll()
static int  ActiveCount { get; }
static int  PoolCapacity { get; set; }  // default 64, set before first call
```

**`TickDelayHandle` struct:**
```csharp
bool IsValid { get; }
void Cancel()
```

**Trade-offs vs alternatives:**

| | MID_TickDelay | Coroutine | Task.Delay |
|---|---|---|---|
| GC allocation | 0 B (always) | ~80–400 B per call | ~120–160 B cold, 0 B warm pool |
| Thread | Main | Main | Threadpool — cannot touch Unity |
| IEnumerator | Not needed | Required — breaks RPC signatures | Not needed |
| Timing error | 0–100ms at Tick_0_1 | 0–16ms at 60fps | ~0–2ms (OS timer) |
| Cancellable | Yes (TickDelayHandle) | Yes (StopCoroutine) | Yes (CancellationToken, extra alloc) |

**NGO/Netcode usage:**
```csharp
// Works directly inside ServerRpc — no IEnumerator, no Task, no alloc
[ServerRpc]
private void SpawnPlayerServerRpc(ulong clientId)
{
    MID_TickDelay.After(RespawnDelay, _respawnCallback, TickRate.Tick_0_2);
}
private static readonly Action _respawnCallback = DoRespawn;
private static void DoRespawn() { /* safe — runs on main thread */ }
```

---

## 5. Logger

Namespace: `MidManStudio.Core.Logging`

### `MID_Logger : MonoBehaviour`

Level-gated singleton logger. Prefix tokens coloured in Editor; message body always plain text.

```csharp
static void LogDebug(MID_LogLevel level, string message, string className = "", string method = "")
static void LogInfo(MID_LogLevel level, string message, string className = "", string method = "")
static void LogWarning(MID_LogLevel level, string message, string className = "", string method = "")
static void LogError(MID_LogLevel level, string message, string className = "", string method = "",
                     Exception e = null)
static void LogException(MID_LogLevel level, Exception e, string message = "",
                         string className = "", string method = "")
static void LogVerbose(MID_LogLevel level, string message, string className = "", string method = "")
static bool ShouldLog(MID_LogLevel current, MID_LogLevel messageLevel)
```

### `MID_LogLevel` enum
 None = 0    no output
Error = 1   errors only
Info = 2    info + warnings + errors  ← recommended for production
Debug = 3   debug + info + warnings + errors
Verbose = 4 everything

**Pattern:**
```csharp
[SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;
MID_Logger.LogInfo(_logLevel, "Ready.", nameof(MyClass), nameof(MyMethod));
```

**Editor tool:** `MidManStudio > Utilities > Logger Manager` — bulk-set log levels across all scene MonoBehaviours.

---

## 6. Singletons

Namespace: `MidManStudio.Core.Singleton`

### `Singleton<T> : MonoBehaviour`

```csharp
static T    Instance     { get; }   // auto-creates if missing
static bool HasInstance  { get; }
static T    TryGetInstance()
static T    Current      { get; }
static bool IsAvailable()
static T    GetExistingInstance()
static void Reset()

protected virtual void Awake()
protected virtual void Remake(bool persistAcrossScenes = false)
protected virtual void InitializeSingleton(bool persist)
```

Implement `SingletonLifecycle` interface on subclass for `OnSceneChange` callbacks.

---

### `StaticContentSingleton<T>` where `T : class, new()`

Thread-safe lazy singleton for pure C# classes — no MonoBehaviour, no GameObject.

```csharp
static T    Instance        { get; }   // thread-safe double-checked locking
static bool HasInstance     { get; }
static bool IsInitialized   { get; }
static T    TryGetInstance()
static void Initialize(T instance)    // inject custom or subclass instance
static void Reset()                   // calls Dispose() if T : IDisposable
```

Implement `IStaticSingletonInitializable` on `T` to receive `Initialize()` on first creation.

---

## 7. Observable Values

Namespace: `MidManStudio.Core.ObservableValues`

### `MID_SusValue<T>`

Generic observable value. Fires callbacks on change or on any set attempt.

```csharp
MID_SusValue(T initialValue = default, Func<T, bool> validationFunc = null)

T    Value          { get; set; }   // set triggers callbacks
bool IsValueNull    { get; }

void SetValidationFunction(Func<T, bool> func)
void ClearValidationFunction()

bool SubscribeToValueChanged(OnValueChangedDelegate callback)   // (T old, T new)
bool UnsubscribeFromValueChanged(OnValueChangedDelegate callback)
bool IsSubscribedToValueChanged(OnValueChangedDelegate callback)

bool SubscribeToAnyUpdate(OnAnyUpdateDelegate callback)         // (T value) — fires every set
bool UnsubscribeFromAnyUpdate(OnAnyUpdateDelegate callback)
bool IsSubscribedToAnyUpdate(OnAnyUpdateDelegate callback)

void SetValueSilently(T value)   // bypass callbacks, validation still runs
void ForceNotify()
void ClearAllSubscriptions()
int  GetSubscriberCount()

static implicit operator T(MID_SusValue<T> v)
```

---

### `ManagedSusValue<T> : MID_SusValue<T>, IManagedSusValue`

Extends `MID_SusValue<T>` with automatic registration in `SusValueManager`.

```csharp
ManagedSusValue(T initialValue = default, string id = null,
                GameObject owner = null, Func<T, bool> validationFunc = null)

string ValueId   { get; }
bool   IsManaged { get; }
```

If `owner` is supplied, all subscriptions are cleared automatically when the owner is destroyed.

---

### `SusValueManager : Singleton<SusValueManager>`

```csharp
void RegisterValue(IManagedSusValue value, GameObject owner = null)
void UnregisterValue(string valueId)
void ClearAllForOwner(GameObject owner)
void ClearAll()
bool IsRegistered(string valueId)
int  RegisteredCount { get; }
```

---

## 8. Events

Namespace: `MidManStudio.Core.Events`

### `MID_GameEvent : ScriptableObject`

Create via: `MidManStudio > Utilities > Game Event`

```csharp
void Raise()
void Register(MID_GameEventListener listener)
void Deregister(MID_GameEventListener listener)
int  ListenerCount { get; }
```

### `MID_GameEventListener : MonoBehaviour`

Attach to any GameObject. Self-registers/deregisters on Enable/Disable.

```csharp
public MID_GameEvent _gameEvent;    // assign in inspector
public UnityEvent    _onResponse;   // fires when event is raised
public virtual void  OnEventRaised()
public void          RaiseEvent()
```

### `MID_DelayedGameEventListener : MID_GameEventListener`

Fires an immediate response then a delayed response via `MID_TickDelay`.

```csharp
[SerializeField] private float      _delay;
[SerializeField] private TickRate   _tickRate;
[SerializeField] private UnityEvent _delayedResponse;
```

### `MID_EventBus<T>` where `T : IMIDEvent` (static)

Typed global event bus. One channel per event type.

```csharp
static void Subscribe(Action<T> handler)
static void Unsubscribe(Action<T> handler)
static void Raise(T payload)
static void ClearAll()
static int  SubscriberCount { get; }
static MID_LogLevel LogLevel   // set per channel
```

```csharp
// Define event payload
public struct PlayerDiedEvent : IMIDEvent { public ulong PlayerId; }

// Subscribe
MID_EventBus<PlayerDiedEvent>.Subscribe(OnPlayerDied);

// Fire
MID_EventBus<PlayerDiedEvent>.Raise(new PlayerDiedEvent { PlayerId = 5 });

// Cleanup on scene unload
MID_EventBus<PlayerDiedEvent>.ClearAll();
```

### `MID_EventBusRegistry` (static)

```csharp
static void Register<T>() where T : IMIDEvent   // register for bulk teardown
static void ClearAll()                           // call on scene unload
```

---

## 9. Audio

Namespace: `MidManStudio.Core.Audio`

### `MID_AudioManager : Singleton<MID_AudioManager>`

```csharp
void   PlayMusic(string id, bool fade = true)
void   StopMusic(bool fade = true)
void   PauseMusic()
void   ResumeMusic()
void   SetMusicEnabled(bool enabled)
void   SetMusicPitch(float targetPitch, bool instant = false)
void   PlaySFX(string id)
void   PlayClipDirect(AudioClip clip, float volume = 1f)
void   PlaySFXPitched(string id, float pitch)
void   PlayClipDirectPitched(AudioClip clip, float pitch, float volume = 1f)
void   SetMasterVolume(float v)
float  MasterVolume      { get; }
bool   IsMusicPlaying    { get; }
bool   IsMusicEnabled    { get; }
string CurrentMusicId    { get; }
event  Action<bool> OnMusicEnabledChanged
```

### `MID_SpawnableAudio : MonoBehaviour`

Pooled audio object. Use `PoolableObjectType.SpawnableAudio`.

```csharp
void PlayOneShot(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f)
void PlayLooping(AudioClip clip, Vector3 position, Transform follow = null,
                 Vector3 offset = default, float volume = 1f, float pitch = 1f)
void PlaySequential(AudioClip flyingClip, AudioClip collisionClip, Vector3 position,
                    Transform follow, float volume = 1f, float pitch = 1f)
void TriggerCollision(float volume = 1f)
void Return()
```

### `MID_AudioLibrarySO : ScriptableObject`

Create via: `MidManStudio > Utilities > Audio Library`

```csharp
void BuildLookup()
bool TryGet(string id, out MID_AudioEntry entry)
bool HasClip(string id)
int  Count { get; }
```

---

## 10. Timers

Namespace: `MidManStudio.Core.Timers`

### `CountdownTimer`

```csharp
CountdownTimer(float duration)
void  Start()  void Stop()  void Pause()  void Resume()
void  Tick(float deltaTime)
void  Reset()  void Reset(float newDuration)
bool  IsFinished { get; }
bool  IsRunning  { get; }
float Progress   { get; }
Action OnTimerStart, OnTimerStop, OnTimerComplete
```

### `StopwatchTimer`

```csharp
void  Start()  void Stop()  void Tick(float deltaTime)  void Reset()
float GetTime()
bool  IsRunning { get; }
```

### `ValueInterpolationTimer`

```csharp
ValueInterpolationTimer(float start, float end, float duration,
                        InterpolationMode mode = Linear, AnimationCurve curve = null)
void  Start()  void StartPingPong()  void Stop()  void Tick(float deltaTime)  void Reset()
void  Reconfigure(float start, float end, float duration)
void  SetInterpolationMode(InterpolationMode mode, AnimationCurve curve = null)
float CurrentValue { get; }
float Progress     { get; }
bool  IsRunning    { get; }
Action<float> OnValueChanged
Action        OnInterpolationComplete, OnInterpolationStart
```

`InterpolationMode`: `Linear`, `EaseIn`, `EaseOut`, `EaseInOut`, `Custom`

### `SteppedValueTimer`

Steps a float value incrementally over time at a fixed interval.

```csharp
SteppedValueTimer(float startValue, float endValue, float stepSize, float stepInterval)
void  Start()  void Stop()  void Tick(float deltaTime)  void Reset()
float CurrentValue { get; }
bool  IsRunning    { get; }
float Progress     { get; }
Action<float> OnValueChanged
Action        OnComplete, OnStepComplete
```

### `TimerFactory` (static helpers)

```csharp
ValueInterpolationTimer CreateDissolveTimer(float duration, InterpolationMode mode)
ValueInterpolationTimer CreateUnDissolveTimer(float duration, InterpolationMode mode)
ValueInterpolationTimer CreateAlphaFadeTimer(float start, float end, float duration, ...)
SteppedValueTimer       CreateSteppedDissolveTimer(float min, float max, float step, float interval)
```

---

## 11. Library System

Namespace: `MidManStudio.Core.Libraries`

A generic keyed asset registry. Group `ScriptableObject` assets under a `MID_LibrarySO`, retrieve by string key via `MID_LibraryRegistry`.

### `MID_LibrarySO : ScriptableObject`

Create via: `MidManStudio > Utilities > Library`

```csharp
void BuildLookup()
T    GetItem<T>(string itemId) where T : MID_LibraryItemSO
bool HasItem(string itemId)
int  ItemCount { get; }
IEnumerable<string> AllItemIds { get; }
string LibraryId { get; }
```

### `MID_LibraryItemSO : ScriptableObject` (abstract)

Base class for all library items. Subclass to add custom data fields.

```csharp
string ItemId { get; }   // set in inspector or defaults to asset file name
```

**Creating a custom item type:**
```csharp
[CreateAssetMenu(menuName = "MyGame/Libraries/WeaponItem")]
public class WeaponItemSO : MID_LibraryItemSO
{
    public float  damage;
    public Sprite icon;
}
```

### `MID_BasicLibraryItemSO : MID_LibraryItemSO`

Create via: `MidManStudio > Utilities > Library Item (Basic)`

Ready-to-use item with `displayName`, `description`, `icon`, and `tags[]`.  
Use this when you don't need custom data fields.

### `MID_LibraryRegistry : Singleton<MID_LibraryRegistry>`

```csharp
// String-key API
T    GetItem<T>(string libraryId, string itemId) where T : MID_LibraryItemSO
bool LibraryExists(string libraryId)
bool ItemExists(string libraryId, string itemId)

// Generated enum API (after running Library Type Generator)
T    GetItem<T>(LibraryId libraryId, LibraryItemId itemId) where T : MID_LibraryItemSO
bool LibraryExists(LibraryId libraryId)
bool ItemExists(LibraryId libraryId, LibraryItemId itemId)
```

**Usage:**
```csharp
// Simple string lookup
var item = MID_LibraryRegistry.Instance
    .GetItem<MID_BasicLibraryItemSO>("Weapons", "Sword");
Debug.Log(item.displayName);

// Custom type lookup
var weapon = MID_LibraryRegistry.Instance
    .GetItem<WeaponItemSO>("Weapons", "Sword");
Debug.Log(weapon.damage);
```

### `LibraryTypeProviderSO : ScriptableObject`

Create via: `MidManStudio > Utilities > Library Type Provider`  
**Open generator:** `MidManStudio > Utilities > Library Type Generator`

Contributes entries to the generated `LibraryId` and `LibraryItemId` enums.

---

## 12. Scene Management

Namespace: `MidManStudio.Core.SceneManagement`

### `MID_SceneLoader : Singleton<MID_SceneLoader>` implements `ISceneLoader`

```csharp
void LoadScene(int sceneId, SceneLoadType loadType = Single, short delayMs = 0)
void LoadScene(SceneId id, SceneLoadType loadType = Single, short delayMs = 0)
void UnloadScene(int sceneId)
bool IsSceneLoaded(int sceneId)
SceneId GetActiveSceneId()
bool  IsLoadingScene       { get; }
int   CurrentLoadingSceneId { get; }
Action<float>  OnLoadProgressChanged  { get; set; }
Action<int>    OnSceneLoadCompleted   { get; set; }
Action<string> OnSceneLoadFailed      { get; set; }
```

### `MID_SceneTransitionController : Singleton<...>` (abstract)

Subclass to drive your own fade/UI animations.

```csharp
void LoadScene(SceneId sceneId, bool useTransition = true, short delayMs = 0)
void LoadScene(SceneId sceneId, SceneLoadType loadType, bool useTransition = true, short delayMs = 0)
void EmergencyCleanup()

// Override these hooks
protected virtual IEnumerator TransitionIn()
protected virtual IEnumerator TransitionOut()
protected virtual IEnumerator OnLoadingStarted()
protected virtual IEnumerator OnLoadingFinished()
protected virtual void        OnProgressUpdated(float progress)
protected virtual void        OnLoadingMessageChanged(string message)
protected virtual IEnumerator OnTransitionError(string error)
```

### `SceneTypeProviderSO : ScriptableObject`

Create via: `MidManStudio > Utilities > Scene Type Provider`  
**Open generator:** `MidManStudio > Utilities > Scene Type Generator`

### Enums

```csharp
enum SceneLoadType { Single, Additive, NetworkAdditive }
enum SceneNetworkDependency { None, InternetRequired, NetworkSessionRequired, Optional }
```

---

## 13. UI State System

Namespace: `MidManStudio.Core.UIState`

### Overview

The UI State system uses a **per-context** model. Each logical UI area (Menu, HUD, Lobby) has its own `MID_UIStateContext` SO asset and its own generated `[Flags]` enum. There is no global `UIStateId` enum — each context is self-contained.

**Setup flow:**
1. Create `UIStateContextProviderSO` → add states → run generator → produces e.g. `MenuUIState.cs`
2. Create `MID_UIStateContext` SO asset → set `enumTypeName = "MidManStudio.Core.UIState.MenuUIState"`
3. Assign context to `MID_UIStateVisibility`, `MID_UIStateButton`, or `MID_UIStateManager`

---

### `MID_UIStateContext : ScriptableObject`

Create via: `MidManStudio > Utilities > UI State Context`

```csharp
int  CurrentState { get; }
bool CanGoBack    { get; }

void ChangeState(int newState)    // pass (int)MenuUIState.Settings
void GoBack()
void ClearHistory()
bool IsInState(int state)
bool HasFlag(int flag)

event Action<int> OnStateChanged   // fires on every state change
```

---

### `MID_UIStateManager : Singleton<MID_UIStateManager>`

Drives panel show/hide for one `MID_UIStateContext`.

```csharp
MID_UIStateContext Context    { get; }
int               CurrentState { get; }
bool              CanGoBack    { get; }

void ChangeState(int newState)
void GoBack()
void ClearHistory()
bool IsInState(int state)
void SetContext(MID_UIStateContext context)

event Action<int> OnStateChanged
```

**Inspector:** assign `Context`, set `Initial State`, add `UIStatePanelConfig` entries.  
The custom inspector shows named enum dropdowns when a context is assigned.

---

### `UIStatePanelConfig`

| Field | Description |
|---|---|
| `stateMask` | Raw int value of the generated enum member |
| `displayName` | Inspector label only |
| `show` | GameObjects to activate on enter |
| `hide` | GameObjects to deactivate on enter |
| `onEnter` / `onExit` | UnityEvents for enter/exit |

---

### `MID_UIStateVisibility : MonoBehaviour`

Requires `MID_UIElement`. Shows/hides based on context state flags.

```csharp
[SerializeField] private MID_UIStateContext _context;
[SerializeField] private int               _showWhenMask;  // custom inspector shows checkboxes
```

### `MID_UIStateButton : MonoBehaviour`

Requires `Button`. Transitions context to a target state on click.

```csharp
[SerializeField] private MID_UIStateContext _context;
[SerializeField] private int               _targetStateMask;  // custom inspector shows dropdown
[SerializeField] private bool              _disableWhenActive;
```

### `MID_UIElement : MonoBehaviour`

Requires `CanvasGroup`. Base show/hide for any UI panel.

```csharp
bool IsShowing { get; }
void Show()
void Show(bool propagateToChildren)
void Hide()
void Hide(float targetAlpha)
void Toggle()
```

### `UIStateContextProviderSO : ScriptableObject`

Create via: `MidManStudio > Utilities > UI State Context Provider`  
**Open generator:** `MidManStudio > Utilities > UI State Context Generator`

| Field | Description |
|---|---|
| `contextName` | Becomes `{contextName}UIState` enum name. PascalCase, no spaces |
| `packageId` | Unique across all providers |
| `states` | List of `UIStateEntry` — each becomes a bit flag |

---

## 14. UI Components

Namespace: `MidManStudio.Core.UI`

### `MID_Button : MonoBehaviour`

Requires `Button`. Animated click feedback, zero external tween dependency.

```csharp
void SetInteractable(bool value)
```

**Animation types:** `ScalePop`, `MoveLeft/Right/Up/Down`, `Bounce`, `Pulse`, `Shake`, `Rotate`, `FadeFlash`

**Events:** `OnClickAction` (game logic), `OnClickSound` (route to audio system)

---

## 15. Helper Functions

Namespace: `MidManStudio.Core.HelperFunctions`

### `MID_HelperFunctions` (static)

```csharp
// Logging shims (routes to MID_Logger)
void LogDebug(string msg, ...)
void LogWarning(string msg, ...)
void LogError(string msg, ...)
void LogException(Exception e, ...)

// GameObject
void KillObjChildren(Transform holder)
void KillMultipleParentsChildren(List<Transform> holders)

// UI
void  SetCanvasGroup(CanvasGroup cg, bool enable)
Color GetColorFromString(string hexColor)

// String formatting
string ToSentenceCase(string input)
string ToCamelCase(string input)
string ToPascalCase(string input)
string ToKebabCase(string input)
string ToSnakeCase(string input)

// Validation
bool IsStringValid(string val)   // false for null, empty, or "null"

// Reflection debug
string GetStructOrClassMemberValues<T>(T instance)

// Serialisation (Unity JsonUtility — no Newtonsoft dependency)
string ToJson<T>(T obj, bool prettyPrint = true)
T      FromJson<T>(string json)
string ToXml<T>(T obj)
bool   IsValidJson(string json)
```

### `MID_HelperFunctionsWithType<T>` (static generic)

```csharp
List<U>               Map<U>(List<T> items, Func<T, U> fn)
List<T>               Filter(List<T> items, Predicate<T> pred)
U                     Reduce<U>(List<T> items, U seed, Func<U, T, U> fn)
Dictionary<K,List<T>> GroupBy<K>(List<T> items, Func<T, K> keySelector)
bool                  AnyMatch(List<T> items, Predicate<T> pred)
bool                  AllMatch(List<T> items, Predicate<T> pred)
void                  PrintValues(List<T> items)
```

---

## 16. Sequential Process Runner

Namespace: `MidManStudio.Core.SequentialProcessing`

### `MID_SequentialProcessRunner` (static)

Runs async tasks sequentially across priority lanes with retry and fallback support.

```csharp
static void  AddTask(SequentialTask task)
static void  AddTasks(IEnumerable<SequentialTask> tasks)
static Task  RunAll()
static bool  IsCompleted(string taskName)
static void  Reset()
static void  ResetLane(int lane)

static Action         OnAllLanesComplete
static Action<int>    OnLaneComplete
static Action<string> OnTaskCompleted
static Action<string> OnTaskFailed
static MID_LogLevel   LogLevel
static int            DelayBetweenTasksMs
```

### `SequentialTask`

```csharp
SequentialTask(string name, int lane,
               Func<Task<bool>> execute,
               Func<Task<bool>> fallback = null)

string Name        { get; }
int    Lane        { get; }
bool   HasFallback { get; }
int    RetryCount  { get; }
bool   IsCompleted { get; }
const  int MaxRetries = 6
```

---

## 17. Editor Tools

### `SceneDependencyInjector : MonoBehaviour` (Editor only)

Instantiates required persistent manager prefabs on Play, removing the need for a bootstrap scene during isolated testing.

```csharp
void InjectDependencies()
void ForceReinject()
void CleanupInjectedObjects()
```

**Inspector:** `requiredDependencies` (list of prefabs), `autoInjectOnPlay`, `cleanupOnStop`

---

### `MID_LoggerEditorWindow`

Open via: `MidManStudio > Utilities > Logger Manager`

Scans all scene MonoBehaviours for `MID_LogLevel` fields. Supports bulk set, search, group by GameObject, and export to console.

---

### `PoolTypeGeneratorWindow`

Open via: `MidManStudio > Utilities > Pool Type Generator`

Discovers all `PoolTypeProviderSO` / `ParticlePoolTypeProviderSO` assets and writes the shared enum files.

---

### `DynamicDebugPanel : MonoBehaviour` (Editor Play mode)

Runtime overlay for displaying stats, values, and logs.

```csharp
void AddSection(string name, Color titleColor = default)
void AddValue(string section, string name, object value, DebugValueType type = Display, ...)
void UpdateValue(string section, string name, object value)
void RemoveValue(string section, string name)
void AddLog(string message)
void ClearLogs()
void TogglePanel()
void SetPanelState(bool show)
```

`DebugValueType`: `Display`, `Slider`, `Toggle`, `Button`, `ProgressBar`

---

### `MID_NamedListAttribute`

```csharp
[MID_NamedList]                              // uses IArrayElementTitle.Name
[MID_NamedList("fieldName")]                 // uses named field as label
[MID_NamedList("fieldName", true, "color")]  // with per-element colour tinting
```

Implement `IArrayElementTitle` on list element types to control the displayed name.  
Implement `IArrayElementColor` to control background tint colour.

---

## 18. Assembly Definitions

### Runtime Assembly — `MidManStudio.Utilities`

**File:** `packages/com.midmanstudio.utilities/Runtime/MidManStudio.Utilities.asmdef`

```json
{
  "name": "MidManStudio.Utilities",
  "rootNamespace": "MidManStudio.Core",
  "references": ["Unity.Burst", "Unity.Collections"],
  "allowUnsafeCode": true,
  "autoReferenced": true
}
```

`autoReferenced: true` means any assembly in the project can use it without explicitly listing it as a reference.

---

### Editor Assembly — `MidManStudio.Utilities.Editor`

**File:** `packages/com.midmanstudio.utilities/Editor/MidManStudio.Utilities.Editor.asmdef`

```json
{
  "name": "MidManStudio.Utilities.Editor",
  "rootNamespace": "MidManStudio.Core.Editor",
  "references": ["MidManStudio.Utilities"],
  "includePlatforms": ["Editor"],
  "autoReferenced": false
}
```

Editor-only. Contains all `#if UNITY_EDITOR` code that has been promoted to proper editor-assembly classes: `PoolTypeGenerator`, `LibraryTypeGenerator`, `SceneTypeGenerator`, `UIStateContextGenerator`, `UIStateTypeGenerator`, `MID_LoggerEditorWindow`, `NamedListDrawer`, `SceneDependencyInjector`, all custom inspectors.

---

### Tests Assembly — `MidManStudio.Utilities.Tests`

**File:** `packages/com.midmanstudio.utilities/Tests/Runtime/MidManStudio.Utilities.Tests.asmdef`

```json
{
  "name": "MidManStudio.Utilities.Tests",
  "rootNamespace": "MidManStudio.Core.Tests",
  "references": ["MidManStudio.Utilities", "UnityEngine.TestRunner", "UnityEditor.TestRunner"],
  "autoReferenced": false,
  "defineConstraints": ["UNITY_INCLUDE_TESTS"]
}
```

Contains: `MID_TickDelayBenchRunner`, `MID_TickDelayBenchWindow`, `MID_TickDispatcherBench`, `MID_TickDispatcherBenchWindow`.  
Only compiled when `UNITY_INCLUDE_TESTS` is defined (i.e. when Test Runner is active).

---

### Reference Diagram  YourGame.asmdef
└── MidManStudio.Utilities          (autoReferenced — implicit)
└── Unity.Burst
└── Unity.Collections
YourGame.Editor.asmdef
└── MidManStudio.Utilities.Editor   (explicit reference if needed)
└── MidManStudio.Utilities
MidManStudio.Utilities.Tests          (only with UNITY_INCLUDE_TESTS)
└── MidManStudio.Utilities
└── UnityEngine.TestRunner

---

### What References What — File → Assembly Map

| File | Assembly |
|---|---|
| All `Runtime/` scripts | `MidManStudio.Utilities` |
| All `Editor/` scripts | `MidManStudio.Utilities.Editor` |
| `Tests/Runtime/MID_TickDelayBenchmark.cs` | `MidManStudio.Utilities.Tests` |
| `Tests/Runtime/MID_TickDispatcherBench.cs` | `MidManStudio.Utilities.Tests` |
| Generated `PoolableObjectType.cs` | `MidManStudio.Utilities` (Runtime/PoolSystems/Generated/) |
| Generated `PoolableParticleType.cs` | `MidManStudio.Utilities` (Runtime/PoolSystems/Generated/) |
| Generated `MenuUIState.cs` etc. | `MidManStudio.Utilities` (Runtime/UIState/Generated/) |
| Generated `SceneId.cs` / `SceneRegistry.cs` | `MidManStudio.Utilities` (Runtime/SceneManagement/Generated/) |
