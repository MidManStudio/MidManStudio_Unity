// LocalParticlePool.cs
// Singleton pool manager for particle GameObjects.
// Uses the generated PoolableParticleType enum.

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Pools
{
    public class LocalParticlePool : Singleton<LocalParticlePool>
    {
        #region Serialized Fields

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Particle Pool Configuration")]
        [MID_NamedList]
        [SerializeField] private List<ParticlePoolConfig> particleConfigs = new List<ParticlePoolConfig>();

        [Header("Auto-Registration")]
        [SerializeField] private int  autoRegisterPrewarmCount = 10;
        [SerializeField] private int  autoRegisterMaxPoolSize  = 30;
        [SerializeField] private bool enableAutoRegistration   = true;

        [Header("Monitor (read-only)")]
        [SerializeField] private int                     totalPooledParticles;
        [SerializeField] private int                     totalActiveParticles;
        [SerializeField] private int                     childrenCount;
        [SerializeField] private List<ParticlePoolStats> poolStatistics  = new List<ParticlePoolStats>();
        [SerializeField] private List<string>            configWarnings  = new List<string>();

        #endregion

        #region Private State

        private bool _initialized;

        private readonly Dictionary<int, Queue<GameObject>>   _pooledParticles = new();
        private readonly Dictionary<int, ParticlePoolConfig>  _typeConfigs     = new();
        private readonly Dictionary<int, GameObject>          _typePrefabs     = new();
        private readonly HashSet<int>                         _registeredTypes = new();
        private readonly Dictionary<int, int>                 _totalSpawned    = new();
        private readonly Dictionary<int, int>                 _activeCount     = new();
        private readonly Dictionary<GameObject, int>          _prefabToType    = new();

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

            MID_Logger.LogInfo(_logLevel, "Initialising particle pool.",
                nameof(LocalParticlePool), nameof(CallInitializePool));

            configWarnings.Clear();

            if (!ValidateConfigs())
            {
                MID_Logger.LogError(_logLevel,
                    $"Particle pool validation failed — {configWarnings.Count} error(s).",
                    nameof(LocalParticlePool), nameof(CallInitializePool));
                foreach (var w in configWarnings)
                    Debug.LogError($"[LocalParticlePool] {w}");
                return;
            }

            foreach (var config in particleConfigs)
                RegisterInternal(config);

            _initialized = true;
            MID_Logger.LogInfo(_logLevel,
                $"Particle pool ready — {particleConfigs.Count} type(s) registered.",
                nameof(LocalParticlePool), nameof(CallInitializePool));
        }

        // ── Get ───────────────────────────────────────────────────────────────

        public GameObject GetObject(PoolableParticleType type, Vector3 position, Quaternion rotation)
            => GetObject((int)type, position, rotation);

        public GameObject GetObject(PoolableParticleType type, Vector2 position, Quaternion rotation)
            => GetObject((int)type, new Vector3(position.x, position.y, 0f), rotation);

        public GameObject GetObject(int typeId, Vector3 position, Quaternion rotation)
        {
            if (!EnsureRegistered(typeId)) return null;

            var pool  = _pooledParticles[typeId];
            bool isNew = pool.Count == 0;

            var obj = isNew
                ? CreateInstance(_typeConfigs[typeId])
                : pool.Dequeue();

            if (isNew) _totalSpawned[typeId]++;
            _activeCount[typeId]++;

            obj.SetActive(true);
            obj.transform.position = position;
            obj.transform.rotation = rotation;
            obj.transform.SetParent(null);

            foreach (var ps in obj.GetComponentsInChildren<ParticleSystem>())
            {
                ps.Clear();
                ps.Play();
            }

            MID_Logger.LogDebug(_logLevel,
                $"Get {(PoolableParticleType)typeId} | id={obj.GetInstanceID()} " +
                $"new={isNew} active={_activeCount[typeId]} pool={pool.Count}",
                nameof(LocalParticlePool), nameof(GetObject));

            return obj;
        }

        // ── Return ────────────────────────────────────────────────────────────

        public void ReturnObject(GameObject obj, PoolableParticleType type)
            => ReturnObject(obj, (int)type, decrement: true);

        public void ReturnObject(GameObject obj, int typeId)
            => ReturnObject(obj, typeId, decrement: true);

        private void ReturnObject(GameObject obj, int typeId, bool decrement)
        {
            if (!_registeredTypes.Contains(typeId))
            {
                MID_Logger.LogError(_logLevel,
                    $"Return failed — type {typeId} not registered. Destroying.",
                    nameof(LocalParticlePool));
                if (decrement && _activeCount.ContainsKey(typeId)) _activeCount[typeId]--;
                Destroy(obj);
                return;
            }

            var pool   = _pooledParticles[typeId];
            var config = _typeConfigs[typeId];

            if (pool.Count >= config.maxPoolSize)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Particle pool full for type {typeId} — destroying overflow.",
                    nameof(LocalParticlePool));
                if (decrement) _activeCount[typeId]--;
                Destroy(obj);
                return;
            }

            if (decrement && _activeCount.ContainsKey(typeId))
                _activeCount[typeId]--;

            foreach (var ps in obj.GetComponentsInChildren<ParticleSystem>())
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ResetParticle(obj);
            obj.transform.SetParent(transform);
            obj.SetActive(false);
            pool.Enqueue(obj);

            MID_Logger.LogDebug(_logLevel,
                $"Returned {typeId} | id={obj.GetInstanceID()} pool={pool.Count}",
                nameof(LocalParticlePool));
        }

        // ── Registration ──────────────────────────────────────────────────────

        public void AddType(PoolableParticleType type, GameObject prefab,
                            int prewarm = 10, int maxSize = 30, float lifetime = 5f)
            => AddType((int)type, prefab, prewarm, maxSize, lifetime);

        public void AddType(int typeId, GameObject prefab,
                            int prewarm = 10, int maxSize = 30, float lifetime = 5f)
        {
            if (_registeredTypes.Contains(typeId))
            {
                MID_Logger.LogWarning(_logLevel, $"Type {typeId} already registered.",
                    nameof(LocalParticlePool));
                return;
            }

            if (_prefabToType.ContainsKey(prefab))
            {
                MID_Logger.LogError(_logLevel,
                    $"Prefab '{prefab.name}' already registered as type {_prefabToType[prefab]}.",
                    nameof(LocalParticlePool));
                return;
            }

            RegisterInternal(new ParticlePoolConfig
            {
                typeId          = typeId,
                displayName     = prefab.name,
                prefab          = prefab,
                prewarmCount    = prewarm,
                maxPoolSize     = maxSize,
                defaultLifetime = lifetime
            });
        }

        public bool IsRegistered(PoolableParticleType type) => _registeredTypes.Contains((int)type);
        public bool IsRegistered(int typeId)                 => _registeredTypes.Contains(typeId);

        public void ClearPool()
        {
            int total = 0;
            foreach (var typeId in _registeredTypes)
            {
                var pool = _pooledParticles[typeId];
                while (pool.Count > 0)
                {
                    var obj = pool.Dequeue();
                    if (obj != null) { Destroy(obj); total++; }
                }
            }

            _pooledParticles.Clear();
            _typeConfigs.Clear();
            _typePrefabs.Clear();
            _registeredTypes.Clear();
            _totalSpawned.Clear();
            _activeCount.Clear();
            _prefabToType.Clear();

            MID_Logger.LogInfo(_logLevel, $"Particle pool cleared — {total} destroyed.",
                nameof(LocalParticlePool));
        }

        #endregion

        #region Private Helpers

        private bool ValidateConfigs()
        {
            var seenTypes   = new HashSet<int>();
            var seenPrefabs = new Dictionary<GameObject, int>();

            foreach (var cfg in particleConfigs)
            {
                if (cfg.prefab == null)
                {
                    configWarnings.Add($"Config '{cfg.displayName}' has null prefab.");
                    continue;
                }

                if (seenTypes.Contains(cfg.typeId))
                {
                    configWarnings.Add($"Duplicate typeId {cfg.typeId}.");
                    return false;
                }

                if (seenPrefabs.ContainsKey(cfg.prefab))
                {
                    configWarnings.Add(
                        $"Prefab '{cfg.prefab.name}' assigned to both typeId " +
                        $"{seenPrefabs[cfg.prefab]} and {cfg.typeId}.");
                    return false;
                }

                seenTypes.Add(cfg.typeId);
                seenPrefabs[cfg.prefab] = cfg.typeId;
            }

            return true;
        }

        private void RegisterInternal(ParticlePoolConfig config)
        {
            if (config.prefab == null) return;

            _registeredTypes.Add(config.typeId);
            _typeConfigs[config.typeId]     = config;
            _typePrefabs[config.typeId]     = config.prefab;
            _pooledParticles[config.typeId] = new Queue<GameObject>(config.maxPoolSize);
            _totalSpawned[config.typeId]    = 0;
            _activeCount[config.typeId]     = 0;
            _prefabToType[config.prefab]    = config.typeId;

            MID_Logger.LogDebug(_logLevel,
                $"Registered typeId={config.typeId} prefab={config.prefab.name} " +
                $"prewarm={config.prewarmCount} max={config.maxPoolSize}",
                nameof(LocalParticlePool));

            for (int i = 0; i < config.prewarmCount; i++)
            {
                var obj = CreateInstance(config);
                ReturnObject(obj, config.typeId, decrement: false);
            }
        }

        private GameObject CreateInstance(ParticlePoolConfig config)
        {
            var obj = Instantiate(config.prefab, transform);

            var pr = obj.GetComponent<LocalParticleReturn>()
                  ?? obj.AddComponent<LocalParticleReturn>();
            pr.SetOriginalType((PoolableParticleType)config.typeId);
            pr.SetMaxLifetime(config.defaultLifetime);

            return obj;
        }

        private bool EnsureRegistered(int typeId)
        {
            if (_registeredTypes.Contains(typeId)) return true;

            if (!enableAutoRegistration)
            {
                MID_Logger.LogError(_logLevel,
                    $"Type {typeId} not registered and auto-registration is disabled.",
                    nameof(LocalParticlePool));
                return false;
            }

            var prefab = FindPrefabForType(typeId);
            if (prefab == null)
            {
                MID_Logger.LogError(_logLevel,
                    $"Type {typeId} not registered and no matching prefab found.",
                    nameof(LocalParticlePool));
                return false;
            }

            MID_Logger.LogWarning(_logLevel,
                $"Auto-registering particle type {typeId} with prefab {prefab.name}.",
                nameof(LocalParticlePool));
            AddType(typeId, prefab, autoRegisterPrewarmCount, autoRegisterMaxPoolSize);
            return true;
        }

        private static void ResetParticle(GameObject obj)
        {
            obj.transform.position   = Vector3.zero;
            obj.transform.rotation   = Quaternion.identity;
            obj.transform.localScale = Vector3.one;

            foreach (var ps in obj.GetComponentsInChildren<ParticleSystem>())
            {
                ps.Clear();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        private GameObject FindPrefabForType(int typeId)
        {
            string typeName = ((PoolableParticleType)typeId).ToString();
            foreach (var cfg in particleConfigs)
                if (cfg.prefab != null && cfg.prefab.name.Contains(typeName))
                    return cfg.prefab;
            return null;
        }

        private void UpdateMonitor()
        {
            poolStatistics.Clear();
            totalPooledParticles = 0;
            totalActiveParticles = 0;
            childrenCount        = transform.childCount;

            foreach (var typeId in _registeredTypes)
            {
                var cfg       = _typeConfigs[typeId];
                int available = _pooledParticles[typeId].Count;
                int spawned   = _totalSpawned.GetValueOrDefault(typeId, 0);
                int active    = _activeCount.GetValueOrDefault(typeId, 0);

                totalPooledParticles += available;
                totalActiveParticles += active;

                poolStatistics.Add(new ParticlePoolStats(
                    cfg.prefab != null ? cfg.prefab.name : $"type_{typeId}",
                    spawned, active, available, cfg.maxPoolSize));
            }
        }

        #endregion
    }
}
