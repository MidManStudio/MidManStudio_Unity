# com.midmanstudio.utilities: Editor Windows

Three UI Toolkit windows that live in `Editor/Helpers/`, each with a `.uxml` and `.uss` file next to it. They are Editor-only and
need Unity 2022.3 or later. All three use the shared pieces from [editor-helpers.md](editor-helpers.md) (`GradientBannerElement`,
`InfoPopupHandler`, `MidEditorUIHelpers`, and for the execution order window also `OrderBarElement`).

| Window | Menu | Priority |
|---|---|---|
| Script Utilities | `MidManStudio > Utilities > Script Utilities` | 115 |
| Script Execution Order | `MidManStudio > Utilities > Script Execution Order` | 116 |
| Sprite to Tilemap Converter | `MidManStudio > Utilities > Sprite to Tilemap Converter` | 117 |

Each window finds its UXML and USS by file name with `MidEditorUIHelpers`. If the UXML cannot be found, the window shows a
yellow warning label instead of the tool.

## Modules

### `MID_ScriptExecutionOrderWindow.cs` (Editor)
**What it does:** Shows and edits the script execution order that Unity stores in each script's `.meta` file (the same thing as
Project Settings, Script Execution Order), with search, a visual strip and a script browser.

**Lists:**
- Managed: every non-abstract MonoBehaviour script whose order is not 0, sorted by order and then by name.
- Unmanaged (Script Browser, opened with the button under the list): every other non-abstract MonoBehaviour script, sorted by
  namespace and then name.
- The scan covers all MonoScripts in the project, packages included. ScriptableObjects and abstract classes are left out.

**Toolbar:**
- Search matches the type name, namespace or assembly, case-insensitive, in both lists.
- Namespace dropdown: one exact namespace. It combines with the search.
- Group by Namespace: managed list under namespace headings (headings sorted by name, entries by order). Dragging and the up and
  down buttons are off while it is on.

**Order bar:** an `OrderBarElement` above the managed list with one dot per managed script, so you can see where scripts sit relative
to 0. Hover a dot for the name and order.

**Managed row:**
- The order field. Typing a number marks the row as changed.
- Up and down: swap the order with the previous or next row of the visible list.
- Locate: selects and pings the script in the Project window.
- Remove (x): sets the order back to 0 and moves the script to the browser.
- Dragging a row (when not grouped): the rows keep their set of order values and receive them again by position, so the order
  numbers themselves do not change, only who has which.

**Browser row:** locate, and `+` adds the script. A new script gets the highest managed order plus the step, or the step when the
list is empty.

**Footer:**
- Step field (default 100): used by `+` and by Auto Number.
- Auto Number: renumbers the managed list in its current order as `(index - count/2) * step`, so the numbers run around 0.
- Apply: calls `MonoImporter.SetExecutionOrder` for every managed script, sets removed ones to 0, and saves the assets.
- Discard: rescans and drops all unsaved changes.
- Apply and Discard are only visible while the window sees unsaved changes.
- When the window regains focus it rescans, unless it sees unsaved changes.

**Gotchas:**
- The up and down buttons act on the rows you can see. With a filter on, a neighbour is the next visible row, not the next script in
  the whole list. Auto Number ignores filters and renumbers every managed script.
- Auto Number gives the middle script the order 0. Unity treats 0 as the default, so that script leaves the managed list the next
  time the window scans. A list of one script becomes 0 as well.
- Apply writes every managed script, not only the changed ones. Whether Unity rewrites the `.meta` file of a script whose order did
  not change was not checked.
- Scripts from packages installed from git or a registry are in read-only folders. Whether the order of those can be saved was not
  checked in Unity.
- The window rescans on every focus when there are no unsaved changes, and a scan loads every MonoScript in the project. In a large
  project this is a pause each time you click back into the window.
- Execution order is applied to the script's class, not to a scene object, and it affects every scene.

### `MID_ScriptUtilitiesWindow.cs` (Editor)
**What it does:** A window with two tabs.

**Script Reader tab:**
- Lists every `.cs` file in the project, packages included, with the folder as a heading, sorted by folder and file name.
- Search matches the file name or the path. The "Search content" toggle also searches inside the files.
- Selecting a file shows it with line numbers in a read-only list with a monospace font. The "Doc Only" toggle shows only `///` lines,
  coloured by kind (summary, param, returns, remarks and example, other tags).
