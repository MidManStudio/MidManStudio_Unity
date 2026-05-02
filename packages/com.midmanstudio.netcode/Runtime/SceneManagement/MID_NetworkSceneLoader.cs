// MID_NetworkSceneLoader.cs
// NGO-managed additive scene loader. Implements ISceneLoader.
// Uses HybridNetworkSingleton — available before NGO spawns.
// Scene names resolved via generated SceneRegistry.
//
// SETUP:
//   1. Add to a persistent NetworkBehaviour GameObject with a NetworkObject.
//   2. Call MID_SceneTransitionController.Instance.SetNetworkLoader(Instance) at runtime.
//   3. Host calls LoadScene(..., SceneLoadType.NetworkAdditive) — synced to all clients.

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using MidManStudio.Core.Logging;
using MidManStudio.Core.SceneManagement;
using SceneEventProgressStatus = Unity.Netcode.SceneEventProgressStatus;

namespace MidManStudio.Core.Netcode.SceneManagement
{
    /// <summary>
    /// NGO-managed scene loader. Host/server only — clients receive scene events automatically.
    /// </summary>
    public class MID_NetworkSceneLoader : HybridNetworkSingleton<MID_NetworkSceneLoader>,
                                          ISceneLoader
    {
        #region Inspector

        [SerializeField] private NetworkManager _networkManager;
        [SerializeField] private MID_LogLevel   _logLevel = MID_LogLevel.Info;

        #endregion

        #region ISceneLoader

        public bool IsLoadingScene        => _isTransitionInProgress;
        public int  CurrentLoadingSceneId => (int)_sceneBeingLoaded;

        public Action<float>  OnLoadProgressChanged { get; set; }
        public Action<int>    OnSceneLoadCompleted  { get; set; }
        public Action<string> OnSceneLoadFailed     { get; set; }

        public void LoadScene(int sceneId, SceneLoadType loadType = SceneLoadType.NetworkAdditive,
            short delayMs = 0)
        {
            string sceneName = SceneRegistry.GetName(sceneId);
            if (string.IsNullOrEmpty(sceneName))
            {
                string err = $"SceneId {sceneId} has no scene name in SceneRegistry.";
                MID_Logger.LogError(_logLevel, err, nameof(MID_NetworkSceneLoader));
                OnSceneLoadFailed?.Invoke(err);
                return;
            }

            NetworkLoadScene(sceneId, sceneName);
        }

        public void UnloadScene(int sceneId)
        {
            if (!ValidateOperation("unload scene")) return;

            if (!_currentGameplayScene.IsValid() || !_currentGameplayScene.isLoaded)
            {
                MID_Logger.LogWarning(_logLevel, "No valid gameplay scene to unload.",
                    nameof(MID_NetworkSceneLoader));
                return;
            }

            try
            {
                _networkManager.SceneManager.UnloadScene(_currentGameplayScene);
                MID_Logger.LogInfo(_logLevel, $"Unloading scene {sceneId}.",
                    nameof(MID_NetworkSceneLoader));
            }
            catch (Exception e)
            {
                MID_Logger.LogError(_logLevel, $"Unload error: {e.Message}",
                    nameof(MID_NetworkSceneLoader), nameof(UnloadScene), e);
            }
        }

        public bool IsSceneLoaded(int sceneId) =>
            UnityEngine.SceneManagement.SceneManager.GetSceneByBuildIndex(sceneId).isLoaded;

        #endregion

        #region State

        private bool              _isTransitionInProgress;
        private int               _sceneBeingLoaded = -1;
        private Scene             _currentGameplayScene;
        private int               _currentActiveSceneId = -1;
        private readonly Dictionary<ulong, bool> _clientLoadStatus = new();

        #endregion

        #region Extra Events

        public Action<ulong, bool>               OnPlayerReadinessChanged;
        public Action<SceneEventProgressStatus>  OnSceneEventProgressUpdate;

        #endregion

        #region Lifecycle

        protected override void Awake()
        {
            base.Awake();
            FindNetworkManager();
            MID_Logger.LogInfo(_logLevel, "MID_NetworkSceneLoader initialized.",
                nameof(MID_NetworkSceneLoader));
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            FindNetworkManager();
            RegisterNGOEvents();
        }

        public override void OnNetworkDespawn()
        {
            UnregisterNGOEvents();
            _clientLoadStatus.Clear();
            _isTransitionInProgress = false;
            base.OnNetworkDespawn();
        }

        public override void OnDestroy()
        {
            UnregisterNGOEvents();
            _clientLoadStatus.Clear();
            OnLoadProgressChanged = null;
            OnSceneLoadCompleted  = null;
            OnSceneLoadFailed     = null;
            base.OnDestroy();
        }

        #endregion

        #region NGO Event Wiring

        private void RegisterNGOEvents()
        {
            if (_networkManager?.SceneManager == null) return;
            _networkManager.OnClientConnectedCallback    += OnClientConnected;
            _networkManager.OnClientDisconnectCallback   += OnClientDisconnected;
            _networkManager.SceneManager.OnSceneEvent        += OnNGOSceneEvent;
            _networkManager.SceneManager.OnLoadEventCompleted += OnLoadEventCompleted;
            _networkManager.SceneManager.OnUnloadEventCompleted += OnUnloadEventCompleted;
            _networkManager.SceneManager.OnLoadComplete    += OnClientLoadComplete;
        }

        private void UnregisterNGOEvents()
        {
            if (_networkManager?.SceneManager == null) return;
            _networkManager.OnClientConnectedCallback    -= OnClientConnected;
            _networkManager.OnClientDisconnectCallback   -= OnClientDisconnected;
            _networkManager.SceneManager.OnSceneEvent        -= OnNGOSceneEvent;
            _networkManager.SceneManager.OnLoadEventCompleted -= OnLoadEventCompleted;
            _networkManager.SceneManager.OnUnloadEventCompleted -= OnUnloadEventCompleted;
            _networkManager.SceneManager.OnLoadComplete    -= OnClientLoadComplete;
        }

        #endregion

        #region NGO Callbacks

        private void OnClientConnected(ulong clientId)
        {
            _clientLoadStatus[clientId] = false;
        }

        private void OnClientDisconnected(ulong clientId)
        {
            _clientLoadStatus.Remove(clientId);
        }

        private void OnNGOSceneEvent(SceneEvent ev)
        {
            MID_Logger.LogDebug(_logLevel, $"NGO scene event: {ev.SceneEventType} — {ev.SceneName}",
                nameof(MID_NetworkSceneLoader));

            if (ev.SceneEventType == SceneEventType.Load)
                OnSceneEventProgressUpdate?.Invoke(SceneEventProgressStatus.Started);
        }

        private void OnLoadEventCompleted(string sceneName, LoadSceneMode mode,
            List<ulong> completed, List<ulong> timedOut)
        {
            MID_Logger.LogInfo(_logLevel,
                $"All clients loaded '{sceneName}'. Completed={completed.Count} TimedOut={timedOut.Count}",
                nameof(MID_NetworkSceneLoader));

            int buildIdx = GetBuildIndex(sceneName);

            _currentGameplayScene   = UnityEngine.SceneManagement.SceneManager.GetSceneByName(sceneName);
            _currentActiveSceneId   = buildIdx;
            _isTransitionInProgress = false;
            _sceneBeingLoaded       = -1;

            ResetAllClientStatus();

            OnSceneLoadCompleted?.Invoke(buildIdx);
        }

        private void OnUnloadEventCompleted(string sceneName, LoadSceneMode mode,
            List<ulong> completed, List<ulong> timedOut)
        {
            _currentGameplayScene = default;
        }

        private void OnClientLoadComplete(ulong clientId, string sceneName, LoadSceneMode mode)
        {
            int total   = _clientLoadStatus.Count;
            int loaded  = 0;
            foreach (var v in _clientLoadStatus.Values) if (v) loaded++;

            float progress = total > 0 ? (float)(loaded + 1) / total : 1f;
            OnLoadProgressChanged?.Invoke(progress);
        }

        #endregion

        #region Core Load

        private void NetworkLoadScene(int sceneId, string sceneName)
        {
            if (!ValidateOperation($"load scene {sceneName}")) return;

            try
            {
                ResetAllClientStatus();
                _sceneBeingLoaded       = sceneId;
                _isTransitionInProgress = true;

                MID_Logger.LogInfo(_logLevel,
                    $"Starting NGO additive load: {sceneName}",
                    nameof(MID_NetworkSceneLoader));

                var status = _networkManager.SceneManager.LoadScene(sceneName, LoadSceneMode.Additive);

                if (status != SceneEventProgressStatus.Started)
                    throw new Exception($"NGO scene load returned: {status}");
            }
            catch (Exception e)
            {
                _isTransitionInProgress = false;
                _sceneBeingLoaded       = -1;
                string err = $"NGO load error for {sceneName}: {e.Message}";
                MID_Logger.LogError(_logLevel, err, nameof(MID_NetworkSceneLoader),
                    nameof(NetworkLoadScene), e);
                OnSceneLoadFailed?.Invoke(err);
            }
        }

        #endregion

        #region Player Readiness

        public void SetPlayerReady(ulong clientId, bool ready)
        {
            if (!_clientLoadStatus.ContainsKey(clientId)) return;
            _clientLoadStatus[clientId] = ready;
            OnPlayerReadinessChanged?.Invoke(clientId, ready);
        }

        public bool IsPlayerReady(ulong clientId) =>
            _clientLoadStatus.TryGetValue(clientId, out bool r) && r;

        public bool AreAllPlayersReady()
        {
            if (_clientLoadStatus.Count == 0) return false;
            foreach (var v in _clientLoadStatus.Values)
                if (!v) return false;
            return true;
        }

        public int  GetCurrentActiveSceneId() => _currentActiveSceneId;
        public bool IsTransitionInProgress()  => _isTransitionInProgress;

        #endregion

        #region Helpers

        private void FindNetworkManager()
        {
            if (_networkManager == null)
                _networkManager = FindAnyObjectByType<NetworkManager>();
        }

        private bool ValidateOperation(string op)
        {
            FindNetworkManager();

            if (_networkManager == null)
            {
                string e = $"Cannot {op}: NetworkManager not found.";
                MID_Logger.LogError(_logLevel, e, nameof(MID_NetworkSceneLoader));
                OnSceneLoadFailed?.Invoke(e);
                return false;
            }

            if (!IsNetworkReady() || !_networkManager.IsListening)
            {
                string e = $"Cannot {op}: Network not ready.";
                MID_Logger.LogError(_logLevel, e, nameof(MID_NetworkSceneLoader));
                OnSceneLoadFailed?.Invoke(e);
                return false;
            }

            if (!_networkManager.IsServer && !_networkManager.IsHost)
            {
                string e = $"Cannot {op}: Only host/server can load scenes.";
                MID_Logger.LogError(_logLevel, e, nameof(MID_NetworkSceneLoader));
                OnSceneLoadFailed?.Invoke(e);
                return false;
            }

            return true;
        }

        private static int GetBuildIndex(string sceneName)
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCountInBuildSettings; i++)
            {
                string path = UnityEngine.SceneManagement.SceneUtility.GetScenePathByIndex(i);
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                if (name == sceneName) return i;
            }
            return -1;
        }

        private void ResetAllClientStatus()
        {
            var keys = new List<ulong>(_clientLoadStatus.Keys);
            foreach (var k in keys)
            {
                _clientLoadStatus[k] = false;
                OnPlayerReadinessChanged?.Invoke(k, false);
            }
        }

        #endregion
    }
}
