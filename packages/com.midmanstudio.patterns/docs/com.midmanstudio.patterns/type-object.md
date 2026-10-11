# com.midmanstudio.patterns: Type Object

## Modules

### `ObjectType.cs`
**What it does:** A data-defined "class": a name, an optional parent type and named attributes. `Set` stores an
attribute on this type. `Get<T>`, `TryGet<T>` and `Get<T>(name, fallback)` read an attribute, looking at this
type first and then up the parent chain, so a child type only states what differs from its parent. `Has`,
`HasOwn`, `Remove`, `IsA` and `AttributeNames` complete the type.

**Decisions:**
- The parent is fixed at construction, so a chain can never form a loop and no cycle check is needed.
- Values are stored as objects. Reading with a type that is not exactly the stored type throws
  `InvalidCastException`, for example reading an `int` attribute as `float`. A silent conversion would hide
  a data mistake. Type objects hold definition data, so the boxing cost is acceptable.
- `AttributeNames` lists each visible name once, own names first.

### `TypeCatalog.cs`
**What it does:** Registry of types by name. `Define(name, parentName)` creates and registers a type; the parent
must already exist, so types are defined parent first. `Get`, `TryGet`, `Contains`, `Count` and `Names` look
types up. An unknown parent or a duplicate name throws `ArgumentException`.

### `TypedObject.cs`
**What it does:** An instance of an `ObjectType`. Reads use the instance's own values first and fall back to the
type and its ancestors. `Set` stores an instance value, `Remove` drops it so the type's value shows through.

**Decisions:**
- Instances only keep what they changed, so a thousand enemies of one type share the type's data.

### `TypeObjectTests.cs`
**What it does:** 18 edit-mode NUnit tests: own and inherited attributes, overriding and removing,
several inheritance levels, `Has` against `HasOwn`, missing attributes, wrong types, null values, `IsA`,
attribute names, name validation, the catalog (parent first, duplicates, unknown names) and typed objects
(fallback, overrides, independence, errors).

**Decisions:**
- Four deliberate breakages were tried. Three were caught (parents not consulted, a wrong value returned,
  instance overrides ignored). The fourth, removing the duplicate type check in the catalog, was not detected
  because `Dictionary.Add` still throws `ArgumentException`; only the message differs.
  The tests were run outside Unity against a small NUnit stand-in and have not been run in the Unity Test Runner.

## Fixes and Problems

None so far. No bug has been found in a released version.
