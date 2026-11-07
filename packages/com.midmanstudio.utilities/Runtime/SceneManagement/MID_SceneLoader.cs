// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/scenemanagement.md, section "MID_SceneLoader.cs"
// ============================================================================
using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;

namespace MidManStudio.Core.SceneManagement
{
    /// <summary>
    /// Standard (non-network) scene loader. Implements <see cref="ISceneLoader"/>. Handles single and
    /// additive loading with progress reporting, and can check the internet for scenes marked
    /// <see cref="SceneNetworkDependency.InternetRequired"/>. Wrap it in a
    /// <see cref="MID_SceneTransitionController"/> subclass for fade and loading screens.
    /// </summary>
    /// <remarks>
    /// Setup: put it on a persistent singleton GameObject, add <c>SceneTypeProviderSO</c> assets for your scenes
    /// and run the Scene Type Generator. Only one load runs at a time: a second call while loading is ignored
    /// with a warning. The internet check uses <c>MID_NetworkConnectionManager.ConfirmConnectionAsync</c> from the
    /// netcode package when that assembly is present (found by reflection), else
    /// <c>Application.internetReachability</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// MID_SceneLoader.Instance.LoadScene(SceneId.MainMenu);
    /// MID_SceneLoader.Instance.OnSceneLoadCompleted += id => Debug.Log($"Loaded {id}");
    /// </code>
    /// </example>
    public class MID_SceneLoader : Singleton<MID_SceneLoader>, ISceneLoader
    {
        #region Inspector

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Tooltip("If true, fires OnSceneLoadFailed for InternetRequired scenes " +
                 "when MID_NetworkConnectionManager reports no connection.")]
        [SerializeField] private bool _enforceInternetCheck = true;

        #endregion

        #region State

        private AsyncOperation _asyncOp;
        private bool           _isLoading;
        private int            _currentLoadingId = -1;

        #endregion

        #region ISceneLoader

        /// <inheritdoc/>
        public bool IsLoadingScene      => _isLoading;
        public int  CurrentLoadingSceneId => _currentLoadingId;

        /// <inheritdoc/>
        public Action<float>  OnLoadProgressChanged { get; set; }
        public Action<int>    OnSceneLoadCompleted  { get; set; }
        public Action<string> OnSceneLoadFailed     { get; set; }

