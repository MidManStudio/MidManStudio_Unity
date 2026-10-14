# com.midmanstudio.patterns: Template Method

## Modules

### `TemplateAction.cs`
**What it does:** Fixes the order of an action's steps and lets subclasses fill them in. `Run(context)` is not
virtual. It calls `CanRun`, then `Begin`, then `Execute`, and always finishes with `End`. A subclass overrides the
steps it needs: only `Execute` is required, the others default to doing nothing (`CanRun` defaults to true).
`Run` returns false when `CanRun` said no, and true when the action ran to the end.

**How it works:**
- When `Execute` throws, `OnFailed(context, error)` is called, then `End`, and the same exception propagates.
- When `Begin` throws, the action never started: `OnFailed` and `End` are not called.
- When `OnFailed` throws, `End` still runs and the new exception replaces the original one.

**Decisions:**
- `Begin` sits outside the guarded block. A step that fails before the action starts has nothing to clean up, and
  running `End` after it could undo something that was never done.
- Making `Run` non-virtual is what gives every subclass the guarantee that `End` runs. A test checks it.
- The action keeps no state between runs, so one instance can run many times and with different contexts.
- Not thread-safe.

### `TemplateMethodTests.cs`
**What it does:** 11 edit-mode NUnit tests: step order, a refused run, a failing `Execute`, a failing `Begin`, a
failing `OnFailed`, a failing `End`, defaults for steps that are not overridden, `Run` not being virtual, the
context reaching every step and repeated runs.

**Decisions:**
- Five deliberate breakages were tried (`Begin` inside the guarded block, `OnFailed` never called, `CanRun`
  ignored, `Run` made virtual, `End` skipped after a failure). All were caught.
  The tests were run outside Unity against a small NUnit stand-in and have not been run in the Unity Test Runner.

## Fixes and Problems

None so far. No bug has been found in a released version.
