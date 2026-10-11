# com.midmanstudio.patterns: Game Loop

## Modules

### `FixedStepLoop.cs`
**What it does:** Runs simulation in fixed-size steps while the frame rate varies. `Advance(frameSeconds)` adds
the elapsed time to an accumulator and calls the step function once for every full step it contains, then
returns how many ran. `Alpha` is the fraction of the next step already elapsed, for blending the previous and
current state when drawing. `TimeScale` multiplies the frame time, `Paused` stops stepping, `StepCount` and
`DroppedSteps` count, and `Reset` clears the accumulator and the counters.

**How it works:**
- The accumulator is a double, so adding many small frames does not drift.
- A step is called with the fixed step length, never with the frame time.
- When more than `MaxStepsPerAdvance` steps are due, the extra whole steps are discarded and added to
  `DroppedSteps`, and the remainder of the accumulator stays below one step. Without the cap a long hitch
  makes the next frame run so many steps that it is slower still.
- An exception from the step function propagates. Steps that already ran stay counted and the accumulator
  keeps the time that was not yet used.

**Decisions:**
- The class is engine-free: the caller passes the frame time. Unity's `FixedUpdate` already provides this for
  physics, so the class is for simulation that runs outside it, such as netcode, replays and tests.
- A `TimeScale` of 0 freezes the simulation but still accepts frames. `Paused` ignores frames completely and
  keeps the accumulator.
- Not thread-safe.

### `GameLoopTests.cs`
**What it does:** 13 edit-mode NUnit tests: one full step, accumulation over short frames, several steps, the
hitch cap and dropped steps, `Alpha`, the same step total for different frame splits, `TimeScale`, a scale of
zero, pausing, `Reset`, counters, a throwing step function and argument checks. The tests use frame and step
lengths that are powers of two so every sum is exact.

**Decisions:**
- Four deliberate breakages were tried (extra steps carried over instead of dropped, the exact step boundary
  missed, pause ignored, time scale ignored). All were caught.
  The tests were run outside Unity against a small NUnit stand-in and have not been run in the Unity Test Runner.

## Fixes and Problems

None so far. No bug has been found in a released version.
