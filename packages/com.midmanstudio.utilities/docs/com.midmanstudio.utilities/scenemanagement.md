# com.midmanstudio.utilities: SceneManagement

Scene loading by typed id. The runtime files are in `Runtime/SceneManagement/` (namespace
`MidManStudio.Core.SceneManagement`), the generator is in `Editor/SceneManagement/`. Two files in
`Runtime/SceneManagement/Generated/`, `SceneId.cs` and `SceneRegistry.cs`, are written by the generator and are not
documented one by one. `SceneId` is an enum whose values are build indices, with `None = -1`. `SceneRegistry` is a static
class with a name table and a network dependency table: `GetName`, `GetDependency` and `IsKnown`, each by `SceneId` or by
int (`IsKnown` takes an int).

The copies of these two files in the repository are not empty placeholders. They were generated from the author's own
project and contain two scenes, `MainMenu = 0` and `Lobby = 1`, from a provider with the package id `com.midDman`. Run the
generator in your own project to replace them.

How the parts fit: scene entries live in `SceneTypeProviderSO` assets and are turned into the two generated files.
`MID_SceneLoader` loads scenes with the plain Unity scene manager. The netcode package has a second loader,
`MID_NetworkSceneLoader`, that loads scenes through Netcode for GameObjects. Both implement `ISceneLoader`.
`MID_SceneTransitionController` is a base class for loading screens that talks to either loader through that interface.
A related editor component, `SceneDependencyInjector`, is described in [editor-helpers.md](editor-helpers.md).

## Modules

### `ISceneLoader.cs`
**What it does:** The contract of a scene loader. Scenes are addressed by the int value of a `SceneId`.
- State: `IsLoadingScene`, `CurrentLoadingSceneId` (-1 when idle), `IsSceneLoaded(int)`.
- Events, as settable `Action` properties: `OnLoadProgressChanged(float)`, `OnSceneLoadCompleted(int)`,
  `OnSceneLoadFailed(string)`.
- Calls: `LoadScene(int sceneId, SceneLoadType loadType = Single, short delayMs = 0)` and `UnloadScene(int)`.

**Who implements it:** `MID_SceneLoader` (this package) and `MID_NetworkSceneLoader` in
`com.midmanstudio.netcode` (namespace `MidManStudio.Netcode.SceneManagement`). Only `MID_SceneTransitionController` uses the
interface.

**Gotchas:** The events are properties of delegate type, not C# events. `+=` works, but any caller can also assign `null`
to the property or invoke it. `delayMs` is a `short`, so the longest delay is 32767 ms.

### `SceneLoadType.cs`
**What it does:** `Single` (standard Unity single-mode load, the previous scene is destroyed), `Additive` (previous scenes
stay) and `NetworkAdditive` (an additive load managed by Netcode for GameObjects and synced to all clients).

**Gotchas:** `MID_SceneLoader` does not know `NetworkAdditive`. It treats every value other than `Single` as additive. The
`NetworkAdditive` behaviour comes only from the netcode loader.

### `SceneNetworkDependency.cs`
**What it does:** What a scene needs to load: `None`, `InternetRequired`, `NetworkSessionRequired` and `Optional`. The value is
set per scene in the provider and baked into `SceneRegistry`.

**What reads it:** `MID_SceneLoader` checks `InternetRequired` (see below). `MID_SceneTransitionController` turns
`NetworkSessionRequired` into a `NetworkAdditive` load. Nothing reads `Optional`, so it behaves like `None`.

### `MID_SceneLoader.cs`
**What it does:** The non-network loader, a `Singleton<MID_SceneLoader>` that implements `ISceneLoader`. `Awake` makes it
persist across scenes. Besides the interface it has overloads that take a `SceneId` (`LoadScene`, `UnloadScene`,
`IsSceneLoaded`) and `GetActiveSceneId()`, which returns `SceneId.None` when the active build index is not in the registry.
The inspector fields are `_logLevel` (`Info`) and `_enforceInternetCheck` (true).

**How a load works:**
1. A second `LoadScene` while one is running is ignored with a warning. Loads are not queued.
2. An id that is not in `SceneRegistry` logs a warning, and the load is tried anyway by build index.
3. When the scene is marked `InternetRequired` and the check is enabled, it checks the connection. With no connection it
   calls `OnSceneLoadFailed`. The check uses `MID_NetworkConnectionManager.ConfirmConnectionAsync` from the netcode package
   when that assembly is loaded (found by reflection, assembly `MidManStudio.Netcode`), otherwise
   `Application.internetReachability`.
