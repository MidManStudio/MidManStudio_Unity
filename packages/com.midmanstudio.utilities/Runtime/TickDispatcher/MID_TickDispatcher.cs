// ═══════════════════════════════════════════════════════════════════════════════
// MID_TickDispatcher
// MidMan Studio — Core Systems
//
// A zero-allocation, subscriber-based tick dispatcher that replaces per-
// MonoBehaviour Update() calls with shared interval-based callbacks.
//
// ── WHY USE THIS ────────────────────────────────────────────────────────────
//   Unity maintains an internal native list of every MonoBehaviour with an
//   Update() method. Each callback crosses the native-to-managed boundary once
//   per frame, regardless of whether the script has anything to do. At scale
//   (hundreds of enemies, timers, AI systems) this boundary crossing cost
//   dominates frame time even when callbacks are empty.
//
//   MID_TickDispatcher collapses all subscribers into a single Update() call
//   and dispatches callbacks only at the interval they need, not every frame.
//   A pathfinding system that only needs to recalculate 5×/sec should subscribe
//   to Tick_0_2, not run dead code 60×/sec.
//
// ── HOW IT WORKS ────────────────────────────────────────────────────────────
//   1. Each TickRate maps to a fixed time interval (Tick_0_2 fires every 0.2s).
//   2. Systems call MID_TickDispatcher.Subscribe(TickRate.Tick_0_2, OnTick).
//   3. One Update() accumulates deltaTime per interval bucket.
//   4. When a bucket's timer exceeds its interval, all subscribers are invoked.
//   5. Buckets with zero subscribers are skipped entirely — zero cost at idle.
//   6. Systems call Unsubscribe() in OnDisable/OnDestroy — never skip this.
//
// ── ZERO-ALLOCATION DESIGN ──────────────────────────────────────────────────
//   • Subscribers are stored in a HashSet<TickCallback> for O(1) add/remove.
//   • Invocation copies to a pre-allocated TickCallback[] (no GC per-frame).
//   • HashSet.CopyTo() avoids the enumerator allocation during dispatch.
//   • Empty buckets are skipped entirely — zero cost when inactive.
//   • Death spiral protection clamps accumulation before the dispatch loop,
//     so a lag spike can never generate a cascade of catch-up ticks.
//
// ── IS THIS AS OPTIMISED AS IT CAN BE? ──────────────────────────────────────
//   Yes — within managed C#. The remaining cost is unavoidable managed delegate
//   invocation. The two theoretical next steps are:
//
//   Burst Compiler: NOT applicable here. Burst requires fully unmanaged code
//   (NativeCollections, function pointers). Delegates are managed objects —
//   Burst cannot compile or invoke them. Adopting Burst would require scrapping
//   the delegate model entirely and rebuilding around NativeArray<FunctionPointer>
//   which is a fundamentally different (and far more complex) architecture.
//   Not worth it unless you are running 10 000+ subscribers per bucket.
//
//   DOTS/ECS: If your project is already ECS-based, a ComponentSystemGroup with
//   a custom update rate is the correct pattern. This dispatcher is optimal for
//   classic GameObject/MonoBehaviour projects.
//
// ── BENCHMARKED RESULTS (MidMan Studio, 200 subscribers, Tick_0_1 vs ~72 fps)
//   ┌────────────────────────┬────────────┬───────────────┬──────────────┐
//   │ Phase                  │ Update/sec │ Dispatcher/sec│ CPU Saved    │
//   ├────────────────────────┼────────────┼───────────────┼──────────────┤
//   │ A — Empty callbacks    │ 0.72 ms    │ 0.008 ms      │ 98.9 %       │
//   │ B — Heavy work (200×)  │ 102.74 ms  │ 25.38 ms      │ 75.3 %       │
//   │ C — CountdownTimers    │ 2.70 ms    │ 0.30 ms       │ 88.7 %       │
//   │ D — InterpTimers       │ 2.82 ms    │ 0.48 ms       │ 80.5 %       │
//   │ E — Micro-bench raw    │ 2.24 ms    │ 2.29 ms       │ 0.969× ratio │
//   └────────────────────────┴────────────┴───────────────┴──────────────┘
//   Dispatcher overhead (CopyTo vs direct loop) ≈ 0.97–1.14× — effectively
//   identical per-fire cost. All savings come purely from reduced fire rate.
//
// ── MINIMUM TICK RATE — READ THIS BEFORE USING FAST RATES ───────────────────
//   The dispatcher only saves CPU when it fires LESS often than your frame rate.
//   The moment tick rate >= fps, you are paying dispatch overhead on top of the
//   work cost with zero frequency saving. Results at 200 subscribers, ~72 fps:
//
//   Tick_0_1  (10/sec)  — 75–99% saving.  RECOMMENDED minimum for most systems.
//   Tick_0_05 (20/sec)  — ~50–60% saving. Acceptable for fast-reaction systems.
//   Tick_0_02 (50/sec)  — 1–28% saving.   Marginal. Only if 50hz is genuinely
//                          needed AND your target fps is well above 50.
//   Tick_0_01 (100/sec) — NEGATIVE saving at 72 fps (-77% to -191%).
//                          The dispatcher costs MORE than raw Update at this rate.
//                          Only valid if guaranteed runtime fps > 100.
//                          Death spiral warnings at normal fps are expected and
//                          correct — they are telling you this rate is misused.
//
//   HARD RULE: Never use Tick_0_02 or Tick_0_01 for gameplay logic.
//   Reserve them for input polling or physics helpers that explicitly require
//   sub-frame precision AND only when fps is guaranteed to exceed the tick rate.
//
// ── USAGE ───────────────────────────────────────────────────────────────────
//   void OnEnable()  => MID_TickDispatcher.Subscribe(TickRate.Tick_0_2, OnTick);
//   void OnDisable() => MID_TickDispatcher.Unsubscribe(TickRate.Tick_0_2, OnTick);
//   void OnTick(float deltaTime) { /* called 5×/sec instead of 60×/sec */ }
//
// ── ADDING NEW TICK RATES ───────────────────────────────────────────────────
//   1. Add an entry to the TickRate enum at the bottom of this file.
//      Follow the naming convention: Tick_<interval_in_seconds> using
//      underscores for decimal points (e.g. Tick_0_25 for every 0.25s).
//   2. Add a matching entry to _tickIntervals in the same class.
//   3. That is all — buckets are built from _tickIntervals automatically.
//
// ── DEPENDENCY ──────────────────────────────────────────────────────────────
//   MID_Logger (MidManStudio.Core.Logging)
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using MidManStudio.Core.Logging;

