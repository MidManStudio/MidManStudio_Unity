# com.midmanstudio.utilities: Helpers

Small shared building blocks that do not belong to a bigger system. Seven files, in two places:

- `Runtime/Helpers/` holds `MID_BaseSO`, `MID_NamedListAttribute` (with the two interfaces `IArrayElementTitle` and
  `IArrayElementColor`), `MID_SortingLayerAttribute`, `MID_HelperFunctions`, `MID_SusValue` (with `ManagedSusValue`) and
  `SusValueManager`. Namespaces: `MidManStudio.Core` for `MID_BaseSO`, `MidManStudio.Core.EditorUtils` for the attributes
  and interfaces, `MidManStudio.Core.HelperFunctions` for the static helpers and `MidManStudio.Core.ObservableValues` for the
  observable values.
- `Runtime/Editor/DynamicDebugPanel.cs` (namespace `MidManStudio.Core.EditorTools`) is a runtime script. It sits in a folder
  called `Editor`, but that folder is inside the runtime assembly definition, so it is compiled into the runtime assembly and
  it ships in builds. Only its drawing code is Editor-only.

The Editor side of the attributes and of `MID_BaseSO` (the property drawers, the inspector and the Project window icon)
is described in [editor-helpers.md](editor-helpers.md).

Who uses what in this repo today: `ProjectileConfigSO` (projectile system) and `MID_LoggerSettings` (utilities) derive
from `MID_BaseSO`. `[MID_NamedList]` is used by the FX, pool, library, scene, UI state, netcode and projectile lists.
`[MID_SortingLayer]` is used twice in the projectile system. The projectile system calls only `LogDebug` and
`LogWarning` from `MID_HelperFunctions`. Nothing else in the repo uses `MID_SusValue`, `ManagedSusValue`,
`SusValueManager` or `DynamicDebugPanel`.

## Modules

### `MID_BaseSO.cs`
**What it does:** An abstract ScriptableObject base for MidManStudio assets. It adds an optional icon that the Project
window draws over the asset's default icon, and it implements `IArrayElementTitle`.

**API:**
- `CustomIcon`: the Texture2D set in the inspector (field `_customIcon`), or null.
- `GroupIconPath` (virtual, null by default): a project path to a Texture2D that all assets of one type share. Override it in
  a subclass.
- `ResolveIcon()`: the per-asset icon first, then the group icon loaded from `GroupIconPath`, otherwise null. The group
  icon is loaded through the AssetDatabase, so it is only found in the Editor.
- `Name` (virtual, defaults to the asset name): the label `[MID_NamedList]` lists show.

**Usage:**
- One icon per asset: select the asset and drag a Texture2D into the Icon field.
- One icon per type: `public override string GroupIconPath => "Assets/Icons/weapon_icon.png";`. The value is a project
  path, and a path that does not exist is ignored.
- Unity's own `[Icon("path")]` attribute on the class also gives a type-level icon (Unity 2021.2 and later) and needs none
  of this class.