4. `delayMs` is awaited with `Task.Delay`.
5. The load runs with `allowSceneActivation = false`. Progress is reported about every 50 ms until Unity reports 0.9. Then it
   reports 1, allows activation, waits for the operation to finish and calls `OnSceneLoadCompleted`.
6. Any exception is logged and reported through `OnSceneLoadFailed`. The loading flag is cleared in every case.

**Decisions:**
- The utilities package has no hard reference to the netcode package. The connection manager and the network loader are
  both found by type name.
- Loading is `async void` with `Task.Delay` polling. Unity continues it on the main thread.

**Gotchas:**
- `UnloadScene` calls `SceneManager.UnloadSceneAsync` and logs "Unloading" without checking that the scene is loaded, and it
  does not report completion.
- The load progress it reports is the raw Unity value up to 0.9. Smoothing is the job of the transition controller.
- `OnDestroy` clears all three event properties.

### `MID_SceneTransitionController.cs`
**What it does:** An abstract `Singleton<MID_SceneTransitionController>` that needs a `Canvas` on the same object. Game code
derives from it. It connects to `MID_SceneLoader` and, when it exists, to the network loader, and runs the loading screen
around a load. `Awake` makes it persist across scenes.

**Inspector fields:** `_extraLoadingDelay` (0 seconds, extra time to wait after the load has started), `_loadingMessages`
(three strings, `Loading...`, `Preparing...`, `Almost ready...`), `_messageCycleInterval` (2 seconds) and `_logLevel`.

**Public API:**
- `LoadScene(SceneId, bool useTransition = true, short delayMs = 0)` picks the loader from the registry: a scene marked
  `NetworkSessionRequired` is loaded as `NetworkAdditive`, any other as `Single`.
- `LoadScene(SceneId, SceneLoadType, ...)` and `LoadScene(int, SceneLoadType, ...)` take the load type directly.
- `SetNetworkLoader(ISceneLoader)` connects a network loader by hand. Call it before the first network load.
- `EmergencyCleanup()` (also a context menu item) stops all coroutines and resets the state.
- State: `IsTransitioning`, `CurrentTargetScene` (`SceneId.None` when idle), `CurrentProgress`.
- Events: `OnSceneTransitionComplete(int)`, `OnSceneTransitionFailed(string)`, `OnSceneTransitionProgress(float)`.

**The hooks to override** (all virtual, all empty by default): `OnPreTransition`, `TransitionIn`, `OnLoadingStarted`,
`OnLoadingFinished`, `TransitionOut`, `OnPostTransition`, `OnTransitionError` (all coroutines), and the plain methods
`OnProgressUpdated(float)`, `OnLoadingMessageChanged(string)` and `OnTransitionComplete(int)`.

**Order with `useTransition` on:** `OnPreTransition`, `TransitionIn`, the `delayMs` wait, the load starts,
`OnLoadingStarted`, the message and progress loops start. When the loader reports completion for the target scene:
`OnLoadingFinished`, `TransitionOut`, `OnPostTransition`, state reset, `OnSceneTransitionComplete`. On failure:
`OnTransitionError`, a fixed wait of 2 seconds, state reset, `OnSceneTransitionFailed`.

**Decisions:**
- Game code supplies the animation. The base class owns only the order of the steps, the message cycling and the progress
  value, so it works with any tween library.
- Loaders are found once in `Start`, with the network loader found by reflection on the type
  `MidManStudio.Netcode.SceneManagement.MID_NetworkSceneLoader`. `SetNetworkLoader` is the explicit alternative.

**Gotchas:**
- Loaders that do not exist at `Start` are never connected later. There is no retry.
- Only one transition runs at a time. A second `LoadScene` is ignored with a warning until the first completes or fails,
  including the 2 second error wait.
- With `useTransition` off, the first steps are skipped but the completion steps still run: when the load finishes, the
  loading-finished, `TransitionOut` and post-transition hooks are called.
- `delayMs` is waited here, after `TransitionIn`. It is not passed on to the loader.
- `OnTransitionComplete` is not called, see Fixes and Problems.

### `SceneTypeProviderSO.cs`
**What it does:** The ScriptableObject that lists scenes for the generator (Create > MidManStudio > Utilities > Scene Type
Provider). Fields: `packageId` (default `com.mygame`, unique), `displayName`, `priority` (default 100, lower is processed
first) and `scenes`. The file also holds the serializable `SceneEntryDefinition` with `enumName` (the enum member, PascalCase),
`sceneName` (the exact name in Build Settings), `buildIndex` (default 0, becomes the enum value, unique over all
providers), `networkDependency` and `comment`. `SceneCount` is the list size.

