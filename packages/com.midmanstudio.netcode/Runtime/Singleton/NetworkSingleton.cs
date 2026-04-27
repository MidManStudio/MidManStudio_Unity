// NetworkSingleton.cs
// NGO-aware singleton base class.
// Instance is available after Awake but network features only activate post-spawn.
// Requires Unity Netcode for GameObjects (com.unity.netcode.gameobjects).

using UnityEngine;
using Unity.Netcode;
using UnityEngine.SceneManagement;

namespace MidManStudio.Core.Netcode
{
    /// <summary>
    /// Singleton that inherits from NetworkBehaviour.
    /// Instance is set in Awake. Network RPCs / ownership are only valid after
    /// OnNetworkSpawn fires.
    /// </summary>
    public class NetworkSingleton<T> : NetworkBehaviour where T : Component
    {
        private static T    _instance;
        private static bool _persistAcrossScenes;
        private static bool _persistenceInitialized;
        private static bool _isSpawned;

        // ── Public properties ─────────────────────────────────────────────────

        public static bool HasInstance            => _instance != null && _instance;
        public static T    TryGetInstance()       => HasInstance ? _instance : null;
        public static T    Current                => _instance;
        public static bool IsNetworkSpawnedState  => _isSpawned;

        // Events
        public delegate void NetworkSceneChangeHandler(string previous, string current);
        public static event  NetworkSceneChangeHandler OnNetworkSceneChanged;

        // ── Instance accessor ─────────────────────────────────────────────────

        public static T Instance
        {
            get
            {
                if (_instance == null || !_instance)
                    FindOrCreateInstance();
                return _instance;
            }
        }

        // ── Unity lifecycle ───────────────────────────────────────────────────

        protected virtual void Awake()
        {
            if (_instance == null || !_instance)
            {
                _instance = this as T;
                Debug.Log($"[NetworkSingleton] {typeof(T).Name} initialized.");
            }
            else if (_instance != this)
            {
                Debug.LogWarning(
                    $"[NetworkSingleton] Duplicate {typeof(T).Name} — destroying.");
                Destroy(gameObject);
            }
        }

        // ── NGO lifecycle ─────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _isSpawned = true;

            if (_persistAcrossScenes && !_persistenceInitialized)
                ApplyPersistence();

            try
            {
                (_instance as INetworkSingletonLifecycle)
                    ?.OnNetworkSpawned(IsServer, IsHost, IsClient, IsOwner);
            }
            catch (System.Exception e)
            {
                Debug.LogError(
                    $"[NetworkSingleton] OnNetworkSpawned lifecycle error: {e.Message}");
            }

            Debug.Log($"[NetworkSingleton] {typeof(T).Name} spawned — " +
                      $"server={IsServer} host={IsHost} client={IsClient}");
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            _isSpawned = false;

            try
            {
                (_instance as INetworkSingletonLifecycle)?.OnNetworkDespawned();
            }
            catch (System.Exception e)
            {
                Debug.LogError(
                    $"[NetworkSingleton] OnNetworkDespawn lifecycle error: {e.Message}");
            }
        }

        public override void OnDestroy()
        {
            if (_instance == this)
            {
                _instance              = null;
                _persistenceInitialized = false;
                _isSpawned             = false;
                UnsubscribeSceneEvents();
                Debug.Log($"[NetworkSingleton] {typeof(T).Name} destroyed.");
            }
            base.OnDestroy();
        }

        // ── Protected helpers ─────────────────────────────────────────────────

        /// <summary>Call from Awake or InitializeSingleton to opt into persistence.</summary>
        protected virtual void Remake(bool persistAcrossScenes = false)
        {
            _persistAcrossScenes = persistAcrossScenes;
            if (_isSpawned && persistAcrossScenes && !_persistenceInitialized)
                ApplyPersistence();
        }

