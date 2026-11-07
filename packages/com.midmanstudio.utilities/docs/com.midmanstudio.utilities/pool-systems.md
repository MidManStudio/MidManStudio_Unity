# com.midmanstudio.utilities: PoolSystems

The pool systems keep reusable GameObjects by type. The runtime files are in `Runtime/PoolSystems/` (namespace
`MidManStudio.Core.Pools`, the provider assets in `MidManStudio.Core.Pools.Generator`). The generator and the inspectors are in
`Editor/PoolSystems/`. Two files in `Runtime/PoolSystems/Generated/`, `PoolableObjectType.cs` and `PoolableParticleType.cs`,
are written by the generator and are not documented one by one.

The pieces fit together like this: provider assets (`ObjectPoolTypeProviderSO`, `ParticlePoolTypeProviderSO`) list type
names. The generator turns them into the two enums. `LocalObjectPool` and `LocalParticlePool` pool prefabs by those enums and
add a return component to each instance. `TrailRendererPool` and `UIParticlePoolManager` are separate helpers.

## Modules

### `PackagePoolProviderBootstrapper.cs`
**What it does:** Editor-only. After import (`[InitializeOnLoad]`, run on the next editor tick) it creates the
default pool type provider assets for this package when they do not exist yet, in
`Assets/MidManStudio/Utilities/PoolProviders`. The object provider
(`ObjectPoolTypeProvider_Utilities.asset`) has package id `com.midmanstudio.utilities`, priority 0 and two
entries: `Trail` (offset 1) and `FlipbookEffect` (offset 2). The particle provider
(`ParticlePoolTypeProvider_Utilities.asset`) has no entries. The menu item
`MidManStudio > Utilities > Internal > Recreate Default Pool Providers` runs the same check by hand.

**Decisions:**
- It only creates an asset when the file is missing, so it never overwrites a provider that was edited.
- Each package creates its own provider. The audio package does it in `AudioPoolProviderBootstrapper`, the
  projectile system in its own bootstrapper. The generator merges all providers into one enum, ordered by
  priority.
- `Trail` and `FlipbookEffect` stay pinned at offsets 1 and 2, so their enum values did not change when
  `SpawnableAudio` left. Offset 0 is left free.

### `PoolTypeGenerator.cs`
**What it does:** Editor-only. Builds `PoolableObjectType.cs`, `PoolableParticleType.cs` and
`PoolableNetworkObjectType.cs` from every `ObjectPoolTypeProviderSO`, `ParticlePoolTypeProviderSO` and network
provider asset in the project. The file also holds the window `MidManStudio > Utilities > Pool Type Generator`
and an asset postprocessor that generates again when a provider or the settings asset changes, if Auto-Generate
is on in the settings (`PoolTypeGeneratorSettingsSO`).

**How it works:**
- `Generate` reads the providers for the three enums and passes each set to `EnumBlockLogic.Assign`. The numbering
  rules are in [enum-generators.md](enum-generators.md). When all three come out without errors, it writes the
  three enum files and then the lock file. With an error in any of them, it writes nothing.
- Network providers are read through `SerializedObject` by field name (`packageId`, `displayName`, `priority`,
  `entries`), so this editor assembly does not reference `com.midmanstudio.netcode`.
- The lock file is at `lockFilePath` in the settings, `Assets/MidManStudio/Generated/Pools/PoolTypeLock.json` by
  default. It is meant to be committed. A lock file that cannot be read stops the run with an error.
- The text of the enum files is the same as before. The header still carries a `Generated:` timestamp, so every
  run rewrites all three files even when no number changed.

**Decisions:**
- The numbering moved out of this file into `EnumBlockLogic.cs`, so the pool and effect generators cannot drift
  apart.
- The setting `verboseProviderLogging` exists but the generator logs the providers on every run. That is how it
  behaved before and it was left as it was.

**Tests:** the numbering is covered by `Tests/Editor/EnumBlockLogicTests.cs`, see [enum-generators.md](enum-generators.md).
The generator and the window have no tests.