/// <summary>
/// Singleton tick dispatcher. Attach to a persistent GameObject in your
/// pre-load or bootstrap scene. Auto-creates itself if not found.
/// </summary>
public class MID_TickDispatcher : MonoBehaviour
{
    // ── Serialized configuration ─────────────────────────────────────────────

    [SerializeField]
    [Tooltip("Controls which log calls are emitted by this dispatcher. " +
             "Set to Warning or Error in production builds.")]
    private MID_LogLevel _logLevel = MID_LogLevel.Info;

    [Header("Death Spiral Protection")]

    [SerializeField]
    [Tooltip("Maximum number of catch-up ticks allowed per frame per bucket. " +
             "Prevents a lag spike from generating a flood of back-to-back callbacks.")]
    private int _maxTicksPerFrame = 5;

    [SerializeField]
    [Tooltip("When enabled, any bucket that hits the tick cap has its timer " +
             "reset to zero and emits a warning listing all subscribers.")]
    private bool _enableDeathSpiralProtection = true;

    // ── Editor runtime monitor ───────────────────────────────────────────────
    // Visible in the Inspector during Play mode. Zero runtime cost in builds.
    // Shows which tick rates are active and how many subscribers each has.

#if UNITY_EDITOR
    [Header("Live Bucket Monitor  (Editor Only)")]
    [SerializeField]
    [Tooltip("Refreshed every frame in Play mode. Shows active tick rates and " +
             "their current subscriber counts. IDLE buckets have zero subscribers " +
             "and are completely skipped in Update — they cost nothing.")]
    private List<TickRateStatus> _editorBucketStatus = new List<TickRateStatus>();
#endif

