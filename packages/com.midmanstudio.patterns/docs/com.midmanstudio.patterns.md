# com.midmanstudio.patterns

## Overview

Generic design pattern building blocks, kept free of game-specific code and of Unity
engine references: the runtime assembly sets `noEngineReferences`, so the code is plain C#
and can be unit tested outside the editor. Singleton, object pooling and the event bus are
not part of this package; they live in `com.midmanstudio.utilities`.

The package is split by pattern, one doc part per pattern:

- [Command](com.midmanstudio.patterns/command.md) - undo/redo history, grouping, merging,
  composite commands, deferred command queue. Done.
