# com.midmanstudio.utilities: Performance

## Modules

### `MID_FrameTimeStats.cs`
**What it does:** Fixed-size rolling window of frame times in milliseconds.
`Add()` is O(1) and allocation-free. `Recalculate()` fills `AverageMs`,
`BestMs`, `WorstMs` and `OnePercentLowMs` (plus the matching `...Fps`
properties) from the current window. Plain C# with no Unity dependency, so it
can be used and tested outside a scene.

**Decisions:**
- `Recalculate()` sorts a preallocated scratch copy of the window. That is
  cheap at a few hundred samples but not free, so callers refresh on a timer
  instead of every frame.
- "1% low" is the mean of the slowest 1% of frames in the window (at least
  one frame), converted to FPS. This is the figure most benchmark tools mean
  by the term. With the default 300-frame window that is the 3 worst frames.
- `Add()` drops NaN, infinite and non-positive values instead of storing them,
  so one bad delta cannot poison the average.
- Behavior was checked with a throwaway console harness: constant 60 FPS input,
  a single 100 ms hitch among 99 fast frames, ring-buffer wraparound, invalid
  samples, and adding samples after a `Recalculate()`. There is no automated
  test assembly for this package yet.

### `MID_FpsCounter.cs`
**What it does:** On-screen frame-rate readout. Add it to any GameObject. It
shows the windowed average FPS and frame time, and optionally the best and
worst frame and the 1% low. The overlay is drawn with IMGUI (no Canvas or font
asset), can be toggled with a key (F3 by default), and can be moved to any
screen corner. `TextUpdated`, `DisplayText` and the FPS properties let a
project show the numbers in its own UI instead.

**Decisions:**
- Frame times come from `Time.unscaledDeltaTime`. Unity's time-handling manual
  states that the unscaled values ignore `Time.maximumDeltaTime` and
  `Time.timeScale` and report the real elapsed time, which is what a frame-rate
  readout needs. `Time.smoothDeltaTime` is not used because it is scaled and
  smoothed, and smoothing hides the spikes this tool exists to show.
  `Time.frameCount / Time.time` is not used because it is an average since
  startup and stops reflecting current conditions.
- The shown FPS is the window average, not `1 / deltaTime` of the last frame,
  which jumps too much to read. The readout text is rebuilt every
  `_refreshInterval` seconds (0.5 by default) rather than every frame, so it
  stays legible and the `StringBuilder`/string allocations happen only a couple
  of times per second. IMGUI itself still allocates a little per repaint; for a
  zero-allocation display, turn the overlay off and drive a UI text from
  `TextUpdated`.
- The first `_warmupFrames` frames are ignored so scene-load hitches do not
  distort the window. `ResetStats()` restarts the window and the warmup, for
  use after a scene load or a mode change.
- Color is chosen against a target frame rate: green at 90% or more of target,
  yellow at 50% or more, red below. `_targetFps` of 0 uses
  `Application.targetFrameRate`, or 60 when that is unset. The display refresh
  rate is not read automatically because `Resolution.refreshRate` is obsolete
  in Unity 2022.2 and later.
- Font size and margins scale with `Screen.dpi` (clamped to 1x to 3x) so the
  overlay is readable on phones, and the position respects `Screen.safeArea`.
  Both can be turned off.
- The toggle key is read through the legacy Input Manager, guarded by
  `ENABLE_LEGACY_INPUT_MANAGER`. On a project that only uses the new Input
  System the key is skipped and `Toggle()` / `Visible` still work.
- `_developmentBuildOnly` disables the component in release builds, for
  projects that leave the counter in a scene permanently.
