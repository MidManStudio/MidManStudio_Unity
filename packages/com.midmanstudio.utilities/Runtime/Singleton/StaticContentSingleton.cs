// StaticContentSingleton.cs
// Thread-safe lazy singleton for plain C# classes (not MonoBehaviours).
// Use for stateless managers, registries, caches, and service locators
// that don't need a GameObject.
//
// USAGE:
//   public class MyRegistry : StaticContentSingleton<MyRegistry>
//   {
//       public void DoWork() { }
//   }
//   MyRegistry.Instance.DoWork();
//
// INITIALIZE WITH CUSTOM INSTANCE:
//   StaticContentSingleton<MyRegistry>.Initialize(new MyRegistrySubclass());
//
// DISPOSABLE CLEANUP:
//   If T implements System.IDisposable, Reset() calls Dispose() automatically.

using UnityEngine;

namespace MidManStudio.Core.Singleton
{
    /// <summary>
    /// Thread-safe lazy singleton for plain C# classes.
    /// </summary>
    public class StaticContentSingleton<T> where T : class, new()
    {
        private static T              _instance;
        private static readonly object _lock        = new object();
        private static bool           _initialized;

        // ── Public properties ─────────────────────────────────────────────────

        public static bool HasInstance    => _instance != null;
        public static bool IsInitialized  => _initialized;

        /// <summary>
        /// Returns the existing instance without creating one.
        /// Returns null if the singleton has not been initialized yet.
        /// </summary>
        public static T TryGetInstance() => _instance;

        // ── Instance accessor ─────────────────────────────────────────────────

        /// <summary>
        /// Get (or lazily create) the singleton instance.
        /// Thread-safe via double-checked locking.
        /// </summary>
        public static T Instance
        {
            get
            {
                if (_instance != null) return _instance;

                lock (_lock)
                {
                    if (_instance != null) return _instance;

                    _instance    = new T();
                    _initialized = true;

                    Debug.Log(
                        $"[StaticSingleton] Created {typeof(T).Name}.");

                    TryCallInitialize(_instance);
                }

                return _instance;
            }
        }

        // ── Explicit initialization ───────────────────────────────────────────

        /// <summary>
        /// Provide a custom (or pre-configured) instance instead of using new T().
        /// Useful for injecting subclasses or mock objects in tests.
        /// If an instance already exists it is reset first.
        /// </summary>
        public static void Initialize(T instance)
        {
            lock (_lock)
            {
                if (_instance != null)
                {
                    Debug.LogWarning(
                        $"[StaticSingleton] {typeof(T).Name} already initialized — " +
                        "overriding with supplied instance.");
                    TryDispose(_instance);
                }

                _instance    = instance;
                _initialized = instance != null;

                if (_initialized)
                {
                    TryCallInitialize(_instance);
                    Debug.Log(
                        $"[StaticSingleton] {typeof(T).Name} initialized with " +
                        "supplied instance.");
                }
            }
        }

        // ── Reset ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Dispose (if applicable) and clear the singleton.
        /// The next access to Instance will create a fresh one.
        /// Primarily for testing or explicit lifecycle management.
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                if (_instance == null)
                {
                    Debug.Log(
                        $"[StaticSingleton] {typeof(T).Name} — nothing to reset.");
                    return;
                }

                TryDispose(_instance);
                _instance    = null;
                _initialized = false;

                Debug.Log($"[StaticSingleton] {typeof(T).Name} reset.");
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static void TryCallInitialize(T instance)
        {
            if (instance is IStaticSingletonInitializable init && !init.IsInitialized)
                init.Initialize();
        }

        private static void TryDispose(T instance)
        {
            if (instance is System.IDisposable disposable)
                disposable.Dispose();
        }
    }

    // ── Optional interfaces ───────────────────────────────────────────────────

    /// <summary>
    /// Implement on your singleton class to receive an Initialize() call
    /// the first time the instance is created.
    /// </summary>
    public interface IStaticSingletonInitializable
    {
        bool IsInitialized { get; }
        void Initialize();
    }
}
