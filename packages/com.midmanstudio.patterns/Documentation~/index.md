# MidMan Studio Patterns

Generic design pattern building blocks for Unity 2022.3+. No dependencies.

| Pattern | Purpose |
|---|---|
| Command | Undo/redo history with grouping and merging, composite commands, deferred command queue |
| State | Stack-based state machine with deferred transitions and condition-based transitions |
| Event Queue | Typed queue that separates posting an event from delivering it |
| Dependency Injection | Service locator and a factory-based container |
| Broker Chain | Central broker running priority-ordered handlers over a query, with a stat sheet for buffs and modifiers |
| Dirty Flag | Dirty flag and lazily recomputed cached values with dependency chains |
| Double Buffer | Two buffers swapped each step so readers see one consistent snapshot |
| Flyweight | Cache that shares one immutable instance per key |
| Prototype | Registry of template objects that creates new ones by cloning |
| Type Object | Data-defined types with inherited attributes, a catalog and instances |
| Data Locality | Items packed in one array with stable handles that survive removal |
| Spatial Partition | Uniform 2D grid for circle and rectangle queries |
| Decorator | Base class and a runtime-editable decorator stack |
| Factory | Creates objects by key from registered creators |
| Game Loop | Fixed-step loop with a step cap, interpolation factor and time scale |

See the package `README.md` and `APICATALOG.md`.
