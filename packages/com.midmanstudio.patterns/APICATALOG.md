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

## Broker Chain (`MidManStudio.Patterns.BrokerChain`)

### `Broker<TQuery>`
| Member | Description |
|---|---|
| `IDisposable Register(BrokerHandler<TQuery> handler, int priority = 0, float lifetimeSeconds = 0f, Action onExpired = null)` | Lower priority runs first, equal priorities in registration order. Dispose the handle to unregister, effective immediately. A lifetime above 0 makes `Tick` remove the handler. |
| `BrokerResult Run(object sender, ref TQuery query)` | Runs every handler over the query. Returns `Stop` if a handler ended the chain. A handler exception propagates. |
| `TQuery Query(object sender, TQuery seed)` | Runs the chain on a copy and returns it |
| `void Tick(float deltaTime)` | Removes timed handlers whose lifetime ran out, then runs their `onExpired` callbacks |
| `void Clear()`, `Count`, `const int MaxDepth` | `MaxDepth` (16) limits nested `Run` calls on one broker |

`delegate BrokerResult BrokerHandler<TQuery>(object sender, ref TQuery query)`.
`BrokerResult`: `Continue`, `Stop`.
`BrokerPriority`: `Add` (0), `Multiply` (100), `Override` (200), `Clamp` (1000).

### Stats
| Type | Members |
|---|---|
| `StatQuery<TStat>` | `Stat`, `Value`, `StatQuery(TStat stat, float value)` |
| `StatModifiers` | `Add<TStat>(stat, amount)`, `Multiply<TStat>(stat, factor)`, `Override<TStat>(stat, value)`, `Clamp<TStat>(stat, min, max)`, each returning a `BrokerHandler<StatQuery<TStat>>` |
| `StatSheet<TStat>` | `SetBase`, `GetBase` (0 when unset), `HasBase`, `Get(stat, sender = null)`, `AddModifier`, `MultiplyModifier`, `OverrideModifier`, `ClampModifier`, `Tick(deltaTime)`, `Broker` |

## Dirty Flag (`MidManStudio.Patterns.DirtyFlag`)
| Type | Members |
|---|---|
| `IVersioned` | `int Version` |
| `DirtyFlag` | `DirtyFlag(bool startDirty = true)`, `IsDirty`, `Version`, `Mark()`, `bool Consume()` |
| `CachedValue<T>` | `CachedValue(Func<T> compute, params IVersioned[] dependencies)`, `Value`, `Version` (both bring the value up to date), `IsInvalidated`, `Invalidate()` |

## Double Buffer (`MidManStudio.Patterns.DoubleBuffer`)
| Member | Description |
|---|---|
| `DoubleBuffer<T>(T front, T back)`, `static Create(Func<T>)` | Rejects the same instance twice for reference types |
| `Front`, `Back`, `SwapCount` | Readers use `Front`, the writer fills `Back` |
| `void Swap()`, `void Swap(Action<T> resetBack)` | The second form resets the new back buffer after swapping |

## Flyweight (`MidManStudio.Patterns.Flyweight`)
| Member | Description |
|---|---|
| `FlyweightCache<TKey, TShared>(Func<TKey, TShared> factory, IEqualityComparer<TKey> comparer = null)` | |
| `TShared Get(TKey key)` | Creates on first use. A null or throwing factory caches nothing |
| `TryGet`, `Contains`, `Remove`, `Clear`, `Count`, `CreatedCount` | |

## Prototype (`MidManStudio.Patterns.Prototype`)
| Member | Description |
|---|---|
| `IPrototype<T>` | `T Clone()` |
| `PrototypeRegistry<TKey, T>` | `Register` (rejects duplicates), `Set`, `Unregister`, `Contains`, `Count`, `Create(key)`, `Create(key, Action<T> customize)`, `TryCreate(key, out T)`. A clone that is null or the prototype itself throws |

## Type Object (`MidManStudio.Patterns.TypeObject`)
| Type | Members |
|---|---|
| `ObjectType` | `ObjectType(string name, ObjectType parent = null)`, `Name`, `Parent`, `Set<T>`, `Remove`, `Has`, `HasOwn`, `Get<T>`, `Get<T>(name, fallback)`, `TryGet<T>`, `IsA`, `AttributeNames()`. A wrong stored type throws `InvalidCastException` |
| `TypeCatalog` | `Define(name, parentName = null)`, `Get`, `TryGet`, `Contains`, `Count`, `Names` |
| `TypedObject` | `TypedObject(ObjectType type)`, `Type`, `Set<T>`, `Remove`, `Has`, `HasOwn`, `Get<T>`, `Get<T>(name, fallback)`, `TryGet<T>` |

## Data Locality (`MidManStudio.Patterns.DataLocality`)
| Type | Members |
|---|---|
| `PackedHandle` | `IsAssigned`; `default` is never valid |
| `PackedList<T>` | `PackedList(int capacity = 16)`, `Count`, `Items` (live backing array, indices 0 to `Count` - 1), `PackedHandle Add(T)`, `bool Remove(handle)`, `IsValid`, `IndexOf`, `TryGet`, indexer get and set, `Clear()` |

