# com.midmanstudio.utilities: Editor Helpers

Editor-only code for the helpers and a few small tools. All of it is in the Editor assembly
(`MidManStudio.Utilities.Editor`, Editor platform only), so none of it is in a build. Eight files:

- Drawers and inspectors for the attributes and for `MID_BaseSO`: `NamedListDrawer`, `SortingLayerDrawer`,
  `MID_BaseSOEditor`, `MID_BaseSOProjectIconDrawer`. The runtime side is in [helpers.md](helpers.md).
- Shared UI Toolkit pieces for the editor windows: `MidEditorUIElements`.
- `SceneDependencyInjector` (a component plus its inspector).
- `MID_PackageUpdateChecker` (a window plus a class that starts it).
- `MID_ScriptableObjectViewerWindow`.

The other three windows in `Editor/Helpers/` (Script Execution Order, Script Utilities, Sprite to Tilemap) are in
[editor-windows.md](editor-windows.md). The windows on this page are under the menu `MidManStudio > Utilities`.

## Modules

### `NamedListDrawer.cs` (Editor)
**What it does:** The drawer for `[MID_NamedList]`. For each list element it works out a name and draws the normal field with
that name as its label. With `useCustomColors` it also draws a tint behind the element.

**Name, in this order:** `IArrayElementTitle.Name` (read with `SerializedProperty.boxedValue`), then the element field named in
the attribute, then the field's display name. A blank name is shown as `Missing_<index>`.

**Tint:** `IArrayElementColor.ElementColor` first, then a Color field named by the attribute (default `color`), looked up by
full path and then relative to the element. `Color.clear` or no colour means no tint. The tint is the colour at alpha 0.15
behind the element, with a 4 px left bar and a 2 px top bar at alpha 0.5.

**Gotchas:**
- `boxedValue` copies the element into a managed object. That happens for the name and again for the colour, on every
  repaint of every visible element. It is fine for short lists and costly for long ones.
- If the attribute names an element field that does not exist, and the element does not implement `IArrayElementTitle`,
  a warning is logged on every repaint.
- A blank name on a field that is not a list element is shown as `Missing_<n>`, and the counter goes up on every repaint, so
  that label keeps changing.
- The class is public and its `Attribute` property is virtual, so it can be subclassed.

### `SortingLayerDrawer.cs` (Editor)
**What it does:** The drawer for `[MID_SortingLayer]`. It shows a popup of `SortingLayer.layers` and stores the chosen name in the
string. It uses only the public `SortingLayer` API, not the private `EditorGUILayout.SortingLayerField`, which is reachable only
through reflection and can change between Unity versions.

**Gotchas:**
- A stored name that matches no layer is shown as the first layer in the list, but the string is not changed until someone
  picks a layer. A prefab or asset can therefore show one layer and hold another name.
- A field that is not a string, or a project with no sorting layers, gets the normal field.

### `MID_BaseSOEditor.cs` (Editor)
**What it does:** The inspector for `MID_BaseSO` and every class derived from it (`editorForChildClasses` is on). A derived
class that has its own custom editor uses that one and loses this layout.

