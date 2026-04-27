// HybridNetworkSingleton.cs
// Like NetworkSingleton but instance is available immediately in Awake —
// before any network spawn occurs. Network features layer on top when spawned.
// Persists across scenes by default.

using UnityEngine;
using Unity.Netcode;
using UnityEngine.SceneManagement;

namespace MidManStudio.Core.Netcode
{
    /// <summary>
    /// Hybrid singleton: instance is ready in Awake, network features activate
    /// on OnNetworkSpawn. Use when you need the component available both in
    /// online and offline contexts.
    /// </summary>
    public class HybridNetworkSingleton<T> : NetworkBehaviour where T : Component
    {
        private static T    _instance;
        private static bool _persistAcrossScenes    = true;
        private static bool _persistenceInitialized;
        private static bool _isNetworkSpawned;
        private static bool _isNetworkReady;

        // ── Public properties ─────────────────────────────────────────────────

        public static bool HasInstance  => _instance != null && _instance;
        public static T    TryGetInstance() => HasInstance ? _instance : null;
        public static T    Current      => _instance;

        // Events
        public delegate void NetworkStateHandler(bool isSpawned);
        public static event  NetworkStateHandler OnNetworkStateChanged;

        public delegate void NetworkSceneHandler(string previous, string current);
        public static event  NetworkSceneHandler OnNetworkSceneChanged;

        // ── Instance accessor ─────────────────────────────────────────────────

        /// <summary>
        /// Always returns an instance. Creates one if needed — even before network spawn.
        /// </summary>
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
                Debug.Log(
                    $"[HybridNetworkSingleton] {typeof(T).Name} initialized in Awake.");
            }
            else if (_instance != this)
            {
                Debug.LogWarning(
                    $"[HybridNetworkSingleton] Duplicate {typeof(T).Name} — destroying.");
                Destroy(gameObject);
                return;
            }

