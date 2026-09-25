# com.midmanstudio.utilities: Logging

## Modules

### `MID_LogLevel.cs`
**What it does:** The verbosity enum every logging call is gated on: `None`,
`Error`, `Info`, `Debug`, `Verbose`, in ascending order.

### `MID_Logger.cs`
**What it does:** Level-gated singleton logger (`MID_Logger.LogDebug` /
`LogInfo` / `LogWarning` / `LogError` / `LogException` / `LogVerbose`, plus
`*WithColor` variants). Every call takes the caller's own configured
`MID_LogLevel` as its first argument; `ShouldLog` compares it against the
message's severity to decide whether to write anything. In the editor, only
the level prefix (`[INFO]`, `[DEBUG]`, etc) is wrapped in a rich-text colour
tag; the message body is always plain text.

**Decisions:**
- `LogWarning`/`LogWarningWithColor` are gated at the `Info` threshold, not
  a dedicated `Warning` level, because `MID_LogLevel` doesn't have one. A
  warning is visible whenever `Info` or more verbose is configured, and
  hidden at `Error` or `None`. Documented on both methods and on
  `MID_LogLevel.Info` itself, rather than adding a new enum value, since
  that would be a breaking change to every serialized `MID_LogLevel` field
  across every package.

### `MID_LoggerSettings.cs`
**What it does:** ScriptableObject holding the project's default
`MID_LogLevel` (`DefaultLogLevel`). Created via
`MidManStudio/Utilities/Logger Settings`; read by `MID_LoggerEditorWindow`
and expected to live in a `Resources` folder for runtime access.

### `ExampleScript.cs`
**What it does:** Reference example showing the region-layout and
`MID_Logger` call-site convention other scripts in this codebase follow
(one `MID_LogLevel` field per component, logged lifecycle methods, a
try/catch around risky work using `LogException`). Not part of the runtime
API; nothing else in the package references it.

### `MID_LoggerEditorWindow.cs`
**What it does:** Editor window (`MidManStudio > Utilities > Logger
Manager`) for bulk-viewing and setting every `MID_LogLevel` field on every
MonoBehaviour in the open scene: search/filter, group by GameObject, select
a subset and apply a level to just that subset or to everything at once,
and export the current levels to the console as a report.

**Decisions:**
- Finds candidate fields by type (`FieldType == typeof(MID_LogLevel)`) via
  reflection, not by field name, so it works on any field regardless of
  what it's called, not just the `_logLevel` naming convention the rest of
  this codebase happens to use.
- Applies a new level through `SerializedObject` first (survives domain
  reloads and prefab saves, and integrates with `Undo`), falling back to
  direct reflection only if `SerializedObject.FindProperty` can't find the
  field. A mismatch between the two is logged as a warning rather than
  failing silently.

## Fixes and Problems

No bugs found in this part; this pass was documentation only (NOTICE
headers plus XML `<summary>` docs on the public surface of each file).
