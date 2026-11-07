# com.midmanstudio.utilities: Auto Reference

Auto Reference fills the Component, GameObject and interface fields of your scripts for you, so you do not drag each
reference by hand. The runtime part is in `Runtime/AutoReference/` (namespace `MidManStudio.Core.AutoReference`, assembly
`MidManStudio.Utilities`). The Edit Mode tools are in `Editor/AutoReference/` (namespace
`MidManStudio.Core.EditorUtils.AutoReference`, editor assembly). Two UI Toolkit assets sit next to the editor scripts:
`MID_AutoReferenceWindow.uxml` and the two `.uss` files. They are not code and are not documented one by one.

The pieces fit together like this: a script opts in with `[MID_AutoRefable]`. A `MID_AutoRef` component on the same
GameObject holds the settings. `MID_AutoReferenceResolver` does the work, and `MID_NameMatcher` picks between several
candidates. The editor adds a watcher, an inspector and a bulk window.

## Modules

### `MID_AutoRefableAttribute.cs`
**What it does:** A class attribute (`MID_AutoRefableAttribute`, used as `[MID_AutoRefable]`). A script without it is
skipped by the resolver. The attribute is inherited, so a subclass of an opted-in script is opted in too. The constructor
takes `autoAddComponent` (default false), which is exposed as `AutoAddComponent`.

**How to use it:**
```csharp
[MID_AutoRefable]                          // the script is scanned when a MID_AutoRef resolves
public class HealthBarView : MonoBehaviour
{
    [SerializeField] private Image _fill;  // filled from a child
    [SerializeField] private TMP_Text _label;
}
```
With `[MID_AutoRefable(autoAddComponent: true)]` the editor also adds `MID_AutoRef` for you when the script is added.

### `MID_NoAutoRefAttribute.cs`
**What it does:** A field attribute. A field with `[MID_NoAutoRef]` is never touched by the resolver, even when its type
and name would match. Use it for fields that other code assigns, or for pooled references.

### `MID_AutoRefOptions.cs`
**What it does:** The serializable settings class `MID_AutoRefOptions` and the enum `MID_AutoRefRunMode`. Defaults:

| Field | Default | Meaning |
|---|---|---|
| `includeChildren` | true | Search this object and all children (depth first). When false, only this object. |
| `includeInactiveChildren` | true | Include inactive children in that search. |
| `includeExternalRoot` | false | Also search a second hierarchy. |
| `externalSearchRoot` | none | The root for that second search. Inactive objects are always included there. |
| `overwriteExisting` | false | When false a field that already has a value is left alone. |
| `runMode` | `Manual` | When `MID_AutoRef` resolves by itself. |
| `logUnresolved` | true | Warn when a field has no candidate. |
| `logAmbiguousResolved` | true | Log a line when 2 or more candidates were found and one was picked. |

Run modes: `Manual` (only by context menu, inspector button, window or code), `Awake` and `Start` (Play Mode and builds),
`OnValidate` (Edit Mode only, no button needed).

**Gotchas:** A `MID_AutoRef` component keeps its own options. The bulk window keeps a separate set that is not saved and
starts from the defaults every time the window opens.

### `MID_NameMatcher.cs`
**What it does:** `MID_NameMatcher.Score(fieldName, candidateName)` returns 0 to 1. Plain C#, no dependency. It is used
only when a field has more than one candidate. The candidate name is the name of the candidate's GameObject, not the
component type.

**How the score works:**
- Both names are normalised: `(Clone)` removed, lowercase, letters and digits only. If the two are equal the score is 1.
- Otherwise `score = 0.45 * levenshtein + 0.35 * token_jaccard + 0.25 bonus`, clamped to 0..1. The three parts:
  - Levenshtein similarity is `1 - distance / longer length` on the normalised names.
  - Token Jaccard is the overlap of word sets. Words split on camelCase, `_`, space and `-`. A leading `m` word is dropped
    (`m_title` and `title` match). The sets are built from the original names, not the normalised ones.
  - The bonus of 0.25 applies when one normalised name contains the other.
- Either name empty gives 0.

Worked examples (calculated by hand from the code): field `titleText` against an object named `Title` scores about 0.68.
Field `healthBar` against `HealthBarFill` scores about 0.79. Field `healthBar` against `HealthBar` scores 1.

**Gotchas:** The weights add up to 1.05, so a name that scores high on all three parts is clamped. The `m` prefix is dropped
only for the word match. In the Levenshtein part `m_title` is normalised to `mtitle`.

