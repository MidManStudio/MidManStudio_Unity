# com.midmanstudio.utilities

- **Auto Reference system** — attribute-driven auto-wiring of Component/GameObject/interface
  fields on MonoBehaviours. Scans self + children (+ optional external search root) and
  disambiguates multi-candidate fields via a fuzzy name-match scorer (Levenshtein + token
  Jaccard + substring bonus, no external dependency). Full reference: [APICATALOG.md §19](./APICATALOG.md#19-auto-reference).
  - `[MID_AutoRefable(autoAddComponent: false)]` — class attribute opting a script into scanning.
  - `[MID_NoAutoRef]` — field-level opt-out.
  - `MID_AutoRef` — runtime component; run modes `Manual` / `Awake` / `Start` / `OnValidate`.
  - `MID_AutoReferenceResolver` — static core resolver, callable standalone.
  - `MID_AutoRefComponentWatcher` — editor-only watcher that auto-adds `MID_AutoRef` when an
    `autoAddComponent: true` script is added to a GameObject. Duplicate-safe: existence check +
    `[DisallowMultipleComponent]` backstop.
  - `MidManStudio > Utilities > Auto Reference` — bulk scan/resolve window with an
    "Auto-Add Missing Components on Scan" toggle (off by default).
  - Custom inspector for `MID_AutoRef` with a one-click "Resolve Now" button.

- **Tick Dispatcher** — shared interval dispatcher replacing per-MonoBehaviour `Update()`.
  Nine tick rates from `Tick_0_01` (100/sec) to `Tick_5` (0.2/sec).
- **Tick Delay** — zero-allocation delayed and repeating actions built on the Tick Dispatcher.
  `After` / `Repeat` / `RepeatForever` / `Cancel` / `CancelAll`, pool-based, cancellable handles.
- **Logger** (`MID_Logger`) — leveled logging singleton.
- **Singletons** — MonoSingleton base classes.
- **Observable Values** — change-notifying value wrappers.
- **Events** — lightweight event bus.
- **Pool System** — generic object/particle pools, trail renderer pool, enum-refactored pool
  type registration.
- **Audio** — audio manager and playback pooling.
- **FX System** — effect playback/pooling utilities.
- **Timers** — `CountdownTimer`, `StopwatchTimer`, `ValueInterpolationTimer` (linear/eased/
  custom-curve, ping-pong support).
- **Library System** — data library / registry pattern with generator tooling.
- **Scene Management** — `SceneDependencyInjector` and related scene-load utilities.
- **UI State System** — state-driven UI panel management.
- **UI Components** — reusable UGUI components.
- **Helper Functions** (`MID_HelperFunctions`) — general-purpose static helpers.
- **Sequential Processing** — ordered async/coroutine step execution.
- **Sticky Note** (`MID_StickyNote`) — UGUI-based, edit-mode-visible scene note overlay with
  theming, anchoring, minimize/close, and drag support (Play Mode).
- **Editor Tools** — Script Utilities Window, Script Execution Order Window, Sprite-to-Tilemap
  Converter, and supporting shared UI Toolkit helpers (`MidEditorUIHelpers`,
  `GradientBannerElement`).
