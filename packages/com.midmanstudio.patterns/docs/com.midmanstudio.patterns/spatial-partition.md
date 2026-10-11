# com.midmanstudio.patterns: Spatial Partition

## Modules

### `GridHandle.cs`
**What it does:** Reference to an item in a `SpatialGrid2D<T>`. Invalid after the item is removed. `default` is
never valid.

### `SpatialGrid2D.cs`
**What it does:** Uniform 2D grid for finding items near a point without testing every item. Items live in square
cells of `cellSize`. `Add(item, x, y)` returns a handle. `Move` and `Remove` take the handle. `QueryCircle` and
`QueryRect` add the matching items to a list the caller supplies and return how many they added.

**How it works:**
- A cell is found by flooring the position divided by the cell size. Two cell coordinates are packed into one
  `long` key for a dictionary of cells. Each cell holds a list of item indexes, and each item remembers its
  index in that list, so removing from a cell is a swap with the last entry.
- A query visits the cells that overlap its bounds and checks the exact distance, or the rectangle, for the
  items in them. A circle includes items exactly on its edge. A rectangle includes its edges.
- Emptied cell lists go back to a spare stack and are reused. Queries use plain loops and do not allocate.

**Decisions:**
- Choose a cell size close to the typical query radius. A cell far smaller than the radius makes a query visit
  many cells. A cell far larger puts many items in each cell.
- Results are appended to the caller's list and the list is not cleared, so several queries can fill one list.
  Result order is by cell, then by insertion order inside the cell, and is not otherwise defined.
- Positions must be finite and within int range after dividing by the cell size, otherwise the call throws
  `ArgumentOutOfRangeException` and nothing changes. A `Move` that fails leaves the item where it was.
- The Rust projectile core has its own spatial grid for projectile hits. This one is separate and works on any
  managed item type.
- Not thread-safe.

### `SpatialPartitionTests.cs`
**What it does:** 17 edit-mode NUnit tests: circle and rectangle queries, edges, cell borders, negative
coordinates, moving and removing, handle reuse, appending to the result list, duplicates, cell release,
`Clear`, argument checks, a failed move and a 1500-step random test that compares every circle query with a
brute-force search.

**Decisions:**
- Six deliberate breakages were tried. Five were caught (a stale cell index after a swap-remove, the circle edge
  excluded, `Move` never changing cell, the generation not raised on `Remove`, the rectangle edge excluded). The
  sixth, truncating instead of flooring the cell coordinate, was not detected. It does not change any result; it
  only makes the cells around zero twice as wide.
  The tests were run outside Unity against a small NUnit stand-in and have not been run in the Unity Test Runner.

## Fixes and Problems

None so far. No bug has been found in a released version.
