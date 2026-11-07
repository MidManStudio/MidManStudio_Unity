# com.midmanstudio.utilities: StickyNote

The sticky note is a scene note that draws itself as an overlay in the Game View. It has one runtime file,
`Runtime/StickyNote/MID_StickyNote.cs` (it also holds the small `MID_StickyNoteDragHandler` class), and one editor
file, `Editor/StickyNote/MID_StickyNoteEditor.cs`. The runtime types are in the namespace `MidManStudio.Core.Notes` and
the assembly `MidManStudio.Utilities`. The editor class is in `MidManStudio.Core.EditorUtils.Notes` and the assembly
`MidManStudio.Utilities.Editor`. There are no generated files. The `TickTest` and `PoolSystem` samples each contain
one note.

## Modules

### `MID_StickyNote.cs`
**What it does:** A MonoBehaviour (`[ExecuteAlways]`, `[DisallowMultipleComponent]`, menu path
`MidManStudio/Utilities/Sticky Note`) that builds its own UI at runtime. The build creates a child GameObject named
`StickyNoteCanvas_<instance id>` with a Screen Space Overlay `Canvas` (sorting order from the inspector, default 500),
a `CanvasScaler` in constant pixel size mode, and a `GraphicRaycaster`. The hierarchy is made of legacy UGUI
components (`Text`, `Image`, `Button`, `ScrollRect`), not TextMeshPro:

- `NotePanel`: the note itself, with a `CanvasGroup`. Its size is the Width setting by the header height (30) plus the
  body height.
- `Header`: a 30 px bar with the title, a minimize button (label `-`), a close button (label `x`) and the drag handler.
- `Body`: a vertical `ScrollRect` with a `RectMask2D` viewport inset by 4 px and one wrapping `Text`.
- `PinButton`: a 30 by 30 button on the canvas, outside the panel. It is shown only while the note is closed and
  brings it back with `Show()`.

The canvas gets `HideFlags.DontSave`, so it is not written into the scene file. It is not hidden in the Hierarchy
window. It is destroyed in `OnDisable` and `OnDestroy` and rebuilt in `OnEnable`.

**What works in Edit Mode and what works only in Play Mode.** The README says the note works in both modes with drag,
minimize and close. The code supports that only in part:

| Feature | Edit Mode | Play Mode |
|---|---|---|
| Build and show the note | Yes, when `Build in Edit Mode` is on (default) and the GameObject is in a loaded scene | Always. The `Build in Edit Mode` setting is ignored |
| Live preview of anchor, margin, theme, font size, width, height and text | Yes, through `OnValidate` while the note is built | Yes |
| Drag the header | Intended for Play Mode only | Yes, when `Draggable` is on |
| Minimize and close buttons, pin button, scrolling | Intended for Play Mode only | Yes |
| `Show`, `Hide`, `Toggle`, `Minimize`, `Restore` from code | Run, because they do not check the play state | Yes |
| Inspector Visible and Minimized buttons | Not drawn | Drawn |
| Inspector Rebuild button | Available | Available |

Drag, minimize, close, the pin button and scrolling are ordinary UGUI event handlers. The code has no other input
path, and the file comments and tooltips state that they need the EventSystem to process input, which is Play Mode.
This was not tested in Unity here. The README sentence is true for showing and previewing the note in Edit Mode, and
not for interacting with it.

**How to use it:**
1. Add the component to a GameObject in a scene (`Add Component`, `MidManStudio/Utilities/Sticky Note`). Keep the
   GameObject outside other canvases.
2. Edit the title and notes in the inspector, or set them from code:
```csharp
var note = GetComponent<MID_StickyNote>();
note.SetTitle("Controls");
note.SetNotes(new List<string> { "WASD to move", "F to fire" });
note.SetTextColor(Color.white);   // override the theme's text colour
note.Minimize();
```
The text is the non-empty note entries, one per line, then the text file content after a blank line (the file is
appended, it does not replace the list). Trailing line breaks are trimmed. With no entries and no file the body shows
`(no notes)`.