- Path bar buttons: open the file in the code editor (`AssetDatabase.OpenAsset`), ping it in the Project window, and copy its full path.
- The refresh button, and every focus, rescan the file list.

**Window Priority tab:**
- Lists the `[MenuItem]` priority of every EditorWindow subclass in the loaded assemblies. One row per method that has a non-validate
  MenuItem; the first MenuItem on a method is used.
- The filter box matches namespace, type name or menu path. It starts as `MidManStudio`.
- Sort by priority (then menu path) or by menu path. The src button opens that window's source in the Script Reader tab.
- Colours: green is a MidManStudio assembly, orange is a negative priority, blue is everything else.

**Gotchas:**
- The source is read from the file path on disk (`Path.GetFullPath` of the asset path, relative to the project folder). A script that is not
  at that path on disk shows an empty source and no error. That is the case for packages installed from git or a registry, which are
  stored in `Library/PackageCache`. Content search skips those files in the same way.
- Content search reads every `.cs` file that did not match by name, in full, on the UI thread, every time the search text changes. It is
  slow in a large project.
- The folder heading removes every `Assets/` and `packages/` piece of the path, so a script directly in `Assets` shows the folder
  `Assets`, and a package folder shows as `Packages/...` because Unity's real prefix is a capital P.
- The src button picks the first MonoScript file whose name equals the window's type name. A window whose class is named differently
  from its file gets nothing, and the button does nothing (no message).
- The Window Priority tab only lists MenuItems that sit on static methods of EditorWindow classes. A MenuItem on any other class does
  not appear. An assembly that fails to load some of its types is skipped without a message.
- Priority groups: the info popup says menu items are grouped in bands of 100. Unity's rule is that a divider is drawn when two
  priorities differ by 11 or more, see the Fixes section.

### `MID_SpriteToTilemapConverter.cs` (Editor)
**What it does:** Bakes the SpriteRenderers under a source object into Tilemap cells, so a hand-placed sprite layout can become a
real tilemap.

**Steps:**
1. Assign the Source Root (any GameObject; scene objects are the intended use), and set the Tile Output Path (default
   `Assets/MidManStudio/GeneratedTiles`, must start with `Assets`).
2. Scan. Every SpriteRenderer under the root that has a sprite (inactive ones included) is found and grouped by sorting layer name and
   sorting order. The groups are listed with their sprite counts, sorted by layer name and order.
3. Give each group a Target Tilemap (a Tilemap under a Grid). A group with no tilemap is skipped.
4. Dry Run reports what Convert would do and writes nothing. Convert does it.

