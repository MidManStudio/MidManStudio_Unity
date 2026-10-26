# com.midmanstudio.audio: Enum Ids and Categories

Audio ids and audio categories are generated C# enums, the same idea as the FX and pool type enums in
`com.midmanstudio.utilities`. Game code plays a sound with `PlaySFX(AudioId.SwordHit)` instead of a string. Two
kinds of ScriptableObject feed the generator: providers (names for the enum members) and catalogs (which clip and
which mixer group belong to each value). The manager reads catalogs at runtime and never touches the enum names,
only their int values, so it works no matter which assembly the generated enums end up in.

## How the pieces fit

1. An `AudioIdProviderSO` lists audio id names. An `AudioCategoryProviderSO` lists category names. The audio
   package ships one of each (created on first import by `AudioEnumProviderBootstrapper.cs`). A game adds its own
   with a different `packageId`.
2. The Audio Type Generator (`MidManStudio > Audio > Audio Type Generator`) reads every provider and writes
   `AudioCategory.cs`, `AudioId.cs` and a lock file, by default under `Assets/MidManStudio/Generated/Audio/`.
3. A `MID_AudioCatalogSO` rows pair each id with a clip, a category and a volume, and each category with a mixer
   group and a volume. Catalog fields are plain `int`, drawn as enum popups by `MID_AudioIdDrawer.cs`, so a
   catalog asset stays valid when the enums are regenerated.
4. `MID_AudioManager` merges its catalogs and plays by enum value. See `MID_AudioManager.cs` in
   `docs/com.midmanstudio.audio.md`.

The generated files compile into the assembly of the folder they are written to. Under `Assets` with no
assembly definition that is `Assembly-CSharp`, which code inside an assembly definition cannot reference. A
project whose game code sits in its own assembly definition should point the output paths at a folder inside
that assembly.

## Quick start

```csharp
// 1. MidManStudio > Audio > Audio Id Provider: packageId "com.mygame", priority 100, entries SwordHit, TavernTheme.
//    MidManStudio > Audio > Audio Type Generator > Generate Now.
// 2. MidManStudio > Audio > Audio Catalog: add rows (id, category, clip, volume), assign it to the manager's
//    Catalogs list.
// 3. Play.
MID_AudioManager.Instance.PlaySFX(AudioId.SwordHit);
MID_AudioManager.Instance.PlayMusic(AudioId.TavernTheme);
MID_AudioManager.Instance.PlayRandomSFX(AudioCategory.Sfx);
MID_AudioManager.Instance.SetCategoryVolume(AudioCategory.Music, 0.5f);
MID_AudioManager.Instance.SetCategoryMuted(AudioCategory.Voice, true);
```

## How values are assigned

- Providers are placed in blocks of values. A category block is at least `categoryBlockSize` wide (default 100)
  and an id block at least `idBlockSize` (default 1000). The audio package's own providers have priority 0, so
  they get categories 0 to 99 and ids 0 to 999. A game provider with priority 100 is placed after them.
- An entry with `explicitOffset` of 0 or more is pinned to that offset inside its block. Entries with -1 are
  numbered by the generator.
- The lock file remembers every number. An entry that already has a number keeps it, wherever it sits in the
  provider's list. A new entry takes the lowest free number in the block that no removed entry ever used.
- A removed entry's number is retired and is not given to another entry. If the same name is added back, it gets
  the old number again.
- A block keeps its place. A provider that appears for the first time is placed after all existing blocks, so an
  existing block never shifts. A block that outgrows its size grows in place when the numbers after it are free.
  Otherwise it is moved to the end with a warning, because its values change.
- A provider that is missing (a package removed, an asset deleted) keeps its numbers in the lock, so they come
  back unchanged if it returns.

## Modules

### `MID_AudioEnumAttributes.cs`
**What it does:** The attributes that connect generated enums to the rest of the package. The generator writes
`[MID_AudioEnum(kind)]` on both enums. `[MID_AudioId]` and `[MID_AudioCategory]` go on `int` fields and make the
inspector draw them as enum popups.

**Public API:**
- `enum MID_AudioEnumKind { Id = 0, Category = 1 }`
- `MID_AudioEnumAttribute(MID_AudioEnumKind kind)` for enums, with the property `Kind`.
- `MID_AudioIdAttribute` and `MID_AudioCategoryAttribute`, both `PropertyAttribute` types for `int` fields.

