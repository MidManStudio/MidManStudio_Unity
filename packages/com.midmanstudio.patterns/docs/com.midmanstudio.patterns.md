# com.midmanstudio.patterns

## Overview

Generic design pattern building blocks, kept free of game-specific code and of Unity
engine references: the runtime assembly sets `noEngineReferences`, so the code is plain C#
and can be unit tested outside the editor. Singleton, object pooling and the event bus are
not part of this package; they live in `com.midmanstudio.utilities`.

The package is split by pattern, one doc part per pattern:

- [Command](com.midmanstudio.patterns/command.md) - undo/redo history, grouping, merging,
  composite commands, deferred command queue. Done.
- [State](com.midmanstudio.patterns/state.md) - stack-based state machine with deferred
  transitions, pausable states and condition-based transitions. Done.
- [Event Queue](com.midmanstudio.patterns/event-queue.md) - typed queue that separates posting an
  event from delivering it, with bounded or unbounded storage. Done.
- [Dependency Injection](com.midmanstudio.patterns/dependency-injection.md) - service locator and a
  factory-based container with singleton, scoped and transient lifetimes. Done.
- [Broker Chain](com.midmanstudio.patterns/broker-chain.md) - a central broker that runs registered handlers
  in priority order over one query, with timed handlers and a ready-made stat sheet for buffs and
  modifiers. Done.
- [Dirty Flag](com.midmanstudio.patterns/dirty-flag.md) - a dirty flag and lazily recomputed cached values that
  recompute only stale links of a dependency chain. Done.
- [Double Buffer](com.midmanstudio.patterns/double-buffer.md) - two buffers swapped each step so readers see one
  consistent snapshot. Done.
- [Flyweight](com.midmanstudio.patterns/flyweight.md) - a cache that shares one immutable instance per key.
  Done.
- [Prototype](com.midmanstudio.patterns/prototype.md) - a registry of template objects that creates new ones
  by cloning. Done.
- [Type Object](com.midmanstudio.patterns/type-object.md) - data-defined types with attributes inherited from a
  parent, a catalog, and instances with their own overrides. Done.
- [Data Locality](com.midmanstudio.patterns/data-locality.md) - items packed in one array with stable handles
  that survive removal. Done.
- [Spatial Partition](com.midmanstudio.patterns/spatial-partition.md) - uniform 2D grid for circle and
  rectangle queries without allocation. Done.
- [Decorator](com.midmanstudio.patterns/decorator.md) - a base class and a runtime-editable stack that wraps an
  object in decorators. Done.
- [Factory](com.midmanstudio.patterns/factory.md) - creates objects by key from registered creator functions,
  with and without an argument. Done.
- [Game Loop](com.midmanstudio.patterns/game-loop.md) - fixed-step simulation loop with a step cap,
  interpolation factor and time scale. Done.

Observer is not a part of this package. `com.midmanstudio.utilities` already provides it as
`MID_EventBus<T>` (immediate typed events) and `MID_SusValue` (observable values), and repeating it here
would give two versions of the same thing.

## CI and Workflows

`.github/workflows/release-packages.yml` publishes a GitHub Release for a package. Pushing a tag that
starts with `patterns/v` (for example `patterns/v0.1.0`), or running the workflow by hand and choosing
`patterns`, packages `packages/com.midmanstudio.patterns` and uses its `CHANGELOG.md` for the release
notes. No workflow runs this package's tests; Unity needs a licensed editor to run them, so they are run
from the Test Runner with the package listed under `testables` in the project's `manifest.json`.
