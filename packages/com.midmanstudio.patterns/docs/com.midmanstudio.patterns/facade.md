# com.midmanstudio.patterns: Facade

## Modules

### `FacadeBase.cs`
**What it does:** Base class for a facade: one small object that gives callers a few simple methods and hides the
subsystems behind them. A subclass registers a creator for each subsystem with `Register<T>` and fetches it with
`Get<T>`. `IsCreated<T>` tells whether a subsystem exists yet. `Dispose` disposes every created subsystem that
implements `IDisposable`.

**How it works:**
- A subsystem is created the first time it is fetched and cached after that.
- Subsystems are disposed newest first, so one that was created from another one (inside its creator, through
  `Get`) is disposed before the one it depends on.
- Every subsystem is disposed even if one throws. The first exception is rethrown afterwards.
- A creator that asks for its own type, directly or through other subsystems, throws `InvalidOperationException`.
  A creator that throws or returns null caches nothing, so the next call tries again.
- After `Dispose`, `Get` and `Register` throw `ObjectDisposedException`. A second `Dispose` does nothing.

**Decisions:**
- `Register`, `Get` and `IsCreated` are protected, so only the facade itself reaches the subsystems. Callers use
  the facade's own methods.
- Subsystems are created lazily because many of them are not needed in every session.
- This is not a dependency injection container. There are no lifetimes and no resolving by interface from
  outside. Use the DI container when that is needed.
- Not thread-safe.

### `FacadeTests.cs`
**What it does:** 13 edit-mode NUnit tests: lazy creation, one instance per type, unregistered and duplicate
registrations, null results, a subsystem created from another, newest-first disposal, unused subsystems left
alone, a failing dispose, double dispose, use after dispose, a circular creation and a throwing creator.

**Decisions:**
- Seven deliberate breakages were tried. Six were caught (not caching, oldest-first disposal, stopping at the
  first failure, the creation guard never cleared, a null subsystem accepted, `Get` working after dispose). The
  seventh, removing the guard against a second `Dispose`, was not detected. It does not change behaviour, because
  the first `Dispose` already empties the list of subsystems.
  The tests were run outside Unity against a small NUnit stand-in and have not been run in the Unity Test Runner.

## Fixes and Problems

None so far. No bug has been found in a released version.