### `PoolEntryDefinition.cs`
**What it does:** The serializable entry of a provider. Fields: `entryName` (becomes the enum member), `comment` (written next
to it) and `explicitOffset` (default -1). With -1 the generator chooses the number. With 0 or more the entry is pinned to
that offset inside the provider's block. `Name` is the label in inspector lists.

**Decisions:** A pin makes the number known in advance. Entries that other packages or saved data rely on (`Trail`,
`FlipbookEffect`) are pinned. An unpinned entry keeps its number through the lock file, see
[enum-generators.md](enum-generators.md).

### `IPoolEntryProviderSO.cs`
**What it does:** One property, `List<PoolEntryDefinition> Entries`. Both provider types implement it, so the JSON import
inspector is written once. The two provider types stay separate so the generator can find object and particle providers
independently.

### `ObjectPoolTypeProviderSO.cs`
**What it does:** The provider for `PoolableObjectType` (Create > MidManStudio > Utilities > Pool Type Provider (Object)).
Fields: `packageId` (default `com.mygame`, unique), `displayName`, `priority` (default 100) and `entries`. The tooltip says
0 is the utilities package and 10 the projectile system. The audio package uses 20 (see the generated file header).
Game code starts at 100. `EntryCount` gives the size.

### `ParticlePoolTypeProviderSO.cs`
**What it does:** The same for `PoolableParticleType` (Create > MidManStudio > Utilities > Pool Type Provider (Particle)).
The fields have no tooltips, the rules are the same as for the object provider.

### `PoolTypeGeneratorSettingsSO.cs`
**What it does:** The settings asset (Create > MidManStudio > Utilities > Pool Type Generator Settings). Fields and defaults:
`objectEnumOutputPath`, `particleEnumOutputPath` and `networkEnumOutputPath` (paths under `packages/...`),
`lockFilePath` (`Assets/MidManStudio/Generated/Pools/PoolTypeLock.json`), `minimumBlockSize` (100, at least 10),
`generatedNamespace` (`MidManStudio.Core.Pools`), `autoGenerateOnAssetChange` (false) and `verboseProviderLogging` (true,
not read by the generator, see above).

**Gotchas:** The three enum paths point into `packages/com.midmanstudio.*`, relative to the project root. They fit the layout
of the MidManStudio_Unity repository. In a project that installs the packages from git, the folders do not exist and the
generator would write to a new folder next to `Assets`. Change the paths in the settings asset to a folder under `Assets`
for such a project. The lock file path is under `Assets`, so it works in any layout. Found by reading the code, not run in
Unity.

### `BasicPoolConfig.cs`
**What it does:** Four small serializable classes in `MidManStudio.Core.Pools`:
- `BasicPoolConfig`: `objectType`, `displayName`, `prefab`, `prewarmCount` (default 5) and `maxPoolSize` (default 15). One
  row of `LocalObjectPool`.
- `ParticlePoolConfig`: the same with `particleType`, defaults 10 and 30, and `defaultLifetime` (default 5 seconds, at least
  0.1).
- `PoolStats` and `ParticlePoolStats`: `typeName`, `totalSpawned`, `activeCount`, `availableInPool` and `maxPoolSize`, for the
  inspector monitor. The two classes are identical.

**Gotchas:** `totalSpawned` counts instances made after the prewarm, not the prewarmed ones.

### `LocalObjectPool.cs`
**What it does:** A `Singleton<LocalObjectPool>` that pools prefab instances by `PoolableObjectType`. Namespace
`MidManStudio.Core.Pools`.

**How to use it:**
```csharp
LocalObjectPool.Instance.CallInitializePool();                       // once, after the scene is ready
var obj = LocalObjectPool.Instance.GetObject(PoolableObjectType.Trail, position, rotation);
LocalObjectPool.Instance.ReturnObject(obj, PoolableObjectType.Trail); // or let LocalPoolReturn do it
```
- Inspector: a list of `BasicPoolConfig` rows, auto-registration settings (`enableAutoRegistration` true, prewarm 10, max
  20) and read-only monitor fields.
- `CallInitializePool()` checks the list (a null prefab is skipped with a warning, a duplicate type or prefab is an error and
  leaves the pool uninitialised), registers each row and creates its prewarm instances. It then calls
  `LocalParticlePool.Instance.CallInitializePool()` when that pool is not initialised.
