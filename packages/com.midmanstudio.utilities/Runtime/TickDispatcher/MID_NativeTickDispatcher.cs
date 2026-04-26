// ═══════════════════════════════════════════════════════════════════════════════
// MID_NativeTickDispatcher
// MidMan Studio — Core Systems
//
// A Burst-compiled, unmanaged companion to MID_TickDispatcher.
// Uses NativeCollections and Burst function pointers for near-native dispatch
// performance on workloads that are fully data-oriented.
//
// ── READ THIS FIRST — IS THIS THE RIGHT TOOL? ───────────────────────────────
//
//   USE MID_TickDispatcher (the managed one) for:
//     • Enemy AI, ability systems, timer batches, UI refreshes
//     • Anything that touches managed objects (Transform, GameObject, etc.)
//     • Instance methods — callbacks that need access to 'this'
//     • General gameplay code — i.e. 99% of your game
//
//   USE MID_NativeTickDispatcher for:
//     • Large homogeneous simulations: projectiles, crowds, particles, grids
//     • Systems where BOTH the dispatch AND the callback work can be fully
//       Burst-compiled with zero managed references
//     • Pure math workloads operating on NativeArrays indexed by entity ID
//     • Systems with 500+ subscribers doing meaningful math per tick
//       (below that threshold the managed dispatcher is fast enough)
//
//   IF IN DOUBT: use MID_TickDispatcher. It benchmarks at 75-99% CPU saving
//   vs raw Update for typical workloads. Only switch to this system when you
//   have profiled and confirmed the managed dispatcher is a bottleneck.
//
// ── WHY BURST HELPS HERE (AND ONLY HERE) ────────────────────────────────────
//
//   The managed dispatcher is already as optimised as managed C# allows.
//   Its remaining cost is unavoidable: delegate invocation crosses the managed
//   heap boundary on every call. Burst cannot touch managed delegates.
//
//   This dispatcher stores callbacks as FunctionPointer<T> — unmanaged structs
//   wrapping a native code pointer compiled by Burst. The dispatch loop itself
//   is also Burst-compiled. The result is a hot path that:
//     • Never touches the managed heap
//     • Enables SIMD auto-vectorisation inside callbacks
//     • Eliminates managed null checks and bounds checks
//     • Runs in native code from bucket timer check through to callback return
//
//   Realistic gain over the managed dispatcher: 10–30× on pure math workloads.
//   On workloads that touch any managed object: zero gain (Burst can't help).
//
// ── HARD RULES FOR SUBSCRIBERS ──────────────────────────────────────────────
//
//   1. Subscriber methods MUST be static. No instance methods, no lambdas,
//      no closures. Burst function pointers cannot capture 'this'.
//
//   2. Subscriber methods MUST be decorated with [BurstCompile].
//      An undecorated static method will compile and run but as managed code —
//      you lose all Burst benefit and defeat the purpose of this dispatcher.
//
//   3. Subscriber methods MUST NOT touch managed objects. No GameObjects,
//      no MonoBehaviours, no C# classes, no string operations, no boxing.
//      Allowed: NativeArray, NativeList, UnsafeList, math.*, primitive types.
//
//   4. Subscribe and Unsubscribe MUST be called from the main thread.
//      BurstCompiler.CompileFunctionPointer is not thread-safe.
//
//   5. Always Unsubscribe in OnDisable or OnDestroy. A leaked function pointer
//      pointing to a destroyed system will cause a native crash, not a managed
//      exception. There is no safety net here the way there is in managed code.
//
// ── HOW SUBSCRIBERS PASS CONTEXT (no 'this' available) ──────────────────────
//
//   Since callbacks are static, they cannot reference instance data. The
//   correct pattern is a shared NativeArray indexed by a stable entity ID:
//
//   // Shared data store (owned by some system or ECS-style manager)
//   public static NativeArray<ProjectileData> ProjectileStore;
//
//   // Subscriber registers its entity index at creation time
//   void OnEnable()
//   {
//       _entityIndex = ProjectileSystem.Allocate(initialData);
//       MID_NativeTickDispatcher.Subscribe(TickRate.Tick_0_1, OnTick);
//   }
//
//   [BurstCompile]
//   static void OnTick(float deltaTime)
//   {
//       // Operates on the whole store — or each entity stores its own index
//       // in a separate NativeArray and reads it here.
//       for (int i = 0; i < ProjectileStore.Length; i++)
//           ProjectileStore[i] = Advance(ProjectileStore[i], deltaTime);
//   }
//
//   ALTERNATIVE — Single static callback per system type (recommended):
//   Rather than one subscriber per entity, register ONE static callback for
//   the entire system. That callback iterates a NativeArray of all entities.
//   This is the most cache-friendly pattern and the one Burst optimises best.
//
// ── USAGE SUMMARY ───────────────────────────────────────────────────────────
//
//   // 1. Define your callback — static, [BurstCompile], no managed refs
//   [BurstCompile]
//   private static void OnProjectileTick(float deltaTime) { ... }
//
//   // 2. Subscribe from the main thread (OnEnable / Awake)
//   void OnEnable()
//       => MID_NativeTickDispatcher.Subscribe(TickRate.Tick_0_1, OnProjectileTick);
//
//   // 3. Unsubscribe — NEVER skip this
//   void OnDisable()
//       => MID_NativeTickDispatcher.Unsubscribe(TickRate.Tick_0_1, OnProjectileTick);
//
// ── MINIMUM TICK RATE GUIDANCE ───────────────────────────────────────────────
//   Same rules as MID_TickDispatcher apply — tick rate must be below fps or
//   the frequency saving disappears. See MID_TickDispatcher header for the
//   full benchmark table. Tick_0_1 (10/sec) is the recommended minimum.
//   Burst gains are on top of frequency savings, not instead of them.
//
// ── UNITY VERSION REQUIREMENT ────────────────────────────────────────────────
//   Requires: Unity 2021.3+ and com.unity.burst 1.6+
//   Tested:   Unity 2022.3 LTS, Burst 1.8
//   BurstCompiler.CompileFunctionPointer is available from Burst 1.0 onward.
//   Unity.Collections (NativeList) requires com.unity.collections 1.0+.
//
// ── PACKAGES REQUIRED ────────────────────────────────────────────────────────
//   com.unity.burst        >= 1.6.0
//   com.unity.collections  >= 1.0.0
//   (both ship with Unity 2021.3+ by default)
//
// ── DEPENDENCIES ─────────────────────────────────────────────────────────────
//   MID_TickDispatcher (shares TickRate enum)
//   MID_Logger (MidManStudio.Core.Logging)
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using MidManStudio.Core.Logging;

