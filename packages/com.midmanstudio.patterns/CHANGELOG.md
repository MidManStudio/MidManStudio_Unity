# com.midmanstudio.patterns

## 0.1.0

- First release (not yet tagged).
- **Command**: `ICommand`, `IUndoableCommand`, `IMergeableCommand`, `INamedCommand`, `ActionCommand`, `UndoableActionCommand`, `CompositeCommand`, `CommandHistory` (undo/redo, capacity, merging, groups) and `CommandQueue`. See `docs/com.midmanstudio.patterns/command.md`.
- **State**: `IState<T>`, `IPausableState<T>`, `State<T>` and `StateMachine<T>` (change, push, pop, stop, deferred transitions, condition transitions). See `docs/com.midmanstudio.patterns/state.md`.
- **Event Queue**: `EventQueue<T>` and `EventQueueOverflow`. See `docs/com.midmanstudio.patterns/event-queue.md`.
- **Dependency Injection**: `ServiceLocator`, `Container`, `Lifetime`, `IResolver`, `ServiceNotFoundException` and `CircularDependencyException`. See `docs/com.midmanstudio.patterns/dependency-injection.md`.
- **Broker Chain**: `Broker<TQuery>`, `BrokerHandler<TQuery>`, `BrokerResult`, `BrokerPriority`, `StatQuery<TStat>`, `StatModifiers` and `StatSheet<TStat>` (priority-ordered handlers over one query, early stop, timed handlers, nested queries, stat modifiers). See `docs/com.midmanstudio.patterns/broker-chain.md`.