- `GetObject` (a Vector3 and a Vector2 overload) takes an instance from the type's queue. It skips queue entries that were
  destroyed from outside. When the queue is empty it creates a new instance. The instance gets no parent, is placed and
  activated.
- `ReturnObject` rejects a null or destroyed object and an object that is already queued. It destroys the object when the
  queue is already at `maxPoolSize`. Otherwise it resets the object (position, rotation, scale, the Rigidbody and
  Rigidbody2D velocities, trails), makes it a child of the pool, deactivates it and queues it.
- `AddType` registers a type at run time. `IsRegistered`, `GetQueuedCount`, `ReturnAllActive` and `ClearPool` do what their
  summaries say.
- Each instance gets a `LocalPoolReturn` (added when the prefab has none) with the pool type set.

**Decisions:**
- Auto-registration: a type that is not registered is registered on first use from the config list, with the auto-registration
  prewarm and size. This keeps code working when `CallInitializePool` was not called or a row was added late.
- A set of all queued objects (`_queuedObjects`) rejects a duplicate return. Without it one instance could be handed to
  two callers.

**Gotchas:**
- Pooled objects return themselves after 5 seconds by default, see `LocalPoolReturn.cs`.
- `ResetObject` sets the root scale to 1, so a prefab whose root scale is not 1 loses it. See the Fixes list.
- A new instance is created active, so its `Awake` and `OnEnable` run once before it is placed.
- When `CallInitializePool` was not called, the first use of a type registers it with the auto-registration numbers (10 and
  20), not with the numbers of its row.
- `ClearPool` leaves `HasBeenInitialized()` true, so a second `CallInitializePool` does nothing. After a clear, types are
  registered again on first use.
- Singleton rules apply: see [singleton.md](singleton.md). `Instance` creates an empty pool in Play Mode when none exists.

### `LocalPoolReturn.cs`
**What it does:** A MonoBehaviour that returns its GameObject to `LocalObjectPool`. The file also holds `ReturnConfig`
(`autoReturn` true, `duration` 5, `useAutoDestruct` true). `OnEnable` starts a timer coroutine when auto-return is on, and
`OnDisable` stops it. When the timer ends, or on `ReturnToPoolNow()`, it resets the AudioSource (stop, time 0), the
Animator (play state 0 at 0) and the TrailRenderers, then calls `ReturnObject` when the type is registered. When it is not
registered, or the pool is missing, it destroys the object if `useAutoDestruct` is on. API: `SetOriginalType`,
`GetOriginalType`, `SetAutoReturn`, `SetDuration`, `GetDuration`, `IsAutoReturnEnabled`, `IsScheduledForReturn`.

**Gotchas:**
- The default config returns every pooled object 5 seconds after it is enabled. Objects that live longer need
  `SetAutoReturn(false)` or a `LocalPoolReturn` on the prefab with another config.
- The timer uses `WaitForSeconds`, which follows `Time.timeScale`.
- The AudioSource and Animator reset only look at the root object, not at children.
- Because the type must be registered, the auto-registration branch in `ReturnObject` is not reached through this component.

### `LocalParticlePool.cs`
**What it does:** A `Singleton<LocalParticlePool>` that pools particle prefabs by `PoolableParticleType`. The structure
matches `LocalObjectPool`: a list of `ParticlePoolConfig` rows, auto-registration (prewarm 10, max 30), `CallInitializePool`,
`GetObject` (Vector3 and Vector2), `ReturnObject`, `AddType` (with a lifetime), `IsRegistered`, `ClearPool` and an inspector
monitor with a `configWarnings` list. Differences: on get it clears and plays every ParticleSystem on the instance, on
return it stops and clears them, and each instance gets a `LocalParticleReturn` with the row's `defaultLifetime`.

**Gotchas:**
- There is no `_queuedObjects` check and no skip of destroyed queue entries. A second `ReturnObject` call for the same
  instance queues it twice, and a destroyed instance in the queue is handed out and fails on use. See the Fixes list.
- The same scale reset and the same per-frame monitor as `LocalObjectPool`.

