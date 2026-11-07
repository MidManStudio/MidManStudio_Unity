// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/tickdispatcher.md, section "MID_TickDispatcher.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.TickDispatcher
{
    /// <summary>
    /// Shared interval dispatcher that replaces per-MonoBehaviour <c>Update()</c> calls.
    /// Singleton MonoBehaviour: place it on a persistent GameObject in a bootstrap scene, or
    /// let it create itself (a DontDestroyOnLoad GameObject named "[MID_TickDispatcher]") on
    /// the first <see cref="Subscribe"/> call.
    /// </summary>
    /// <remarks>
    /// Subscribers are grouped into one bucket per <see cref="TickRate"/>. One <c>Update()</c>
    /// advances every bucket that has subscribers and invokes its callbacks when the bucket's
    /// interval has elapsed. Buckets without subscribers are skipped. The callback receives the
    /// bucket's fixed interval, not <c>Time.deltaTime</c>. Always pair <see cref="Subscribe"/>
    /// with <see cref="Unsubscribe"/> in <c>OnDisable</c> or <c>OnDestroy</c>. Main thread only.
    /// </remarks>
    /// <example>
    /// <code>
    /// void OnEnable()  => MID_TickDispatcher.Subscribe(TickRate.Tick_0_2, OnTick);
    /// void OnDisable() => MID_TickDispatcher.Unsubscribe(TickRate.Tick_0_2, OnTick);
    /// void OnTick(float interval) { /* about 5 times per second */ }
    /// </code>
    /// </example>
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
            { TickRate.Tick_0_01, 0.01f },   // 100/sec — faster than a normal frame. See TickRate.
            { TickRate.Tick_0_02, 0.02f },   //  50/sec — marginal. See TickRate.
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
                TickRate rate = kvp.Key;
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

            _instance = null;
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
            bool added = bucket.Subscribers.Add(callback);

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

                try { cb.Invoke(deltaTime); }
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
                    TickRate = kvp.Key.ToString(),
                    Interval = kvp.Value,
                    FiresPerSec = 1f / kvp.Value,
                    Subscribers = 0,
                    Status = "IDLE",
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
                entry.Status = bucket.HasSubscribers ? "ACTIVE" : "IDLE";
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
            public int SubscriberCount => Subscribers.Count;
            public bool HasSubscribers => Subscribers.Count > 0;
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

    /// <summary>
    /// Dispatch rates for <see cref="MID_TickDispatcher"/>. The name is the interval in seconds,
    /// with an underscore for the decimal point.
    /// </summary>
    /// <remarks>
    /// The useful range starts at <see cref="Tick_0_1"/> (10 per second). Faster rates fire at or
    /// above a typical frame rate, so they save little or cost more than a plain <c>Update()</c>,
    /// and <see cref="MID_TickDelay"/> refuses them. To add a rate, add a value here and a matching
    /// entry to <c>MID_TickDispatcher._tickIntervals</c>. Keep the values ordered from the fastest
    /// rate to the slowest: <see cref="MID_TickDelay"/> compares rates with <c>&gt;=</c> and uses
    /// the numeric value as an array index.
    /// </remarks>
    public enum TickRate
    {
        /// <summary>100 per second. Not for gameplay logic. Only sensible when the frame rate is above 100.</summary>
        Tick_0_01,
        /// <summary>50 per second. Marginal saving at normal frame rates.</summary>
        Tick_0_02,
        /// <summary>20 per second. Fast systems such as weapon or projectile checks.</summary>
        Tick_0_05,
        /// <summary>10 per second. Recommended minimum: fast AI, cooldowns, state sync.</summary>
        Tick_0_1,
        /// <summary>5 per second. Standard: AI pathfinding, ability cooldowns, targeting.</summary>
        Tick_0_2,
        /// <summary>2 per second. Area checks, perception, environment polling.</summary>
        Tick_0_5,
        /// <summary>1 per second. Health regeneration, UI number refreshes.</summary>
        Tick_1,
        /// <summary>Once every 2 seconds. Distant objects, LOD checks.</summary>
        Tick_2,
        /// <summary>Once every 5 seconds. Spawner and wave logic.</summary>
        Tick_5,
    }
}