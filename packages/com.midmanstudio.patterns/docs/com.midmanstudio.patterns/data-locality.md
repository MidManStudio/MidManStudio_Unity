# com.midmanstudio.patterns: Data Locality

## Modules

### `PackedHandle.cs`
**What it does:** Stable reference to an item in a `PackedList<T>`. It stays valid while the item moves inside
the list and becomes invalid once the item is removed, even if the slot is reused. `default` is never valid.
`IsAssigned` is false for `default`.

**Decisions:**
- A handle holds a slot number and a generation. Every removal raises the slot's generation, so an old handle
  to a reused slot no longer matches.
- Handles are not tagged with the list that made them. A handle used on a different list can match an item
  there by accident, so keep each handle with its own list.

### `PackedList.cs`
**What it does:** Stores items back to back in one array so a loop over them walks memory in order. `Add`
returns a handle. `Remove(handle)` moves the last item into the hole, so the array never has holes and item
order is not stable. `IsValid`, `TryGet`, the indexer and `IndexOf` resolve handles. `Items` and `Count` expose
the array for tight loops. `Clear` invalidates every handle.

**How it works:**
- Four int arrays map between item index and slot: slot of each item, item of each slot (-1 when free), the
  generation of each slot and a stack of free slots. Resolving a handle is two array reads and a compare.
- The arrays double in size when full.

**Decisions:**
- `Items` is the live backing array, replaced when the list grows and shuffled when an item is removed. It is
  exposed for loops over `0` to `Count - 1` and must not be kept across `Add` or `Remove`.
- Removed items are overwritten with `default`, so the list does not keep objects alive.
- Not thread-safe.

### `DataLocalityTests.cs`
**What it does:** 14 edit-mode NUnit tests: handles and counts, insertion order, removing a middle item and the
last item, handle invalidation and slot reuse, the default handle, growth, the indexer, `Clear`, released
items, a 3000-step random test against a simple model and the capacity check.

**Decisions:**
- Four deliberate breakages were tried (a moved item keeping its old index, the generation not raised on
  removal, the generation not checked, `Clear` keeping handles valid). All were caught.
  The tests were run outside Unity against a small NUnit stand-in and have not been run in the Unity Test Runner.

## Fixes and Problems

None so far. No bug has been found in a released version.