### `LocalParticleReturn.cs`
**What it does:** A MonoBehaviour that returns a particle object to `LocalParticlePool` after `maxLifetime` seconds (default
10, set from the config by the pool), or on `ReturnToPool()` and `ForceReturn()`. A flag ignores a second return until the
object is enabled again. When the type is not registered or the pool is missing, the object is destroyed. API:
`SetOriginalType`, `GetOriginalType`, `SetMaxLifetime`, `ReturnToPool`, `ForceReturn`.

### `TrailRendererPool.cs`
**What it does:** A `Singleton<TrailRendererPool>` with a fixed number of TrailRenderer objects (`_poolSize`, default 256,
created in `Awake` as hidden children). The file also holds the public struct `TrailConfig` and the internal class
`TrailSlot`.
- `Acquire(TrailConfig, ownerId = 0)` returns a slot index, or -1 when none is free. It applies the config, clears the
  trail and turns it on.
- `SetPosition(slot, position)` moves the slot's transform.
- `Release(slot)` stops emitting and keeps the slot busy for `trail.time + _fadePad` seconds (default pad 0.1) so the trail
  fades out. `Update` then disables the renderer. `ReleaseByOwner(ownerId)` releases all slots of one owner.
  `ForceRelease(slot)` disables a slot at once.
- When no slot is completely free, `Acquire` takes the fading slot that is closest to the end of its fade.
- `PoolSize` and `IsAcquired(slot)`.

**`TrailConfig`:** `Material`, `ColorGradient`, `Time`, `StartWidth`, `EndWidth`, `CapVertices` (0 to 4), `SortingLayerID`
and `SortingOrder`. `TrailConfig.Default` gives time 0.25, start width 0.1 and end width 0. A time of 0 or less is
replaced by 0.25, and a negative width by 0.1 (start) or 0 (end).

**Decisions:** The renderers use cheap settings: no shadows, no lighting data, no motion vectors, view alignment. A slot is
a plain index, so callers need no object reference. The projectile system's `TrailObjectPool` uses this pool.

**Gotchas:**
- Use `TrailConfig.Default` or set the fields. A `default(TrailConfig)` has a start width of 0, which is not replaced, so the
  trail is invisible.
- A null `Material` or `ColorGradient` in the config leaves what the previous user of that slot set. See the Fixes list.
- A slot index is not checked against its owner, see the Fixes list.
- `ReleaseByOwner(0)` releases every slot that was acquired with the default owner id.
- The sorting layer id 0 is whatever layer has id 0 in the project. Pass `SortingLayer.NameToID("Default")` to be explicit.

### `UIParticlePoolManager.cs`
**What it does:** A MonoBehaviour (not a singleton) that plays ParticleSystems already placed in the HUD, by string key. The
file also holds `UIEffectConfig` (`key`, `particleSystem`, `useEmitMode`). `Awake` builds the key lookup (a null system is an
error, a duplicate key keeps the first) and then stops all systems and deactivates their GameObjects. API:
- `TriggerEffect(key, emitCount = 10)` plays or emits depending on the config.
- `PlayEffect(key)` stops a system that is playing, activates the GameObject and calls `Play()`.
- `EmitEffect(key, count)` activates the GameObject and calls `Emit(count)`.
- `StopEffect(key, clear = true)` and `StopAll(clear = true)`. With `clear` the system is cleared and its GameObject is
  deactivated.
- `IsPlaying`, `GetSystem`, `RegisterEffect` and `UnregisterEffect`.

**Decisions:** It creates nothing and keeps no queue, so the name "pool" is historical. The string key replaces the closed enum,
so a HUD can add effects without a generator run.

**Gotchas:**
- An unknown key logs an error. That includes `IsPlaying`, so polling an unknown key logs every call.
- A null key throws, because the lookup is a Dictionary.
- `OnDestroy` calls `StopAll(true)`.