**Decisions:** Build indices are entered by hand, not assigned. The id of a scene is its position in Build Settings, which
is what Unity's loading calls take.

**Gotchas:** The build index here must match File > Build Settings. Nothing checks that. If you reorder scenes in Build
Settings, update the provider.

### `SceneTypeGenerator.cs` (Editor)
**What it does:** `SceneTypeGeneratorCore.Generate()` reads every `SceneTypeProviderSO` asset, checks them and writes
`SceneId.cs` and `SceneRegistry.cs`. The window `SceneTypeGeneratorWindow` (MidManStudio > Utilities > Scene Type Generator)
lists the providers and has a Generate Now button that shows warnings and errors.

**What it checks:** Duplicate package ids, duplicate build indices, duplicate enum names, an enum name that is not a C#
identifier. A scene with a blank enum name is skipped with a warning, and a blank scene name gives a warning and an empty
string in the registry. With any error nothing is written.

**What it writes:** Providers are sorted by priority, then package id. `SceneId` gets `None = -1` and then one member per
scene. `SceneRegistry` gets two dictionaries (build index to scene name, build index to network dependency) with an entry
for -1.

**Gotchas:**
- The two output paths are constants in the code (`packages/com.midmanstudio.utilities/Runtime/SceneManagement/Generated/...`),
  relative to the project root, and there is no settings asset. In a project that installs the package from git these folders
  do not exist, so the files are written next to `Assets` and Unity does not compile them. The generator works as intended in
  the layout of the MidManStudio_Unity repository. Found by reading the code, not run in Unity.
- Each run rewrites both files because the header has a `Generated:` timestamp.
- The scene name registry is used by the netcode loader (`SceneRegistry.GetName`), so a wrong `sceneName` breaks network
  loads even when the build index is right.

## Fixes and Problems

### `ISceneLoader.cs`
- Fixed in the comments: the event summary said progress fires every frame. `MID_SceneLoader` reports about every 50 ms.
- Moved out of the file header: the list of implementers and the reason for the interface (no hard dependency on either
  loader). Both are in the section above.

### `MID_SceneLoader.cs`
- Moved out of the file header: setup, the usage example and the note about the internet check. They are in the section
  above and in the class summary.
- Not fixed: `Task.Delay` is used for the start delay and for polling. Unity WebGL has no thread timers, and `Task.Delay`
  is generally reported as not completing there. This was not tested here. A WebGL build would hang at the first await.
- Not fixed: `UnloadScene` reports nothing on success or failure, and logs "Unloading" even for a scene that is not loaded.

### `MID_SceneTransitionController.cs`
- Moved out of the class comment: the subclassing example and the usage lines, now in the class summary.
- Not fixed: the hook `OnTransitionComplete(int)` is never called. The base class only invokes the
  `OnSceneTransitionComplete` event. The old class comment showed an override of the hook, which does nothing. The comment
  on the hook now says so, and the class example no longer uses it.
- Not fixed: progress smoothing uses `Time.deltaTime` inside a loop that runs every 0.05 seconds, so each step is
  `2 * deltaTime` and the speed is about 0.67 per second at 60 fps, not 2 per second. Also, when the load completes the
  progress loop is stopped at once, so `OnProgressUpdated` can finish below 1. Found by reading the code, not run.
- Not fixed: a failure from any connected loader starts the error flow, even when no transition of this controller is
  running, and then resets the state.
- Not fixed: if a load finishes while `OnLoadingStarted` is still running (a very small scene and a long spinner
  animation), the completion steps can start before it ends, and the hooks overlap.
- Not fixed: `useTransition = false` still runs the completion hooks, see the gotchas above.

### `SceneTypeGenerator.cs`
- Not fixed: the output paths are fixed in the code and assume the repository layout.
- Not fixed: a scene with `buildIndex = -1` passes the checks. The generated registry would then have two entries for -1 and
  throw when its static dictionaries are built. Duplicate build index checks also count entries with a blank enum name.
- Not fixed: the scene name and the comment are written into the generated code without escaping. A quote or a backslash in
  a scene name, or a line break in a comment, gives a file that does not compile.
- Not fixed: numbering is not locked. This is not a problem for the numbers themselves, because they are the build indices
  you enter, but there is no check against the real Build Settings list.

### `SceneNetworkDependency.cs`, `SceneLoadType.cs`, `SceneTypeProviderSO.cs`
- Moved out of the file headers: the one-line descriptions and the menu path. The code is unchanged.
- Not fixed: `SceneNetworkDependency.Optional` is read by no code.