**Layout:** a box with a 36 px preview of the icon in use (or Unity's default icon), the asset name and a line that says where
the icon comes from (per-asset icon, group icon with its path, or the default). Then the `Custom Icon` field, a help box that
shows the group icon path when one is used, and then every other field (`DrawPropertiesExcluding` skips the icon field).

When the icon field changes, it saves, then calls `MID_BaseSOProjectIconDrawer.InvalidateCache` for the asset and repaints the
Project window.

**Gotcha:** if there is no resolved icon and Unity's default icon is not a `Texture2D`, the preview draws a white square.

### `MID_BaseSOProjectIconDrawer.cs` (Editor)
**What it does:** Draws the icon of each `MID_BaseSO` asset in the Project window. It is `[InitializeOnLoad]` and hooks
`EditorApplication.projectWindowItemOnGUI`.

**How:**
1. For each visible item it looks the GUID up in a cache. On a miss it loads the asset as `MID_BaseSO`. If it is not one, the
   GUID goes into a "not a MID_BaseSO" set and is never looked at again. Otherwise the result of `ResolveIcon()` is cached,
   including null.
2. With an icon, it works out a square (the row height in list view, the smaller side of the tile in icon view), paints an
   opaque rectangle in the Project window background colour over the area, and draws the icon on top.

**Why the background rectangle:** Unity paints its own icon first and gives no way to stop that, and the icon is drawn with
alpha blending. Without the rectangle the old icon shows through letterboxed edges and through transparent pixels of the new one.
The colours are the standard dark and light skin colours, with a rough blue tint for a selected row. They are not read from the
real row, so they can be slightly off. This is cosmetic.

**Gotchas:**
- The cache is cleared on script reload and by `InvalidateCache(guid)`, which only the inspector calls. An icon changed by
  undo, by a text edit of the asset or by a pull from source control does not show until the next script reload. A "no icon"
  result is cached too, so a group icon file added later also needs a reload.
- On the first draw of each visible item, `LoadAssetAtPath<MID_BaseSO>` is called for it to check its type. That is a one-time cost
  per item per script reload, and it applies to every asset type in the project, not only ScriptableObjects.
- `Selection.assetGUIDs` is read for each drawn item to choose the selected colour.

### `MidEditorUIElements.cs` (Editor)
**What it does:** Shared UI Toolkit pieces for the MidManStudio editor windows. Namespace `MidManStudio.Core.EditorTools`. Four
public types:

- `GradientBannerElement`: a four-corner gradient drawn as one mesh. Fields `ColorTL`, `ColorTR`, `ColorBL`, `ColorBR`. Create
  it in C# and insert it at index 0 of a header element, with absolute position and all four edges 0. It ignores the mouse.
- `OrderBarElement`: a strip with one dot per script placed by execution order. `UpdateData(items)` takes
  `(name, order, dirty, isMms)` tuples. The range is the lowest to highest order, `-200..200` when empty, and `+-100` around a
  single value. Yellow is changed and not yet saved, blue is below zero, orange above, grey is zero. Hovering shows the nearest
  dot within 14 px as a tooltip. The `isMms` flag is accepted but not used.
- `InfoPopupHandler`: manages the floating help panel in the UXML. It looks for the elements `info-popup`, `info-popup-title`
  and `info-popup-body`. `Toggle(button, title, body)` shows the panel under the button, or hides it if it is already showing for
  that button. A mouse press anywhere outside it hides it. Without an `info-popup` element everything does nothing and
  `IsAvailable` is false.
- `MidEditorUIHelpers.FindUxml(name)` and `FindUss(name)`: find a UXML or USS file by file name without extension.

**Used by:** the Script Execution Order, Script Utilities and Sprite to Tilemap windows, the Scriptable Object Viewer, the
Hierarchy Arranger and the Auto Reference window and inspector.

**Gotchas:**
- `FindUxml` and `FindUss` search the whole project, packages included, with `AssetDatabase.FindAssets`, and take the first file
  with that exact name. Nothing is cached, so each window open repeats the search, and a second file with the same name
  anywhere in the project can win.
- `InfoPopupHandler` does not check the two labels. A UXML that has `info-popup` but not the labels throws when the popup is shown.

### `SceneDependencyInjector.cs` (Editor)
**What it does:** For testing one scene on its own without a bootstrap scene. It holds a list of manager prefabs and, when Play
Mode starts, instantiates the ones the scene does not already have. The file holds `DependencyItem` (one prefab row, its `Name`
is the prefab name), the component `SceneDependencyInjector` and its inspector `SceneDependencyInjectorEditor`.

**Settings:** `requiredDependencies`, `autoInjectOnPlay` (default on), `cleanupOnStop` (default on), `_logLevel` (default `Info`).
State: `_injectedObjects` and `_hasInjected`. Context menu: Inject Dependencies, Force Reinject All, Cleanup Injected Objects.

**How it runs:**
1. `OnEnable` does nothing outside Play Mode. In Play Mode it subscribes to `playModeStateChanged` and injects when
   `autoInjectOnPlay` is on and it has not injected yet. The `EnteredPlayMode` handler does the same check.
2. For each row: a null prefab is skipped with a warning. A prefab that is already present is skipped. Otherwise it is
   instantiated, renamed to the prefab name (no `(Clone)`) and, because the prefab is an asset, moved to `DontDestroyOnLoad`.
3. On `ExitingPlayMode`, with `cleanupOnStop` on, the injected objects are destroyed.

**"Already present" means:** any MonoBehaviour type on the prefab's root object exists in a loaded scene (`FindObjectsByType`,
active objects only), or an active object with the prefab's name exists. Only the root object's components are checked.