## Spatial Partition (`MidManStudio.Patterns.SpatialPartition`)
| Type | Members |
|---|---|
| `GridHandle` | `IsAssigned`; `default` is never valid |
| `SpatialGrid2D<T>` | `SpatialGrid2D(float cellSize)`, `Count`, `OccupiedCells`, `Add(item, x, y)`, `Move(handle, x, y)`, `Remove`, `IsValid`, `TryGetPosition`, `TryGetItem`, `Clear`, `QueryCircle(cx, cy, radius, List<T> results)`, `QueryRect(minX, minY, maxX, maxY, List<T> results)`. Queries append to the list and return the number added |

## Decorator (`MidManStudio.Patterns.Decorator`)
| Type | Members |
|---|---|
| `Decorator<T>` | Abstract base with `protected readonly T Inner` |
| `DecoratorStack<T>` | `IDisposable Add(Func<T, T> decorator)`, `T Build(T core)` (first added is innermost), `Count`, `Clear()` |

## Factory (`MidManStudio.Patterns.Factory`)
| Type | Members |
|---|---|
| `Factory<TKey, TProduct>` | `Register`, `Set`, `Unregister`, `Contains`, `Count`, `Keys`, `Create(key)`, `TryCreate(key, out product)` |
| `Factory<TKey, TArg, TProduct>` | Same, with `Create(key, arg)` and `TryCreate(key, arg, out product)` |

## Game Loop (`MidManStudio.Patterns.GameLoop`)
| Member | Description |
|---|---|
| `FixedStepLoop(float stepSeconds, Action<float> onStep, int maxStepsPerAdvance = 5)` | |
| `int Advance(float frameSeconds)` | Runs the steps that are due, returns how many ran. Drops steps beyond the cap |
| `Alpha`, `Accumulator`, `StepSeconds`, `MaxStepsPerAdvance`, `StepCount`, `DroppedSteps` | |
| `TimeScale`, `Paused` | `TimeScale` of 0 freezes the simulation; `Paused` ignores frames |
| `void Reset()` | Clears the accumulator and the counters |

## Subclass Sandbox (`MidManStudio.Patterns.SubclassSandbox`)
| Type | Members |
|---|---|
| `SandboxedBehavior<TContext>` | `void Run(TContext context)`, `IsRunning`, `protected abstract void Activate()`, `protected TContext Context` (only inside `Activate`) |
| `SandboxedFunction<TContext, TResult>` | `TResult Run(TContext context)`, `IsRunning`, `protected abstract TResult Evaluate()`, `protected TContext Context` |

## Facade (`MidManStudio.Patterns.Facade`)
| Member | Description |
|---|---|
| `abstract class FacadeBase : IDisposable` | Subclass registers subsystems and exposes simple methods |
| `protected void Register<T>(Func<T> creator)` | Rejects a duplicate type |
| `protected T Get<T>()` | Creates on first use. Throws for an unregistered type, a circular creation or a null creator result |
| `protected bool IsCreated<T>()` | |
| `void Dispose()`, `IsDisposed` | Disposes created `IDisposable` subsystems newest first. Every subsystem is disposed; the first exception is rethrown |

## Template Method (`MidManStudio.Patterns.TemplateMethod`)
| Member | Description |
|---|---|
| `abstract class TemplateAction<TContext>` | |
| `bool Run(TContext context)` | Not virtual. `CanRun`, `Begin`, `Execute`, `End`. Returns false when `CanRun` said no |
| `protected virtual bool CanRun`, `Begin`, `OnFailed(context, Exception)`, `End` | Optional steps. `End` runs after `Execute` succeeds or fails |
| `protected abstract void Execute(TContext context)` | |

## Bytecode (`MidManStudio.Patterns.Bytecode`)
| Type | Members |
|---|---|
| `Opcode` | 28 instructions: `Halt`, `PushConst`, `Pop`, `Dup`, `Swap`, `Add`, `Sub`, `Mul`, `Div`, `Mod`, `Neg`, `Eq`, `Ne`, `Lt`, `Le`, `Gt`, `Ge`, `Not`, `And`, `Or`, `Load`, `Store`, `Jump`, `JumpIfFalse`, `JumpIfTrue`, `Call`, `Return`, `CallHost` |
| `ProgramBuilder` | `Push(float)`, `Emit(Opcode)`, `Load`, `Store`, `CallHost`, `NewLabel()`, `Mark(label)`, `Jump`, `JumpIfFalse`, `JumpIfTrue`, `Call`, `Position`, `Build(int localCount = -1)` |
| `ProgramLabel` | `IsMarked` |
| `BytecodeProgram` | `BytecodeProgram(int[] code, float[] constants, int localCount)`, `Length`, `ConstantCount`, `LocalCount`, `CodeAt`, `ConstantAt`, `Disassemble()` |
| `VirtualMachine` | `VirtualMachine(int maxStack = 256, int maxCallDepth = 64, int maxInstructions = 100000)`, `float Run(program, float[] arguments = null)`, `RegisterHost(id, HostFunction)`, `UnregisterHost`, `Push`, `Pop`, `Peek`, `GetLocal`, `SetLocal`, `StackDepth`, `InstructionsExecuted`, `MaxInstructions`, `IsRunning` |
| `HostFunction` | `delegate void HostFunction(VirtualMachine vm)` |
| `BytecodeException` | `Error` (`BytecodeError`), `Address` |
| `BytecodeError` | `StackOverflow`, `StackUnderflow`, `CallDepthExceeded`, `BudgetExceeded`, `InvalidOpcode`, `TruncatedInstruction`, `InvalidJump`, `InvalidConstant`, `InvalidLocal`, `DivideByZero`, `UnknownHostFunction` |

