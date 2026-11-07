# com.midmanstudio.utilities: UI State

The UI state system drives screens and panels from one state value. The state is a `[Flags]` enum that is generated from a
list of names, and it is stored as a plain int. The runtime part is in `Runtime/UIState/` (namespace
`MidManStudio.Core.UIState`). The inspectors and the generator are in `Editor/UIState/` (namespace
`MidManStudio.Core.EditorUtils.UIState`). Three files in `Runtime/UIState/Generated/` are written by the generator and
are not documented one by one: `MenuUIState.cs`, `LobbyUIState.cs` and `ProjLobbyUIState.cs`. Each is a `[Flags]` enum
with `None = 0` and one bit per state. `ProjLobbyUIState` (`Main`, `Searching`, `InLobby`, `Loading`, `NetworkCheck`) is
used by the projectile system test lobby UI. The netcode `LocalLobbyUIManager` works on the same kind of context with raw ints and names this enum in its comments. The other two enums are examples.

The pieces fit together like this: a `MID_UIStateContext` asset holds the state names, the current state and the back
history. A `MID_UIStateManager` applies the state to panel lists. `MID_UIStateVisibility`, `MID_UIStateButton` and
`MID_UIStateBackButton` are small components that read the context or change it. `MID_UIElement` is the show and hide
base they work on.

## Modules

### `MID_UIStateContext.cs`
**What it does:** A ScriptableObject (Create > MidManStudio > Utilities > UI State Context) that is the definition and the
state machine at once. Fields: `contextName` (default `Menu`, PascalCase, used for the enum name), `packageId` (default
`com.mygame`, used for the duplicate warning and the generated file header) and `states` (a list of `UIStateEntry` with `enumName` and an
optional `comment`). The file also holds the serializable class `UIStateEntry`.

**Run time API:**
- `CurrentState` (raw int, 0 is None), `CanGoBack`, `HistoryDepth`, `StateCount`.
- `ChangeState(int)` does nothing when the state is the same. Otherwise it pushes the old state to the history (unless the
  old state is 0 or the change is a back step), sets the new one and raises `OnStateChanged(int)`.
- `GoBack()` pops the history and changes to that state without pushing. It logs a warning when the history is empty.
- `ClearHistory()`, `IsInState(int)` (exact match) and `HasFlag(int)` (any bit).
- `enumTypeName` and `contextDisplayName` are computed from `contextName`. They are properties, not fields to set.

**How to use it:**
1. Create the asset, set `contextName`, add states.
2. Run MidManStudio > Utilities > UI State Context Generator. It writes `{contextName}UIState.cs`.
3. Assign the asset to the manager and to the components below. Cast the generated enum to int to change state:
```csharp
context.ChangeState((int)MenuUIState.Settings);
bool inSettings = context.HasFlag((int)MenuUIState.Settings);
```

**Decisions:**
- States are bit flags, so one value can stand for "Settings and Shop" and a panel can list several states in a mask.
- The state is stored as an int, not as the enum, so that one component class works for every generated enum. The
  inspectors find the enum by name and draw it.
- The state and the history live in the asset. Any number of components share them without a reference to each other.

**Gotchas:**
- `OnEnable` resets the state, the history and all `OnStateChanged` subscribers. With Enter Play Mode set to not reload the
  domain, `OnEnable` does not run again, so the state and history of the last session stay. We expect this from the code,
  not run in Unity.
- The asset keeps its state across scene loads in a play session. A new scene starts from the old state.
- State 0 is never put in the history, so you cannot go back to None.

### `MID_UIElement.cs`
**What it does:** The base panel class (`[RequireComponent(typeof(CanvasGroup))]`). `Show()` sets alpha 1, interactable and
blocksRaycasts true. `Hide()` sets alpha 0 and both flags false. `Hide(float targetAlpha)` uses another alpha, and
`Show(bool propagateToChildren)` can skip the child notification. `Toggle()` flips between them. `IsShowing` reports the
last call. The GameObject stays active the whole time. The `onVisibilityChanged` UnityEvent (`UnityEvent<bool>`) is raised on
the element and on every `MID_UIElement` below it. Those descendants are collected once in `Start`, and only their event is
raised, not their own `Show` or `Hide`.

