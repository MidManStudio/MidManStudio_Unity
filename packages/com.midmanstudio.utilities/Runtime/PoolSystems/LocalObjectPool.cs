using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Utilities;

namespace MidManStudio.Core.Pools
{
    // =============================================================================
    // PoolableObjectType — ForeignProtocol
    // =============================================================================
    [System.Serializable]
    public enum PoolableObjectType
    {
        // ===== FLIGHT PLAN - PROJECTILES (200-209) =====
        FP_Bullet = 200,
        FP_Rocket = 201,
        FP_WaveBullet = 202,

        // ===== FLIGHT PLAN - OBSTACLES (210) =====
        FP_Obstacle = 210,

        // ===== FLIGHT PLAN - POWERUPS (220-229) =====
        FP_PowerUp_Shield = 220,
        FP_PowerUp_SlowTime = 221,
        FP_PowerUp_Magnet = 222,
        FP_PowerUp_Rockets = 223,
        FP_PowerUp_Laser = 224,
        FP_PowerUp_Invulnerability = 225,
        FP_PowerUp_MultiShot = 226,   
        FP_PowerUp_WaveShot = 227,
        // ===== FLIGHT PLAN - COLLECTIBLES (230-239) =====
        FP_Coin = 230,

        // ===== FLIGHT PLAN - AUDIO (300-301) =====
        SpawnableAudio = 300,

        // ===== FLIGHT PLAN - BOSS (400-404) =====
        FP_Boss = 400,
        FP_BossLaser = 401,
        FP_BossHomingShot = 402,
        FP_BossBullet = 403,
        FP_BossPulsingBullet = 404,

        // ===== FLIGHT PLAN - ENEMIES (500-509) =====
        FP_Enemy_LaserStrafe = 500,
        FP_Enemy_Kamikaze = 501,
        FP_Enemy_Shooter = 502,
        FP_EnemyBullet = 503,
    }

    [System.Serializable]
    public class BasicPoolConfig : IArrayElementTitle
    {
        public PoolableObjectType objectType;
        public GameObject prefab;
        public int prewarmCount = 10;
        public int maxPoolSize = 20;

        public string Name => GetPropperName();
        private string GetPropperName()
        {
            if (prefab != null) return prefab.name;
            return objectType.ToString();
        }
    }

    [System.Serializable]
    public class PoolStats
    {
        public string prefabName;
        public int totalSpawned;
        public int currentlyActive;
        public int availableInPool;
        public int maxPoolSize;

        public PoolStats(string name, int spawned, int active, int available, int max)
        {
            prefabName = name;
            totalSpawned = spawned;
            currentlyActive = active;
            availableInPool = available;
            this.maxPoolSize = max;
        }
    }

    /// <summary>
    /// LocalObjectPool — Singleton pool manager for non-particle GameObjects.
    /// </summary>
    public class LocalObjectPool : Singleton<LocalObjectPool>
    {
        #region Serialized Fields

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Pool Configuration")]
        [MID_NamedList]
        [SerializeField] private List<BasicPoolConfig> poolConfigs = new List<BasicPoolConfig>();

        [Header("Auto-Registration Settings")]
        [SerializeField] private int autoRegisterPrewarmCount = 10;
        [SerializeField] private int autoRegisterMaxPoolSize = 20;
        [SerializeField] private bool enableAutoRegistration = true;

        [Header("Pool Monitoring (Read Only)")]
        [SerializeField] private int totalPooledObjects;
        [SerializeField] private int totalActiveObjects;
        [SerializeField] private int childrenCount;
        [SerializeField] private List<PoolStats> poolStatistics = new List<PoolStats>();

        #endregion

        #region Private Fields

        private bool _hasBeenInitialized = false;

        private Dictionary<PoolableObjectType, Queue<GameObject>> pooledObjects = new Dictionary<PoolableObjectType, Queue<GameObject>>();
        private Dictionary<PoolableObjectType, BasicPoolConfig> typeConfigs = new Dictionary<PoolableObjectType, BasicPoolConfig>();
        private Dictionary<PoolableObjectType, GameObject> typePrefabs = new Dictionary<PoolableObjectType, GameObject>();
        private HashSet<PoolableObjectType> registeredTypes = new HashSet<PoolableObjectType>();
        private Dictionary<PoolableObjectType, int> totalSpawnedCount = new Dictionary<PoolableObjectType, int>();
        private Dictionary<PoolableObjectType, int> currentActiveCount = new Dictionary<PoolableObjectType, int>();
        private Dictionary<GameObject, PoolableObjectType> prefabToTypeMap = new Dictionary<GameObject, PoolableObjectType>();

