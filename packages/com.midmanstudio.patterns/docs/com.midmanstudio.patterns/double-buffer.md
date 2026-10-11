# com.midmanstudio.patterns: Double Buffer

## Modules

### `DoubleBuffer.cs`
**What it does:** Holds two instances of the same type. Readers use `Front` while the writer fills `Back`, and
`Swap()` exchanges them. `Swap(resetBack)` also calls a reset function on the new back buffer (the old front)
so the next step starts clean. `Create(factory)` builds both buffers from one function. `SwapCount` counts swaps.

**How it works:**
- Writes never touch what is being read, so every reader sees one consistent snapshot per step, whatever
  order the updates run in. Without the second buffer, an update that reads a neighbour's value could see a
  value that was already changed in the same step.

**Decisions:**
- The constructor rejects the same instance passed twice for a reference type, since that would make the
  pattern do nothing. Equal values of a value type are allowed.
- The class does not copy data between buffers. A step that needs the previous contents in the back buffer
  copies them itself.
- Not thread-safe. It gives consistent snapshots inside one thread, not safe sharing between threads.

### `DoubleBufferTests.cs`
**What it does:** 9 edit-mode NUnit tests: initial buffers, swapping, writes invisible until the swap, reset
after swap (which buffer is reset), a neighbour-propagation example that fails with in-place updates, the
same-instance check and null arguments.

**Decisions:**
- Two deliberate breakages were tried (swap losing the old front, reset on the wrong buffer). Both were caught.
  The tests were run outside Unity against a small NUnit stand-in and have not been run in the Unity Test Runner.

## Fixes and Problems

None so far. No bug has been found in a released version.
