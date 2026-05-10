// packages/com.midmanstudio.netcode/Runtime/LocalMultiplayer/LocalLobbyUIManager.cs
// REWRITTEN: replaces custom LobbyUIState enum with MID_UIStateContext from utilities.
//
// SETUP:
//   1. Create a MID_UIStateContext SO asset, set contextName = "Lobby"
//   2. Add states: NetworkCheck, Browse, Hosting, Joining, Loading
//   3. Run: MidManStudio > Utilities > UI State Context Generator
//      → produces LobbyUIState.cs enum
//   4. Assign the context asset to the LobbyContext field in inspector
//   5. Subclass this and override the On* virtual hooks for your game's panels

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.UIState;
using MidManStudio.Netcode.LocalMultiplayer;

namespace MidManStudio.Netcode.UI
{
    [RequireComponent(typeof(Canvas))]
    public abstract class LocalLobbyUIManager : MonoBehaviour
    {
        #region Inspector

        [Header("Lobby Manager")]
        [SerializeField] protected LocalLobbyManager _lobbyManager;

        [Header("Network Monitor")]
        [SerializeField] protected MobileNetworkStatusMonitor _networkMonitor;

        [Header("UI State Context")]
        [Tooltip("Assign the 'Lobby' MID_UIStateContext SO here.\n" +
                 "Expected states: NetworkCheck, Browse, Hosting, Joining, Loading.\n" +
                 "Run the UI State Context Generator after adding states to the SO.")]
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
                    "No LobbyContext assigned. State transitions will still fire events " +
                    "but nothing will drive panel show/hide automatically.\n" +
                    "Create a MID_UIStateContext with contextName='Lobby' and assign it here.",
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

            SetLoading(false);
            if (success) GoToHosting();
            OnHostResult(success);
        }

        private void HandleJoinResult(bool success)
        {
            MID_Logger.LogInfo(_logLevel, $"Join result: {success}",
                nameof(LocalLobbyUIManager));

            SetLoading(false);
            if (success) GoToJoining();
            OnJoinResult(success);
        }

        private void HandleLobbyDisbanded()
        {
            MID_Logger.LogInfo(_logLevel, "Lobby disbanded.",
                nameof(LocalLobbyUIManager));

            GoToBrowse();
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
        // These helpers let you drive state from code without knowing the int values.
        // After running the generator, you can also call
        //   _lobbyContext.ChangeState((int)LobbyUIState.Browse)
        // directly from your subclass.

        /// <summary>Transition to a state by raw int (from generated LobbyUIState enum).</summary>
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

        // Convenience named transitions — these look up the enum value by name
        // via reflection so they work regardless of the generated int value.

        protected void GoToNetworkCheck() => ChangeStateByName("NetworkCheck");
        protected void GoToBrowse()        => ChangeStateByName("Browse");
        protected void GoToHosting()       => ChangeStateByName("Hosting");
        protected void GoToJoining()       => ChangeStateByName("Joining");
        protected void GoToLoading()       => ChangeStateByName("Loading");

        private void SetLoading(bool loading)
        {
            if (loading) GoToLoading();
        }

        /// <summary>
        /// Looks up a state value by name from the generated LobbyUIState enum
        /// via the context's enumTypeName — no hard dependency on the generated type.
        /// </summary>
        private void ChangeStateByName(string stateName)
        {
            if (_lobbyContext == null) return;

            int val = ResolveEnumValue(_lobbyContext.enumTypeName, stateName);
            if (val < 0)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"State '{stateName}' not found in enum '{_lobbyContext.enumTypeName}'.\n" +
                    "Make sure the state exists in the LobbyContext and the generator has been run.",
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

        /// <summary>Result of StartHosting(). success=false means show an error.</summary>
        protected virtual void OnHostResult(bool success) { }

        /// <summary>Result of JoinLobby(). success=false means show an error.</summary>
        protected virtual void OnJoinResult(bool success) { }

        /// <summary>The host left — return to browse state.</summary>
        protected virtual void OnLobbyDisbanded() { }

        /// <summary>
        /// WiFi / hotspot / mobile-data status changed.
        /// Values: WIFI_CONNECTED, HOTSPOT, MOBILE_DATA, NO_NETWORK.
        /// </summary>
        protected virtual void OnNetworkStatusChanged(string status) { }

        /// <summary>
        /// Game is starting — load your game scene here.
        /// snapshot contains the final player list with team assignments.
        /// </summary>
        protected virtual void OnGameStartReceived(LocalLobbySnapshot snapshot) { }

        #endregion

        #region Protected Helpers — Action wrappers

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
            GoToBrowse();
        }

        protected void RequestStopHosting()
        {
            _lobbyManager.StopHosting();
            GoToBrowse();
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

        protected List<LocalLobbyPlayer> GetPlayers() =>
            _lobbyManager.GetPlayers();

        protected bool AreAllReady() =>
            _lobbyManager.AreAllPlayersReady();

        #endregion
    }
}