**Gotchas:** The icon is drawn over the asset's row in the Project window. It is not set as the asset's real Unity icon
(Unity's icon setter only supports GameObjects and scripts), and it does nothing at run time. `GroupIconPath` has no fixed folder. The old header example pointed
into `Editor/Icons/` of this package, and that folder does not exist, so the example now says `Assets/Icons/`.

### `MID_NamedListAttribute.cs`
**What it does:** A `PropertyAttribute` that replaces the "Element 0", "Element 1" labels of a list with names. The file
holds three types: the attribute, `IArrayElementTitle` and `IArrayElementColor`.

**Attribute:** `[MID_NamedList]` goes on a List or array field. Unity applies it to every element.
`[MID_NamedList(elementTitleVar, useCustomColors, colorFieldName)]` takes three optional arguments (defaults: `""`, `false`,
`"color"`).

**The label comes from, in this order:**
1. `IArrayElementTitle.Name` when the element type implements it.
2. The element field named by `elementTitleVar`, when that is set.
3. Unity's own label (the field display name).

A null or blank name is shown as `Missing_<index>` for list elements. For a field outside a list it is `Missing_<n>`
with a counter that goes up on every repaint (see the section below).

**Colour:** with `useCustomColors: true`, the drawer tints each element. It takes the colour from `IArrayElementColor.ElementColor`
when the element implements it, otherwise from the Color field named by `colorFieldName`. A colour of `Color.clear` (the
value of a colour field nobody has set) means no tint. Only `NamedListDrawer` reads `IArrayElementColor`.

**Why it is in the runtime assembly:** runtime classes need the attribute type for their fields. The drawer is Editor-only.

### `MID_SortingLayerAttribute.cs`
**What it does:** A `PropertyAttribute` for a `string` field. The field is shown as a dropdown of the project's Sorting
Layers instead of a text box. The layer name is what gets stored.

**Gotchas:**
- If the stored name matches no layer (the layer was renamed or deleted), the dropdown shows the first layer in the list, and
  the stored text is not changed until someone picks a layer. The old header said it "falls back to Default". That is only
  what the dropdown shows. Nothing is written.
- Renaming a layer does not update assets that stored the old name.
- Putting it on a field that is not a string shows the normal field.

### `MID_HelperFunctions.cs`
**What it does:** `MID_HelperFunctions` is a static partial class of small helpers. `MID_HelperFunctionsWithType<T>` is a
second static class with list helpers.

**Log shims:** `LogDebug`, `LogWarning`, `LogError`, `LogException` and `LogVerbose` forward to `MID_Logger`. They pass a
fixed level each, so every call passes its own level check and is always written. They cannot be silenced with a
`MID_LogLevel`. The projectile system guards its `LogDebug` calls with its own `_enableLogs` flag for that reason. `LogVerbose` is
`[Conditional("ENABLE_VERBOSE_LOGS")]`, so the call is removed at compile time unless that symbol is defined in the assembly that
makes the call. They need a `MID_Logger`, see [logging.md](logging.md).

**GameObject and UI:**
- `DestroyObjChildren(Transform)` and `DestroyMultipleParentsChildren(List<Transform>)` use `Object.Destroy`, so the children
  are gone at the end of the frame, and `childCount` still counts them until then.
- `SetCanvasGroup(CanvasGroup, bool)` sets alpha 1, interactable and blocks raycasts, or alpha 0 and both off.
- `GetColorFromString(string)` uses Unity's `ColorUtility.TryParseHtmlString` (so `#RRGGBB`, `#RRGGBBAA` and names such as `red` work)
  and returns white when it cannot parse.

**Strings:**

| Method | Input | Output |
|---|---|---|
| `ToSentenceCase` | `HELLO. how are you?` | `Hello. How are you?` |
| `ToCamelCase` | `Hello World` | `hello world` (spaces kept) |
| `ToPascalCase` | `hello world` | `HelloWorld` |
| `ToKebabCase` | `HelloWorld` | `hello-world` |
| `ToSnakeCase` | `HelloWorld` | `hello_world` |

- `ToSentenceCase` lower-cases everything first, so acronyms and names are lost, and a decimal point counts as a sentence end:
  `a 3.5 kg bag` becomes `A 3.5 Kg bag`.
- `ToCamelCase` keeps the spaces, so it is not real camelCase.
- `ToPascalCase` uses the current culture, so the result can differ on devices set to some languages.
- `ToKebabCase` and `ToSnakeCase` split at spaces and before every capital letter that is not first in its word, so
  `XMLParser` becomes `x-m-l-parser`.
- `IsStringValid(string)` is false for null, empty, whitespace and the text `null` in any case.

**Reflection printing:** `GetStructOrClassMemberValues<T>(T)` returns a text dump of the public fields and readable public
properties, following nested objects and collections. It is meant for plain data (numbers, strings, enums, nested classes,
lists of them). It has no depth limit and no cycle check, so do not pass objects that reach Unity types or a `DateTime`, see
the Fixes section.

**Serialisation:**
- `ToJson<T>(obj, prettyPrint)` and `FromJson<T>(json)` wrap `JsonUtility`, so its limits apply. They log an error on an
  exception and return `"{}"` and `default`.
- `ToXml<T>(obj)` wraps `XmlSerializer`. On failure it logs an error and returns the text `<error>Failed to serialize</error>`,
  which is a string like any other, so check for it.
- `IsValidJson(string)` is only a shape check: trimmed text that starts with `{` and ends with `}`, or starts with `[` and ends
  with `]`. `{abc}` passes.

**`MID_HelperFunctionsWithType<T>`:** `Map`, `Filter`, `Reduce`, `GroupBy`, `AnyMatch`, `AllMatch` and `PrintValues` on a
`List<T>`. The item type is on the class, so call it as `MID_HelperFunctionsWithType<int>.Map(list, x => x * 2)`. `AllMatch`
is true for an empty list and `AnyMatch` is false. `GroupBy` throws for a null key. `PrintValues` writes with `Debug.Log`, not
through `MID_Logger`.

### `MID_SusValue.cs`
**What it does:** `MID_SusValue<T>` is a value container that tells subscribers when it changes. The file also holds the
interface `IManagedSusValue` and the class `ManagedSusValue<T>`.

**Setting the value:**
1. If a validation function is set and it returns false, the set is dropped and a warning is logged.
2. `OnAnyUpdate` subscribers run with the new value. This happens before the value is stored, so inside the callback `.Value`
   is still the old value.
3. If the new value differs from the stored one (`EqualityComparer<T>.Default`, null-safe), it is stored and `OnValueChanged`
   subscribers run with `(old, new)`.

**API:**
- `Value`, `IsValueNull`, and the implicit conversion to `T` (a null container converts to `default`).
- `SubscribeToValueChanged` / `UnsubscribeFromValueChanged` / `IsSubscribedToValueChanged` and the same three for
  `AnyUpdate`. Subscribers are in HashSets, so adding the same delegate twice returns false and does nothing.
- `SetValidationFunction`, `ClearValidationFunction`.
- `SetValueSilently(T)` stores the value without any callback. Validation still runs.
- `ForceNotify()` fires only `OnValueChanged`, with the current value as both old and new.
- `ClearAllSubscriptions()`, `GetSubscriberCount()` (both kinds added together).

**`ManagedSusValue<T>`:** the same container, registered with `SusValueManager` when it is constructed.
`new ManagedSusValue<float>(0f, "MyFloat", gameObject)`. The ID is a new GUID when you pass none. When you pass an owner
GameObject, the value's subscriptions are cleared when that GameObject is destroyed. In Edit Mode `SusValueManager.Instance`
is null, so the value is created but not managed (`IsManaged` is false).

**Gotchas:**
- Do not subscribe or unsubscribe from inside a callback. The notify loops enumerate the HashSets directly, so this is not
  safe, see the Fixes section.
- Construct a `ManagedSusValue` in `Awake` or `Start`, not in a field initialiser or a constructor. Registering looks up the
  manager with Unity calls, and it can create a GameObject, and Unity restricts those in constructors and initialisers.
- `IsValueNull` and the equality check use plain C# null checks, so a destroyed Unity object held in the value is not seen as
  null.
- IDs must be unique among live values, see the Fixes section.

### `SusValueManager.cs`
**What it does:** A `Singleton<SusValueManager>` that keeps every registered `ManagedSusValue` by ID and by owner. It is created
on first use in Play Mode. It is not persistent across scenes unless you make it so (see [singleton.md](singleton.md)). The file
also holds the hidden component `SusValueOwnerWatcher`, which is added to an owner GameObject and calls the manager from
`OnDestroy`.

**API:**
- `RegisterValue(IManagedSusValue, GameObject owner = null)`: called by the `ManagedSusValue` constructor. A duplicate ID is
  skipped with a warning.
- `UnregisterValue(string id)`: clears the value's subscriptions and removes it. Unknown IDs are ignored.
- `ClearAllForOwner(GameObject)`: clears the subscriptions of the owner's values. The values stay registered.
- `ClearAll()`: clears the subscriptions of every value, then forgets all values and owners.
- `RegisteredCount`, `IsRegistered(string id)`.
- Field `_logLevel` (default `Info`) sets how much the manager logs.

### `DynamicDebugPanel.cs` (Runtime/Editor)
**What it does:** An on-screen panel with a Stats view and a Logs view. Add it to one GameObject. A second copy destroys itself.
`Instance` is a plain static, not a `Singleton<T>`, so nothing creates the panel for you and `Instance` can be null.

**Drawing:** IMGUI, and only inside the Unity Editor (the `OnGUI` method and all drawing code are under `#if UNITY_EDITOR`). In a
build nothing is drawn. The component still runs `Update` there, every frame, even when the panel is closed (see the Fixes
section).

**Inspector fields:** `showPanel` (start expanded or closed), `panelRect`, `minimizedRect`, `allowDragging`, `autoScroll`,
`maxLogEntries` (default 100). The panel has three states: expanded, minimized and closed. The header can be dragged.

**Built-in System section:** FPS, FPS (Avg) over the last 60 frames, FPS (Min), FPS (Max), Frame Time in ms and Memory in MB
(`Profiler.GetTotalAllocatedMemoryLong`). The context menu item "Reset FPS Stats" clears the FPS numbers.

**API:**
- `AddSection(name, color)`: does nothing when the section exists.
- `AddValue(section, name, value, type, min, max, onChanged, format)`: adds or replaces a row and creates the section when
  needed. `format` is a number format string, default `F2`.
- `UpdateValue(section, name, value)`, `RemoveValue(section, name)`.
- `AddLog(message)` adds a time-stamped line. `ClearLogs()`.
- `ClearAllStats()` removes all sections, including yours, and re-creates the System section.
- `TogglePanel()` goes closed to expanded, expanded to minimized and minimized to expanded. It never closes an open panel.
  `SetPanelState(true)` expands it and `SetPanelState(false)` closes it.

**Value types:**

| Type | Drawn as | Notes |
|---|---|---|
| `Display` | text | numbers use `format`, other values use `ToString()` |
| `Slider` | slider between `min` and `max` | `onChanged(newValue)` is called while dragging |
| `Toggle` | checkbox | changes the stored value only. There is no callback, and the panel has no way to read the value back |
| `Button` | button | `onChanged(0)` is called on click |
| `ProgressBar` | bar | expects 0 to 1. It is not clamped, a value over 1 draws past the bar |

## Fixes and Problems

### `MID_BaseSO.cs`
- Fixed in the comments: the header said the icon is applied "on import/change via MID_BaseSOEditor", and `ResolveIcon` said it
  is "called by the custom editor to apply the icon". Neither is true. The editor shows a preview, and the Project window icon
  is drawn by `MID_BaseSOProjectIconDrawer`, see [editor-helpers.md](editor-helpers.md). The summary now says what is drawn.
- Fixed in the comments: the `GroupIconPath` example pointed into `Editor/Icons/` of this package, which does not exist.
- Fixed in the docs: the header example overrode `GroupIconPath` as `protected override`. The base member is `public virtual`, and C#
  does not allow an override to change the access, so that example would not compile. The example here says `public override`.
- Moved out of the file header: the three usage notes. They are in the class summary and the section above.

### `MID_NamedListAttribute.cs`
- Moved out of the file header: the note that the attribute is in the runtime assembly so both runtime classes and Editor
  drawers can use it.
- Added summaries to the public members, which had none.

### `MID_SortingLayerAttribute.cs`
- Fixed in the comments: "falls back to Default" now says what the drawer does (shows the first layer and keeps the stored
  text).
- Moved out of the file header: the same runtime assembly note as above.

### `MID_HelperFunctions.cs`
- Fixed in the comments: the shim comment said routing through `MID_Logger` makes log level control work uniformly. It does not.
  Each shim passes a fixed level, so none of them can be silenced by a level.
- Fixed in the comments: `IsValidJson` said it "uses a lightweight manual pass". It only checks the first and last characters.
- Added summaries to the public members, which had none.
- Not fixed: `GetStructOrClassMemberValues` has no depth limit and no cycle check. It follows every public property that returns
  an object, and many Unity types have a property that returns the same type: `Vector3.normalized`, `Vector2.normalized`,
  `Quaternion.normalized`, `Color.linear`, `Color.gamma`, `DateTime.Date`, and any component reaches `gameObject`, which
  reaches `transform`, and so on. Passing an object that holds one of these should recurse until the stack overflows, and a
  stack overflow cannot be caught. Plain data classes are fine. Found by reading the code, not run in Unity.
- Not fixed: `ToCamelCase` keeps the spaces and `ToPascalCase` uses the current culture. Both are described in the sections above.
  Whether to change them is the owner's decision, because callers may rely on today's output.

### `MID_SusValue.cs`
- Fixed in the comments: raw `<T>` and `<float>` inside XML doc comments is malformed XML. It is now written as `&lt;T&gt;`.
- Fixed in the comments: the setter comment said `AnyUpdate` fires before storing "so listeners see old value too if needed". It now
  says what happens: the new value is the argument and `.Value` is still the old value.
- Moved out of the file header: the subscription types, duplicate prevention, validation and conversion notes. They are in the
  class summary and the section above.
- Not fixed: the notify loops (`foreach` over the HashSets) run callbacks while enumerating. If a callback adds a subscriber, that
  changes the set during enumeration and is expected to throw `InvalidOperationException`, and removing one may too, depending on
  the runtime. The `try`/`catch` is inside the loop, around the callback only, so the exception would come out of the `Value`
  setter. Found by reading the code, not run in Unity.
- Not fixed: the `ManagedSusValue` finalizer is effectively dead code, and it can do harm. While a value is registered, the
  manager's dictionary holds it, so the finalizer cannot run. It can only run after `ClearAll` or `UnregisterValue` removed the
  value, and then `_isManaged` is still true, so it calls `UnregisterValue(id)` again from the finalizer thread. That is a
  dictionary access off the main thread. And if another value reused the same ID in the meantime, the finalizer removes that
  other value and clears its subscriptions. A sequence that gets there: value A with ID `Score`, `ClearAll()`, value B with ID
  `Score`, then the garbage collector finalizes A. Found by reading the code, not run.
- Not fixed: when the manager skips a duplicate ID, `_isManaged` is still set to true, so `IsManaged` is wrong for that value, and
  the value is not tracked. A prefab that creates `new ManagedSusValue<int>(0, "Health", gameObject)` and is spawned twice gets
  the second one skipped. The default GUID ID avoids this.

### `SusValueManager.cs`
- Fixed in the comments: raw `<float>` in the class summary (malformed XML), and the usage notes now say that the manager exists
  only in Play Mode.
- Not fixed: `ClearAllForOwnerId` (called when an owner is destroyed) clears the subscriptions but does not remove the values from
  the dictionary. Registered values of destroyed owners stay in memory until `ClearAll`, and their IDs stay taken, so a new value
  with the same ID is skipped with a warning. The same applies to `ClearAllForOwner`.
- Not fixed: no part of the class is thread-safe, and `RegisterValue` adds a component to the owner, so all of it must run on the
  main thread.

### `DynamicDebugPanel.cs`
- Added summaries to the public members, which had none, and a class summary that says the drawing is Editor-only.
- Not fixed: `Update` runs `UpdateSystemStats` every frame, also in builds where nothing is drawn, and also when the panel is closed.
  Each frame it boxes six numbers into `object` (`UpdateValue` takes `object`), so it allocates garbage every frame. A build
  that does not need the panel should not contain it.
- Not fixed: `ClearAllStats` removes the sections you added, not just the System section. The "Clear Stats" button calls it.
- Not fixed: the drawing code changes the dictionaries it is looping over. Dragging a slider or ticking a toggle calls
  `UpdateValue` inside the loop over the section's values, and collapsing a section writes to the section dictionary inside the
  loop over all sections. On runtimes where writing to an existing key invalidates the enumerator, the next step of the loop throws
  `InvalidOperationException`. Whether the Unity Mono runtime does this was not checked. Found by reading the code, not run.
- Not fixed: a toggle value cannot be read back or observed. `DebugValue.onSliderChanged` is only called for sliders and buttons, and
  the class has no getter.
- Not fixed: `1f / Time.unscaledDeltaTime` is infinite when the delta time is 0, and that value then stays in the average and in
  FPS (Max) until "Reset FPS Stats". The average also starts low, because the 60 slot buffer starts at zero.
- Not fixed: the progress bar is not clamped.
