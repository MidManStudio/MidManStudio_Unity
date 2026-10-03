# com.midmanstudio.utilities: HierarchyArranger

## Modules

### `MID_HierarchySeparatorMarker.cs`
**What it does:** Marker component tagging an auto-generated separator
GameObject, so a re-run of `MID_HierarchyArranger` can find and remove its
own previous separators before inserting new ones instead of accumulating
more on every run. Lives in the runtime assembly (not Editor-only) so a
separator accidentally left in a scene never breaks a build, and is hidden
from the Add Component menu.

### `MID_HierarchyArrangeOptions.cs`
**What it does:** The options Editor-only type family for
`MID_HierarchyArranger`: `MID_HierarchyArrangeMode` (grouping strategy),
`MID_HierarchyGroupOrder` (how groups are ordered relative to each other),
`MID_HierarchySeparatorSettings`, and the combined `MID_HierarchyArrangeOptions`.

**Decisions:**
- Lives under `Runtime/HierarchyArranger/` but is wrapped in `#if
  UNITY_EDITOR` and stripped from player builds; it's placed alongside
  `MID_HierarchySeparatorMarker` for discoverability rather than moved into
  `Editor/`. None of its types actually reference `UnityEditor`, so this is
  a build-size/surface choice, not a hard technical requirement.

### `MID_HierarchyArranger.cs` (Editor)
**What it does:** The core static arranging logic (`Arrange`/`ArrangeMany`):
strips stale separators, groups a parent's children per
`MID_HierarchyArrangeOptions`, sorts within each group, orders the groups,
and inserts fresh separators between them. One Undo step per root,
covering any recursion.

**Decisions:**
- Reordering uses `SetAsLastSibling()` applied in exact target sequence,
  not `SetSiblingIndex(cursor++)`: interleaving object creation with an
  incrementing `SetSiblingIndex` is a known Unity gotcha where a freshly
  created GameObject doesn't reliably honor an explicit index in the same
  pass. Calling `SetAsLastSibling()` once per item, in order, is the
  standard fix.

### `MID_HierarchyArrangerWindow.cs` (Editor)
**What it does:** Editor window (`MidManStudio > Utilities > Hierarchy
Arranger`) that builds a `MID_HierarchyArrangeOptions` from UI Toolkit
controls and runs it against either the current selection
(`ArrangeSelected`) or every root object in the active scene
(`ArrangeSceneRoots`, via a scratch GameObject since scene roots don't
share a Transform parent to sort under directly).

## Fixes and Problems

### `MID_HierarchyArrangerWindow.cs`
- The comment explaining why `ArrangeRootLevel`'s unwind loop doesn't
  special-case separator GameObjects used to describe the bug itself
  ("previously these were deleted during the scratch-object unwind, which
  is why separators never showed up when arranging at the scene root").
  Trimmed to a forward-looking why-comment (separators are plain named
  GameObjects and don't need a parent to render correctly) and the history
  moved here: an earlier version of this unwind loop special-cased and
  deleted separator objects when returning scene roots from the scratch
  parent, which meant separators silently never appeared when arranging at
  the scene-root level (only when arranging a normal selection). No code
  change this pass; the loop already doesn't delete them.
