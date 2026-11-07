# com.midmanstudio.utilities: Libraries

A library is a named collection of ScriptableObject items that game code reads by key. The runtime part is in
`Runtime/Libraries/`, the generator is in `Editor/Libraries/`. Everything is in the namespace
`MidManStudio.Core.Libraries` (the provider assets are in `MidManStudio.Core.Libraries.Generator`, the generator in
`MidManStudio.Core.EditorUtils.Libraries`). Two files in `Runtime/Libraries/Generated/`, `LibraryId.cs` and
`LibraryItemId.cs`, are written by the generator and are not documented one by one. The copies in the repository are
placeholders that each hold a single member `_Reserved = 0`.

The pieces fit together like this: item assets (`MID_LibraryItemSO`) go into a library asset (`MID_LibrarySO`), library
assets go into the scene's `MID_LibraryRegistry`, and code asks the registry for an item by two strings or by two
generated enum values. `LibraryTypeProviderSO` assets list the names that the generator turns into the two enums.

## Modules

### `MID_LibraryItemSO.cs`
**What it does:** The abstract base class of every library item. It holds one serialized field, the item id.
`ItemId` returns that id, or the asset file name when the field is blank or white space. `Name` returns `ItemId` and
is the label in inspector lists (`IArrayElementTitle`).

**How to use it:** Derive a class and add your own fields.
```csharp
[CreateAssetMenu(menuName = "MyGame/Libraries/WeaponItem")]
public class WeaponItemSO : MID_LibraryItemSO { public float damage; public Sprite icon; }
```
`WeaponDefinitionSO` in the projectile system package is an example of such a subclass.

**Gotchas:** The fallback uses the asset's `name`, so renaming the file changes the key.

### `MID_BasicLibraryItemSO.cs`
**What it does:** A ready-made item for cases without custom data. Fields: `displayName`, `description` (text area),
`icon` (sprite) and `tags` (string array). Create it with Create > MidManStudio > Utilities > Library Item (Basic).

**Gotchas:** Nothing in the package reads `tags`. The tooltip says they are for filtering, but there is no filter API,
so they are data for your own code.

### `MID_LibrarySO.cs`
**What it does:** A named list of items (Create > MidManStudio > Utilities > Library). `LibraryId` is the id field, or
the asset name when blank. The lookup is a dictionary with a case-insensitive key (`StringComparer.OrdinalIgnoreCase`),
built on first use.

**Public API:**
- `GetItem<T>(string itemId)` returns the item cast to `T`, or null when it is missing or is another type.
- `HasItem(string)`, `ItemCount` and `AllItemIds`.
- `BuildLookup()` builds the dictionary. It does nothing once built. Unity's `OnEnable` clears the built flag after a
  domain reload, so the dictionary is rebuilt on next use.

**Decisions:**
- Items with the same id: the first one is kept and a warning is logged with `Debug.LogWarning`. Null entries in the
  list are skipped.
- Ids are not case sensitive, so `"Sword"` and `"sword"` are the same item.

**Gotchas:** The dictionary is built once. Changing the Items list while the game is running does not change what
`GetItem` returns until the asset is enabled again.

### `MID_LibraryRegistry.cs`
**What it does:** A `Singleton<MID_LibraryRegistry>` MonoBehaviour. Its Libraries list (`[MID_NamedList]`) takes every
`MID_LibrarySO` asset. `Awake` builds two tables from it: one by the library id string (case-insensitive) and one by the
numeric value of the matching `LibraryId` member.

**How to use it:**
```csharp
var sword = MID_LibraryRegistry.Instance.GetItem<WeaponItemSO>("Weapons", "Sword");             // strings
var sword2 = MID_LibraryRegistry.Instance.GetItem<WeaponItemSO>(LibraryId.Weapons, LibraryItemId.Weapons_Sword); // enums
bool has = MID_LibraryRegistry.Instance.ItemExists(LibraryId.Weapons, LibraryItemId.Weapons_Sword);
```
`LibraryExists` and `ItemExists` have a string version and an enum version. The inspector field `_logLevel` defaults
to `Info`.

**How the enum lookup works:** The enum item name has the form `LibraryName_ItemName`. The registry removes the
`LibraryName_` prefix and looks the rest up as an item id. So for the enum call to work, the library asset's id must equal
the `LibraryId` member name, and the item asset's id must equal the item name from the provider (the part after the
underscore). A library whose id matches no member is still found by string.

**Decisions:** Both lookups exist so that existing string callers keep working while new code can use the typed enums.