**Inspector fields (serialized names, defaults and ranges from the code):**

| Section | Field | Default | Notes |
|---|---|---|---|
| Content | `_title` | `Scene Notes` | Header text |
| Content | `_notes` | three sample lines | `List<string>`, empty entries are skipped |
| Content | `_textFile` | none | Optional `TextAsset`, appended after the notes |
| Appearance | `_theme` | `Yellow` | `Yellow`, `Blue`, `Green`, `Pink`, `Dark` |
| Appearance | `_fontSize` | 14 | Range 9 to 28. The title uses this plus 2 |
| Appearance | `_useCustomTextColor`, `_customTextColor` | off, black | Overrides the theme's body text colour |
| Appearance | `_width` | 320 | Range 160 to 700, in canvas pixels |
| Appearance | `_maxBodyHeight` | 360 | Range 80 to 900. Taller text scrolls |
| Appearance | `_cornerPadding` | 8 | Range 2 to 24. Added twice to the body height |
| Position | `_anchor` | `TopLeft` | See below |
| Position | `_margin` | (16, 16) | Distance from the anchored corner |
| Position | `_draggable` | on | Header drag, Play Mode only |
| Position | `_sortingOrder` | 500 | Sorting order of the canvas. Higher draws on top |
| Behaviour | `_startVisible` | on | Initial visible state, applied in `OnEnable` |
| Behaviour | `_buildInEditMode` | on | Build the note outside Play Mode |

The inspector shows the Position fields under the heading Layout, see the editor section below.

**Theme palette.** The four arrays are indexed by the `NoteTheme` value. Values are RGB from 0 to 1.

| Theme | Header | Body | Body text | Header text |
|---|---|---|---|---|
| `Yellow` | 0.95, 0.80, 0.10 | 0.99, 0.96, 0.60 | 0.14, 0.10, 0.02 | 0.18, 0.12, 0.00 |
| `Blue` | 0.20, 0.45, 0.82 | 0.78, 0.87, 0.98 | 0.07, 0.10, 0.24 | white |
| `Green` | 0.16, 0.62, 0.24 | 0.76, 0.95, 0.77 | 0.05, 0.18, 0.07 | white |
| `Pink` | 0.84, 0.24, 0.52 | 0.97, 0.78, 0.86 | 0.22, 0.05, 0.11 | white |
| `Dark` | 0.12, 0.12, 0.16 | 0.18, 0.18, 0.22 | 0.90, 0.90, 0.92 | white |

The pin button takes the theme's header colour.

**Anchors.** For the four corners the panel pivot and anchors are set to that corner and the position is the margin
pointing inward. `Center` uses anchors and pivot (0.5, 0.5), position zero, and ignores the margin. `Free` applies
nothing.

| Anchor | Anchors and pivot | Anchored position |
|---|---|---|
| `TopLeft` | (0, 1) | (margin.x, -margin.y) |
| `TopRight` | (1, 1) | (-margin.x, -margin.y) |
| `BottomLeft` | (0, 0) | (margin.x, margin.y) |
| `BottomRight` | (1, 0) | (-margin.x, margin.y) |
| `Center` | (0.5, 0.5) | (0, 0) |
| `Free` | not changed | not changed |

Dragging the header adds the pointer delta, divided by the canvas scale factor, to the panel position and sets the
`_anchor` field to `Free`. The position is not clamped to the screen. The pin button is placed with the same table
and the same margin, using `TopLeft` when the anchor is `Free`.

**Public API:**
- State: `IsVisible`, `IsMinimized`.
- Visibility: `Show()`, `Hide()` (the hierarchy is kept), `Toggle()`, `SetMinimized(bool)`, `Minimize()`, `Restore()`.
- Content: `SetTitle(string)` (applied at once), `SetNotes(List<string>)` (copies the list, null gives an empty
  list), `AddNote(string)`, `ClearNotes()` (keeps the text file), `SetTextFile(TextAsset)`. Note and text file
  changes only mark the content dirty and are applied by the next `Update()`.
