using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Utilities;

namespace MidManStudio.Core.Pools
{
    // =============================================================================
    // PoolableParticleType — DuckDuckBara
    // Add new particle types here as needed.
    // =============================================================================
    [System.Serializable]
    public enum PoolableParticleType
    {
        // ===== FLIGHT PLAN EFFECTS (300-399) =====
        FP_Explosion_Small = 300,
        FP_Explosion_Medium = 301,
        FP_Explosion_Large = 302,
        FP_PlayerHit = 303,
        FP_PowerUpPickup = 304,
        FP_ProjectileImpact = 305,
        FP_PlayerDeath = 306, 
        FP_CoinPickup = 307,  
    }

    [System.Serializable]
    public class ParticlePoolConfig : IArrayElementTitle
    {
        public PoolableParticleType particleType;
        public GameObject prefab;
        public int prewarmCount = 10;
        public int maxPoolSize = 30;
        public float defaultLifetime = 5f;

        public string Name => GetPropperName();

        private string GetPropperName()
        {
            if (prefab != null) return (prefab.name);
            return particleType.ToString();
        }
    }

    [System.Serializable]
    public class ParticlePoolStats
    {
        public string prefabName;
        public int totalSpawned;
        public int currentlyActive;
        public int availableInPool;
        public int maxPoolSize;

        public ParticlePoolStats(string name, int spawned, int active, int available, int max)
        {
            prefabName = name;
            totalSpawned = spawned;
            currentlyActive = active;
            availableInPool = available;
            maxPoolSize = max;
        }
    }

    /// <summary>
    /// LocalParticlePool — Singleton pool manager for particle effect GameObjects.
    ///
    /// USAGE:
    ///   Initialized automatically by LocalObjectPool.CallInitializePool().
    ///   Retrieve:  LocalParticlePool.Instance.GetObject(PoolableParticleType.MuzzleFlash, pos, rot)
    ///   Return:    LocalParticlePool.Instance.ReturnObject(gameObject, PoolableParticleType.MuzzleFlash)
    ///
    ///   Particles auto-return via LocalParticleReturn after their defaultLifetime expires.
    ///
    /// DEPENDENCY: MID_Logger, MidManStudio.Core.Singleton
    /// </summary>
    public class LocalParticlePool : Singleton<LocalParticlePool>
    {
        #region Serialized Fields

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Particle Pool Configuration")]
        [MID_NamedList]
        [SerializeField] private List<ParticlePoolConfig> particleConfigs = new List<ParticlePoolConfig>();

        [Header("Auto-Registration Settings")]
        [SerializeField] private int autoRegisterPrewarmCount = 10;
        [SerializeField] private int autoRegisterMaxPoolSize = 30;
        [SerializeField] private bool enableAutoRegistration = true;

        [Header("Pool Monitoring (Read Only)")]
        [SerializeField] private int totalPooledParticles;
        [SerializeField] private int totalActiveParticles;
        [SerializeField] private int childrenCount;
        [SerializeField] private List<ParticlePoolStats> poolStatistics = new List<ParticlePoolStats>();

        [Header("Validation")]
        [SerializeField] private List<string> configurationWarnings = new List<string>();
        [SerializeField] private bool hasValidationErrors = false;

        #endregion

        #region Private Fields

        private bool _hasBeenInitialized = false;

        private Dictionary<PoolableParticleType, Queue<GameObject>> pooledParticles = new Dictionary<PoolableParticleType, Queue<GameObject>>();
        private Dictionary<PoolableParticleType, ParticlePoolConfig> typeConfigs = new Dictionary<PoolableParticleType, ParticlePoolConfig>();
        private Dictionary<PoolableParticleType, GameObject> typePrefabs = new Dictionary<PoolableParticleType, GameObject>();
        private HashSet<PoolableParticleType> registeredTypes = new HashSet<PoolableParticleType>();
        private Dictionary<PoolableParticleType, int> totalSpawnedCount = new Dictionary<PoolableParticleType, int>();
        private Dictionary<PoolableParticleType, int> currentActiveCount = new Dictionary<PoolableParticleType, int>();
        private Dictionary<GameObject, PoolableParticleType> prefabToTypeMap = new Dictionary<GameObject, PoolableParticleType>();
        private Dictionary<PoolableParticleType, GameObject> typeToPrefabMap = new Dictionary<PoolableParticleType, GameObject>();

