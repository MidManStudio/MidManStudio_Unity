# com.midmanstudio.utilities: FXSystems

Everything in `Runtime/FXSystems` and `Editor/FXSystems` is covered here except the two generated files
`Generated/EffectCategory.cs` and `Generated/EffectType.cs`. Those are written by the Effect Type Generator and are
not edited by hand. The numbering rules are in [enum-generators.md](enum-generators.md).

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

### `FXEntry.cs`
**What it does:** The serializable list item of `GlobalFXManager`. It binds one (`EffectCategory`, `EffectType`)
pair to either an in-scene `ParticleSystem` or a set of sprite frames. Namespace `MidManStudio.Core.FX`. Fields:
`category`, `effectType`, `displayName`, `particleSystem`, `defaultParticleCount` (6, minimum 1, used when the
caller passes -1), `flipbookFrames`, `flipbookFps` (24, minimum 1).

**How it works:** `IsFlipbook` is true when `flipbookFrames` has at least one frame. If both a particle system and
frames are set, the frames win. `Name` (from `IArrayElementTitle`, the label in the inspector list) is the display
name when set, else `Category/Type (flipbook)` for a flipbook, else `Category/<particle system name>`, else
`Category/Type`.

**Gotchas:**
- The particle system must use Simulation Space World for positioning by emit parameters to work. The manager checks
  this at startup, see `GlobalFXManager.cs` above.
- `category` and `effectType` are stored as the numbers of the generated enums. See
  [enum-generators.md](enum-generators.md) for why numbers stay stable.

### `MID_FlipbookEffect.cs`
**What it does:** A pooled sprite-sheet effect. The prefab needs a `SpriteRenderer` (the component is required) and
this script, and is registered in `LocalObjectPool` as `PoolableObjectType.FlipbookEffect`. `Play(frames, fps,
position, rotation)` places the object, shows each frame for `1 / max(fps, 1)` seconds with a coroutine, and then
calls `Return()`. `Return()` can also be called at any time to stop early.

**How it works:**
- Return goes through `LocalPoolReturn.ReturnToPoolNow()` when the prefab has that component, else through
  `LocalObjectPool.ReturnObject` when the type is registered, else the object is deactivated.
- `Play` with no frames returns the object at once. A second call to `Return` is ignored until `OnEnable` runs again,
  which is how a pooled object is reused.
- The sprite is cleared when playback is reset. The inspector field `_logLevel` defaults to `None`.

**Decisions:** `GlobalFXManager` has one persistent emitter per entry, so it cannot show a hand-drawn sprite sequence
at many positions at once. The flipbook uses the pool slot `PoolableObjectType.FlipbookEffect` of the utilities block
(offset 2) and follows the same return pattern as `MID_SpawnableAudio`.

**Gotchas:**
- Timing uses `WaitForSeconds`, which follows `Time.timeScale`. A paused game freezes the flipbook.
- The sorting layer, order and material come from the prefab. The component sets none of them.
- `Play` must run on an active object, because it starts a coroutine.

