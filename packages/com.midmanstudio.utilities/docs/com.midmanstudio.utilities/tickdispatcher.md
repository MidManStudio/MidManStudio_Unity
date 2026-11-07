# com.midmanstudio.utilities: TickDispatcher

The tick system has two files in `Runtime/TickDispatcher/`. `MID_TickDispatcher` is a shared interval dispatcher
that replaces per-object `Update()` calls. `MID_TickDelay` is a pool of delayed and repeating actions that runs on
top of it. Both are in the namespace `MidManStudio.Core.TickDispatcher` and the runtime assembly
`MidManStudio.Utilities`. The two benchmark runners for this system are in
[tests.md](tests.md), and a working scene is in the `TickTest` sample, see [samples.md](samples.md).

## Modules

### `MID_TickDispatcher.cs`
**What it does:** A singleton MonoBehaviour with one bucket per `TickRate`. A single `Update()` adds
`Time.deltaTime` to the timer of every bucket that has subscribers and calls the subscribers when the bucket's
interval has elapsed. Buckets with no subscribers are skipped. The instance is found or created on first use
(`FindAnyObjectByType`, then a new `DontDestroyOnLoad` GameObject named `[MID_TickDispatcher]`), or it can be placed
in a bootstrap scene. A second instance destroys itself in `Awake`.

**Tick rates:** The enum name is the interval in seconds, with an underscore for the decimal point.

| Rate | Interval | Per second | Use for |
|---|---|---|---|
| `Tick_0_01` | 0.01 s | 100 | Nothing in gameplay. Only sensible when the frame rate is above 100 |
| `Tick_0_02` | 0.02 s | 50 | Marginal. Only when 50 Hz is required and the frame rate is well above 50 |
| `Tick_0_05` | 0.05 s | 20 | Fast systems: weapon or projectile checks |
| `Tick_0_1` | 0.1 s | 10 | Recommended minimum: fast AI, cooldowns, state sync |
| `Tick_0_2` | 0.2 s | 5 | Standard: AI pathfinding, ability cooldowns, targeting |
| `Tick_0_5` | 0.5 s | 2 | Area checks, perception |
| `Tick_1` | 1 s | 1 | Health regeneration, UI number refreshes |
| `Tick_2` | 2 s | 0.5 | Distant objects, LOD checks |
| `Tick_5` | 5 s | 0.2 | Spawners, wave logic |

**How to use it:**
```csharp
void OnEnable()  => MID_TickDispatcher.Subscribe(TickRate.Tick_0_2, OnTick);
void OnDisable() => MID_TickDispatcher.Unsubscribe(TickRate.Tick_0_2, OnTick);
void OnTick(float interval) { /* about 5 times per second */ }
```
The callback type is `MID_TickDispatcher.TickCallback(float deltaTime)`. The value passed in is the bucket's fixed
interval, not `Time.deltaTime`.

**Public API (all static):**
- `Subscribe(TickRate, TickCallback)` returns true when added, false for a null callback, a duplicate, an unknown
  rate or a dispatcher that is not available. A callback that is already subscribed is ignored, not added twice.
- `Unsubscribe(TickRate, TickCallback)`, `IsSubscribed`, `ClearSubscribers(TickRate)`, `ClearAllSubscribers()`.
- `GetSubscriberCount`, `IsTickRateActive`, `GetInterval` (seconds), `GetFrequency` (per second).
- `Instance`, `IsReady` (initialised and alive) and `IsQuitting` (set in `OnApplicationQuit`; `Instance` returns null
  and `Subscribe` returns false after that).

**Inspector fields:** `_logLevel` (default `Info`), `_maxTicksPerFrame` (default 5) and
`_enableDeathSpiralProtection` (default on). In the Editor only, `_editorBucketStatus` lists each rate with its
subscriber count and an `ACTIVE` or `IDLE` state, refreshed every frame. It is stripped from builds.