    // ── Tick interval table ──────────────────────────────────────────────────
    // To add a new rate: add to TickRate enum AND add a matching entry here.

    private static readonly Dictionary<TickRate, float> _tickIntervals =
        new Dictionary<TickRate, float>
        {
            { TickRate.Tick_0_01, 0.01f },   // 100/sec — DANGER ZONE. See header.
            { TickRate.Tick_0_02, 0.02f },   //  50/sec — marginal. See header.
            { TickRate.Tick_0_05, 0.05f },   //  20/sec — fast systems minimum.
            { TickRate.Tick_0_1,  0.1f  },   //  10/sec — recommended minimum.
            { TickRate.Tick_0_2,  0.2f  },   //   5/sec — standard AI / cooldowns.
            { TickRate.Tick_0_5,  0.5f  },   //   2/sec — area / perception checks.
            { TickRate.Tick_1,    1.0f  },   //   1/sec — health regen / UI.
            { TickRate.Tick_2,    2.0f  },   // 0.5/sec — ambient / distant objects.
            { TickRate.Tick_5,    5.0f  },   // 0.2/sec — spawners / wave logic.
        };

    // ── Singleton ────────────────────────────────────────────────────────────

    private static MID_TickDispatcher _instance;
    private static bool _isInitialized = false;
    private static bool _applicationIsQuitting = false;

    /// <summary>True while the application is shutting down.</summary>
    public static bool IsQuitting => _applicationIsQuitting;

    /// <summary>True once the dispatcher has been fully initialised.</summary>
    public static bool IsReady => _isInitialized && _instance != null;

    /// <summary>
    /// Auto-creates the dispatcher if it does not exist in the scene.
    /// Returns null during application quit.
    /// </summary>
    public static MID_TickDispatcher Instance
    {
        get
        {
            if (_applicationIsQuitting)
            {
                Debug.LogWarning("[MID_TickDispatcher] Instance requested during application quit.");
                return null;
            }

            if (_instance == null)
            {
                _instance = FindAnyObjectByType<MID_TickDispatcher>();
                if (_instance == null)
                {
                    var go = new GameObject("[MID_TickDispatcher]");
                    _instance = go.AddComponent<MID_TickDispatcher>();
                    DontDestroyOnLoad(go);
                }
            }

            return _instance;
        }
    }

    // ── Internal bucket storage ──────────────────────────────────────────────

    private readonly Dictionary<TickRate, TickBucket> _buckets =
        new Dictionary<TickRate, TickBucket>();

    // Pre-allocated invocation cache — grown on demand, never shrunk.
    private TickCallback[] _invokeCache = new TickCallback[64];
    private const int MAX_CACHE_SIZE = 512;

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            MID_Logger.LogWarning(_logLevel,
                "Duplicate MID_TickDispatcher detected — destroying extra instance.",
                nameof(MID_TickDispatcher), nameof(Awake));
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        InitialiseBuckets();
        _isInitialized = true;

#if UNITY_EDITOR
        RebuildEditorStatusList();
#endif