### `PoolTypeProviderEditor.cs` (Editor)
**What it does:** `PoolTypeProviderEditorBase` is the shared inspector of both provider types. It draws the default inspector and
a foldout "Import Entries from JSON" with a text area, a "Replace existing entries" toggle (off = append), an Import button
and a Clear button. Import parses with `PoolEntryJsonImporter`, records Undo, changes the provider's list, marks it dirty and
shows a message. The message reminds the user to run the Pool Type Generator again. `ObjectPoolTypeProviderEditor` and
`ParticlePoolTypeProviderEditor` are empty sealed subclasses with the `[CustomEditor]` attributes.

### `PoolEntryJsonImporter.cs` (Editor)
**What it does:** `PoolEntryJsonImporter.TryParse(json, out result, out error)` returns false with a message and never throws. It
needs a top-level array. Two shapes, one per paste: a list of names (`["BulletShell", "MuzzleFlash"]`), or a list of objects
(`[{"entryName":"BulletShell","explicitOffset":2,"comment":"9mm"}]`). In the object form `name` can replace `entryName`,
`offset` or `value` can replace `explicitOffset`, and `description` can replace `comment`. An empty array is valid and gives
no entries.

**Decisions:** `JsonUtility` cannot read a bare array, so the text is wrapped in an object (`{"items": ...}`). The shape is
chosen from the first character after the opening bracket. Missing numbers use `int.MinValue` as "not given", and the default
offset is -1.

**Gotchas:** A list that mixes names and objects is not supported and is expected to give an error message. A name is trimmed, an empty name is an error,
and the importer does not check for a valid C# name or for duplicates. The generator does that when it runs.

## Fixes and Problems

### `PackagePoolProviderBootstrapper.cs`
**Change:** The `SpawnableAudio` entry (offset 0, value 0) was removed from the object provider. It now belongs to
the audio package provider, in the block 200 to 299, so its value is 200.

**Problem:** A project that already has `ObjectPoolTypeProvider_Utilities.asset` keeps the old `SpawnableAudio`
entry, because the bootstrapper never overwrites the asset. The generator rejects duplicate entry names, so the
entry has to be deleted from that asset by hand before the Pool Type Generator will run with the audio package
installed. Entries in `LocalObjectPool` that were saved with `SpawnableAudio` also need their type picked again,
because the stored number was 0.

### `PoolTypeGenerator.cs`
**Problem:** Adding a pool type before the existing ones in a provider list renumbered all of them, and the
numbers stored in `LocalObjectPool` entries and other assets then pointed at other pool types. **Fix:** Entries
keep their number wherever they sit in the list, and removed numbers stay reserved. See `EnumBlockLogic.cs` in
[enum-generators.md](enum-generators.md).

**Change:** A lock file in the old format is read as before. The first run does not change any number the old
lock file holds, and it writes the block ranges and the retired numbers next to the entries. Other changes to
behavior: a name that is empty after trimming is ignored with a warning instead of stopping the run, a pinned
offset past the end of a block makes the block larger instead of stopping the run, and a lock file that cannot be
read is an error (the old generator started again from nothing with a warning). The three enums are now worked
out before any file is written. The old generator wrote the object enum even when the particle enum then failed.

**Problem (open):** The auto-generate hook does not notice a deleted provider asset, because the type of a
deleted path cannot be looked up. This comes from reading the code and was not tried in Unity. Generate by hand
after deleting a provider.

### `LocalObjectPool.cs`
- Fixed in the code earlier, notes moved out of the comments:
  - A destroyed object could sit in a queue, for example a NetworkObject parent destroyed by Netcode while a pooled child
    visual was still queued after an early or duplicate return. `GetObject` then handed out the destroyed reference and the
    next `.transform` access threw `MissingReferenceException`. It now skips and discards such entries, with a warning.
  - `ReturnObject` queued a null or destroyed object without a word. It now warns and returns.
  - The same GameObject could be queued twice, for example when an auto-return timer fired on an object a caller still
    held and returned later by hand. The two copies were then handed to two callers. The set `_queuedObjects` now rejects
    the second return with a warning.
- Not fixed: `ResetObject` sets the root `localScale` to `Vector3.one` on every return and on prewarm. A prefab with another
  root scale is used at scale 1 from the first use. Prefabs have to be authored with root scale 1 (put the size on a child),
  or the reset has to restore the prefab's scale.
