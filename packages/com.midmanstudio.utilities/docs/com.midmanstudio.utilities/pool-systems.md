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

## Fixes and Problems

### `PackagePoolProviderBootstrapper.cs`
**Change:** The `SpawnableAudio` entry (offset 0, value 0) was removed from the object provider. It now belongs to
the audio package provider, in the block 200 to 299, so its value is 200.

**Problem:** A project that already has `ObjectPoolTypeProvider_Utilities.asset` keeps the old `SpawnableAudio`
entry, because the bootstrapper never overwrites the asset. The generator rejects duplicate entry names, so the
entry has to be deleted from that asset by hand before the Pool Type Generator will run with the audio package
installed. Entries in `LocalObjectPool` that were saved with `SpawnableAudio` also need their type picked again,
because the stored number was 0.
