# com.midmanstudio.utilities: Timers

## Modules

### `Timer.cs`
**What it does:** A small family of plain C# timer classes, all driven by
an explicit `Tick(deltaTime)` call rather than Unity's own Update loop:
`Timer` (abstract base: `Start`/`Stop`/`Resume`/`Pause`, `OnTimerStart`/`OnTimerStop`),
`CountdownTimer` (counts down, `OnTimerComplete`), `StopwatchTimer` (counts
up, never completes), `ValueInterpolationTimer` (tweens a float between two
values with an `InterpolationMode`, optional one-round-trip ping-pong),
`SteppedValueTimer` (discrete step increments instead of continuous
interpolation), `NetworkTimer` (fixed-interval tick accumulator), and
`TimerFactory` (convenience constructors for common configurations:
dissolve, fade, stepped dissolve, ping-pong).

**Decisions:**
- `NetworkTimer` here is deliberately a separate, simpler type from
  `com.midmanstudio.netcode`'s own `NetworkTimer` (which uses PascalCase
  properties: `MinTimeBetweenTicks`/`CurrentTick`/`LerpFraction`). This one
  exists for utility-only contexts that can't take a netcode dependency;
  prefer the netcode package's version for anything actually networked.
  Both are named `NetworkTimer`, so importing both namespaces in the same
  file needs a fully-qualified reference or a `using` alias to disambiguate.

## Fixes and Problems

### `Timer.cs`
- `TimerFactory.CreatePingPongTimer()` doesn't actually enable ping-pong:
  it returns `new ValueInterpolationTimer(...)` with no call to set the
  timer's ping-pong flag, which can only be done by calling
  `StartPingPong()` (which also immediately starts the timer — there's no
  way to configure ping-pong mode without starting). A caller who does the
  natural thing, `TimerFactory.CreatePingPongTimer(...).Start()`, gets a
  single one-way interpolation, not a round trip. Not changed this pass
  (fixing it properly needs a way to set ping-pong mode independent of
  starting, which is a small API addition, not a doc fix); documented on
  `CreatePingPongTimer` itself and here so it's not mistaken for working
  as named.
- Not fixed: `SteppedValueTimer.Tick` takes at most one step per call and throws away the time past the interval
  (`_timeSinceLastStep` is set to 0, not reduced by the interval). A long frame, or an interval that is shorter than a
  frame, makes the timer slower than `stepInterval` suggests. For example a 0.04 s interval at 60 fps steps every third
  frame, which is 0.05 s. Found by reading the code, not run in Unity.

### `PerformanceBenchmarkTimer.cs`
- Not fixed: `BenchmarkResult.ToCSV()` formats its numbers with `F3` in the current culture. On a machine whose culture
  uses a decimal comma the line has more than 9 comma separated fields, so the CSV is unusable and the "CSV has 9
  columns" check in `TimerBenchmarkRunner` fails. `ToString()` has the same culture dependence, which only affects
  display. Found by reading the code, not run on such a machine.