**Gotchas:**
- `IsShowing` starts false, even when the panel is visible in the scene, so the first `Toggle()` calls `Show()`.
- A subclass that overrides `Start` must call `base.Start()`, otherwise no descendant is notified.
- Descendants added after `Start` are not notified.
- `Hide` always notifies descendants. Only `Show` has the switch.
- The log level defaults to `None`.

### `MID_UIStateVisibility.cs`
**What it does:** Shows or hides the `MID_UIElement` on its GameObject (`[RequireComponent(typeof(MID_UIElement))]`). On every
state change it computes `shouldShow = mask != 0 && (state & mask) != 0`, and calls `Show()` or `Hide()` when the result
changed. On enable it subscribes and applies the current state at once. The first call after enable is always applied, even
when it equals the stored value.

**Decisions:** A mask of 0 means always hidden. The tooltip says so and calls it rarely useful.

**Gotchas:** The back button treats mask 0 the other way (always visible). See `MID_UIStateBackButton.cs` below and the Fixes list.

### `MID_UIStateButton.cs`
**What it does:** Needs a `Button`. On click it calls `context.ChangeState(targetStateMask)`. With Disable When Active on (the
default) the button is not interactable while the context state equals the target exactly. It subscribes in `OnEnable` and
applies the current state at once.

**Gotchas:** The compare is exact. When the context holds several flags at once, the button never disables.

### `MID_UIStateBackButton.cs`
**What it does:** Needs a `Button` and a `MID_UIElement`. On click it calls `GoBack()` when the history is not empty, and does
nothing otherwise. It combines two jobs: visibility (shown when the state has any flag in the mask, mask 0 means always
shown) and interactability (with Disable When No History on, the default, it is not interactable when `CanGoBack` is false).
`SetContext` swaps the context at run time.

**Gotchas:** See the first entry of its Fixes section: the button is not hidden when it starts in a state outside its mask.

### `MID_UIStateManager.cs`
**What it does:** A `Singleton<MID_UIStateManager>` that applies a context to scene objects. It holds the context, an initial
state (raw int) and a list of `UIStatePanelConfig` rows. A row has `stateMask`, `displayName`, `show` and `hide` GameObject
arrays, and `onEnter` and `onExit` UnityEvents. The file also holds `UIStatePanelConfig`.

**On every state change:**
1. Exit pass: for each row with a mask other than 0 whose mask has no bit in the new state, deactivate its `show` objects and
   invoke `onExit`.
2. Enter pass: for each row whose mask has a bit in the new state, activate its `show` objects, deactivate its `hide`
   objects and invoke `onEnter`.
3. Raise the manager's own `OnStateChanged`. Exceptions in the two events are caught and logged, so one bad listener does not
   stop the others.

**Start:** With a non-zero initial state it calls `ChangeState(initial)`. With initial state 0 it hides the `show` objects of
all rows without raising events (`ApplyNoneState`). With no context it logs an error.

**API:** `ChangeState`, `GoBack`, `ClearHistory`, `IsInState`, `SetContext`, `Context`, `CurrentState` and `CanGoBack`.

**Decisions:** `Awake` calls `Remake(true)`, so the manager persists across scenes. See [singleton.md](singleton.md).

**Gotchas:**
- `onEnter` and `onExit` are not edge triggers. On each change `onExit` runs for every row that is not active, including rows
  that were not active before, and `onEnter` runs for every active row, including rows that were already active.
- A `hide` object is only deactivated on enter. Nothing re-activates it later.
- The manager persists across scenes, but its lists hold scene objects. See the second entry of its Fixes section.
- `OnStateChanged` is a public delegate field, not an `event`, so other code can invoke it or clear it.
- `SetContext` does not apply the new context's current state.