- Colour: `SetTextColor(Color)` (turns the override on), `UseThemeTextColor()`.
- Layout: `ResetToAnchor()` re-applies the configured anchor and margin, `RebuildNow()` destroys and rebuilds the
  hierarchy.
- Editor only (`#if UNITY_EDITOR`): `GetHeaderColor`, `GetBodyColor`, `GetTextColor`, used by the inspector preview.

**Drag handler.** `MID_StickyNoteDragHandler` is a public class in the same file, hidden from the Add Component menu.
It is added to the header at build time and implements `IBeginDragHandler`, `IDragHandler` and `IEndDragHandler`.
Only `OnDrag` does anything. Its public fields are `Target` (the panel), `Canvas` (for the scale factor, a missing
canvas or a factor of 0 or less counts as 1), `OnDragged` and `IsEnabled`.

**Decisions:**
- The note is UGUI and not IMGUI. The author's notes give two reasons, neither tested here. `OnGUI` is said not to run
  in the Game View outside Play Mode even with `[ExecuteAlways]`, because that attribute enables `Update` and
  `OnEnable` in Edit Mode but not the IMGUI pass. `OnGUI` is also said to mix `Screen.width` and `Screen.height`
  (physical pixels) with `Event.current.mousePosition` (scaled by the editor pixels per point on high DPI screens),
  which makes the position drift. Canvas rendering does not depend on the play state, and anchoring has no such
  mismatch.
- In Edit Mode `OnEnable` does not build at once. It queues `DeferredRebuildIfNeeded` with
  `EditorApplication.delayCall`. `OnEnable` can run inside Unity's own message pass (domain reload, scene load, prefab
  stage entry), and calling `AddComponent` there logs "SendMessage cannot be called during Awake, CheckConsistency, or
  OnValidate". The deferred call does nothing if the component was destroyed, is already built, or should not be built
  any more. `Update()` also builds when the note is missing, so it is a second path to the same result. Play Mode
  builds synchronously, so the note is there on the first frame.
- `ShouldBeBuilt()` is `(Application.isPlaying || _buildInEditMode) && gameObject.scene.IsValid()`. `Update()` destroys
  the hierarchy when this turns false, for example after `Build in Edit Mode` is switched off.
- The panel anchors are fixed to a single point before the size is set. The panel is created with stretch anchors, and
  with stretch anchors `sizeDelta` is added to the parent's size instead of being the size.
- EventSystem handling: an existing `EventSystem.current` is never touched. If none is current, an inactive-inclusive
  scan (`FindObjectsByType`) is done and any EventSystem found is left alone. Only when nothing exists a new
  `EventSystem` with a `StandaloneInputModule` is created (`HideFlags.DontSave`). That one is kept in a static field
  with a static reference count and destroyed when the last holder releases it. See the known issues for where this
  counting does not work.
- Sizes are in canvas pixels because the scaler uses constant pixel size at the default scale factor. The note does not
  scale with screen resolution or DPI.
- The built-in font is `LegacyRuntime.ttf`, with `Font.CreateDynamicFontFromOSFont("Arial", 14)` as the fallback.
- Notes are stored in serialized private fields on purpose. The editor finds them by name, and the sample scenes
  serialize those names.

**Gotchas:**
- Interaction is Play Mode only, see the table above. Do not rely on dragging, closing or minimizing in Edit Mode.
- The component is not stripped from player builds. With `Visible on Start` on, the note appears in a build at sorting
  order 500. Disable or remove the GameObject (or tag it `EditorOnly`) before shipping.
- The auto-created EventSystem uses `StandaloneInputModule`, which reads the legacy Input Manager. A project that uses
  only the Input System package should place its own EventSystem with an `InputSystemUIInputModule` in the scene, as
  the code comment says.
