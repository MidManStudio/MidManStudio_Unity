# com.midmanstudio.utilities: Tests and Benchmarks

Everything under `Tests/`. There are three runtime benchmark components with Editor windows, one Editor test file, and two
assembly definitions. None of the runtime files use NUnit. They are run by hand: add the component to a scene, enter Play
Mode and read the Console or the window. Nothing here runs in the Unity Test Runner except the Editor file, and nothing is
wired to CI.

| What | Where | How to run |
|---|---|---|
| Tick Delay Bench (window + `MID_TickDelayBenchRunner`) | `Tests/Runtime/TickDispatcher/MID_TickDelayBenchmark.cs` | `MidManStudio > Utilities > Tests > Tick Delay Bench` (priority 120), Play Mode |
| Tick Dispatcher Bench (window + `MID_TickDispatcherBench`) | `Tests/Runtime/TickDispatcher/MID_TickDispatcherBench.cs` | `MidManStudio > Utilities > Tests > Tick Dispatcher Bench` (priority 121) |
| Timer test suite (`TimerBenchmarkRunner`) | `Tests/Runtime/Timers/TimerBenchmarkRunner.cs` | Add to a GameObject, enter Play Mode |
| Enum block logic tests (NUnit) | `Tests/Editor/EnumBlockLogicTests.cs` | Test Runner, EditMode. Documented in [enum-generators.md](enum-generators.md) |

The audio package has its own bench window, `MidManStudio > Audio > Tests > Audio Bench`. It is not in this package.

## Assemblies

| Assembly | Folder | Settings that matter |
|---|---|---|
| `MidManStudio.Utilities.Tests` | `Tests/Runtime` | Root namespace `BenchTests`. References `MidManStudio.Utilities`, `UnityEngine.TestRunner`, `UnityEditor.TestRunner`. All platforms, no define constraints, not auto-referenced, unsafe code allowed |
| `MidManStudio.Utilities.Tests.Editor` | `Tests/Editor` | Editor only. Needs the `UNITY_INCLUDE_TESTS` define. References the runtime package assembly, the Editor assembly, Burst and Collections |