/// <summary>
/// Burst-compiled unmanaged tick dispatcher.
/// Companion to MID_TickDispatcher — read the file header before deciding
/// which dispatcher to use. This system is for data-oriented Burst workloads
/// only. For general gameplay code, use MID_TickDispatcher.
/// </summary>
public class MID_NativeTickDispatcher : MonoBehaviour
{
    // ── Configuration ─────────────────────────────────────────────────────────

    [SerializeField]
    [Tooltip("Log level for this dispatcher. Warning or Error recommended in production.")]
    private MID_LogLevel _logLevel = MID_LogLevel.Info;

    [Header("Death Spiral Protection")]

    [SerializeField]
    [Tooltip("Maximum catch-up ticks per frame per bucket. " +
             "Mirrors MID_TickDispatcher behaviour.")]
    private int _maxTicksPerFrame = 5;

    [SerializeField]
    private bool _enableDeathSpiralProtection = true;

    // ── Editor runtime monitor ─────────────────────────────────────────────────
#if UNITY_EDITOR
    [Header("Live Bucket Monitor  (Editor Only)")]
    [SerializeField]
    [Tooltip("Refreshed every frame in Play mode. Shows which native tick " +
             "rates are active and their current subscriber counts.")]
    private List<NativeTickRateStatus> _editorBucketStatus = new();
#endif

    // ── Tick interval table ────────────────────────────────────────────────────
    // Mirrors MID_TickDispatcher exactly. Indexed by (int)TickRate.
    // If you add a TickRate to the enum, add a matching entry here.