            if (_persistAcrossScenes && !_persistenceInitialized)
                ApplyPersistence();
        }

        // ── NGO lifecycle ─────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _isNetworkSpawned = true;
            _isNetworkReady   = true;

            Debug.Log($"[HybridNetworkSingleton] {typeof(T).Name} spawned — " +
                      $"server={IsServer} host={IsHost} client={IsClient}");

            FireSafe(() => OnNetworkStateChanged?.Invoke(true));
            FireSafe(() =>
                (_instance as IHybridNetworkSingletonLifecycle)
                    ?.OnNetworkSpawned(IsServer, IsHost, IsClient, IsOwner));
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            _isNetworkSpawned = false;
            _isNetworkReady   = false;

            Debug.Log($"[HybridNetworkSingleton] {typeof(T).Name} despawned.");

            FireSafe(() => OnNetworkStateChanged?.Invoke(false));
            FireSafe(() =>
                (_instance as IHybridNetworkSingletonLifecycle)?.OnNetworkDespawned());
        }

        public override void OnDestroy()
        {
            if (_instance == this)
            {
                _instance              = null;
                _persistenceInitialized = false;
                _isNetworkSpawned      = false;
                _isNetworkReady        = false;
                UnsubscribeAll();
                Debug.Log(
                    $"[HybridNetworkSingleton] {typeof(T).Name} destroyed.");
            }
            base.OnDestroy();
        }

        // ── Protected helpers ─────────────────────────────────────────────────

        protected virtual void InitializeSingleton(bool persistAcrossScenes = true)
        {
            if (!Application.isPlaying)
            {
                Debug.LogError(
                    "[HybridNetworkSingleton] Cannot initialize outside play mode.");
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

            if (persistAcrossScenes && !_persistenceInitialized)
                ApplyPersistence();
        }

        protected virtual void OnApplicationQuit()
        {
            if (_instance != this) return;
            _instance              = null;
            _persistenceInitialized = false;
            _isNetworkSpawned      = false;
            _isNetworkReady        = false;
        }

        // ── Static helpers ────────────────────────────────────────────────────

        /// <summary>Instance can be used for non-network operations.</summary>
        public static bool IsAvailable()
        {
            try { return _instance != null && _instance; }
            catch { return false; }
        }

        /// <summary>Instance is spawned AND network is listening.</summary>
        public static bool IsNetworkReady()
        {
            try
            {
                return _isNetworkReady &&
                       _instance != null && _instance &&
                       NetworkManager.Singleton != null &&
                       NetworkManager.Singleton.IsListening;
            }
            catch { return false; }
        }

        public static bool IsNetworkSpawned()
        {
            try { return _isNetworkSpawned && _instance != null && _instance; }
            catch { return false; }
        }

        public static bool IsServerAuthority()
        {
            if (!IsNetworkReady()) return false;
            try
            {
                var no = _instance.GetComponent<NetworkObject>();
                return no != null && no.IsOwnedByServer;
            }
            catch { return false; }
        }

        public static T GetExistingInstance()
        {
            if (HasInstance) return _instance;
            var found = Object.FindObjectsByType<T>(FindObjectsSortMode.None);
            foreach (var o in found)
                if (o != null && o) return o;
            return null;
        }

        public static void Reset()
        {
            UnsubscribeAll();
            if (_instance != null && _instance && _instance.gameObject != null)
                Object.Destroy(_instance.gameObject);
            _instance              = null;
            _persistenceInitialized = false;
            _isNetworkSpawned      = false;
            _isNetworkReady        = false;
            Debug.Log($"[HybridNetworkSingleton] {typeof(T).Name} reset.");
        }

        // ── Private ───────────────────────────────────────────────────────────

        private static void FindOrCreateInstance()
        {
            var found = Object.FindObjectsByType<T>(FindObjectsSortMode.None);
            if (found.Length > 0)
            {
                _instance = found[0];
                if (found.Length > 1)
                {
                    Debug.LogWarning(
                        $"[HybridNetworkSingleton] Multiple {typeof(T).Name} — keeping first.");
                    for (int i = 1; i < found.Length; i++)
                        if (found[i].gameObject != null)
                            Object.Destroy(found[i].gameObject);
                }
                Debug.Log(
                    $"[HybridNetworkSingleton] Found existing {typeof(T).Name}.");
            }
            else if (Application.isPlaying)
            {
                var go  = new GameObject($"_{typeof(T).Name}");
                _instance = go.AddComponent<T>();
                var no  = go.GetComponent<NetworkObject>() ?? go.AddComponent<NetworkObject>();
                no.DontDestroyWithOwner = true;
                Debug.Log($"[HybridNetworkSingleton] Created {typeof(T).Name}.");
            }

            if (_instance != null && _persistAcrossScenes && !_persistenceInitialized)
                ApplyPersistence();
        }

        private static void ApplyPersistence()
        {
            if (_instance == null || _persistenceInitialized) return;
            try
            {
                DontDestroyOnLoad(_instance.gameObject);
                _persistenceInitialized = true;
                Debug.Log(
                    $"[HybridNetworkSingleton] {typeof(T).Name} persists across scenes.");

                if (NetworkManager.Singleton?.SceneManager != null)
                    NetworkManager.Singleton.SceneManager.OnSceneEvent += OnNetworkSceneEvent;
                else
                    SceneManager.sceneLoaded += OnRegularSceneLoaded;
            }
            catch (System.Exception e)
            {
                Debug.LogError(
                    $"[HybridNetworkSingleton] Persistence error: {e.Message}");
            }
        }

        private static void UnsubscribeAll()
        {
            try
            {
                if (NetworkManager.Singleton?.SceneManager != null)
                    NetworkManager.Singleton.SceneManager.OnSceneEvent -= OnNetworkSceneEvent;
            }
            catch { }
            try { SceneManager.sceneLoaded -= OnRegularSceneLoaded; }
            catch { }
        }

        private static void OnNetworkSceneEvent(SceneEvent ev)
        {
            if (ev.SceneEventType != SceneEventType.LoadComplete) return;
            string prev = SceneManager.GetActiveScene().name;
            string curr = ev.SceneName;

            FireSafe(() => OnNetworkSceneChanged?.Invoke(prev, curr));
            FireSafe(() =>
                (_instance as IHybridNetworkSingletonLifecycle)
                    ?.OnNetworkSceneChange(prev, curr));
        }

        private static void OnRegularSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            FireSafe(() =>
                (_instance as IHybridNetworkSingletonLifecycle)
                    ?.OnSceneChange(scene.name));
        }

        private static void FireSafe(System.Action action)
        {
            try { action?.Invoke(); }
            catch (System.Exception e)
            {
                Debug.LogError($"[HybridNetworkSingleton] Event error: {e.Message}");
            }
        }
    }

    // ── Lifecycle interface ───────────────────────────────────────────────────

    public interface IHybridNetworkSingletonLifecycle
    {
        void OnNetworkSceneChange(string previousScene, string currentScene);
        void OnSceneChange(string sceneName);
        void OnNetworkSpawned(bool isServer, bool isHost, bool isClient, bool isOwner);
        void OnNetworkDespawned();
    }
}
