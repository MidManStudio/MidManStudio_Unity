using UnityEngine;
using UnityEngine.SceneManagement;


namespace MidManStudio.Core.Singleton
{
    /// <summary>
    /// Base singleton class, notice use always use HasInstance to check for existing instance
    /// if you notices object being dynamically created , means you are using .Instance without an existing instance in place
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class Singleton<T> : MonoBehaviour where T : Component
    {
        private static T _instance;

        // Public properties with proper null checks
        public static bool HasInstance => _instance != null && _instance;
        public static T TryGetInstance() => HasInstance ? _instance : null;
        public static T Current => _instance;

        // Persistence settings
        private static bool _persistAcrossScenes = false;
        private static bool _persistenceInitialized = false;

        // Scene management
        private static string _originSceneName;
        private static int _sceneLoadCount = 0;

        // Events
        public delegate void SceneChangeHandler(string previousScene, string currentScene);
        public static event SceneChangeHandler OnSceneChanged;

        // Instance accessor
        public static T Instance
        {
            get
            {
                // Check if instance is null or destroyed
                if (_instance == null || !_instance)
                {
                    var objs = FindObjectsByType(typeof(T), FindObjectsSortMode.None) as T[];
                    if (objs != null && objs.Length > 0)
                    {
                        // Find the first valid (non-destroyed) instance
                        foreach (var obj in objs)
                        {
                            if (obj != null && obj)
                            {
                                _instance = obj;
                                break;
                            }
                        }
                    }

                    // Clean up any destroyed duplicates
                    if (objs != null && objs.Length > 1)
                    {
                        Debug.LogWarning($"[Singleton] Multiple {typeof(T).Name} instances found. Keeping the first valid one and destroying the rest.");
                        bool foundValid = false;
                        for (int i = 0; i < objs.Length; i++)
                        {
                            if (objs[i] != null && objs[i])
                            {
                                if (!foundValid)
                                {
                                    foundValid = true;
                                    _instance = objs[i];
                                }
                                else
                                {
                                    if (objs[i].gameObject != null)
                                        Destroy(objs[i].gameObject);
                                }
                            }
                        }
                    }

                    // Create new instance if none found ok this why we get instance created when not found shiiiiiiii
                    if (_instance == null || !_instance)
                    {
                        if (Application.isPlaying)
                        {
                            GameObject obj = new GameObject();
                            obj.name = $"_{typeof(T).Name}";
                            _instance = obj.AddComponent<T>();
                            Debug.Log($"[Singleton] Created new instance of {typeof(T).Name}");
                        }
                        else
                        {
                            Debug.LogWarning($"[Singleton] Cannot create {typeof(T).Name} instance outside of play mode");
                            return null;
                        }
                    }

                    // Apply persistence settings
                    if (_instance != null && _instance && _persistAcrossScenes && !_persistenceInitialized)
                    {
                        ApplyPersistence();
                    }
                }

                return _instance;
            }
        }

        /// <summary>
        /// Apply persistence settings and subscribe to scene events
        /// </summary>
        private static void ApplyPersistence()
        {
            if (_instance != null && _instance && !_persistenceInitialized)
            {
                try
                {
                    DontDestroyOnLoad(_instance.gameObject);
                    _originSceneName = SceneManager.GetActiveScene().name;
                    _persistenceInitialized = true;
                    Debug.Log($"[Singleton] {typeof(T).Name} set to persist across scenes");

                    // Subscribe to scene change events
                    SceneManager.sceneLoaded += OnSceneLoaded;
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Singleton] Error applying persistence to {typeof(T).Name}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Scene change handler with proper null checks and error handling
        /// </summary>
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                string previousScene;

                // Safer way to get previous scene name
                if (_sceneLoadCount > 0 && SceneManager.sceneCount > 0)
                {
                    // Try to get the previous scene name, fallback to origin scene
                    previousScene = !string.IsNullOrEmpty(_originSceneName) ? _originSceneName : "Unknown";
                }
                else
                {
                    previousScene = !string.IsNullOrEmpty(_originSceneName) ? _originSceneName : "Unknown";
                }

                _sceneLoadCount++;

                // Invoke scene change event with error handling
                try
                {
                    OnSceneChanged?.Invoke(previousScene, scene.name);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Singleton] Error in OnSceneChanged event: {e.Message}");
                }

                // Invoke lifecycle methods when needed
                if (_instance != null && _instance)
                {
                    try
                    {
                        SingletonLifecycle lifecycle = _instance as SingletonLifecycle;
                        lifecycle?.OnSceneChange(previousScene, scene.name);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"[Singleton] Error in lifecycle OnSceneChange: {e.Message}");
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Singleton] Error in OnSceneLoaded: {e.Message}");
            }
        }

