// LocalObjectPool.cs
// Singleton pool manager for non-particle GameObjects.
// Uses the generated PoolableObjectType enum. Pool configs use int typeId internally
// but the public API accepts PoolableObjectType directly.

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Pools
{
    public class LocalObjectPool : Singleton<LocalObjectPool>
    {
        #region Serialized Fields

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Pool Configuration")]
        [Tooltip("Add one entry per poolable prefab. Set typeId to match a PoolableObjectType value.")]
        [MID_NamedList]
        [SerializeField] private List<BasicPoolConfig> poolConfigs = new List<BasicPoolConfig>();

        [Header("Auto-Registration")]
        [SerializeField] private int  autoRegisterPrewarmCount = 10;
        [SerializeField] private int  autoRegisterMaxPoolSize  = 20;
        [SerializeField] private bool enableAutoRegistration   = true;

        [Header("Monitor (read-only)")]
        [SerializeField] private int             totalPooledObjects;
        [SerializeField] private int             totalActiveObjects;
        [SerializeField] private int             childrenCount;
        [SerializeField] private List<PoolStats> poolStatistics = new List<PoolStats>();

        #endregion

        #region Private State

        private bool _initialized;

        // Keyed by the int value of PoolableObjectType
        private readonly Dictionary<int, Queue<GameObject>> _pooledObjects  = new();
        private readonly Dictionary<int, BasicPoolConfig>   _typeConfigs    = new();
        private readonly Dictionary<int, GameObject>        _typePrefabs    = new();
        private readonly HashSet<int>                       _registeredTypes = new();
        private readonly Dictionary<int, int>               _totalSpawned   = new();
        private readonly Dictionary<int, int>               _activeCount    = new();
        private readonly Dictionary<GameObject, int>        _prefabToType   = new();

        #endregion

        #region Properties

        public bool HasBeenInitialized() => _initialized;

        #endregion

        #region Unity Lifecycle

        private void Update()
        {
            if (_initialized) UpdateMonitor();
        }

        #endregion

        #region Public API

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

            // Chain particle pool init
            if (!LocalParticlePool.Instance.HasBeenInitialized())
                LocalParticlePool.Instance.CallInitializePool();
        }

        // ── Get ───────────────────────────────────────────────────────────────

        public GameObject GetObject(PoolableObjectType type, Vector3 position, Quaternion rotation)
            => GetObject((int)type, position, rotation);

        public GameObject GetObject(PoolableObjectType type, Vector2 position, Quaternion rotation)
            => GetObject((int)type, new Vector3(position.x, position.y, 0f), rotation);

        /// <summary>
        /// Raw int overload — use when working with dynamically resolved type IDs.
        /// Prefer the PoolableObjectType overloads in normal code.
        /// </summary>
        public GameObject GetObject(int typeId, Vector3 position, Quaternion rotation)
        {
            if (!EnsureRegistered(typeId)) return null;

            var pool = _pooledObjects[typeId];
            bool isNew = pool.Count == 0;

            var obj = isNew
                ? CreateInstance(_typeConfigs[typeId])
                : pool.Dequeue();

            if (isNew) _totalSpawned[typeId]++;
            _activeCount[typeId]++;

            obj.transform.SetParent(null);
            obj.transform.position = position;
            obj.transform.rotation = rotation;
            obj.SetActive(true);

            MID_Logger.LogDebug(_logLevel,
                $"Get {(PoolableObjectType)typeId} | id={obj.GetInstanceID()} " +
                $"new={isNew} active={_activeCount[typeId]} pool={pool.Count}",
                nameof(LocalObjectPool), nameof(GetObject));

            return obj;
        }

        // ── Return ────────────────────────────────────────────────────────────

        public void ReturnObject(GameObject obj, PoolableObjectType type)
            => ReturnObject(obj, (int)type, decrement: true);

        public void ReturnObject(GameObject obj, int typeId)
            => ReturnObject(obj, typeId, decrement: true);

        private void ReturnObject(GameObject obj, int typeId, bool decrement)
        {
            if (!_registeredTypes.Contains(typeId))
            {
                if (enableAutoRegistration)
                {
                    var prefab = FindPrefabForType(typeId);
                    if (prefab != null)
                    {
                        MID_Logger.LogWarning(_logLevel,
                            $"Auto-registering {typeId} during return.",
                            nameof(LocalObjectPool));
                        AddType(typeId, prefab, autoRegisterPrewarmCount, autoRegisterMaxPoolSize);
                    }
                    else
                    {
                        MID_Logger.LogError(_logLevel,
                            $"Return failed — type {typeId} not registered, no prefab. Destroying.",
                            nameof(LocalObjectPool));
                        Destroy(obj);
                        return;
                    }
                }
                else
                {
                    MID_Logger.LogError(_logLevel,
                        $"Return failed — type {typeId} not registered. Destroying.",
                        nameof(LocalObjectPool));
                    Destroy(obj);
                    return;
                }
            }

            var pool   = _pooledObjects[typeId];
            var config = _typeConfigs[typeId];

            if (pool.Count >= config.maxPoolSize)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Pool full for type {typeId} — destroying overflow.",
                    nameof(LocalObjectPool));
                if (decrement) _activeCount[typeId]--;
                Destroy(obj);
                return;
            }

            if (decrement && _activeCount.ContainsKey(typeId))
                _activeCount[typeId]--;

            ResetObject(obj);
            obj.transform.SetParent(transform);
            obj.SetActive(false);
            pool.Enqueue(obj);

            MID_Logger.LogDebug(_logLevel,
                $"Returned {typeId} | id={obj.GetInstanceID()} pool={pool.Count}",
                nameof(LocalObjectPool));
        }

        // ── Registration ──────────────────────────────────────────────────────

        /// <summary>Register a new type at runtime using the generated enum.</summary>
        public void AddType(PoolableObjectType type, GameObject prefab,
                            int prewarm = 5, int maxSize = 15)
            => AddType((int)type, prefab, prewarm, maxSize);

        /// <summary>Register a new type at runtime using a raw int ID.</summary>
        public void AddType(int typeId, GameObject prefab, int prewarm = 5, int maxSize = 15)
        {
            if (_registeredTypes.Contains(typeId))
            {
                MID_Logger.LogWarning(_logLevel, $"Type {typeId} already registered.",
                    nameof(LocalObjectPool));
                return;
            }

            if (_prefabToType.ContainsKey(prefab))
            {
                MID_Logger.LogError(_logLevel,
                    $"Prefab '{prefab.name}' already registered as type {_prefabToType[prefab]}. " +
                    $"Cannot re-register as {typeId}.",
                    nameof(LocalObjectPool));
                return;
            }

            var config = new BasicPoolConfig
            {
                typeId       = typeId,
                displayName  = prefab.name,
                prefab       = prefab,
                prewarmCount = prewarm,
                maxPoolSize  = maxSize
            };

            RegisterInternal(config);
            MID_Logger.LogInfo(_logLevel,
                $"Runtime registered type {typeId} prewarm={prewarm} max={maxSize}.",
                nameof(LocalObjectPool));
        }

        public bool IsRegistered(PoolableObjectType type)  => _registeredTypes.Contains((int)type);
        public bool IsRegistered(int typeId)                => _registeredTypes.Contains(typeId);

        /// <summary>Return every active pooled object back to its pool.</summary>
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

        public void ClearPool()
        {
            int total = 0;
            foreach (var typeId in _registeredTypes)
            {
                var pool = _pooledObjects[typeId];
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

            MID_Logger.LogInfo(_logLevel, $"Pool cleared — {total} object(s) destroyed.",
                nameof(LocalObjectPool));
        }

        #endregion

        #region Private Helpers

        private bool ValidateConfigs()
        {
            var seenTypes   = new HashSet<int>();
            var seenPrefabs = new Dictionary<GameObject, int>();

            foreach (var cfg in poolConfigs)
            {
                if (cfg.prefab == null)
                {
                    MID_Logger.LogWarning(_logLevel,
                        $"Config '{cfg.displayName}' has null prefab — skipping.",
                        nameof(LocalObjectPool));
                    continue;
                }

                if (seenTypes.Contains(cfg.typeId))
                {
                    MID_Logger.LogError(_logLevel,
                        $"Duplicate typeId {cfg.typeId} in pool configs.",
                        nameof(LocalObjectPool));
                    return false;
                }

                if (seenPrefabs.ContainsKey(cfg.prefab))
                {
                    MID_Logger.LogError(_logLevel,
                        $"Prefab '{cfg.prefab.name}' assigned to both typeId " +
                        $"{seenPrefabs[cfg.prefab]} and {cfg.typeId}.",
                        nameof(LocalObjectPool));
                    return false;
                }

                seenTypes.Add(cfg.typeId);
                seenPrefabs[cfg.prefab] = cfg.typeId;
            }

            return true;
        }

        private void RegisterInternal(BasicPoolConfig config)
        {
            if (config.prefab == null) return;

            _registeredTypes.Add(config.typeId);
            _typeConfigs[config.typeId]   = config;
            _typePrefabs[config.typeId]   = config.prefab;
            _pooledObjects[config.typeId] = new Queue<GameObject>(config.maxPoolSize);
            _totalSpawned[config.typeId]  = 0;
            _activeCount[config.typeId]   = 0;
            _prefabToType[config.prefab]  = config.typeId;

            MID_Logger.LogDebug(_logLevel,
                $"Registered typeId={config.typeId} prefab={config.prefab.name} " +
                $"prewarm={config.prewarmCount} max={config.maxPoolSize}",
                nameof(LocalObjectPool));

            for (int i = 0; i < config.prewarmCount; i++)
            {
                var obj = CreateInstance(config);
                ReturnObject(obj, config.typeId, decrement: false);
            }
        }

        private GameObject CreateInstance(BasicPoolConfig config)
        {
            var obj = Instantiate(config.prefab, transform);

            var lr = obj.GetComponent<LocalPoolReturn>()
                  ?? obj.AddComponent<LocalPoolReturn>();
            lr.SetOriginalType((PoolableObjectType)config.typeId);

            MID_Logger.LogDebug(_logLevel,
                $"Created instance typeId={config.typeId} id={obj.GetInstanceID()}",
                nameof(LocalObjectPool));

            return obj;
        }

        private bool EnsureRegistered(int typeId)
        {
            if (_registeredTypes.Contains(typeId)) return true;

            if (!enableAutoRegistration)
            {
                MID_Logger.LogError(_logLevel,
                    $"Type {typeId} not registered and auto-registration is disabled.",
                    nameof(LocalObjectPool));
                return false;
            }

            var prefab = FindPrefabForType(typeId);
            if (prefab == null)
            {
                MID_Logger.LogError(_logLevel,
                    $"Type {typeId} not registered and no matching prefab found.",
                    nameof(LocalObjectPool));
                return false;
            }

            MID_Logger.LogWarning(_logLevel,
                $"Auto-registering type {typeId} with prefab {prefab.name}.",
                nameof(LocalObjectPool));
            AddType(typeId, prefab, autoRegisterPrewarmCount, autoRegisterMaxPoolSize);
            return true;
        }

        private static void ResetObject(GameObject obj)
{
    obj.transform.position   = Vector3.zero;
    obj.transform.rotation   = Quaternion.identity;
    obj.transform.localScale = Vector3.one;

    var rb2d = obj.GetComponent<Rigidbody2D>();
    if (rb2d != null) { rb2d.velocity = Vector2.zero; rb2d.angularVelocity = 0f; }

    var rb3d = obj.GetComponent<Rigidbody>();
    if (rb3d != null) { rb3d.velocity = Vector3.zero; rb3d.angularVelocity = Vector3.zero; }

    foreach (var trail in obj.GetComponentsInChildren<TrailRenderer>())
        trail?.Clear();
}

        private GameObject FindPrefabForType(int typeId)
        {
            // Try to match by display name containing the enum member name
            string typeName = ((PoolableObjectType)typeId).ToString();
            foreach (var cfg in poolConfigs)
                if (cfg.prefab != null && cfg.prefab.name.Contains(typeName))
                    return cfg.prefab;
            return null;
        }

        private void UpdateMonitor()
        {
            poolStatistics.Clear();
            totalPooledObjects = 0;
            totalActiveObjects = 0;
            childrenCount      = transform.childCount;

            foreach (var typeId in _registeredTypes)
            {
                var cfg      = _typeConfigs[typeId];
                int available = _pooledObjects[typeId].Count;
                int spawned   = _totalSpawned.GetValueOrDefault(typeId, 0);
                int active    = _activeCount.GetValueOrDefault(typeId, 0);

                totalPooledObjects += available;
                totalActiveObjects += active;

                poolStatistics.Add(new PoolStats(
                    cfg.prefab != null ? cfg.prefab.name : $"type_{typeId}",
                    spawned, active, available, cfg.maxPoolSize));
            }
        }

        #endregion
    }
}
