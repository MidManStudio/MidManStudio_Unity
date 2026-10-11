# com.midmanstudio.patterns: Prototype

## Modules

### `IPrototype.cs`
**What it does:** Interface for an object that can produce an independent copy of itself, `T Clone()`.

### `PrototypeRegistry.cs`
**What it does:** Keeps configured template objects by key and creates new objects by cloning them. `Register`
adds a template and rejects a duplicate key, `Set` replaces. `Create(key)` returns a clone,
`Create(key, customize)` also passes the clone to a function before returning it, and `TryCreate` returns false
for an unknown key.

**Decisions:**
- The registry never copies anything itself. Whether a clone is deep or shallow is up to the type's `Clone`.
- A clone that is null, or is the prototype itself, throws `InvalidOperationException`. Returning the prototype
  would let every "copy" change the template.
- `customize` runs on the clone only, so one-off changes (a boss with more health) do not touch the template.
- A key comparer can be passed.
- Not thread-safe.

### `PrototypeTests.cs`
**What it does:** 8 edit-mode NUnit tests: separate copies with the same state, changing a clone, `customize`,
duplicate registration and `Set`, unknown keys, bad clones, `Unregister` and null arguments.

**Decisions:**
- Two deliberate breakages were tried. The self-clone check being removed was caught. Removing the duplicate
  check was not detected, because `Dictionary.Add` still throws `ArgumentException` for a duplicate key; only
  the message differs.
  The tests were run outside Unity against a small NUnit stand-in and have not been run in the Unity Test Runner.

## Fixes and Problems

None so far. No bug has been found in a released version.