**Gotchas:**
- A prefab that carries several managers is skipped as soon as one of its component types exists in the scene. The others on the
  same prefab are then not injected.
- Inactive objects do not count, so an inactive manager already in the scene is treated as missing and a second one is made.
- Order matters. If another script's `Awake` already created a singleton such as `MID_Logger` before this component ran, the
  prefab that contains it is skipped.
- An empty row added with `+` is removed by `OnValidate` the next time the component is validated, for example on the next field
  edit or a scene reload. Assign the prefab right after adding the row.
- Leaving Play Mode destroys everything made in Play Mode anyway, so `cleanupOnStop` mostly matters for the log lines and for
  the manual Cleanup button.
- The inspector draws the list by hand. The `[MID_NamedList]` attribute on the list field is not used by it.
- **Open question, not checked in Unity:** this is a MonoBehaviour in an Editor-only assembly. The README and the scene setup
  guide tell users to add it to a GameObject. Unity may not allow components from an Editor-only assembly to be attached, and a
  scene that holds one would show a missing script in a build. Someone needs to try adding it in a real project before the
  README keeps recommending it.

### `MID_PackageUpdateChecker.cs` (Editor)
**What it does:** A window, `MidManStudio > Utilities > Check for Updates`, that asks GitHub for the repository's latest release
and compares its tag with the installed version. The file also holds `MID_UpdateCheckerAutoRun`, an `[InitializeOnLoad]` class that
starts a silent check.

**Network, plainly:**
- It sends one `GET https://api.github.com/repos/MidManStudio/MidManStudio_Unity/releases/latest`, with the headers
  `User-Agent: MidManStudio-UpdateChecker/<installed version>` and `Accept: application/vnd.github.v3+json`. Nothing else is sent.
  GitHub sees the IP address and that header.
- It runs when the window opens, when Check Now is pressed, and silently on a domain reload when the last successful check is
  more than 24 hours old. Reading the code, an open window also asks again each time it is recreated after a script compile,
  because the "already checked" flag is not kept and the window path ignores the 24 hour rule.
- The automatic check is on by default and there is no setting to turn it off.
- A silent check that finds a newer version opens the window, and opening the window sends a second request.
- A request that fails (offline, a 403 from GitHub's rate limit of 60 anonymous requests an hour, a 404 when there is no
  latest release) records no time, so the next domain reload tries again. There is no back-off and no timeout.

**Stored in EditorPrefs** (per user on this machine, shared by all projects):

| Key | Value |
|---|---|
| `MidManStudio.UpdateChecker.LastCheck` | UTC time of the last successful check |
| `MidManStudio.UpdateChecker.LastResult` | `remoteVersion\|releaseUrl` |
| `MidManStudio.UpdateChecker.SkipTag` | the version the user chose with Skip This Version |

**Reading the answer:** it takes `tag_name`, `html_url` and `body` out of the JSON with plain string search (no JSON parser),
strips a leading `v` from the tag, and compares it with the installed version as a `System.Version`. Older than remote means
"Update available" and the window shows the tag, the install URL, the notes and the buttons (Check Now, Open Release Page, Skip This
Version).

**Known problems, all release-relevant, none fixed. They need an owner decision** (see Fixes):
1. The installed version is read from `packages/com.midmanstudio.utilities/package.json` relative to the Unity project folder. In
   a project that installs the package from git or a registry the file is not there, the installed version is `0.0.0`.
2. `releases/latest` is repository-wide, and the releases are tagged `<package>/vX.Y.Z` (for example `utilities/v1.0.0`). A
   tag with a package prefix does not parse as a version.
3. The install URL it shows ends in `#v<version>`, which is not a tag that exists in this repository.

What a user sees today, from reading the code: with the tag format in this repository the remote version does not parse and counts
as `0.0.0`, so the window always says "up to date", and it prints the raw tag with an extra `v` in front. If the latest release
were a plain `vX.Y.Z` tag instead, a git-installed project would read `0.0.0` as the installed version and would be told an update is
available on every check, with a window opening about once a day.