The Editor test assembly only compiles when the package is listed under `testables` in the project's `Packages/manifest.json`
(Unity's rule for packages that are not embedded in the project). Without that the tests do not show up in the Test Runner.

"Not auto-referenced" on the runtime test assembly only stops other assemblies from referencing it. The benchmark components
can still be added to GameObjects. Scripts in it can not be reached from `Assembly-CSharp` code by name.

## Modules

### `MID_TickDispatcherBench.cs`
**What it is for:** Comparing the cost of calling subscribers on a tick schedule against calling them every frame, for a
chosen number of subscribers.

**What it actually measures:** It never calls `MID_TickDispatcher`. The code uses only the `TickRate` enum and `MID_Logger`
from the package.
- The "Update sim" side calls `SimulatedUpdateBehaviour.UpdateEmpty` or `UpdateHeavy` on an array of plain objects.
- The "Managed" side calls an array of `Action<float>` delegates (single-rate and heavy), or runs a small hand-written
  timer loop (multi-rate).

So the "dispatcher N× faster than Update sim" line in the Console and window compares a delegate loop with a method loop. It
says nothing about the real dispatcher. Read the numbers as a rough cost of the two call styles, not as a dispatcher result.

**Component `MID_TickDispatcherBench`** (add to a GameObject):

| Field | Default | Meaning |
|---|---|---|
| `SubCounts` | 1, 10, 50, 100, 200 | Subscriber counts for the single-rate scenario |
| `BenchRate` | `Tick_0_1` | Rate whose interval is passed to the callbacks |
| `Iterations` | 5000 | Timed passes per scenario. Each pass is timed with its own stopwatch run |
| `MultiSubsPerRate` | 20 | Subscribers per rate in the multi-rate scenario, capped at 10 in the code |
| `MultiRates` | 0.1, 0.2, 0.5, 1, 2 | Rates used together in the multi-rate scenario |

**Scenarios:**
- Single-rate (`RunSingle`): one result per entry of `SubCounts`. Both sides run all subscribers on every pass. The interval
  is only passed as an argument, so it has no effect on how often anything runs.
- Multi-rate (`RunMulti`): one simulated second in 16 ms steps, which is 62 steps. The Update side calls every subscriber on
  every step. The Managed side keeps one timer per rate and calls that rate's subscribers only when the timer runs out. Over
  one second the 2 s rate never fires.
- Heavy (`RunHeavy`): 5 subscribers, each doing 50 sine and cosine steps.
- `RunAll` runs the three and writes a summary to the Console with `MID_Logger` at the component's log level.

Results are shown as average, minimum, maximum and standard deviation in milliseconds per pass. A result with an average of 0
shows as "not run".

**Window `MID_TickDispatcherBenchWindow`:** Finds the component in the open scene or takes one in its object field. Toolbar
buttons Run All and Clear, a Run button per section, and foldouts with bars. It does not check for Play Mode. If no component
is found it says to add one. It repaints on every editor update while open.

**A note from the old file header:** the native (Burst) tick dispatcher was removed because the managed one was faster below
about 500 Burst subscribers doing heavy maths. That is the author's note, and the benchmark does not show it.

### `MID_TickDelayBenchmark.cs`
**What it is for:** Comparing `MID_TickDelay`, a coroutine and `Task.Delay` for allocation per scheduling call and for timing
accuracy. The pitch of the window is zero allocation and main-thread safety for `MID_TickDelay`, at the price of up to one
tick interval of timing error.

**Types in the file:** `DelayBenchGCResult`, `DelayBenchTimingResult` (serializable result structs), `TimingAccumulator`
(internal), the component `MID_TickDelayBenchRunner`, and the window `MID_TickDelayBenchWindow` (inside `#if UNITY_EDITOR`).

**Component `MID_TickDelayBenchRunner`** (add to a GameObject, or use the window's Add Runner to Scene button):

| Field | Default | Window range | Meaning |
|---|---|---|---|
| `DelaySeconds` | 0.5 | 0.1 to 5 | Requested delay for every method |
| `Rate` | `Tick_0_1` | any `TickRate` | Rate passed to `MID_TickDelay` |
| `GCIterations` | 500 | 50 to 1000 | Scheduling calls per method in the allocation test |
| `TimingIterations` | 30 | 10 to 100 | Delays per method in the timing test |
| `WarmupCount` | 20 | 10 to 50 | Delays scheduled during warm-up |

Methods: `RunAll`, `RunGCOnly`, `RunTimingOnly`, `Cancel`. A run does nothing if another is still running. Every run starts
with a warm-up (schedules `WarmupCount` delays, waits, calls `MID_TickDelay.CancelAll()`, and starts five throw-away
coroutines).

**Allocation test:** For each method it forces a full GC (collect, wait for finalizers, collect), reads
`GC.GetAllocatedBytesForCurrentThread()`, makes `GCIterations` calls, reads it again and divides by the count.
- MID_TickDelay: `MID_TickDelay.After(DelaySeconds, cachedAction, Rate)`. The action is a static delegate created once, so the
  call itself should allocate nothing.
- Coroutine: `StartCoroutine(DummyWait(DelaySeconds))`.
- Task: `Task.Delay(TimeSpan)` with the result discarded.

The first run in a Play session counts as the cold run (`RunCount == 1`). Later runs are marked warm. The file comments say
that .NET and Mono reuse internal objects, so Coroutine and Task numbers can fall or show 0 B on warm runs. If every method
shows 0 B the window warns that this can be an artefact of the counter, and says the Profiler's GC.Alloc column is the real
proof. If the API throws, the code falls back to `GC.GetTotalMemory(false)`, which is not per thread.

**Timing test:** For each method it starts one delay per frame, `TimingIterations` times, and compares the measured time with
the requested time as an absolute error.
- MID_TickDelay: callback records `Stopwatch.GetTimestamp()` against the start.
- Coroutine: `WaitForSeconds(DelaySeconds)`, then the same record.
- Task: `Task.Delay(...).ContinueWith(...)`. The callback runs on a thread pool thread, so it uses `Stopwatch.GetTimestamp()`
  and `Interlocked`, not Unity's clock.

The result shows the average and maximum error and how many callbacks fired out of the total. The Task row gets a warning if
fewer than all fired.

**Window `MID_TickDelayBenchWindow`:** Needs Play Mode and a runner in the scene. It has a "Why MID_TickDelay" foldout,
configuration sliders (disabled while running), Run All, GC Only, Timing Only and Cancel, a progress bar, and the allocation
and timing results with bars. All the explanatory text in the window is fixed wording. It is not computed from the results.
The "not found" help box mentions a past error, `DelayBenchGCResult missing ExtensionOfNativeClass`, which means a scene
object with a broken script reference.

**Scene set-up:** `Documentation~/scenesetup.md` Scene 1 tells users to add this component and `MID_TickDispatcherBench`
and open both windows.

### `TimerBenchmarkRunner.cs`
**What it is for:** A hand-run test suite for `Timer.cs` and `PerformanceBenchmarkTimer.cs` (see [timers.md](timers.md)).
It prints a PASS or FAIL line per check to the Console, ends with the totals, and writes a Console error if anything
failed. There are 76 checks.

**Settings:** `Run On Start` (default on, so adding the component to a scene runs the suite as soon as Play Mode starts),
`Timing Tolerance` (default 0.06 s), `Bench Iterations` (default 2000) and `Bench Warmup` (default 100). The component's
context menu has "Run All Tests".

**Checks by timer:**
- `CountdownTimer`: start event, running state, progress at start and end, completion event, `Reset()`, `Reset(newTime)`,
  pause and resume, stop does not complete.
- `StopwatchTimer`: accumulates, pause, reset keeps running, stop.
- `NetworkTimer`: tick rate conversion, one tick per interval, several ticks in one frame, `lerpFraction`, reset, a zero
  rate falls back to 60 Hz.
- `ValueInterpolationTimer`: linear 0 to 1 with events, linear midpoint, `EaseIn` midpoint (2.5 of 10), `EaseOut` midpoint
  (7.5 of 10), `Reconfigure`, stop.
- `SteppedValueTimer`: ascending in 4 steps with events, descending, reset.
- `PerformanceBenchmarkTimer`: exact iteration count, timing invariants, exception counting, a work benchmark,
  `QuickBenchmark`, `QuickCompare`, and `ToCSV()` having 9 columns.

The expected values were traced by hand against `Timer.cs` and `PerformanceBenchmarkTimer.cs` and match the current code,
with the exception noted below for `ToCSV()` on some machines.

**Not covered:** `StartPingPong`, `EaseInOut`, `Custom` curves, `TimerFactory`, `NetworkTimer.SetTickRate`, the `OnTimerStop`
event, the `Func` and `Action<T>` overloads of `RunBenchmark`, the null-action and zero-iteration guards, and
`PerformanceBenchmarkRunner`.

## Fixes and Problems

### Assemblies and layout
- Not fixed: `MidManStudio.Utilities.Tests` has no `UNITY_INCLUDE_TESTS` define constraint, unlike the Editor test
  assembly. Reading the asmdef, it is compiled in every project that has the package, and it references
  `UnityEngine.TestRunner`, which comes from the Test Framework package. This package's `package.json` does not list that
  package. Not tested in a project without the Test Framework package or in a player build.
- Not fixed: the runtime test assembly lists `nunit.framework.dll` under precompiled references with
  `overrideReferences` off. By Unity's asmdef rules the list is not used. No file in the assembly uses NUnit.
- Orphan to delete by hand: `Tests/Runtime/AutoRef.meta` has no folder next to it. Unity reports a `.meta` file with no
  asset.
- Fixed in the README (release pass): the README listed an "Audio Benchmark" row under `MidManStudio > Utilities > Tests`.
  That window belongs to the audio package and its menu is `MidManStudio > Audio > Tests > Audio Bench`.

### `MID_TickDispatcherBench.cs`
- Fixed (comments): the file header and the class said the file "benchmarks MID_TickDispatcher". The header was folded into
  the class summary, which now says what is really measured.
- Not fixed: it does not exercise the real dispatcher, see Modules above. Whether to subscribe real `MID_TickDispatcher`
  subscribers, or to keep it and rename it, is the owner's decision.
- Not fixed: each pass is timed with `Stopwatch.Restart` and `Stop`. For one or ten empty subscribers the work is about the
  same size as the stopwatch call, so small counts mostly measure the stopwatch. Not verified by running.
- Not fixed: the multi-rate scenario has 62 timed steps and the 2 s rate never fires in one simulated second. The component
  field `MultiSubsPerRate` accepts a value above 10, but the code silently uses 10.
- Not fixed: the private `Interval` method is a second copy of the rate to seconds table. If `TickRate` changes it will not
  follow.

### `MID_TickDelayBenchmark.cs`
- Not fixed: the allocation test makes `GCIterations` calls (500 by default, up to 1000) while `MID_TickDelay` holds 64 pending
  delays by default, and the runner never changes `MID_TickDelay.PoolCapacity`. Calls after the 64th are dropped. Each
  dropped call builds the pool-full message with string interpolation (including the capacity number) before the logger
  call, so it allocates a string. Reading the code, the "MID_TickDelay" result at the default settings is therefore not
  0 B per call, and the window shows it in red next to the text "always zero". Not run in Unity. To measure the real path,
  keep `GCIterations` at 64 or less, or raise `MID_TickDelay.PoolCapacity` before the first call.
- Not fixed: the timing test can hit the same limit. It starts one delay per frame, so the pending count is about the
  frame rate times `DelaySeconds`, up to `TimingIterations`. At 0.5 s that is about 30 at 60 fps but about 72 at 144 fps. With
  a 5 s delay and 100 iterations all 100 are pending at once. The "Fired" row of the window shows the shortfall, and
  nothing is logged (see the next point).
- Not fixed: the pool-full warning is silent. `MID_TickDelay` passes `MID_LogLevel.Error` to `MID_Logger.LogWarning`, which
  needs Info or more verbose. See the Fixes and Problems of `MID_TickDelay.cs` in [tickdispatcher.md](tickdispatcher.md).
- Not fixed: the warm-up and the allocation test call `MID_TickDelay.CancelAll()`, and so does Cancel. That cancels every
  pending delay in the scene, not only the bench's own. Running the bench in a scene with game code that uses `MID_TickDelay`
  silently cancels that code's delays.
- Not verified in Unity: the file is called `MID_TickDelayBenchmark.cs` but the MonoBehaviour in it is
  `MID_TickDelayBenchRunner`. Unity expects a MonoBehaviour's class name to match its file name before it can be attached
  from the Inspector or by drag and drop. Mismatches are a common cause of "Can't add script component" errors. The window's
  Add Runner to Scene button uses `AddComponent`, which works in code, but the component it creates may show as a missing script
  after the scene is saved and reloaded. `Documentation~/scenesetup.md` tells users to add the component by name. The
  `DelayBenchGCResult missing ExtensionOfNativeClass` text in the window looks like a trace of an earlier trouble of this
  kind. The other two benchmark files do match (`MID_TickDispatcherBench`, `TimerBenchmarkRunner`). A rename of the file
  (with its `.meta`) to `MID_TickDelayBenchRunner.cs` is the usual fix, and changes the path used in this page and in the
  notice header. Owner's decision, and it needs a real Unity check first.
- Not fixed: `RunCount` counts every run, including timing-only runs, and the first run is treated as cold. A timing-only
  first run makes a later allocation run count as warm, even though the allocation test has not yet run.
- Not fixed: the explanations in the window and the old header ("about 80 to 400 B per StartCoroutine", "about 120 to 160 B for
  Task.Delay", "about 50 ms average error at Tick_0_1", ".NET pools DelayPromise objects") are fixed text. They were not
  measured or checked here. The tick error figure is the middle of the 0 to 100 ms range, not a measurement.
- Not fixed: if `GC.GetAllocatedBytesForCurrentThread` throws, the fallback reads `GC.GetTotalMemory(false)`, which is not a
  per-thread counter and can go down when a collection runs, which gives a negative bytes per call figure.
- Fixed (comments): one comment in the allocation test said `MID_TickDelay` "always shows 0B because it never allocates
  regardless". It now says 0 B is expected while the pool has room. The file header with the measurement notes was folded into
  the class summary of `MID_TickDelayBenchRunner`.

### `TimerBenchmarkRunner.cs`
- Fixed (comments): the reset check said `Tick(0.1f)` "advances two steps". `SteppedValueTimer.Tick` takes at most one step
  per call. The comment for the 4-step check now says "a little over 0.16 s".
- Not fixed: the stopwatch check feeds the same `Time.deltaTime` values to the timer and to its own loop, so the timer's reading
  always equals the loop's. The check finds a timer that does not accumulate. It can not find drift, and it fails when a
  single frame is longer than the tolerance (0.06 s).
- Not fixed: for the countdown and interpolation checks the loop's timeout is the sum of the same `deltaTime` values the timer
  adds up, and it is longer than the duration. Reading the code, the timer always finishes before the timeout, so the
  tolerance setting has no effect there.
- Not fixed: the `ToCSV()` check (9 columns) depends on the machine's culture. See `PerformanceBenchmarkTimer.cs` in
  [timers.md](timers.md).
- Not fixed: the exception check makes the benchmark log a warning for each thrown iteration (50 lines), and `QuickCompare`
  blocks the main thread for 100 ms between the two methods.
- Not fixed: `Run On Start` is on by default, so the component runs the whole suite (over a hundred Console lines) as soon as
  it is added to a scene and Play Mode starts. Choosing a different default is the owner's decision.