- `OnValidate` re-applies anchor, theme, layout and text. It does not touch the canvas sorting order or the pin button
  position, and `_startVisible` is only read in `OnEnable`. After changing `Canvas Sorting Order` or the anchor of a
  note that is already built, use the Rebuild button for the pin button and the sorting order to follow.
- `OnEnable` resets the visible state to `_startVisible` and clears the minimized state. A close or minimize does not
  survive a disable and enable, a domain reload or a play mode change.
- `RebuildNow()` does not check `ShouldBeBuilt()`.
- Selecting `Free` in the inspector gives a bad layout, see the first known issue.
- A new theme needs an entry at the same index in all four palette arrays, or `ApplyTheme` throws an index error.
- `_cornerPadding` only adds height. With a value below 4 the viewport (body height minus 8) is shorter than the text,
  so the last lines can need a scroll. Worked out from the layout code, not run.
- A bottom anchor with a minimized note looks wrong, see the known issues.
- The pin button label is the pin emoji set on a legacy `Text` with the built-in font. Whether the glyph renders was not
  checked.
- Main thread only. Everything uses Unity UI and scene APIs.

### `MID_StickyNoteEditor.cs` (Editor)
**What it does:** The custom inspector for `MID_StickyNote` (`[CustomEditor]`, class `MID_StickyNoteEditor`). It
caches 16 `SerializedProperty` objects by the private field names of the runtime class and draws, top to bottom:

- A header strip with the label `Sticky Note`, a fixed `v2.0 (UGUI)` text and the GameObject name.
- A theme preview, 64 px tall, drawn with IMGUI rectangles (not the real canvas): the header colour with the title,
  two small squares standing in for the buttons, the body colour with the first three note entries (and an ellipsis
  when there are more), and the theme name. The text colour follows the custom colour when it is on.
- An info box that says position and appearance preview outside Play Mode and that dragging, minimizing and closing
  need Play Mode.
- Four foldout sections. `Content` and `Appearance` start open, `Layout` and `Behaviour` start closed. The fold state
  is not saved.
- A `Rebuild Note` button that calls `RebuildNow()`.
- In Play Mode only, a `Runtime Controls` box with a Visible/Hidden button (`Toggle()`) and an Expanded/Minimized
  button (`Minimize()` and `Restore()`). The editor repaints on every draw while this box is shown.

**Section contents:**

| Section | Controls |
|---|---|
| Content | Title, the note list (one text field per entry with up and down buttons and a remove button, `+ Add Note`, `Clear All` with a confirmation dialog), `Text File (.txt)` |
| Appearance | Theme swatches (one button per theme, tinted with the header colour, the selected one underlined), Font Size, Use Custom Text Color and Text Color, Width (px), Max Body Height (px), Inner Text Padding |
| Layout | Initial Anchor, Margin, Draggable, Canvas Sorting Order |
| Behaviour | Visible on Start, Build in Edit Mode |

**Decisions:**
- The preview is drawn separately from the runtime canvas so it works in any inspector state and costs nothing. It
  reads its colours through the editor-only getters on `MID_StickyNote` instead of keeping a second copy of the
  palette.
- The note list is a custom drawer and not the default list, so entries can be moved with arrow buttons and cleared
  after a confirmation.

**Gotchas:**
- The property names (`_title`, `_notes`, `_textFile`, `_theme`, `_fontSize`, `_useCustomTextColor`,
  `_customTextColor`, `_width`, `_maxBodyHeight`, `_cornerPadding`, `_anchor`, `_margin`, `_draggable`,
  `_sortingOrder`, `_startVisible`, `_buildInEditMode`) must match the runtime fields. Renaming one in the runtime
  file breaks the inspector.
- Each note entry is a single-line text field. For multi-line text use the text file.
- The editor does not support multi-object editing (no `[CanEditMultipleObjects]`).

## Fixes and Problems