### `FXEntryDefinition.cs`
**What it does:** One row of a provider asset: `entryName` (becomes the enum member, PascalCase, no spaces), `comment`
(written next to the member) and `explicitOffset` (-1 is auto-assigned, 0 or more is pinned to that offset in the
provider's block). It has the same three fields and rules as `PoolEntryDefinition`, so the pool and effect
generators treat entries the same way. Namespace `MidManStudio.Core.FX.Generator`.

**Gotchas:** Pin an entry when serialized inspector data refers to it by number. Use the same rules as in
[enum-generators.md](enum-generators.md).

### `EffectCategoryProviderSO.cs`
**What it does:** A ScriptableObject that adds entries to the generated `EffectCategory` enum. Create it with
Create > MidManStudio > Utilities > Effect Category Provider. Fields: `packageId` (default `com.mygame`, must be
unique across all providers), `displayName`, `priority` (default 100) and `entries` (list of `FXEntryDefinition`,
drawn with `[MID_NamedList]`). `EntryCount` returns the list size.

**Decisions:** Each package or game project that needs categories has its own provider, so packages never edit each
other's lists. The tooltip on `priority` reserves 0 for the utilities package and 10 for the projectile system. Game
code uses 100 or more. After a change, run MidManStudio > Utilities > Effect Type Generator.

### `EffectTypeProviderSO.cs`
**What it does:** The same as `EffectCategoryProviderSO`, for the `EffectType` enum. Create it with Create >
MidManStudio > Utilities > Effect Type Provider. Same four fields and the same defaults. It has no tooltips on the
fields.

### `EffectTypeGeneratorSettingsSO.cs`
**What it does:** Project-wide generator settings. Create it with Create > MidManStudio > Utilities > Effect Type
Generator Settings. The generator uses the first asset of this type it finds. Fields and defaults:

| Field | Default |
|---|---|
| `categoryEnumOutputPath` | `packages/com.midmanstudio.utilities/Runtime/FXSystems/Generated/EffectCategory.cs` |
| `typeEnumOutputPath` | `packages/com.midmanstudio.utilities/Runtime/FXSystems/Generated/EffectType.cs` |
| `lockFilePath` | `Assets/MidManStudio/Generated/FX/EffectTypeLock.json` (commit it) |
| `minimumBlockSize` | 100 (minimum 10) |
| `generatedNamespace` | `MidManStudio.Core.FX` |
| `autoGenerateOnAssetChange` | false |

**Gotchas:**
- The generator writes these paths as they are, relative to the Unity project root, and creates missing folders. The
  two default enum paths match the layout of the MidManStudio_Unity repository. In a project that installs the package
  from git the package lives in `Library/PackageCache` and is read-only, so the defaults would write a folder named
  `packages` next to `Assets`, which Unity does not compile. Point both paths into `Assets` before generating in such a
  project. Found by reading the code, not run in Unity.

### `PackageEffectProviderBootstrapper.cs` (Editor)
**What it does:** An `[InitializeOnLoad]` class that creates the default provider assets in
`Assets/MidManStudio/Utilities/FXProviders` when they do not exist, one frame after each domain reload. The menu item
MidManStudio > Utilities > Internal > Recreate Default Effect Providers runs the same check by hand.

**What it creates (package id `com.midmanstudio.utilities`, priority 0, all offsets pinned):**
- `EffectCategoryProvider_Utilities.asset`: `Impact` 0, `MuzzleFlash` 1, `ShellEjection` 2, `Explosion` 3.
- `EffectTypeProvider_Utilities.asset`: `Generic` 0, `MetalSurface` 1, `ConcreteSurface` 2, `DirtSurface` 3,
  `WoodSurface` 4, `FleshSurface` 5, `SmallMuzzle` 10, `MediumMuzzle` 11, `LargeMuzzle` 12, `BrassShell` 20,
  `SteelShell` 21, `SmallExplosion` 30, `MediumExplosion` 31, `LargeExplosion` 32.

**Gotchas:**
- Only the default path is checked. If the asset is moved or renamed, a new one is created at the default path on the
  next reload, and the project then has two providers with the same package id. Delete the copy you do not want.
- It does not run the generator. Run MidManStudio > Utilities > Effect Type Generator afterwards.

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

### `FXEntry.cs`
- Moved out of the file header: the description of category and type, and the note that both enums are generated by
  the Effect Type Generator. Both are in the section above. No fixes were made to this file.

### `MID_FlipbookEffect.cs`
- Moved out of the class comment: why the class exists (a persistent `ParticleSystem` cannot play a sprite sequence at
  many positions), the link to the pool slot `FlipbookEffect` in the utilities block, the likeness to
  `MID_SpawnableAudio`, and the setup and usage steps. All are in the section above and in the class summary.
- Not fixed: each frame step creates a new `WaitForSeconds`, and each `Play` creates a coroutine, so a flipbook
  allocates garbage while it plays. This matters when many flipbooks play per second.
- Not fixed: playback follows `Time.timeScale` (see Gotchas above).

### `FXEntryDefinition.cs`, `EffectCategoryProviderSO.cs`, `EffectTypeProviderSO.cs`, `EffectTypeGeneratorSettingsSO.cs`
- Moved out of the file headers: setup steps, the priority reservation and the menu paths. They are in the sections
  above. The code is unchanged.
- Not fixed: the default output paths of the settings asset assume the repository layout, see the gotcha under
  `EffectTypeGeneratorSettingsSO.cs`.
- Not fixed: `EffectTypeProviderSO` has no tooltips on its fields, while `EffectCategoryProviderSO` has them. This is
  a string change in the code, so it is left for a code pass.

### `PackageEffectProviderBootstrapper.cs`
- Not fixed: a moved or renamed default provider asset is created again at the default path, which gives two
  providers with the same package id. Found by reading the code.
