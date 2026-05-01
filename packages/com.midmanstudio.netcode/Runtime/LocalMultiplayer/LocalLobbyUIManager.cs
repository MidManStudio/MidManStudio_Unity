// LocalLobbyUIManager.cs
// Generic base class for offline lobby UI.
// Connects to LocalLobbyManager events and exposes virtual hooks for game-specific UI.
// Subclass this in your game and override the On* methods to drive your actual panels.
//
// USAGE:
//   1. Create a class inheriting LocalLobbyUIManager in your game.
//   2. Override the virtual On* methods to update your UI elements.
//   3. Call the protected helper methods (ShowHostPanel, ShowJoinPanel, etc.)
//      or drive layout entirely from your overrides.
//   4. Assign LocalLobbyManager reference in inspector or let it auto-find.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Netcode.LocalMultiplayer;

namespace MidManStudio.Core.Netcode.UI
{
    /// <summary>
    /// Which top-level panel is visible.
    /// </summary>
    public enum LobbyUIState
    {
        None,
        NetworkCheck,   // checking WiFi / hotspot
        Browse,         // browsing available lobbies
        Hosting,        // in lobby as host
        Joining,        // in lobby as client
        Loading         // transitioning
    }

    // ─────────────────────────────────────────────────────────────────────────

    [RequireComponent(typeof(Canvas))]
    public abstract class LocalLobbyUIManager : MonoBehaviour
    {
        #region Inspector

        [Header("Lobby Manager")]
        [SerializeField] protected LocalLobbyManager _lobbyManager;

        [Header("Network Monitor")]
        [SerializeField] protected MobileNetworkStatusMonitor _networkMonitor;

        [Header("Log")]
        [SerializeField] protected MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region State

        private LobbyUIState _currentState = LobbyUIState.None;
        public  LobbyUIState CurrentState  => _currentState;

        #endregion

        #region Lifecycle

        protected virtual void Awake()
        {
            if (_lobbyManager == null)
                _lobbyManager = FindObjectOfType<LocalLobbyManager>();

            if (_networkMonitor == null)
                _networkMonitor = FindObjectOfType<MobileNetworkStatusMonitor>();

            if (_lobbyManager == null)
            {
                MID_Logger.LogError(_logLevel,
                    "LocalLobbyManager not found — UI will not function.",
                    nameof(LocalLobbyUIManager));
                return;
            }

            SubscribeToManager();
        }

        protected virtual void OnDestroy()
        {
            UnsubscribeFromManager();
        }

        #endregion

        #region Manager Event Wiring

        private void SubscribeToManager()
        {
            _lobbyManager.OnLobbyDiscovered        += HandleLobbyDiscovered;
            _lobbyManager.OnLobbyRemoved           += HandleLobbyRemoved;
            _lobbyManager.OnPlayerJoined           += HandlePlayerJoined;
            _lobbyManager.OnPlayerLeft             += HandlePlayerLeft;
            _lobbyManager.OnPlayerReadyStatusChanged += HandlePlayerReadyChanged;
            _lobbyManager.OnHostResult             += HandleHostResult;
            _lobbyManager.OnJoinResult             += HandleJoinResult;
            _lobbyManager.OnLobbyDisbanded         += HandleLobbyDisbanded;
            _lobbyManager.OnNetworkStatusChanged   += HandleNetworkStatusChanged;
            _lobbyManager.OnGameStartReceived      += HandleGameStartReceived;

            if (_networkMonitor != null)
                _networkMonitor.OnNetworkStatusChanged += HandleNetworkStatusChanged;
        }

        private void UnsubscribeFromManager()
        {
            if (_lobbyManager == null) return;
            _lobbyManager.OnLobbyDiscovered        -= HandleLobbyDiscovered;
            _lobbyManager.OnLobbyRemoved           -= HandleLobbyRemoved;
            _lobbyManager.OnPlayerJoined           -= HandlePlayerJoined;
            _lobbyManager.OnPlayerLeft             -= HandlePlayerLeft;
            _lobbyManager.OnPlayerReadyStatusChanged -= HandlePlayerReadyChanged;
            _lobbyManager.OnHostResult             -= HandleHostResult;
            _lobbyManager.OnJoinResult             -= HandleJoinResult;
            _lobbyManager.OnLobbyDisbanded         -= HandleLobbyDisbanded;
            _lobbyManager.OnNetworkStatusChanged   -= HandleNetworkStatusChanged;
            _lobbyManager.OnGameStartReceived      -= HandleGameStartReceived;

            if (_networkMonitor != null)
                _networkMonitor.OnNetworkStatusChanged -= HandleNetworkStatusChanged;
        }

        #endregion

        #region Private Handlers → Virtual Hooks

        private void HandleLobbyDiscovered(LocalLobbyData lobby)
        {
            MID_Logger.LogDebug(_logLevel, $"Lobby discovered: {lobby}",
                nameof(LocalLobbyUIManager));
            OnLobbyDiscovered(lobby);
        }

        private void HandleLobbyRemoved(string key)
        {
            MID_Logger.LogDebug(_logLevel, $"Lobby removed: {key}",
                nameof(LocalLobbyUIManager));
            OnLobbyRemoved(key);
        }

        private void HandlePlayerJoined(LocalLobbyPlayer player)
        {
            MID_Logger.LogDebug(_logLevel, $"Player joined: {player}",
                nameof(LocalLobbyUIManager));
            OnPlayerJoined(player);
        }

        private void HandlePlayerLeft(ulong clientId)
        {
            MID_Logger.LogDebug(_logLevel, $"Player left: {clientId}",
                nameof(LocalLobbyUIManager));
            OnPlayerLeft(clientId);
        }