        MID_Logger.LogInfo(_logLevel,
            $"Initialised — {_buckets.Count} tick buckets ready.",
            nameof(MID_TickDispatcher), nameof(Awake));
    }

    private void Update()
    {
        if (!_isInitialized || _buckets.Count == 0) return;

        float rawDelta = Time.deltaTime;

        foreach (var kvp in _buckets)
        {
            TickRate   rate   = kvp.Key;
            TickBucket bucket = kvp.Value;

            // Buckets with no subscribers are skipped entirely.
            // They accumulate no time and invoke nothing — zero cost.
            if (!bucket.HasSubscribers) continue;

            // ── Death spiral protection ──────────────────────────────────────
            // Clamp accumulation BEFORE the dispatch loop so the timer can
            // never store more debt than _maxTicksPerFrame intervals.
            float maxAccumulation = bucket.Interval * (_maxTicksPerFrame - 1);
            bucket.Timer = Mathf.Min(bucket.Timer + rawDelta,
                                     maxAccumulation + rawDelta);

            int ticksFired = 0;
            while (bucket.Timer >= bucket.Interval && ticksFired < _maxTicksPerFrame)
            {
                bucket.Timer -= bucket.Interval;
                DispatchBucket(rate, bucket, bucket.Interval);
                ticksFired++;
            }

            if (_enableDeathSpiralProtection && ticksFired >= _maxTicksPerFrame)
            {
                bucket.Timer = 0f;
                MID_Logger.LogWarning(_logLevel,
                    $"Death spiral guard triggered on {rate} — timer reset. " +
                    $"Subscribers ({bucket.SubscriberCount}): {BuildSubscriberNames(bucket)}",
                    nameof(MID_TickDispatcher), nameof(Update));
            }
        }

#if UNITY_EDITOR
        RefreshEditorStatus();
#endif
    }

    private void OnApplicationQuit() => _applicationIsQuitting = true;

    private void OnDestroy()
    {
        if (_instance != this) return;

        MID_Logger.LogInfo(_logLevel,
            "Destroying — clearing all subscribers.",
            nameof(MID_TickDispatcher), nameof(OnDestroy));

        foreach (var bucket in _buckets.Values)
            bucket.Subscribers.Clear();

        _instance      = null;
        _isInitialized = false;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Subscribe a callback to a tick rate.
    /// Call from OnEnable. Always pair with Unsubscribe in OnDisable/OnDestroy.
    /// Duplicate subscriptions are silently ignored.
    /// </summary>
    /// <returns>True if added; false if already present or dispatcher unavailable.</returns>
    public static bool Subscribe(TickRate tickRate, TickCallback callback)
    {
        if (callback == null)
        {
            MID_Logger.LogError(MID_LogLevel.Error,
                "Subscribe called with a null callback — ignoring.",
                nameof(MID_TickDispatcher), nameof(Subscribe));
            return false;
        }

        if (_applicationIsQuitting) return false;

        var instance = Instance;
        if (instance == null || !_isInitialized)
        {
            MID_Logger.LogError(MID_LogLevel.Error,
                "Subscribe failed — dispatcher not initialised.",
                nameof(MID_TickDispatcher), nameof(Subscribe));
            return false;
        }

        if (!instance._buckets.TryGetValue(tickRate, out TickBucket bucket))
        {
            MID_Logger.LogError(MID_LogLevel.Error,
                $"TickRate '{tickRate}' not found in interval table. " +
                "Add it to both the TickRate enum and _tickIntervals.",
                nameof(MID_TickDispatcher), nameof(Subscribe));
            return false;
        }

        bool wasEmpty = !bucket.HasSubscribers;
        bool added    = bucket.Subscribers.Add(callback);

        if (added)
        {
            if (wasEmpty) bucket.Timer = 0f;

            MID_Logger.LogDebug(instance._logLevel,
                wasEmpty
                    ? $"First subscriber on '{tickRate}' — bucket ACTIVE. Total: {bucket.SubscriberCount}"
                    : $"Subscribed to '{tickRate}'. Total: {bucket.SubscriberCount}",
                nameof(MID_TickDispatcher), nameof(Subscribe));
        }
        else
        {
            MID_Logger.LogDebug(instance._logLevel,
                $"Duplicate Subscribe to '{tickRate}' ignored. Total: {bucket.SubscriberCount}",
                nameof(MID_TickDispatcher), nameof(Subscribe));
        }

        return added;
    }

    /// <summary>
    /// Remove a previously subscribed callback.
    /// Call from OnDisable or OnDestroy. Safe to call if not subscribed.
    /// </summary>
    /// <returns>True if found and removed.</returns>
    public static bool Unsubscribe(TickRate tickRate, TickCallback callback)
    {
        if (callback == null || _applicationIsQuitting) return false;

        var instance = Instance;
        if (instance == null || !_isInitialized) return false;

        if (!instance._buckets.TryGetValue(tickRate, out TickBucket bucket))
            return false;

        bool removed = bucket.Subscribers.Remove(callback);

        if (removed)
            MID_Logger.LogDebug(instance._logLevel,
                $"Unsubscribed from '{tickRate}'. Remaining: {bucket.SubscriberCount}" +
                (bucket.HasSubscribers ? "" : " — bucket IDLE"),
                nameof(MID_TickDispatcher), nameof(Unsubscribe));

        return removed;
    }

    /// <summary>Returns true if the callback is currently subscribed.</summary>
    public static bool IsSubscribed(TickRate tickRate, TickCallback callback)
    {
        if (callback == null || _instance == null || !_isInitialized) return false;
        return _instance._buckets.TryGetValue(tickRate, out TickBucket bucket)
               && bucket.Subscribers.Contains(callback);
    }

    /// <summary>
    /// Remove all subscribers from a specific tick rate.
    /// Use sparingly — prefer individual Unsubscribe calls.
    /// </summary>
    public static void ClearSubscribers(TickRate tickRate)
    {
        if (_applicationIsQuitting) return;
        var instance = Instance;
        if (instance == null || !_isInitialized) return;
        if (!instance._buckets.TryGetValue(tickRate, out TickBucket bucket)) return;

        int count = bucket.SubscriberCount;
        bucket.Subscribers.Clear();

        MID_Logger.LogInfo(instance._logLevel,
            $"Cleared {count} subscribers from '{tickRate}' — bucket IDLE.",
            nameof(MID_TickDispatcher), nameof(ClearSubscribers));
    }

    /// <summary>
    /// Remove every subscriber from every bucket.
    /// Intended for scene teardown or full system reset only.
    /// </summary>
    public static void ClearAllSubscribers()
    {
        if (_applicationIsQuitting) return;
        var instance = Instance;
        if (instance == null || !_isInitialized) return;

        int total = 0;
        foreach (var bucket in instance._buckets.Values)
        {
            total += bucket.SubscriberCount;
            bucket.Subscribers.Clear();
        }

        MID_Logger.LogInfo(instance._logLevel,
            $"Cleared {total} subscribers across all buckets — all buckets IDLE.",
            nameof(MID_TickDispatcher), nameof(ClearAllSubscribers));
    }

    // ── Query helpers ────────────────────────────────────────────────────────

    /// <summary>Number of callbacks subscribed to the given tick rate.</summary>
    public static int GetSubscriberCount(TickRate tickRate)
    {
        if (_instance == null || !_isInitialized) return 0;
        return _instance._buckets.TryGetValue(tickRate, out TickBucket b)
            ? b.SubscriberCount : 0;
    }

    /// <summary>True if at least one callback is subscribed to this rate.</summary>
    public static bool IsTickRateActive(TickRate tickRate)
    {
        if (_instance == null || !_isInitialized) return false;
        return _instance._buckets.TryGetValue(tickRate, out TickBucket b)
               && b.HasSubscribers;
    }

    /// <summary>Dispatch interval in seconds for the given tick rate.</summary>
    public static float GetInterval(TickRate tickRate) =>
        _tickIntervals.TryGetValue(tickRate, out float i) ? i : 0f;

    /// <summary>Dispatch frequency in calls/sec for the given tick rate.</summary>
    public static float GetFrequency(TickRate tickRate)
    {
        float interval = GetInterval(tickRate);
        return interval > 0f ? 1f / interval : 0f;
    }

    // ── Private implementation ───────────────────────────────────────────────

    private void InitialiseBuckets()
    {
        _buckets.Clear();
        foreach (var kvp in _tickIntervals)
            _buckets[kvp.Key] = new TickBucket { Interval = kvp.Value };
    }

    /// <summary>
    /// Copies subscriber references into the pre-allocated cache array,
    /// then invokes each one. The Contains() check protects against callbacks
    /// that unsubscribe themselves during the dispatch loop.
    /// </summary>
    private void DispatchBucket(TickRate rate, TickBucket bucket, float deltaTime)
    {
        int count = bucket.Subscribers.Count;
        if (count == 0) return;

        // Grow the cache if needed — rare, one-time cost per bucket.
        if (count > _invokeCache.Length)
        {
            int newSize = Mathf.Min(count * 2, MAX_CACHE_SIZE);
            _invokeCache = new TickCallback[newSize];
            MID_Logger.LogWarning(_logLevel,
                $"Invoke cache grown to {newSize} entries for '{rate}'.",
                nameof(MID_TickDispatcher), nameof(DispatchBucket));
        }

        bucket.Subscribers.CopyTo(_invokeCache);

        for (int i = 0; i < count; i++)
        {
            TickCallback cb = _invokeCache[i];
            if (cb == null || !bucket.Subscribers.Contains(cb)) continue;

            try   { cb.Invoke(deltaTime); }
            catch (Exception ex)
            {
                MID_Logger.LogError(_logLevel,
                    $"Unhandled exception in '{rate}' callback " +
                    $"({cb.Target?.GetType().Name ?? "static"}.{cb.Method.Name}): {ex.Message}",
                    nameof(MID_TickDispatcher), nameof(DispatchBucket));
            }
        }
    }

    /// <summary>
    /// Builds a subscriber name list for death spiral warnings.
    /// Only called on the warning path — zero cost during normal dispatch.
    /// </summary>
    private static string BuildSubscriberNames(TickBucket bucket)
    {
        var sb = new StringBuilder();
        foreach (TickCallback cb in bucket.Subscribers)
        {
            if (cb == null) continue;
            sb.Append($"{cb.Target?.GetType().Name ?? "static"}.{cb.Method.Name}, ");
        }
        return sb.Length > 2 ? sb.ToString(0, sb.Length - 2) : "none";
    }

    // ── Editor monitor ───────────────────────────────────────────────────────
#if UNITY_EDITOR

    /// <summary>
    /// Pre-fills the status list so all rates appear even before any subscribers
    /// are added, allowing the inspector to show IDLE state from the start.
    /// </summary>
    private void RebuildEditorStatusList()
    {
        _editorBucketStatus.Clear();
        foreach (var kvp in _tickIntervals)
        {
            _editorBucketStatus.Add(new TickRateStatus
            {
                TickRate    = kvp.Key.ToString(),
                Interval    = kvp.Value,
                FiresPerSec = 1f / kvp.Value,
                Subscribers = 0,
                Status      = "IDLE",
            });
        }
    }

    /// <summary>
    /// Updates the inspector list each frame. Editor-only — stripped from builds.
    /// Cost is a simple list iteration over 9 entries; negligible.
    /// </summary>
    private void RefreshEditorStatus()
    {
        int idx = 0;
        foreach (var kvp in _tickIntervals)
        {
            if (idx >= _editorBucketStatus.Count) break;
            if (!_buckets.TryGetValue(kvp.Key, out TickBucket bucket)) { idx++; continue; }

            var entry = _editorBucketStatus[idx];
            entry.Subscribers = bucket.SubscriberCount;
            entry.Status      = bucket.HasSubscribers ? "ACTIVE" : "IDLE";
            _editorBucketStatus[idx] = entry;
            idx++;
        }
    }

#endif

    // ── Internal types ───────────────────────────────────────────────────────

    /// <summary>
    /// Callback signature for tick subscribers.
    /// deltaTime is the bucket's fixed interval, not Time.deltaTime.
    /// </summary>
    public delegate void TickCallback(float deltaTime);

    private sealed class TickBucket
    {
        public float Timer;
        public float Interval;
        public HashSet<TickCallback> Subscribers = new HashSet<TickCallback>();
        public int  SubscriberCount => Subscribers.Count;
        public bool HasSubscribers  => Subscribers.Count > 0;
    }

#if UNITY_EDITOR
    /// <summary>
    /// Inspector-visible snapshot of a single tick rate's runtime state.
    /// Refreshed every frame in Play mode. Read-only — editing these fields
    /// has no effect on the dispatcher.
    /// </summary>
    [Serializable]
    public struct TickRateStatus
    {
        [Tooltip("TickRate enum value.")]
        public string TickRate;

        [Tooltip("Seconds between dispatches.")]
        public float Interval;

        [Tooltip("Times this bucket fires per second.")]
        public float FiresPerSec;

        [Tooltip("Number of callbacks currently subscribed. " +
                 "Zero = IDLE; bucket costs nothing in Update.")]
        public int Subscribers;

        [Tooltip("ACTIVE = firing this frame interval. IDLE = skipped entirely.")]
        public string Status;
    }
#endif
}

