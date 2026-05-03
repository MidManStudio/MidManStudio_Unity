// MID_TickDelay.cs
// Zero-allocation delayed action system built on MID_TickDispatcher.
//
// FIX vs previous version:
//   stackalloc inside a stored lambda gets heap-allocated by the compiler.
//   Replaced with per-rate pre-allocated int[] fire buffers — true zero alloc.
//   Lock removed from tick callback hot path (callbacks run on main thread only).
//   Lock retained on AllocateSlot / FreeSlot for thread-safe external calls.

using System;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.TickDispatcher
{
    // ── Public handle ─────────────────────────────────────────────────────────

    /// <summary>
    /// Cancellable handle for a pending TickDelay.
    /// Generation counter prevents stale handles from cancelling recycled slots.
    /// </summary>
    public struct TickDelayHandle
    {
        internal int    SlotIndex;
        internal ushort Generation;

        internal static readonly TickDelayHandle Invalid =
            new TickDelayHandle { SlotIndex = -1, Generation = 0 };

        public bool IsValid => SlotIndex >= 0;

        public void Cancel() => MID_TickDelay.Cancel(this);
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Static entry point for tick-based delayed and repeating actions.
    /// Zero heap allocations after warm-up — uses a fixed slot pool.
    /// </summary>
    public static class MID_TickDelay
    {
        #region Configuration

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

        #region Pool — arrays of primitives, no GC after init

        private static float[]   _remaining;     // seconds until next fire
        private static float[]   _interval;      // repeat interval (for Repeat/Forever)
        private static int[]     _repeatLeft;    // -1 = infinite, 0 = done, N = fires remaining
        private static Action[]  _actions;
        private static TickRate[] _rates;
        private static bool[]    _active;
        private static ushort[]  _generation;

        // Per-rate subscriber tracking
        private static int[]     _rateRefCount;
        private static int       _rateCount;

        // Per-rate tick callbacks (one per TickRate value, registered once)
        private static MID_TickDispatcher.TickCallback[] _tickCallbacks;

        // ── Pre-allocated fire buffers — one per rate, never heap-allocated ──
        // KEY FIX: these replace stackalloc inside lambdas.
        // Tick callbacks run on the main thread → no cross-thread access to these buffers.
        // Slots: positive = fire-and-free index, negative = -(slot+1) = repeat/fire index.
        private static int[][] _firedBuffers;

        private static readonly object _lock = new();
        private static bool _initialised;
        private static int  _activeCount;

        #endregion

        #region Public API

        /// <summary>
        /// Execute <paramref name="action"/> once after <paramref name="seconds"/>.
        /// Returns a cancellable handle.
        /// </summary>
        public static TickDelayHandle After(float seconds, Action action,
    TickRate rate = TickRate.Tick_0_1)
{
    if (action == null)
    {
        MID_Logger.LogWarning(MID_LogLevel.Error,
            "MID_TickDelay.After — null action ignored.",
            nameof(MID_TickDelay));
        return TickDelayHandle.Invalid;
    }

    rate = ClampRate(rate);
    EnsureInitialised();
    return AllocateSlot(Mathf.Max(0f, seconds), action, rate, repeatCount: 1);
}
        /// <summary>
        /// Execute <paramref name="action"/> <paramref name="times"/> times,
        /// separated by <paramref name="intervalSeconds"/>.
        /// </summary>
        public static TickDelayHandle Repeat(float intervalSeconds, int times, Action action,
    TickRate rate = TickRate.Tick_0_1)
{
    if (action == null)
    {
        MID_Logger.LogWarning(MID_LogLevel.Error,
            "MID_TickDelay.Repeat — null action ignored.",
            nameof(MID_TickDelay));
        return TickDelayHandle.Invalid;
    }

    rate = ClampRate(rate);
    EnsureInitialised();
    return AllocateSlot(Mathf.Max(0.01f, intervalSeconds), action,
        rate, repeatCount: Mathf.Max(1, times));
}

        /// <summary>
        /// Execute <paramref name="action"/> every <paramref name="intervalSeconds"/>
        /// until the handle is cancelled.
        /// </summary>
        public static TickDelayHandle RepeatForever(float intervalSeconds, Action action,
    TickRate rate = TickRate.Tick_0_1)
{
    if (action == null)
    {
        MID_Logger.LogWarning(MID_LogLevel.Error,
            "MID_TickDelay.RepeatForever — null action ignored.",
            nameof(MID_TickDelay));
        return TickDelayHandle.Invalid;
    }

    rate = ClampRate(rate);
    EnsureInitialised();
    return AllocateSlot(Mathf.Max(0.01f, intervalSeconds), action,
        rate, repeatCount: -1);
}

        /// <summary>Cancel a pending delay. Safe to call with invalid or already-fired handles.</summary>
        public static void Cancel(TickDelayHandle handle)
        {
            if (!handle.IsValid) return;
            lock (_lock)
            {
                int i = handle.SlotIndex;
                if (i < 0 || i >= _capacity) return;
                if (!_active[i])              return;
                if (_generation[i] != handle.Generation) return;
                FreeSlot(i);
            }
        }

        /// <summary>Cancel all pending delays. Call on scene unload.</summary>
        public static void CancelAll()
        {
            if (!_initialised) return;
            lock (_lock)
            {
                for (int i = 0; i < _capacity; i++)
                    if (_active[i]) FreeSlot(i);
            }
        }

        public static int ActiveCount => _activeCount;

        #endregion

        #region Slot Allocation

        private static TickDelayHandle AllocateSlot(float seconds, Action action,
            TickRate rate, int repeatCount)
        {
            lock (_lock)
            {
                // Find free slot — linear scan, pool is typically small
                int slot = -1;
                for (int i = 0; i < _capacity; i++)
                    if (!_active[i]) { slot = i; break; }

                if (slot < 0)
                {
                    MID_Logger.LogWarning(MID_LogLevel.Error,
                        $"MID_TickDelay pool exhausted (capacity={_capacity}). " +
                        "Increase MID_TickDelay.PoolCapacity. Action dropped.",
                        nameof(MID_TickDelay));
                    return TickDelayHandle.Invalid;
                }

                _remaining[slot]  = seconds;
                _interval[slot]   = seconds;
                _repeatLeft[slot] = repeatCount;
                _actions[slot]    = action;
                _rates[slot]      = rate;
                _active[slot]     = true;
                // generation was incremented at free time; leave as-is here

                _activeCount++;

                // Subscribe this rate's tick callback if first subscriber on that rate
                int rateIdx = (int)rate;
                if (_rateRefCount[rateIdx] == 0)
                    MID_TickDispatcher.Subscribe(rate, _tickCallbacks[rateIdx]);
                _rateRefCount[rateIdx]++;

                return new TickDelayHandle
                {
                    SlotIndex  = slot,
                    Generation = _generation[slot]
                };
            }
        }
// ── Rate guard ────────────────────────────────────────────────────────────

/// <summary>
/// Minimum tick rate allowed for delays.
/// Tick_0_05 and faster are too close to frame rate to be useful as delays
/// and would fire death-spiral warnings if callbacks take any real work.
/// </summary>
private const TickRate MinAllowedRate = TickRate.Tick_0_1;

private static TickRate ClampRate(TickRate requested)
{
    // TickRate enum is ordered fastest→slowest (Tick_0_01=0, Tick_0_1=3, etc.)
    // Lower enum value = faster rate = more likely to cause problems.
    if ((int)requested < (int)MinAllowedRate)
    {
        MID_Logger.LogWarning(MID_LogLevel.Info,
            $"MID_TickDelay rate {requested} is faster than the minimum " +
            $"({MinAllowedRate}). Clamped to {MinAllowedRate}. " +
            "Use Tick_0_1 or slower for delays.",
            nameof(MID_TickDelay));
        return MinAllowedRate;
    }
    return requested;
}
        // Must be called inside lock
        private static void FreeSlot(int i)
        {
            if (!_active[i]) return;

            TickRate rate    = _rates[i];
            _active[i]       = false;
            _actions[i]      = null;
            unchecked { _generation[i]++; }
            _activeCount--;

            int rateIdx = (int)rate;
            _rateRefCount[rateIdx]--;
            if (_rateRefCount[rateIdx] == 0)
                MID_TickDispatcher.Unsubscribe(rate, _tickCallbacks[rateIdx]);
        }

        #endregion

        #region Tick Dispatch — main thread only, no lock needed in hot path

        private static void BuildTickCallback(TickRate rate)
        {
            int   rateIdx  = (int)rate;
            int[] fireBuf  = _firedBuffers[rateIdx]; // pre-allocated, no heap alloc per tick

            _tickCallbacks[rateIdx] = (float dt) =>
            {
                // ── Phase 1: determine which slots fire this tick ─────────────
                // Runs on main thread — no lock needed here.
                // AllocateSlot/FreeSlot are the only other writers and they lock.
                // We read _active/_remaining under the assumption that writes
                // from locked external callers are visible (memory model on x64/ARM).
                int firedCount = 0;

                for (int i = 0; i < _capacity; i++)
                {
                    if (!_active[i] || _rates[i] != rate) continue;

                    _remaining[i] -= dt;
                    if (_remaining[i] > 0f) continue;

                    // Slot fires this tick
                    if (_repeatLeft[i] == 1)
                    {
                        // Final fire — encode as positive (fire then free)
                        fireBuf[firedCount++] = i;
                    }
                    else if (_repeatLeft[i] > 1)
                    {
                        // More repeats — reset timer, encode as negative (fire, keep alive)
                        _remaining[i]  = _interval[i];
                        _repeatLeft[i]--;
                        fireBuf[firedCount++] = -(i + 1);
                    }
                    else // -1 = infinite
                    {
                        _remaining[i] = _interval[i];
                        fireBuf[firedCount++] = -(i + 1);
                    }
                }

                // ── Phase 2: free one-shot slots and invoke all ───────────────
                for (int fi = 0; fi < firedCount; fi++)
                {
                    int encoded    = fireBuf[fi];
                    bool shouldFree = encoded >= 0;
                    int  slotIdx   = shouldFree ? encoded : (-encoded - 1);

                    if (slotIdx < 0 || slotIdx >= _capacity) continue;

                    Action act = _actions[slotIdx];

                    if (shouldFree)
                    {
                        lock (_lock) { FreeSlot(slotIdx); }
                    }

                    try { act?.Invoke(); }
                    catch (Exception e)
                    {
                        MID_Logger.LogError(MID_LogLevel.Error,
                            $"Exception in TickDelay action: {e.Message}",
                            nameof(MID_TickDelay));
                    }
                }
            };
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
                // Unsubscribe active callbacks before wiping
                if (_initialised && _tickCallbacks != null)
                {
                    for (int r = 0; r < _rateCount; r++)
                    {
                        if (_rateRefCount != null && _rateRefCount[r] > 0 &&
                            _tickCallbacks[r] != null)
                            MID_TickDispatcher.Unsubscribe((TickRate)r, _tickCallbacks[r]);
                    }
                }

                _remaining  = new float[_capacity];
                _interval   = new float[_capacity];
                _repeatLeft = new int[_capacity];
                _actions    = new Action[_capacity];
                _rates      = new TickRate[_capacity];
                _active     = new bool[_capacity];
                _generation = new ushort[_capacity];

                var allRates  = (TickRate[])Enum.GetValues(typeof(TickRate));
                _rateCount    = allRates.Length;
                _rateRefCount = new int[_rateCount];
                _tickCallbacks = new MID_TickDispatcher.TickCallback[_rateCount];

                // ── Pre-allocate fire buffers — one per rate ──────────────────
                // Worst case: every slot fires in one tick = _capacity entries.
                _firedBuffers = new int[_rateCount][];
                for (int r = 0; r < _rateCount; r++)
                    _firedBuffers[r] = new int[_capacity];

                // Build callbacks after buffers exist (lambdas capture buffer refs)
                for (int r = 0; r < _rateCount; r++)
                    BuildTickCallback(allRates[r]);

                _activeCount = 0;
                _initialised = true;

                MID_Logger.LogInfo(MID_LogLevel.Info,
                    $"MID_TickDelay initialised — pool capacity={_capacity}.",
                    nameof(MID_TickDelay));
            }
        }

        #endregion
    }
}
