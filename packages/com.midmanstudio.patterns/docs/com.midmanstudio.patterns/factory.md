# com.midmanstudio.patterns: Factory

## Modules

### `Factory.cs`
**What it does:** Creates objects by key. Each key maps to a creator function, so callers ask for "orc" or
"fireball" without knowing the concrete class, and a new kind is added by registering a creator. `Register`
adds one and rejects a duplicate key, `Set` replaces, `Create` runs the creator, `TryCreate` returns false for an
unknown key, and `Unregister`, `Contains`, `Count` and `Keys` manage the registrations.

**Decisions:**
- Every `Create` runs the creator again. For shared instances use the DI container or a flyweight.
- A creator that returns null throws `InvalidOperationException`. A missing product is a bug at the
  registration, not a normal result.
- A key comparer can be passed. Not thread-safe.

### `FactoryWithArg.cs`
**What it does:** `Factory<TKey, TArg, TProduct>` is the same type with creators that take an argument, for
example the position, owner or settings of the object being created.

### `FactoryTests.cs`
**What it does:** 10 edit-mode NUnit tests covering both classes: fresh instances, duplicate keys and `Set`,
unknown keys, null products, the key bookkeeping, value-type products, the key comparer, null creators, and the
argument being passed through.

**Decisions:**
- Two deliberate breakages were tried. Accepting a null product was caught. Removing the duplicate check was
  not detected, because `Dictionary.Add` still throws `ArgumentException`; only the message differs.
  The tests were run outside Unity against a small NUnit stand-in and have not been run in the Unity Test Runner.

## Fixes and Problems

None so far. No bug has been found in a released version.
