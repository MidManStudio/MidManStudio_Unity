// MID_NetworkObjectPool.cs
// Generic NGO network object pool.
// Game-specific prepare/cleanup is handled via IPoolableNetworkObject on each prefab —
// the pool itself has zero game dependencies.
//
// SETUP:
//   1. Add this component to a persistent NetworkBehaviour in your scene.
//   2. Fill pooledPrefabsList in the inspector. Each prefab needs a NetworkObject
//      component. Add a component implementing IPoolableNetworkObject for cleanup hooks.
//   3. Call InitializePool() before any spawning (e.g. from your GameManager).
//
// USAGE (server-side only):
//   var netObj = MID_NetworkObjectPool.Singleton
//       .GetNetworkObject(PoolableNetworkObjectType.MyWeapon, pos, rot);
//   netObj.Spawn();
//   // ... later ...
//   MID_NetworkObjectPool.Singleton
//       .ReturnNetworkObject(netObj, PoolableNetworkObjectType.MyWeapon);

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.SceneManagement;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Netcode.Pools
{
    // ── Pool config ───────────────────────────────────────────────────────────

    [System.Serializable]
    public class NetworkPoolConfig : IArrayElementTitle
    {
        [Tooltip("Network pool type for this entry.")]
        public PoolableNetworkObjectType networkType;

        [Tooltip("Inspector label only.")]
        public string displayName;

        public GameObject prefab;
        public int prewarmCount = 5;

        public string Name =>
            !string.IsNullOrWhiteSpace(displayName) ? displayName :
            prefab != null ? prefab.name :
                                                       networkType.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────

    public class MID_NetworkObjectPool : NetworkBehaviour
    {
        private static MID_NetworkObjectPool _instance;
        public static MID_NetworkObjectPool Singleton => _instance;

        [Header("Pool Configuration")]
        [MID_NamedList]
        [SerializeField]
        private List<NetworkPoolConfig> pooledPrefabsList
            = new List<NetworkPoolConfig>();

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        private readonly HashSet<PoolableNetworkObjectType> _registeredTypes = new();
        private readonly Dictionary<PoolableNetworkObjectType, Queue<NetworkObject>> _pooledObjects = new();
        private readonly Dictionary<PoolableNetworkObjectType, GameObject> _typePrefabs = new();

        private Scene _targetScene;
        private bool _initialized;

        // ── Unity / NGO lifecycle ─────────────────────────────────────────────

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            _targetScene = gameObject.scene;
        }

        public override void OnNetworkDespawn()
        {
            ClearPool();
            base.OnNetworkDespawn();
        }

        public override void OnDestroy()
        {
            if (_instance == this) _instance = null;
            base.OnDestroy();
        }

        // ── Pool initialization ───────────────────────────────────────────────

        public void InitializePool()
        {
            if (_initialized) return;

            MID_Logger.LogInfo(_logLevel, "Initializing network pool.",
                nameof(MID_NetworkObjectPool));

            foreach (var config in pooledPrefabsList)
                RegisterInternal(config);

            _initialized = true;

            MID_Logger.LogInfo(_logLevel,
                $"Network pool ready — {pooledPrefabsList.Count} type(s).",
                nameof(MID_NetworkObjectPool));
        }

        private void RegisterInternal(NetworkPoolConfig config)
        {
            if (config.prefab == null) return;

            Assert.IsNotNull(
                config.prefab.GetComponent<NetworkObject>(),
                $"[MID_NetworkObjectPool] '{config.prefab.name}' has no NetworkObject.");

            _registeredTypes.Add(config.networkType);
            _typePrefabs[config.networkType] = config.prefab;
            _pooledObjects[config.networkType] = new Queue<NetworkObject>();

            NetworkManager.Singleton.PrefabHandler.AddHandler(
                config.prefab,
                new PooledPrefabHandler(config.networkType, this));

            for (int i = 0; i < config.prewarmCount; i++)
            {
                var obj = CreateInstance(config.prefab);
                var netObj = obj.GetComponent<NetworkObject>();
                ResetObject(netObj);
                EnqueueObject(netObj, config.networkType);
            }

            MID_Logger.LogDebug(_logLevel,
                $"Registered {config.networkType} prefab={config.prefab.name} " +
                $"prewarm={config.prewarmCount}",
                nameof(MID_NetworkObjectPool));
        }

        // ── Public API ────────────────────────────────────────────────────────

        public NetworkObject GetNetworkObject(PoolableNetworkObjectType type,
                                              Vector3 position, Quaternion rotation)
        {
            if (!_registeredTypes.Contains(type))
            {
                MID_Logger.LogError(_logLevel,
                    $"{type} not registered.",
                    nameof(MID_NetworkObjectPool), nameof(GetNetworkObject));
                return null;
            }

            var queue = _pooledObjects[type];
            NetworkObject netObj;

            if (queue.Count > 0)
            {
                netObj = queue.Dequeue();
            }
            else
            {
                netObj = CreateInstance(_typePrefabs[type]).GetComponent<NetworkObject>();
                ResetObject(netObj);
            }

            netObj.gameObject.SetActive(true);
            netObj.transform.position = position;
            netObj.transform.rotation = rotation;

            foreach (var poolable in netObj.GetComponents<IPoolableNetworkObject>())
            {
                try { poolable.OnPoolRetrieve(); }
                catch (System.Exception e)
                {
                    MID_Logger.LogError(_logLevel,
                        $"OnPoolRetrieve error on {netObj.name}: {e.Message}",
                        nameof(MID_NetworkObjectPool));
                }
            }

            MID_Logger.LogDebug(_logLevel,
                $"Get {type} id={netObj.NetworkObjectId} pool={queue.Count}",
                nameof(MID_NetworkObjectPool), nameof(GetNetworkObject));

            return netObj;
        }

        public NetworkObject GetNetworkObject(PoolableNetworkObjectType type)
            => GetNetworkObject(type, Vector3.zero, Quaternion.identity);

        public void ReturnNetworkObject(NetworkObject netObj, PoolableNetworkObjectType type)
        {
            if (netObj == null)
            {
                MID_Logger.LogError(_logLevel, "Attempted to return null NetworkObject.",
                    nameof(MID_NetworkObjectPool));
                return;
            }

            if (!_registeredTypes.Contains(type))
            {
                MID_Logger.LogError(_logLevel,
                    $"{type} not registered — destroying.",
                    nameof(MID_NetworkObjectPool));
                Destroy(netObj.gameObject);
                return;
            }

            ResetObject(netObj);
            EnqueueObject(netObj, type);

            MID_Logger.LogDebug(_logLevel,
                $"Returned {type} id={netObj.NetworkObjectId} pool={_pooledObjects[type].Count}",
                nameof(MID_NetworkObjectPool));
        }

        public bool IsRegistered(PoolableNetworkObjectType type)
            => _registeredTypes.Contains(type);

        // ── Pool management ───────────────────────────────────────────────────

        public void ClearPool()
        {
            foreach (var type in _registeredTypes)
            {
                if (NetworkManager.Singleton?.PrefabHandler != null &&
                    _typePrefabs.TryGetValue(type, out var prefab))
                    NetworkManager.Singleton.PrefabHandler.RemoveHandler(prefab);
            }

            _pooledObjects.Clear();
            _typePrefabs.Clear();
            _registeredTypes.Clear();
            _initialized = false;

            MID_Logger.LogInfo(_logLevel, "Network pool cleared.",
                nameof(MID_NetworkObjectPool));
        }

        // ── Private helpers ───────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private GameObject CreateInstance(GameObject prefab)
        {
            var obj = Instantiate(prefab);
            if (_targetScene.IsValid() && _targetScene.isLoaded)
                SceneManager.MoveGameObjectToScene(obj, _targetScene);
            return obj;
        }

        private static void ResetObject(NetworkObject netObj)
        {
            if (netObj == null) return;

            foreach (var poolable in netObj.GetComponents<IPoolableNetworkObject>())
            {
                try { poolable.OnPoolReset(); }
                catch (System.Exception e)
                {
                    Debug.LogError(
                        $"[MID_NetworkObjectPool] OnPoolReset error on {netObj.name}: {e.Message}");
                }
            }

            netObj.transform.SetParent(null);
            netObj.transform.position = Vector3.zero;
            netObj.transform.rotation = Quaternion.identity;
            netObj.transform.localScale = Vector3.one;
        }

        private void EnqueueObject(NetworkObject netObj, PoolableNetworkObjectType type)
        {
            netObj.gameObject.SetActive(false);
            _pooledObjects[type].Enqueue(netObj);
        }

        // ── NGO prefab handler ────────────────────────────────────────────────

        private class PooledPrefabHandler : INetworkPrefabInstanceHandler
        {
            private readonly PoolableNetworkObjectType _type;
            private readonly MID_NetworkObjectPool _pool;

            public PooledPrefabHandler(PoolableNetworkObjectType type, MID_NetworkObjectPool pool)
            {
                _type = type;
                _pool = pool;
            }

            NetworkObject INetworkPrefabInstanceHandler.Instantiate(
                ulong ownerClientId, Vector3 position, Quaternion rotation)
                => _pool.GetNetworkObject(_type, position, rotation);

            void INetworkPrefabInstanceHandler.Destroy(NetworkObject netObj)
                => _pool.ReturnNetworkObject(netObj, _type);
        }
    }
}