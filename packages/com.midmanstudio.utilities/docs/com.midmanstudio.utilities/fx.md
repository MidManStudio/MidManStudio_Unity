# com.midmanstudio.utilities: FXSystems

Only the files touched so far are covered here. The rest of `Runtime/FXSystems` is not yet documented.

## Modules

### `GlobalFXManager.cs`
**What it does:** Singleton that plays effects by category and type at a world position. A list of `FXEntry`
items maps each (category, type) pair to an in-scene `ParticleSystem` or to sprite-sheet flipbook frames.
`TriggerEffect`, `TriggerImpact`, `TriggerMuzzleFlash` and `EjectShell` look the entry up and either emit
particles or play a pooled `MID_FlipbookEffect`. `TryGetEffect` returns the particle system for a pair. For each
effect that plays it raises the static event `CategoryAudioRequested(EffectCategory, float volume)`.

**How it works:**
- Entries are indexed by `category * 100000 + type`. When the exact pair has no entry, the lookup falls back to
  the `Generic` type of the same category, and logs a warning when that is missing too.
- Each category has a cap per frame (impact 12, muzzle flash 8, shell ejection 16, explosion 4, anything else 8).
  Impacts and explosions are also dropped when another effect of the same category already played within
  `_spatialThrottleRadius` (0.4) of the position in the same frame. Both counters reset in `LateUpdate`.
- `Awake` builds the lookup and validates every particle system. It forces Simulation Space to World and turns
  Loop off, each with a warning.
- A flipbook entry needs `PoolableObjectType.FlipbookEffect` registered in `LocalObjectPool`. Without it the
  manager logs a warning and plays nothing for that entry.

**Decisions:**
- The overloads without an `EffectType` default to `Generic` (impact), `SmallMuzzle` (muzzle flash) and
  `BrassShell` (shell ejection). They stay so call sites written before `EffectType` existed keep working. The
  last parameter of `TriggerImpact(Vector3, Vector3, ...)` is named `volumeOverride` to match the named-argument
  call in `TestSceneBootstrapper`.
- Audio is a static event and not a reference. The audio package depends on utilities, so utilities cannot
  reference it. A plain C# event is used instead of `MID_EventBus<T>` because `Raise` allocates a snapshot array
  on every call, and effects are triggered many times per frame. `MID_FXAudioRelay` in `com.midmanstudio.audio`
  subscribes while enabled.

## Fixes and Problems

### `GlobalFXManager.cs`
**Change:** The manager used to hold a `MID_NativeAudioBridge` reference and four clip-index fields, and played
the clip itself. They were removed when audio moved to its own package. The serialized values stay unused in
existing scenes. Add `MID_FXAudioRelay` once per scene and enter the clip indices there. The default indices
are the same as before: impact 0, muzzle flash 1, shell ejection 2, explosion -1.

**Fix:** The APICATALOG entry for `FXEntry` listed an `audioClip` field and the manager entry listed an
`AudioBridge` field. `FXEntry` has no audio clip, and the bridge reference is gone. Both rows were removed.