### `MID_ScriptableObjectViewerWindow.cs` (Editor)
**What it does:** `MidManStudio > Utilities > Scriptable Object Viewer`. Lists every ScriptableObject asset in the project, packages
included, grouped by class name, with an embedded inspector for the selected one. Namespace
`MidManStudio.Core.EditorUtils.ScriptableObjectViewer`. Its UXML and USS are next to it.

**Controls:**
- Category dropdown: `All Categories` or one class, each with its count. The category is the concrete class name from
  `asset.GetType().Name`, not the folder or a base class.
- Search: a case-insensitive match on the asset name only.
- Folder scope: a folder asset. Only assets under that folder are listed.
- Refresh: rescans.
- Paging: 50 rows per page, with previous and next buttons.
- Rows show the icon, the name and, in `All Categories`, the class name. The icon is the `MID_BaseSO` icon (`ResolveIcon()`) when
  there is one, otherwise the asset's mini thumbnail.
- Detail panel: an `IMGUIContainer` that draws `Editor.CreateEditor(asset).OnInspectorGUI()`, so it uses the type's own custom
  editor, `MID_BaseSOEditor` included. Edits are real edits to the asset.

**Gotchas:**
- The scan runs `FindAssets("t:ScriptableObject")` and loads every result, packages included. In a large project that is every
  ScriptableObject in memory and a noticeable pause on open and on Refresh.
- The list does not follow the project. Assets created, deleted or renamed after the scan show up only after Refresh. A renamed
  asset keeps its old position in the sort order until the next filter change or scan.
- Selecting a row also pings the asset and sets `Selection.activeObject`, so the Inspector and the Project window follow it.
- Two classes with the same name in different namespaces share one category.
- State (selection, filters, page) is not kept across script reloads. The window rescans when it is recreated.

## Fixes and Problems

### `NamedListDrawer.cs`
- Fixed in the comments: the note about the missing-name counter said it ensures unique identifiers. It only numbers blank names
  outside lists, and it goes up on every repaint.
- Not fixed: the `Empty_<index>` branch of `ValidateName` can never run. The `IsNullOrWhiteSpace` check before it already catches
  every name that is empty after `Trim`. A comment now says so. Harmless.
- Not fixed: `boxedValue` is used on every repaint, see the gotchas above.

### `SortingLayerDrawer.cs`
- Added the missing behaviour to the summary: what the drawer shows for an unknown name and when it writes.

### `MID_BaseSOEditor.cs`
- Moved out of the file header: the note that the Project window thumbnail is handled by `MID_BaseSOProjectIconDrawer`. It is in the
  class summary now.
- History worth keeping: this editor used to call `EditorGUIUtility.SetIconForObject(target, icon)` to put a per-asset icon on
  each asset's thumbnail. See the entry below for why that was dropped.

### `MID_BaseSOProjectIconDrawer.cs`
- Moved out of the file header, now in the class summary and the section above:
  - The icon bug. Per-asset icons did not show on the individual assets. `SetIconForObject` is documented as scoped to a GameObject or a
    MonoScript, so it never worked reliably for ScriptableObject instances. Drawing the icon over each Project window item in
    `projectWindowItemOnGUI` replaced it.
  - The cache. The callback runs for every visible item on every repaint, and without a cache that was a `LoadAssetAtPath` and a
    `ResolveIcon` call per item per frame.
  - The "draws over, doesn't replace" bug. The callback runs after Unity has painted its own icon, and `GUI.DrawTexture` blends
    instead of clearing, so the old icon showed through letterboxed edges and transparent pixels. The fix paints an opaque
    background-coloured rectangle first.
  - The colours are the standard dark and light skin values with an approximate selection tint, not sampled from the real row. They
    can be adjusted if they look off in some Unity version or theme.
- Not fixed: the cache is not invalidated by undo or by changes made outside the inspector, see the gotchas above.

### `MidEditorUIElements.cs`
- Moved out of the file header and the section comments: the description of each of the four types. They are in the summaries now.
- Added summaries to the public members, which had none.
- Not fixed: `OrderBarElement.UpdateData` takes an `isMms` flag and a `ColMms` colour exists, and neither is used.
- Not fixed: `InfoPopupHandler` throws if the popup element exists without its two labels.