### `MID_AutoReferenceResolver.cs`
**What it does:** The static core, `MID_AutoReferenceResolver`. It has two public methods and two result types.
- `Resolve(GameObject target, MID_AutoRefOptions options)` returns a `List<MID_AutoRefFieldResult>`. It goes over every
  MonoBehaviour on `target`, skips missing scripts and `MID_AutoRef` itself, skips types without `[MID_AutoRefable]`, and
  resolves the eligible fields of the rest. It returns an empty list when `target` or `options` is null.
- `IsAutoRefable(Type)` is true when the type or a base type has `[MID_AutoRefable]`.
- `MID_AutoRefFieldResult` is a read-only struct with the script name, field name, field type name, the outcome, the
  candidate count, the score (1 for a single candidate) and the name of the assigned object.
- `MID_AutoRefOutcome` is `Assigned`, `SkippedAlreadySet`, `NoCandidates` or `AmbiguousResolved`.

**Which fields are eligible:** Fields declared in the script or in its base classes (up to `MonoBehaviour`) that are public
or have `[SerializeField]`, are not static, readonly or const, do not have `[NonSerialized]` or `[MID_NoAutoRef]`, and whose
type is a Component type, `GameObject` or an interface. Arrays and lists are not eligible. Other `UnityEngine.Object` types
such as `ScriptableObject` or `Sprite` are not eligible either.

**How a field is resolved:**
1. If the field already holds a Unity object and `overwriteExisting` is false, the outcome is `SkippedAlreadySet`.
2. Candidates are collected: for `GameObject` fields every object in the scan, for other types every component of that type.
   The scan covers the target and its children (or only the target when `includeChildren` is false), then the external
   root when enabled. Duplicates are removed by instance id. The target itself is a candidate.
3. No candidate: `NoCandidates`, plus a warning when `logUnresolved` is on.
4. One candidate: assigned with score 1. Several: `PickBestMatch` scores each by `MID_NameMatcher` and takes the highest.
   On a tie the first one found wins (the target first, then children in depth first order, then the external root).
   The outcome is `AmbiguousResolved`, plus an info line when `logAmbiguousResolved` is on.
5. In Edit Mode the assignment is recorded with `Undo.RecordObject` and the object is marked dirty. In Play Mode neither is done.

**Decisions:**
- The attribute is on the class, not on each field, so a script opts in once and individual fields opt out.
- The class lives in the runtime assembly, with the Undo and dirty calls inside `#if UNITY_EDITOR`, so it works in builds.
- In Edit Mode the warnings go to `Debug.Log` and `Debug.LogWarning` with the prefix `[AutoRef]`. In Play Mode they go to
  `MID_Logger`. This keeps a scan in Edit Mode from creating the logger singleton. In Play Mode both messages are logged at
  `MID_LogLevel.Info`. See [logging.md](logging.md).

**Gotchas:**
- Interface fields: Unity does not serialize an interface field marked only with `[SerializeField]`, so we expect an
  assignment made in Edit Mode to be lost when the scene is saved or scripts reload. Run time modes are not affected. Not
  verified in Unity.
- An interface field that holds a plain C# object (not a Unity object) counts as empty and is overwritten.
- The target is a candidate, so a `Transform` or `GameObject` field can resolve to the object itself.
- Cost: reflection plus one `GetComponentsInChildren` call per field. Fine for a few objects, so avoid `Awake` mode on
  thousands of objects.

### `MID_AutoRef.cs`
**What it does:** The component, `MID_AutoRef` (`[DisallowMultipleComponent]`). It holds one `MID_AutoRefOptions` (property
`Options`) and calls the resolver. `ResolveNow()` is public and is also a context menu entry named "Resolve Now".

**Run modes:** `Awake` and `Start` call `ResolveNow()` in that Unity message. `OnValidate` (Edit Mode only) queues a resolve
with `EditorApplication.delayCall`, because Unity does not allow some operations, such as adding a component, inside
`OnValidate`. The queued call checks that the component still exists. Nothing is queued in Play Mode.

**How to use it:** Add `MID_AutoRef` to a GameObject that has an opted-in script, press Resolve Now, and read the summary
in the inspector. Or call it from code: `GetComponent<MID_AutoRef>().ResolveNow();`.

**Gotchas:**
- In `Awake` mode the order against other scripts is not defined. A script that reads its own fields in its `Awake` can run
  before `MID_AutoRef.Awake` has filled them. Use `Start`, `OnValidate` or Manual (then save the scene), or set the Script
  Execution Order.
- Every change to the component's inspector values queues another resolve in `OnValidate` mode. With `overwriteExisting`
  off this is harmless, because a second run skips fields that are already set.

