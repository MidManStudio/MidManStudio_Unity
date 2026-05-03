// MID_TickDelay.cs
// Zero-allocation delayed action system built on MID_TickDispatcher.
//
// ZERO-ALLOC DESIGN:
//   All TickDispatcher callbacks are subscribed ONCE during Reinitialise() and
//   kept subscribed permanently. Each callback does a cheap ref-count check and
//   early-returns when no delays are active for that rate. This eliminates the
//   HashSet.Add / string-interpolation allocations that would otherwise occur
//   every time After() was called after a quiet period.
//
//   AllocateSlot() and FreeSlot() never touch the TickDispatcher subscription
//   list — zero allocation on the hot path after warm-up.

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
    /// Zero heap allocations on the hot path — permanent dispatcher subscriptions
    /// with idle early-return instead of subscribe/unsubscribe per-cycle.
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
        private static float[]   _interval;      // repeat interval
        private static int[]     _repeatLeft;    // -1 = infinite, 0 = done, N = fires remaining
        private static Action[]  _actions;
        private static TickRate[] _rates;
        private static bool[]    _active;
        private static ushort[]  _generation;

        // Per-rate active-delay reference counts.
        // Used ONLY for the idle early-return in tick callbacks —
        // NOT for managing subscriptions (those are permanent).
        private static int[]     _rateRefCount;
        private static int       _rateCount;

        // One callback per TickRate, subscribed once and never unsubscribed
        // (until Reinitialise() rebuilds everything).
        private static MID_TickDispatcher.TickCallback[] _tickCallbacks;

        // Pre-allocated per-rate fire buffers — avoids stackalloc inside lambdas.
        // Encoded: >= 0 = fire-then-free slot index; < 0 = -(slot+1) = fire-and-keep slot.
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
                // Linear scan — pool is typically small and stays warm in L1.
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

                _activeCount++;
                _rateRefCount[(int)rate]++;
                // Subscription is permanent — no Subscribe call needed here.

                return new TickDelayHandle
                {
                    SlotIndex  = slot,
                    Generation = _generation[slot]
                };
            }
        }

        // ── Rate guard ────────────────────────────────────────────────────────

        private const TickRate MinAllowedRate = TickRate.Tick_0_1;

        private static TickRate ClampRate(TickRate requested)
        {
            if ((int)requested < (int)MinAllowedRate)
            {
                MID_Logger.LogWarning(MID_LogLevel.Info,
                    $"MID_TickDelay rate {requested} is faster than the minimum " +
                    $"({MinAllowedRate}). Clamped to {MinAllowedRate}.",
                    nameof(MID_TickDelay));
                return MinAllowedRate;
            }
            return requested;
        }

        // Must be called inside lock
        private static void FreeSlot(int i)
        {
            if (!_active[i]) return;

            int rateIdx      = (int)_rates[i];
            _active[i]       = false;
            _actions[i]      = null;
            unchecked { _generation[i]++; }
            _activeCount--;
            _rateRefCount[rateIdx]--;
            // Subscription is permanent — no Unsubscribe call here.
        }

        #endregion

        #region Tick Dispatch — main thread only

        private static void BuildTickCallback(TickRate rate)
        {
            int   rateIdx = (int)rate;
            int[] fireBuf = _firedBuffers[rateIdx]; // pre-allocated, never heap-allocated per tick

            _tickCallbacks[rateIdx] = (float dt) =>
            {
                // Cheap idle early-return — zero cost when no delays are active for this rate.
                if (_rateRefCount[rateIdx] == 0) return;

                // ── Phase 1: determine which slots fire this tick ─────────────
                int firedCount = 0;

                for (int i = 0; i < _capacity; i++)
                {
                    if (!_active[i] || _rates[i] != rate) continue;

                    _remaining[i] -= dt;
                    if (_remaining[i] > 0f) continue;

                    if (_repeatLeft[i] == 1)
                    {
                        // Final fire — encode as positive (fire then free)
                        fireBuf[firedCount++] = i;
                    }
                    else if (_repeatLeft[i] > 1)
                    {
                        // More repeats — reset timer
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

                // ── Phase 2: free one-shot slots then invoke all ──────────────
                for (int fi = 0; fi < firedCount; fi++)
                {
                    int encoded    = fireBuf[fi];
                    bool shouldFree = encoded >= 0;
                    int  slotIdx   = shouldFree ? encoded : (-encoded - 1);

                    if (slotIdx < 0 || slotIdx >= _capacity) continue;

                    Action act = _actions[slotIdx];

                    if (shouldFree)
                        lock (_lock) { FreeSlot(slotIdx); }

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
                // Unsubscribe ALL existing callbacks before rebuilding.
                // (With permanent subscriptions every rate is subscribed, so we
                //  no longer check ref counts here.)
                if (_initialised && _tickCallbacks != null)
                {
                    for (int r = 0; r < _rateCount; r++)
                    {
                        if (_tickCallbacks[r] != null)
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

                var allRates   = (TickRate[])Enum.GetValues(typeof(TickRate));
                _rateCount     = allRates.Length;
                _rateRefCount  = new int[_rateCount];
                _tickCallbacks = new MID_TickDispatcher.TickCallback[_rateCount];

                // Pre-allocate fire buffers — one per rate, sized to worst-case (all slots fire).
                _firedBuffers = new int[_rateCount][];
                for (int r = 0; r < _rateCount; r++)
                    _firedBuffers[r] = new int[_capacity];

                // Build callbacks BEFORE subscribing so the lambda captures the buffer refs.
                for (int r = 0; r < _rateCount; r++)
                    BuildTickCallback(allRates[r]);

                // Permanently subscribe all rates.
                // Each callback early-returns when _rateRefCount == 0 (zero cost at idle).
                // This avoids Subscribe/Unsubscribe (and associated string allocations) on
                // every After() call when a rate transitions from idle to active.
                for (int r = 0; r < _rateCount; r++)
                    MID_TickDispatcher.Subscribe(allRates[r], _tickCallbacks[r]);

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
