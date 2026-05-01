// MID_TickDelay.cs
// Zero-allocation delayed action system built on MID_TickDispatcher.
// Replaces fire-and-forget coroutines and Task.Delay for simple timed callbacks.
//
// WHY NOT COROUTINES / TASK.DELAY:
//   StartCoroutine allocates a Coroutine object and an IEnumerator on the heap.
//   Task.Delay allocates a Task, a timer, and a state machine — plus a thread-pool
//   transition on continuation. For short in-gameplay delays (0.1s – 3s) these are
//   unnecessary. MID_TickDelay reuses a fixed pool of TickDelayHandle structs and
//   routes through the existing MID_TickDispatcher bucket — zero extra allocations
//   after the pool is full.
//
// POOL SIZE:
//   Default is 64 concurrent delays. Raise PoolCapacity if you need more.
//   If the pool is exhausted a warning is logged and the action is dropped.
//   The pool never allocates after warm-up.
//
// USAGE:
//   // Fire and forget
//   MID_TickDelay.After(1.5f, () => GiveItemToPlayer());
//
//   // With a specific tick rate (coarser = cheaper)
//   MID_TickDelay.After(0.5f, ShowPopup, TickRate.Tick_0_5);
//
//   // Cancellable
//   var handle = MID_TickDelay.After(3f, ExpireOffer);
//   handle.Cancel();
//
//   // Repeat N times
//   MID_TickDelay.Repeat(0.2f, 5, SpawnEnemy);
//
//   // Repeat until cancelled
//   var loopHandle = MID_TickDelay.RepeatForever(1f, PollServer);
//   loopHandle.Cancel();
//
//   // Chain (next delay starts after previous fires)
//   MID_TickDelay.After(1f, () =>
//   {
//       DoFirstThing();
//       MID_TickDelay.After(0.5f, DoSecondThing);
//   });

using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Timers;

namespace MidManStudio.Core.TickDispatcher
{
    // ── Public handle returned by every After / Repeat call ───────────────────

    /// <summary>
    /// Lightweight cancellable handle for a pending TickDelay.
    /// Call Cancel() at any time before the delay fires to abort it.
    /// Handles are invalidated automatically after they fire or are cancelled.
    /// </summary>
    public struct TickDelayHandle
    {
        internal int    SlotIndex;
        internal ushort Generation; // guards against stale handles pointing to recycled slots

        internal static readonly TickDelayHandle Invalid =
            new TickDelayHandle { SlotIndex = -1, Generation = 0 };

        public bool IsValid => SlotIndex >= 0;

        /// <summary>Cancel the pending delay. Safe to call if already fired or cancelled.</summary>
        public void Cancel() => MID_TickDelay.Cancel(this);
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Static entry point for all tick-based delayed actions.
    /// Internally manages a fixed-size slot pool — no heap allocations during gameplay.
    /// </summary>
    public static class MID_TickDelay
    {
        #region Configuration

        /// <summary>
        /// Maximum number of concurrent in-flight delays.
        /// Increase if you see "pool exhausted" warnings.
        /// Changing this after first use reinitialises the pool.
        /// </summary>
        public static int PoolCapacity
        {
            get => _capacity;
            set
            {
                if (value == _capacity) return;
                _capacity = Mathf.Max(8, value);
                Reinitialise();
            }
        }

        private static int _capacity = 64;

        #endregion

        #region Pool

        // One slot per possible concurrent delay.
        // Stored as arrays-of-fields rather than array-of-structs to keep hot data tight.
        private static float[]          _remaining;   // seconds left
        private static float[]          _interval;    // total interval (for Repeat)
        private static int[]            _repeatLeft;  // -1 = infinite, 0 = done, N = fires left
        private static Action[]         _actions;
        private static TickRate[]       _rates;
        private static bool[]           _active;
        private static ushort[]         _generation;  // incremented on recycle

        private static readonly object  _lock        = new();
        private static bool             _initialised;
        private static int              _activeCount;

        // Single shared dispatcher callback — registered once, removed when pool empties
        private static MID_TickDispatcher.TickCallback[] _tickCallbacks;

        // Per-rate subscriber tracking — we only subscribe a rate when ≥1 slot uses it
        private static int[] _rateRefCount;
        private static int   _rateCount;

        #endregion

        #region Public API

