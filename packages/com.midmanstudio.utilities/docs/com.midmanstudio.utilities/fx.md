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

### `EffectTypeGenerator.cs`
**What it does:** Editor-only. Builds `EffectCategory.cs` and `EffectType.cs` from every `EffectCategoryProviderSO`
and `EffectTypeProviderSO` asset in the project. The file also holds the window
`MidManStudio > Utilities > Effect Type Generator` and an asset postprocessor that generates again when a provider
or the settings asset changes, if Auto-Generate is on in the settings (`EffectTypeGeneratorSettingsSO`).

**How it works:**
- `Generate` reads the providers for each enum and passes them to `EnumBlockLogic.Assign`. The numbering rules are
  in [enum-generators.md](enum-generators.md). When both enums come out without errors, it writes the two enum
  files and then the lock file. With an error in either, it writes nothing.
- The lock file is at `lockFilePath` in the settings, `Assets/MidManStudio/Generated/FX/EffectTypeLock.json` by
  default. It is meant to be committed. A lock file that cannot be read stops the run with an error.
- The text of the enum files is the same as before: a header, one comment line per block, then
  `Name = value, // comment //[pinned]`. The header still carries a `Generated:` timestamp, so every run rewrites
  both files even when no number changed.

**Decisions:**
- The numbering moved out of this file into `EnumBlockLogic.cs`, so the effect and pool generators cannot drift
  apart.
- Output paths, block size, namespace and the Auto-Generate switch are unchanged.

**Tests:** the numbering is covered by `Tests/Editor/EnumBlockLogicTests.cs`, see [enum-generators.md](enum-generators.md).
The generator and the window have no tests.

## Fixes and Problems

### `GlobalFXManager.cs`
**Change:** The manager used to hold a `MID_NativeAudioBridge` reference and four clip-index fields, and played
the clip itself. They were removed when audio moved to its own package. The serialized values stay unused in
existing scenes. Add `MID_FXAudioRelay` once per scene and enter the clip indices there. The default indices
are the same as before: impact 0, muzzle flash 1, shell ejection 2, explosion -1.

**Fix:** The APICATALOG entry for `FXEntry` listed an `audioClip` field and the manager entry listed an
`AudioBridge` field. `FXEntry` has no audio clip, and the bridge reference is gone. Both rows were removed.

### `EffectTypeGenerator.cs`
**Problem:** Adding an effect type or category before the existing ones in a provider list renumbered all of
them, and the stored numbers in scenes and assets then pointed at other effects. **Fix:** Entries keep their
number wherever they sit in the list, and removed numbers stay reserved. See `EnumBlockLogic.cs` in
[enum-generators.md](enum-generators.md).

**Change:** A lock file in the old format is read as before. The first run does not change any number the old
lock file holds, and it writes the block ranges and the retired numbers next to the entries. A project that was
renumbered by the old generator keeps the numbers it has now. Other changes to behavior: a name that is empty
after trimming is ignored with a warning, and a name that is not a valid C# member name is an error (the old
generator wrote both into the file). A pinned offset past the end of a block makes the block larger instead of
stopping the run. A lock file that cannot be read is an error (the old generator started again from nothing with
a warning). Both enums are now worked out before any file is written. The old generator wrote the category enum
even when the type enum then failed.

**Problem (open):** The auto-generate hook does not notice a deleted provider asset, because the type of a
deleted path cannot be looked up. This comes from reading the code and was not tried in Unity. Generate by hand
after deleting a provider. The `Generated:` timestamp (see above) makes every run change both files.