**Dispatch details:**
- Each bucket owns a `HashSet<TickCallback>`, so add and remove are O(1). Before invoking, the set is copied to one
  shared `TickCallback[]` (starts at 64 entries, grows to at most 512) with `CopyTo`, so no enumerator is allocated.
- Each callback is checked with `Contains` right before it is invoked, so a callback that unsubscribes another
  callback in the same tick prevents it from running.
- A callback that throws is caught and logged with its type and method name. The rest of the bucket still runs.
- When a bucket goes from empty to one subscriber its timer is reset to zero, so the first callback comes one full
  interval later.
- Catch-up: when a frame is longer than the interval the bucket fires more than once in that frame, up to
  `_maxTicksPerFrame` times, each call with the same interval value.
- Death spiral guard: when a bucket fires `_maxTicksPerFrame` times in one frame and the guard is on, its timer is
  reset to zero and a warning lists the subscribers. With the guard off the cap per frame still applies, and from the
  next frame the stored backlog is limited to `_maxTicksPerFrame - 1` intervals plus the length of that frame.

**Measured results recorded by the author:** The table below was in the file header before this pass. It was
measured by the author with 200 subscribers on `Tick_0_1` at about 72 fps and was not re-run when it was moved here.

| Phase | Update per second | Dispatcher per second | CPU saved |
|---|---|---|---|
| A: empty callbacks | 0.72 ms | 0.008 ms | 98.9 % |
| B: heavy work, 200 callbacks | 102.74 ms | 25.38 ms | 75.3 % |
| C: countdown timers | 2.70 ms | 0.30 ms | 88.7 % |
| D: interpolation timers | 2.82 ms | 0.48 ms | 80.5 % |
| E: raw micro benchmark | 2.24 ms | 2.29 ms | ratio 0.969 |

The author's reading: per-fire cost is the same as a direct loop (ratio 0.97 to 1.14), and all savings come from the
lower fire rate. At the same setup `Tick_0_05` saved about 50 to 60 percent, `Tick_0_02` 1 to 28 percent, and
`Tick_0_01` was slower than plain `Update()` (minus 77 to minus 191 percent).

**Decisions:**
- The saving comes only from firing less often than the frame rate. Once the rate is at or above the frame rate the
  dispatcher adds its own cost on top of the callback work. This is why the guide calls `Tick_0_1` the recommended
  minimum and why `MID_TickDelay` refuses anything faster.
- The delegate model is kept. Burst cannot call managed delegates, so using it would mean rebuilding around
  `NativeArray<FunctionPointer>`. The author judged that not worth it below roughly 10,000 subscribers per bucket.
- For a project that is already ECS based, a system group with a custom update rate is the better tool. This
  dispatcher is meant for GameObject and MonoBehaviour projects.
- To add a rate: add a value to the `TickRate` enum and a matching entry to `_tickIntervals`. Buckets are built from
  `_tickIntervals`. Keep the enum ordered from the fastest rate to the slowest, because `MID_TickDelay` compares rates
  with `>=` and uses the numeric value as an array index.
- The dependency is `MID_Logger` only.

**Gotchas:**
- Main thread only. `Instance` calls Unity find and create APIs, and the buckets are not locked.
- `Subscribe`, `Unsubscribe`, `ClearSubscribers` and `ClearAllSubscribers` go through `Instance` and will create a
  dispatcher if none exists. `IsSubscribed`, `GetSubscriberCount` and `IsTickRateActive` do not create one.
- The callback argument is the interval, so do not use it as a frame time.
- Always unsubscribe in `OnDisable` or `OnDestroy`. A destroyed subscriber that stays in the set keeps the delegate
  alive and runs a method on a destroyed object.
- The shared invoke array is not cleared after a dispatch, so references to the last invoked callbacks stay in it until
  they are overwritten.

### `MID_TickDelay.cs`
**What it does:** A static class for delayed and repeating `Action` delegates. It is an alternative to
`StartCoroutine` and `Task.Delay` that does not allocate inside its own code and has no `IEnumerator`, so it can be
used inside Netcode for GameObjects RPC bodies. It subscribes once to the dispatcher for each allowed rate and keeps its
state in parallel arrays (`_remaining`, `_interval`, `_repeatLeft`, `_actions`, `_rates`, `_active`, `_generation`), so
a pending delay is a slot index, not an object.