        protected virtual void InitializeSingleton(bool persistAcrossScenes = false)
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("[NetworkSingleton] Cannot initialize outside play mode.");
                return;
            }

            _persistAcrossScenes = persistAcrossScenes;

            if (_instance == null || !_instance)
                _instance = this as T;
            else if (_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            if (persistAcrossScenes && !_persistenceInitialized && _isSpawned)
                ApplyPersistence();
        }

        protected virtual void OnApplicationQuit()
        {
            if (_instance != this) return;
            _instance              = null;
            _persistenceInitialized = false;
            _isSpawned             = false;
        }

        // ── Static helpers ────────────────────────────────────────────────────

        public static void Reset()
        {
            UnsubscribeSceneEvents();
            if (_instance != null && _instance && _instance.gameObject != null)
                Destroy(_instance.gameObject);

            _instance              = null;
            _persistenceInitialized = false;
            _isSpawned             = false;
            Debug.Log($"[NetworkSingleton] {typeof(T).Name} reset.");
        }

        public static bool IsServerAuthority()
        {
            if (_instance == null || !_isSpawned) return false;
            var no = _instance.GetComponent<NetworkObject>();
            return no != null && no.IsOwnedByServer;
        }

        public static bool IsNetworkActive()
        {
            try
            {
                return _isSpawned &&
                       _instance != null && _instance &&
                       NetworkManager.Singleton != null &&
                       NetworkManager.Singleton.IsListening;
            }
            catch { return false; }
        }

        // ── Private ───────────────────────────────────────────────────────────

        private static void FindOrCreateInstance()
        {
            var found = FindObjectsByType<T>(FindObjectsSortMode.None);
            if (found.Length > 0)
            {
                _instance = found[0];
                if (found.Length > 1)
                {
                    Debug.LogWarning(
                        $"[NetworkSingleton] Multiple {typeof(T).Name} found — keeping first.");
                    for (int i = 1; i < found.Length; i++)
                        if (found[i].gameObject != null)
                            Destroy(found[i].gameObject);
                }
            }
            else if (Application.isPlaying)
            {
                var go = new GameObject($"_{typeof(T).Name}");
                _instance = go.AddComponent<T>();
                if (go.GetComponent<NetworkObject>() == null)
                    go.AddComponent<NetworkObject>();
                Debug.Log($"[NetworkSingleton] Created {typeof(T).Name}.");
            }
        }

        private static void ApplyPersistence()
        {
            if (_instance == null || _persistenceInitialized) return;
            try
            {
                DontDestroyOnLoad(_instance.gameObject);
                _persistenceInitialized = true;
                Debug.Log($"[NetworkSingleton] {typeof(T).Name} persists across scenes.");

                if (NetworkManager.Singleton?.SceneManager != null)
                    NetworkManager.Singleton.SceneManager.OnSceneEvent += OnNetworkSceneEvent;
            }
            catch (System.Exception e)
            {
                Debug.LogError(
                    $"[NetworkSingleton] Persistence error: {e.Message}");
            }
        }

        private static void UnsubscribeSceneEvents()
        {
            try
            {
                if (NetworkManager.Singleton?.SceneManager != null)
                    NetworkManager.Singleton.SceneManager.OnSceneEvent -= OnNetworkSceneEvent;
            }
            catch { }
        }

        private static void OnNetworkSceneEvent(SceneEvent ev)
        {
            if (ev.SceneEventType != SceneEventType.LoadComplete) return;
            string prev = SceneManager.GetActiveScene().name;
            string curr = ev.SceneName;

            try { OnNetworkSceneChanged?.Invoke(prev, curr); }
            catch (System.Exception e)
            {
                Debug.LogError($"[NetworkSingleton] Scene event error: {e.Message}");
            }

            try
            {
                (_instance as INetworkSingletonLifecycle)?.OnNetworkSceneChange(prev, curr);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[NetworkSingleton] Scene lifecycle error: {e.Message}");
            }
        }
    }

    // ── Lifecycle interface ───────────────────────────────────────────────────

    public interface INetworkSingletonLifecycle
    {
        void OnNetworkSceneChange(string previousScene, string currentScene);
        void OnNetworkSpawned(bool isServer, bool isHost, bool isClient, bool isOwner);
        void OnNetworkDespawned();
    }
}