### `MID_UIStateContextEditor.cs` (Editor)
**What it does:** The inspectors of the four components and a helper class. It is public and has four custom editors:
`MID_UIStateVisibilityEditor`, `MID_UIStateBackButtonEditor`, `MID_UIStateButtonEditor` and `MID_UIStateManagerEditor`, and the
static helper `UIStateContextEditorUtils`. There is no editor for `MID_UIStateContext` itself in this file.
- `DrawFlagsCheckboxes(enumTypeName, maskProp)` draws one checkbox per non-zero enum member and a summary line.
- `DrawSingleStateDropdown(enumTypeName, maskProp, includeNone)` draws a popup of the single-bit members.
- Both find the enum with `Assembly.GetType` on every loaded assembly, by the name `MidManStudio.Core.UIState.<Name>UIState`. When
  the enum is missing they show a warning and fall back to a raw int field.
- The manager inspector draws its rows by hand. It uses a plain `Foldout` for each row and for the arrays, because the
  default array drawing opens a foldout header group that cannot be nested inside another one. Arrays are drawn as a size
  field plus one object field per element.

**Gotchas:**
- The editors read the serialized fields by name (`_context`, `_showWhenMask`, `_targetStateMask` and so on). Renaming a field
  in the runtime class breaks its inspector.
- `DrawSingleStateDropdown` writes the selected value on every repaint. See the Fixes list.

### `UIStateContextGenerator.cs` (Editor)
**What it does:** `UIStateContextGeneratorCore.Generate()` finds every `MID_UIStateContext` asset, checks them and writes one
enum file per asset, then refreshes the AssetDatabase. The window `UIStateContextGeneratorWindow` (MidManStudio > Utilities >
UI State Context Generator, priority 104) lists the contexts and has a Generate Now button. `Generate()` returns success,
errors and warnings.

**What it checks:** Duplicate `contextName` (error), an empty `contextName` (error), a name or state name that is not a C#
identifier (error), more than 30 states in one context (error), duplicate `packageId` (warning), an empty state name (warning,
the entry is skipped). With any error, nothing is written.

**What it writes:** `{contextName}UIState.cs` with `None = 0` and each state as `1 << index` in list order, with a comment
`// bit N` or `// <comment>  (bit N)`. The header holds a `Generated:` timestamp and the context and package names, so every
run rewrites every file. The file is written as UTF-8 with a byte order mark.

**Gotchas:**
- The output folder is a constant (`packages/com.midmanstudio.utilities/Runtime/UIState/Generated/`), relative to the project
  root. In a project that installs the package from git this folder does not exist, so the files go to a folder called
  `packages` next to `Assets` where Unity does not compile them. The generator works as intended only in the layout of the
  MidManStudio_Unity repository. This is the same limit as the Library and Scene generators. Found by reading the code, not run
  in Unity.
- A context that is deleted or renamed leaves its old enum file behind. The generator never deletes files.

## Fixes and Problems

### `MID_UIStateContext.cs`
- Moved out of the file header: the four setup steps. They are in the section above and in the class summary. The code is
  unchanged.
- Not fixed: a back step can fail to clear the "going back" flag. `GoBack` sets the flag and then calls `ChangeState(previous)`.
  `ChangeState` returns at once when `previous` equals the current state, and the flag stays set, so the next normal change is
  not pushed to the history. The state can equal the top of the history because state 0 is never pushed. A sequence that
  gets there: A, then 0, then A again (history holds A), then `GoBack`. Found by reading the code, not run.
- Not fixed: the reset in `OnEnable` is not repeated when the domain is not reloaded, see the gotcha above.

### `MID_UIElement.cs`
- Moved out of the file header: the usage note and the note that the class replaces `MenuManager` and `GenericUIElement`.
- Fixed in the comments: a comment said the element collects its immediate children. The code collects all descendants, with
  inactive ones. The comment and the summary now say that.
- Not fixed: `IsShowing` is false at start even when the panel is visible.

### `MID_UIStateVisibility.cs`
- Fixed in the code before this pass: the first state change after enable was skipped when the new visibility equalled
  the stored `_visible` value, so the initial state was never applied. The `_initialised` flag forces the first call through. The
  comments that said "FIX" now just describe the flag.
- Not fixed: mask 0 means always hidden here, but always visible on the back button, and the shared inspector summary says
  "No states selected, will always be visible (mask = 0)" for both. For this component that text is wrong.

