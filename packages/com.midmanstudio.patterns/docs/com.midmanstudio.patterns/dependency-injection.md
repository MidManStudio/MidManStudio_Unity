# com.midmanstudio.patterns: Dependency Injection

## Modules

### `Lifetime.cs, IResolver.cs`
**What it does:** `Lifetime` is `Singleton`, `Scoped` or `Transient`. `IResolver` is the read side of the
container: `Resolve<T>`, `TryResolve<T>`, and the `Type` based `Resolve` and `TryResolve`. A factory
receives an `IResolver` and uses it to ask for the services it needs.

**Decisions:**
- Factories receive `IResolver`, not the `Container`, so a factory can ask for services but cannot
  register or dispose anything.
- The generic methods are constrained to `class`. Services are interfaces or classes, and the constraint
  keeps value types from being boxed through the `object` based internals.

**Tests:** `Tests/Editor/DependencyInjectionTests.cs`.

### `ServiceExceptions.cs`
**What it does:** `ServiceNotFoundException` carries the `ServiceType` that was requested.
`CircularDependencyException` carries the `Chain` of services in resolution order, ending with the one
that closed the loop, and prints it as `A -> B -> A`. Both derive from `InvalidOperationException`.

**Tests:** `Tests/Editor/DependencyInjectionTests.cs`.

### `ServiceLocator.cs`
**What it does:** A registry of ready-made instances looked up by type. `Register`, `Unregister`, `Has`,
`TryGet`, `Get`, `Clear`. A locator can be given a parent and falls back to it when its own table has no
entry, so a scene locator can sit over an application one.

**Decisions:**
- It is an instance, not a static class. A static locator is hidden global state, and with Unity's
  domain reload disabled it also survives between play sessions. Code that wants one global locator can
  hold a static field of its own and own that choice.
- The key is the type given at registration (`Register<IClock>(x)` is found by `Get<IClock>`), not the
  runtime type of the instance, so a service is always asked for by the contract it was registered under.
- Registering a type again replaces the entry on that locator. That is what lets a test swap in a
  double. `Unregister` and `Clear` only touch the locator they are called on, never its parent.
- The locator creates nothing and disposes nothing. Lifetime is the owner's business; use the container
  when the pattern needs creation and disposal.

**Tests:** `Tests/Editor/DependencyInjectionTests.cs`.

### `Container.cs`
**What it does:** A small dependency injection container. `Register` takes a factory and a `Lifetime`,
`RegisterInstance` takes an existing object, `Resolve` and `TryResolve` build services on demand.
`CreateScope` makes a child container that resolves everything its parent can, may add registrations that
shadow the parent's, and keeps its own Scoped instances. `Dispose` disposes what the container created.

**How it works:**
- A singleton's instance lives in the container that holds its registration, so the root and every scope
  share one. A scoped instance lives in the scope that resolved it. A transient is created on every
  resolve and kept nowhere.
- A singleton's factory runs against the container that owns it, not the scope that asked. A singleton
  that depends on a scoped service therefore resolves it from the root, where it throws, instead of
  silently holding on to one scope's instance.
- A transient's factory runs against the container that asked. A transient registered at the root can
  therefore depend on scoped services when it is resolved from a scope.
- One resolution chain is shared by a container and all its scopes. A service already on the chain when
  it is requested again is a circular dependency. The entry is removed in a `finally`, so a failed
  resolve leaves the chain clean.

**Decisions:**
- Factories instead of constructor reflection. Reflection based injection breaks under IL2CPP code
  stripping unless every constructor is preserved, and it hides the construction code. A factory is a
  plain lambda, is checked by the compiler, and has no stripping risk. The cost is writing the `new`
  call out, which is also what shows what a service depends on.
- Scoped from the root throws. The root has no scope to hold the instance, and treating it as one would
  turn a scoped service into a singleton by accident.
- A registration can be replaced only until it has been resolved. After that, code somewhere already
  holds an instance from the old registration, and swapping the registration would leave two answers to
  the same question. The check covers transient and scoped services as well, which are not cached on the
  registering container and so need the registration's own "resolved" mark. A scope that already
  resolved a type from its parent also cannot register that type locally.
- The container disposes only what it owns: instances its own factories created (singleton and scoped),
  and instances registered with `ownsInstance: true`, newest first. Transients are not tracked, because
  tracking every one would grow without bound for the container's lifetime. The caller owns them.
- `Dispose` attempts every disposal and throws one `AggregateException` afterwards, so one failure does
  not leave later services undisposed. Disposing a container does not dispose its scopes.
- A factory that returns null, or an object that is not an instance of the registered type, throws with
  the service named, at the resolve that caused it.
- Not thread-safe. Use it from one thread.

**Tests:** `Tests/Editor/DependencyInjectionTests.cs`.

### `DependencyInjectionTests.cs`
**What it does:** Edit-mode NUnit tests for the locator and the container (37 tests): locator register,
replace, parent fallback, shadowing, unregister and clear; container lifetimes, dependencies between
factories, instance registration, scopes, the scoped-from-root and captive dependency rules, shadowing,
re-registration rules, circular dependencies and recovery after one, failing and null factories, and
disposal order, ownership, scopes, failures, and use after disposal.

**Decisions:**
- The tests were run outside Unity against a small NUnit stand-in. Eight deliberate breakages were
  checked. Seven were caught at once. One was missed: allowing re-registration after a resolve still
  passed, because the cached-instance check caught singletons. A test for transient and scoped services
  was added and the breakage is now caught. They have not been run inside the Unity Test Runner yet.

## Fixes and Problems

- The first version of the re-registration tests only covered singletons, so removing the "already
  resolved" check on a registration went unnoticed; singletons were still rejected by the separate
  cached-instance check. Found by deliberately breaking the check. Added
  `RegisteringAgain_AfterATransientOrScopedServiceWasResolved_Throws`. No production code changed.
