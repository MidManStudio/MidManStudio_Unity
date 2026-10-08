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

## State (`MidManStudio.Patterns.State`)

| Type | Members |
|---|---|
| `IState<TContext>` | `Enter(TContext)`, `Update(TContext, float deltaTime)`, `Exit(TContext)` |
| `IPausableState<TContext> : IState<TContext>` | `Pause(TContext)`, `Resume(TContext)` |
| `State<TContext>` | Abstract base with empty virtual `Enter`, `Update`, `Exit` |

### `StateMachine<TContext>`
| Member | Description |
|---|---|
| `StateMachine(TContext context, int maxChainedTransitions = 64)` | |
| `Context`, `Current`, `Previous`, `StackDepth`, `IsRunning`, `IsIn<TState>()` | |
| `ChangeState(IState<TContext>)` | Exits the top state, then enters the new one. Same state is a no-op. |
| `PushState(IState<TContext>)` | Covers the current state. Pauses it if it is pausable, otherwise exits it. |
| `PopState()` | Exits the top state and uncovers the one below. Throws when there is nothing below. |
| `Stop()` | Exits every state, top first |
| `Update(float deltaTime)` | Updates the top state, then checks transitions. Throws if called from a callback. |
| `AddTransition(from, to, Func<TContext,bool>)`, `AddAnyTransition(to, Func<TContext,bool>)`, `ClearTransitions()` | Any-state transitions are checked first. First true condition wins. |
| `event Action<IState<TContext>, IState<TContext>> StateChanged` | (previous, current), after a change completes |

Requests made from inside a callback are queued and applied when the callback returns.

## Event Queue (`MidManStudio.Patterns.EventQueue`)

### `EventQueue<TEvent>`
| Member | Description |
|---|---|
| `EventQueue(int capacity = 0, EventQueueOverflow overflow = DropOldest)` | 0 means unbounded |
| `bool Post(TEvent)` | False only when `DropNewest` discarded this event. Throws under `Throw` when full. |
| `IDisposable Subscribe(Action<TEvent>)` | Dispose the handle to unsubscribe, effective immediately |
| `int Process(int maxEvents = int.MaxValue)` | Delivers up to that many of the events queued when the call began |
| `Count`, `IsEmpty`, `Capacity`, `SubscriberCount`, `DroppedCount`, `Clear()` | |
| `event Action<TEvent, Exception> HandlerFailed` | With a subscriber delivery continues. With none, the failure is rethrown after every handler has run. |

`EventQueueOverflow`: `DropOldest`, `DropNewest`, `Throw`.

## Dependency Injection (`MidManStudio.Patterns.DependencyInjection`)

### `ServiceLocator`
| Member | Description |
|---|---|
| `ServiceLocator(ServiceLocator parent = null)` | Lookups fall back to the parent |
| `Register<T>(T)`, `Unregister<T>()`, `Has<T>()`, `TryGet<T>(out T)`, `Get<T>()`, `Clear()`, `Count` | `Get` throws `ServiceNotFoundException` |

### `Container : IResolver, IDisposable`
| Member | Description |
|---|---|
| `Register<T>(Func<IResolver,T> factory, Lifetime lifetime = Singleton)` | Replaceable only until first resolved |
| `RegisterInstance<T>(T instance, bool ownsInstance = false)` | Singleton. Disposed with the container only when owned. |
| `Resolve<T>()`, `TryResolve<T>(out T)`, `Resolve(Type)`, `TryResolve(Type, out object)`, `IsRegistered<T>()` | |
| `CreateScope()`, `IsRoot` | Child container. Shadows and extends the parent. |
| `Dispose()` | Disposes owned instances newest first, throws an `AggregateException` if any fail |

`Lifetime`: `Singleton` (one per owning container), `Scoped` (one per scope, not resolvable from the root),
`Transient` (new each time, not tracked).
`IResolver`: `Resolve<T>()`, `TryResolve<T>(out T)`, `Resolve(Type)`, `TryResolve(Type, out object)`.
`ServiceNotFoundException.ServiceType`, `CircularDependencyException.Chain`.

