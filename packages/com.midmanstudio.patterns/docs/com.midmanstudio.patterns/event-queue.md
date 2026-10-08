# com.midmanstudio.patterns: Event Queue

## Modules

### `EventQueueOverflow.cs`
**What it does:** The three ways a bounded queue can handle a post when it is full: `DropOldest` (keep
the newest events), `DropNewest` (keep the events already queued), `Throw`.

**Tests:** `Tests/Editor/EventQueueTests.cs`.

### `EventQueue.cs`
**What it does:** Typed queue that separates posting an event from delivering it. `Post` stores the
event. `Process(maxEvents)` delivers the queued events, oldest first, to every subscriber and returns how
many were delivered. `Subscribe` returns an `IDisposable` handle; disposing it unsubscribes. The queue is
either unbounded (capacity 0, grows as needed) or bounded with an overflow policy, and counts the events
the policy discarded in `DroppedCount`.

**How it works:**
- Storage is a ring buffer. The unbounded form doubles the array and copies the events in order when it
  fills.
- The subscriber list is an array that is replaced on every subscribe and unsubscribe, never edited in
  place, so a delivery in progress keeps iterating the array it started with.
- Each subscriber is a small object with an `Active` flag. Delivery skips an inactive one.

**Decisions:**
- This pattern exists next to `MID_EventBus<T>` in the utilities package because the two solve different
  problems. The bus delivers immediately, from a static registry. The queue delays delivery to a point the
  caller chooses, is an instance (so each system can own its own channel and a test can make a fresh one),
  and keeps a bound on memory. The patterns package has no dependency on utilities, so the bus is not used
  here.
- `Process` delivers only the events that were queued when the call began. A handler that posts another
  event cannot keep one call from returning; the new event is delivered by the next call.
- Unsubscribing takes effect at once, including for the event being delivered right now. A handler that
  disposes a later subscriber stops that subscriber from seeing the current event. A subscriber added
  during delivery misses the current event and receives the following ones.
- Subscriptions are identified by handle, not by delegate. The same delegate subscribed twice is called
  twice, and disposing one handle removes only that subscription.
- One failing handler does not stop the others. With a `HandlerFailed` subscriber, failures are reported
  to it and delivery carries on. With none, every handler still runs for the event, then the single
  exception is rethrown with its original stack trace, or an `AggregateException` is thrown when more
  than one handler failed. The failing event counts as delivered and the events after it stay queued.
- Calling `Process` from inside a handler throws. With a `HandlerFailed` subscriber that shows up as an
  ordinary handler failure.
- Events are generic, so struct events are stored and delivered without boxing.
- Coalescing, priorities and thread safety are not provided. Not thread-safe; use it from one thread.

**Tests:** `Tests/Editor/EventQueueTests.cs`.

### `EventQueueTests.cs`
**What it does:** Edit-mode NUnit tests for the queue (20 tests): ordering, delivery to several
subscribers, the `Process` limit, events posted by handlers, subscribing and unsubscribing during
delivery, double dispose, the same delegate subscribed twice, the three overflow policies, growth and
wrap-around, handler failure with and without a `HandlerFailed` subscriber, `Process` from a handler,
`Clear`, struct events, and argument checks.

**Decisions:**
- The tests were run outside Unity against a small NUnit stand-in. Three deliberate breakages were each
  caught: an unsubscribe that did not take effect during delivery, a discard that was not counted, and
  `Process` delivering events posted during the call (the run did not finish). They have not been run
  inside the Unity Test Runner yet.

## Fixes and Problems

None so far. No bug has been found in a released version.