**How to use it:**
```csharp
private static readonly Action _onDone = HandleDone;   // create the delegate once

TickDelayHandle h = MID_TickDelay.After(1f, _onDone);                 // once
TickDelayHandle r = MID_TickDelay.Repeat(0.5f, 4, _onDone);           // 4 times
TickDelayHandle f = MID_TickDelay.RepeatForever(2f, _onDone, TickRate.Tick_0_5);
h.Cancel();                    // or MID_TickDelay.Cancel(h)
MID_TickDelay.CancelAll();     // for example on scene unload
```
- `After(seconds, action, rate = Tick_0_1)`: negative seconds are treated as 0.
- `Repeat(intervalSeconds, times, action, rate)`: interval has a floor of 0.01, `times` a floor of 1.
- `RepeatForever(intervalSeconds, action, rate)`: runs until cancelled.
- `Cancel` and `CancelAll` are safe on handles that already fired. `ActiveCount` is the number of pending delays.
- A null action is ignored with a log message and the call returns an invalid handle.
- A rate faster than `Tick_0_1` is replaced with `Tick_0_1` and a log message is written.

**Handles:** `TickDelayHandle` is a struct of a slot index and a generation number. Freeing a slot increments its
generation, so an old handle cannot cancel a delay that now uses the same slot. `IsValid` is false only for the
sentinel returned for a rejected call. It stays true after the delay has fired or been cancelled.

**The zero-allocation rule:** The call itself allocates nothing once the pool exists. Converting a method group or a
capturing lambda to `Action` creates a new delegate each time, so these allocate on every call:
`After(1f, MyStaticMethod)` and `After(1f, () => DoWork())`. Create the delegate once, as in the example above.

**Pool:** 64 slots by default. `PoolCapacity` has a floor of 8. Setting it rebuilds the pool and drops every pending
delay without calling it, so set it before the first use. When all slots are used the new call is dropped and an error
level message says so.

**Decisions:**
- Rates faster than `Tick_0_1` are not supported, for the same reason as above: they fire at or above frame rate, so
  there is nothing to gain. Use a coroutine for sub-frame timing.
- Slot data lives in arrays and each rate has a preallocated buffer for the slots that fire in a tick, so the tick
  callback allocates nothing. One-shot slots are freed before their action runs, so an action can schedule a new delay
  at once.
- Each rate has a counter of active delays, so a tick for a rate that has none returns immediately.
- An exception inside an action is caught and logged, and the other delays of that tick still run.

**Gotchas:**
- Timing is quantised to the rate's interval, see the first entry under Fixes and Problems for the measured effect.
- Main thread only. The lock protects slot bookkeeping, but the tick callback reads the arrays without it.
- Rates are subscribed once and never removed, so the dispatcher keeps a bucket for each allowed rate active for the
  rest of the session.

## Fixes and Problems

### `MID_TickDispatcher.cs`
- Moved out of the file header: the explanation of why the dispatcher exists, how it works, the zero-allocation
  design, the Burst and ECS notes, the benchmark table, the minimum rate warning, the usage example and the steps for
  adding a rate. All of it is now in the section above and in the XML summaries.
- The old header and the `TickRate` summary said that death spiral warnings at normal frame rates are expected for
  `Tick_0_01`. The code does not behave that way. The guard needs `_maxTicksPerFrame` (5) ticks in one frame, which
  means a frame of roughly four to five intervals: about 20 to 25 fps or lower for `Tick_0_01`, about 10 fps for
  `Tick_0_02`. At 60 fps `Tick_0_01` fires once or twice per frame and is slow, but it does not warn. The wording was
  corrected in the summaries.
