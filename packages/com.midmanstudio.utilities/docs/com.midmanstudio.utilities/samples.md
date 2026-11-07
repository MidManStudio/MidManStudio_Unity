# com.midmanstudio.utilities: Samples

Six samples in `Samples~/`, declared in `package.json`. In the Package Manager window, open the package, go to Samples and
press Import. Unity copies the folder to `Assets/Samples/MidMan Studio Utilities/<version>/<sample name>/`. Each sample is a
scene plus the scripts and assets it needs. The `~` in the folder name keeps Unity from importing them as part of the package.

| Sample (Package Manager name) | Folder | Scene | Scripts | Needs |
|---|---|---|---|---|
| Benchmarks | `BenchMarks` | `BenchMarks.unity` | none (uses the Tests scripts) | `com.midmanstudio.audio` for the audio bench in the scene |
| Auto Ref | `OtherStuff` | `OtherStuff.unity` | 4 | nothing else |
| PoolSystem | `PoolSystem` | `PoolSystemTest.unity` | 2 | nothing else |
| Ui State | `UiState` | `MenuStateTest.unity` | none | nothing else |
| Tick Test | `TickTest` | `TickTest.unity` | 1 | nothing else |
| Visual Effects | `VisualEffects` | `VisualAndAudio.unity` | 1 | `com.midmanstudio.audio`, or the project does not compile |

Which package components each scene uses, found by matching script GUIDs in the scene files:
- Benchmarks: `MID_TickDispatcherBench` and `TimerBenchmarkRunner` from this package (see [tests.md](tests.md)), and
  `MID_AudioBenchRunner` and `MID_NativeAudioBridge` from the audio package, plus `MID_StickyNote` labels. The scene does not
  contain `MID_TickDelayBenchRunner`. The `Audio/` folder has `Explosion.wav` and `GunShot.wav`.
- Auto Ref: `MID_AutoRef`, `AutoRefTest` and the three demo runners.
- PoolSystem: `LocalObjectPool`, `LocalParticlePool` and `Test_ProjectileSpawner`. `Test_Projectile` is on the
  `ProjectileVisual2D` prefab. The folder also has the `ProjectileImpact` prefab and two sprites (`Heart.png`,
  `Perfect_Star3.png`).
- Ui State: `MID_UIStateManager`, `MID_UIStateButton`, `MID_UIStateBackButton`, `MID_UIStateVisibility`, `MID_UIElement`, and a
  context provider asset `Config/Context/MenuStateContext.asset` (context name `Menu`, package id `com.manDman`, states
  MainMenu, Settings, Missions, Shop). See [uistate.md](uistate.md).
- Tick Test: `MID_TickTimerShowcase`.
- Visual Effects: `GlobalFXManager`, `LocalObjectPool`, `Test_VisualAndAudio`, `MID_NativeAudioBridge` (audio package), the
  `SpawnableAudio` prefab and the same two wave files.

Several scenes also use `MID_StickyNote` objects as on-screen notes (see [stickynote.md](stickynote.md)).

## Modules

### `AutoRefTest.cs`
**What it does:** An empty component for trying Auto Reference from the Inspector. It is in the global namespace and has three
fields: a private serialized `CapsuleCollider _collider`, a public `Rigidbody _rb` and a public `MeshCollider _mesh`.
`[MID_AutoRefable(true)]` opts it into resolving and makes the editor add a `MID_AutoRef` component next to it. Start and
Update are empty. See [autoreference.md](autoreference.md).

### `MID_AutoRefDemo_BasicMatch.cs`
**What it does:** Demo 1. Namespace `MidManStudio.Core.Tests.AutoReference`. The file holds three helper types and the runner:
- `MID_DemoIconSlot` and `MID_DemoLabelSlot`: empty components that stand in for UI elements. No UGUI or TextMeshPro is needed.
  All three demos use `MID_DemoLabelSlot`, and demo 3 uses `MID_DemoIconSlot`, so the three demo files must be imported together.
- `MID_DemoSimplePanel` (`[MID_AutoRefable]`): fields `_icon` and `_title`.
- `MID_AutoRefDemo_BasicMatch`: builds a root with the panel and two children named `Icon` and `Title`, adds a real `MID_AutoRef`,
  sets Include Children and calls `ResolveNow()`. It checks that both fields got the right child (2 checks).

**Running:** Every demo runner has Run On Start (default on) and a context menu entry "Run Test". Each prints PASS and FAIL lines
and a total to the Console, and leaves the objects it made in the scene.

