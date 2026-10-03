# com.midmanstudio.utilities: SequentialProcessRunner

## Modules

### `MID_SequentialProcessRunner.cs`
**What it does:** Generic sequential task runner with priority lanes (lane
0 runs first; a lane only starts once every earlier lane has fully
completed its pass) and per-task retry with an optional fallback.
`SequentialTask` wraps one async unit of work; `MID_SequentialProcessRunner`
is the static runner (`AddTask`/`AddTasks`, `RunAll`, `IsCompleted`,
`Reset`/`ResetLane`). No cloud/internet dependencies; those concerns belong
in the task bodies themselves.

**Decisions:**
- One `RunAll()` call makes a single pass through every lane. A task that
  still has retries left when its lane's pass ends stays queued and is
  retried on the *next* `RunAll()` call, not looped within the same call.
  `OnAllLanesComplete` therefore means "one pass finished," not "every task
  ultimately succeeded" — check `OnTaskFailed` or `IsCompleted` for that.
  Documented on the class and on `OnAllLanesComplete` rather than changed,
  since callers already rely on the multi-run retry shape.
- `Reset()` is not safe to call while `RunAll()` is in progress (for
  example, from an event handler wired to one of this class's own events
  raised mid-run): a lane that hasn't started yet will find its queue
  already cleared. Documented on `Reset()` rather than guarded against,
  since guarding would need to either block or silently defer the reset,
  and the class has no existing precedent for either.

## Fixes and Problems

No bugs found in this part; this pass was documentation only (NOTICE
header plus XML `<summary>` docs on the public surface).
