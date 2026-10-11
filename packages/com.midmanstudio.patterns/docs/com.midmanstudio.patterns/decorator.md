# com.midmanstudio.patterns: Decorator

## Modules

### `Decorator.cs`
**What it does:** Base class for a decorator. It holds the wrapped object in `Inner`. A subclass implements the
same interface as the wrapped object, forwards calls to `Inner` and adds its own behaviour before or after.
A null inner object throws `ArgumentNullException`.

### `DecoratorStack.cs`
**What it does:** An ordered list of decorators that can change at runtime. `Add(wrap)` appends a function that
receives the object built so far and returns the wrapper, and returns a handle that removes it when disposed.
`Build(core)` applies every decorator, the first added innermost, so the last added is the object callers use.

**Decisions:**
- Order is registration order only. For handlers that need explicit priorities, use the Broker Chain.
- Objects that were already built are not tracked. After the list changes, build again.
- A decorator that returns null makes `Build` throw `InvalidOperationException` and names its position.
- Not thread-safe.

### `DecoratorTests.cs`
**What it does:** 9 edit-mode NUnit tests: no decorators, ordering in both directions, a decorator seeing the
object built so far, removing by handle, objects built earlier staying unchanged, separate builds, `Clear`, a
null result and null arguments.

**Decisions:**
- Two deliberate breakages were tried (reverse order, `Dispose` not removing). Both were caught.
  The tests were run outside Unity against a small NUnit stand-in and have not been run in the Unity Test Runner.

## Fixes and Problems

None so far. No bug has been found in a released version.
