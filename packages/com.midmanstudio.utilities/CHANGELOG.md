# com.midmanstudio.utilities — Package Catalog
**MidMan Studio Utilities** v1.0.0 | Unity 2022.3+  
Last updated: 2026-06-15

> ⚠ **Discrepancy fixes applied in this catalog are marked with ⚠ FIX.**  
> 🗑 Items marked **DELETE** are empty or misplaced and should be removed.  
> ✅ Items marked **CORRECT** were already right.

---

## Full Folder Tree

```
com.midmanstudio.utilities/
│
├── package.json
├── CHANGELOG.md
├── LICENSE.md
├── README.md
│
├── Runtime/                                         ← MidManStudio.Utilities.asmdef
│   ├── MidManStudio.Utilities.asmdef                ← ⚠ FIX: add Burst + Collections + Mathematics refs
│   │
│   ├── TickDispatcher/
│   │   ├── MID_TickDispatcher.cs                    ← Main dispatcher (MonoSingleton)
│   │   ├── MID_NativeTickDispatcher.cs              ← Burst IJob wrapper
│   │   ├── MID_TickDelay.cs                         ← Zero-alloc delay/repeat scheduler
│   │   ├── TickDelayHandle.cs                       ← Cancellation token struct
│   │   └── TickRate.cs                              ← Enum: Tick_0_01 → Tick_5
│   │
│   ├── Logging/
│   │   ├── MID_Logger.cs                            ← Static level-gated coloured logger
│   │   └── MID_LogLevel.cs                          ← Enum: None, Error, Info, Debug, Verbose
│   │
│   ├── Singletons/
│   │   ├── MID_Singleton.cs                         ← Pure C# singleton base
│   │   └── MID_MonoSingleton.cs                     ← MonoBehaviour singleton base
│   │
│   ├── ObservableValues/
│   │   ├── MID_SusValue.cs                          ← Generic reactive value container
│   │   ├── ManagedSusValue.cs                       ← ⚠ FIX: remove unsafe finalizer (see notes)
│   │   └── SusValueManager.cs                       ← Registry for managed values (MonoSingleton)
│   │
│   ├── Events/
│   │   ├── MID_GameEventSO.cs                       ← ScriptableObject event channel
│   │   ├── MID_GameEventListener.cs                 ← MonoBehaviour listener with UnityEvent response
│   │   ├── MID_DelayedGameEventListener.cs          ← ⚠ FIX: cache delegate to avoid alloc per-fire
│   │   └── MID_TypedEventBus.cs                     ← Static generic event bus (no SO dependency)
│   │
│   ├── PoolSystems/
│   │   ├── LocalObjectPool.cs                       ← GameObject pool (MonoSingleton)
│   │   ├── LocalParticlePool.cs                     ← ParticleSystem pool (MonoSingleton)
│   │   ├── TrailRendererPool.cs                     ← TrailRenderer pool (MonoSingleton)
│   │   ├── LocalPoolReturn.cs                       ← Auto-return component (added by pool on spawn)
│   │   ├── IPoolable.cs                             ← OnSpawn / OnReturn interface
│   │   ├── PoolTypeSettings/
│   │   │   ├── PoolTypeGeneratorSettingsSO.cs       ← Root generator SO (holds all providers)
│   │   │   ├── PoolableObjectTypeProviderSO.cs      ← Per-package object type list
│   │   │   └── PoolableParticleTypeProviderSO.cs    ← Per-package particle type list
│   │   └── Generated/                               ← ⚠ FIX: moved here from PoolSystems/ root
│   │       ├── PoolableObjectType.cs                ← Generated enum (was: PoolSystems/Generated/)
│   │       └── PoolableParticleType.cs              ← Generated enum (was: PoolSystems/Generated/)
│   │
│   ├── Audio/
│   │   ├── MID_AudioManager.cs                      ← Music crossfade/pitch + SFX pool (MonoSingleton)
│   │   ├── MID_AudioLimiter.cs                      ← DSP peak limiter; Rust on desktop, C# on WebGL
│   │   ├── MID_NativeAudioBridge.cs                 ← 16-voice AudioSource steal pool (MonoSingleton)
│   │   ├── MID_AudioLibrarySO.cs                    ← ⚠ FIX: moved here from Libraries/Configs/
│   │   └── Plugins/
│   │       ├── Windows/
│   │       │   └── mid_audio_limiter.dll
│   │       ├── macOS/
│   │       │   └── mid_audio_limiter.bundle
│   │       ├── Android/
│   │       │   └── libmid_audio_limiter.so
│   │       └── Linux/
│   │           └── mid_audio_limiter.so
│   │       (WebGL: no plugin — C# fallback in MID_AudioLimiter.cs used automatically)
│   │
│   ├── FXSystems/
│   │   ├── GlobalFXManager.cs                       ← ⚠ FIX: namespace MidManStudio.Core.FX (not .Audio)
│   │   ├── FXEntry.cs                               ← (Category, Type) → ParticleSystem + AudioClip binding
│   │   ├── FXTypeSettings/
│   │   │   ├── EffectCategoryProviderSO.cs          ← Per-package category list
│   │   │   └── EffectTypeProviderSO.cs              ← Per-package type list
│   │   └── Generated/
│   │       ├── EffectCategory.cs                    ← Generated enum ✅ (already in Generated/)
│   │       └── EffectType.cs                        ← Generated enum ✅ (already in Generated/)
│   │
│   ├── Timers/
│   │   ├── MID_CountdownTimer.cs                    ← Countdown with pause/resume + events
│   │   ├── MID_Stopwatch.cs                         ← Elapsed time tracker
│   │   ├── MID_InterpolationTimer.cs                ← AnimationCurve-evaluated 0→1 progress
│   │   ├── MID_SteppedTimer.cs                      ← N-step interval timer
│   │   └── MID_NetworkTimer.cs                      ← NGO NetworkVariable-backed synced timer
│   │
│   ├── Libraries/
│   │   ├── MID_LibraryRegistry.cs                   ← Keyed SO asset registry (MonoSingleton)
│   │   ├── MID_LibrarySO.cs                         ← Library ScriptableObject
│   │   ├── MID_LibraryItemSO.cs                     ← Abstract item base ScriptableObject
│   │   ├── MID_BasicLibraryItemSO.cs                ← Concrete: displayName + icon + description
│   │   ├── LibraryTypeSettings/
│   │   │   └── LibraryTypeGeneratorSettingsSO.cs    ← Root generator SO
│   │   └── Generated/                               ← ⚠ FIX: moved here from Libraries/Generator/
│   │       ├── LibraryId.cs                         ← Generated enum (was: Libraries/Generator/)
│   │       └── LibraryItemId.cs                     ← Generated enum (was: Libraries/Generator/)
│   │
│   ├── SceneManagement/
│   │   ├── MID_SceneManager.cs                      ← Async scene loader (MonoSingleton)
│   │   ├── MID_SceneTransitionController.cs         ← Fade in/out transition controller
│   │   ├── SceneTypeSettings/
│   │   │   └── SceneTypeGeneratorSettingsSO.cs      ← Root generator SO
│   │   └── Generated/
│   │       ├── SceneId.cs                           ← Generated enum ✅
│   │       └── SceneRegistry.cs                     ← Generated static metadata class ✅
│   │
│   ├── UIState/
│   │   ├── MID_UIStateManager.cs                    ← Stack-based [Flags] state machine driver
│   │   ├── MID_UIStateContext.cs                    ← SO: contextName + stateNames list
│   │   ├── MID_UIStateVisibility.cs                 ← Show/hide based on state flags match
│   │   ├── MID_UIStateButton.cs                     ← Transitions to target state on click
│   │   ├── MID_UIStateBackButton.cs                 ← ⚠ FIX: add to APICATALOG (was missing)
│   │   └── Generated/                               ← Context-specific [Flags] enums written here
│   │       └── (e.g. MenuUIState.cs, HUDUIState.cs)
│   │   🗑 DELETE: UIState/Generator/               ← Empty folder — context provider merged into
│   │                                                   MID_UIStateContext.cs; Generator/ serves no purpose
│   │
│   ├── UI/
│   │   └── MID_UIElement.cs                         ← CanvasGroup animated show/hide base component
│   │
│   ├── HelperFunctions/
│   │   └── MID_HelperFunctions.cs                   ← ⚠ FIX: rename Kill→Destroy methods (see APICATALOG)
│   │
│   ├── SequentialProcessing/
│   │   ├── MID_SequentialRunner.cs                  ← Priority lane async task runner (MonoSingleton)
│   │   └── MID_SequentialTask.cs                    ← Abstract task base with retry logic
│   │
│   └── StickyNote/
│       └── MID_StickyNote.cs                        ← In-Game-View overlay (Edit + Play Mode)
│
├── Editor/                                          ← MidManStudio.Utilities.Editor.asmdef
│   ├── MidManStudio.Utilities.Editor.asmdef
│   ├── LoggerManager/
│   │   └── MID_LoggerManagerWindow.cs               ← Bulk log level editor across scene objects
│   ├── PoolTypeGenerator/
│   │   └── MID_PoolTypeGeneratorWindow.cs           ← Writes PoolSystems/Generated/ files
│   ├── LibraryTypeGenerator/
│   │   └── MID_LibraryTypeGeneratorWindow.cs        ← Writes Libraries/Generated/ files
│   ├── SceneTypeGenerator/
│   │   └── MID_SceneTypeGeneratorWindow.cs          ← Writes SceneManagement/Generated/ files
│   ├── UIStateGenerator/
│   │   └── MID_UIStateContextGeneratorWindow.cs     ← Writes UIState/Generated/ files
│   ├── FXTypeGenerator/
│   │   └── MID_FXTypeGeneratorWindow.cs             ← Writes FXSystems/Generated/ files
│   ├── ScriptUtilities/
│   │   └── MID_ScriptUtilitiesWindow.cs             ← Misc editor script helpers
│   └── SceneDependencyInjector/
│       └── SceneDependencyInjector.cs               ← MonoBehaviour: auto-spawn missing managers
│
├── Tests/
│   ├── Runtime/
│   │   ├── MidManStudio.Utilities.Tests.asmdef
│   │   ├── TickDispatcher/
│   │   │   └── MID_TickDispatcherTests.cs
│   │   └── TickDelay/
│   │       └── MID_TickDelayTests.cs
│   └── Editor/
│       ├── MidManStudio.Utilities.Tests.Editor.asmdef
│       ├── Audio/
│       │   └── MID_AudioBenchmark.cs
│       ├── TickDispatcher/
│       │   └── MID_TickDispatcherBenchmark.cs
│       └── TickDelay/
│           └── MID_TickDelayBenchmark.cs
│
└── Samples~/
    └── SetupDemo/
        ├── SetupDemo.unity
        ├── Managers.prefab                          ← Pre-wired persistent manager hierarchy
        └── README.md
```

