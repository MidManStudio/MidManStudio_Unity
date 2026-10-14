# com.midmanstudio.patterns: Subclass Sandbox

## Modules

### `SandboxedBehavior.cs`
**What it does:** Base class for behaviours that are written as subclasses and may only act on the world through a
context object. The context is usually an interface of primitive operations (move, spawn, play a sound).
`Run(context)` hands it to the behaviour, the subclass reads it through `Context` inside `Activate()`, and the
context is dropped again when the call ends. `IsRunning` tells whether a run is in progress.

**How it works:**
- `Context` throws `InvalidOperationException` outside a call to `Run`.
- The context is cleared and `IsRunning` is reset in a `finally` block, so a behaviour that throws can run again.
- Calling `Run` on an instance that is already running, for example from its own `Activate`, throws
  `InvalidOperationException`. Different instances do not block each other.

**Decisions:**
- Each behaviour depends on one small interface instead of on every system it touches, so behaviours can be
  tested with a fake context and the game systems can change without touching them.
- The class cannot stop a subclass from copying `Context` into a field of its own. It only prevents the accidental
  case, reading the context after the run through `Context`. Keeping the context is a convention to follow.
- Not thread-safe.

### `SandboxedFunction.cs`
**What it does:** The same type for behaviours that return a value, such as a target to attack, a damage amount or a
yes or no decision. `Run(context)` returns what `Evaluate()` returns.

### `SubclassSandboxTests.cs`
**What it does:** 12 edit-mode NUnit tests: acting through the context, the context readable only during a run,
`IsRunning`, a throwing behaviour, a run inside a run, a null context, one instance with several contexts,
independent instances and the same checks for the function variant.

**Decisions:**
- Six deliberate breakages were tried (context kept after the run, running flag never cleared, re-entrant run
  allowed, context readable outside a run, the function keeping its context, a null context accepted). All were caught.
  The tests were run outside Unity against a small NUnit stand-in and have not been run in the Unity Test Runner.

## Fixes and Problems

None so far. No bug has been found in a released version.
