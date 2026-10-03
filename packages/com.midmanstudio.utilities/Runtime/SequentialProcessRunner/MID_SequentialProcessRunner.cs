// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/sequentialprocessrunner.md, section "MID_SequentialProcessRunner.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.SequentialProcessing
{
    // ── Task definition ───────────────────────────────────────────────────────

    /// <summary>
    /// One unit of work for <see cref="MID_SequentialProcessRunner"/>: an
    /// async body (<c>execute</c>), an optional offline/local
    /// <c>fallback</c> tried once the primary body exhausts its retries,
    /// and a priority <see cref="Lane"/> (lower runs first, and a lane
    /// only starts once every earlier lane has fully completed).
    /// </summary>
    public class SequentialTask
    {
        /// <summary>Retry ceiling shared by every task; not configurable per-task.</summary>
        public const int MaxRetries = 6;

        /// <summary>Human-readable task name, used for logging and <see cref="MID_SequentialProcessRunner.IsCompleted"/> lookups.</summary>
        public string Name            { get; }
        /// <summary>Priority lane this task runs in. 0 is highest priority and runs first.</summary>
        public int    Lane            { get; }
        /// <summary>True if a fallback was supplied to the constructor.</summary>
        public bool   HasFallback     { get; }
        /// <summary>How many times this task has failed and been retried so far.</summary>
        public int    RetryCount      { get; private set; }
        /// <summary>True once this task (or its fallback) has succeeded.</summary>
        public bool   IsCompleted     { get; private set; }

        private readonly Func<Task<bool>> _execute;
        private readonly Func<Task<bool>> _fallback;

        /// <param name="name">Human-readable task name for logging.</param>
        /// <param name="lane">Priority lane (0 = highest). Lower lanes must complete before higher.</param>
        /// <param name="execute">Async task body. Return true = success, false = failure/retry.</param>
        /// <param name="fallback">Optional offline/local fallback if primary fails all retries.</param>
        public SequentialTask(string name, int lane,
            Func<Task<bool>> execute, Func<Task<bool>> fallback = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Task name cannot be empty.", nameof(name));

            Name        = name;
            Lane        = lane;
            _execute    = execute ?? throw new ArgumentNullException(nameof(execute));
            _fallback   = fallback;
            HasFallback = fallback != null;
        }

        internal async Task<bool> RunAsync()
        {
            try { return await _execute(); }
            catch (Exception e)
            {
                MID_Logger.LogError(MID_LogLevel.Error,
                    $"Task '{Name}' threw exception: {e.Message}",
                    nameof(SequentialTask));
                return false;
            }
        }

        internal async Task<bool> RunFallbackAsync()
        {
            if (_fallback == null) return false;
            try { return await _fallback(); }
            catch (Exception e)
            {
                MID_Logger.LogError(MID_LogLevel.Error,
                    $"Task '{Name}' fallback threw exception: {e.Message}",
                    nameof(SequentialTask));
                return false;
            }
        }

        internal void IncrementRetry() => RetryCount++;
        internal void MarkComplete()   => IsCompleted = true;
        internal void Reset()          { RetryCount = 0; IsCompleted = false; }
    }

    // ── Runner ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs tasks sequentially across priority lanes with retry and
    /// fallback support. No cloud/internet dependencies; those concerns
    /// belong in the task bodies themselves.
    ///
    /// One <see cref="RunAll"/> call makes a single pass through every
    /// lane. A task that still has retries left when its lane's pass ends
    /// stays queued and is retried on the *next* <see cref="RunAll"/> call
    /// rather than looping within the same call; <see cref="OnAllLanesComplete"/>
    /// therefore means "one pass finished," not "every task ultimately
    /// succeeded" (check <see cref="OnTaskFailed"/> / <see cref="IsCompleted"/>
    /// for that).
    /// </summary>
    public static class MID_SequentialProcessRunner
    {
        #region Configuration

        /// <summary>Log level for this runner's own progress/retry/completion logging.</summary>
        public static MID_LogLevel LogLevel       = MID_LogLevel.Info;
        /// <summary>Delay inserted between each task (and each retry) within a lane, in milliseconds. 0 disables the delay.</summary>
        public static int          DelayBetweenTasksMs = 50;

        #endregion

        #region Events

        /// <summary>Raised once every lane has completed one pass (see the class summary for what "complete" means here).</summary>
        public static Action        OnAllLanesComplete;
        /// <summary>Raised after a lane finishes its pass, with the lane index.</summary>
        public static Action<int>   OnLaneComplete;   // lane index
        /// <summary>Raised when a task (or its fallback) succeeds, with the task name.</summary>
        public static Action<string> OnTaskCompleted; // task name
        /// <summary>Raised when a task exhausts <see cref="SequentialTask.MaxRetries"/> (fallback included), with the task name.</summary>
        public static Action<string> OnTaskFailed;    // task name after all retries

        #endregion

        #region State

        private static readonly Dictionary<int, Queue<SequentialTask>> _lanes       = new();
        private static readonly Dictionary<int, List<SequentialTask>>  _failed      = new();
        private static readonly HashSet<string>                        _completed   = new();

        private static bool _isRunning;
        private static int  _runCount;

        #endregion

        #region Public API

        /// <summary>Add a task to the runner. Lane 0 runs first.</summary>
        public static void AddTask(SequentialTask task)
        {
            if (task == null) return;
            EnsureLane(task.Lane);
            _lanes[task.Lane].Enqueue(task);

            MID_Logger.LogDebug(LogLevel,
                $"Task queued: '{task.Name}' lane={task.Lane}",
                nameof(MID_SequentialProcessRunner));
        }

        /// <summary>Adds each task via <see cref="AddTask"/>, in enumeration order.</summary>
        public static void AddTasks(IEnumerable<SequentialTask> tasks)
        {
            foreach (var t in tasks) AddTask(t);
        }

        /// <summary>
        /// Run all lanes in priority order. Awaitable — resolves when all lanes complete.
        /// </summary>
        public static async Task RunAll()
        {
            if (_isRunning)
            {
                MID_Logger.LogWarning(LogLevel, "Already running.",
                    nameof(MID_SequentialProcessRunner));
                return;
            }

            _isRunning = true;
            _runCount++;
            MID_Logger.LogInfo(LogLevel, $"Starting run #{_runCount}.",
                nameof(MID_SequentialProcessRunner));

            try
            {
                var sortedLanes = _lanes.Keys.OrderBy(k => k).ToList();
                foreach (int lane in sortedLanes)
                {
                    await RunLane(lane);
                    OnLaneComplete?.Invoke(lane);
                    MID_Logger.LogInfo(LogLevel, $"Lane {lane} complete.",
                        nameof(MID_SequentialProcessRunner));
                }

                OnAllLanesComplete?.Invoke();
                MID_Logger.LogInfo(LogLevel, $"All lanes complete. Run #{_runCount}.",
                    nameof(MID_SequentialProcessRunner));
            }
            catch (Exception e)
            {
                MID_Logger.LogError(LogLevel, $"RunAll exception: {e.Message}",
                    nameof(MID_SequentialProcessRunner));
            }
            finally
            {
                _isRunning = false;
            }
        }

        /// <summary>Returns true if a task with this name has completed successfully.</summary>
        public static bool IsCompleted(string taskName) => _completed.Contains(taskName);

        /// <summary>
        /// Reset all state so RunAll can be called again. Not safe to call
        /// while <see cref="RunAll"/> is in progress (for example, from an
        /// event handler wired to one of this class's own events raised
        /// mid-run): a lane that hasn't started yet will find its queue
        /// already cleared. Call between full runs, not during one.
        /// </summary>
        public static void Reset()
        {
            _lanes.Clear();
            _failed.Clear();
            _completed.Clear();
            MID_Logger.LogInfo(LogLevel, "Reset complete.",
                nameof(MID_SequentialProcessRunner));
        }

        /// <summary>Reset only a specific lane.</summary>
        public static void ResetLane(int lane)
        {
            if (_lanes.ContainsKey(lane))  _lanes[lane]  = new Queue<SequentialTask>();
            if (_failed.ContainsKey(lane)) _failed[lane] = new List<SequentialTask>();
            MID_Logger.LogDebug(LogLevel, $"Lane {lane} reset.",
                nameof(MID_SequentialProcessRunner));
        }

        #endregion

        #region Lane Execution

        private static async Task RunLane(int lane)
        {
            EnsureLane(lane);
            var queue  = _lanes[lane];
            var failed = _failed[lane];

            MID_Logger.LogInfo(LogLevel,
                $"Running lane {lane} — {queue.Count} task(s).",
                nameof(MID_SequentialProcessRunner));

            // Process primary queue
            while (queue.Count > 0)
            {
                var task = queue.Dequeue();
                await ExecuteTask(task, failed);
                if (DelayBetweenTasksMs > 0) await Task.Delay(DelayBetweenTasksMs);
            }

            // Retry failed
            if (failed.Count > 0)
            {
                MID_Logger.LogInfo(LogLevel,
                    $"Lane {lane} — retrying {failed.Count} failed task(s).",
                    nameof(MID_SequentialProcessRunner));
                await RetryFailed(failed);
            }
        }

        private static async Task ExecuteTask(SequentialTask task, List<SequentialTask> failedList)
        {
            MID_Logger.LogDebug(LogLevel, $"Executing: '{task.Name}'",
                nameof(MID_SequentialProcessRunner));

            bool ok = await task.RunAsync();
            if (ok)
            {
                task.MarkComplete();
                _completed.Add(task.Name);
                OnTaskCompleted?.Invoke(task.Name);
                MID_Logger.LogDebug(LogLevel, $"Completed: '{task.Name}'",
                    nameof(MID_SequentialProcessRunner));
            }
            else
            {
                task.IncrementRetry();
                if (task.RetryCount < SequentialTask.MaxRetries)
                {
                    failedList.Add(task);
                    MID_Logger.LogWarning(LogLevel,
                        $"Failed: '{task.Name}' (retry {task.RetryCount}/{SequentialTask.MaxRetries})",
                        nameof(MID_SequentialProcessRunner));
                }
                else
                {
                    OnTaskFailed?.Invoke(task.Name);
                    MID_Logger.LogError(LogLevel,
                        $"'{task.Name}' exhausted {SequentialTask.MaxRetries} retries — giving up.",
                        nameof(MID_SequentialProcessRunner));
                }
            }
        }

        private static async Task RetryFailed(List<SequentialTask> failedList)
        {
            var toRetry = failedList.ToList();
            failedList.Clear();

            foreach (var task in toRetry)
            {
                bool ok = await task.RunAsync();
                if (!ok && task.HasFallback)
                {
                    MID_Logger.LogInfo(LogLevel,
                        $"Trying fallback for '{task.Name}'.",
                        nameof(MID_SequentialProcessRunner));
                    ok = await task.RunFallbackAsync();
                }

                if (ok)
                {
                    task.MarkComplete();
                    _completed.Add(task.Name);
                    OnTaskCompleted?.Invoke(task.Name);
                    MID_Logger.LogDebug(LogLevel, $"Retry succeeded: '{task.Name}'",
                        nameof(MID_SequentialProcessRunner));
                }
                else
                {
                    task.IncrementRetry();
                    if (task.RetryCount < SequentialTask.MaxRetries)
                    {
                        failedList.Add(task); // re-queue for next RunAll call
                        MID_Logger.LogWarning(LogLevel,
                            $"Retry failed: '{task.Name}' ({task.RetryCount}/{SequentialTask.MaxRetries})",
                            nameof(MID_SequentialProcessRunner));
                    }
                    else
                    {
                        OnTaskFailed?.Invoke(task.Name);
                        MID_Logger.LogError(LogLevel,
                            $"'{task.Name}' exhausted all retries including fallback.",
                            nameof(MID_SequentialProcessRunner));
                    }
                }

                if (DelayBetweenTasksMs > 0) await Task.Delay(DelayBetweenTasksMs);
            }
        }

        #endregion

        #region Helpers

        private static void EnsureLane(int lane)
        {
            if (!_lanes.ContainsKey(lane))  _lanes[lane]  = new Queue<SequentialTask>();
            if (!_failed.ContainsKey(lane)) _failed[lane] = new List<SequentialTask>();
        }

        #endregion
    }
}
