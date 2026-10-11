# com.midmanstudio.patterns: Dirty Flag

## Modules

### `IVersioned.cs`
**What it does:** Interface with one member, `int Version`. A consumer remembers the version it last used and
treats any other value as "changed".

### `DirtyFlag.cs`
**What it does:** A flag that says "something changed, recompute". `Mark()` sets it and raises `Version`;
`Consume()` returns whether it was set and clears it. It starts dirty unless `startDirty` is false.

**Decisions:**
- `Version` only rises on `Mark`. `Consume` does not change it, so a flag that one consumer cleared still
  reads as changed to every other consumer that tracks versions.
- `Version` wraps around after `int.MaxValue`. Consumers compare for inequality, so a wrap is harmless.

### `CachedValue.cs`
**What it does:** A value computed on first read, and again only after `Invalidate()` or a change in one of
its dependencies. Dependencies are `IVersioned` objects, a `DirtyFlag` or another `CachedValue`, so chains of
derived values recompute only the stale links.

**How it works:**
- `Value` and `Version` both bring the value up to date first. A refresh records each dependency's version,
  runs the compute function, clears the dirty state and raises its own version.
- A value is stale when it was invalidated, never computed, or any dependency's version differs from the one
  recorded at the last compute.

**Decisions:**
- Staleness is found by comparing version numbers on read, not by notifying dependents. There are no events
  to subscribe to or unsubscribe from, so a cached value can be dropped without cleanup.
- Reading `Version` computes the value. A downstream value can only learn that the one it depends on changed by
  asking it, and an invalidated value only knows its version after it recomputed.
- If the compute function throws, the value stays invalidated and the next read tries again. The exception
  propagates to the caller.
- A value that reads itself, directly or through other cached values, throws `InvalidOperationException` on
  the first read and stays usable afterwards.
- Not thread-safe.

### `DirtyFlagTests.cs`
**What it does:** 14 edit-mode NUnit tests: flag defaults, marking and consuming, lazy and single
computation, invalidation, dependency on a flag, unrelated flags, a three-link chain, version behaviour, a
throwing compute function, a circular dependency and argument checks.

**Decisions:**
- Six deliberate breakages of the code were tried (never marked clean, dependency changes ignored, versions
  never recorded, cycle check removed, `Mark` not raising the version, `Consume` not clearing). All were caught.
  The tests were run outside Unity against a small NUnit stand-in and have not been run in the Unity Test Runner.

## Fixes and Problems

None so far. No bug has been found in a released version.
