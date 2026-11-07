// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/scenemanagement.md, section "MID_SceneTransitionController.cs"
// ============================================================================
using System;
using System.Collections;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;

namespace MidManStudio.Core.SceneManagement
{
    /// <summary>
    /// Abstract base class for scene transition UI controllers. It coordinates <c>MID_SceneLoader</c> and,
    /// optionally, a network loader through <see cref="ISceneLoader"/>. Game code derives from it and overrides
    /// the virtual hooks to drive its own fade or animation (DOTween, LeanTween, coroutines).
    /// </summary>
    /// <remarks>
    /// Order of a transition with <c>useTransition</c> on: <c>OnPreTransition</c>, <c>TransitionIn</c>, the optional
    /// delay, the load starts, <c>OnLoadingStarted</c>, then when the loader reports completion
    /// <c>OnLoadingFinished</c>, <c>TransitionOut</c>, <c>OnPostTransition</c>, and the
    /// <see cref="OnSceneTransitionComplete"/> event. The loaders are looked up once in <c>Start</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// public class MySceneManager : MID_SceneTransitionController
    /// {
    ///     protected override IEnumerator TransitionIn()  { /* fade to black  */ yield break; }
    ///     protected override IEnumerator TransitionOut() { /* fade from black */ yield break; }
    ///     protected override void OnProgressUpdated(float p) { progressBar.value = p; }
    /// }
    ///
    /// MySceneManager.Instance.LoadScene(SceneId.Gameplay);
    /// MySceneManager.Instance.LoadScene(SceneId.Network, SceneLoadType.NetworkAdditive);
    /// </code>
    /// </example>
    [RequireComponent(typeof(Canvas))]
    public abstract class MID_SceneTransitionController : Singleton<MID_SceneTransitionController>
    {
        #region Inspector

        [Header("Transition Settings")]
        [SerializeField] protected float _extraLoadingDelay = 0f;

        [Header("Loading Messages")]
        [SerializeField] protected string[] _loadingMessages =
        {
            "Loading...", "Preparing...", "Almost ready..."
        };
        [SerializeField] protected float _messageCycleInterval = 2f;

