# com.midmanstudio.patterns: Command

## Modules

### `ICommand.cs, IUndoableCommand.cs, IMergeableCommand.cs, INamedCommand.cs`
**What it does:** The command contracts. `ICommand` has `Execute()`.
`IUndoableCommand` adds `Undo()`. `IMergeableCommand` adds `TryMerge(next)` so a
burst of small edits can collapse into one undo step. `INamedCommand` exposes a
`Name` for "Undo Move Door" style labels.

**Decisions:**
- Undo is a separate interface rather than a method on `ICommand`. A queue of
  one-way commands (spawn this, play that) should not have to implement an
  `Undo` that does nothing, and `CommandHistory` can then reject a command that
  cannot be undone at compile time.
- `Name` is its own interface instead of a property on `ICommand`, so naming is
  optional and the delegate commands can carry one without forcing it on
  every implementer.
- `TryMerge` is called after the next command has already executed. The merging
  command takes the other's effect into its own `Undo`, and the other is dropped.
  Merging on `Execute` order keeps the merge decision inside the command, which
  is the only place that knows whether two edits are the same kind of edit.

### `ActionCommand.cs`
**What it does:** `ActionCommand` wraps one delegate as a one-way command.
`UndoableActionCommand` wraps an execute and an undo delegate as an undoable one.
Both take an optional name. A null delegate throws `ArgumentNullException` at
construction, so the mistake shows up where it was made and not at the first run.

### `CompositeCommand.cs`
**What it does:** Runs several undoable commands as one step. `Execute` runs the
children in order and `Undo` runs them newest first.

**Decisions:**
- `Execute` is all or nothing. If a child throws, the children that already ran
  are undone newest first and the original exception is rethrown. The child that
  threw is not undone: it reported failure, so it is responsible for leaving
  itself consistent. If a rollback step also throws, an `AggregateException`
  carries the original failure first, then the rollback failures.
- `Undo` keeps going past a child that throws, so as much as possible is undone,
  then throws an `AggregateException` with every failure. Stopping at the first
  failure would leave the world half undone with no way to finish.
- A null child throws at construction.

### `CommandHistory.cs`
**What it does:** Undo/redo stack. `Execute` runs a command and records it,
`Undo` and `Redo` walk the stack, and executing a new command clears redo.
Supports a capacity (0 is unlimited, the oldest step is dropped past it), step
names, merging, groups, and a `Changed` event for refreshing UI.

**Decisions:**
- A command that throws is never recorded. A failed `Execute` leaves both stacks
  as they were. A failed `Undo` leaves the command on the undo stack, and a failed
  `Redo` leaves it on the redo stack, so the caller can retry or inspect.
- Merging is blocked after an undo, a redo, a clear, or a closed group, and when
  `BreakMerge()` is called. Without that, a command executed after an undo would
  merge into an older step and silently change what that step means. Call
  `BreakMerge()` when a continuous edit ends, for example on mouse release.
- Groups run each command immediately, so later commands in the group see the
  earlier ones' effects, and record the whole group as one `CompositeCommand` when it
  closes. The composite is recorded without being executed again. An unnamed group
  holding one command records that command directly. Groups cannot be nested, and
  `Undo`, `Redo` and `Clear` throw while one is open, because the history is not in
  a consistent state to walk until it closes. `GroupScope` carries an id so a stale
  scope cannot close a later group.
- A command that calls back into `Execute`, `Undo` or `Redo` throws
  `InvalidOperationException`. Re-entering would change the stacks in the middle of
  the operation that is walking them. The guard is released when the failing
  operation unwinds, so one bad command does not leave the history stuck.
- Trimming uses `List.RemoveRange` on the oldest end. That is a shift of at most
  `capacity` references per recorded command, which is negligible at the sizes an
  undo stack uses; a ring buffer would add complexity for no visible gain.
- Not thread-safe. Use it from one thread.

### `CommandQueue.cs`
**What it does:** First-in, first-out queue of commands to run later.
`Process(maxCommands)` runs up to that many of the commands queued when the call
began and returns how many ran. Call it from your own update loop or tick
dispatcher to spread work across frames.

**Decisions:**
- Commands queued by a running command wait for the next `Process` call. Without
  that, a command that always queues another would keep one call from returning.
- A failing command is removed from the queue. If `CommandFailed` has a
  subscriber, it is told and processing continues. With no subscriber the
  exception is rethrown, so a failure is never silently swallowed. The commands
  after it stay queued.
- Calling `Process` from inside a running command throws. It is reported through
  `CommandFailed` like any other command failure.
- There is no `MonoBehaviour` runner. The runtime assembly has no engine
  references, so it stays testable outside Unity, and a project already has its
  own update loop to call `Process` from.
- Not thread-safe. Use it from one thread.

### `CommandTests.cs`
**What it does:** Edit-mode NUnit tests for the whole Command API (37 tests):
history order, redo clearing, failure behavior of execute/undo/redo, capacity,
names, the `Changed` event, merging and merge breaking, groups, re-entrancy,
composite rollback, the queue, and delegate commands. They live under
`Tests/Editor` and show up in the Test Runner when the package is listed in the
project's `testables`.

**Decisions:**
- The tests were run outside Unity against a small NUnit stand-in, and three
  deliberate breakages of the production code (redo not cleared, undo not
  breaking merge, composite undo in forward order) were each caught. The
  merge-after-undo breakage was first missed because redo also breaks the merge,
  which is why `AfterAnUndo_TheNextCommandDoesNotMergeIntoAnOlderStep` exists.
  They have not been run inside the Unity Test Runner yet.
