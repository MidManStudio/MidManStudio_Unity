# MidMan Studio Utilities — API Catalog

`com.midmanstudio.utilities` v1.1.0  
Assembly: `MidManStudio.Utilities`  
Namespace root: `MidManStudio.Core`

---

## Table of Contents

1. [Pool System](#1-pool-system)
2. [Pool Type Generator](#2-pool-type-generator)
3. [Tick Dispatchers](#3-tick-dispatchers)
4. [Logger](#4-logger)
5. [Singletons](#5-singletons)
6. [Observable Values](#6-observable-values)
7. [Audio](#7-audio)
8. [Timers](#8-timers)
9. [Library System](#9-library-system)
10. [UI](#10-ui)
11. [Helper Functions](#11-helper-functions)
12. [Editor Tools](#12-editor-tools)

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
// Initialize — call once at game start (chains to LocalParticlePool)
void CallInitializePool()

// Retrieve
GameObject GetObject(PoolableObjectType type, Vector3 position, Quaternion rotation)
GameObject GetObject(PoolableObjectType type, Vector2 position, Quaternion rotation)
GameObject GetObject(int typeId, Vector3 position, Quaternion rotation) // raw int overload

// Return
void ReturnObject(GameObject obj, PoolableObjectType type)
void ReturnObject(GameObject obj, int typeId)

// Runtime registration
void AddType(PoolableObjectType type, GameObject prefab, int prewarm = 5, int maxSize = 15)
void AddType(int typeId, GameObject prefab, int prewarm = 5, int maxSize = 15)

// Queries
bool IsRegistered(PoolableObjectType type)
bool IsRegistered(int typeId)
bool HasBeenInitialized()

// Bulk operations
void ReturnAllActive()   // returns every active LocalPoolReturn in scene
void ClearPool()         // destroy all pooled objects and clear registration
```

---

### `LocalParticlePool : Singleton<LocalParticlePool>`

Singleton pool manager for particle effect GameObjects.  
Keyed by `PoolableParticleType` (or raw `int`).  
Initialized automatically by `LocalObjectPool.CallInitializePool()`.

**Public API**

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
void ReturnToPoolNow()           // immediate return, bypasses timer
bool IsScheduledForReturn()
float GetDuration()
bool IsAutoReturnEnabled()
PoolableObjectType GetOriginalType()
```

---

### `LocalParticleReturn : MonoBehaviour`

Auto-returns a pooled particle to `LocalParticlePool` after `maxLifetime` seconds.  
Added automatically by `LocalParticlePool`.

```csharp
void SetOriginalType(PoolableParticleType type)
void SetMaxLifetime(float seconds)
void ReturnToPool()
void ForceReturn()
PoolableParticleType GetOriginalType()
```

---

### `TrailRendererPool : Singleton<TrailRendererPool>`

Generic slot-based `TrailRenderer` pool. Any moving entity can acquire a slot.

```csharp
// Acquire a slot — returns slot index, -1 if exhausted
int Acquire(TrailConfig config, int ownerId = 0)

// Move trail (call every frame / FixedUpdate)
void SetPosition(int slot, Vector3 worldPosition)

// Release — trail fades out naturally, then slot is recycled
void Release(int slot)

// Release all slots owned by an object (by GetInstanceID())
void ReleaseByOwner(int ownerId)

// Immediate release with no fade
void ForceRelease(int slot)

// Queries
bool IsAcquired(int slot)
int PoolSize { get; }
```

**`TrailConfig` struct**

```csharp
public struct TrailConfig
{
    public Material  Material;
    public Gradient  ColorGradient;
    public float     Time;          // trail fade time in seconds
    public float     StartWidth;
    public float     EndWidth;
    public int       CapVertices;   // 0 = flat, 2–4 = rounded

    public static TrailConfig Default { get; } // sensible defaults
}
```

---

### `BasicPoolConfig` / `ParticlePoolConfig`

Inspector-visible pool entry. Assign in `LocalObjectPool` / `LocalParticlePool`.

| Field | Description |
|---|---|
| `typeId` | Integer value matching a `PoolableObjectType` / `PoolableParticleType` member |
| `displayName` | Inspector label only — no runtime effect |
| `prefab` | The prefab to pool |
| `prewarmCount` | Instances pre-created on init |
| `maxPoolSize` | Pool will destroy overflow beyond this |
| `defaultLifetime` | (particle only) seconds before auto-return |

---

## 2. Pool Type Generator

Namespace: `MidManStudio.Core.Pools.Generator`  
Assembly: `MidManStudio.Utilities.Editor` (editor only)

**Open via:** `MidManStudio > Pool Type Generator`

---

### `PoolTypeProviderSO : ScriptableObject`

Create via: `MidManStudio > Pool Type Provider (Object)`

Contributes entries to the generated `PoolableObjectType` enum.

| Field | Description |
|---|---|
| `packageId` | Unique reverse-domain ID. e.g. `com.mygame` |
| `displayName` | Shown in generator window |
| `priority` | Lower = earlier block. 0 = utilities, 10 = projectile, 100+ = user |
| `entries` | List of `PoolEntryDefinition` |

---

### `ParticlePoolTypeProviderSO : ScriptableObject`

Create via: `MidManStudio > Pool Type Provider (Particle)`

Identical shape to `PoolTypeProviderSO`. Contributes to `PoolableParticleType`.

---

### `PoolEntryDefinition`

| Field | Type | Description |
|---|---|---|
| `entryName` | `string` | Becomes the enum member name. PascalCase, no spaces |
| `comment` | `string` | Written as `// comment` next to the member in the file |
| `explicitOffset` | `int` | `-1` = auto-assigned. `≥0` = pinned to this offset within the provider's block |

**Pinning** locks the absolute integer value. Use for entries referenced by serialised inspector fields so their values never shift when you reorder or add entries above them.

---

### `PoolTypeGeneratorSettingsSO : ScriptableObject`

Create via: `MidManStudio > Pool Type Generator Settings`

| Field | Default | Description |
|---|---|---|
| `objectEnumOutputPath` | `Assets/MidManStudio/Generated/PoolableObjectType.cs` | Output path for object enum |
| `particleEnumOutputPath` | `Assets/MidManStudio/Generated/PoolableParticleType.cs` | Output path for particle enum |
| `networkEnumOutputPath` | `Assets/MidManStudio/Generated/PoolableNetworkObjectType.cs` | Output path for network enum |
| `lockFilePath` | `Assets/MidManStudio/Generated/PoolTypeLock.json` | Lock file — commit to source control |
| `minimumBlockSize` | `100` | Minimum slot gap between providers. Auto-expands in multiples |
| `generatedNamespace` | `MidManStudio.Core.Pools` | Namespace written into generated files |
| `autoGenerateOnAssetChange` | `false` | Regenerate automatically when providers change |

---

### Block assignment rules

1. Providers sorted by `priority` ascending, then `packageId` alphabetically (for stability)
2. Each provider's block size = `ceil(entryCount / minBlockSize) * minBlockSize`, minimum `minBlockSize`
3. Blocks are contiguous — no gaps between providers
4. If a provider grows beyond its block the generator refuses and reports the overflow
5. The lock file preserves auto-assigned values across regenerations
6. Pinned offsets always win over lock file values

---

## 3. Tick Dispatchers

Namespace: global (`MID_TickDispatcher`, `MID_NativeTickDispatcher`)

### `MID_TickDispatcher : MonoBehaviour`

Zero-allocation managed tick dispatcher. Replaces per-MonoBehaviour `Update()` for systems that don't need every frame.

```csharp
// Subscribe — call in OnEnable
bool Subscribe(TickRate tickRate, TickCallback callback)

// Unsubscribe — call in OnDisable / OnDestroy — NEVER skip
bool Unsubscribe(TickRate tickRate, TickCallback callback)

// Queries
bool   IsSubscribed(TickRate r, TickCallback cb)
int    GetSubscriberCount(TickRate r)
bool   IsTickRateActive(TickRate r)
float  GetInterval(TickRate r)      // seconds between dispatches
float  GetFrequency(TickRate r)     // dispatches per second
void   ClearSubscribers(TickRate r)
void   ClearAllSubscribers()

// Static properties
bool IsReady    { get; }
bool IsQuitting { get; }
```

**`TickRate` enum**

| Member | Interval | Fires/sec | Recommended use |
|---|---|---|---|
| `Tick_0_01` | 10ms | 100 | ⚠️ Danger — see header comments |
| `Tick_0_02` | 20ms | 50 | ⚠️ Marginal |
| `Tick_0_05` | 50ms | 20 | Fast systems minimum |
| `Tick_0_1` | 100ms | 10 | ✅ Recommended minimum |
| `Tick_0_2` | 200ms | 5 | Standard AI / cooldowns |
| `Tick_0_5` | 500ms | 2 | Area / perception checks |
| `Tick_1` | 1s | 1 | Health regen, UI numbers |
| `Tick_2` | 2s | 0.5 | Ambient / distant objects |
| `Tick_5` | 5s | 0.2 | Spawners, wave logic |

**Callback signature:** `delegate void TickCallback(float deltaTime)`  
`deltaTime` is the bucket's fixed interval, not `Time.deltaTime`.

---

### `MID_NativeTickDispatcher : MonoBehaviour`

Burst-compiled native tick dispatcher. Use only for fully data-oriented workloads with 500+ subscribers doing real math.  
For everything else, use `MID_TickDispatcher`.

**Rules for subscribers:**
- Method must be `static`
- Method must have `[BurstCompile]`
- Method must NOT touch managed objects
- Subscribe/Unsubscribe from main thread only
- **Always** unsubscribe in `OnDisable` / `OnDestroy`

```csharp
// Subscriber signature
public delegate void NativeTickDelegate(float deltaTime);

bool Subscribe(TickRate r, NativeTickDelegate callback)
bool Unsubscribe(TickRate r, NativeTickDelegate callback)
bool IsSubscribed(TickRate r, NativeTickDelegate callback)
int  GetSubscriberCount(TickRate r)
void ClearSubscribers(TickRate r)
void ClearAllSubscribers()
```

---

## 4. Logger

Namespace: `MidManStudio.Core.Logging`

### `MID_Logger : MonoBehaviour`

Level-gated singleton logger with colour support in the Unity Editor.

```csharp
// All methods are static
void LogDebug(MID_LogLevel level, string message, string className = "", string method = "")
void LogInfo(MID_LogLevel level, string message, string className = "", string method = "")
void LogWarning(MID_LogLevel level, string message, string className = "", string method = "")
void LogError(MID_LogLevel level, string message, string className = "", string method = "",
              Exception e = null)
void LogException(MID_LogLevel level, Exception e, string message = "",
                  string className = "", string method = "")
void LogVerbose(MID_LogLevel level, string message, string className = "", string method = "")
bool ShouldLog(MID_LogLevel current, MID_LogLevel messageLevel)
```

### `MID_LogLevel` enum

```
None = 0    — no output
Error = 1   — errors and exceptions only
Info = 2    — info + warnings + errors
Debug = 3   — debug + info + warnings + errors
Verbose = 4 — everything
```

**Typical pattern:**
```csharp
[SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

// In production, set _logLevel to Info or Error via the Inspector
// or via MidManStudio > Logger Manager
```

---

## 5. Singletons

Namespace: `MidManStudio.Core.Singleton`

### `Singleton<T> : MonoBehaviour` where `T : Component`

Standard MonoBehaviour singleton with optional scene persistence.

```csharp
static T    Instance        { get; }   // auto-creates if missing
static bool HasInstance     { get; }
static T    TryGetInstance()           // returns null if not present
static T    Current         { get; }
static bool IsAvailable()
static T    GetExistingInstance()      // finds without creating
static void Reset()

// Override in subclass
protected virtual void Awake()
protected virtual void Remake(bool persistAcrossScenes = false)
protected virtual void InitializeSingleton(bool persist)
```

Implement `SingletonLifecycle` interface on your subclass to receive `OnSceneChange` callbacks.

---

### `StaticContentSingleton<T>` where `T : class, new()`

Thread-safe lazy singleton for pure C# classes (no MonoBehaviour).

```csharp
static T    Instance        { get; }   // thread-safe double-checked locking
static bool HasInstance     { get; }
static bool IsInitialized   { get; }
static T    TryGetInstance()
static void Initialize(T instance)    // inject custom / subclass instance
static void Reset()                   // calls Dispose() if T : IDisposable
```

Implement `IStaticSingletonInitializable` on `T` to receive an `Initialize()` call on first creation.

---

## 6. Observable Values

Namespace: `MidManStudio.Core.ObservableValues`

### `MID_SusValue<T>`

Generic observable value. Fires callbacks on change or on any set attempt.

```csharp
// Constructor
MID_SusValue(T initialValue = default, Func<T, bool> validationFunc = null)

// Properties
T    Value          { get; set; }  // set fires callbacks if validation passes
bool IsValueNull    { get; }

// Validation
void SetValidationFunction(Func<T, bool> func)
void ClearValidationFunction()

// OnValueChanged — fires when value actually differs
bool SubscribeToValueChanged(OnValueChangedDelegate callback)   // (T old, T new)
bool UnsubscribeFromValueChanged(OnValueChangedDelegate callback)
bool IsSubscribedToValueChanged(OnValueChangedDelegate callback)

// OnAnyUpdate — fires on every .Value = set, even if unchanged
bool SubscribeToAnyUpdate(OnAnyUpdateDelegate callback)         // (T value)
bool UnsubscribeFromAnyUpdate(OnAnyUpdateDelegate callback)
bool IsSubscribedToAnyUpdate(OnAnyUpdateDelegate callback)

// Utilities
void SetValueSilently(T value)     // set without callbacks (validation still runs)
void ForceNotify()                 // push current value to all OnValueChanged subs
void ClearAllSubscriptions()
int  GetSubscriberCount()

// Implicit conversion
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

If `owner` is supplied, all subscriptions are cleared automatically when the owner `GameObject` is destroyed.

---

### `SusValueManager : Singleton<SusValueManager>`

Tracks all `ManagedSusValue` instances for bulk cleanup.

```csharp
void RegisterValue(IManagedSusValue value, GameObject owner = null)
void UnregisterValue(string valueId)
void ClearAllForOwner(GameObject owner)
void ClearAll()
bool IsRegistered(string valueId)
int  RegisteredCount { get; }
```

---

## 7. Audio

Namespace: `MidManStudio.Core.Audio`

### `MID_AudioManager : Singleton<MID_AudioManager>`

```csharp
// Music
void   PlayMusic(string id, bool fade = true)
void   StopMusic(bool fade = true)
void   PauseMusic()
void   ResumeMusic()
void   SetMusicEnabled(bool enabled)
void   SetMusicPitch(float targetPitch, bool instant = false)
string CurrentMusicId { get; }
bool   IsMusicPlaying { get; }
bool   IsMusicEnabled { get; }

// SFX
void PlaySFX(string id)
void PlayClipDirect(AudioClip clip, float volume = 1f)
void PlaySFXPitched(string id, float pitch)
void PlayClipDirectPitched(AudioClip clip, float pitch, float volume = 1f)

// Volume
void  SetMasterVolume(float v)   // 0..1
float MasterVolume { get; }

// Event
event Action<bool> OnMusicEnabledChanged
```

---

### `MID_SpawnableAudio : MonoBehaviour`

Pooled audio object. Retrieve from `LocalObjectPool` using `PoolableObjectType.SpawnableAudio`.

```csharp
void PlayOneShot(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f)
void PlayLooping(AudioClip clip, Vector3 position, Transform follow = null,
                 Vector3 offset = default, float volume = 1f, float pitch = 1f)
void PlaySequential(AudioClip flyingClip, AudioClip collisionClip, Vector3 position,
                    Transform follow, float volume = 1f, float pitch = 1f)
void TriggerCollision(float volume = 1f)
void Return()
```

---

### `MID_AudioLibrarySO : ScriptableObject`

Create via: `MidManStudio > Audio Library`

String-keyed clip registry. Used by `MID_AudioManager` for music and SFX lookups.

```csharp
void BuildLookup()                              // called automatically on Awake
bool TryGet(string id, out MID_AudioEntry entry)
bool HasClip(string id)
int  Count { get; }
```

---

## 8. Timers

Namespace: `MidManStudio.Core.Timers`

### `CountdownTimer`

```csharp
CountdownTimer(float duration)
void  Start()
void  Stop()
void  Pause()
void  Resume()
void  Tick(float deltaTime)       // call every frame or from TickDispatcher
void  Reset()
void  Reset(float newDuration)
bool  IsFinished { get; }
bool  IsRunning  { get; }
float Progress   { get; }        // 0..1
Action OnTimerStart
Action OnTimerStop
Action OnTimerComplete
```

### `StopwatchTimer`

```csharp
StopwatchTimer()
void  Start()
void  Stop()
void  Tick(float deltaTime)
void  Reset()
float GetTime()
bool  IsRunning { get; }
```

### `ValueInterpolationTimer`

Interpolates a float from start to end over a duration with configurable easing.

```csharp
ValueInterpolationTimer(float start, float end, float duration,
                        InterpolationMode mode = Linear,
                        AnimationCurve customCurve = null)

void  Start()
void  StartPingPong()
void  Stop()
void  Tick(float deltaTime)
void  Reset()
void  Reconfigure(float start, float end, float duration)
void  SetInterpolationMode(InterpolationMode mode, AnimationCurve curve = null)
float CurrentValue { get; }
float Progress     { get; }
bool  IsRunning    { get; }
Action<float> OnValueChanged
Action        OnInterpolationComplete
Action        OnInterpolationStart
```

`InterpolationMode`: `Linear`, `EaseIn`, `EaseOut`, `EaseInOut`, `Custom`

### `TimerFactory` (static helpers)

```csharp
ValueInterpolationTimer CreateDissolveTimer(float duration, InterpolationMode mode)
ValueInterpolationTimer CreateUnDissolveTimer(float duration, InterpolationMode mode)
ValueInterpolationTimer CreateAlphaFadeTimer(float start, float end, float duration, ...)
SteppedValueTimer       CreateSteppedDissolveTimer(float min, float max, float step, float interval)
```

---

## 9. Library System

Namespace: `MidManStudio.Core.Libraries`

A generic keyed asset registry. Group related `ScriptableObject` assets under a `MID_LibrarySO`, then retrieve them by string key via `MID_LibraryRegistry`.

### `MID_LibraryRegistry : Singleton<MID_LibraryRegistry>`

```csharp
T    GetItem<T>(string libraryId, string itemId) where T : MID_LibraryItemSO
bool LibraryExists(string libraryId)
bool ItemExists(string libraryId, string itemId)
```

### `MID_LibrarySO : ScriptableObject`

```csharp
T    GetItem<T>(string itemId) where T : MID_LibraryItemSO
bool HasItem(string itemId)
int  ItemCount { get; }
string LibraryId { get; }
```

### `MID_LibraryItemSO : ScriptableObject` (abstract)

Override in your own `ScriptableObject` types. Exposes `ItemId` (string key).

---

## 10. UI

Namespace: `MidManStudio.Core.Pools` / `MidManStudio.Core.UI`

### `UIParticlePoolManager : MonoBehaviour`

Manages UI-layer `ParticleSystem` effects by string key.

```csharp
void TriggerEffect(string key, int emitCount = 10)   // auto-selects Play or Emit
void PlayEffect(string key)                           // ParticleSystem.Play()
void EmitEffect(string key, int count = 10)           // ParticleSystem.Emit()
void StopEffect(string key, bool clear = true)
void StopAll(bool clear = true)
bool IsPlaying(string key)
ParticleSystem GetSystem(string key)
void RegisterEffect(UIEffectConfig config)            // runtime registration
void UnregisterEffect(string key)
```

**`UIEffectConfig`** fields: `key` (string), `particleSystem`, `useEmitMode` (bool)

---

### `MID_Button : MonoBehaviour`

Requires `Button`. Animated click feedback via LeanTween.

```csharp
void SetInteractable(bool value)
```

**Inspector — Animation Types:** `ScalePop`, `MoveLeft/Right/Up/Down`, `Bounce`, `Pulse`, `Shake`, `Rotate`, `FadeFlash`

**Events (assign in inspector):**  
`OnClickAction` — game logic  
`OnClickSound` — route to audio system

**Requires:** LeanTween (free, Asset Store)

---

## 11. Helper Functions

Namespace: `MidManStudio.Core.HelperFunctions`

### `MID_HelperFunctions` (static)

**Logging shims** (routes to `MID_Logger`):
```csharp
void LogDebug(string msg, string className = "", string method = "")
void LogWarning(string msg, string className = "", string method = "")
void LogError(string msg, string className = "", string method = "", Exception e = null)
void LogException(Exception e, string msg = "", string className = "", string method = "")
```

**GameObject:**
```csharp
void KillObjChildren(Transform holder)
void KillMultipleParentsChildren(List<Transform> holders)
```

**UI:**
```csharp
void  SetCanvasGroup(CanvasGroup cg, bool enable)
Color GetColorFromString(string hexColor)
```

**String formatting:**
```csharp
string ToSentenceCase(string input)
string ToCamelCase(string input)
string ToPascalCase(string input)
string ToKebabCase(string input)
string ToSnakeCase(string input)
```

**Validation:**
```csharp
bool IsStringValid(string val)   // false for null, empty, or "null"
```

**Reflection debug:**
```csharp
string GetStructOrClassMemberValues<T>(T instance)  // formatted field/property dump
```

**Serialisation:**
```csharp
string ToJson<T>(T obj, bool prettyPrint = true)   // JsonUtility
T      FromJson<T>(string json)
string ToXml<T>(T obj)
bool   IsValidJson(string json)
```

---

### `MID_HelperFunctionsWithType<T>` (static generic)

```csharp
List<U>              Map<U>(List<T> items, Func<T, U> fn)
List<T>              Filter(List<T> items, Predicate<T> pred)
U                    Reduce<U>(List<T> items, U seed, Func<U, T, U> fn)
Dictionary<K,List<T>>GroupBy<K>(List<T> items, Func<T, K> keySelector)
bool                 AnyMatch(List<T> items, Predicate<T> pred)
bool                 AllMatch(List<T> items, Predicate<T> pred)
void                 PrintValues(List<T> items)
```

---

## 12. Editor Tools

### `SceneDependencyInjector : MonoBehaviour` (Editor only)

Drop in any test scene. Instantiates required persistent manager prefabs on Play.

```csharp
void InjectDependencies()
void ForceReinject()
void CleanupInjectedObjects()
```

**Inspector fields:** `requiredDependencies` (list of prefabs), `autoInjectOnPlay`, `cleanupOnStop`, `_logLevel`

---

### `MID_LoggerEditorWindow`

Open via: `MidManStudio > Logger Manager`

- Scans all scene `MonoBehaviour`s for `MID_LogLevel` fields
- Change log levels per-component without entering Play Mode
- Bulk set all to None / Error / Info / Debug / Verbose
- Export current log levels to console
- Group by GameObject

---

### `MID_NamedListAttribute`

Attribute for inspector list elements. Shows a named title instead of `Element 0`.

```csharp
[MID_NamedList]                              // uses IArrayElementTitle.Name
[MID_NamedList("fieldName")]                 // uses named field
[MID_NamedList("fieldName", true, "color")]  // with per-element colour tinting
```

Implement `IArrayElementTitle` on your list element type to control the displayed name.  
Implement `IArrayElementColor` to control the background tint colour.

---

### `DynamicDebugPanel : MonoBehaviour` (Editor Play mode)

Runtime overlay for displaying stats and logs.

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
