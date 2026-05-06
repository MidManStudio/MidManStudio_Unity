# com.midmanstudio.utilities

**MidMan Studio Utilities** v1.0.0 — Core runtime utilities for Unity 2022.3+.  
No game-specific dependencies. Foundation for all MidManStudio packages.

---

## Requirements

| Dependency | Version |
|---|---|
| Unity | 2022.3 LTS |
| `com.unity.burst` | 1.8.0+ |
| `com.unity.collections` | 1.2.4+ |

---

## Installation

**Via git URL** (Unity Package Manager → Add package from git URL):https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.utilities#v1.0.0**Via local file path** (development — `manifest.json`):
```json
"com.midmanstudio.utilities": "file:../../packages/com.midmanstudio.utilities"
```

Dependencies are resolved automatically by UPM.

---

## What's Included

| System | Namespace | Description |
|---|---|---|
| Tick Dispatcher | `MidManStudio.Core.TickDispatcher` | Zero-alloc interval-based Update replacement |
| Tick Delay | `MidManStudio.Core.TickDispatcher` | Zero-alloc delayed/repeating actions, NGO-ready |
| Logger | `MidManStudio.Core.Logging` | Level-gated coloured console logger |
| Singletons | `MidManStudio.Core.Singleton` | MonoBehaviour + pure C# singleton bases |
| Observable Values | `MidManStudio.Core.ObservableValues` | Reactive value containers with auto-cleanup |
| Events | `MidManStudio.Core.Events` | SO event channels + typed static event bus |
| Pool System | `MidManStudio.Core.Pools` | Object, particle, trail renderer pools |
| Pool Type Generator | `MidManStudio.Core.Pools.Generator` | Code-generates shared pool enum from SO providers |
| Audio | `MidManStudio.Core.Audio` | Music (crossfade/pitch) + SFX manager |
| Timers | `MidManStudio.Core.Timers` | Countdown, stopwatch, interpolation, stepped |
| Library System | `MidManStudio.Core.Libraries` | Keyed ScriptableObject asset registry |
| Scene Management | `MidManStudio.Core.SceneManagement` | Async loader + transition controller |
| UI State System | `MidManStudio.Core.UIState` | Per-context Flags enum state machine |
| UI Components | `MidManStudio.Core.UI` | Animated button with no tween dependency |
| Helpers | `MidManStudio.Core.HelperFunctions` | String, UI, JSON, reflection utilities |
| Sequential Runner | `MidManStudio.Core.SequentialProcessing` | Priority lane async task runner with retry |
| Editor Tools | — | Logger manager, pool generator, script reader, dependency injector |

---

## Quick Start

### Tick Dispatcher

Replaces per-MonoBehaviour `Update()`. A system that only needs to check 5×/sec
should not run 60×/sec.

```csharp
// IMPORTANT: always use static readonly delegate — method group allocates in Unity 2019.2+
private static readonly MID_TickDispatcher.TickCallback _onTick = OnTick;

private void OnEnable()  => MID_TickDispatcher.Subscribe(TickRate.Tick_0_2, _onTick);
private void OnDisable() => MID_TickDispatcher.Unsubscribe(TickRate.Tick_0_2, _onTick); // never skip

private static void OnTick(float dt)
{
    // fires 5×/sec instead of 60×/sec — 75%+ CPU saving on this system
}
```

---

### Tick Delay — Zero-Alloc Delayed Actions

The zero-GC alternative to `StartCoroutine` and `Task.Delay`, designed for use inside
Netcode for GameObjects RPCs where `IEnumerator` is not an option.

```csharp
// Pre-allocate delegate once — passing a field reference costs zero GC
private static readonly Action _onRespawn = DoRespawn;
private TickDelayHandle _respawnHandle;

// Inside a ServerRpc — no IEnumerator, no Task, no allocation
[ServerRpc]
private void RequestRespawnServerRpc()
{
    _respawnHandle = MID_TickDelay.After(3f, _onRespawn, TickRate.Tick_0_2);
}

private static void DoRespawn()
{
    // fires on main thread — safe to call any Unity API
}

private void OnDisable()
{
    _respawnHandle.Cancel(); // safe even if already fired
}
```

