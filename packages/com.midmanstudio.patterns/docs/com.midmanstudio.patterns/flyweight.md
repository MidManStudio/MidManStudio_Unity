# com.midmanstudio.patterns: Flyweight

## Modules

### `FlyweightCache.cs`
**What it does:** Shares one immutable instance per key. `Get(key)` returns the cached instance and creates it
with the factory the first time. `TryGet`, `Contains`, `Remove` and `Clear` manage the cache. `Count` is the
number of cached instances and `CreatedCount` is how often the factory ran, including instances removed since.

**How it works:**
- A factory that throws or returns null caches nothing. The next `Get` runs the factory again.
- A factory may ask for other keys. Asking for its own key would recurse forever, so it throws
  `InvalidOperationException`; the cache stays usable.

**Decisions:**
- The cached objects must not be changed by their users. The cache does not enforce that.
- `Remove` forgets the instance but users that already hold it keep it. The next `Get` makes a new one, so
  after a `Remove` two different instances can exist for one key.
- A key comparer can be passed, for example `StringComparer.OrdinalIgnoreCase`.
- Not thread-safe.

### `FlyweightTests.cs`
**What it does:** 11 edit-mode NUnit tests: same key same instance, different keys, `TryGet` and `Contains`
not creating, `Remove` and re-creation, null and throwing factories, nested keys, a factory asking for its own
key, `Clear`, the key comparer and a null factory.

**Decisions:**
- Three deliberate breakages were tried (instances not cached, `CreatedCount` not counted, null product
  accepted). All were caught. The tests were run outside Unity against a small NUnit stand-in and have not
  been run in the Unity Test Runner.

## Fixes and Problems

None so far. No bug has been found in a released version.