**What Convert does for each sprite:**
- Finds or makes the Tile asset `<output path>/<sprite name>.asset` (spaces, `/` and `\` in the name become `_`). An asset that already
  exists at that path is reused.
- Takes the cell from the sprite's world position (`grid.WorldToCell`) and the offset from the cell centre to that position.
- Sets the tile, makes the cell colour editable (`TileFlags.None`), sets the colour to the sprite's colour, and sets a transform matrix
  of the offset, the sprite's local rotation and its local scale.
- If a second sprite of the same group lands in the same cell, a cell collision is reported and the later sprite replaces the earlier one.

**Report:** sprites converted, skipped, tiles created and reused, cell collisions, and one line per problem. The result box is green
when nothing was skipped and nothing collided, otherwise orange. A dry run counts a tile as created when its asset does not exist yet.

**Delete source:** after Convert the source root can be destroyed with `Undo.DestroyObjectImmediate`.

**Not carried over:** flip X and flip Y, the material, mask interaction, draw mode (sliced or tiled), any other component on the sprite
object (colliders, scripts, animators), and the order of sprites inside one group, because a tilemap draws all its cells with its own
sorting.

**Gotchas:**
- Two different sprites with the same name share one tile asset path. The first run creates the asset for the first sprite, and the
  second sprite then finds that asset and reuses it, so it is shown with the first sprite's image. The same happens when an asset from an
  earlier run has that name but another sprite. Tiles are never checked against the sprite they hold. Sprites cut from one sheet normally
  have unique names, so the risk is across different sheets. A dry run does not show it.
- The position is exact only when the Grid has no rotation or scale and the Tilemap's Tile Anchor is the default (0.5, 0.5). The offset
  is measured in world space and stored as a tile-local matrix. The sprite's local rotation and scale are used, not the world values,
  so a rotated or scaled parent is lost. This was read from the code, not tested.
- The cell is chosen from the sprite's pivot position, and all three coordinates are used. A sprite at a different z ends up in a
  different z layer of the tilemap, and cell collision is only checked inside one z layer.
- If "Delete source" is on and a target Tilemap is inside the source root, the tilemap is destroyed along with the root. Everything else
  under the root goes too, not only the sprites.
- The Source Root field also accepts a prefab asset. Destroying an asset with "Delete source" was not tried.
- Created Tile assets stay after Undo. Whether one Undo fully restores the Tilemap contents was not checked in Unity.
- A sprite name with a character Windows does not allow in file names, other than the three that are replaced (for example `:` or `?`),
  makes `CreateAsset` fail for that sprite.
- A Tile Output Path such as `AssetsX/Tiles` passes the "inside Assets" check, and a trailing slash gives paths with `//`. Dry Run does
  not check the path at all.
- Scan rebuilds the groups, so Target Tilemap choices are lost on rescan. Groups and the result are not kept across script reloads, and
  the Source Root field is empty again after one.
- Groups are sorted by sorting layer name, not by the layer order in Project Settings. This only affects the list order.

## Fixes and Problems

### `MID_ScriptExecutionOrderWindow.cs`
- Moved out of the file header into the class summary: the list of features, and the Unity 2022.3 requirement.
- Not fixed: a script that is only removed from the managed list is not counted as an unsaved change. The window decides there are
  changes with `_managed.Any(IsDirty)`, and a removed script is no longer in `_managed`. The Apply and Discard buttons stay hidden (the USS
  hides them unless the window sees changes), so the removal cannot be applied, and the next focus rescan silently brings the script back.
  Typing 0 in the order field instead of using the remove button works, because the row stays in the list and is changed. Removing a script
  while another script has an unsaved change also works. Found by reading the code and the USS, not run in Unity.
- Not fixed: when two scripts have the same order, the up and down buttons do nothing visible. `MoveManaged` swaps the two orders (no change), then
  sees they are equal and adds the direction to the other script instead of the one being moved, so the other script's order moves away in the wrong
  direction and the sort order stays the same. Dragging does not help either, because it reassigns the same equal numbers. Typing different numbers,
  or Auto Number, works. The code looks like it meant `se.EditOrder += direction`. Found by reading, not run.
- Not fixed: Auto Number gives the middle script the order 0 (described above).
- Not fixed: the rescan on every focus, and `ApplyChanges` writing every managed script.

### `MID_ScriptUtilitiesWindow.cs`
- Moved out of the file header into the class summary: the two-tab description and the Unity 2022.3 requirement.
- Not fixed: the Script Reader reads files by their path relative to the project folder, so scripts of packages installed from git or a
  registry show an empty source with no message. A fix would read the file through the asset path in a way that also works for
  `Library/PackageCache`, for example `PackageInfo.FindForAssetPath` to get the real folder, or `MonoScript.text` for the contents.
- Not fixed: the info popup text for the Window Priority tab says "< 0 above the first separator, 0 to 99 top group, 100 to 199 second group,
  200 and up further groups". Unity's scripting reference for `MenuItem` says a divider is drawn when priorities differ by 11 or more, so the real
  groups follow gaps of more than 10, not bands of 100. The text is a string in the code, so it is unchanged. Fixing it is a text edit and the
  owner's decision (it belongs to the question about editing user-facing strings).
- Not fixed: content search is synchronous and reads every file on each text change.

### `MID_SpriteToTilemapConverter.cs`
- Moved out of the file header into the class summary: what it does, the dry run, the collision detection, Undo, and the requirements.
- Not fixed: the same-name tile reuse problem, the world-space offset assumption, the delete-source hazards and the other gotchas listed
  above. None of them was run in Unity. The same-name problem follows directly from the code: the tile cache is keyed by `Sprite`, but the asset
  path is built from the sprite name only, and a found asset is never compared with the sprite.