        #endregion

        #region Properties

        public bool HasBeenInitialized() => _hasBeenInitialized;

        #endregion

        #region Unity Lifecycle

        private void Update()
        {
            if (_hasBeenInitialized) UpdatePoolStatistics();
        }

        #endregion

        #region Public Methods

        public void CallInitializePool()
        {
            if (_hasBeenInitialized) return;

            MID_Logger.LogInfo(_logLevel, "Starting pool initialization.",
                nameof(LocalObjectPool), nameof(CallInitializePool));

            if (!ValidatePoolConfigurations())
            {
                MID_Logger.LogError(_logLevel, "Pool validation failed — aborting initialization.",
                    nameof(LocalObjectPool), nameof(CallInitializePool));
                return;
            }

            foreach (var config in poolConfigs)
                RegisterPrefabInternal(config);

            _hasBeenInitialized = true;

            MID_Logger.LogInfo(_logLevel, $"Pool initialized with {poolConfigs.Count} types.",
                nameof(LocalObjectPool), nameof(CallInitializePool));

            if (!LocalParticlePool.Instance.HasBeenInitialized())
                LocalParticlePool.Instance.CallInitializePool();
        }

        public GameObject GetObject(PoolableObjectType objectType, Vector3 position, Quaternion rotation)
        {
            if (!registeredTypes.Contains(objectType))
            {
                if (enableAutoRegistration)
                {
                    GameObject foundPrefab = FindPrefabForType(objectType);
                    if (foundPrefab != null)
                    {
                        MID_Logger.LogWarning(_logLevel,
                            $"Auto-registering {objectType} with prefab {foundPrefab.name}.",
                            nameof(LocalObjectPool), nameof(GetObject));
                        AddType(objectType, foundPrefab, autoRegisterPrewarmCount, autoRegisterMaxPoolSize);
                    }
                    else
                    {
                        MID_Logger.LogError(_logLevel,
                            $"Type {objectType} not registered and no suitable prefab found.",
                            nameof(LocalObjectPool), nameof(GetObject));
                        return null;
                    }
                }
                else
                {
                    MID_Logger.LogError(_logLevel,
                        $"Type {objectType} is not registered.",
                        nameof(LocalObjectPool), nameof(GetObject));
                    return null;
                }
            }

            var pool = pooledObjects[objectType];
            GameObject obj;
            bool wasCreatedNew = false;

            if (pool.Count == 0)
            {
                obj = CreatePooledInstance(typeConfigs[objectType]);
                totalSpawnedCount[objectType]++;
                wasCreatedNew = true;
            }
            else
            {
                obj = pool.Dequeue();
            }

            currentActiveCount[objectType]++;
            obj.transform.SetParent(null);      // detach from pool root BEFORE activating
            obj.transform.position = position;
            obj.transform.rotation = rotation;
            obj.SetActive(true);                // OnEnable fires as a clean scene-root object

            MID_Logger.LogDebug(_logLevel,
                $"Retrieved {objectType} | ID:{obj.GetInstanceID()} | New:{wasCreatedNew} | Active:{currentActiveCount[objectType]} | Pool:{pool.Count}",
                nameof(LocalObjectPool), nameof(GetObject));

            return obj;
        }

        public GameObject GetObject(PoolableObjectType objectType, Vector2 position, Quaternion rotation)
            => GetObject(objectType, new Vector3(position.x, position.y, 0), rotation);

        public void ReturnObject(GameObject obj, PoolableObjectType objectType)
        {
            MID_Logger.LogDebug(_logLevel,
                $"Return request: {objectType} | ID:{obj.GetInstanceID()}",
                nameof(LocalObjectPool), nameof(ReturnObject));
            ReturnObjectInternal(obj, objectType, true);
        }

        /// <summary>
        /// Returns every currently active pooled object back to its pool.
        /// Called by FP_GameManager.RestartGame() instead of reloading the scene.
        /// FindObjectsOfType (no includeInactive param) only returns active-in-hierarchy
        /// objects — pool-resident inactive objects are automatically excluded.
        /// </summary>
        public void ReturnAllActive()
        {
            var allPoolReturns = FindObjectsOfType<LocalPoolReturn>();
            int returned = 0;
            foreach (var lr in allPoolReturns)
            {
                if (lr == null) continue;
                lr.ReturnToPoolNow();
                returned++;
            }
            MID_Logger.LogInfo(_logLevel,
                $"ReturnAllActive — returned {returned} active objects to pool.",
                nameof(LocalObjectPool), nameof(ReturnAllActive));
        }