### `MID_AutoRefComponentWatcher.cs` (Editor)
**What it does:** An `[InitializeOnLoad]` internal static class. It subscribes to `ObjectFactory.componentWasAdded`. When a
component with `[MID_AutoRefable]` is added: if the GameObject has no `MID_AutoRef` and the attribute has
`autoAddComponent` true, it adds one with `Undo.AddComponent`. If the `MID_AutoRef` (new or existing) has run mode
`OnValidate`, it queues a resolve with `delayCall`. A new `MID_AutoRef` starts in `Manual` mode, so in practice the resolve
happens when a component with `OnValidate` already exists.

**Decisions:** It reacts to the add event, so nothing scans scenes. It also checks for an existing component, and the
`[DisallowMultipleComponent]` on `MID_AutoRef` is a second guard.

### `MID_AutoRefEditor.cs` (Editor)
**What it does:** The inspector for `MID_AutoRef`, built with UI Toolkit (`CreateInspectorGUI`). It draws the default fields,
a RESOLVE NOW button and a summary line: `N assigned · N ambiguous · N unresolved · N already set`. The line gets the style
class `mid-autoref-summary--warn` when something is unresolved. The stylesheet `MID_AutoRefEditor.uss` is found by name with
`MidEditorUIHelpers.FindUss`. The same file holds the internal class `GradientResolveButton`: a green to orange gradient
painted with `generateVisualContent` and a separate label.

**Decisions:** The button is a plain `VisualElement` with a `Clickable` manipulator and not a `Button` subclass. The old
file header said a `Button` subclass broke the text, and the reason is not written down anywhere (see below).

### `MID_AutoReferenceWindow.cs` (Editor)
**What it does:** The window `MID_AutoReferenceWindow`, opened with MidManStudio > Utilities > Auto Reference (menu priority
118, minimum size 600 x 480). It finds every GameObject with an opted-in script in the loaded scenes, lists them (a filled
dot when `MID_AutoRef` is present, an empty one when not), and has these actions:
- Refresh and Scan: rescan the loaded scenes.
- Add Missing: adds `MID_AutoRef` with Undo to every listed object that lacks it. The toggle "auto add on scan" (off by
  default) does this after each scan.
- Resolve Selected and Resolve All: resolve with the window's own options, then write a log: `✓` assigned, `~` ambiguous with
  candidate count and score, `✗` no match. "Already set" fields are counted but not logged.
- Selecting rows selects and pings the objects in the scene.

**Gotchas:**
- Only loaded scenes are scanned. Prefab assets and prefab mode are not.
- The window scans once when it opens. A script change or a new object needs Refresh.
- The window builds its UI from `MID_AutoReferenceWindow.uxml` and shows a warning label when that file is not found.

## Fixes and Problems

### `MID_AutoRef.cs`, `MID_AutoRefOptions.cs`, `MID_AutoRefableAttribute.cs`, `MID_NoAutoRefAttribute.cs`
- Moved out of the file headers: the usage notes for the component, the options, the two attributes and the note on the
  `OnValidate` run mode. They are in the sections above and in the XML summaries. The code is unchanged.
- Not fixed: with `Awake` run mode the order against the scripts that use the fields is not defined, see the gotcha above.

### `MID_AutoReferenceResolver.cs`
- Fixed in the comments: the class comment said the Undo, dirtying and logging code sits inside `#if UNITY_EDITOR`. Only
  Undo and dirtying do. The logging choice is made with `Application.isPlaying` at run time. The comment now says that.
- Not fixed: interface fields are probably not saved, see the gotcha above.
- Not fixed: when two candidates have the same best score the first one is picked, and the result does not say that it
  was a tie.

### `MID_NameMatcher.cs`
- Moved out of the file header: the description of the three parts of the score. It is in the section above. The code is
  unchanged.
- Not fixed: the `m` prefix is dropped in the word match but not in the Levenshtein part.

### `MID_AutoRefEditor.cs`
- The class header ended in the middle of a sentence ("painted with") and the button summary pointed to a file header for
  the reason a `Button` subclass broke the text. That reason was never written in the file. Both comments now only say what
  the code does. The reason is unknown, so the button stays a plain `VisualElement`.

### `MID_AutoRefComponentWatcher.cs`, `MID_AutoReferenceWindow.cs`
- Moved out of the file headers: what the watcher and the window do, and the menu path. They are in the sections above and
  in the class summaries. The code is unchanged.
- Not fixed: the window does not scan prefab assets or unloaded scenes.
