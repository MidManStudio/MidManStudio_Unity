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
        [SerializeField] private int autoRegisterPrewarmCount = 10;
        [SerializeField] private int autoRegisterMaxPoolSize = 30;
        [SerializeField] private bool enableAutoRegistration = true;

        [Header("Monitor (read-only)")]
        [SerializeField] private int totalPooledParticles;
        [SerializeField] private int totalActiveParticles;
        [SerializeField] private int childrenCount;
        [SerializeField] private List<ParticlePoolStats> poolStatistics = new List<ParticlePoolStats>();
        [SerializeField] private List<string> configWarnings = new List<string>();

        #endregion

        #region Private State

        private bool _initialized;

        private readonly Dictionary<PoolableParticleType, Queue<GameObject>> _pooledParticles = new();
        private readonly Dictionary<PoolableParticleType, ParticlePoolConfig> _typeConfigs = new();
        private readonly Dictionary<PoolableParticleType, GameObject> _typePrefabs = new();
        private readonly HashSet<PoolableParticleType> _registeredTypes = new();
        private readonly Dictionary<PoolableParticleType, int> _totalSpawned = new();
        private readonly Dictionary<PoolableParticleType, int> _activeCount = new();
        private readonly Dictionary<GameObject, PoolableParticleType> _prefabToType = new();

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
        {
            if (!EnsureRegistered(type)) return null;

            var pool = _pooledParticles[type];
            bool isNew = pool.Count == 0;

            var obj = isNew
                ? CreateInstance(_typeConfigs[type])
                : pool.Dequeue();

            if (isNew) _totalSpawned[type]++;
            _activeCount[type]++;

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
                $"Get {type} | id={obj.GetInstanceID()} " +
                $"new={isNew} active={_activeCount[type]} pool={pool.Count}",
                nameof(LocalParticlePool), nameof(GetObject));

            return obj;
        }

        public GameObject GetObject(PoolableParticleType type, Vector2 position, Quaternion rotation)
            => GetObject(type, new Vector3(position.x, position.y, 0f), rotation);

        // ── Return ────────────────────────────────────────────────────────────

        public void ReturnObject(GameObject obj, PoolableParticleType type)
        {
            if (!_registeredTypes.Contains(type))
            {
                MID_Logger.LogError(_logLevel,
                    $"Return failed — {type} not registered. Destroying.",
                    nameof(LocalParticlePool));
                if (_activeCount.ContainsKey(type)) _activeCount[type]--;
                Destroy(obj);
                return;
            }

            var pool = _pooledParticles[type];
            var config = _typeConfigs[type];

            if (pool.Count >= config.maxPoolSize)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Particle pool full for {type} — destroying overflow.",
                    nameof(LocalParticlePool));
                if (_activeCount.ContainsKey(type)) _activeCount[type]--;
                Destroy(obj);
                return;
            }

            if (_activeCount.ContainsKey(type)) _activeCount[type]--;

            foreach (var ps in obj.GetComponentsInChildren<ParticleSystem>())
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ResetParticle(obj);
            obj.transform.SetParent(transform);
            obj.SetActive(false);
            pool.Enqueue(obj);

            MID_Logger.LogDebug(_logLevel,
                $"Returned {type} | id={obj.GetInstanceID()} pool={pool.Count}",
                nameof(LocalParticlePool));
        }

        // ── Registration ──────────────────────────────────────────────────────

        public void AddType(PoolableParticleType type, GameObject prefab,
                            int prewarm = 10, int maxSize = 30, float lifetime = 5f)
        {
            if (_registeredTypes.Contains(type))
            {
                MID_Logger.LogWarning(_logLevel, $"{type} already registered.",
                    nameof(LocalParticlePool));
                return;
            }

            if (_prefabToType.ContainsKey(prefab))
            {
                MID_Logger.LogError(_logLevel,
                    $"Prefab '{prefab.name}' already registered as {_prefabToType[prefab]}.",
                    nameof(LocalParticlePool));
                return;
            }

            RegisterInternal(new ParticlePoolConfig
            {
                particleType = type,
                displayName = prefab.name,
                prefab = prefab,
                prewarmCount = prewarm,
                maxPoolSize = maxSize,
                defaultLifetime = lifetime
            });
        }

        public bool IsRegistered(PoolableParticleType type) => _registeredTypes.Contains(type);

        public void ClearPool()
        {
            int total = 0;
            foreach (var type in _registeredTypes)
            {
                var pool = _pooledParticles[type];
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
            var seenTypes = new HashSet<PoolableParticleType>();
            var seenPrefabs = new Dictionary<GameObject, PoolableParticleType>();

            foreach (var cfg in particleConfigs)
            {
                if (cfg.prefab == null)
                {
                    configWarnings.Add($"Config '{cfg.displayName}' has null prefab.");
                    continue;
                }

                if (seenTypes.Contains(cfg.particleType))
                {
                    configWarnings.Add($"Duplicate particleType {cfg.particleType}.");
                    return false;
                }

                if (seenPrefabs.ContainsKey(cfg.prefab))
                {
                    configWarnings.Add(
                        $"Prefab '{cfg.prefab.name}' assigned to both " +
                        $"{seenPrefabs[cfg.prefab]} and {cfg.particleType}.");
                    return false;
                }

                seenTypes.Add(cfg.particleType);
                seenPrefabs[cfg.prefab] = cfg.particleType;
            }

            return true;
        }

        private void RegisterInternal(ParticlePoolConfig config)
        {
            if (config.prefab == null) return;

            _registeredTypes.Add(config.particleType);
            _typeConfigs[config.particleType] = config;
            _typePrefabs[config.particleType] = config.prefab;
            _pooledParticles[config.particleType] = new Queue<GameObject>(config.maxPoolSize);
            _totalSpawned[config.particleType] = 0;
            _activeCount[config.particleType] = 0;
            _prefabToType[config.prefab] = config.particleType;

            MID_Logger.LogDebug(_logLevel,
                $"Registered {config.particleType} prefab={config.prefab.name} " +
                $"prewarm={config.prewarmCount} max={config.maxPoolSize}",
                nameof(LocalParticlePool));

            for (int i = 0; i < config.prewarmCount; i++)
            {
                var obj = CreateInstance(config);
                var pool = _pooledParticles[config.particleType];
                if (pool.Count < config.maxPoolSize)
                {
                    ResetParticle(obj);
                    obj.transform.SetParent(transform);
                    obj.SetActive(false);
                    pool.Enqueue(obj);
                }
            }
        }

        private GameObject CreateInstance(ParticlePoolConfig config)
        {
            var obj = Instantiate(config.prefab, transform);

            var pr = obj.GetComponent<LocalParticleReturn>()
                  ?? obj.AddComponent<LocalParticleReturn>();
            pr.SetOriginalType(config.particleType);
            pr.SetMaxLifetime(config.defaultLifetime);

            return obj;
        }

        private bool EnsureRegistered(PoolableParticleType type)
        {
            if (_registeredTypes.Contains(type)) return true;

            if (!enableAutoRegistration)
            {
                MID_Logger.LogError(_logLevel,
                    $"{type} not registered and auto-registration is disabled.",
                    nameof(LocalParticlePool));
                return false;
            }

            var prefab = FindPrefabForType(type);
            if (prefab == null)
            {
                MID_Logger.LogError(_logLevel,
                    $"{type} not registered and no matching prefab found.",
                    nameof(LocalParticlePool));
                return false;
            }

            MID_Logger.LogWarning(_logLevel,
                $"Auto-registering particle {type} with prefab {prefab.name}.",
                nameof(LocalParticlePool));
            AddType(type, prefab, autoRegisterPrewarmCount, autoRegisterMaxPoolSize);
            return true;
        }

        private static void ResetParticle(GameObject obj)
        {
            obj.transform.position = Vector3.zero;
            obj.transform.rotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;

            foreach (var ps in obj.GetComponentsInChildren<ParticleSystem>())
            {
                ps.Clear();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        private GameObject FindPrefabForType(PoolableParticleType type)
        {
            foreach (var cfg in particleConfigs)
                if (cfg.particleType == type && cfg.prefab != null)
                    return cfg.prefab;
            return null;
        }

        private void UpdateMonitor()
        {
            poolStatistics.Clear();
            totalPooledParticles = 0;
            totalActiveParticles = 0;
            childrenCount = transform.childCount;

            foreach (var type in _registeredTypes)
            {
                var cfg = _typeConfigs[type];
                int available = _pooledParticles[type].Count;
                int spawned = _totalSpawned.GetValueOrDefault(type, 0);
                int active = _activeCount.GetValueOrDefault(type, 0);

                totalPooledParticles += available;
                totalActiveParticles += active;

                poolStatistics.Add(new ParticlePoolStats(
                    cfg.prefab != null ? cfg.prefab.name : type.ToString(),
                    spawned, active, available, cfg.maxPoolSize));
            }
        }

        #endregion
    }
}