- Not fixed: more than 512 subscribers on one rate. The invoke array is capped at 512 (`MAX_CACHE_SIZE`), so
  `HashSet.CopyTo` throws `ArgumentException` for 513 or more. The throw is outside the per-callback `try`, so it leaves
  `Update()` every frame, skips the buckets after it and allocates a new array each time.
- Not fixed: `_maxTicksPerFrame` has no minimum. At 0 or below no bucket ever fires, and with the guard on every active
  bucket logs a warning every frame.
- Not fixed: the guard fires whenever exactly `_maxTicksPerFrame` ticks ran, even if no backlog was left, so it can
  warn at a steady low frame rate.
- Not fixed: `_applicationIsQuitting` is never set back to false. With Enter Play Mode Options and domain reload
  turned off, the second play session sees `Instance` return null and `Subscribe` return false. Found by reading the
  code, no reset hook exists in the package. It was not run.
- Not fixed: the unsubscribe and clear methods can create a dispatcher during teardown if the instance was already
  destroyed and the application is not quitting.

### `MID_TickDelay.cs`
- Moved out of the file header: the zero-allocation contract with the wrong and correct examples, the minimum rate note
  and the pool sizing note. They are now in the XML summary of the class and in the section above.
- Not fixed: delays are quantised to the tick interval. The remaining time is reduced by a full interval on every tick,
  starting from the first tick after the call, which can come at any point inside the interval. A delay can therefore
  fire up to one interval early. Float rounding of 0.1 can also add one extra tick. A float32 simulation of the same
  arithmetic at `Tick_0_1` (not run in Unity) gave: 0.3 s needs 4 ticks (ideal 3), 0.5 s needs 6 (ideal 5), 3 s needs 31
  (ideal 30), 5 s needs 51 (ideal 50), while 0.1, 0.2, 0.7, 1, 1.5, 2 and 10 s need the ideal count. The README gives
  the error as 0 to 100 ms; the real range is up to 100 ms early and, for the values above, up to 100 ms late.
- Not fixed: repeating delays reset the remaining time to the full interval after each run, which discards the
  overshoot. The real period is the interval rounded up to a multiple of the tick interval, plus the rounding above.
  Simulated at `Tick_0_1`: 0.05 gives 0.1 s, 0.15 gives 0.2 s, 0.25 gives 0.3 s, 0.3 gives 0.4 s, 0.5 gives 0.6 s, 0.75
  gives 0.8 s. Use a rate that divides the interval, or measure.
- Not fixed: `default(TickDelayHandle)` is treated as valid, see the handle remark above. A cancel on a field that was
  never assigned can cancel the first delay ever placed in slot 0.
- Not fixed: if an action cancels another delay that is due in the same tick and then schedules a new delay that takes
  the cancelled slot, the new delay can run immediately or be freed without running. Found by reading the code, not
  reproduced.
- Not fixed: the `PoolCapacity` setter compares the raw value before applying the floor of 8, so setting a value below
  8 when the capacity is already 8 still rebuilds the pool and drops every pending delay.
- Not fixed: the module state is static and `_initialised` is never reset. With domain reload turned off, the
  dispatcher is destroyed when play mode ends, so in the next session the old callbacks are gone and delays never fire.
  Found by reading the code, not run.
- Not fixed: scheduling scans the pool for a free slot, and a tick scans all slots once for each rate that has active
  delays. This is fine at the default 64 slots and grows linearly with `PoolCapacity`.
- Not fixed: the warnings in `MID_TickDelay` are never printed. The pool-full message in `AllocateSlot` and the three
  null-action messages in `After`, `Repeat` and `RepeatForever` call `MID_Logger.LogWarning(MID_LogLevel.Error, ...)`.
  `LogWarning` needs a level of Info or more verbose, and `ShouldLog` compares the level as a number, so Error (1) is
  below Info (2) and the call returns before logging. A full pool therefore drops the action silently. The pool-full
  message is also built with string interpolation before the logger is called, so every dropped call allocates a
  string even though nothing is shown. The tests doc, `MID_TickDelayBenchmark.cs`, shows how this affects the Tick
  Delay Bench.