        /// <summary>
        /// Setup the singleton
        /// </summary>
        protected virtual void Awake()
        {
            InitializeSingleton(false);
        }

        /// <summary>
        /// Call this to remake the singleton
        /// </summary>
        protected virtual void Remake(bool persistAcrossScenes = false)
        {
            if (this == null) return;
            InitializeSingleton(persistAcrossScenes);
        }

        /// <summary>
        /// Initialize the singleton with persistence option
        /// </summary>
        protected virtual void InitializeSingleton(bool persistAcrossScenes)
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("[Singleton] Cannot initialize singleton outside of play mode");
                return;
            }

            if (this == null)
            {
                Debug.LogError("[Singleton] Cannot initialize null singleton instance");
                return;
            }

            _persistAcrossScenes = persistAcrossScenes;

            if (_instance == null || !_instance)
            {
                _instance = this as T;
                Debug.Log($"[Singleton] {typeof(T).Name} initialized");
            }
            else if (_instance != this)
            {
                Debug.LogWarning($"[Singleton] Another instance of {typeof(T).Name} already exists. Destroying this instance.");
                if (gameObject != null)
                    Destroy(gameObject);
                return;
            }

            if (persistAcrossScenes && !_persistenceInitialized)
            {
                ApplyPersistence();
            }
        }

        /// <summary>
        /// Cleanup when the singleton is destroyed
        /// </summary>
        protected virtual void OnDestroy()
        {
            // Clean up static references if this was the active instance
            if (_instance == this)
            {
                _instance = null;
                _persistenceInitialized = false;

                // Safely unsubscribe from scene events
                try
                {
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Singleton] Error unsubscribing from scene events: {e.Message}");
                }

                Debug.Log($"[Singleton] {typeof(T).Name} instance destroyed and cleaned up");
            }
        }

        /// <summary>
        /// Called when the object is about to be destroyed (Unity lifecycle)
        /// </summary>
        protected virtual void OnApplicationQuit()
        {
            // Clean up when application is quitting to prevent errors
            if (_instance == this)
            {
                _instance = null;
                _persistenceInitialized = false;
                _sceneLoadCount = 0;
            }
        }

        /// <summary>
        /// Reset the singleton (useful for testing)
        /// </summary>
        public static void Reset()
        {
            if (_instance != null && _instance)
            {
                try
                {
                    // Unsubscribe from events
                    SceneManager.sceneLoaded -= OnSceneLoaded;

                    if (_instance.gameObject != null)
                        Destroy(_instance.gameObject);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Singleton] Error during reset: {e.Message}");
                }
            }

            _instance = null;
            _persistenceInitialized = false;
            _sceneLoadCount = 0;

            Debug.Log($"[Singleton] {typeof(T).Name} reset");
        }

        /// <summary>
        /// Check if singleton is available for use
        /// </summary>
        public static bool IsAvailable()
        {
            try
            {
                return _instance != null && _instance;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Get singleton without creating if it doesn't exist
        /// </summary>
        public static T GetExistingInstance()
        {
            if (_instance != null && _instance)
                return _instance;

            // Try to find existing instance without creating
            var objs = FindObjectsByType(typeof(T), FindObjectsSortMode.None) as T[];
            if (objs != null && objs.Length > 0)
            {
                foreach (var obj in objs)
                {
                    if (obj != null && obj)
                    {
                        return obj;
                    }
                }
            }

            return null;
        }

    }

    /// <summary>
    /// Optional interface for singleton lifecycle events
    /// </summary>
    public interface SingletonLifecycle
    {
        void OnSceneChange(string previousScene, string currentScene);
    }

}