        /// <inheritdoc/>
        public void LoadScene(int sceneId, SceneLoadType loadType = SceneLoadType.Single,
            short delayMs = 0)
        {
            if (_isLoading)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Load already in progress ({_currentLoadingId}). Ignoring {sceneId}.",
                    nameof(MID_SceneLoader));
                return;
            }

            if (!SceneRegistry.IsKnown(sceneId) && sceneId != -1)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"SceneId {sceneId} is not in SceneRegistry — loading by index anyway.",
                    nameof(MID_SceneLoader));
            }

            ExecuteLoad(sceneId, loadType, delayMs);
        }

        /// <inheritdoc/>
        public void UnloadScene(int sceneId)
        {
            try
            {
                SceneManager.UnloadSceneAsync(sceneId);
                MID_Logger.LogInfo(_logLevel, $"Unloading scene {sceneId}.",
                    nameof(MID_SceneLoader));
            }
            catch (Exception e)
            {
                MID_Logger.LogError(_logLevel, $"Unload error for {sceneId}: {e.Message}",
                    nameof(MID_SceneLoader), nameof(UnloadScene), e);
            }
        }

        /// <inheritdoc/>
        public bool IsSceneLoaded(int sceneId) =>
            SceneManager.GetSceneByBuildIndex(sceneId).isLoaded;

        #endregion

        #region Public Helpers

        /// <summary>Same as the int overload, taking the generated <see cref="SceneId"/>.</summary>
        public void LoadScene(SceneId id, SceneLoadType loadType = SceneLoadType.Single,
            short delayMs = 0) =>
            LoadScene((int)id, loadType, delayMs);

        public void UnloadScene(SceneId id) => UnloadScene((int)id);
        public bool IsSceneLoaded(SceneId id) => IsSceneLoaded((int)id);

        /// <summary>The generated id of the active scene, or <see cref="SceneId.None"/> when its build index is not in the registry.</summary>
        public SceneId GetActiveSceneId()
        {
            int idx = SceneManager.GetActiveScene().buildIndex;
            return SceneRegistry.IsKnown(idx) ? (SceneId)idx : SceneId.None;
        }

        #endregion

        #region Internal Load

        private async void ExecuteLoad(int sceneId, SceneLoadType loadType, short delayMs)
        {
            _isLoading        = true;
            _currentLoadingId = sceneId;

            try
            {
                // Internet check
                if (_enforceInternetCheck &&
                    SceneRegistry.GetDependency(sceneId) == SceneNetworkDependency.InternetRequired)
                {
                    MID_Logger.LogInfo(_logLevel, $"Scene {sceneId} requires internet — checking.",
                        nameof(MID_SceneLoader));
                    bool ok = await CheckInternetAsync();
                    if (!ok)
                    {
                        string err = $"Internet required to load scene {sceneId}.";
                        MID_Logger.LogError(_logLevel, err, nameof(MID_SceneLoader));
                        OnSceneLoadFailed?.Invoke(err);
                        return;
                    }
                }

                if (delayMs > 0)
                    await Task.Delay(delayMs);

                LoadSceneMode mode = loadType == SceneLoadType.Single
                    ? LoadSceneMode.Single
                    : LoadSceneMode.Additive;

                _asyncOp = SceneManager.LoadSceneAsync(sceneId, mode);

                if (_asyncOp == null)
                    throw new Exception($"AsyncOperation null for scene {sceneId}.");

                _asyncOp.allowSceneActivation = false;

                while (_asyncOp.progress < 0.9f)
                {
                    OnLoadProgressChanged?.Invoke(_asyncOp.progress);
                    await Task.Delay(50);
                }

                OnLoadProgressChanged?.Invoke(1f);
                _asyncOp.allowSceneActivation = true;

                while (!_asyncOp.isDone)
                    await Task.Delay(50);

                MID_Logger.LogInfo(_logLevel, $"Scene {sceneId} loaded.",
                    nameof(MID_SceneLoader));
                OnSceneLoadCompleted?.Invoke(sceneId);
            }
            catch (Exception e)
            {
                string err = $"Error loading scene {sceneId}: {e.Message}";
                MID_Logger.LogError(_logLevel, err, nameof(MID_SceneLoader),
                    nameof(ExecuteLoad), e);
                OnSceneLoadFailed?.Invoke(err);
            }
            finally
            {
                _isLoading        = false;
                _currentLoadingId = -1;
            }
        }

        private async Task<bool> CheckInternetAsync()
        {
            // Attempt to use MID_NetworkConnectionManager if present
            // Uses reflection so this file has no hard dependency on it
            var type = Type.GetType(
                "MidManStudio.Netcode.MID_NetworkConnectionManager, MidManStudio.Netcode");
            if (type != null)
            {
                var method = type.GetMethod("ConfirmConnectionAsync",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method != null)
                {
                    var task = method.Invoke(null, null) as Task<bool>;
                    if (task != null) return await task;
                }
            }
            // Fallback: Unity reachability
            return Application.internetReachability != NetworkReachability.NotReachable;
        }

        #endregion

        #region Lifecycle

        protected override void Awake()
        {
            base.Awake();
            Remake(true);
            MID_Logger.LogInfo(_logLevel, "MID_SceneLoader initialized.",
                nameof(MID_SceneLoader));
        }

        protected override void OnDestroy()
        {
            OnLoadProgressChanged = null;
            OnSceneLoadCompleted  = null;
            OnSceneLoadFailed     = null;
            base.OnDestroy();
        }

        #endregion
    }
}