- Not fixed: with a prewarm count above the maximum size, the extra instances are created active as children of the pool and
  are never queued, so they stay in the scene.
- Not fixed: the monitor in `Update` clears and refills a list and allocates one `PoolStats` object per type every frame, in
  builds too. It is only useful in the editor.
- Not fixed: `AddType` with a null prefab throws.
- Not fixed: `ClearPool` leaves `_initialized` true, see the gotcha above.

### `LocalParticlePool.cs`
- Not fixed: no protection against a second return of the same instance, a null instance or a destroyed queue entry. This is
  the protection `LocalObjectPool` got above. `LocalParticleReturn` ignores its own second return, but a manual call of
  `ReturnObject` is not covered.
- Not fixed: the same root scale reset (`ResetParticle`) and the same per-frame monitor as `LocalObjectPool`.

### `LocalPoolReturn.cs`
- Moved out of the file header: what the component does and that the pool adds it. They are in the section above and in the
  class summary. The code is unchanged.
- Not fixed: the auto-return default (5 seconds, on) applies to every pooled instance. A caller that holds an object longer
  must turn it off, and an early timer return is the cause of the duplicate-return case that `LocalObjectPool` now rejects.

### `LocalParticleReturn.cs`
- Moved out of the file header: the one-line description. The code is unchanged.

### `TrailRendererPool.cs`
- Moved out of the file header: the description and the usage lines. They are in the class summary.
- Removed from a comment: a reference to "the RustSim batch draw calls", which is not part of this package. The comment now
  only says the renderer sorts by sorting layer and order.
- Not fixed: a slot index has no generation or owner check. `SetPosition`, `Release` and `IsAcquired` only check that the slot is
  in use. When an owner releases a slot and a second owner acquires it, a late call from the first owner moves or releases
  the second owner's trail.
- Not fixed: a null `Material` or `ColorGradient` in the config leaves the previous user's values on the slot. Width, time and
  sorting are always set.
- Not fixed: `Awake` creates the slots even on a duplicate instance that the singleton then destroys.
- Not fixed: `Update` loops over all slots every frame, 256 by default. It is cheap, but it runs even when no trail is in use.

### `UIParticlePoolManager.cs`
- Moved out of the file header: the usage, setup and play mode notes. They are in the class summary and the section above.
- Not fixed: `UnregisterEffect` fails with a null entry in the config list, and `RegisterEffect` does not check the particle
  system and can add a second entry for a key that is already in the list.
- Open question (naming): the class plays existing systems and pools nothing. The name is kept, so existing scenes keep working.

### `IPoolEntryProviderSO.cs`, `ObjectPoolTypeProviderSO.cs`, `ParticlePoolTypeProviderSO.cs`, `PoolEntryDefinition.cs`, `PoolTypeGeneratorSettingsSO.cs`
- Moved out of the file headers: the setup steps, the explanation of why the interface exists, and the offset rules. They
  are in the sections above and in the class summaries.
- Fixed in the comments: the headers of the three provider files (a copy of the same block in each) still said that list
  order matters for unpinned entries and that critical entries must be pinned so they do not move when an entry is inserted.
  That was the rule before the lock file. Unpinned entries now keep their number wherever they sit in the list. The headers
  also named the old menu path "MidManStudio > Pool Type Provider". The real paths are in the sections above.
- Not fixed: `verboseProviderLogging` is not read, see the generator section above.
- Not fixed: the enum output paths assume the repository layout, see the gotcha in the settings section.
- Note: the generated enums in `Runtime/PoolSystems/Generated/` hold entries from other packages (`Projectile_*` and
  `SpawnableAudio` in `PoolableObjectType`, `Projectile_*` in `PoolableParticleType`), because the files are built from every
  provider in the repository. A project with only the utilities package still compiles with them.

### `PoolEntryJsonImporter.cs`, `PoolTypeProviderEditor.cs`
- Moved out of the file headers: the accepted JSON shapes and the purpose of the import panel. They are in the class summaries
  and the sections above. The code is unchanged.
- Not fixed: a negative `offset` other than -1 is passed on as it is, and the generator decides what it means.