---

## Project-Level Files (NOT inside the package)

These live in the consuming Unity project under `Assets/`, not inside the UPM package:

```
Assets/MidManStudio/Generated/
├── Pools/
│   └── PoolTypeLock.json          ← Stabilises enum offsets across regenerations
└── FX/
    └── EffectTypeLock.json        ← Stabilises FX enum offsets
```

> **Always commit both lock files to source control.**  
> Deleting them allows enum values to shift, breaking saved scene references.

---

## Generated File Summary

| Generator | Writes to | Files produced |
|---|---|---|
| Pool Type Generator | `Runtime/PoolSystems/Generated/` | `PoolableObjectType.cs`, `PoolableParticleType.cs` |
| FX Type Generator | `Runtime/FXSystems/Generated/` | `EffectCategory.cs`, `EffectType.cs` |
| Library Type Generator | `Runtime/Libraries/Generated/` | `LibraryId.cs`, `LibraryItemId.cs` |
| Scene Type Generator | `Runtime/SceneManagement/Generated/` | `SceneId.cs`, `SceneRegistry.cs` |
| UI State Generator | `Runtime/UIState/Generated/` | `{Context}UIState.cs` (one per context) |

---

## Namespace Map

| Folder | Namespace |
|---|---|
| TickDispatcher/ | `MidManStudio.Core.TickDispatcher` |
| Logging/ | `MidManStudio.Core.Logging` |
| Singletons/ | `MidManStudio.Core.Singleton` |
| ObservableValues/ | `MidManStudio.Core.ObservableValues` |
| Events/ | `MidManStudio.Core.Events` |
| PoolSystems/ | `MidManStudio.Core.Pools` |
| PoolSystems/Generated/ | `MidManStudio.Core.Pools` |
| Audio/ | `MidManStudio.Core.Audio` |
| FXSystems/ | `MidManStudio.Core.FX` ← ⚠ FIX (was .Audio) |
| FXSystems/Generated/ | `MidManStudio.Core.FX` |
| Timers/ | `MidManStudio.Core.Timers` |
| Libraries/ | `MidManStudio.Core.Libraries` |
| Libraries/Generated/ | `MidManStudio.Core.Libraries` |
| SceneManagement/ | `MidManStudio.Core.SceneManagement` |
| UIState/ | `MidManStudio.Core.UIState` |
| UI/ | `MidManStudio.Core.UI` |
| HelperFunctions/ | `MidManStudio.Core.HelperFunctions` |
| SequentialProcessing/ | `MidManStudio.Core.SequentialProcessing` |
| StickyNote/ | `MidManStudio.Core.Notes` |
| Editor/ | `MidManStudio.Editor` |

