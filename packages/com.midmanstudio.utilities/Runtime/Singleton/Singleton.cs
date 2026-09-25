// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/singleton.md, section "Singleton.cs"
// ============================================================================
using UnityEngine;
using UnityEngine.SceneManagement;


namespace MidManStudio.Core.Singleton
{
    /// <summary>
    /// Base class for a MonoBehaviour singleton. Subclass it with your own
    /// component type: <c>public class Foo : Singleton&lt;Foo&gt;</c>.
    ///
    /// <see cref="Instance"/> lazily finds or creates the singleton the first
    /// time it is accessed: if no instance exists in the scene, one is
    /// created automatically (in play mode only). To check whether one
    /// already exists without triggering that auto-create, use
    /// <see cref="HasInstance"/> or <see cref="GetExistingInstance"/>
    /// instead. If an instance turns up unexpectedly, it usually means
    /// something accessed <see cref="Instance"/> before a real one was
    /// placed in the scene.
    /// </summary>
    /// <typeparam name="T">The concrete MonoBehaviour subclass.</typeparam>
    public class Singleton<T> : MonoBehaviour where T : Component
    {
        private static T _instance;

        // Public properties with proper null checks

        /// <summary>True if a live instance currently exists. Never creates one.</summary>
        public static bool HasInstance => _instance != null && _instance;

        /// <summary>Returns the existing instance, or null if none exists yet. Never creates one.</summary>
        public static T TryGetInstance() => HasInstance ? _instance : null;

        /// <summary>
        /// The raw backing instance, without the find/create logic
        /// <see cref="Instance"/> runs. Unlike <see cref="TryGetInstance"/>,
        /// this can return a reference to an instance Unity has since
        /// destroyed; prefer <see cref="TryGetInstance"/> or
        /// <see cref="HasInstance"/> unless that distinction matters.
        /// </summary>
        public static T CurrentInstance => _instance;

        // Persistence settings
        private static bool _persistAcrossScenes = false;
        private static bool _persistenceInitialized = false;

        // Scene management
        private static string _originSceneName;
        private static int _sceneLoadCount = 0;

        // Events

        /// <summary>Signature for <see cref="OnSceneChanged"/>.</summary>
        public delegate void SceneChangeHandler(string previousScene, string currentScene);

        /// <summary>
        /// Raised after a scene finishes loading, but only once this
        /// singleton has been marked to persist across scenes (see
        /// <see cref="InitializeSingleton"/>). Never raised for a
        /// non-persisting singleton.
        /// </summary>
        public static event SceneChangeHandler OnSceneChanged;

        /// <summary>
        /// Gets the singleton instance, finding it in the scene or creating
        /// one if neither exists yet (play mode only; logs a warning and
        /// returns null in edit mode). If more than one instance is found,
        /// keeps the first valid one and destroys the rest. See the class
        /// summary for why this can surprise you with an unexpected
        /// auto-created instance.
        /// </summary>
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

                    // Create a new instance if none was found in the scene.
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

        /// <summary>Calls <see cref="InitializeSingleton"/> with persistence off. Override to change what happens on Awake, calling base.Awake() first.</summary>
        protected virtual void Awake()
        {
            InitializeSingleton(false);
        }

        /// <summary>Re-runs singleton setup, for example after a subclass has been reconfigured. No-op if this component has already been destroyed.</summary>
        protected virtual void Remake(bool persistAcrossScenes = false)
        {
            if (this == null) return;
            InitializeSingleton(persistAcrossScenes);
        }

        /// <summary>
        /// Claims this component as the singleton instance if none exists
        /// yet, or destroys this GameObject if a different instance already
        /// holds the slot. Pass <paramref name="persistAcrossScenes"/> true
        /// to also call <see cref="DontDestroyOnLoad"/> and start raising
        /// <see cref="OnSceneChanged"/>. Only valid in play mode.
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

        /// <summary>Clears the static instance reference and unsubscribes from scene events, if this was the active instance.</summary>
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

        /// <summary>Clears the static instance state on quit, so a leftover reference doesn't survive into the next play session in the editor.</summary>
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
        /// Destroys the current instance's GameObject (if any) and clears
        /// all static state, so the next <see cref="Instance"/> access
        /// starts fresh. Mainly useful for tests.
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
        /// Equivalent to <see cref="HasInstance"/>, wrapped in a try/catch
        /// so it can never throw.
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
        /// Returns the existing instance without creating one, searching
        /// the scene if the cached reference is stale. Unlike
        /// <see cref="TryGetInstance"/>, this re-searches the scene rather
        /// than trusting the cached field, at the cost of an allocation.
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
    /// Optional interface for singleton lifecycle events. Implement it on
    /// a <see cref="Singleton{T}"/> subclass to be notified of scene
    /// changes while persisting.
    /// </summary>
    public interface SingletonLifecycle
    {
        /// <summary>Called after a scene finishes loading, while this singleton is persisting across scenes.</summary>
        void OnSceneChange(string previousScene, string currentScene);
    }

}
