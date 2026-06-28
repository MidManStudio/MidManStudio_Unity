
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.UIState;
using MidManStudio.Netcode.LocalMultiplayer;

namespace MidManStudio.Netcode.UI
{
    /// <summary>
    /// Overridable ui manager for offline lobbies, subscribes to callbacks from lobbymanager , and invokes events that can be ovveriden to perform some logic, useful for ui and stuff
    /// </summary>
    [RequireComponent(typeof(Canvas))]
    public abstract class LocalLobbyUIManager : MonoBehaviour
    {
        #region Inspector

        [Header("Lobby Manager")]
        [SerializeField] protected LocalLobbyManager _lobbyManager;

        [Header("Network Monitor")]
        [SerializeField] protected MobileNetworkStatusMonitor _networkMonitor;

        [Header("UI State Context")]
        [Tooltip("Assign the 'ProjLobby' MID_UIStateContext SO here.\n" +
                 "Expected states (bit values):\n" +
                 "  Main=1  Searching=2  InLobby=4  Loading=8  NetworkCheck=16\n" +
                 "Run the UI State Context Generator after adding/editing states.")]
        [SerializeField] protected MID_UIStateContext _lobbyContext;

        [Header("Log")]
        [SerializeField] protected MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region State accessors

        /// <summary>Raw int of the current lobby UI state.</summary>
        public int CurrentState => _lobbyContext != null ? _lobbyContext.CurrentState : 0;

        /// <summary>True when back-navigation is available.</summary>
        public bool CanGoBack => _lobbyContext != null && _lobbyContext.CanGoBack;

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

            if (_lobbyContext == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "No LobbyContext assigned. State transitions will fire events " +
                    "but panels won't auto-show/hide.\n" +
                    "Create a MID_UIStateContext with contextName='ProjLobby' and assign it here.",
                    nameof(LocalLobbyUIManager));
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
            _lobbyManager.OnLobbyDiscovered          += HandleLobbyDiscovered;
            _lobbyManager.OnLobbyRemoved             += HandleLobbyRemoved;
            _lobbyManager.OnPlayerJoined             += HandlePlayerJoined;
            _lobbyManager.OnPlayerLeft               += HandlePlayerLeft;
            _lobbyManager.OnPlayerReadyStatusChanged += HandlePlayerReadyChanged;
            _lobbyManager.OnHostResult               += HandleHostResult;
            _lobbyManager.OnJoinResult               += HandleJoinResult;
            _lobbyManager.OnLobbyDisbanded           += HandleLobbyDisbanded;
            _lobbyManager.OnNetworkStatusChanged     += HandleNetworkStatusChanged;
            _lobbyManager.OnGameStartReceived        += HandleGameStartReceived;

            if (_networkMonitor != null)
                _networkMonitor.OnNetworkStatusChanged += HandleNetworkStatusChanged;
        }

        private void UnsubscribeFromManager()
        {
            if (_lobbyManager == null) return;

            _lobbyManager.OnLobbyDiscovered          -= HandleLobbyDiscovered;
            _lobbyManager.OnLobbyRemoved             -= HandleLobbyRemoved;
            _lobbyManager.OnPlayerJoined             -= HandlePlayerJoined;
            _lobbyManager.OnPlayerLeft               -= HandlePlayerLeft;
            _lobbyManager.OnPlayerReadyStatusChanged -= HandlePlayerReadyChanged;
            _lobbyManager.OnHostResult               -= HandleHostResult;
            _lobbyManager.OnJoinResult               -= HandleJoinResult;
            _lobbyManager.OnLobbyDisbanded           -= HandleLobbyDisbanded;
            _lobbyManager.OnNetworkStatusChanged     -= HandleNetworkStatusChanged;
            _lobbyManager.OnGameStartReceived        -= HandleGameStartReceived;

            if (_networkMonitor != null)
                _networkMonitor.OnNetworkStatusChanged -= HandleNetworkStatusChanged;
        }

        #endregion

        #region Private Handlers

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

        private void HandlePlayerReadyChanged(LocalLobbyPlayer player) =>
            OnPlayerReadyChanged(player);

        private void HandleHostResult(bool success)
        {
            MID_Logger.LogInfo(_logLevel, $"Host result: {success}",
                nameof(LocalLobbyUIManager));

            if (success)
                GoToInLobby();
            else
                GoToMain(); // FIX: previously stayed on Loading panel on failure

            OnHostResult(success);
        }

        private void HandleJoinResult(bool success)
        {
            MID_Logger.LogInfo(_logLevel, $"Join result: {success}",
                nameof(LocalLobbyUIManager));

            if (success)
                GoToInLobby();
            else
                GoToMain(); // FIX: previously stayed on Loading panel on failure

            OnJoinResult(success);
        }

        private void HandleLobbyDisbanded()
        {
            MID_Logger.LogInfo(_logLevel, "Lobby disbanded.",
                nameof(LocalLobbyUIManager));

            GoToMain();
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
            MID_Logger.LogInfo(_logLevel, "Game start received.",
                nameof(LocalLobbyUIManager));

            GoToLoading();
            OnGameStartReceived(snapshot);
        }

        #endregion

        #region Context State Navigation

