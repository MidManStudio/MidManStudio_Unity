# com.midmanstudio.patterns

## 0.1.0

- First release (not yet tagged).
- **Command**: `ICommand`, `IUndoableCommand`, `IMergeableCommand`, `INamedCommand`, `ActionCommand`, `UndoableActionCommand`, `CompositeCommand`, `CommandHistory` (undo/redo, capacity, merging, groups) and `CommandQueue`. See `docs/com.midmanstudio.patterns/command.md`.
- **State**: `IState<T>`, `IPausableState<T>`, `State<T>` and `StateMachine<T>` (change, push, pop, stop, deferred transitions, condition transitions). See `docs/com.midmanstudio.patterns/state.md`.
- **Event Queue**: `EventQueue<T>` and `EventQueueOverflow`. See `docs/com.midmanstudio.patterns/event-queue.md`.
- **Dependency Injection**: `ServiceLocator`, `Container`, `Lifetime`, `IResolver`, `ServiceNotFoundException` and `CircularDependencyException`. See `docs/com.midmanstudio.patterns/dependency-injection.md`.
- **Broker Chain**: `Broker<TQuery>`, `BrokerHandler<TQuery>`, `BrokerResult`, `BrokerPriority`, `StatQuery<TStat>`, `StatModifiers` and `StatSheet<TStat>` (priority-ordered handlers over one query, early stop, timed handlers, nested queries, stat modifiers). See `docs/com.midmanstudio.patterns/broker-chain.md`.
- **Dirty Flag**: `DirtyFlag`, `CachedValue<T>`, `IVersioned` (lazily recomputed values with dependency chains).
- **Double Buffer**: `DoubleBuffer<T>` (front and back buffers, swap with an optional reset).
- **Flyweight**: `FlyweightCache<TKey, TShared>`.
- **Prototype**: `IPrototype<T>`, `PrototypeRegistry<TKey, T>`.
- **Type Object**: `ObjectType`, `TypeCatalog`, `TypedObject` (attributes inherited from a parent type).
- **Data Locality**: `PackedList<T>`, `PackedHandle` (contiguous storage with stable handles).
- **Spatial Partition**: `SpatialGrid2D<T>`, `GridHandle` (circle and rectangle queries without allocation).
- **Decorator**: `Decorator<T>`, `DecoratorStack<T>`.
- **Factory**: `Factory<TKey, TProduct>`, `Factory<TKey, TArg, TProduct>`.
- **Game Loop**: `FixedStepLoop` (fixed-step accumulator with a step cap, interpolation factor and time scale).
- **Subclass Sandbox**: `SandboxedBehavior<TContext>`, `SandboxedFunction<TContext, TResult>`.
- **Facade**: `FacadeBase` (lazily created subsystems, reverse-order disposal).
- **Template Method**: `TemplateAction<TContext>` (fixed steps with guaranteed cleanup).
- **Bytecode**: `VirtualMachine`, `ProgramBuilder`, `BytecodeProgram`, `Opcode`, `HostFunction`, `BytecodeException` (stack machine with host functions and safety limits).
- Each has a doc part in `docs/com.midmanstudio.patterns/`.