**Gotchas:**
- Put one registry in a scene. `Instance` creates an empty registry in Play Mode when none exists, and every lookup
  then logs a warning and returns null. See [singleton.md](singleton.md) for the lifetime rules.
- Enum parsing is case sensitive, string lookup is not.
- `GetItem` logs "Item not found" when the item exists but has another type than `T`, because the library returns null
  for a failed cast.
- A duplicate library id is skipped with a warning, so the second asset is not reachable.

### `LibraryTypeProviderSO.cs`
**What it does:** The ScriptableObject that lists names for the generator (Create > MidManStudio > Utilities > Library
Type Provider). Fields: `packageId` (default `com.mygame`, unique), `displayName`, `priority` (default 100, lower comes
first, 0 is reserved for the utilities package) and `libraries`. The file also holds the serializable class
`LibraryEntryDefinition` with `libraryName`, `itemNames` (list of strings) and `comment`. `LibraryCount` and
`TotalItemCount()` give the sizes that the generator window shows.

**Decisions:** One provider per package or game project, as with the pool and effect types. Item names are written
without the library prefix. The generator adds the prefix.

### `LibraryTypeGenerator.cs` (Editor)
**What it does:** `LibraryTypeGeneratorCore.Generate()` reads every `LibraryTypeProviderSO` asset, checks it and writes
the two enum files. The window `LibraryTypeGeneratorWindow` (MidManStudio > Utilities > Library Type Generator) lists the
providers found and has a Generate Now button. It shows warnings and errors in the window. `Generate()` returns a
tuple of success, errors and warnings.

**What it checks:** Duplicate package ids, an empty library name, a name that is not a C# identifier (library names and
item names), duplicate library names and duplicate item keys (`Library_Item`) over all providers. An empty item name is
a warning and is skipped. When there is any error, nothing is written.

**What it writes:**
- Providers are sorted by priority, then package id. `LibraryId` members are numbered from 0 in that order, and
  `LibraryItemId` members are numbered from 0 in one run over all providers.
- Each file starts with a header and a `Generated:` timestamp, so every run rewrites both files.
- The comment of a library entry is written next to its `LibraryId` member.

**Gotchas:**
- The two output paths are constants in the code (`packages/com.midmanstudio.utilities/Runtime/Libraries/Generated/...`),
  relative to the project root. There is no settings asset. In a project that installs the package from git, these
  folders do not exist, so the files are written to a folder called `packages` next to `Assets`, where Unity does not
  compile them. The generator works as intended only in the layout of the MidManStudio_Unity repository. Found by
  reading the code, not run in Unity.
- There is no lock file. See the first entry under Fixes and Problems.

## Fixes and Problems

### `MID_LibraryItemSO.cs`, `MID_BasicLibraryItemSO.cs`, `MID_LibrarySO.cs`
- Moved out of the file headers: the quick start, the custom subclass example, the create menu paths and the retrieval
  example. They are in the sections above and in the XML summaries. The code is unchanged.
- Not fixed: nothing reads `MID_BasicLibraryItemSO.tags`.
- Not fixed: `MID_LibrarySO` builds its dictionary once, so run-time changes to the list are not seen. The method that
  builds it does nothing after the first call.

### `MID_LibraryRegistry.cs`
- Fixed in the comments: a comment said the enum parser works even when the `LibraryId` enum did not exist at compile
  time and that the enum may not exist before the first generation. Neither is true. The code names `LibraryId` directly,
  so the enum must exist to compile, and the package ships a placeholder for that. The comments now say what the code
  does.
- Not fixed: a failed type cast and a missing item give the same "not found" warning.

### `LibraryTypeProviderSO.cs`
- Moved out of the file header: the pattern note and the priority ranges (0 reserved, 100 for game code). They are in the
  section above and in the class summary.

### `LibraryTypeGenerator.cs`
- Not fixed: there is no stable numbering. The effect, pool and audio generators keep a lock file so that an entry keeps
  its number when others are added (see [enum-generators.md](enum-generators.md)). This generator numbers entries from 0
  in list order on every run, so adding or removing a library or item before the end renumbers the ones after it. A
  number that was saved in a scene, prefab or ScriptableObject then points at another entry. The registry itself maps by
  name, so it is not affected, but any serialized `LibraryId` or `LibraryItemId` field is.
- Not fixed: the output paths are fixed in the code and assume the repository layout, see the gotcha above.
- Not fixed: the identifier check allows C# keywords. A library named `class` passes the check and gives a file that does
  not compile. Item names are safe because the generator prefixes them with the library name.
- Not fixed: the generator writes no `_Reserved` member, while the placeholder files in the repository have one. After
  the first run with no providers the enums are empty.