        /// <summary>
        /// Transition to a state by raw int (from generated ProjLobbyUIState enum).
        /// All GoTo* methods funnel through here.
        /// </summary>
        protected void ChangeState(int newState)
        {
            if (_lobbyContext == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "Cannot change state — no LobbyContext assigned.",
                    nameof(LocalLobbyUIManager));
                return;
            }
            _lobbyContext.ChangeState(newState);
        }

        /// <summary>Navigate back one level in the context history.</summary>
        protected void GoBack() => _lobbyContext?.GoBack();

        // ── Named state transitions ───────────────────────────────────────────

        /// <summary>
        /// Initial screen: player name field, Host button, Look for Lobbies button.
        /// ProjLobbyUIState.Main = 1.
        /// </summary>
        protected void GoToMain() => ChangeStateByName("Main");

        /// <summary>
        /// Lobby list screen shown after clicking Look for Lobbies.
        /// ProjLobbyUIState.Searching = 2.
        /// </summary>
        protected void GoToSearching() => ChangeStateByName("Searching");

        /// <summary>
        /// In-lobby room shared by host and joining client.
        /// ProjLobbyUIState.InLobby = 4.
        /// </summary>
        protected void GoToInLobby() => ChangeStateByName("InLobby");

        /// <summary>Loading / connecting overlay. ProjLobbyUIState.Loading = 8.</summary>
        protected void GoToLoading() => ChangeStateByName("Loading");

        /// <summary>No-network warning panel. ProjLobbyUIState.NetworkCheck = 16.</summary>
        protected void GoToNetworkCheck() => ChangeStateByName("NetworkCheck");

        // ── Backward-compat aliases ───────────────────────────────────────────

        /// <summary>Alias for GoToMain(). Kept for backward compatibility.</summary>
        protected void GoToBrowse()   => GoToMain();

        /// <summary>Alias for GoToInLobby(). Kept for backward compatibility.</summary>
        protected void GoToHosting()  => GoToInLobby();

        /// <summary>Alias for GoToInLobby(). Kept for backward compatibility.</summary>
        protected void GoToJoining()  => GoToInLobby();

        // ─────────────────────────────────────────────────────────────────────

        private void ChangeStateByName(string stateName)
        {
            if (_lobbyContext == null) return;

            int val = ResolveEnumValue(_lobbyContext.enumTypeName, stateName);
            if (val < 0)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"State '{stateName}' not found in enum '{_lobbyContext.enumTypeName}'.\n" +
                    "Make sure the state exists in the context SO and the generator has been run.",
                    nameof(LocalLobbyUIManager));
                return;
            }
            _lobbyContext.ChangeState(val);
        }

        private static int ResolveEnumValue(string enumTypeName, string memberName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(enumTypeName);
                if (t == null || !t.IsEnum) continue;
                try { return (int)System.Enum.Parse(t, memberName); }
                catch { return -1; }
            }
            return -1;
        }

        #endregion

        #region Protected Helpers — Action wrappers

        /// <summary>
        /// Transitions to the Searching state AND starts lobby discovery.
        /// This is the single call for the "Look for Lobbies" button.
        /// Also fires OnSearchStarted() so the concrete class can clear stale cards etc.
        /// </summary>
        protected void RequestGoToSearching()
        {
            RequestStopSearch();
            GoToSearching();
            RequestStartSearch();
            OnSearchStarted();
        }

        protected void RequestHost(LocalLobbyConfig config)
        {
            GoToLoading();
            _lobbyManager.StartHosting(config);
        }

        protected void RequestJoin(LocalLobbyData lobby)
        {
            GoToLoading();
            _lobbyManager.JoinLobby(lobby);
        }

        protected void RequestLeave()
        {
            _lobbyManager.LeaveLobby();
            GoToMain();
        }

        protected void RequestStopHosting()
        {
            _lobbyManager.StopHosting();
            GoToMain();
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

        protected bool AreAllReady() =>
            _lobbyManager.AreAllPlayersReady();

        #endregion

        #region Virtual Hooks — Override in subclass

        /// <summary>A new lobby was found during discovery scan.</summary>
        protected virtual void OnLobbyDiscovered(LocalLobbyData lobby) { }

        /// <summary>A previously discovered lobby timed out and was removed.</summary>
        protected virtual void OnLobbyRemoved(string lobbyKey) { }

        /// <summary>A player joined the current lobby.</summary>
        protected virtual void OnPlayerJoined(LocalLobbyPlayer player) { }

        /// <summary>A player left the current lobby.</summary>
        protected virtual void OnPlayerLeft(ulong clientId) { }

        /// <summary>A player's ready status changed.</summary>
        protected virtual void OnPlayerReadyChanged(LocalLobbyPlayer player) { }

        /// <summary>
        /// Result of StartHosting(). success=false → already navigated back to Main.
        /// On success → already navigated to InLobby.
        /// </summary>
        protected virtual void OnHostResult(bool success) { }

        /// <summary>
        /// Result of JoinLobby(). success=false → already navigated back to Main.
        /// On success → already navigated to InLobby.
        /// </summary>
        protected virtual void OnJoinResult(bool success) { }

        /// <summary>The host left or server shut down — already navigated to Main.</summary>
        protected virtual void OnLobbyDisbanded() { }

        /// <summary>
        /// WiFi / hotspot / mobile-data status changed.
        /// Values: WIFI_CONNECTED, HOTSPOT, MOBILE_DATA, NO_NETWORK.
        /// The concrete class decides whether to show the NetworkCheck panel.
        /// </summary>
        protected virtual void OnNetworkStatusChanged(string status) { }

        /// <summary>
        /// Game is starting — load your game scene here.
        /// Already navigated to Loading state.
        /// snapshot contains the final player list with team assignments.
        /// </summary>
        protected virtual void OnGameStartReceived(LocalLobbySnapshot snapshot) { }

        /// <summary>
        /// Called by RequestGoToSearching() after state has changed to Searching and
        /// search has started. Use to clear stale lobby cards or show a spinner.
        /// </summary>
        protected virtual void OnSearchStarted() { }

        #endregion
    }
}
