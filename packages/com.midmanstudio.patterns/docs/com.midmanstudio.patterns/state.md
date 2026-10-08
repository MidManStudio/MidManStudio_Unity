# com.midmanstudio.patterns: State

## Modules

### `IState.cs, IPausableState.cs`
**What it does:** The state contracts. `IState<TContext>` has `Enter`, `Update` and `Exit`, each
receiving the machine's context. `IPausableState<TContext>` adds `Pause` and `Resume` for states that
can sit underneath another one and come back.

**Decisions:**
- The context is passed into every call instead of being held by the state. A state object then has no
  reference to the thing it controls, so one instance can be shared by several machines and is easy to
  construct in a test.
- Pausing is a separate interface. A state that does not implement it is exited when something is
  pushed over it and entered again when it is uncovered, which is the right behavior for states that
  own resources (a spawned effect, a subscription) and should release them while hidden.

**Tests:** `Tests/Editor/StateMachineTests.cs`, covered under "StateMachineTests.cs" below.

### `State.cs`
**What it does:** Abstract base class with empty virtual `Enter`, `Update` and `Exit`, so a state only
overrides what it uses.

**Tests:** `Tests/Editor/StateMachineTests.cs`. Every test state derives from it.

### `StateMachine.cs`
**What it does:** Stack-based state machine. `ChangeState` replaces the top state, `PushState` covers
it, `PopState` uncovers the one below, `Stop` exits everything. `Update(deltaTime)` calls the top state
and then checks the transition conditions added with `AddTransition` (from one state) and
`AddAnyTransition` (from whichever is current). `StateChanged` reports the previous and the new current
state after each completed change.

**How it works:**
- Every request goes into a queue. When the machine is idle the queue is flushed immediately. When a
  state callback or a `StateChanged` handler is running, the request waits and is applied as soon as that
  callback returns. A state is therefore never replaced while one of its own methods is still on the
  call stack.
- Callback order for a change is `Exit(old)`, then the stack is updated, then `Enter(new)`, then
  `StateChanged`. For a push over a pausable state it is `Pause(old)`, `Enter(new)`. For a pop it is
  `Exit(top)`, then `Resume(uncovered)` for a pausable state or `Enter(uncovered)` otherwise.
- `Update` checks any-state transitions first, then the current state's transitions, in the order they
  were added. The first condition that is true queues a change and the rest are not evaluated.

**Decisions:**
- Run-to-completion queueing was chosen over applying requests immediately. States routinely ask for the
  next state from inside `Enter` or `Update`, and immediate application would run the new state's `Enter`
  inside the old state's `Enter`, with callbacks interleaved in an order that is hard to reason about.
- Changing to the state that is already current does nothing. This keeps an any-state transition whose
  condition stays true from re-entering the same state every frame. A transition whose target is the
  current state is skipped for the same reason.
- `maxChainedTransitions` (64 by default) bounds how many requests one flush applies. Two states that
  keep sending each other back would otherwise loop forever; hitting the limit throws
  `InvalidOperationException` and drops the waiting requests.
- Failure behavior is fixed so the machine is never left half changed: if `Exit` (or `Pause`) throws, the
  old state stays current and the new one is not entered. If `Enter` (or `Resume`) throws, the new state
  is already current and the exception propagates. Any exception drops the requests still waiting, since
  they were queued against a state that no longer holds. An exception from a state's `Update` does the
  same.
- `PopState` on a stack of one throws instead of doing nothing, because silently ignoring it would hide
  a push and pop that do not match. When the pop was queued from inside a callback, the exception comes
  from the call that applies it.
- `Update` called from inside a callback throws. It is the one entry point that cannot be deferred.
- `Stop` removes each state as soon as its `Exit` returns, so an `Exit` that throws leaves the states
  below it on the stack.
- Not thread-safe. Use it from one thread.

**Tests:** `Tests/Editor/StateMachineTests.cs`.

### `StateMachineTests.cs`
**What it does:** Edit-mode NUnit tests for the state machine (26 tests): start, change, same-state
no-op, update, the `StateChanged` event, push and pop with pausable and plain states, stop, requests made
from `Enter` and `Update`, the chain limit, update from inside a callback, the failure rules for `Exit`,
`Enter` and `Update`, the transition rules (order, any-state priority, skipping the current state,
clearing), `IsIn`, and argument checks. They show up in the Test Runner when the package is listed in the
project's `testables`.

**Decisions:**
- The tests were run outside Unity against a small NUnit stand-in. Six deliberate breakages of the
  machine were each caught: requests applied immediately instead of deferred, waiting requests kept after
  a failure, any-state transitions not checked first, `Stop` exiting bottom first, `Push` never pausing,
  and the chain limit removed. With the limit removed the run does not finish, which is itself a failure.
  They have not been run inside the Unity Test Runner yet.

## Fixes and Problems

None so far. The behavior above was settled before the first release, and no bug has been found in a
released version.