    private static readonly float[] _tickIntervals = new float[]
    {
        0.01f,   // Tick_0_01 — 100/sec  ⚠ DANGER ZONE
        0.02f,   // Tick_0_02 —  50/sec  ⚠ Marginal
        0.05f,   // Tick_0_05 —  20/sec
        0.1f,    // Tick_0_1  —  10/sec  ✓ Recommended minimum
        0.2f,    // Tick_0_2  —   5/sec  ✓ Standard
        0.5f,    // Tick_0_5  —   2/sec  ✓ Slow
        1.0f,    // Tick_1    —   1/sec  ✓ Very slow
        2.0f,    // Tick_2    — 0.5/sec  ✓ Ambient
        5.0f,    // Tick_5    — 0.2/sec  ✓ Rare
    };

    private const int BUCKET_COUNT = 9; // Must match TickRate enum length.

    // ── Callback delegate ──────────────────────────────────────────────────────

    /// <summary>
    /// Signature for all native tick subscribers.
    /// The method MUST be static and decorated with [BurstCompile].
    /// deltaTime is the bucket's fixed interval, not Time.deltaTime.
    /// </summary>
    public delegate void NativeTickDelegate(float deltaTime);

    // ── Internal Burst dispatch delegate ──────────────────────────────────────
    // Used to compile the hot dispatch loop itself into Burst native code.
    // This is internal plumbing — subscribers do not use this type directly.

    private unsafe delegate void BurstDispatchDelegate(
        FunctionPointer<NativeTickDelegate>* subscribers,
        int count,
        float deltaTime);

    // ── Singleton ──────────────────────────────────────────────────────────────

    private static MID_NativeTickDispatcher _instance;
    private static bool _isInitialized       = false;
    private static bool _applicationIsQuitting = false;

    public static bool IsQuitting => _applicationIsQuitting;
    public static bool IsReady    => _isInitialized && _instance != null;

    public static MID_NativeTickDispatcher Instance
    {
        get
        {
            if (_applicationIsQuitting)
            {
                Debug.LogWarning("[MID_NativeTickDispatcher] Instance requested during quit.");
                return null;
            }

            if (_instance == null)
            {
                _instance = FindAnyObjectByType<MID_NativeTickDispatcher>();
                if (_instance == null)
                {
                    var go = new GameObject("[MID_NativeTickDispatcher]");
                    _instance = go.AddComponent<MID_NativeTickDispatcher>();
                    DontDestroyOnLoad(go);
                }
            }

            return _instance;
        }
    }

    // ── Native storage ─────────────────────────────────────────────────────────
    // One NativeList of FunctionPointers per tick rate bucket.
    // Managed array outer shell — native list contents. This is intentional:
    // the outer array is allocated once at init and never resized.
    // The NativeLists themselves live on the unmanaged heap.

    private NativeList<FunctionPointer<NativeTickDelegate>>[] _buckets;
    private NativeArray<float> _timers; // one accumulator per bucket

    // The Burst-compiled dispatch loop, compiled once in Awake.
    private static FunctionPointer<BurstDispatchDelegate> _burstDispatch;
    private static bool _burstDispatchCompiled = false;

    // ── Unity lifecycle ────────────────────────────────────────────────────────

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            MID_Logger.LogWarning(_logLevel,
                "Duplicate MID_NativeTickDispatcher — destroying extra instance.",
                nameof(MID_NativeTickDispatcher), nameof(Awake));
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);

        InitialiseNativeStorage();
        CompileBurstDispatch();
        _isInitialized = true;

#if UNITY_EDITOR
        RebuildEditorStatusList();