        [Header("Log")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Events

        /// <summary>Fires on all clients when a scene finishes loading. Payload is SceneId int.</summary>
        public Action<int>    OnSceneTransitionComplete;
        /// <summary>Fires after a load failed, once the error hook and a fixed 2 second wait are done. Payload is the error message.</summary>
        public Action<string> OnSceneTransitionFailed;
        /// <summary>Fires with the smoothed progress, 0..1, about every 50 ms while loading.</summary>
        public Action<float>  OnSceneTransitionProgress;

        #endregion

        #region State

        private ISceneLoader _regularLoader;
        private ISceneLoader _networkLoader;

        private bool     _isTransitioning;
        private int      _currentTargetId = -1;
        private SceneLoadType _currentLoadType;
        private float    _currentProgress;
        private float    _targetProgress;

        private Coroutine _transitionCoroutine;
        private Coroutine _messageCycleCoroutine;
        private Coroutine _progressCoroutine;

        #endregion

        #region Properties

        /// <summary>True from the start of a transition until it completes or fails.</summary>
        public bool      IsTransitioning   => _isTransitioning;
        /// <summary>The scene being loaded, or <see cref="SceneId.None"/> when idle.</summary>
        public SceneId   CurrentTargetScene => (SceneId)_currentTargetId;
        /// <summary>The smoothed progress shown to the UI, 0..1.</summary>
        public float     CurrentProgress   => _currentProgress;

        #endregion

        #region Lifecycle

        protected override void Awake()
        {
            base.Awake();
            Remake(true);
        }

        protected virtual void Start()
        {
            InitializeLoaders();
        }

        protected override void OnDestroy()
        {
            CleanupTransition();
            UnsubscribeFromLoaders();
            base.OnDestroy();
        }

        #endregion

        #region Loader Wiring

        private void InitializeLoaders()
        {
            // Regular loader — utilities package
            if (MID_SceneLoader.HasInstance)
            {
                _regularLoader = MID_SceneLoader.Instance;
                SubscribeTo(_regularLoader);
                MID_Logger.LogInfo(_logLevel, "Regular scene loader connected.",
                    nameof(MID_SceneTransitionController));
            }
            else
            {
                MID_Logger.LogWarning(_logLevel, "MID_SceneLoader not found.",
                    nameof(MID_SceneTransitionController));
            }

            // Network loader — injected by game code or auto-found
            // Uses reflection so utilities has no hard dep on netcode assembly
            TryFindNetworkLoader();
        }

        private void TryFindNetworkLoader()
        {
            // Game code can call SetNetworkLoader() to inject it directly.
            // This reflection path is a convenience fallback.
            var type = Type.GetType(
                "MidManStudio.Netcode.SceneManagement.MID_NetworkSceneLoader, MidManStudio.Netcode");
            if (type == null) return;

            var go = FindObjectOfType(type) as MonoBehaviour;
            if (go is ISceneLoader loader)
            {
                _networkLoader = loader;
                SubscribeTo(_networkLoader);
                MID_Logger.LogInfo(_logLevel, "Network scene loader connected.",
                    nameof(MID_SceneTransitionController));
            }
        }

        /// <summary>
        /// Inject the network loader from game code.
        /// Call before loading any network scene.
        /// </summary>
        public void SetNetworkLoader(ISceneLoader loader)
        {
            if (_networkLoader != null) UnsubscribeFrom(_networkLoader);
            _networkLoader = loader;
            if (loader != null) SubscribeTo(loader);
        }

        private void SubscribeTo(ISceneLoader loader)
        {
            loader.OnLoadProgressChanged += HandleProgress;
            loader.OnSceneLoadCompleted  += HandleComplete;
            loader.OnSceneLoadFailed     += HandleFailed;
        }

        private void UnsubscribeFrom(ISceneLoader loader)
        {
            if (loader == null) return;
            loader.OnLoadProgressChanged -= HandleProgress;
            loader.OnSceneLoadCompleted  -= HandleComplete;
            loader.OnSceneLoadFailed     -= HandleFailed;
        }

        private void UnsubscribeFromLoaders()
        {
            UnsubscribeFrom(_regularLoader);
            UnsubscribeFrom(_networkLoader);
        }

        #endregion

        #region Public API

        /// <summary>Load a scene, auto-detecting whether to use the regular or network loader.</summary>
        public void LoadScene(SceneId sceneId, bool useTransition = true, short delayMs = 0)
        {
            var dep      = SceneRegistry.GetDependency(sceneId);
            var loadType = dep == SceneNetworkDependency.NetworkSessionRequired
                ? SceneLoadType.NetworkAdditive
                : SceneLoadType.Single;

            LoadScene((int)sceneId, loadType, useTransition, delayMs);
        }

        /// <summary>Load a scene with an explicit load type.</summary>
        public void LoadScene(SceneId sceneId, SceneLoadType loadType,
            bool useTransition = true, short delayMs = 0) =>
            LoadScene((int)sceneId, loadType, useTransition, delayMs);

        /// <summary>
        /// Starts a transition. Ignored with a warning while another transition runs. Reports
        /// <see cref="OnSceneTransitionFailed"/> when no loader is connected for the load type.
        /// <paramref name="delayMs"/> is waited here, after TransitionIn, not inside the loader.
        /// </summary>
        public void LoadScene(int sceneId, SceneLoadType loadType = SceneLoadType.Single,
            bool useTransition = true, short delayMs = 0)
        {
            if (_isTransitioning)
            {
                MID_Logger.LogWarning(_logLevel, "Transition already in progress.",
                    nameof(MID_SceneTransitionController));
                return;
            }

            if (!ValidateLoader(loadType))
            {
                OnSceneTransitionFailed?.Invoke($"No suitable loader for {loadType}.");
                return;
            }

            _currentTargetId = sceneId;
            _currentLoadType = loadType;

            if (_transitionCoroutine != null) StopCoroutine(_transitionCoroutine);
            _transitionCoroutine = StartCoroutine(useTransition
                ? RunWithTransition(delayMs)
                : RunWithoutTransition(delayMs));
        }

        [ContextMenu("Emergency Cleanup")]
        /// <summary>Stops all transition coroutines and resets the state. Also in the component context menu.</summary>
        public void EmergencyCleanup()
        {
            MID_Logger.LogWarning(_logLevel, "Emergency cleanup triggered.",
                nameof(MID_SceneTransitionController));
            CleanupTransition();
        }

        #endregion

        #region Coroutines

        private IEnumerator RunWithTransition(short delayMs)
        {
            _isTransitioning = true;
            _currentProgress = 0f;
            _targetProgress  = 0f;

            // Pre-load hook
            yield return StartCoroutine(OnPreTransition());

            // Fade in / black screen
            yield return StartCoroutine(TransitionIn());

            if (delayMs > 0)
                yield return new WaitForSeconds(delayMs / 1000f);

            // Start actual load
            StartLoadingOperation();

            // Show loading UI
            yield return StartCoroutine(OnLoadingStarted());

            _messageCycleCoroutine = StartCoroutine(CycleMessages());
            _progressCoroutine     = StartCoroutine(TrackProgress());

            if (_extraLoadingDelay > 0f)
                yield return new WaitForSeconds(_extraLoadingDelay);

            // Wait — completion handled by HandleComplete callback which starts CompleteTransition
        }

        private IEnumerator RunWithoutTransition(short delayMs)
        {
            _isTransitioning = true;
            if (delayMs > 0) yield return new WaitForSeconds(delayMs / 1000f);
            StartLoadingOperation();
        }

        private IEnumerator CompleteTransition()
        {
            // Stop cycling
            if (_messageCycleCoroutine != null)
            { StopCoroutine(_messageCycleCoroutine); _messageCycleCoroutine = null; }
            if (_progressCoroutine != null)
            { StopCoroutine(_progressCoroutine); _progressCoroutine = null; }

            yield return StartCoroutine(OnLoadingFinished());
            yield return StartCoroutine(TransitionOut());
            yield return StartCoroutine(OnPostTransition());

            int completedId = _currentTargetId;
            ResetState();
            OnSceneTransitionComplete?.Invoke(completedId);

            MID_Logger.LogInfo(_logLevel, $"Transition complete for scene {completedId}.",
                nameof(MID_SceneTransitionController));
        }

        private IEnumerator TrackProgress()
        {
            while (_isTransitioning)
            {
                if (_currentProgress < _targetProgress)
                {
                    _currentProgress = Mathf.MoveTowards(_currentProgress, _targetProgress,
                        2f * Time.deltaTime);
                    OnProgressUpdated(_currentProgress);
                    OnSceneTransitionProgress?.Invoke(_currentProgress);
                }
                yield return new WaitForSeconds(0.05f);
            }
        }

        private IEnumerator CycleMessages()
        {
            int idx = 0;
            while (_isTransitioning)
            {
                if (_loadingMessages != null && _loadingMessages.Length > 0)
                {
                    OnLoadingMessageChanged(_loadingMessages[idx % _loadingMessages.Length]);
                    idx++;
                }
                yield return new WaitForSeconds(_messageCycleInterval);
            }
        }

        #endregion

        #region Load Dispatch

        private void StartLoadingOperation()
        {
            ISceneLoader loader = _currentLoadType == SceneLoadType.NetworkAdditive
                ? _networkLoader
                : _regularLoader;

            loader?.LoadScene(_currentTargetId, _currentLoadType);

            MID_Logger.LogInfo(_logLevel,
                $"Load started — scene={_currentTargetId} type={_currentLoadType}",
                nameof(MID_SceneTransitionController));
        }

        private bool ValidateLoader(SceneLoadType loadType)
        {
            if (loadType == SceneLoadType.NetworkAdditive)
                return _networkLoader != null;
            return _regularLoader != null;
        }

        #endregion

        #region Loader Callbacks

        private void HandleProgress(float progress)
        {
            _targetProgress = progress;
        }

        private void HandleComplete(int sceneId)
        {
            if (sceneId != _currentTargetId || !_isTransitioning) return;
            MID_Logger.LogInfo(_logLevel, $"Scene {sceneId} load complete.",
                nameof(MID_SceneTransitionController));
            _targetProgress = 1f;
            StartCoroutine(CompleteTransition());
        }

        private void HandleFailed(string error)
        {
            MID_Logger.LogError(_logLevel, $"Scene load failed: {error}",
                nameof(MID_SceneTransitionController));
            StartCoroutine(HandleError(error));
        }

        private IEnumerator HandleError(string error)
        {
            if (_messageCycleCoroutine != null)
            { StopCoroutine(_messageCycleCoroutine); _messageCycleCoroutine = null; }
            if (_progressCoroutine != null)
            { StopCoroutine(_progressCoroutine); _progressCoroutine = null; }

            yield return StartCoroutine(OnTransitionError(error));
            yield return new WaitForSeconds(2f);

            ResetState();
            OnSceneTransitionFailed?.Invoke(error);
        }

        #endregion

        #region Virtual Hooks — override in subclass

        /// <summary>Called before TransitionIn. Use for pre-transition cleanup (popups, etc.).</summary>
        protected virtual IEnumerator OnPreTransition() { yield break; }

        /// <summary>Fade to black / animate in. Yield until animation is done.</summary>
        protected virtual IEnumerator TransitionIn() { yield break; }

        /// <summary>Fade from black / animate out. Yield until animation is done.</summary>
        protected virtual IEnumerator TransitionOut() { yield break; }

        /// <summary>Called after scene load starts — show spinner, progress bar, etc.</summary>
        protected virtual IEnumerator OnLoadingStarted() { yield break; }

        /// <summary>Called when load finishes, before TransitionOut — hide spinner, etc.</summary>
        protected virtual IEnumerator OnLoadingFinished() { yield break; }

        /// <summary>Called after TransitionOut — final cleanup, unhide HUD, etc.</summary>
        protected virtual IEnumerator OnPostTransition() { yield break; }

        /// <summary>Called every ~50ms with smoothed progress 0..1.</summary>
        protected virtual void OnProgressUpdated(float progress) { }

        /// <summary>Called when the loading message cycles to a new string.</summary>
        protected virtual void OnLoadingMessageChanged(string message) { }

        /// <summary>Called when load fails. Show error UI, then yield for as long as needed.</summary>
        protected virtual IEnumerator OnTransitionError(string error) { yield break; }

        /// <summary>Declared for subclasses but not called by this base class. Use the <see cref="OnSceneTransitionComplete"/> event.</summary>
        protected virtual void OnTransitionComplete(int sceneId) { }

        #endregion

        #region Cleanup

        private void CleanupTransition()
        {
            if (_transitionCoroutine != null)
            { StopCoroutine(_transitionCoroutine); _transitionCoroutine = null; }
            if (_messageCycleCoroutine != null)
            { StopCoroutine(_messageCycleCoroutine); _messageCycleCoroutine = null; }
            if (_progressCoroutine != null)
            { StopCoroutine(_progressCoroutine); _progressCoroutine = null; }

            ResetState();
        }

        private void ResetState()
        {
            _isTransitioning  = false;
            _currentTargetId  = -1;
            _currentLoadType  = SceneLoadType.Single;
            _currentProgress  = 0f;
            _targetProgress   = 0f;
        }

        #endregion
    }
}
