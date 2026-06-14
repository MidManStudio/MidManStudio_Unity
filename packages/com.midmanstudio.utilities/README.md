
# com.midmanstudio.utilities
**MidMan Studio Utilities** v1.0.0 — Core runtime utilities for Unity 2022.3+.
No game-specific dependencies. Foundation for all MidManStudio packages.
## Requirements
| Dependency | Version |
|---|---|
| Unity | 2022.3 LTS |
| com.unity.burst | 1.8.9+ |
| com.unity.collections | 2.2.1+ |
| com.unity.mathematics | 1.3.1+ |
## Installation
**Via git URL** (Unity Package Manager → Add package from git URL):
https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.utilities#v1.0.0
**Via local file path** (development — manifest.json):
"com.midmanstudio.utilities": "file:../../packages/com.midmanstudio.utilities"
Dependencies are resolved automatically by UPM.
## What's Included
| System | Namespace | Description |
|---|---|---|
| Tick Dispatcher | MidManStudio.Core.TickDispatcher | Zero-alloc interval-based Update replacement |
| Tick Delay | MidManStudio.Core.TickDispatcher | Zero-alloc delayed/repeating actions, NGO-ready |
| Logger | MidManStudio.Core.Logging | Level-gated coloured console logger |
| Singletons | MidManStudio.Core.Singleton | MonoBehaviour + pure C# singleton bases |
| Observable Values | MidManStudio.Core.ObservableValues | Reactive value containers with auto-cleanup |
| Events | MidManStudio.Core.Events | SO event channels + typed static event bus |
| Pool System | MidManStudio.Core.Pools | Object, particle, and trail renderer pools |
| Pool Type Generator | MidManStudio.Core.Pools.Generator | Code-generates shared pool enums from SO providers |
| Audio Manager | MidManStudio.Core.Audio | Music crossfade/pitch + SFX pool manager |
| Audio Limiter | MidManStudio.Core.Audio | Rust DSP peak limiter on AudioListener; C# fallback on WebGL |
| Native Audio Bridge | MidManStudio.Core.Audio | 16-voice AudioSource pool with circular steal |
| FX System | MidManStudio.Core.FX | Unified CPU particle + audio effect manager |
| FX Type Generator | MidManStudio.Core.FX.Generator | Code-generates EffectCategory/EffectType enums |
| Timers | MidManStudio.Core.Timers | Countdown, stopwatch, interpolation, stepped, network |
| Library System | MidManStudio.Core.Libraries | Keyed ScriptableObject asset registry |
| Scene Management | MidManStudio.Core.SceneManagement | Async loader + transition controller |
| UI State System | MidManStudio.Core.UIState | Per-context [Flags] enum state machine |
| UI Components | MidManStudio.Core.UI | Animated button with no tween dependency |
| Helpers | MidManStudio.Core.HelperFunctions | String, UI, JSON, reflection utilities |
| Sequential Runner | MidManStudio.Core.SequentialProcessing | Priority lane async task runner with retry |
| Sticky Note | MidManStudio.Core.Notes | In-Game-View overlay note for scene setup docs |
| Editor Tools | — | Logger manager, pool/library/scene/UI generators, dependency injector |
## Quick Start
### Tick Dispatcher
Replaces per-MonoBehaviour Update(). A system that only needs 5 checks/sec should not run 60×/sec.
**Rate selection guide:**
| Rate | Fires/sec | Use for |
|---|---|---|
| Tick_0_05 | 20 | Fast weapon systems, projectile checks |
| Tick_0_1 | 10 | ✅ Recommended minimum — fast AI, cooldowns |
| Tick_0_2 | 5 | Standard — enemy AI, ability systems |
| Tick_0_5 | 2 | Area checks, perception |
| Tick_1 | 1 | Health regen, UI numbers |
| Tick_2 | 0.5 | Distant object updates |
| Tick_5 | 0.2 | Spawners, wave logic |
⚠ Tick_0_01 and Tick_0_02 fire faster than a typical frame — negative saving at normal fps. Never use for gameplay logic.
### Tick Delay — Zero-Alloc Delayed Actions
The zero-GC alternative to StartCoroutine and Task.Delay. Works inside Netcode for GameObjects RPCs where IEnumerator is forbidden.
**Trade-off comparison:**
|  | MID_TickDelay | Coroutine | Task.Delay |
|---|---|---|---|
| GC allocation | **0 B always** | ~80–400 B/call | ~120 B cold |
| Thread | **Main** | Main | Threadpool — unsafe for Unity APIs |
| IEnumerator | **Not needed** | Required — breaks RPC signatures | Not needed |
| Cancellation | **TickDelayHandle** | StopCoroutine | CancellationToken (+alloc) |
| Timing error | 0–100ms at Tick_0_1 | 0–16ms at 60fps | ~0–2ms (OS timer) |
### Logger
Bulk-manage log levels across all scene objects: MidManStudio > Utilities > Logger Manager.
**Log levels:**
| Level | Output |
|---|---|
| None | No output |
| Error | Errors only |
| Info | Info + warnings + errors ← production recommended |
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
 2. Add FXEntry items in the inspector — one per (EffectCategory, EffectType) pair
 3. Assign in-scene ParticleSystem references — must have **Simulation Space = World**
 4. Optionally assign a MID_NativeAudioBridge for per-category audio
