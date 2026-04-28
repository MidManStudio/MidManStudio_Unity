# Changelog — com.midmanstudio.utilities

All notable changes to this package are documented here.  
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

---

## [1.1.0] — Unreleased

### Added
- **Pool Type Generator** — editor tool that writes `PoolableObjectType.cs` and  
  `PoolableParticleType.cs` from `PoolTypeProviderSO` / `ParticlePoolTypeProviderSO` assets.  
  Replaces the old closed `PoolableObjectType` enum.
  - `PoolTypeProviderSO` — object pool entry provider asset
  - `ParticlePoolTypeProviderSO` — particle pool entry provider asset
  - `PoolTypeGeneratorSettingsSO` — configurable output paths, block size, namespace
  - `PackagePoolProviderBootstrapper` — auto-creates default provider assets on import
  - Lock file (`PoolTypeLock.json`) prevents value shifts across regenerations
  - `explicitOffset` pinning for entries that must never change value
  - Collision detection across providers (duplicate IDs, duplicate entry names)
  - C# keyword validation on entry names
  - Auto-generate on asset change (optional, disabled by default)
- **`TrailRendererPool`** — generic slot-based trail renderer pool.  
  Replaces the old project-specific `TrailObjectPool` that was in the projectile package.
- **`ManagedSusValue<T>`** — `MID_SusValue` subclass that auto-registers with `SusValueManager`.
- **`SusValueManager`** — tracks managed values, bulk-clears by owner GameObject.
- **`SusValueOwnerWatcher`** — internal MonoBehaviour placed on owning GameObjects  
  to trigger cleanup on `OnDestroy`.
- **`StaticContentSingleton<T>`** — thread-safe lazy singleton for plain C# classes.  
  Replaces the previous version; now supports `IStaticSingletonInitializable` and  
  `System.IDisposable` on the wrapped type.
- **`UIParticlePoolManager`** — reworked to use string keys instead of a closed enum.  
  Fully extensible. Effects registered in inspector or at runtime.
- **`SceneDependencyInjector`** — editor-only tool, now uses `MID_Logger` instead of  
  raw `Debug.Log`, game-specific Quick Add buttons removed.
- **`MID_AudioManager.SetMusicEnabled(bool)`** — runtime music enable/disable with crossfade.  
  `OnMusicEnabledChanged` event replaces the old `FP_SettingsManager` dependency.

### Changed
- `LocalObjectPool` and `LocalParticlePool` now key internally on `int` (the underlying  
  value of the generated enum) so new providers can be added without modifying pool code.
  Public API still accepts `PoolableObjectType` / `PoolableParticleType` overloads.
- `BasicPoolConfig` and `ParticlePoolConfig` now use `int typeId` + `string displayName`  
  instead of a closed enum field.
- `MID_HelperFunctions` — all game-specific methods removed (sprite atlas loading,  
  cloud save checks, item identification, `EQItemCustomInventoryData` references).  
  JSON now uses `JsonUtility` (no Newtonsoft dependency). Logging routes to `MID_Logger`.
- `MID_AudioManager` — all `FP_SettingsManager` and analytics references removed.
- `LocalPoolReturn` / `LocalParticleReturn` — updated to use generated enums.

### Removed
- Closed `PoolableObjectType` enum (replaced by code-generated file)
- Closed `PoolableParticleType` enum (replaced by code-generated file)
- `MID_HelperFunctions.GetAndReturnMainItemSpriteAndFixImageOrIcon` and related sprite helpers
- `MID_HelperFunctions.CloudBasedScriptRequirementsMet`
- `MID_HelperFunctions.isFlipableSprite`, `isSummon`
- `MID_AudioManager` dependency on `FP_SettingsManager`
- `UIParticlePoolManager.UIEffectType` closed enum

---

## [1.0.0] — Initial Release

### Added
- `MID_TickDispatcher` — managed zero-allocation interval-based tick system
- `MID_NativeTickDispatcher` — Burst-compiled native tick dispatcher
- `MID_Logger` + `MID_LogLevel` + `MID_LoggerSettings` + `MID_LoggerEditorWindow`
- `Singleton<T>` — MonoBehaviour singleton base class
- `MID_SusValue<T>` — generic observable value container
- `LocalObjectPool` + `LocalParticlePool` — pooling systems with prewarm and auto-registration
- `LocalPoolReturn` + `LocalParticleReturn` — auto-return components
- `MID_AudioManager` — music (crossfade, pitch glide) + SFX (one-shot, pitched)
- `MID_SpawnableAudio` — pooled audio object (one-shot, looping, sequential)
- `MID_AudioLibrarySO` — string-keyed audio clip registry
- `MID_LibrarySO` + `MID_LibraryItemSO` + `MID_LibraryRegistry` — generic asset library
- `CountdownTimer`, `StopwatchTimer`, `ValueInterpolationTimer`, `SteppedValueTimer`
- `MID_Button` — animated UI button with LeanTween (scale pop, bounce, shake, fade, etc.)
- `MID_HelperFunctions` — static utility methods
- `MID_NamedListAttribute` + `NamedListDrawer` — named inspector list drawing
- `DynamicDebugPanel` — runtime debug overlay (editor only)
- `PerformanceBenchmarkTimer` — microbenchmark utility