        /// <summary>
        /// Execute <paramref name="action"/> once after <paramref name="seconds"/>.
        /// Returns a handle that can be used to cancel before it fires.
        /// </summary>
        public static TickDelayHandle After(float seconds, Action action,
            TickRate rate = TickRate.Tick_0_1)
        {
            if (action == null)
            {
                MID_Logger.LogWarning(MID_LogLevel.Error,
                    "MID_TickDelay.After called with null action — ignoring.",
                    nameof(MID_TickDelay));
                return TickDelayHandle.Invalid;
            }

            seconds = Mathf.Max(0f, seconds);
            EnsureInitialised();
            return AllocateSlot(seconds, action, rate, repeatCount: 1);
        }

        /// <summary>
        /// Execute <paramref name="action"/> <paramref name="times"/> times,
        /// separated by <paramref name="intervalSeconds"/> each.
        /// </summary>
        public static TickDelayHandle Repeat(float intervalSeconds, int times, Action action,
            TickRate rate = TickRate.Tick_0_1)
        {
            if (action == null)
            {
                MID_Logger.LogWarning(MID_LogLevel.Error,
                    "MID_TickDelay.Repeat called with null action — ignoring.",
                    nameof(MID_TickDelay));
                return TickDelayHandle.Invalid;
            }

            times           = Mathf.Max(1, times);
            intervalSeconds = Mathf.Max(0.01f, intervalSeconds);
            EnsureInitialised();
            return AllocateSlot(intervalSeconds, action, rate, repeatCount: times);
        }

        /// <summary>
        /// Execute <paramref name="action"/> every <paramref name="intervalSeconds"/> forever
        /// until the returned handle is cancelled.
        /// </summary>
        public static TickDelayHandle RepeatForever(float intervalSeconds, Action action,
            TickRate rate = TickRate.Tick_0_1)
        {
            if (action == null)
            {
                MID_Logger.LogWarning(MID_LogLevel.Error,
                    "MID_TickDelay.RepeatForever called with null action — ignoring.",
                    nameof(MID_TickDelay));
                return TickDelayHandle.Invalid;
            }

            intervalSeconds = Mathf.Max(0.01f, intervalSeconds);
            EnsureInitialised();
            return AllocateSlot(intervalSeconds, action, rate, repeatCount: -1);
        }

        /// <summary>
        /// Cancel a pending delay. Safe to call with an invalid or already-fired handle.
        /// </summary>
        public static void Cancel(TickDelayHandle handle)
        {
            if (!handle.IsValid) return;
            lock (_lock)
            {
                int i = handle.SlotIndex;
                if (i < 0 || i >= _capacity) return;
                if (!_active[i])              return;
                if (_generation[i] != handle.Generation) return; // stale handle

                FreeSlot(i);
            }
        }

        /// <summary>
        /// Cancel all pending delays. Useful on scene unload.
        /// </summary>
        public static void CancelAll()
        {
            if (!_initialised) return;
            lock (_lock)
            {
                for (int i = 0; i < _capacity; i++)
                    if (_active[i]) FreeSlot(i);
            }
        }

        /// <summary>Number of currently active delays.</summary>
        public static int ActiveCount => _activeCount;

        #endregion

        #region Core — Allocation and Tick

        private static TickDelayHandle AllocateSlot(float seconds, Action action,
            TickRate rate, int repeatCount)
        {
            lock (_lock)
            {
                // Find free slot
                int slot = -1;
                for (int i = 0; i < _capacity; i++)
                {
                    if (!_active[i]) { slot = i; break; }
                }

                if (slot < 0)
                {
                    MID_Logger.LogWarning(MID_LogLevel.Error,
                        $"MID_TickDelay pool exhausted (capacity={_capacity}). " +
                        "Increase MID_TickDelay.PoolCapacity. Action dropped.",
                        nameof(MID_TickDelay));
                    return TickDelayHandle.Invalid;
                }

                // Write slot data
                _remaining[slot]  = seconds;
                _interval[slot]   = seconds;
                _repeatLeft[slot] = repeatCount;
                _actions[slot]    = action;
                _rates[slot]      = rate;
                _active[slot]     = true;
                // Generation already incremented on free; leave as-is on alloc

                _activeCount++;

                // Subscribe this rate's callback if not already active
                int rateIdx = (int)rate;
                if (_rateRefCount[rateIdx] == 0)
                {
                    MID_TickDispatcher.Subscribe(rate, _tickCallbacks[rateIdx]);
                }
                _rateRefCount[rateIdx]++;

                return new TickDelayHandle
                {
                    SlotIndex  = slot,
                    Generation = _generation[slot]
                };
            }
        }