### `SceneDependencyInjector.cs`
- Moved out of the file header: the three usage steps and the note about `cleanupOnStop`. They are in the class summary and the section
  above. The header also had the typo "GameObjcet", which is gone with it.
- Added summaries to the public members, which had none.
- Not fixed: a prefab is treated as present when any one of its root components exists in the scene, see the gotchas. A stricter check
  (all of them, or by prefab identity) is a code change and the owner's decision.
- Not fixed: the log text says "Use Force Reinject to run again", but the menu item and the button are called "Force Reinject All".
  It is a string in the code, so it is unchanged.
- Not fixed: `OnDisable` only unsubscribes from `playModeStateChanged` when `Application.isPlaying` is true. If Unity reports false
  at that point (not checked) a subscription to a destroyed component stays until the next domain reload.
- Open: whether the component can be attached from an Editor-only assembly, see above. This is the most important thing to check
  before the README keeps telling users to add it.

### `MID_PackageUpdateChecker.cs`
- Moved out of the file header: the line saying to set `GITHUB_USER`, `GITHUB_REPO` and `PACKAGE_JSON_PATH`, and the line about the
  daily check. They are in the class summary now, with what is sent and what is stored.
- Fixed in the comments: `AutoCheck` said it is called once per Editor session. `[InitializeOnLoad]` runs on every domain reload, so
  it can run many times a session, and only the 24 hour rule on the last successful check limits it.
- Added a comment at `PACKAGE_JSON_PATH` that says what the path is relative to.
- Not fixed (release blocker, needs an owner decision), the three problems listed above:
  1. The installed version comes from a repository-relative path, so a git-installed project reads `0.0.0`.
  2. `releases/latest` is shared by every package in the repository, and the `<package>/vX.Y.Z` tag format does not parse as a version.
     Today this means the checker reports "up to date" whatever the installed version is.
  3. The shown install URL uses `#v<version>`, not `#utilities/v<version>`.
  Choices to consider: read the package's own version through `PackageInfo.FindForAssembly` instead of a file path, ask for the list
  of releases and pick the newest `utilities/v*` tag, build the URL with the real tag, add an EditorPref or setting to turn the
  automatic check off, and do not rerun after a failed check until a back-off has passed. Or ship the package without the
  checker. None of that has been done, because it changes behaviour.
- Not fixed: no way to turn the automatic check off, and a failed check is retried on every domain reload. GitHub allows 60
  anonymous requests an hour per IP address, and a second window request follows a silent one.
- Not fixed: the JSON is read with plain string search. `ExtractJsonString` takes the first match of the key, and the release notes
  only have `\n` and `\r\n` unescaped, so other escapes (for example `\"`) show as typed.
- Not fixed: a version with a prerelease suffix (`1.0.0-rc.1`) does not parse and counts as `0.0.0`.
- Not fixed: the cached result is restored when the window opens, but the release notes are not cached. They show only after a fresh
  check.
- Not documented in the README or the other docs before this pass: that this package makes an automatic network request. It has to
  go into the README before release.

### `MID_ScriptableObjectViewerWindow.cs`
- Moved out of the file header into the class summary: what the window does, why categories use the concrete class name, and why the
  detail panel uses `Editor.CreateEditor`.
- Moved out of the code, history of three fixes that are kept in the code as they were:
  - The category dropdown only showed after typing in the search box. Filling the dropdown inside `CreateGUI` raced the first layout
    pass. `Rescan` now runs after it, and the dropdown is also marked for repaint on every rescan.
  - The dropdown problem was made worse by binding hundreds of rows at once on the first frame. The list now binds one page of 50
    rows at a time.
  - Custom icons did not show. A `MID_BaseSO` icon is painted by the Project window drawer, which does not run in this list, so
    the row asks `ResolveIcon()` first and falls back to the thumbnail.
- Fixed in the comments: the icon comment said the reason is explained in `MID_BaseSOEditor`'s header. The explanation is in
  `MID_BaseSOProjectIconDrawer`, and the comment now says that.
- Not fixed: the scan loads every ScriptableObject in the project, packages included, with no filter for packages.
- Not fixed: the list does not follow asset changes after a scan.
