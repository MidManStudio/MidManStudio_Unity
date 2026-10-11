# com.midmanstudio.patterns: Broker Chain

## Modules

### `BrokerResult.cs`
**What it does:** What a handler returns: `Continue` passes the query to the next handler, `Stop` ends the
chain.

**Tests:** `Tests/Editor/BrokerChainTests.cs`.

### `BrokerHandler.cs`
**What it does:** The handler delegate, `BrokerResult BrokerHandler<TQuery>(object sender, ref TQuery query)`.
A handler reads or changes the query and says whether the chain goes on. `sender` is whatever the caller
passed in, usually the object being asked.

**Decisions:**
- The query is passed with `ref`, so a struct query is changed in place with no copy and no boxing. A class
  query works too.

**Tests:** `Tests/Editor/BrokerChainTests.cs`.

### `BrokerPriority.cs`
**What it does:** Four constants used by the stat helpers: `Add` (0), `Multiply` (100), `Override` (200) and
`Clamp` (1000). Lower runs first.

**Decisions:**
- The values leave gaps, so a custom handler can run between two of the helpers by choosing a number in
  between.

**Tests:** `Tests/Editor/BrokerChainTests.cs`.

### `Broker.cs`
**What it does:** The central broker for one query type. Objects that want to change the answer to a
question register a handler with the broker instead of linking to each other. `Run(sender, ref query)` calls
every registered handler over the same query, lowest priority first, so each handler sees the result of the
ones before it. `Query(sender, seed)` does the same on a copy and returns the changed copy. `Register`
returns an `IDisposable` handle; disposing it unregisters the handler. A handler registered with a
lifetime is removed by `Tick(deltaTime)` once that much time has passed, and can have an `onExpired`
callback.

**How it works:**
- Handlers are kept in an array sorted by priority. A new handler goes after the last entry whose priority
  is not higher, so equal priorities keep registration order.
- The array is replaced on every register, unregister and expiry, never edited in place. A run in progress
  keeps iterating the array it started with, and each entry has an `Active` flag that the run checks before
  calling it.
- `Tick` subtracts the elapsed time from every timed entry, removes the ones that reached zero in one array
  rebuild, then runs their callbacks.

**Decisions:**
- This is Chain of Responsibility with the links held in one place. The classic form gives each handler a
  reference to the next one. Here the handlers know nothing about each other and the broker decides the
  order. The pattern is described this way in the git-amend tutorial on player stats and modifiers, which
  relies on C# delegates running in the order they were added. This broker makes the order explicit: priority
  first, then registration order.
- Unregistering takes effect at once, including for a run in progress. A handler that disposes a later
  handler stops it from running in the current run. A handler registered during a run misses that run and
  takes part in the next one.
- A handler can call `Run` on the same broker, for example a modifier that reads a second stat. The depth is
  limited to `MaxDepth` (16) and the next call throws `InvalidOperationException`, which turns a modifier
  that asks for the value it is changing into an error instead of a stack overflow.
- An exception from a handler ends the chain and propagates to the caller of `Run`. The event queue isolates
  handlers from each other because each one reacts to the event on its own. Here the handlers build a single
  answer, so a failing link would leave a half-computed value; the caller gets the exception instead of a
  quiet partial result. The nesting depth is restored and the broker stays usable.
- Time is advanced by the caller with `Tick`. The patterns package has no engine references and no
  dependency on utilities, so it has no clock of its own. A caller drives it from `Update` or from a tick
  dispatcher.
- `onExpired` runs after the handler is gone from the broker, so a callback can register a replacement. It
  is not called when the handle is disposed. When callbacks throw, every callback still runs and the first
  exception is rethrown afterwards.
- Not provided: thread safety, removing a handler by delegate instead of by handle, and asynchronous
  handlers. Not thread-safe; use it from one thread.

**Tests:** `Tests/Editor/BrokerChainTests.cs`.

### `StatQuery.cs`
**What it does:** The query type for stats: the `Stat` being asked for and its `Value` so far.

**Tests:** `Tests/Editor/BrokerChainTests.cs`.

### `StatModifiers.cs`
**What it does:** Four factory methods that return handlers for `StatQuery<TStat>`: `Add`, `Multiply`,
`Override` and `Clamp`. Each one only touches queries for its own stat and always continues the chain.

**Decisions:**
- Stats are compared with `EqualityComparer<TStat>.Default`, so any enum or other equatable type works as a
  stat id.

**Tests:** `Tests/Editor/BrokerChainTests.cs`.

### `StatSheet.cs`
**What it does:** A set of base stat values with a broker in front of them. `Get(stat)` starts from the base
value and runs the broker over it. `AddModifier`, `MultiplyModifier`, `OverrideModifier` and
`ClampModifier` register the matching handler at the matching `BrokerPriority`. `Broker` exposes the broker
for custom handlers and `Tick` advances timed modifiers.

**Decisions:**
- This is the use case the pattern is usually shown with, kept as a small ready-made class. A stat that
  has no base value reads as 0 before the handlers run.
- `Get` passes the sheet as the sender unless the caller gives another object, so a handler can tell who
  asked.

**Tests:** `Tests/Editor/BrokerChainTests.cs`.

### `BrokerChainTests.cs`
**What it does:** Edit-mode NUnit tests for the broker and the stat helpers (33 tests): equal and mixed
priorities, handlers seeing earlier results, `Query` working on a copy, `Stop`, the sender, disposing and
double disposing, the same delegate registered twice, disposing, registering and clearing during a run,
lifetimes and the exact moment of expiry, `onExpired` ordering and failure, nested queries, endless nesting,
a throwing handler, argument checks, and the stat sheet (base values, flat bonuses, multiplier order,
override and clamp order, per-stat isolation, timed modifiers, a handler reading another stat, the sender).

**Decisions:**
- The tests were run outside Unity against a small NUnit stand-in. Twelve deliberate breakages of the code
  were tried (unstable ordering, ignoring the active flag, ignoring `Stop`, expiry off by one, nesting depth
  never restored, untimed handlers expiring, swapped priorities, a stat match that always succeeds, `Query`
  doing nothing, and others). One was first missed, a `Clear` that left handles active, and a test for
  clearing during a run was added. They have not been run inside the Unity Test Runner yet.

## Fixes and Problems

None so far. No bug has been found in a released version.