        // One callback per tick rate — only the slots belonging to this rate are ticked
        private static void BuildTickCallback(TickRate rate)
        {
            int rateIdx = (int)rate;
            _tickCallbacks[rateIdx] = (float dt) =>
            {
                // Iterate all slots for this rate
                // Lock is NOT held during action invoke to avoid deadlocks.
                // We snapshot slots that fired and invoke outside the lock.
                Span<int> firedBuffer = stackalloc int[_capacity];
                int firedCount = 0;

                lock (_lock)
                {
                    for (int i = 0; i < _capacity; i++)
                    {
                        if (!_active[i] || _rates[i] != rate) continue;

                        _remaining[i] -= dt;
                        if (_remaining[i] > 0f) continue;

                        // This slot fires
                        if (_repeatLeft[i] > 0)
                        {
                            _repeatLeft[i]--;
                            if (_repeatLeft[i] == 0)
                            {
                                // Last fire — mark for invoke then free
                                firedBuffer[firedCount++] = i;
                                // Don't free yet — need to invoke first
                            }
                            else
                            {
                                // More repeats remain — reset timer, queue invoke
                                _remaining[i] = _interval[i];
                                firedBuffer[firedCount++] = -(i + 1); // negative = reset, not free
                            }
                        }
                        else if (_repeatLeft[i] == -1)
                        {
                            // Infinite repeat — reset timer, queue invoke
                            _remaining[i] = _interval[i];
                            firedBuffer[firedCount++] = -(i + 1);
                        }
                    }
                }

                // Invoke outside lock
                for (int fi = 0; fi < firedCount; fi++)
                {
                    int encoded = firedBuffer[fi];
                    bool shouldFree = encoded >= 0;
                    int  slot       = shouldFree ? encoded : (-encoded - 1);

                    if (slot < 0 || slot >= _capacity || !_active[slot]) continue;

                    Action act = _actions[slot];

                    if (shouldFree)
                    {
                        lock (_lock) { FreeSlot(slot); }
                    }

                    try
                    {
                        act?.Invoke();
                    }
                    catch (Exception e)
                    {
                        MID_Logger.LogError(MID_LogLevel.Error,
                            $"Exception in TickDelay action: {e.Message}",
                            nameof(MID_TickDelay));
                    }
                }
            };
        }

        // Must be called inside lock
        private static void FreeSlot(int i)
        {
            if (!_active[i]) return;

            TickRate rate    = _rates[i];
            _active[i]       = false;
            _actions[i]      = null;
            unchecked { _generation[i]++; } // invalidate existing handles
            _activeCount--;

            int rateIdx = (int)rate;
            _rateRefCount[rateIdx]--;

            if (_rateRefCount[rateIdx] == 0)
                MID_TickDispatcher.Unsubscribe(rate, _tickCallbacks[rateIdx]);
        }

        #endregion

        #region Initialisation

        private static void EnsureInitialised()
        {
            if (_initialised) return;
            Reinitialise();
        }

        private static void Reinitialise()
        {
            lock (_lock)
            {
                // Unsubscribe any active callbacks before wiping
                if (_initialised && _tickCallbacks != null)
                {
                    for (int r = 0; r < _rateCount; r++)
                    {
                        if (_rateRefCount != null && _rateRefCount[r] > 0 && _tickCallbacks[r] != null)
                            MID_TickDispatcher.Unsubscribe((TickRate)r, _tickCallbacks[r]);
                    }
                }

                _remaining    = new float[_capacity];
                _interval     = new float[_capacity];
                _repeatLeft   = new int[_capacity];
                _actions      = new Action[_capacity];
                _rates        = new TickRate[_capacity];
                _active       = new bool[_capacity];
                _generation   = new ushort[_capacity];

                // Count tick rates from the enum
                _rateCount    = Enum.GetValues(typeof(TickRate)).Length;
                _rateRefCount = new int[_rateCount];
                _tickCallbacks = new MID_TickDispatcher.TickCallback[_rateCount];

                foreach (TickRate rate in Enum.GetValues(typeof(TickRate)))
                    BuildTickCallback(rate);

                _activeCount  = 0;
                _initialised  = true;

                MID_Logger.LogInfo(MID_LogLevel.Info,
                    $"MID_TickDelay initialised — pool capacity={_capacity}.",
                    nameof(MID_TickDelay));
            }
        }

        #endregion
    }
}