### `MID_AutoRefDemo_AmbiguousMatch.cs`
**What it does:** Demo 2. A panel (`MID_DemoAmbiguousPanel`) has three `MID_DemoLabelSlot` fields: `_healthLabel`, `_manaLabel`,
`_staminaLabel`. The runner creates three children named `StaminaLabel`, `HealthLabel`, `ManaLabel` (not the field order), calls
`MID_AutoReferenceResolver.Resolve` with Include Children, and checks that each field got the child with the matching name
(3 checks). The point is that with several candidates of one type, the name decides.

### `MID_AutoRefDemo_ExternalAndOverwrite.cs`
**What it does:** Demo 3. The panel (`MID_DemoExternalPanel`) has `_externalIcon`, `_preAssigned` and `_manualOnly` (marked
`[MID_NoAutoRef]`). The runner puts the icon under a separate root, assigns `_preAssigned` by hand and creates a second label.
- Pass 1, Overwrite Existing off, external search root set: the icon is found outside the hierarchy, the hand-assigned field is kept,
  the `[MID_NoAutoRef]` field stays null (3 checks).
- Pass 2, Overwrite Existing on: the opt-out field is still null, and the pre-assigned field is not null (2 checks).

### `Test_Projectile.cs`
**What it does:** Sits on the `ProjectileVisual2D` prefab. In `OnEnable`, which runs every time the object leaves the pool, it clears
the Rigidbody2D velocity and adds an impulse along `transform.right` (forward in 2D) of strength `speed` (default 10). In
`OnTriggerEnter2D` it takes a `Projectile_Impact` particle from `LocalParticlePool` at its position and returns itself with
`poolReturn.ReturnToPoolNow()`. It does this only if both pools exist and the `collider` and `poolReturn` fields are set.
Global namespace.

### `Test_ProjectileSpawner.cs`
**What it does:** On the F key, spawns a `Projectile_Visual2D` from `LocalObjectPool` at `shotpoint` (position and rotation), at most
once per `firerate` seconds (default 1). Global namespace. Reads the old `Input` class.

### `MID_TickTimerShowcase.cs`
**What it does:** Keeps the tick dispatcher, `MID_TickDelay` and the timers running together. Namespace
`MidManStudio.Core.Tests.TickDispatcher`. Add it to a GameObject in its own scene and press Play.

| Part | Fields (defaults) | What happens |
|---|---|---|
| Dispatcher | fast `Tick_0_2`, slow `Tick_1`, summary `Tick_2` | Three subscriptions. The first two count ticks. The summary writes a line with the counters, pending delay count, stopwatch time, countdown progress and pulse value |
| Tick delay | one-shot 3 s, repeat every 1.5 s four times, forever every 5 s | One-shot fires once. The finite repeat counts to its total. The forever heartbeat logs until the object is destroyed |
| Timers | countdown 4 s, pulse 2 s | A `CountdownTimer` that restarts itself when done, a `StopwatchTimer`, and a `ValueInterpolationTimer` (0 to 1, ease in-out, ping-pong) that restarts when done |

The timers are ticked by hand in `Update`. Delegates are built once in `Awake` because the zero-allocation rule of `MID_TickDelay`
needs a cached delegate. `OnDestroy` unsubscribes the three callbacks and cancels the forever delay. The log level field defaults to
Verbose. See [tickdispatcher.md](tickdispatcher.md) and [timers.md](timers.md).

### `Test_VisualAndAudio.cs`
**What it does:** Test input for the Visual Effects scene, at the mouse position (screen to world, z set to 0), at most once per
`fireRate` seconds (default 0.2). Global namespace.
- F: `GlobalFXManager.TriggerEffect(MuzzleFlash, SmallMuzzle, position, zero, 20, -1)`, then a `SpawnableAudio` object from
  `LocalObjectPool` plays `regularAudio` with `PlayOneShot`.
- E: `TriggerEffect(Explosion, SmallExplosion, position, zero, 80, -1)`, then `MID_NativeAudioBridge.Instance.PlayClip(1)`.

It uses the main camera (cached in `Start`, error logged if missing) and the old `Input` class. It needs the audio package for
`MID_SpawnableAudio`, `MID_NativeAudioBridge` and the `MidManStudio.Core.Audio` namespace. See [fx.md](fx.md).

## Fixes and Problems

### Samples in general
- Not fixed: importing the Visual Effects sample into a project that does not have `com.midmanstudio.audio` gives compile errors
  from `Test_VisualAndAudio.cs`, and those errors block every script in the project, not only the sample. The sample description in
  `package.json` says it needs the audio package. Options such as an assembly definition with a version define, or splitting the
  audio part out, are the owner's decision.
