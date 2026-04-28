# com.midmanstudio.utilities

**MidMan Studio Utilities** — Core runtime utilities for Unity 2022.3+.  
No game-specific dependencies. Used as a foundation by all other MidManStudio packages.

---

## Installation

**Via git URL** (Unity Package Manager → Add package from git URL):
```
https://github.com/YOUR_USERNAME/MidManStudio.git?path=/packages/com.midmanstudio.utilities#v1.1.0
```

**Via local file path** (development):
```json
"com.midmanstudio.utilities": "file:../../packages/com.midmanstudio.utilities"
```

**Dependencies** (auto-resolved by UPM):
| Package | Version |
|---|---|
| `com.unity.burst` | 1.8.0+ |
| `com.unity.collections` | 2.1.0+ |

---

## What's included

| System | Description |
|---|---|
| **Pool System** | `LocalObjectPool`, `LocalParticlePool`, `TrailRendererPool` with code-generated extensible enums |
| **Pool Type Generator** | Editor tool that produces `PoolableObjectType` and `PoolableParticleType` enums from ScriptableObject providers |
| **Tick Dispatcher** | Zero-allocation interval-based Update replacement (`MID_TickDispatcher`, `MID_NativeTickDispatcher`) |
| **Logger** | Level-gated coloured console logger (`MID_Logger`, `MID_LoggerEditorWindow`) |
| **Singletons** | `Singleton<T>` (MonoBehaviour), `StaticContentSingleton<T>` (pure C#) |
| **Observable Values** | `MID_SusValue<T>`, `ManagedSusValue<T>`, `SusValueManager` |
| **Audio** | `MID_AudioManager`, `MID_SpawnableAudio`, `MID_AudioLibrarySO` |
| **Timers** | `CountdownTimer`, `StopwatchTimer`, `ValueInterpolationTimer`, `SteppedValueTimer` |
| **Library System** | `MID_LibrarySO`, `MID_LibraryItemSO`, `MID_LibraryRegistry` |
| **UI** | `UIParticlePoolManager`, `MID_Button` |
| **Helpers** | `MID_HelperFunctions` — string formatting, UI utils, reflection debug, JSON/XML |
| **Editor Tools** | `SceneDependencyInjector`, `MID_LoggerEditorWindow`, `NamedListDrawer` |

---

## Quick Start

### Object Pool

```csharp
// 1. Open MidManStudio > Pool Type Generator
// 2. Create a Pool Type Provider asset for your game
// 3. Add your entries and click Generate Now
// 4. Configure LocalObjectPool in the inspector with your prefabs
// 5. Initialize at game start:
LocalObjectPool.Instance.CallInitializePool();

// Get an object
var go = LocalObjectPool.Instance.GetObject(PoolableObjectType.Enemy_Basic, pos, rot);

// Return an object
LocalObjectPool.Instance.ReturnObject(go, PoolableObjectType.Enemy_Basic);
```

### Tick Dispatcher

```csharp
// Subscribe (in OnEnable)
MID_TickDispatcher.Subscribe(TickRate.Tick_0_2, OnTick);

// Unsubscribe (in OnDisable — NEVER skip this)
MID_TickDispatcher.Unsubscribe(TickRate.Tick_0_2, OnTick);

// Callback — fires 5x/sec instead of 60x/sec
void OnTick(float deltaTime) { /* AI, cooldowns, etc. */ }
```

### Logger

```csharp
[SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

MID_Logger.LogInfo(_logLevel, "Ready.", nameof(MyClass), nameof(MyMethod));
MID_Logger.LogWarning(_logLevel, "Something odd.", nameof(MyClass));
MID_Logger.LogError(_logLevel, "Critical failure.", nameof(MyClass), nameof(MyMethod));
```

### Observable Values

```csharp
// Plain — no automatic cleanup
var health = new MID_SusValue<float>(100f);
health.SubscribeToValueChanged((old, next) => UpdateHealthBar(old, next));
health.Value = 80f; // fires callback

// Managed — auto-cleaned when owning GameObject is destroyed
var score = new ManagedSusValue<int>(0, id: "PlayerScore", owner: gameObject);
score.SubscribeToAnyUpdate(v => scoreLabel.text = v.ToString());
```

### Audio Manager

```csharp
// Play music (crossfades from current track)
MID_AudioManager.Instance.PlayMusic("gameplay_theme");

// Play SFX
MID_AudioManager.Instance.PlaySFX("shoot");

// Pitched one-shot
MID_AudioManager.Instance.PlayClipDirectPitched(clip, pitch: 1.2f);

// Enable / disable music (e.g. user settings)
MID_AudioManager.Instance.SetMusicEnabled(false);
```

---

## Pool Type Generator

The generator writes `PoolableObjectType.cs` and `PoolableParticleType.cs` — shared enums that every package and your game contributes to without collision.

### Adding your own pool types

1. Right-click in Project → **MidManStudio → Pool Type Provider (Object)**
2. Set `packageId` to something unique (e.g. `com.mygame`), `priority` to `100`
3. Add entry names (PascalCase, no spaces)
4. **MidManStudio → Pool Type Generator → Generate Now**

Your entries appear in `PoolableObjectType` at block 200+ (100 slots per provider, auto-expanding).

### Block ranges (defaults)

| Priority | Package | Object block | Particle block |
|---|---|---|---|
| 0 | `com.midmanstudio.utilities` | 0–99 | 0–99 |
| 10 | `com.midmanstudio.projectilesystem` | 100–199 | 100–199 |
| 100+ | Your game | 200+ | varies |

### Pinning entries

Set `explicitOffset` on an entry to lock its integer value permanently.  
Use this for entries that are saved in serialised inspector fields:

```
entryName      = "Enemy_Boss"
explicitOffset = 5      // always = blockStart + 5, never shifts
```

Unpinned entries are stabilised by the lock file (`PoolTypeLock.json`).  
**Commit the lock file to source control.**

---

## Scene Dependency Injector (Editor Only)

Drop `SceneDependencyInjector` on any GameObject in a test scene.  
Add your persistent manager prefabs to the list.  
When you enter Play Mode the managers are instantiated if not already present — no bootstrap scene needed for testing.

---

## Trail Renderer Pool

`TrailRendererPool` manages a fixed pool of `TrailRenderer` GameObjects.  
Any system (not just projectiles) can request a slot:

```csharp
var cfg  = new TrailConfig { Material = mat, Time = 0.3f, StartWidth = 0.1f };
int slot = TrailRendererPool.Instance.Acquire(cfg, ownerId: GetInstanceID());

// Every frame / FixedUpdate:
TrailRendererPool.Instance.SetPosition(slot, transform.position);

// On death:
TrailRendererPool.Instance.Release(slot); // fades naturally then recycles
```

---

## Supported Unity Versions

| Unity | Status |
|---|---|
| 2022.3 LTS | ✅ Primary target |
| 2023.x | ✅ Compatible |
| 6000.x (Unity 6) | ⚠️ Untested |

---

## License

MIT — see `LICENSE.md`.
