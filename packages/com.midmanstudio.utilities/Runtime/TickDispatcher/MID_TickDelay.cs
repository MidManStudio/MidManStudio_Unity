// MID_TickDelay.cs
// Zero-allocation delayed action system built on MID_TickDispatcher.
//
// ZERO-ALLOC CONTRACT — READ BEFORE USE:
//   MID_TickDelay.After() itself performs zero heap allocations after warm-up.
//   However, converting a method group to Action allocates in Unity 2019.2+.
//
//   WRONG — allocates every call:
//     MID_TickDelay.After(1f, MyStaticMethod);   // method group → allocates
//     MID_TickDelay.After(1f, () => DoWork());   // non-static lambda → closure
//
//   CORRECT — pre-allocate the delegate once and reuse:
//     private static readonly Action _onTick = HandleTick;
//     MID_TickDelay.After(1f, _onTick);          // zero alloc ✓
//
// MINIMUM RATE:
//   MID_TickDelay enforces a minimum rate of Tick_0_1.
//   Faster rates (Tick_0_05, Tick_0_02, Tick_0_01) are clamped.
//   These rates fire faster than a typical frame — the dispatcher cost
//   exceeds any benefit. Use Coroutine or Task for sub-frame timing instead.
//
// POOL SIZING:
//   Default capacity = 64 slots. Raise before the first call if your game
//   schedules more than 64 simultaneous delays:
//     MID_TickDelay.PoolCapacity = 128;

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
    /// Zero heap allocations inside After() on the hot path.
    /// Callers are responsible for pre-allocating their Action delegates.
    /// </summary>
    public static class MID_TickDelay
    {
        #region Configuration

        /// <summary>
        /// Maximum number of simultaneous pending delays.
        /// Set before the first call. Changing at runtime rebuilds the pool.
        /// Default: 64.
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

        /// <summary>
        /// The minimum allowed tick rate. Rates faster than this are clamped.
        /// MID_TickDelay only subscribes to rates at or above this value.
        /// </summary>
        private static readonly TickRate MinAllowedRate = TickRate.Tick_0_1;

        #endregion

        #region Pool

        private static float[]    _remaining;
        private static float[]    _interval;
        private static int[]      _repeatLeft;   // -1=infinite  0=done  N=fires left
        private static Action[]   _actions;
        private static TickRate[] _rates;
        private static bool[]     _active;
        private static ushort[]   _generation;

        // Per-rate active-delay ref counts — used only for the idle early-return.
        private static int[]      _rateRefCount;
        private static int        _rateCount;

        // One permanent callback per allowed rate (subscribed once, never removed).
        private static MID_TickDispatcher.TickCallback[] _tickCallbacks;

        // Pre-allocated fire buffers — one per rate, avoids stackalloc per tick.
        // Encoding: >= 0 = fire-then-free slot;  < 0 = -(slot+1) = fire-and-keep.
        private static int[][]    _firedBuffers;

        // Which rates are actually subscribed (only those >= MinAllowedRate).
        private static bool[]     _rateSubscribed;

        private static readonly object _lock = new();
        private static bool _initialised;
        private static int  _activeCount;

        #endregion

        #region Public API

        /// <summary>
        /// Execute <paramref name="action"/> once after <paramref name="seconds"/>.
        /// <para>
        /// IMPORTANT: Pre-allocate your delegate to guarantee zero GC:
        ///   private static readonly Action _cb = MyMethod;
        ///   MID_TickDelay.After(1f, _cb);
        /// </para>
        /// Returns a cancellable handle. Rate is clamped to Tick_0_1 minimum.
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
        /// Rate is clamped to Tick_0_1 minimum.
        /// </summary>
        public static TickDelayHandle Repeat(float intervalSeconds, int times,
            Action action, TickRate rate = TickRate.Tick_0_1)
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
        /// Rate is clamped to Tick_0_1 minimum.
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

        /// <summary>
        /// Cancel a pending delay. Safe to call with invalid or already-fired handles.
        /// </summary>
        public static void Cancel(TickDelayHandle handle)
        {
            if (!handle.IsValid) return;
            lock (_lock)
            {
                int i = handle.SlotIndex;
                if (i < 0 || i >= _capacity) return;
                if (!_active[i])             return;
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

        /// <summary>Number of currently active pending delays.</summary>
        public static int ActiveCount => _activeCount;

        #endregion

        #region Slot Management

        private static TickDelayHandle AllocateSlot(float seconds, Action action,
            TickRate rate, int repeatCount)
        {
            lock (_lock)
            {
                int slot = -1;
                for (int i = 0; i < _capacity; i++)
                    if (!_active[i]) { slot = i; break; }

                if (slot < 0)
                {
                    MID_Logger.LogWarning(MID_LogLevel.Error,
                        $"MID_TickDelay pool exhausted (capacity={_capacity}). " +
                        "Increase MID_TickDelay.PoolCapacity before first use. Action dropped.",
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

                return new TickDelayHandle
                {
                    SlotIndex  = slot,
                    Generation = _generation[slot]
                };
            }
        }

        // Must be called inside lock.
        private static void FreeSlot(int i)
        {
            if (!_active[i]) return;
            _rateRefCount[(int)_rates[i]]--;
            _active[i]    = false;
            _actions[i]   = null;
            _activeCount--;
            unchecked { _generation[i]++; }
        }

        #endregion

        #region Rate Guard

        private static bool IsAllowedRate(TickRate rate) => rate >= MinAllowedRate;

        private static TickRate ClampRate(TickRate requested)
        {
            if (!IsAllowedRate(requested))
            {
                MID_Logger.LogWarning(MID_LogLevel.Info,
                    $"MID_TickDelay does not support rates faster than {MinAllowedRate}. " +
                    $"Rates below {MinAllowedRate} fire faster than a typical frame — " +
                    $"the dispatcher overhead exceeds any gain. " +
                    $"Requested {requested} → clamped to {MinAllowedRate}. " +
                    $"Use a Coroutine for sub-frame precision.",
                    nameof(MID_TickDelay));
                return MinAllowedRate;
            }
            return requested;
        }

        #endregion

        #region Tick Dispatch — main thread only

        private static void BuildTickCallback(TickRate rate, int rateIdx)
        {
            int[]  fireBuf = _firedBuffers[rateIdx];

            _tickCallbacks[rateIdx] = (float dt) =>
            {
                // Cheap idle early-return — zero cost when no delays use this rate.
                if (_rateRefCount[rateIdx] == 0) return;

                // ── Phase 1: collect firing slots ─────────────────────────────
                int firedCount = 0;

                for (int i = 0; i < _capacity; i++)
                {
                    if (!_active[i] || _rates[i] != rate) continue;

                    _remaining[i] -= dt;
                    if (_remaining[i] > 0f) continue;

                    if (_repeatLeft[i] == 1)
                    {
                        fireBuf[firedCount++] = i;           // positive = fire then free
                    }
                    else if (_repeatLeft[i] > 1)
                    {
                        _remaining[i]  = _interval[i];
                        _repeatLeft[i]--;
                        fireBuf[firedCount++] = -(i + 1);    // negative = fire and keep
                    }
                    else // -1 = infinite
                    {
                        _remaining[i] = _interval[i];
                        fireBuf[firedCount++] = -(i + 1);
                    }
                }

                // ── Phase 2: free one-shot slots, then invoke all ─────────────
                for (int fi = 0; fi < firedCount; fi++)
                {
                    int  encoded    = fireBuf[fi];
                    bool shouldFree = encoded >= 0;
                    int  slotIdx    = shouldFree ? encoded : (-encoded - 1);

                    if (slotIdx < 0 || slotIdx >= _capacity) continue;

                    Action act = _actions[slotIdx];

                    if (shouldFree)
                        lock (_lock) { FreeSlot(slotIdx); }

                    try
                    {
                        act?.Invoke();
                    }
                    catch (Exception e)
                    {
                        MID_Logger.LogError(MID_LogLevel.Error,
                            $"Exception in MID_TickDelay action: {e.Message}",
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
                // Unsubscribe previously subscribed callbacks.
                if (_initialised && _tickCallbacks != null && _rateSubscribed != null)
                {
                    var prevRates = (TickRate[])Enum.GetValues(typeof(TickRate));
                    for (int r = 0; r < Math.Min(prevRates.Length, _tickCallbacks.Length); r++)
                    {
                        if (_rateSubscribed[r] && _tickCallbacks[r] != null)
                            MID_TickDispatcher.Unsubscribe(prevRates[r], _tickCallbacks[r]);
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
                _rateSubscribed = new bool[_rateCount];

                _firedBuffers = new int[_rateCount][];
                for (int r = 0; r < _rateCount; r++)
                    _firedBuffers[r] = new int[_capacity];

                // Build callbacks for all rates.
                for (int r = 0; r < _rateCount; r++)
                    BuildTickCallback(allRates[r], r);

                // Subscribe ONLY to allowed rates (>= Tick_0_1).
                // Fast rates (Tick_0_01, Tick_0_02, Tick_0_05) are excluded —
                // they fire faster than a normal frame and cause death spiral warnings.
                for (int r = 0; r < _rateCount; r++)
                {
                    if (IsAllowedRate(allRates[r]))
                    {
                        MID_TickDispatcher.Subscribe(allRates[r], _tickCallbacks[r]);
                        _rateSubscribed[r] = true;
                    }
                    else
                    {
                        _rateSubscribed[r] = false;
                    }
                }

                _activeCount = 0;
                _initialised = true;

                MID_Logger.LogInfo(MID_LogLevel.Info,
                    $"MID_TickDelay initialised — pool capacity={_capacity}, " +
                    $"minimum rate={MinAllowedRate}.",
                    nameof(MID_TickDelay));
            }
        }

        #endregion
    }
}