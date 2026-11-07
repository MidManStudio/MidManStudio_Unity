// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/pool-systems.md, section "LocalObjectPool.cs"
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Pools
{
    /// <summary>
    /// Pool for prefab instances, keyed by the generated <see cref="PoolableObjectType"/> enum. Add one entry per prefab in
    /// the inspector, call <see cref="CallInitializePool"/> once, then use <see cref="GetObject(PoolableObjectType, Vector3, Quaternion)"/>
    /// and <see cref="ReturnObject"/>. Every instance gets a <see cref="LocalPoolReturn"/> that returns it by itself after
    /// its auto-return time. A type that is not registered is registered on first use from the config list, with the
    /// auto-registration prewarm and size, when auto-registration is on. The monitor fields in the inspector are
    /// updated every frame.
    /// </summary>
    public class LocalObjectPool : Singleton<LocalObjectPool>
    {
        #region Serialized Fields

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Pool Configuration")]
        [Tooltip("Add one entry per poolable prefab.")]
        [MID_NamedList]
        [SerializeField] private List<BasicPoolConfig> poolConfigs = new List<BasicPoolConfig>();

        [Header("Auto-Registration")]
        [SerializeField] private int autoRegisterPrewarmCount = 10;
        [SerializeField] private int autoRegisterMaxPoolSize = 20;
        [SerializeField] private bool enableAutoRegistration = true;

        [Header("Monitor (read-only)")]
        [SerializeField] private int totalPooledObjects;
        [SerializeField] private int totalActiveObjects;
        [SerializeField] private int childrenCount;
        [SerializeField] private List<PoolStats> poolStatistics = new List<PoolStats>();

        #endregion

        #region Private State

        private bool _initialized;

        private readonly Dictionary<PoolableObjectType, Queue<GameObject>> _pooledObjects = new();
        private readonly Dictionary<PoolableObjectType, BasicPoolConfig> _typeConfigs = new();
        private readonly Dictionary<PoolableObjectType, GameObject> _typePrefabs = new();
        private readonly HashSet<PoolableObjectType> _registeredTypes = new();
        private readonly Dictionary<PoolableObjectType, int> _totalSpawned = new();
        private readonly Dictionary<PoolableObjectType, int> _activeCount = new();
        private readonly Dictionary<GameObject, PoolableObjectType> _prefabToType = new();

        // Every GameObject that currently sits in one of the _pooledObjects queues. ReturnObject uses it to
        // reject a second return of an object that is already queued, for example an auto-return timer that
        // fires on an object a caller still holds and returns later by hand. An entry is removed when an object
        // leaves a queue (GetObject dequeue, ClearPool).
        private readonly HashSet<GameObject> _queuedObjects = new();

        #endregion

        #region Properties

        /// <summary>True after <see cref="CallInitializePool"/> has registered the configured types.</summary>
        public bool HasBeenInitialized() => _initialized;

        #endregion

        #region Unity Lifecycle

        private void Update()
        {
            if (_initialized) UpdateMonitor();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Checks the config list and registers every entry with its prewarm count, then starts <see cref="LocalParticlePool"/> if it
        /// is not initialised. Does nothing when already initialised. When the list has a duplicate type or prefab it logs an error and stays uninitialised.
        /// </summary>
        public void CallInitializePool()
        {
            if (_initialized) return;

            MID_Logger.LogInfo(_logLevel, "Initialising object pool.",
                nameof(LocalObjectPool), nameof(CallInitializePool));

            if (!ValidateConfigs()) return;

            foreach (var config in poolConfigs)
                RegisterInternal(config);

            _initialized = true;

            MID_Logger.LogInfo(_logLevel,
                $"Object pool ready — {poolConfigs.Count} type(s) registered.",
                nameof(LocalObjectPool), nameof(CallInitializePool));

            if (!LocalParticlePool.Instance.HasBeenInitialized())
                LocalParticlePool.Instance.CallInitializePool();
        }

        // ── Get ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Takes an instance from the queue, or creates one when the queue is empty, and activates it at the position. Returns null when
        /// the type cannot be registered. The instance has no parent after this call.
        /// </summary>
        public GameObject GetObject(PoolableObjectType type, Vector3 position, Quaternion rotation)
        {
            if (!EnsureRegistered(type)) return null;

            var pool = _pooledObjects[type];
            GameObject obj = null;

            // A queued entry can go stale when something outside the pool destroys it, for example a NetworkObject
            // parent destroyed by Netcode while a pooled child visual still sits in the queue. Unity's overridden
            // `== null` reports true for a destroyed object although the C# reference is not null, so such
            // entries are skipped here.
            while (pool.Count > 0)
            {
                var candidate = pool.Dequeue();
                _queuedObjects.Remove(candidate);

                if (candidate != null) { obj = candidate; break; }

                MID_Logger.LogWarning(_logLevel,
                    $"Discarded a destroyed pooled {type} reference found in queue.",
                    nameof(LocalObjectPool), nameof(GetObject));
            }

            bool isNew = obj == null;
            if (isNew)
            {
                obj = CreateInstance(_typeConfigs[type]);
                _totalSpawned[type]++;
            }

            _activeCount[type]++;

            obj.transform.SetParent(null);
            obj.transform.position = position;
            obj.transform.rotation = rotation;
            obj.SetActive(true);

            MID_Logger.LogDebug(_logLevel,
                $"Get {type} | id={obj.GetInstanceID()} " +
                $"new={isNew} active={_activeCount[type]} pool={pool.Count}",
                nameof(LocalObjectPool), nameof(GetObject));

            return obj;
        }

        /// <summary>2D overload. The z position is 0.</summary>
        public GameObject GetObject(PoolableObjectType type, Vector2 position, Quaternion rotation)
            => GetObject(type, new Vector3(position.x, position.y, 0f), rotation);

        // ── Return ────────────────────────────────────────────────────────────

        /// <summary>
        /// Puts an instance back in the queue after resetting it. Ignores null, destroyed and already queued objects (with a warning). Destroys
        /// the object instead when the queue is full or the type is unknown and cannot be registered.
        /// </summary>
        public void ReturnObject(GameObject obj, PoolableObjectType type)
        {
            // A destroyed or null object has nothing to return. It is reported, not queued, so the double
            // return or dangling reference that caused the call stays visible.
            if (obj == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"ReturnObject called with a null/destroyed {type} reference — ignored.",
                    nameof(LocalObjectPool));
                return;
            }

            // Reject a second return of an object that is already queued, so one GameObject is never handed to
            // two owners. See the _queuedObjects field.
            if (_queuedObjects.Contains(obj))
            {
                MID_Logger.LogWarning(_logLevel,
                    $"ReturnObject: {type} (id={obj.GetInstanceID()}) is already queued — " +
                    "ignoring duplicate return.",
                    nameof(LocalObjectPool), nameof(ReturnObject));
                return;
            }

            if (!_registeredTypes.Contains(type))
            {
                if (enableAutoRegistration)
                {
                    var prefab = FindPrefabForType(type);
                    if (prefab != null)
                    {
                        MID_Logger.LogWarning(_logLevel,
                            $"Auto-registering {type} during return.",
                            nameof(LocalObjectPool));
                        AddType(type, prefab, autoRegisterPrewarmCount, autoRegisterMaxPoolSize);
                    }
                    else
                    {
                        MID_Logger.LogError(_logLevel,
                            $"Return failed — {type} not registered, no prefab. Destroying.",
                            nameof(LocalObjectPool));
                        Destroy(obj);
                        return;
                    }
                }
                else
                {
                    MID_Logger.LogError(_logLevel,
                        $"Return failed — {type} not registered. Destroying.",
                        nameof(LocalObjectPool));
                    Destroy(obj);
                    return;
                }
            }

            var pool = _pooledObjects[type];
            var config = _typeConfigs[type];

            if (pool.Count >= config.maxPoolSize)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Pool full for {type} — destroying overflow.",
                    nameof(LocalObjectPool));
                if (_activeCount.ContainsKey(type)) _activeCount[type]--;
                Destroy(obj);
                return;
            }

            if (_activeCount.ContainsKey(type)) _activeCount[type]--;

            ResetObject(obj);
            obj.transform.SetParent(transform);
            obj.SetActive(false);
            pool.Enqueue(obj);
            _queuedObjects.Add(obj);

            MID_Logger.LogDebug(_logLevel,
                $"Returned {type} | id={obj.GetInstanceID()} pool={pool.Count}",
                nameof(LocalObjectPool));
        }

        // ── Registration ──────────────────────────────────────────────────────

        /// <summary>Registers a type at run time. Does nothing (with a warning or an error) when the type or the prefab is already registered.</summary>
        public void AddType(PoolableObjectType type, GameObject prefab,
                            int prewarm = 5, int maxSize = 15)
        {
            if (_registeredTypes.Contains(type))
            {
                MID_Logger.LogWarning(_logLevel, $"{type} already registered.",
                    nameof(LocalObjectPool));
                return;
            }

            if (_prefabToType.ContainsKey(prefab))
            {
                MID_Logger.LogError(_logLevel,
                    $"Prefab '{prefab.name}' already registered as {_prefabToType[prefab]}. " +
                    $"Cannot re-register as {type}.",
                    nameof(LocalObjectPool));
                return;
            }

            var config = new BasicPoolConfig
            {
                objectType = type,
                displayName = prefab.name,
                prefab = prefab,
                prewarmCount = prewarm,
                maxPoolSize = maxSize
            };

            RegisterInternal(config);
            MID_Logger.LogInfo(_logLevel,
                $"Runtime registered {type} prewarm={prewarm} max={maxSize}.",
                nameof(LocalObjectPool));
        }

        /// <summary>True when the type has been registered.</summary>
        public bool IsRegistered(PoolableObjectType type) => _registeredTypes.Contains(type);

        /// <summary>
        /// Number of instances currently idle in the queue for a type (i.e.
        /// NOT counting instances presently checked out via GetObject).
        /// Returns 0 for an unregistered type. Used by
        /// ProjectileVisualWarmupManager to know exactly how many
        /// Get/Return cycles are needed to touch every currently-pooled
        /// instance once, instead of guessing a fixed number.
        /// </summary>
        public int GetQueuedCount(PoolableObjectType type)
            => _pooledObjects.TryGetValue(type, out var pool) ? pool.Count : 0;

        /// <summary>Returns every active object that has a <see cref="LocalPoolReturn"/>. Searches the whole scene, so do not call it every frame.</summary>
        public void ReturnAllActive()
        {
            int count = 0;
            foreach (var lr in FindObjectsByType<LocalPoolReturn>(FindObjectsSortMode.None))
            {
                if (lr == null) continue;
                lr.ReturnToPoolNow();
                count++;
            }
            MID_Logger.LogInfo(_logLevel,
                $"ReturnAllActive — {count} object(s) returned.",
                nameof(LocalObjectPool));
        }

        /// <summary>Destroys all queued instances and forgets every registration. Instances that are currently checked out are not touched.</summary>
        public void ClearPool()
        {
            int total = 0;
            foreach (var type in _registeredTypes)
            {
                var pool = _pooledObjects[type];
                while (pool.Count > 0)
                {
                    var obj = pool.Dequeue();
                    if (obj != null) { Destroy(obj); total++; }
                }
            }

            _pooledObjects.Clear();
            _typeConfigs.Clear();
            _typePrefabs.Clear();
            _registeredTypes.Clear();
            _totalSpawned.Clear();
            _activeCount.Clear();
            _prefabToType.Clear();
            _queuedObjects.Clear();

            MID_Logger.LogInfo(_logLevel, $"Pool cleared — {total} object(s) destroyed.",
                nameof(LocalObjectPool));
        }

        #endregion

        #region Private Helpers

        private bool ValidateConfigs()
        {
            var seenTypes = new HashSet<PoolableObjectType>();
            var seenPrefabs = new Dictionary<GameObject, PoolableObjectType>();

            foreach (var cfg in poolConfigs)
            {
                if (cfg.prefab == null)
                {
                    MID_Logger.LogWarning(_logLevel,
                        $"Config '{cfg.displayName}' has null prefab — skipping.",
                        nameof(LocalObjectPool));
                    continue;
                }

                if (seenTypes.Contains(cfg.objectType))
                {
                    MID_Logger.LogError(_logLevel,
                        $"Duplicate objectType {cfg.objectType} in pool configs.",
                        nameof(LocalObjectPool));
                    return false;
                }

                if (seenPrefabs.ContainsKey(cfg.prefab))
                {
                    MID_Logger.LogError(_logLevel,
                        $"Prefab '{cfg.prefab.name}' assigned to both {seenPrefabs[cfg.prefab]} " +
                        $"and {cfg.objectType}.",
                        nameof(LocalObjectPool));
                    return false;
                }

                seenTypes.Add(cfg.objectType);
                seenPrefabs[cfg.prefab] = cfg.objectType;
            }

            return true;
        }

        private void RegisterInternal(BasicPoolConfig config)
        {
            if (config.prefab == null) return;

            _registeredTypes.Add(config.objectType);
            _typeConfigs[config.objectType] = config;
            _typePrefabs[config.objectType] = config.prefab;
            _pooledObjects[config.objectType] = new Queue<GameObject>(config.maxPoolSize);
            _totalSpawned[config.objectType] = 0;
            _activeCount[config.objectType] = 0;
            _prefabToType[config.prefab] = config.objectType;

            MID_Logger.LogDebug(_logLevel,
                $"Registered {config.objectType} prefab={config.prefab.name} " +
                $"prewarm={config.prewarmCount} max={config.maxPoolSize}",
                nameof(LocalObjectPool));

            for (int i = 0; i < config.prewarmCount; i++)
            {
                var obj = CreateInstance(config);
                // Return without decrementing — object was never "active"
                var pool = _pooledObjects[config.objectType];
                if (pool.Count < config.maxPoolSize)
                {
                    ResetObject(obj);
                    obj.transform.SetParent(transform);
                    obj.SetActive(false);
                    pool.Enqueue(obj);
                    _queuedObjects.Add(obj);
                }
            }
        }

        private GameObject CreateInstance(BasicPoolConfig config)
        {
            var obj = Instantiate(config.prefab, transform);

            var lr = obj.GetComponent<LocalPoolReturn>()
                  ?? obj.AddComponent<LocalPoolReturn>();
            lr.SetOriginalType(config.objectType);

            MID_Logger.LogDebug(_logLevel,
                $"Created instance {config.objectType} id={obj.GetInstanceID()}",
                nameof(LocalObjectPool));

            return obj;
        }

        private bool EnsureRegistered(PoolableObjectType type)
        {
            if (_registeredTypes.Contains(type)) return true;

            if (!enableAutoRegistration)
            {
                MID_Logger.LogError(_logLevel,
                    $"{type} not registered and auto-registration is disabled.",
                    nameof(LocalObjectPool));
                return false;
            }

            var prefab = FindPrefabForType(type);
            if (prefab == null)
            {
                MID_Logger.LogError(_logLevel,
                    $"{type} not registered and no matching prefab found.",
                    nameof(LocalObjectPool));
                return false;
            }

            MID_Logger.LogWarning(_logLevel,
                $"Auto-registering {type} with prefab {prefab.name}.",
                nameof(LocalObjectPool));
            AddType(type, prefab, autoRegisterPrewarmCount, autoRegisterMaxPoolSize);
            return true;
        }

        private static void ResetObject(GameObject obj)
        {
            obj.transform.position = Vector3.zero;
            obj.transform.rotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;

            var rb2d = obj.GetComponent<Rigidbody2D>();
            if (rb2d != null) { rb2d.velocity = Vector2.zero; rb2d.angularVelocity = 0f; }

            var rb3d = obj.GetComponent<Rigidbody>();
            if (rb3d != null) { rb3d.velocity = Vector3.zero; rb3d.angularVelocity = Vector3.zero; }

            foreach (var trail in obj.GetComponentsInChildren<TrailRenderer>())
                trail?.Clear();
        }

        private GameObject FindPrefabForType(PoolableObjectType type)
        {
            foreach (var cfg in poolConfigs)
                if (cfg.objectType == type && cfg.prefab != null)
                    return cfg.prefab;
            return null;
        }

        private void UpdateMonitor()
        {
            poolStatistics.Clear();
            totalPooledObjects = 0;
            totalActiveObjects = 0;
            childrenCount = transform.childCount;

            foreach (var type in _registeredTypes)
            {
                var cfg = _typeConfigs[type];
                int available = _pooledObjects[type].Count;
                int spawned = _totalSpawned.GetValueOrDefault(type, 0);
                int active = _activeCount.GetValueOrDefault(type, 0);

                totalPooledObjects += available;
                totalActiveObjects += active;

                poolStatistics.Add(new PoolStats(
                    cfg.prefab != null ? cfg.prefab.name : type.ToString(),
                    spawned, active, available, cfg.maxPoolSize));
            }
        }

        #endregion
    }
}