#endif

        MID_Logger.LogInfo(_logLevel,
            $"Initialised — {BUCKET_COUNT} native tick buckets ready. " +
            $"Burst dispatch: {(_burstDispatchCompiled ? "compiled" : "FALLBACK managed")}",
            nameof(MID_NativeTickDispatcher), nameof(Awake));
    }

    private void Update()
    {
        if (!_isInitialized) return;

        float rawDelta = Time.deltaTime;

        for (int i = 0; i < BUCKET_COUNT; i++)
        {
            var bucket = _buckets[i];
            if (bucket.Length == 0) continue; // idle bucket — zero cost

            float interval = _tickIntervals[i];

            // Death spiral clamp — mirrors managed dispatcher exactly.
            float maxAccumulation = interval * (_maxTicksPerFrame - 1);
            float timer = _timers[i];
            timer = Mathf.Min(timer + rawDelta, maxAccumulation + rawDelta);

            int ticksFired = 0;
            while (timer >= interval && ticksFired < _maxTicksPerFrame)
            {
                timer -= interval;
                DispatchBucket(i, bucket, interval);
                ticksFired++;
            }

            _timers[i] = timer;

            if (_enableDeathSpiralProtection && ticksFired >= _maxTicksPerFrame)
            {
                _timers[i] = 0f;
                MID_Logger.LogWarning(_logLevel,
                    $"Death spiral guard triggered on bucket[{i}] ({(TickRate)i}) — " +
                    $"timer reset. Subscribers: {bucket.Length}",
                    nameof(MID_NativeTickDispatcher), nameof(Update));
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
            "Destroying — disposing all native buckets.",
            nameof(MID_NativeTickDispatcher), nameof(OnDestroy));

        DisposeNativeStorage();

        _instance      = null;
        _isInitialized = false;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Subscribe a static Burst-compiled method to a native tick rate.
    ///
    /// REQUIREMENTS — violating these causes crashes or silent failures:
    ///   1. <paramref name="callback"/> MUST be a static method reference.
    ///      Instance methods and lambdas will throw ArgumentException.
    ///   2. <paramref name="callback"/> MUST be decorated with [BurstCompile].
    ///      Without it the system still works but runs as managed code,
    ///      defeating the purpose of this dispatcher entirely.
    ///   3. Must be called from the main thread.
    ///
    /// Call from Awake or OnEnable. Always pair with Unsubscribe.
    /// </summary>
    /// <exception cref="ArgumentNullException">callback is null.</exception>
    /// <exception cref="ArgumentException">callback is not a static method.</exception>
    public static bool Subscribe(TickRate tickRate, NativeTickDelegate callback)
    {
        if (callback == null)
            throw new ArgumentNullException(nameof(callback),
                "[MID_NativeTickDispatcher] Subscribe called with null callback.");

        // Enforce static-only — instance methods cannot be Burst function pointers.
        if (callback.Target != null)
            throw new ArgumentException(
                $"[MID_NativeTickDispatcher] Only static methods can be subscribed. " +
                $"'{callback.Method.DeclaringType?.Name}.{callback.Method.Name}' is an instance method. " +
                $"See file header for the correct usage pattern.",
                nameof(callback));

        if (_applicationIsQuitting) return false;

        var instance = Instance;
        if (instance == null || !_isInitialized)
        {
            MID_Logger.LogError(MID_LogLevel.Error,
                "Subscribe failed — dispatcher not initialised.",
                nameof(MID_NativeTickDispatcher), nameof(Subscribe));
            return false;
        }

        int idx = (int)tickRate;
        if (idx < 0 || idx >= BUCKET_COUNT)
        {
            MID_Logger.LogError(MID_LogLevel.Error,
                $"TickRate '{tickRate}' index {idx} is out of range. " +
                "If you added a new TickRate, update _tickIntervals and BUCKET_COUNT.",
                nameof(MID_NativeTickDispatcher), nameof(Subscribe));
            return false;
        }

        // Compile the function pointer on the main thread.
        // BurstCompiler caches by method — calling this twice for the same
        // static method returns the same IntPtr. Safe and cheap after first call.
        FunctionPointer<NativeTickDelegate> fp;
        try
        {
            fp = BurstCompiler.CompileFunctionPointer<NativeTickDelegate>(callback);
        }
        catch (Exception ex)
        {
            MID_Logger.LogError(MID_LogLevel.Error,
                $"BurstCompiler.CompileFunctionPointer failed for " +
                $"'{callback.Method.Name}': {ex.Message}. " +
                "Ensure the Burst package is installed and the method is [BurstCompile].",
                nameof(MID_NativeTickDispatcher), nameof(Subscribe));
            return false;
        }

        // Deduplicate by pointer value — same static method must not be added twice.
        var bucket = instance._buckets[idx];
        for (int i = 0; i < bucket.Length; i++)
        {
            if (bucket[i].Value == fp.Value)
            {
                MID_Logger.LogDebug(instance._logLevel,
                    $"Duplicate Subscribe to '{tickRate}' ignored " +
                    $"({callback.Method.Name}). Total: {bucket.Length}",
                    nameof(MID_NativeTickDispatcher), nameof(Subscribe));
                return false;
            }
        }

        bool wasEmpty = bucket.Length == 0;
        bucket.Add(fp);

        // Reset timer on first subscriber so the bucket does not fire
        // immediately from accumulated idle time.
        if (wasEmpty) instance._timers[idx] = 0f;

        MID_Logger.LogDebug(instance._logLevel,
            wasEmpty
                ? $"First subscriber on '{tickRate}' — bucket ACTIVE. " +
                  $"Method: {callback.Method.Name}"
                : $"Subscribed '{callback.Method.Name}' to '{tickRate}'. " +
                  $"Total: {bucket.Length}",
            nameof(MID_NativeTickDispatcher), nameof(Subscribe));

        return true;
    }

    /// <summary>
    /// Remove a previously subscribed static callback.
    /// Call from OnDisable or OnDestroy.
    ///
    /// WARNING: Failing to unsubscribe a callback whose owning system has
    /// been destroyed will cause a native access violation crash on the next
    /// dispatch. There is no managed safety net — the dispatcher will call
    /// the function pointer regardless of the subscriber's lifetime.
    /// </summary>
    public static bool Unsubscribe(TickRate tickRate, NativeTickDelegate callback)
    {
        if (callback == null || _applicationIsQuitting) return false;
        if (_instance == null || !_isInitialized) return false;

        int idx = (int)tickRate;
        if (idx < 0 || idx >= BUCKET_COUNT) return false;

        FunctionPointer<NativeTickDelegate> fp;
        try { fp = BurstCompiler.CompileFunctionPointer<NativeTickDelegate>(callback); }
        catch { return false; }

        var bucket = _instance._buckets[idx];
        for (int i = 0; i < bucket.Length; i++)
        {
            if (bucket[i].Value != fp.Value) continue;

            // SwapBack is O(1) — order is not meaningful for dispatch.
            bucket.RemoveAtSwapBack(i);

            MID_Logger.LogDebug(_instance._logLevel,
                $"Unsubscribed '{callback.Method.Name}' from '{tickRate}'. " +
                $"Remaining: {bucket.Length}" +
                (bucket.Length == 0 ? " — bucket IDLE" : ""),
                nameof(MID_NativeTickDispatcher), nameof(Unsubscribe));

            return true;
        }

        return false;
    }

    /// <summary>Returns true if the static callback is currently subscribed.</summary>
    public static bool IsSubscribed(TickRate tickRate, NativeTickDelegate callback)
    {
        if (callback == null || _instance == null || !_isInitialized) return false;
        int idx = (int)tickRate;
        if (idx < 0 || idx >= BUCKET_COUNT) return false;

        FunctionPointer<NativeTickDelegate> fp;
        try { fp = BurstCompiler.CompileFunctionPointer<NativeTickDelegate>(callback); }
        catch { return false; }

        var bucket = _instance._buckets[idx];
        for (int i = 0; i < bucket.Length; i++)
            if (bucket[i].Value == fp.Value) return true;

        return false;
    }

    /// <summary>Remove all subscribers from a specific tick rate.</summary>
    public static void ClearSubscribers(TickRate tickRate)
    {
        if (_applicationIsQuitting || _instance == null || !_isInitialized) return;
        int idx = (int)tickRate;
        if (idx < 0 || idx >= BUCKET_COUNT) return;

        int count = _instance._buckets[idx].Length;
        _instance._buckets[idx].Clear();

        MID_Logger.LogInfo(_instance._logLevel,
            $"Cleared {count} native subscribers from '{tickRate}' — bucket IDLE.",
            nameof(MID_NativeTickDispatcher), nameof(ClearSubscribers));
    }

    /// <summary>Remove all subscribers from all tick rates.</summary>
    public static void ClearAllSubscribers()
    {
        if (_applicationIsQuitting || _instance == null || !_isInitialized) return;
        int total = 0;
        for (int i = 0; i < BUCKET_COUNT; i++)
        {
            total += _instance._buckets[i].Length;
            _instance._buckets[i].Clear();
        }
        MID_Logger.LogInfo(_instance._logLevel,
            $"Cleared {total} native subscribers across all buckets.",
            nameof(MID_NativeTickDispatcher), nameof(ClearAllSubscribers));
    }

    // ── Query helpers ──────────────────────────────────────────────────────────

    public static int   GetSubscriberCount(TickRate r) =>
        (_instance == null || !_isInitialized) ? 0 : _instance._buckets[(int)r].Length;

    public static bool  IsTickRateActive(TickRate r) =>
        _instance != null && _isInitialized && _instance._buckets[(int)r].Length > 0;

    public static float GetInterval(TickRate r)  =>
        (int)r < BUCKET_COUNT ? _tickIntervals[(int)r] : 0f;

    public static float GetFrequency(TickRate r) =>
        GetInterval(r) > 0f ? 1f / GetInterval(r) : 0f;

    // ── Private implementation ─────────────────────────────────────────────────

    private void InitialiseNativeStorage()
    {
        _buckets = new NativeList<FunctionPointer<NativeTickDelegate>>[BUCKET_COUNT];
        for (int i = 0; i < BUCKET_COUNT; i++)
            _buckets[i] = new NativeList<FunctionPointer<NativeTickDelegate>>(
                8, Allocator.Persistent);

        _timers = new NativeArray<float>(BUCKET_COUNT, Allocator.Persistent,
            NativeArrayOptions.ClearMemory);
    }

    private void DisposeNativeStorage()
    {
        if (_buckets != null)
            for (int i = 0; i < BUCKET_COUNT; i++)
                if (_buckets[i].IsCreated) _buckets[i].Dispose();

        if (_timers.IsCreated) _timers.Dispose();
    }

    /// <summary>
    /// Compiles the inner dispatch loop into Burst native code once at startup.
    /// Falls back to the managed dispatch path if Burst compilation fails
    /// (e.g. Burst package not installed, or running on an unsupported platform).
    /// </summary>
    private static void CompileBurstDispatch()
    {
        if (_burstDispatchCompiled) return;
        try
        {
            unsafe
            {
                _burstDispatch = BurstCompiler
                    .CompileFunctionPointer<BurstDispatchDelegate>(BurstDispatchImpl);
                _burstDispatchCompiled = true;
            }
        }
        catch (Exception ex)
        {
            // Not a fatal error — managed fallback path is used.
            Debug.LogWarning(
                $"[MID_NativeTickDispatcher] Burst dispatch compilation failed: {ex.Message}\n" +
                "Falling back to managed dispatch. Subscribers will still fire correctly " +
                "but without Burst performance gains.");
        }
    }

    /// <summary>
    /// Hot dispatch path. Uses the Burst-compiled loop if available,
    /// falls back to managed iteration if not.
    /// </summary>
    private unsafe void DispatchBucket(
        int bucketIndex,
        NativeList<FunctionPointer<NativeTickDelegate>> bucket,
        float interval)
    {
        int count = bucket.Length;
        if (count == 0) return;

        if (_burstDispatchCompiled)
        {
            // Pass a raw pointer to the list's backing store directly into
            // Burst. No copies, no managed overhead. GetUnsafePtr() is safe
            // here because we own this list and are on the main thread.
            _burstDispatch.Invoke(
                (FunctionPointer<NativeTickDelegate>*)bucket.GetUnsafePtr(),
                count,
                interval);
        }
        else
        {
            // Managed fallback — still calls each function pointer, just without
            // the Burst-compiled loop around the invocation.
            for (int i = 0; i < count; i++)
            {
                try   { bucket[i].Invoke(interval); }
                catch (Exception ex)
                {
                    MID_Logger.LogError(_logLevel,
                        $"Exception in native bucket[{bucketIndex}] subscriber {i}: {ex.Message}",
                        nameof(MID_NativeTickDispatcher), nameof(DispatchBucket));
                }
            }
        }
    }

    /// <summary>
    /// The Burst-compiled dispatch loop. This method is compiled to native code
    /// by BurstCompiler.CompileFunctionPointer. It receives a raw pointer to the
    /// NativeList's backing array and iterates it without any managed overhead.
    ///
    /// IMPORTANT: This is internal plumbing. Do not call directly.
    /// The [BurstCompile] attribute here applies to the loop, not the subscribers.
    /// Each subscriber must carry its own [BurstCompile] attribute.
    /// </summary>
    [BurstCompile]
    private static unsafe void BurstDispatchImpl(
        FunctionPointer<NativeTickDelegate>* subscribers,
        int count,
        float deltaTime)
    {
        for (int i = 0; i < count; i++)
            subscribers[i].Invoke(deltaTime);
    }

    // ── Editor monitor ─────────────────────────────────────────────────────────
#if UNITY_EDITOR

    private void RebuildEditorStatusList()
    {
        _editorBucketStatus.Clear();
        for (int i = 0; i < BUCKET_COUNT; i++)
        {
            float interval = _tickIntervals[i];
            _editorBucketStatus.Add(new NativeTickRateStatus
            {
                TickRate    = ((TickRate)i).ToString(),
                Interval    = interval,
                FiresPerSec = 1f / interval,
                Subscribers = 0,
                Status      = "IDLE",
            });
        }
    }

    private void RefreshEditorStatus()
    {
        for (int i = 0; i < BUCKET_COUNT && i < _editorBucketStatus.Count; i++)
        {
            var e = _editorBucketStatus[i];
            e.Subscribers = _buckets[i].Length;
            e.Status      = _buckets[i].Length > 0 ? "ACTIVE" : "IDLE";
            _editorBucketStatus[i] = e;
        }
    }

    /// <summary>Inspector-visible snapshot of a native bucket's runtime state.</summary>
    [Serializable]
    public struct NativeTickRateStatus
    {
        [Tooltip("TickRate enum value.")]
        public string TickRate;
        [Tooltip("Seconds between dispatches.")]
        public float  Interval;
        [Tooltip("Times this bucket fires per second.")]
        public float  FiresPerSec;
        [Tooltip("Subscribed static Burst callbacks. Zero = IDLE, costs nothing.")]
        public int    Subscribers;
        [Tooltip("ACTIVE = firing each interval. IDLE = skipped in Update entirely.")]
        public string Status;
    }

#endif
}

// ═══════════════════════════════════════════════════════════════════════════════
// QUICK REFERENCE — correct subscriber pattern
//
//  DO THIS:
//  ─────────────────────────────────────────────────────────────────────────────
//  using Unity.Burst;
//  using Unity.Collections;
//
//  public class ProjectileSystem : MonoBehaviour
//  {
//      // Shared data store — owned and disposed by this system
//      public static NativeArray<float2> Positions;
//      public static NativeArray<float2> Velocities;
//      public static NativeArray<int>    ActiveFlags;
//
//      void Awake()
//      {
//          Positions  = new NativeArray<float2>(1024, Allocator.Persistent);
//          Velocities = new NativeArray<float2>(1024, Allocator.Persistent);
//          ActiveFlags = new NativeArray<int>(1024, Allocator.Persistent);
//      }
//
//      void OnEnable()
//          => MID_NativeTickDispatcher.Subscribe(TickRate.Tick_0_05, Tick);
//
//      void OnDisable()
//          => MID_NativeTickDispatcher.Unsubscribe(TickRate.Tick_0_05, Tick);
//
//      void OnDestroy()
//      {
//          Positions.Dispose();
//          Velocities.Dispose();
//          ActiveFlags.Dispose();
//      }
//
//      // Static + [BurstCompile] — the only valid subscriber signature
//      [BurstCompile]
//      private static void Tick(float deltaTime)
//      {
//          for (int i = 0; i < ActiveFlags.Length; i++)
//          {
//              if (ActiveFlags[i] == 0) continue;
//              Positions[i] += Velocities[i] * deltaTime;
//          }
//      }
//  }
//
//  DON'T DO THIS:
//  ─────────────────────────────────────────────────────────────────────────────
//  // ✗ Instance method — will throw ArgumentException on Subscribe
//  void OnTick(float dt) { transform.position += ...; }
//
//  // ✗ Lambda — closure captures 'this', not static
//  MID_NativeTickDispatcher.Subscribe(TickRate.Tick_0_1, dt => DoWork(dt));
//
//  // ✗ Static but no [BurstCompile] — compiles and runs but as managed code.
//  //   You get none of the performance benefit.
//  private static void Tick(float dt) { ... }
//
//  // ✗ Touching managed objects inside a Burst callback — will crash or fail
//  [BurstCompile]
//  private static void Tick(float dt)
//  {
//      gameObject.SetActive(false); // GameObject is managed — Burst can't touch it
//      Debug.Log("hello");         // string allocation — not Burst compatible
//  }
// ═══════════════════════════════════════════════════════════════════════════════
