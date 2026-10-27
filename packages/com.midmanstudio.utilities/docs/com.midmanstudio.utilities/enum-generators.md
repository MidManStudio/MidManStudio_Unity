# com.midmanstudio.utilities: Enum generators

The Effect Type Generator and the Pool Type Generator both turn provider assets into generated enum files and keep
the numbers stable through a lock file. The number handling lives in one shared file, `EnumBlockLogic.cs`, which
both generators call. The generator files themselves are documented with their systems:
`EffectTypeGenerator.cs` in [fx.md](fx.md) and `PoolTypeGenerator.cs` in [pool-systems.md](pool-systems.md).

## Modules

### `EnumBlockLogic.cs`
**What it does:** Editor-only, with no Unity API calls. Given the providers found in the project and the lock
file state from the last run, `EnumBlockLogic.Assign` places each provider's block of numbers, gives every entry a
number, and returns the state to save back into the lock file. It also holds the name checks
(`IsValidIdentifier`, `IsValidNamespace`) and `OneLine`, which keeps a comment on one line.

**How the numbers are chosen:**
- Providers are sorted by priority, then by package id. Each provider owns a block, a start number and a size. The
  size is the minimum block size, rounded up so every entry and every pinned offset fits.
- Priority only decides where a provider goes the first time. After that the lock file decides. A block keeps its
  start number even when providers with a lower priority number are added later. A new provider goes after the
  end of the last block that exists, including blocks of providers that are not in the project right now.
- When a provider needs more room than its block has, the block grows in place if the numbers after it are free.
  If they are not, the provider moves to a new block at the end, its values change, and a warning says so. Its
  neighbours do not move.
- Pinned entries (offset 0 or more) get the block start plus the offset. A warning is given when a pinned entry
  lands on a different number than the lock file has.
- Entries without a pin keep the number they already have, wherever they sit in the list. Inserting an entry at
  the top or reordering the list does not move anything.
- A new entry takes the lowest free number in the block that no removed entry used. If the block has no such
  number left, it reuses one that a removed entry used and a warning says so.
- When an entry is removed, its number goes into the retired list. The number stays reserved. If an entry with the
  same name comes back, it gets the same number again.
- A provider that is missing (package removed, asset deleted) keeps its block and its numbers in the lock file.
  The entries do not appear in the generated enum while it is missing.

**Lock file:** Each lock file holds three lists per enum. The effect lock file has `categoryEntries`,
`categoryBlocks`, `categoryRetired`, `typeEntries`, `typeBlocks` and `typeRetired`. The pool lock file has the
same three lists for `object`, `particle` and `network`. An entry is `packageId`, `name`, `value`. A block is
`packageId`, `start`, `size`. Lock files written before this change only have the `*Entries` lists. When a lock
file has entries but no blocks, the blocks are worked out from where the entries sit: the start is the lowest
value of the provider rounded down to the minimum block size, and the size covers the highest value. The first run
after an update therefore does not move anything, and the run writes the real blocks. An older version of the
package that reads a new lock file ignores the extra lists.

**Decisions:**
- One shared file instead of a copy per generator. The two copies had to be changed together and drift apart.
- A name that is empty after trimming is ignored with a warning. The pool generator used to stop with an error on
  this, and a blank row in an inspector list is a common leftover.
- A name that is not a valid C# member name is an error in both generators. The effect generator did not check
  this before and wrote a file that did not compile.
- A pinned offset past the end of the block makes the block larger. It used to be an error.
- Letters outside ASCII are accepted in names, as the pool generator accepted them before.
- Both generators work out every enum before they write a file. An error in any enum means no enum file and no
  lock file is written.
- A lock file that cannot be read stops the run. Starting from nothing would renumber every entry, so the
  decision is left to the owner of the project. Deleting the lock file lets all numbers be assigned again.
- The audio package keeps its own copy of this logic (`AudioTypeLogic` in `com.midmanstudio.audio`). It was built
  and tested on its own. The audio package depends on utilities, so it can switch to this file later.

**Tests:** `Tests/Editor/EnumBlockLogicTests.cs`.

### `EnumBlockLogicTests.cs`
**What it does:** 25 NUnit Edit Mode tests for `EnumBlockLogic`. They cover the first run, pinned entries, a
pinned offset past the block, inserting and reordering entries, removing an entry and adding it back, a block that
grows in place and a block that has to move, a provider that goes missing and returns, lock files in the old
format, lock files with broken blocks, bad input, and 40 random sequences of edits that check no entry ever
changes number and no number is given to two different names.

**Decisions:**
- The test assembly is `MidManStudio.Utilities.Tests.Editor`. `EnumBlockLogic.cs` names it in an
  `InternalsVisibleTo` attribute, because the logic classes are internal. The test assembly definition references
  `MidManStudio.Utilities.Editor` for the same reason.
- The tests call `Assign` directly and need no project assets, so they run in any project.

## CI and Workflows

No workflow runs these tests. They run from the Unity Test Runner in Edit Mode.

## Fixes and Problems

### `EnumBlockLogic.cs`
**Problem:** The old generators laid out the numbers again on every run. An entry inserted before others pushed
them all up by one. Running the old effect generator with the entries A, B, C (0, 1, 2) and then adding New at
the top gave New 0, A 1, B 2, C 3, with three warnings. The pool generator had the same two passes. A number
that is stored in a scene, a prefab or a ScriptableObject then points at a different entry.

**Fix:** The block placement and value assignment now follow the rules above. The same run gives A 0, B 1, C 2 and
New 3, with no warnings.

**Checked outside Unity:** The generator files were compiled against hand-written Unity stand-ins and run on real
files. 25 tests pass. Seven deliberate breakages of the logic (forgetting remembered numbers, forgetting retired
numbers, skipping the old lock file step, not saving retired numbers, dropping missing providers, never growing a
block in place, placing new providers from zero) each made at least one test fail. A fresh run of the old and the
new generators on the same providers gave enum files that match line for line, apart from the timestamp, for all
five enums. The real `PackageSandbox` pool lock file (13 entries, old format, with a byte order mark) gave no
changed number and no warning, a second run changed nothing, and an entry added at the top of the projectile
provider took the next free number.

**Not checked in Unity:** `JsonUtility` reading and writing the new lists, the generator windows, the
auto-generate hook, and the Test Runner picking up the test assembly. The stand-in for `JsonUtility` is not the
real one.

**Problem:** Three other generators copied the old algorithm and still renumber on insert:
`ProjectileConfigGenerator.cs` in `com.midmanstudio.projectilesystem`, `HudItemTypeGenerator.cs` in
`com.midmanstudio.hudman` and `SettingsEnumGenerator.cs` in `com.midmanstudio.gensettings`. They were not changed.
