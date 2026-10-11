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
| State | MidManStudio.Patterns.State | Stack-based state machine with deferred transitions, pausable states, condition-based transitions |
| Event Queue | MidManStudio.Patterns.EventQueue | Typed queue that separates posting an event from delivering it, bounded or unbounded |
| Dependency Injection | MidManStudio.Patterns.DependencyInjection | Service locator and a factory-based container with singleton, scoped and transient lifetimes |
| Broker Chain | MidManStudio.Patterns.BrokerChain | Central broker that runs registered handlers in priority order over one query, with timed handlers and a stat sheet for buffs and modifiers |

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

```csharp
using MidManStudio.Patterns.State;

var machine = new StateMachine<Enemy>(enemy);
machine.ChangeState(patrol);
machine.AddTransition(patrol, chase, e => e.CanSeePlayer);
// in your update loop:
machine.Update(Time.deltaTime);
```

```csharp
using MidManStudio.Patterns.DependencyInjection;

var container = new Container();
container.Register<IClock>(r => new SystemClock());
container.Register<IShop>(r => new Shop(r.Resolve<IClock>()));
var shop = container.Resolve<IShop>();
```

```csharp
using MidManStudio.Patterns.BrokerChain;

enum Stat { Attack, Speed }

var stats = new StatSheet<Stat>();
stats.SetBase(Stat.Attack, 10f);

var buff = stats.AddModifier(Stat.Attack, 5f, lifetimeSeconds: 8f);   // +5 for 8 seconds
stats.MultiplyModifier(Stat.Attack, 2f);                              // runs after flat bonuses
float attack = stats.Get(Stat.Attack);                                // (10 + 5) * 2 = 30
// in your update loop:
stats.Tick(Time.deltaTime);
```

See `APICATALOG.md` for the full API and `docs/com.midmanstudio.patterns/` for the design notes of each pattern.