**Adding custom categories/types:**
 1. Create EffectCategoryProviderSO or EffectTypeProviderSO (right-click Project)
 2. Set priority ≥ 100 for game code (0 = utilities reserved, 10 = projectile reserved)
 3. MidManStudio > Utilities > Effect Type Generator > Generate Now
### Audio Manager
Features crossfade/pitch for music, and a reliable SFX pool manager for one-shots.
### Native Audio Bridge
16-voice AudioSource pool. Any load type works — Decompress On Load is **not** required.
### Audio Limiter
Peak limiter for the final mixed output. Attach to the **AudioListener** GameObject.
 * Desktop/Mobile: Rust DSP via DLL — zero managed allocation in DSP path
 * WebGL: Pure C# fallback — same threshold/attack/release behaviour
### Observable Values
Use MID_SusValue<T> for plain values (manual cleanup) or ManagedSusValue<T> to have subscriptions cleared automatically when the owner GameObject is destroyed.
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
 1. Right-click > MidManStudio > Utilities > Library Item (Basic) — create items
 2. Right-click > MidManStudio > Utilities > Library — create library, add items
 3. Add MID_LibraryRegistry to a persistent GameObject, assign library
 4. Retrieve via MID_LibraryRegistry.Instance.GetItem(...)
### Sticky Note
In-Game-View overlay for scene documentation, setup instructions, or tutorial content. Works in Play Mode and Edit Mode. Supports drag-to-reposition, minimize, and close.
## Pool Type Generator
Writes PoolableObjectType.cs and PoolableParticleType.cs under Runtime/PoolSystems/Generated/.
### Adding your own pool types
 1. MidManStudio > Utilities > Pool Type Generator > + Object Provider
 2. Set packageId (e.g. com.mygame), priority ≥ 100, add entry names (PascalCase)
 3. Generate Now
### Block ranges
| Priority | Package | Block |
|---|---|---|
| 0 | com.midmanstudio.utilities | 0–99 |
| 10 | com.midmanstudio.projectilesystem | 100–199 |
| 100+ | Your game | 200+ |
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
| Tick Delay Benchmark | MidManStudio > Utilities > Tests > Tick Delay Bench |
| Tick Dispatcher Benchmark | MidManStudio > Utilities > Tests > Tick Dispatcher Bench |
| Audio Benchmark | MidManStudio > Utilities > Tests > Audio Bench |
| Script Utilities | MidManStudio > Utilities > Script Utilities |
| Scene Dependency Injector | Add SceneDependencyInjector component to any scene GameObject |
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
**Always commit lock files to source control** — they prevent enum value shifts on regeneration.
## Persistent Manager Setup
Recommended hierarchy for a Managers prefab (DontDestroyOnLoad):
 1. MID_Logger
 2. MID_TickDispatcher
 3. SusValueManager
 4. LocalObjectPool
 5. LocalParticlePool
 6. TrailRendererPool
 7. MID_AudioManager
 8. MID_NativeAudioBridge
 9. GlobalFXManager
 10. MID_LibraryRegistry
 11. MID_UIStateManager
**For isolated scene testing:** use SceneDependencyInjector instead of a bootstrap scene. Assign required manager prefabs in Required Dependencies and press Play.
## License
MIT — see LICENSE.md.
Copyright © 2026 Abdulhamid Manman Suleiman / MidMan Studio
