# com.midmanstudio.utilities: Singleton

## Modules

### `Singleton.cs`
**What it does:** Base class for a MonoBehaviour singleton
(`Singleton<T>`). `Instance` finds or lazily creates the instance in the
scene; `HasInstance`/`TryGetInstance`/`GetExistingInstance` check or fetch
it without creating one. Optional persistence across scene loads
(`InitializeSingleton(persistAcrossScenes: true)`), with an `OnSceneChanged`
event and a `SingletonLifecycle.OnSceneChange` callback for subclasses that
need to react.

**Decisions:**
- `Instance` auto-creates a new GameObject and instance if none exists in
  the scene (play mode only). This is intentional, but surprises people who
  expect a singleton accessor to only ever return an existing instance;
  `HasInstance` and `GetExistingInstance` exist specifically for callers
  that want to check without triggering that side effect.
- `CurrentInstance` returns the raw backing field, which can be a reference
  to an instance Unity has since destroyed. `TryGetInstance` runs Unity's
  destroyed-object check first. Kept both since call sites already depend
  on the distinction; documented on each property instead of removing one.
- `IsAvailable()` is functionally identical to `HasInstance`, just wrapped
  in a try/catch. Documented as equivalent rather than merged, since it is
  called from enough places that removing it isn't worth the churn here.

### `StaticContentSingleton.cs`
**What it does:** Thread-safe lazy singleton for plain C# classes that
don't need a GameObject (registries, caches, service locators). Same
`Instance`/`HasInstance`/`TryGetInstance`/`Reset` shape as `Singleton<T>`,
plus `Initialize(instance)` to inject a specific instance (subclass, mock)
instead of relying on `new T()`. If `T` implements `IStaticSingletonInitializable`,
`Initialize()` on the instance runs once, the first time it's created.
If `T` implements `IDisposable`, `Reset()` disposes it automatically.

**Decisions:**
- Double-checked locking on `Instance` rather than a plain lock on every
  access, since this is read far more often than it's created.
- Was already close to fully documented before this pass; only the two
  properties without their own `<summary>` (`HasInstance`, `IsInitialized`)
  and the `IStaticSingletonInitializable` interface members needed one
  added. The original top-of-file usage comment (constructor pattern,
  `Initialize()` for injection, disposable cleanup) was folded into the
  class's `<summary>`/`<example>` instead of sitting above the `using`
  statements, so it shows up in IDE tooltips.

## Fixes and Problems

### `Singleton.cs`
- Removed a stray casual comment ("ok this why we get instance created when
  not found shiiiiiiii") sitting next to the already-clear "Create a new
  instance if none was found" comment above the auto-create block. No
  behavior change.