**Trade-off vs alternatives:**

| | MID_TickDelay | Coroutine | Task.Delay |
|---|---|---|---|
| GC allocation | **0 B always** | ~80–400 B/call | ~120 B cold, pool after warmup |
| Thread | **Main** | Main | Threadpool — unsafe for Unity APIs |
| IEnumerator | **Not needed** | Required — breaks RPC signatures | Not needed |
| Cancellation | **TickDelayHandle** | StopCoroutine | CancellationToken (+alloc) |
| Timing error | 0–100ms at Tick_0_1 | 0–16ms at 60fps | ~0–2ms (OS timer) |

---

### Logger

```csharp
[SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

MID_Logger.LogInfo(_logLevel, "System ready.", nameof(MyClass), nameof(MyMethod));
MID_Logger.LogWarning(_logLevel, "Unexpected state.");
MID_Logger.LogError(_logLevel, "Critical failure.", nameof(MyClass), nameof(MyMethod), ex);
```

Bulk-manage log levels across all scene objects: `MidManStudio > Utilities > Logger Manager`.

---

### Object Pool

```csharp
// 1. Open MidManStudio > Utilities > Pool Type Generator
// 2. Create a Pool Type Provider asset, add entries, click Generate Now
// 3. Add LocalObjectPool to a persistent GameObject, configure prefabs
// 4. Initialize at game start:
LocalObjectPool.Instance.CallInitializePool();

// Spawn
var go = LocalObjectPool.Instance.GetObject(PoolableObjectType.Enemy_Basic, pos, rot);

// Return (LocalPoolReturn auto-added to pooled objects — can also call manually)
LocalObjectPool.Instance.ReturnObject(go, PoolableObjectType.Enemy_Basic);
```

---

### Observable Values

```csharp
// Plain value — manual cleanup
var health = new MID_SusValue<float>(100f);
health.SubscribeToValueChanged((old, next) => UpdateHealthBar(old, next));
health.Value = 80f; // fires callback

// Managed value — auto-cleared when owning GameObject is destroyed
var score = new ManagedSusValue<int>(0, id: "PlayerScore", owner: gameObject);
score.SubscribeToAnyUpdate(v => scoreLabel.text = v.ToString());
```

---

### UI State System

```csharp
// 1. Create UIStateContextProviderSO
//    contextName = "Menu", add states: MainMenu, Settings, Credits
// 2. MidManStudio > Utilities > UI State Context Generator > Generate Now
//    → produces MenuUIState.cs: [Flags] enum { None=0, MainMenu=1, Settings=2, Credits=4 }
// 3. Create MID_UIStateContext SO asset
//    enumTypeName = "MidManStudio.Core.UIState.MenuUIState"
// 4. In code:
MID_UIStateManager.Instance.ChangeState((int)MenuUIState.Settings);
MID_UIStateManager.Instance.GoBack();
```

---

### Library System

```csharp
// 1. Create Library Items: right-click > MidManStudio > Utilities > Library Item (Basic)
// 2. Create Library:       right-click > MidManStudio > Utilities > Library
//    Add items to the Items list, set libraryId = "Weapons"
// 3. Add MID_LibraryRegistry to a persistent GameObject, assign your library
// 4. Retrieve:
var sword = MID_LibraryRegistry.Instance
    .GetItem<MID_BasicLibraryItemSO>("Weapons", "Sword");
Debug.Log(sword.displayName);

// Custom item type:
var weapon = MID_LibraryRegistry.Instance
    .GetItem<WeaponItemSO>("Weapons", "Sword");
Debug.Log(weapon.damage);
```

---

### Audio