### `MID_StickyNote.cs`
- Moved out of the file header: the UGUI against IMGUI reasoning, the Play Mode limit for drag, minimize and close, the
  Edit Mode build timing note and the EventSystem ownership note. They are now in the class summary and in the
  Decisions above. The wording "does not run outside Play Mode" for `OnGUI` is the author's claim and was not tested.
- The note was rewritten from an IMGUI version (`OnGUI`) to the current UGUI version in June 2026. Only the reasoning
  survives in the old header, so there is nothing more to record about the earlier version.
- Early July 2026: building from `OnEnable` in Edit Mode was changed to a deferred call (`EditorApplication.delayCall`)
  because building inside Unity's message pass logged the "SendMessage cannot be called during Awake,
  CheckConsistency, or OnValidate" warning. Play Mode stays synchronous.
- Early July 2026: the auto-created EventSystem became reference counted and is destroyed only when no holder is left.
  The comment says this stopped duplicate EventSystems from piling up in the scene. In the same change the existence
  check went from `FindAnyObjectByType<EventSystem>()` to an inactive-inclusive `FindObjectsByType`, so an inactive
  EventSystem now counts as existing. See the fourth and fifth known issues below: the counting does not cover every
  case.
- The panel anchors are set before the size, so that `sizeDelta` takes effect. The comment calls the old order a
  problem ("sizeDelta now actually has effect"). The Free anchor still has the problem, see below.
- Not fixed: a `Free` anchor leaves the panel with the stretch anchors it is created with, so `sizeDelta` is added to
  the canvas size and the panel becomes about the canvas size plus Width and the body height. This happens when `Free`
  is chosen in the inspector, and also when a note that was dragged (the drag sets `_anchor` to `Free`) is rebuilt, for
  example with the Rebuild button or by disabling and enabling it. Found by reading the code, not run.
- Not fixed: `ResetToAnchor()` does nothing after a drag, because the drag overwrote `_anchor` with `Free` and the
  original value is not kept. The drag is not clamped, so a note dragged off screen cannot be brought back by this
  method. Closing and reopening does not move it either, because only the panel position changed.
- Not fixed: minimizing does not resize the panel. The header sits at the top of the full-height panel, so with
  `BottomLeft` or `BottomRight` (and with `Center`) the minimized header floats above the margin by the height of the
  hidden body. Found by reading the code, not run.
- Not fixed: the EventSystem reference count only works for some cases. `EnsureEventSystem` returns at the first line
  when `EventSystem.current` is not null, before it claims a reference. With two notes and no user EventSystem, the
  second note finds the first note's EventSystem current and never holds a reference. When the first note is disabled
  or destroyed the count reaches 0 and the EventSystem is destroyed under the second note. It depends on the new
  EventSystem being `EventSystem.current` right after creation. Found by reading the code, not run.
- Not fixed: in Play Mode `Rebuild` calls `Destroy`, which acts at the end of the frame. `RebuildNow()` (or a disable
  and enable in the same frame) releases the last reference and destroys the owned EventSystem, but
  `EnsureEventSystem` then still sees it as `EventSystem.current` and creates nothing. At the end of the frame the
  EventSystem is gone and the rebuilt note gets no input. Edit Mode uses `DestroyImmediate` and is not affected. Found
  by reading the code, not run.
- Not fixed: `README.md` says the note works in Edit Mode with drag, minimize and close. `APICATALOG.md` says the text
  file overrides the notes list (the code appends it), calls the anchor enum `AnchorPosition` (the type is
  `NoteAnchor`, the serialized field is `_anchor`) and does not list the `Free` value. Those files are outside this pass and were not changed.

### `MID_StickyNoteEditor.cs`
- Not fixed: the header strip shows the fixed text `v2.0 (UGUI)`. It is a string literal and does not follow the
  package version (1.0.0).
- Not fixed: the preview does not match the runtime in two cases. It draws the header text white for every theme,
  while the runtime uses a dark brown for `Yellow`. It does not show the text file, so a note with no entries and a
  text file assigned previews as an empty box.
