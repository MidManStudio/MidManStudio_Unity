# com.midmanstudio.patterns: API Catalog
**MidMan Studio Patterns** v0.1.0

## Command (`MidManStudio.Patterns.Command`)

### Interfaces
| Type | Members |
|---|---|
| `ICommand` | `void Execute()` |
| `IUndoableCommand : ICommand` | `void Undo()` |
| `IMergeableCommand : IUndoableCommand` | `bool TryMerge(IUndoableCommand next)` |
| `INamedCommand` | `string Name { get; }` |

### Delegate commands
| Type | Constructor |
|---|---|
| `ActionCommand : ICommand, INamedCommand` | `(Action execute, string name = null)` |
| `UndoableActionCommand : IUndoableCommand, INamedCommand` | `(Action execute, Action undo, string name = null)` |

### `CompositeCommand : IUndoableCommand, INamedCommand`
| Member | Description |
|---|---|
| `CompositeCommand(string name, params IUndoableCommand[] commands)` | Also takes an `IEnumerable<IUndoableCommand>` |
| `int Count` | Number of child commands |
| `Execute()` | Runs children in order. If one throws, undoes the earlier ones (newest first) and rethrows. |
| `Undo()` | Undoes children newest first, keeps going past a failure, then throws an `AggregateException` if any failed. |

### `CommandHistory`
| Member | Description |
|---|---|
| `CommandHistory(int capacity = 100)` | 0 means unlimited |
| `Capacity`, `SetCapacity(int)` | Lowering it drops the oldest steps immediately |
| `UndoCount`, `RedoCount`, `CanUndo`, `CanRedo` | |
| `NextUndoName`, `NextRedoName` | From `INamedCommand`, or null |
| `Execute(IUndoableCommand)` | Runs and records. Clears redo. Nothing is recorded if it throws. |
| `bool Undo()`, `bool Redo()` | False when there is nothing to do. Stacks are unchanged if the command throws. |
| `Clear()` | Forgets history without undoing |
| `BreakMerge()` | Next command will not merge into the previous one |
| `BeginGroup(string name = null)` | Returns a disposable `GroupScope`. Commands run immediately and are recorded as one step. No nesting. |
| `EndGroup()`, `IsGroupOpen` | |
| `event Action Changed` | After any change to the history |

### `CommandQueue`
| Member | Description |
|---|---|
| `Enqueue(ICommand)`, `Enqueue(Action, string name = null)` | |
| `int Process(int maxCommands = int.MaxValue)` | Runs up to that many of the commands queued when the call began. Returns how many ran. |
| `Count`, `IsEmpty`, `Clear()` | |
| `event Action<ICommand, Exception> CommandFailed` | With a subscriber a failed command is dropped and processing continues. With none, the exception is rethrown. |