```csharp
// Play music with crossfade
MID_AudioManager.Instance.PlayMusic("gameplay_theme");

// One-shot SFX
MID_AudioManager.Instance.PlaySFX("shoot");

// Enable/disable music (e.g. from a settings screen)
MID_AudioManager.Instance.SetMusicEnabled(false);
MID_AudioManager.Instance.OnMusicEnabledChanged += enabled => UpdateMusicToggleUI(enabled);
```

---

## Pool Type Generator

Writes `PoolableObjectType.cs` and `PoolableParticleType.cs` — shared enums that any
package or game project can contribute to without conflicts.

### Adding your own pool types

1. `MidManStudio > Utilities > Pool Type Generator → + Object Provider`
2. Set `packageId` (e.g. `com.mygame`), `priority` ≥ 100, add entry names (PascalCase)
3. `Generate Now`

Your entries appear in `PoolableObjectType` at block 200+ (utilities = 0–99, projectile = 100–199).

### Block ranges

| Priority | Package | Block |
|---|---|---|
| 0 | `com.midmanstudio.utilities` | 0–99 |
| 10 | `com.midmanstudio.projectilesystem` | 100–199 |
| 100+ | Your game | 200+ |

### Pinning entriesentryName      = "Enemy_Boss"
explicitOffset = 5      // always = blockStart + 5, never shifts on regeneration
Unpinned entries are stabilised by the lock file (`PoolTypeLock.json`).  
**Commit the lock file to source control.**

---

## Editor Tools

| Tool | Open via |
|---|---|
| Logger Manager | `MidManStudio > Utilities > Logger Manager` |
| Pool Type Generator | `MidManStudio > Utilities > Pool Type Generator` |
| Library Type Generator | `MidManStudio > Utilities > Library Type Generator` |
| Scene Type Generator | `MidManStudio > Utilities > Scene Type Generator` |
| UI State Context Generator | `MidManStudio > Utilities > UI State Context Generator` |
| Tick Delay Benchmark | `MidManStudio > Utilities > Tests > Tick Delay Bench` |
| Tick Dispatcher Benchmark | `MidManStudio > Utilities > Tests > Tick Dispatcher Bench` |
| Script Utilities | `MidManStudio > Utilities > Script Utilities` |

---

## Assembly StructureMidManStudio.Utilities          Runtime — autoReferenced, allowUnsafeCode
├── Unity.Burst
└── Unity.Collections
MidManStudio.Utilities.Editor   Editor only — not autoReferenced
└── MidManStudio.Utilities
MidManStudio.Utilities.Tests    Tests only (UNITY_INCLUDE_TESTS)
├── MidManStudio.Utilities
├── UnityEngine.TestRunner
└── UnityEditor.TestRunner
Your game assembly automatically sees `MidManStudio.Utilities` (autoReferenced).  
Your editor assembly needs to explicitly reference `MidManStudio.Utilities.Editor`
only if it uses pool generator, library generator, or custom inspector code.

---

## Persistent Manager Setup

Recommended hierarchy for a `Managers` prefab (DontDestroyOnLoad):Managers
├── MID_Logger              ← must be first, everything logs through it
├── MID_TickDispatcher      ← must exist before any subscriber (including TickDelay)
├── SusValueManager
├── LocalObjectPool         ← CallInitializePool() chains to LocalParticlePool
├── LocalParticlePool
├── TrailRendererPool
├── MID_AudioManager
├── MID_LibraryRegistry
└── MID_UIStateManager      ← one per screen contextDuring isolated scene testing, use `SceneDependencyInjector` instead of the bootstrap scene:

1. Add `SceneDependencyInjector` to any GameObject in your test scene
2. Drag your manager prefabs into the `Required Dependencies` list
3. Press Play — managers are instantiated automatically if not already present

---

## License

MIT — see `LICENSE.md`.  
Copyright © 2026 Abdulhamid Manman Suleiman / MidMan Studio