**Decisions:**
- The enums are found by this attribute and not by the names `AudioId` and `AudioCategory`, so the generator
  namespace and enum names can change without breaking the drawers.
- The fields stay `int`. A catalog asset therefore serializes plain numbers and does not depend on the enum type
  existing when the asset loads.

### `MID_EnumKey.cs`
**What it does:** Internal helper `MID_EnumKey<T>` that converts an enum value to its int and tells the manager
whether the enum is a generated audio enum of the wrong kind. The manager uses it in every generic overload.

**Public API:** none. The class is `internal`. Its members are `ToInt(T value)` and
`ShouldWarnWrongKind(MID_AudioEnumKind expected)`.

**Decisions:**
- `T` is constrained to `unmanaged, System.Enum`. For an enum backed by a 4-byte type, `ToInt` calls
  `UnsafeUtility.EnumToInt`, which does not box or allocate. Enums backed by `byte`, `short` or `long` use
  `Convert.ToInt32`, which can box.
- The wrong-kind check reads the attribute once per enum type and warns at most once per type. An enum without
  the attribute (a game's own enum) is accepted without a warning.

### `MID_AudioEnumLookup.cs`
**What it does:** Editor-only lookup of the generated enums. The whole file is inside `#if UNITY_EDITOR`, so it
does not exist in player builds. The drawers and catalog validation use it to list names and values.

**Public API:**
- `static Type GetEnumType(MID_AudioEnumKind kind)`
- `static string[] GetNames(MID_AudioEnumKind kind)` and `static int[] GetValues(MID_AudioEnumKind kind)`, sorted
  by value, both empty when the enum has not been generated.
- `static bool TryGetName(MID_AudioEnumKind kind, int value, out string name)`

**Decisions:**
- It finds enums through `TypeCache.GetTypesWithAttribute<MID_AudioEnumAttribute>()`. The result is cached until
  the next domain reload, which is when a regenerated enum appears.
- If two enums claim the same kind, it uses the one with the lower full name and logs a warning.

### `MID_AudioCatalogSO.cs`
**What it does:** The ScriptableObject that gives ids and categories their meaning. A catalog holds category
settings (mixer group and volume per category) and clip entries (id, category, clip, volume). A package can ship
a catalog and a game can add another; the manager merges them.

**Public API:**
- `MID_AudioCategorySettings`: `int category` (`[MID_AudioCategory]`), `AudioMixerGroup mixerGroup`,
  `float volume = 1f`.
- `MID_AudioClipEntry`: `int id` (`[MID_AudioId]`), `int category`, `AudioClip clip`, `float volume = 1f`.
- `MID_AudioCatalogSO`: `IReadOnlyList<MID_AudioCategorySettings> Categories`,
  `IReadOnlyList<MID_AudioClipEntry> Clips`, `int Validate(List<string> problems)`. Create it from
  `MidManStudio/Audio/Audio Catalog`.
- The context menu entry `Validate Catalog` logs every problem as a warning.

**Decisions:**
- `Validate` reports repeated ids, rows without a clip, id 0, repeated category rows, and (in the editor) values
  that are not members of the generated enums. It does not stop anything from being saved.
- A clip with category `None` (0) plays through the manager's default SFX or music group with the category scale
  of a `None` settings row, if the catalog has one.

### `AudioEntryDefinition.cs`
**What it does:** One row of a provider: `string entryName`, `string comment` and `int explicitOffset`. Each row
becomes one member of the generated enum.

**Public API:** the three fields above and `Name` (the entry name, or `Unnamed Entry`).

**Decisions:**
- `explicitOffset` is -1 for auto-numbered entries. A value of 0 or more pins the entry to that offset inside the
  provider's block. Pin an entry when saved data or other assets refer to its number.

### `AudioCategoryProviderSO.cs`
**What it does:** Provider asset for the `AudioCategory` enum. Create it from
`MidManStudio/Audio/Audio Category Provider`.

**Public API:** `string packageId`, `string displayName`, `int priority = 100`,
`List<AudioEntryDefinition> entries`, `int EntryCount`.

**Decisions:**
- `packageId` has to be unique across all providers of the same kind. Priority 0 is reserved for the audio
  package. Priority only decides the order in which providers that are new to the lock file are placed, and it is
  printed in the block comment of the generated file. A provider that already has a block keeps it.

### `AudioIdProviderSO.cs`
**What it does:** Provider asset for the `AudioId` enum. Same fields and rules as `AudioCategoryProviderSO`.
Create it from `MidManStudio/Audio/Audio Id Provider`.

**Public API:** `string packageId`, `string displayName`, `int priority = 100`,
`List<AudioEntryDefinition> entries`, `int EntryCount`.

### `AudioTypeGeneratorSettingsSO.cs`
**What it does:** Project-wide generator settings. The bootstrapper creates one at
`Assets/MidManStudio/Generated/Audio/AudioTypeGeneratorSettings.asset`.

**Public API (fields and defaults):**
- `categoryEnumOutputPath` = `Assets/MidManStudio/Generated/Audio/AudioCategory.cs`
- `idEnumOutputPath` = `Assets/MidManStudio/Generated/Audio/AudioId.cs`
- `lockFilePath` = `Assets/MidManStudio/Generated/Audio/AudioTypeLock.json`
- `categoryBlockSize` = 100, `idBlockSize` = 1000
- `generatedNamespace` = `MidManStudio.Core.Audio`
- `autoGenerateOnAssetChange` = false

**Decisions:**
- The lock file belongs in source control. It is what keeps numbers stable between machines.
- Paths have to be under `Assets`. The generator reports an error for a path it cannot write.
- Auto-generate is off by default, as it is in the FX and pool generator settings.

### `AudioTypeGeneratorLogic.cs`
**What it does:** The part of the generator that does not use the Unity editor API: name checks, block placement,
number assignment and the text of the enum files. It is `#if UNITY_EDITOR` and mostly `internal`, and the test
assembly sees it through `InternalsVisibleTo`.

**Public API:** `AudioGenerationResult` (`Success`, `CategoryBlocks`, `IdBlocks`, `FilesChanged`, `Errors`,
`Warnings`, `HasErrors`). The rest is internal: `AudioTypeLogic.Assign(providers, minBlock, old, result, kind, out
updated)`, `AudioTypeLogic.BuildEnumSource(blocks, ns, enumName, summary, kind)`, `IsValidIdentifier`,
`IsValidNamespace`, and the lock classes `AudioLockFile`, `AudioLockSection`, `AudioLockBlock`, `AudioLockEntry`.

**How it works:**
- Entry names have to be valid C# identifiers. Keywords and `value__` are refused. A name used twice anywhere in
  one enum is an error. An empty name is ignored with a warning.
- `Assign` returns null when there are errors, and nothing is written. The lock section it was given is never
  changed. The new state comes back in `updated`.
- Entries are numbered in three passes: pinned entries, then entries the lock already knows, then new entries.
- The lock has `blocks` (start and size per package), `entries` (live names and values) and `retired` (numbers of
  removed names). Overlapping or invalid lock blocks are not trusted. They are reported and placed again.
- The generated text has no timestamp, always uses `\n`, and a comment is forced onto one line so it cannot turn
  into code. The same input gives the same bytes.

**Decisions:**
- Entries keep their number when other entries are inserted above them, and blocks keep their place when a
  provider is added. The FX and pool generators in `com.midmanstudio.utilities` do neither (see Fixes and
  Problems).
- A removed number is never reused by a different name. If a block is full of retired numbers, a new entry takes
  one anyway and a warning says so.

**Tests:** `Tests/Editor/AudioTypeLogicTests.cs`.

### `AudioTypeGenerator.cs`
**What it does:** The editor side of the generator. It contains `AudioTypeGeneratorCore`, the window and the
asset postprocessor.

**Public API:**
- `AudioTypeGeneratorCore.FindSettings()`, `OutputsExist(settings)` and `Generate(settings)`, which returns an
  `AudioGenerationResult`.
- `AudioTypeGeneratorWindow`, opened from `MidManStudio/Audio/Audio Type Generator`. It shows the settings, the
  providers it found, buttons to create providers, `Generate Now`, and the last result with warnings and errors.

**How it works:**
- `Generate` validates the settings, reads the lock, assigns categories and ids, builds both files and writes
  them only if their text changed. The lock is written last, after both enum files succeeded. Files are UTF-8
  without a byte order mark.
- A lock file that cannot be read is an error, and the generator stops. Starting over would renumber every id.
  Deleting the lock file is the way to opt in to that.
- `AudioTypeAssetPostprocessor` regenerates after a provider or settings asset is imported or moved, when
  `autoGenerateOnAssetChange` is on. A pending flag stops it from queueing a second run.

**Decisions:**
- Files are written only when their content changed, so a regenerate with no edits causes no recompile.

### `AudioEnumProviderBootstrapper.cs`
**What it does:** `[InitializeOnLoad]` class that creates the audio package's default providers and the settings
asset when they are missing, then runs the generator once per editor session if the enum files do not exist.

**Public API:** `Bootstrap()`, `Recreate()` (menu `MidManStudio/Audio/Internal/Recreate Default Audio
Providers`) and `CreateDefaultSettings()`.

**How it works:**
- The default category provider has `packageId` `com.midmanstudio.audio`, priority 0 and the entries `None` 0,
  `Music` 1, `Sfx` 2, `Ui` 3, `Ambience` 4 and `Voice` 5, all pinned. The default id provider has the single entry
  `None` 0, pinned. Both assets are created in `Assets/MidManStudio/Audio/EnumProviders/`.
- A provider is looked up by `packageId`, so one the user moved or renamed is not created a second time.
- The first generation is guarded by a `SessionState` flag, so a failure is logged once and not on every domain
  reload.

**Decisions:**
- `None` is 0 in both enums. An unassigned `int` field in the inspector is 0, so it means no sound and no
  category.

### `MID_AudioIdDrawer.cs`
**What it does:** Editor property drawers for `[MID_AudioId]` and `[MID_AudioCategory]` fields.

**Public API:** `MID_AudioEnumPopupDrawer` (abstract base), `MID_AudioIdDrawer`, `MID_AudioCategoryDrawer`.

**How it works:**
- The field is drawn with `EditorGUI.IntPopup` over the names and values from `MID_AudioEnumLookup`.
- A value that is not in the enum shows as `Missing (n)` and stays selectable, so no data is lost when an enum
  member is removed.
- When the enum has not been generated, the field is drawn as a plain int and its tooltip names the menu entry
  that generates it.

### `AudioTypeLogicTests.cs`
**What it does:** NUnit tests for `AudioTypeLogic`, in the `MidManStudio.Audio.Editor.Tests` assembly
(`Tests/Editor/MidManStudio.Audio.Editor.Tests.asmdef`, Editor only, behind `UNITY_INCLUDE_TESTS`).

**Public API:** none. The tests cover identifier rules, first-run block placement, pinned entries, a pinned offset
past the block, inserting an entry above existing ones, regenerating from the same lock, retired numbers,
returning entries, a new provider with a lower priority, a missing provider that returns, block growth in place,
block relocation with a warning, bad input, empty names, overlapping lock blocks and the generated text.

## Fixes and Problems

### `AudioTypeGeneratorLogic.cs`
**Problem (in the FX and pool generators):** the numbering in `Editor/FXSystems/EffectTypeGenerator.cs` has two
weaknesses that the audio generator was written to avoid. The pool generator in
`Editor/PoolSystems/PoolTypeGenerator.cs` has the same two passes, and its source comment calls the algorithm
identical.
- Auto-numbered entries are processed in list order. A new entry placed before existing ones takes the lowest
  free number, which can be a number the lock still holds for an entry further down. That entry then moves to
  the next free number. The FX numbering code was run as shipped with `A`, `B`, `C` locked at 0, 1, 2 and a new
  entry placed first: the result was 0 for the new entry and 1, 2, 3 for the old ones. A warning is printed,
  but anything saved with the old numbers now points at a different member. A new entry added at the end of the
  list does not cause this.
- Blocks are laid out from the start on every run, in priority order, and block positions are not stored in the
  lock. Adding a provider with a lower priority, or growing a provider past its block size, moves every block
  after it, and the entries in those blocks all get new numbers. This was read from the code and not run.
The generated FX and pool files also carry a `Generated:` timestamp, so every run rewrites them. This package does
not change the FX and pool generators.

**Decision:** a number is kept for as long as the lock file knows the name, including after the entry was removed,
so that the same name added back gets its old number. The cost is that blocks fill up with retired numbers over
time. The block grows when that happens.

### `AudioTypeGenerator.cs`
**Problem:** `AudioTypeAssetPostprocessor` decides whether a changed path is a provider or settings asset with
`AssetDatabase.GetMainAssetTypeAtPath`. A deleted path has no asset left to inspect, so deleting a provider does
not trigger auto-generation. Run `Generate Now` after deleting one. The deleted provider's numbers stay in the lock
file. This was reasoned from the code and has not been tried in the editor.

**Decision:** the generator reports a lock file it cannot parse as an error instead of starting over. Starting
over would silently renumber every audio id.
