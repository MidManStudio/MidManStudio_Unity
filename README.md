# com.midmanstudio.patterns
**MidMan Studio Patterns** v0.1.0. Generic design pattern building blocks for Unity 2022.3+.
No dependencies. The runtime assembly has no engine references, so everything in it also runs
and tests outside Unity.

Singleton, object pooling and the event bus already live in `com.midmanstudio.utilities` and
are not repeated here.

## Installation
**Via git URL** (Unity Package Manager, Add package from git URL):
https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.patterns

**Via local file path** (development, manifest.json):
"com.midmanstudio.patterns": "file:../../packages/com.midmanstudio.patterns"

To see the package's tests in the Test Runner, also add it to the `testables` list in the same manifest.

## What's Included
| Pattern | Namespace | Description |
|---|---|---|
| Command | MidManStudio.Patterns.Command | Undo/redo history with grouping and merging, composite commands, deferred command queue |

## Quick start
```csharp
using MidManStudio.Patterns.Command;

var history = new CommandHistory(capacity: 100);

history.Execute(new UndoableActionCommand(
    execute: () => door.Open(),
    undo:    () => door.Close(),
    name:    "Open door"));

history.Undo();   // door closed again
history.Redo();   // door open again
```

See `APICATALOG.md` for the full API and `docs/com.midmanstudio.patterns/command.md` for the design notes.