- Not fixed: the samples are copied into the user's `Assets` folder. Four scripts (`AutoRefTest`, `Test_Projectile`,
  `Test_ProjectileSpawner`, `Test_VisualAndAudio`) are in the global namespace with short names that can clash with a
  user's own scripts.
- Not fixed: `Test_ProjectileSpawner` and `Test_VisualAndAudio` read `Input.GetKeyDown` and `Input.mousePosition`. With Active Input
  Handling set to "Input System Package (New)" they throw `InvalidOperationException` every frame. Not run.
- Not fixed: `Documentation~/index.md` and `Documentation~/scenesetup.md` tell the reader to import a "Setup Demo" sample. No sample
  of that name exists. The six samples are the ones in the table above. They are user-facing text, so changing them is for the
  owner to decide.
- Not fixed: `Documentation~/scenesetup.md` Scene 1 says to add `MID_TickDelayBenchRunner` and expects "MID_TickDelay = 0 B per call
  (green)". Both statements need a check, see [tests.md](tests.md) (file name mismatch and the 64 slot pool).
- Not fixed: the UiState sample's context provider `MenuStateContext.asset` has the package id `com.manDman`, which looks like a
  test name. See [uistate.md](uistate.md) for the generated `MenuUIState` enum that belongs to it.

### `MID_AutoRefDemo_BasicMatch.cs`, `MID_AutoRefDemo_AmbiguousMatch.cs`, `MID_AutoRefDemo_ExternalAndOverwrite.cs`
- Fixed (comments): the `SHOWCASE 1`, `2` and `3` file headers were folded into the class summaries.
- Not fixed: demo 3 checks "Pre-assigned field re-resolved (overwrite on)" with `panel.PreAssigned != null`. The field was assigned
  by hand before, so it is not null whether or not the resolver replaced it. The check can not fail for the case it names. It would
  need to compare against the replacement label (and the resolver's choice between the two labels would then matter).
- Not fixed: demo 2 and demo 3 use `MID_DemoLabelSlot` and `MID_DemoIconSlot` from the demo 1 file, so importing only some of
  the three files does not compile.
- Not fixed: each run creates new GameObjects and never destroys them, so running a demo twice (Start and then the context
  menu) leaves duplicates.

### `Test_Projectile.cs`
- Fixed (comments): a comment `FIX: Changed transform.forward (3D) to transform.right (2D Forward)` was replaced with a plain
  statement. History: the push first used the 3D forward axis (z), which a 2D force ignores, and now uses the local right
  axis.
- Not fixed: `OnTriggerEnter2D` reacts to every trigger collider, including other projectiles, the spawner and impact objects, and
  returns the projectile to the pool and spawns an impact each time. It is a sample, but a user who copies it needs a layer or tag
  check.
- Not fixed: `rb.velocity` is obsolete in Unity 6 (`linearVelocity`). The package targets 2022.3, where it is correct.

### `Test_ProjectileSpawner.cs`
- Not fixed: the cooldown starts on every F press, including when a pool is missing and nothing was fired.

### `MID_TickTimerShowcase.cs`
- Fixed (comments): the file header was folded into the class summary.
- Not fixed: three log messages are plain strings with `{...}` placeholders and no `$` prefix, so the Console shows the braces
  as text: `"✓ One-shot delay fired after {_oneShotDelay}s."`, `"✓ Finite repeat fired ({_finiteRepeatsFired}/{_finiteRepeatCount})."`
  and `"Countdown cycle {_countdownCycles} complete — restarting."`. This is a string (user-facing text) change, so it is left for the
  owner to decide.
- Not fixed: the inspector shows two `Timers` headers. The second one is above the log level field and probably should read
  "Logging".
- Not fixed: the summary line and the heartbeat use `Debug.Log` and `MID_Logger` with the log level field. The summary line ignores the
  level, so it prints even when the level is None.

### `Test_VisualAndAudio.cs`
- Not fixed: the FX calls pass `-1` as the last argument, which is `audioVolume`. The audio package clamps a volume to 0 to 1 in
  `PlayClip`, so the sound that `MID_FXAudioRelay` would play for the effect is silent. It looks deliberate, because the sample plays
  its own sound, but nothing in the file says so.
- Not fixed: the file has an unused `using UnityEngine.UIElements;`.
- Not fixed: `PlayClip(1)` plays clip index 1 of the bridge's clip list, set in the scene. The comment of `PlayClip` says index 1
  is the muzzle clip, while the sample uses it for the explosion. Which clip is at index 1 in the scene was not checked.
- Not fixed: the muzzle path only plays audio when `LocalObjectPool` exists, inside the `GlobalFXManager.HasInstance` check. Without a
  `GlobalFXManager` neither the flash nor the sound plays.