        private void HandlePlayerReadyChanged(LocalLobbyPlayer player)
        {
            OnPlayerReadyChanged(player);
        }

        private void HandleHostResult(bool success)
        {
            MID_Logger.LogInfo(_logLevel, $"Host result: {success}",
                nameof(LocalLobbyUIManager));
            SetLoading(false);
            if (success) SetState(LobbyUIState.Hosting);
            OnHostResult(success);
        }

        private void HandleJoinResult(bool success)
        {
            MID_Logger.LogInfo(_logLevel, $"Join result: {success}",
                nameof(LocalLobbyUIManager));
            SetLoading(false);
            if (success) SetState(LobbyUIState.Joining);
            OnJoinResult(success);
        }

        private void HandleLobbyDisbanded()
        {
            MID_Logger.LogInfo(_logLevel, "Lobby disbanded.",
                nameof(LocalLobbyUIManager));
            SetState(LobbyUIState.Browse);
            OnLobbyDisbanded();
        }

        private void HandleNetworkStatusChanged(string status)
        {
            MID_Logger.LogDebug(_logLevel, $"Network status: {status}",
                nameof(LocalLobbyUIManager));
            OnNetworkStatusChanged(status);
        }

        private void HandleGameStartReceived(LocalLobbySnapshot snapshot)
        {
            MID_Logger.LogInfo(_logLevel, "Game start received — handing off to game.",
                nameof(LocalLobbyUIManager));
            SetState(LobbyUIState.Loading);
            OnGameStartReceived(snapshot);
        }

        #endregion

        #region Virtual Hooks — Override in subclass

        /// <summary>A new lobby was found during discovery scan.</summary>
        protected virtual void OnLobbyDiscovered(LocalLobbyData lobby) { }

        /// <summary>A previously discovered lobby timed out.</summary>
        protected virtual void OnLobbyRemoved(string lobbyKey) { }

        /// <summary>A player joined the current lobby.</summary>
        protected virtual void OnPlayerJoined(LocalLobbyPlayer player) { }

        /// <summary>A player left the current lobby.</summary>
        protected virtual void OnPlayerLeft(ulong clientId) { }

        /// <summary>A player's ready status changed.</summary>
        protected virtual void OnPlayerReadyChanged(LocalLobbyPlayer player) { }

        /// <summary>Result of attempting to host. success=false means show error UI.</summary>
        protected virtual void OnHostResult(bool success) { }

        /// <summary>Result of attempting to join. success=false means show error UI.</summary>
        protected virtual void OnJoinResult(bool success) { }

        /// <summary>The host left — client should return to browse.</summary>
        protected virtual void OnLobbyDisbanded() { }

        /// <summary>
        /// Network status changed. Status values: WIFI_CONNECTED, HOTSPOT, MOBILE_DATA, NO_NETWORK.
        /// Use to show/hide hosting and joining buttons.
        /// </summary>
        protected virtual void OnNetworkStatusChanged(string status) { }

        /// <summary>
        /// Game is starting. Load your game scene here.
        /// The snapshot contains the final player list with team assignments.
        /// </summary>
        protected virtual void OnGameStartReceived(LocalLobbySnapshot snapshot) { }

        /// <summary>Called whenever the UI transitions to a new state.</summary>
        protected virtual void OnStateChanged(LobbyUIState previous, LobbyUIState next) { }

        #endregion

        #region Protected Helpers

        /// <summary>Transition to a new UI state and fire OnStateChanged.</summary>
        protected void SetState(LobbyUIState newState)
        {
            if (_currentState == newState) return;
            var prev = _currentState;
            _currentState = newState;
            MID_Logger.LogDebug(_logLevel, $"UI state: {prev} → {newState}",
                nameof(LocalLobbyUIManager));
            OnStateChanged(prev, newState);
        }

        /// <summary>Show or hide a loading overlay. Override to drive your own spinner.</summary>
        protected virtual void SetLoading(bool loading)
        {
            if (loading) SetState(LobbyUIState.Loading);
        }

        // ── Action wrappers so subclass UI buttons can call one line ──────────

        protected void RequestHost(LocalLobbyConfig config)
        {
            SetLoading(true);
            _lobbyManager.StartHosting(config);
        }

        protected void RequestJoin(LocalLobbyData lobby)
        {
            SetLoading(true);
            _lobbyManager.JoinLobby(lobby);
        }

        protected void RequestLeave()
        {
            _lobbyManager.LeaveLobby();
            SetState(LobbyUIState.Browse);
        }

        protected void RequestStopHosting()
        {
            _lobbyManager.StopHosting();
            SetState(LobbyUIState.Browse);
        }

        protected void RequestStartSearch() => _lobbyManager.StartSearching();
        protected void RequestStopSearch()  => _lobbyManager.StopSearching();

        protected void RequestSetReady(ulong clientId, bool ready) =>
            _lobbyManager.SetPlayerReady(clientId, ready);

        protected void RequestGameStart() =>
            _lobbyManager.RequestGameStart();

        protected void RequestPlayerName(string name) =>
            _lobbyManager.SetPlayerName(name);

        protected bool CanHost() =>
            _networkMonitor == null || _networkMonitor.CanHost();

        protected bool CanJoin() =>
            _networkMonitor == null || _networkMonitor.CanJoin();

        protected IReadOnlyDictionary<string, LocalLobbyData> GetDiscoveredLobbies() =>
            _lobbyManager.GetDiscoveredLobbies();

        protected System.Collections.Generic.List<LocalLobbyPlayer> GetPlayers() =>
            _lobbyManager.GetPlayers();

        protected bool AreAllReady() => _lobbyManager.AreAllPlayersReady();

        #endregion
    }
}
