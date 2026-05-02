// MID_SequentialProcessRunner.cs
// Generic sequential task runner with priority lanes and retry logic.
// No cloud / internet dependencies — those concerns belong in game code.
//
// LANE PRIORITY ORDER (lower = runs first):
//   Priority0 → runs first (high priority, blocking)
//   Priority1 → runs after Priority0 completes
//   Priority2 → runs after Priority1 completes (low priority, background)
//
// USAGE:
//   MID_SequentialProcessRunner.AddTask(
//       new SequentialTask("LoadUserProfile", lane: 0,
//           execute: async () => { ... return true; },
//           fallback: async () => { ... return true; })); // optional offline fallback
//
//   MID_SequentialProcessRunner.OnAllLanesComplete += OnInitDone;
//   await MID_SequentialProcessRunner.RunAll();

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.SequentialProcessing
{
    // ── Task definition ───────────────────────────────────────────────────────

    public class SequentialTask
    {
        public const int MaxRetries = 6;

        public string Name            { get; }
        public int    Lane            { get; }
        public bool   HasFallback     { get; }
        public int    RetryCount      { get; private set; }
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
    /// Runs tasks sequentially across priority lanes with retry and fallback support.
    /// </summary>
    public static class MID_SequentialProcessRunner
    {
        #region Configuration

        public static MID_LogLevel LogLevel       = MID_LogLevel.Info;
        public static int          DelayBetweenTasksMs = 50;

        #endregion

        #region Events

        public static Action        OnAllLanesComplete;
        public static Action<int>   OnLaneComplete;   // lane index
        public static Action<string> OnTaskCompleted; // task name
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

        /// <summary>Reset all state so RunAll can be called again.</summary>
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
