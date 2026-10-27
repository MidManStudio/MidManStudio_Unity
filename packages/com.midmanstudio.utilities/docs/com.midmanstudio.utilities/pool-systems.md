# com.midmanstudio.utilities: PoolSystems

Only the files touched so far are covered here. The rest of `Runtime/PoolSystems` and `Editor/PoolSystems` is
not yet documented.

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