// ═══════════════════════════════════════════════════════════════════════════════
/// <summary>
/// Available dispatch rates for MID_TickDispatcher.
///
/// ── SELECTION GUIDE (backed by stress test at 200 subscribers, ~72 fps) ─────
///
///   Tick_0_01  (100/sec) ⚠ DANGER — fires faster than a normal frame.
///                          Negative CPU saving at fps &lt; 100. Death spiral
///                          warnings are expected and correct. Only use for
///                          sub-frame physics helpers when fps > 100 is
///                          guaranteed. NEVER use for gameplay logic.
///
///   Tick_0_02  ( 50/sec) ⚠ MARGINAL — only 1–28% saving at ~72 fps.
///                          Use only when 50hz precision is strictly required
///                          AND target fps is well above 50.
///
///   Tick_0_05  ( 20/sec)   FAST MINIMUM — ~50% saving. Acceptable for
///                          high-frequency weapon systems, projectile checks,
///                          or fast network sync where Tick_0_1 is too slow.
///
///   Tick_0_1   ( 10/sec) ✓ RECOMMENDED MINIMUM for most gameplay systems.
///                          75–99% CPU saving confirmed. Weapon cooldowns,
///                          fast AI reactions, network state sync.
///
///   Tick_0_2   (  5/sec) ✓ STANDARD — enemy AI pathfinding, ability
///                          cooldowns, targeting, aggro checks.
///
///   Tick_0_5   (  2/sec) ✓ SLOW — area checks, perception, slow AI,
///                          environmental trigger polling.
///
///   Tick_1     (  1/sec) ✓ VERY SLOW — health regen, score display,
///                          UI number refreshes, passive stat ticks.
///
///   Tick_2     (0.5/sec) ✓ AMBIENT — distant object updates, LOD
///                          state checks, background audio triggers.
///
///   Tick_5     (0.2/sec) ✓ RARE — spawner logic, wave pacing,
///                          music/ambient event triggers.
///
/// ── ADDING A NEW RATE ────────────────────────────────────────────────────────
///   1. Add a value here: Tick_X_X (underscores replace decimal points).
///   2. Add { TickRate.Tick_X_X, Xf } to MID_TickDispatcher._tickIntervals.
///   3. Done. The bucket is created automatically.
/// </summary>
public enum TickRate
{
    Tick_0_01,  // 100/sec ⚠ DANGER ZONE — see guide above
    Tick_0_02,  //  50/sec ⚠ MARGINAL   — see guide above
    Tick_0_05,  //  20/sec   Fast minimum — weapon systems, projectiles
    Tick_0_1,   //  10/sec ✓ Recommended minimum — fast AI, cooldowns
    Tick_0_2,   //   5/sec ✓ Standard — enemy AI, ability systems
    Tick_0_5,   //   2/sec ✓ Slow — area checks, perception
    Tick_1,     //   1/sec ✓ Very slow — health regen, UI
    Tick_2,     // 0.5/sec ✓ Ambient — distant objects
    Tick_5,     // 0.2/sec ✓ Rare — spawners, wave logic
}