### `MID_UIStateBackButton.cs`
- Moved out of the file header: the setup steps. They are in the section above and in the class summary.
- Not fixed: the button is not hidden at start when it starts outside its mask. `_isVisible` starts false, so when the first
  `RefreshVisibility` computes "do not show" the equality check returns before `Hide()` is called, and the button stays at the
  CanvasGroup value saved in the scene (alpha 1 by default, so visible) until the state first enters and leaves its mask. `MID_UIStateVisibility` fixed this exact
  problem with `_initialised`, and this component has no such flag. Found by reading the code, not run.
- Not fixed: `SetContext` does not reset `_isVisible`.

### `MID_UIStateButton.cs`
- Removed the comment "No changes to runtime logic." from the end of the old header. It said nothing about the code.

### `MID_UIStateManager.cs`
- Fixed in the comments: two "FIX" notes now describe the code as it is. They said that for initial state 0 `ChangeState` is a
  no-op (0 to 0), so `ApplyNoneState` hides the `show` objects directly, and that `MID_UIStateVisibility` handles its own initial
  state.
- Not fixed: the manager persists across scenes while its lists hold scene objects. When the scene that holds a manager is
  loaded again, the new manager is destroyed as a duplicate by the singleton, and the old one still points at the destroyed
  objects of the first load (the null checks skip them). The panels of the second load are not driven by the manager.
- Not fixed: `Start` calls `ChangeState(initial)`, which does nothing when the context already holds that state (the asset keeps
  its state across scene loads). The row lists are not applied in that case. With initial state 0, `ApplyNoneState` hides the
  `show` objects even when the context holds another state.
- Not fixed: `onExit` and `onEnter` are not edge triggered, see the gotcha above.

### `MID_UIStateContextEditor.cs`
- Fixed in the comments: two "FIX" notes explain why the manager inspector uses a plain `Foldout` and a hand-made array drawer
  (a foldout header group cannot be nested in another). The notes now only state that reason.
- Not fixed: `DrawSingleStateDropdown` assigns the chosen option to the property on every repaint, not only when the user
  changes it. When the stored value is not in the list (a new button with 0, a removed state, a combined mask) the popup shows
  the first entry and the inspector overwrites the stored value with it. For a new manager row this changes the default mask
  from 0 to the first state as soon as the row is shown.
- Not fixed: `DrawSummary` says "will always be visible" for mask 0 in the visibility inspector, see above.
- Not fixed: the enum lookup walks all loaded assemblies on every repaint.
- Not fixed: "+ Add Panel Config" uses `InsertArrayElementAtIndex` at the end. As far as we know Unity copies the last element
  into the new one, so the new row can start with the previous row's objects. Not verified in Unity.

### `UIStateContextGenerator.cs`
- Not fixed: there is no stable numbering. States get bits by list order on every run, so adding or removing a state before the
  end moves the bits of the ones after it. Every saved mask (`_showWhenMask`, `_targetStateMask`, `stateMask`,
  `_initialState`) is a raw int in scenes and prefabs and then points at another state. The effect, pool and audio generators
  keep a lock file for this, see [enum-generators.md](enum-generators.md).
- Not fixed: two states with the same name in one context are not detected, and the generated enum does not compile.
- Not fixed: the identifier check allows C# keywords.
- Not fixed: the output folder is fixed in the code, see the gotcha above.
- Not fixed: the check allows 30 states, but the bit shift stays within an int up to 31 states. The limit is lower than needed,
  which is safe.
- Note for the release: the three generated enums that ship in the package carry test package names in their headers
  (`com.manDman`, `com.mygame`, `com.test.proj`) and the generation date 2026-05-28. They compile and are used by other
  packages, so they were left alone. Whether `MenuUIState` and `LobbyUIState` should ship is a decision for the owner.
- Documentation note: `Documentation~/scenesetup.md` (Scene 2) still tells readers to create a "UI State Context Provider" and
  to set `contextDisplayName` and `enumTypeName` on the context. There is no such menu entry, and those two are read-only
  computed properties. The step list needs a rewrite to match the page above.
