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
| Dirty Flag | MidManStudio.Patterns.DirtyFlag | Dirty flag and lazily recomputed cached values with dependency chains |
| Double Buffer | MidManStudio.Patterns.DoubleBuffer | Two buffers swapped each step so readers see one consistent snapshot |
| Flyweight | MidManStudio.Patterns.Flyweight | Cache that shares one immutable instance per key |
| Prototype | MidManStudio.Patterns.Prototype | Registry of template objects that creates new ones by cloning |
| Type Object | MidManStudio.Patterns.TypeObject | Data-defined types with inherited attributes, a catalog and instances |
| Data Locality | MidManStudio.Patterns.DataLocality | Items packed in one array with stable handles that survive removal |
| Spatial Partition | MidManStudio.Patterns.SpatialPartition | Uniform 2D grid for circle and rectangle queries |
| Decorator | MidManStudio.Patterns.Decorator | Base class and a runtime-editable decorator stack |
| Factory | MidManStudio.Patterns.Factory | Creates objects by key from registered creators |
| Game Loop | MidManStudio.Patterns.GameLoop | Fixed-step loop with a step cap, interpolation factor and time scale |
| Subclass Sandbox | MidManStudio.Patterns.SubclassSandbox | Base classes for behaviours that act only through a context that exists while they run |
| Facade | MidManStudio.Patterns.Facade | Base class that hides lazily created subsystems and disposes them in reverse order |
| Template Method | MidManStudio.Patterns.TemplateMethod | Fixed sequence of steps with guaranteed cleanup, filled in by subclasses |
| Bytecode | MidManStudio.Patterns.Bytecode | Small stack virtual machine with a program builder, host functions and safety limits |

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

```csharp
using MidManStudio.Patterns.GameLoop;
using MidManStudio.Patterns.DataLocality;

var loop = new FixedStepLoop(1f / 60f, dt => Simulate(dt));
// in your update loop:
loop.Advance(Time.deltaTime);
float blend = loop.Alpha;                    // draw between the previous and current state

var enemies = new PackedList<Enemy>();
PackedHandle h = enemies.Add(new Enemy());   // the handle survives other removals
enemies.Remove(h);
```

```csharp
using MidManStudio.Patterns.Bytecode;

var program = new ProgramBuilder().Push(2).Push(3).Emit(Opcode.Add).Build();
var vm = new VirtualMachine();
vm.RegisterHost(1, m => Heal(m.Pop()));            // the only door into the game
float five = vm.Run(program);
```

See `APICATALOG.md` for the full API and `docs/com.midmanstudio.patterns/` for the design notes of each pattern.