---

## Assembly Structure

```
MidManStudio.Utilities                    autoReferenced: true  | allowUnsafeCode: true
├── Unity.Burst          (⚠ FIX: was missing)
├── Unity.Collections    (⚠ FIX: was missing)
└── Unity.Mathematics    (⚠ FIX: was missing)

MidManStudio.Utilities.Editor             autoReferenced: false | Editor only
├── MidManStudio.Utilities
├── Unity.Burst
└── Unity.Collections

MidManStudio.Utilities.Tests              autoReferenced: false | UNITY_INCLUDE_TESTS
├── MidManStudio.Utilities
├── UnityEngine.TestRunner
└── UnityEditor.TestRunner

MidManStudio.Utilities.Tests.Editor      autoReferenced: false | UNITY_INCLUDE_TESTS | Editor only
├── MidManStudio.Utilities
├── Unity.Burst
└── Unity.Collections
```

---

## Discrepancy Fix Summary

| # | Location | Issue | Fix |
|---|---|---|---|
| 1 | `PoolSystems/` root | Generated files at root level | Move to `PoolSystems/Generated/` |
| 2 | `Libraries/Generator/` | Generated files in wrong subfolder | Move to `Libraries/Generated/` |
| 3 | `MID_HelperFunctions.cs` | Methods named `KillObjChildren` / `KillMultipleParentsChildren` | Rename to `DestroyObjChildren` / `DestroyMultipleParentsChildren` |
| 4 | `MidManStudio.Utilities.asmdef` | `references: []` — Burst/Collections/Mathematics missing | Add all three |
| 5 | `GlobalFXManager.cs` | Namespace `MidManStudio.Core.Audio` | Change to `MidManStudio.Core.FX` |
| 6 | `Libraries/Configs/MID_AudioLibrarySO.cs` | Wrong folder for an Audio-namespace file | Move to `Audio/` |
| 7 | `MID_DelayedGameEventListener.cs` | Method group delegate allocated per-fire | Cache `Action _fireDelayedDelegate` in Awake |
| 8 | `ManagedSusValue.cs` | Finalizer calls Unity singleton from GC thread | Remove finalizer; rely on OnDestroy/ClearAllForOwner |
| 9 | `UIState/Generator/` | Empty folder | Delete it |
| 10 | APICATALOG | `MID_UIStateBackButton` undocumented | Added to Section 13 |
| 11 | APICATALOG | `int typeId` pool overloads listed but don't exist | Removed from catalog |