        #endregion

        #region Properties

        public bool HasBeenInitialized() => _hasBeenInitialized;

        #endregion

        #region Unity Lifecycle

        private void Update()
        {
            if (_hasBeenInitialized)
                UpdatePoolStatistics();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Initializes all configured particle pools. Called automatically by LocalObjectPool.
        /// </summary>
        public void CallInitializePool()
        {
            if (_hasBeenInitialized) return;

            MID_Logger.LogInfo(_logLevel, "Starting particle pool initialization.", nameof(LocalParticlePool), nameof(CallInitializePool));

            configurationWarnings.Clear();
            hasValidationErrors = false;

            if (!ValidatePoolConfigurations())
            {
                hasValidationErrors = true;
                MID_Logger.LogError(_logLevel,
                    $"PARTICLE POOL VALIDATION FAILED — {configurationWarnings.Count} errors. Check inspector.",
                    nameof(LocalParticlePool), nameof(CallInitializePool));

                foreach (var warning in configurationWarnings)
                    Debug.LogError($"[LocalParticlePool] {warning}");

                return;
            }

            foreach (var config in particleConfigs)
                RegisterPrefabInternal(config);

            _hasBeenInitialized = true;
            MID_Logger.LogInfo(_logLevel, $"Particle pool initialized with {particleConfigs.Count} types.", nameof(LocalParticlePool), nameof(CallInitializePool));
        }

        /// <summary>
        /// Retrieves a particle object from the pool, activates it, and plays all particle systems.
        /// </summary>
        public GameObject GetObject(PoolableParticleType particleType, Vector3 position, Quaternion rotation)
        {
            if (!registeredTypes.Contains(particleType))
            {
                if (enableAutoRegistration)
                {
                    GameObject foundPrefab = FindPrefabForType(particleType);
                    if (foundPrefab != null)
                    {
                        MID_Logger.LogWarning(_logLevel, $"Auto-registering {particleType}.", nameof(LocalParticlePool), nameof(GetObject));
                        AddType(particleType, foundPrefab, autoRegisterPrewarmCount, autoRegisterMaxPoolSize);
                    }
                    else
                    {
                        MID_Logger.LogError(_logLevel, $"Type {particleType} not registered and no prefab found.", nameof(LocalParticlePool), nameof(GetObject));
                        return null;
                    }
                }
                else
                {
                    MID_Logger.LogError(_logLevel, $"Type {particleType} is not registered.", nameof(LocalParticlePool), nameof(GetObject));
                    return null;
                }
            }

            var pool = pooledParticles[particleType];
            GameObject obj;
            bool wasCreatedNew = false;

            if (pool.Count == 0)
            {
                obj = CreatePooledInstance(typeConfigs[particleType]);
                totalSpawnedCount[particleType]++;
                wasCreatedNew = true;
            }
            else
            {
                obj = pool.Dequeue();
            }

            currentActiveCount[particleType]++;

            obj.SetActive(true);
            obj.transform.position = position;
            obj.transform.rotation = rotation;
            obj.transform.SetParent(null);

            ParticleSystem[] particleSystems = obj.GetComponentsInChildren<ParticleSystem>();
            foreach (var ps in particleSystems) { ps.Clear(); ps.Play(); }

            MID_Logger.LogDebug(_logLevel,
                $"Retrieved {particleType} | ID: {obj.GetInstanceID()} | New: {wasCreatedNew} | Active: {currentActiveCount[particleType]} | Pool: {pool.Count}",
                nameof(LocalParticlePool), nameof(GetObject));

            return obj;
        }

        /// <summary>2D overload — Z is set to 0.</summary>
        public GameObject GetObject(PoolableParticleType particleType, Vector2 position, Quaternion rotation)
            => GetObject(particleType, new Vector3(position.x, position.y, 0), rotation);

        /// <summary>Returns a particle object to the pool. Call this or let LocalParticleReturn handle it.</summary>
        public void ReturnObject(GameObject obj, PoolableParticleType particleType)
        {
            MID_Logger.LogDebug(_logLevel, $"Return request: {particleType} | ID: {obj.GetInstanceID()}", nameof(LocalParticlePool), nameof(ReturnObject));
            ReturnObjectInternal(obj, particleType, true);
        }

        /// <summary>Registers a new particle type at runtime.</summary>
        public void AddType(PoolableParticleType particleType, GameObject prefab, int prewarmCount = 10, int maxPoolSize = 30, float defaultLifetime = 5f)
        {
            if (registeredTypes.Contains(particleType))
            {
                MID_Logger.LogWarning(_logLevel, $"Type {particleType} already registered.", nameof(LocalParticlePool), nameof(AddType));
                return;
            }

            if (prefabToTypeMap.ContainsKey(prefab))
            {
                MID_Logger.LogError(_logLevel, $"Prefab '{prefab.name}' already registered as {prefabToTypeMap[prefab]}.", nameof(LocalParticlePool), nameof(AddType));
                return;
            }

            var config = new ParticlePoolConfig
            {
                particleType = particleType,
                prefab = prefab,
                prewarmCount = prewarmCount,
                maxPoolSize = maxPoolSize,
                defaultLifetime = defaultLifetime
            };

            RegisterPrefabInternal(config);
            MID_Logger.LogInfo(_logLevel, $"Runtime registered: {particleType} | Prewarm: {prewarmCount} | Max: {maxPoolSize}", nameof(LocalParticlePool), nameof(AddType));
        }

        /// <summary>Returns true if the type has been registered with the pool.</summary>
        public bool IsRegistered(PoolableParticleType particleType) => registeredTypes.Contains(particleType);

        /// <summary>Destroys all pooled particles and clears all registration data.</summary>
        public void ClearPool()
        {
            int totalDestroyed = 0;
            foreach (var particleType in registeredTypes)
            {
                var pool = pooledParticles[particleType];
                while (pool.Count > 0)
                {
                    GameObject obj = pool.Dequeue();
                    if (obj != null) { Destroy(obj); totalDestroyed++; }
                }
            }

            pooledParticles.Clear();
            typeConfigs.Clear();
            typePrefabs.Clear();
            registeredTypes.Clear();
            totalSpawnedCount.Clear();
            currentActiveCount.Clear();
            prefabToTypeMap.Clear();
            typeToPrefabMap.Clear();

            MID_Logger.LogInfo(_logLevel, $"Particle pool cleared — destroyed {totalDestroyed} objects.", nameof(LocalParticlePool), nameof(ClearPool));
        }

        #endregion

        #region Private Methods

        private bool ValidatePoolConfigurations()
        {
            bool isValid = true;
            var seenTypes = new HashSet<PoolableParticleType>();
            var prefabCheck = new Dictionary<GameObject, PoolableParticleType>();

            for (int i = 0; i < particleConfigs.Count; i++)
            {
                var config = particleConfigs[i];

                if (config.prefab == null)
                {
                    string w = $"Config at index {i} has NULL prefab for type {config.particleType}.";
                    configurationWarnings.Add(w);
                    MID_Logger.LogWarning(_logLevel, w, nameof(LocalParticlePool), nameof(ValidatePoolConfigurations));
                    continue;
                }

                if (!seenTypes.Add(config.particleType))
                {
                    string w = $"DUPLICATE TYPE: '{config.particleType}' appears more than once.";
                    configurationWarnings.Add(w);
                    MID_Logger.LogError(_logLevel, w, nameof(LocalParticlePool), nameof(ValidatePoolConfigurations));
                    isValid = false;
                }

                if (prefabCheck.ContainsKey(config.prefab))
                {
                    string w = $"SAME PREFAB MULTIPLE TYPES: '{config.prefab.name}' has types {prefabCheck[config.prefab]} and {config.particleType}.";
                    configurationWarnings.Add(w);
                    MID_Logger.LogError(_logLevel, w, nameof(LocalParticlePool), nameof(ValidatePoolConfigurations));
                    isValid = false;
                }
                else
                {
                    prefabCheck[config.prefab] = config.particleType;
                }
            }

            return isValid;
        }

        private void RegisterPrefabInternal(ParticlePoolConfig config)
        {
            if (config.prefab == null) return;

            registeredTypes.Add(config.particleType);
            typeConfigs[config.particleType] = config;
            typePrefabs[config.particleType] = config.prefab;
            pooledParticles[config.particleType] = new Queue<GameObject>(config.maxPoolSize);
            totalSpawnedCount[config.particleType] = 0;
            currentActiveCount[config.particleType] = 0;
            prefabToTypeMap[config.prefab] = config.particleType;
            typeToPrefabMap[config.particleType] = config.prefab;

            MID_Logger.LogDebug(_logLevel,
                $"Registered: {config.particleType} | Prefab: {config.prefab.name} | Prewarm: {config.prewarmCount} | Max: {config.maxPoolSize}",
                nameof(LocalParticlePool), nameof(RegisterPrefabInternal));

            for (int i = 0; i < config.prewarmCount; i++)
            {
                GameObject obj = CreatePooledInstance(config);
                ReturnObjectInternal(obj, config.particleType, false);
            }
        }

        private void ReturnObjectInternal(GameObject obj, PoolableParticleType particleType, bool decrementActive)
        {
            if (!registeredTypes.Contains(particleType))
            {
                MID_Logger.LogError(_logLevel, $"RETURN FAILED — type {particleType} not registered. Destroying.", nameof(LocalParticlePool), nameof(ReturnObjectInternal));
                if (decrementActive && currentActiveCount.ContainsKey(particleType))
                    currentActiveCount[particleType]--;
                Destroy(obj);
                return;
            }

            var pool = pooledParticles[particleType];
            var config = typeConfigs[particleType];

            if (pool.Count >= config.maxPoolSize)
            {
                MID_Logger.LogWarning(_logLevel, $"Pool at max capacity for {particleType} — destroying overflow.", nameof(LocalParticlePool), nameof(ReturnObjectInternal));
                if (decrementActive) currentActiveCount[particleType]--;
                Destroy(obj);
                return;
            }

            if (decrementActive && currentActiveCount.ContainsKey(particleType))
                currentActiveCount[particleType]--;

            ParticleSystem[] particleSystems = obj.GetComponentsInChildren<ParticleSystem>();
            foreach (var ps in particleSystems)
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ResetParticleState(obj);

            if (obj.transform.parent != transform)
                obj.transform.SetParent(transform);

            obj.SetActive(false);
            pool.Enqueue(obj);

            MID_Logger.LogDebug(_logLevel,
                $"Returned {particleType} | ID: {obj.GetInstanceID()} | Pool: {pool.Count}",
                nameof(LocalParticlePool), nameof(ReturnObjectInternal));
        }

        private GameObject CreatePooledInstance(ParticlePoolConfig config)
        {
            GameObject obj = Instantiate(config.prefab, transform);

            LocalParticleReturn returnComponent = obj.GetComponent<LocalParticleReturn>();
            if (returnComponent == null)
                returnComponent = obj.AddComponent<LocalParticleReturn>();

            returnComponent.SetOriginalType(config.particleType);
            returnComponent.SetMaxLifetime(config.defaultLifetime);

            MID_Logger.LogDebug(_logLevel, $"Created particle instance: {config.particleType} | ID: {obj.GetInstanceID()}", nameof(LocalParticlePool), nameof(CreatePooledInstance));
            return obj;
        }

        private void ResetParticleState(GameObject obj)
        {
            obj.transform.position = Vector3.zero;
            obj.transform.rotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;

            ParticleSystem[] particleSystems = obj.GetComponentsInChildren<ParticleSystem>();
            foreach (var ps in particleSystems)
            {
                ps.Clear();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        private GameObject FindPrefabForType(PoolableParticleType particleType)
        {
            string typeName = particleType.ToString();
            foreach (var config in particleConfigs)
            {
                if (config.prefab != null && config.prefab.name.Contains(typeName))
                    return config.prefab;
            }
            return null;
        }

        private void UpdatePoolStatistics()
        {
            poolStatistics.Clear();
            totalPooledParticles = 0;
            totalActiveParticles = 0;
            childrenCount = transform.childCount;

            foreach (var particleType in registeredTypes)
            {
                var config = typeConfigs[particleType];
                int available = pooledParticles[particleType].Count;
                int spawned = totalSpawnedCount.GetValueOrDefault(particleType, 0);
                int active = currentActiveCount.GetValueOrDefault(particleType, 0);

                totalPooledParticles += available;
                totalActiveParticles += active;

                poolStatistics.Add(new ParticlePoolStats(config.prefab.name, spawned, active, available, config.maxPoolSize));
            }
        }

        #endregion
    }
}