        public void AddType(PoolableObjectType objectType, GameObject prefab,
                            int prewarmCount = 5, int maxPoolSize = 15)
        {
            if (registeredTypes.Contains(objectType))
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Type {objectType} is already registered.",
                    nameof(LocalObjectPool), nameof(AddType));
                return;
            }

            if (prefabToTypeMap.ContainsKey(prefab))
            {
                MID_Logger.LogError(_logLevel,
                    $"Prefab '{prefab.name}' is already registered as {prefabToTypeMap[prefab]}. Cannot register as {objectType}.",
                    nameof(LocalObjectPool), nameof(AddType));
                return;
            }

            var config = new BasicPoolConfig
            {
                objectType = objectType,
                prefab = prefab,
                prewarmCount = prewarmCount,
                maxPoolSize = maxPoolSize
            };

            RegisterPrefabInternal(config);
            MID_Logger.LogInfo(_logLevel,
                $"Runtime registered: {objectType} | Prewarm:{prewarmCount} | Max:{maxPoolSize}",
                nameof(LocalObjectPool), nameof(AddType));
        }

        public bool IsRegistered(PoolableObjectType objectType) => registeredTypes.Contains(objectType);

        public void ClearPool()
        {
            int totalDestroyed = 0;
            foreach (var objectType in registeredTypes)
            {
                var pool = pooledObjects[objectType];
                while (pool.Count > 0)
                {
                    GameObject obj = pool.Dequeue();
                    if (obj != null) { Destroy(obj); totalDestroyed++; }
                }
            }

            pooledObjects.Clear();
            typeConfigs.Clear();
            typePrefabs.Clear();
            registeredTypes.Clear();
            totalSpawnedCount.Clear();
            currentActiveCount.Clear();
            prefabToTypeMap.Clear();

            MID_Logger.LogInfo(_logLevel,
                $"Pool cleared — destroyed {totalDestroyed} objects.",
                nameof(LocalObjectPool), nameof(ClearPool));
        }

        #endregion

        #region Private Methods

        private bool ValidatePoolConfigurations()
        {
            var seenTypes = new HashSet<PoolableObjectType>();
            var prefabCheck = new Dictionary<GameObject, PoolableObjectType>();

            for (int i = 0; i < poolConfigs.Count; i++)
            {
                var config = poolConfigs[i];

                if (config.prefab == null)
                {
                    MID_Logger.LogWarning(_logLevel,
                        $"Config at index {i} has null prefab.",
                        nameof(LocalObjectPool), nameof(ValidatePoolConfigurations));
                    continue;
                }

                if (seenTypes.Contains(config.objectType))
                {
                    MID_Logger.LogError(_logLevel,
                        $"DUPLICATE TYPE: '{config.objectType}' assigned to multiple prefabs.",
                        nameof(LocalObjectPool), nameof(ValidatePoolConfigurations));
                    return false;
                }

                if (prefabCheck.ContainsKey(config.prefab))
                {
                    MID_Logger.LogError(_logLevel,
                        $"SAME PREFAB MULTIPLE TYPES: '{config.prefab.name}' has types {prefabCheck[config.prefab]} and {config.objectType}.",
                        nameof(LocalObjectPool), nameof(ValidatePoolConfigurations));
                    return false;
                }

                if (config.prewarmCount < 0)
                {
                    MID_Logger.LogWarning(_logLevel,
                        $"Negative prewarm on {config.prefab.name} — clamping to 0.",
                        nameof(LocalObjectPool), nameof(ValidatePoolConfigurations));
                    config.prewarmCount = 0;
                }

                seenTypes.Add(config.objectType);
                prefabCheck[config.prefab] = config.objectType;
            }

            return true;
        }

        private void RegisterPrefabInternal(BasicPoolConfig config)
        {
            if (config.prefab == null) return;

            registeredTypes.Add(config.objectType);
            typeConfigs[config.objectType] = config;
            typePrefabs[config.objectType] = config.prefab;
            pooledObjects[config.objectType] = new Queue<GameObject>(config.maxPoolSize);
            totalSpawnedCount[config.objectType] = 0;
            currentActiveCount[config.objectType] = 0;
            prefabToTypeMap[config.prefab] = config.objectType;

            MID_Logger.LogDebug(_logLevel,
                $"Registered: {config.objectType} | Prefab:{config.prefab.name} | Prewarm:{config.prewarmCount} | Max:{config.maxPoolSize}",
                nameof(LocalObjectPool), nameof(RegisterPrefabInternal));

            for (int i = 0; i < config.prewarmCount; i++)
            {
                GameObject obj = CreatePooledInstance(config);
                ReturnObjectInternal(obj, config.objectType, false);
            }
        }

        private void ReturnObjectInternal(GameObject obj, PoolableObjectType objectType, bool decrementActive)
        {
            if (!registeredTypes.Contains(objectType))
            {
                if (enableAutoRegistration && decrementActive)
                {
                    GameObject foundPrefab = FindPrefabForType(objectType);
                    if (foundPrefab != null)
                    {
                        MID_Logger.LogWarning(_logLevel,
                            $"Auto-registering {objectType} during return.",
                            nameof(LocalObjectPool), nameof(ReturnObjectInternal));
                        AddType(objectType, foundPrefab, autoRegisterPrewarmCount, autoRegisterMaxPoolSize);
                    }
                    else
                    {
                        MID_Logger.LogError(_logLevel,
                            $"RETURN FAILED — type {objectType} not registered, no prefab found. Destroying.",
                            nameof(LocalObjectPool), nameof(ReturnObjectInternal));
                        Destroy(obj);
                        return;
                    }
                }
                else
                {
                    MID_Logger.LogError(_logLevel,
                        $"RETURN FAILED — type {objectType} not registered. Destroying.",
                        nameof(LocalObjectPool), nameof(ReturnObjectInternal));
                    if (decrementActive && currentActiveCount.ContainsKey(objectType))
                        currentActiveCount[objectType]--;
                    Destroy(obj);
                    return;
                }
            }

            var pool = pooledObjects[objectType];
            var config = typeConfigs[objectType];

            if (pool.Count >= config.maxPoolSize)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Pool at max capacity for {objectType} — destroying overflow.",
                    nameof(LocalObjectPool), nameof(ReturnObjectInternal));
                if (decrementActive) currentActiveCount[objectType]--;
                Destroy(obj);
                return;
            }

            if (decrementActive && currentActiveCount.ContainsKey(objectType))
                currentActiveCount[objectType]--;

            ResetObjectState(obj);

            if (obj.transform.parent != transform)
                obj.transform.SetParent(transform);

            obj.SetActive(false);
            pool.Enqueue(obj);

            MID_Logger.LogDebug(_logLevel,
                $"Returned {objectType} | ID:{obj.GetInstanceID()} | Pool:{pool.Count}",
                nameof(LocalObjectPool), nameof(ReturnObjectInternal));
        }

        private GameObject CreatePooledInstance(BasicPoolConfig config)
        {
            GameObject obj = Instantiate(config.prefab, transform);

            LocalPoolReturn returnComponent = obj.GetComponent<LocalPoolReturn>();
            if (returnComponent == null)
                returnComponent = obj.AddComponent<LocalPoolReturn>();

            returnComponent.SetOriginalType(config.objectType);

            MID_Logger.LogDebug(_logLevel,
                $"Created instance: {config.objectType} | ID:{obj.GetInstanceID()}",
                nameof(LocalObjectPool), nameof(CreatePooledInstance));

            return obj;
        }

        private void ResetObjectState(GameObject obj)
        {
            obj.transform.position = Vector3.zero;
            obj.transform.rotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;

            Rigidbody2D rb2d = obj.GetComponent<Rigidbody2D>();
            if (rb2d != null) { rb2d.velocity = Vector2.zero; rb2d.angularVelocity = 0f; }

            TrailRenderer[] trails = obj.GetComponentsInChildren<TrailRenderer>();
            foreach (var trail in trails)
                if (trail != null) trail.Clear();
        }

        private GameObject FindPrefabForType(PoolableObjectType objectType)
        {
            string typeName = objectType.ToString();
            foreach (var config in poolConfigs)
                if (config.prefab != null && config.prefab.name.Contains(typeName))
                    return config.prefab;
            return null;
        }

        private void UpdatePoolStatistics()
        {
            poolStatistics.Clear();
            totalPooledObjects = 0;
            totalActiveObjects = 0;
            childrenCount = transform.childCount;

            foreach (var objectType in registeredTypes)
            {
                var config = typeConfigs[objectType];
                int available = pooledObjects[objectType].Count;
                int spawned = totalSpawnedCount.GetValueOrDefault(objectType, 0);
                int active = currentActiveCount.GetValueOrDefault(objectType, 0);

                totalPooledObjects += available;
                totalActiveObjects += active;

                poolStatistics.Add(new PoolStats(config.prefab.name, spawned, active, available, config.maxPoolSize));
            }
        }

        #endregion
    }
}