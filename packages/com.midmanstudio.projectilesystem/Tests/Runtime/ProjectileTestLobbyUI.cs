// packages/com.midmanstudio.projectilesystem/Tests/Runtime/ProjectileTestLobbyUI.cs
//
// COMPLETE REWRITE — matches intended flow described in design:
//
//   Canvas boots → Main state (player name, Host button, Look for Lobbies button)
//       │
//       ├─ "Host" ──────────────────────────────► Loading → InLobby (as host)
//       │                                                      │
//       └─ "Look for Lobbies" ──────────────────► Searching   │  Ready/Start/Leave
//                                                   │          │
//                                                   └─ card.Join ► Loading → InLobby (as client)
//
//   InLobby (host):   player list, Start Game, Ready, Leave
//   InLobby (client): player list, Ready, Leave   (Start hidden)
//   Network lost:     NetworkCheck panel with Open WiFi / Hotspot buttons
//
// PANEL SETUP (do this in the Inspector):
//   Each panel needs a MID_UIElement + MID_UIStateVisibility component.
//   Set _showWhenMask on each panel's MID_UIStateVisibility:
//     _panelMain         → 1   (ProjLobbyUIState.Main)
//     _panelBrowse       → 2   (ProjLobbyUIState.Searching)
//     _panelInLobby      → 4   (ProjLobbyUIState.InLobby)
//     _panelLoading      → 8   (ProjLobbyUIState.Loading)
//     _panelNetworkCheck → 16  (ProjLobbyUIState.NetworkCheck)
//
// HOW PANELS SHOW/HIDE:
//   This class calls GoTo*() methods from LocalLobbyUIManager.
//   Those call _lobbyContext.ChangeState(value).
//   Each panel's MID_UIStateVisibility reacts automatically — no SetActive() here.
//
// RACE-CONDITION FIX:
//   LocalLobbyManager has a 0.1s startup delay in InitializeAsync().
//   We subscribe to OnInitialized and navigate to Main only after that fires,
//   preventing StartSearching() from hitting the _isInitialized guard and silently failing.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using MidManStudio.Core.Logging;
using MidManStudio.Core.UIState;
using MidManStudio.Netcode.LocalMultiplayer;
using MidManStudio.Netcode.UI;

namespace TestGame
{
    [RequireComponent(typeof(Canvas))]
    public class ProjectileTestLobbyUI : LocalLobbyUIManager
    {
        // ── Main panel UI elements ────────────────────────────────────────────
        // Panel mask = 1  (ProjLobbyUIState.Main)
        // Children: player name input, host button, look-for-lobbies button

        [Header("Main Panel Elements")]
        [Tooltip("Text input in the Main panel for the player's display name.")]
        [SerializeField] private TMP_InputField _playerNameInput;

        [Tooltip("'Host' button in the Main panel.")]
        [SerializeField] private Button _hostButton;

        [Tooltip("'Look for Lobbies' button in the Main panel. " +
                 "Transitions to the Searching state and starts UDP discovery.")]
        [SerializeField] private Button _lookForLobbiesButton;

        [Tooltip("Network-status label shown in the Main panel (optional).")]
        [SerializeField] private TMP_Text _networkStatusText;

        // ── Searching / Browse panel UI elements ──────────────────────────────
        // Panel mask = 2  (ProjLobbyUIState.Searching)
        // Children: scrollable lobby card list, refresh button

        [Header("Searching Panel Elements")]
        [Tooltip("Scroll-view content container where LobbyEntryCard prefabs are instantiated.")]
        [SerializeField] private Transform _lobbyListContainer;

        [Tooltip("Prefab with a LobbyEntryCard component.")]
        [SerializeField] private LobbyEntryCard _lobbyEntryCardPrefab;

        [Tooltip("'Refresh' button — restarts discovery and clears stale cards.")]
        [SerializeField] private Button _refreshButton;

        [Tooltip("Label shown when no lobbies have been found yet.")]
        [SerializeField] private TMP_Text _noLobbiesText;

        // ── In-lobby panel UI elements ────────────────────────────────────────
        // Panel mask = 4  (ProjLobbyUIState.InLobby)
        // Shared by host and joining client. Start button is hidden for clients.

        [Header("In-Lobby Panel Elements")]
        [Tooltip("Shows the lobby name / host name at the top.")]
        [SerializeField] private TMP_Text _lobbyTitleText;

        [Tooltip("Container where PlayerEntryCard prefabs are instantiated.")]
        [SerializeField] private Transform _playerListContainer;

        [Tooltip("Prefab with a PlayerEntryCard component.")]
        [SerializeField] private PlayerEntryCard _playerEntryCardPrefab;

        [Tooltip("'Start Game' button — only shown to the host.")]
        [SerializeField] private Button _startButton;

        [Tooltip("'Ready / Unready' toggle button.")]
        [SerializeField] private Button _readyButton;

        [Tooltip("Label inside the Ready button (text changes between 'Ready' and 'Unready').")]
        [SerializeField] private TMP_Text _readyButtonLabel;

        [Tooltip("'Leave' button — works for both host (stops lobby) and client (leaves).")]
        [SerializeField] private Button _leaveButton;

        // ── Loading panel UI elements ─────────────────────────────────────────
        // Panel mask = 8  (ProjLobbyUIState.Loading)

        [Header("Loading Panel Elements")]
        [SerializeField] private TMP_Text _loadingText;

        // ── Network Check panel UI elements ───────────────────────────────────
        // Panel mask = 16  (ProjLobbyUIState.NetworkCheck)

        [Header("Network Check Panel Elements")]
        [SerializeField] private TMP_Text _noNetworkText;
        [SerializeField] private Button   _openWifiButton;
        [SerializeField] private Button   _openHotspotButton;

        // ── Lobby config ──────────────────────────────────────────────────────

        [Header("Lobby Config")]
        [SerializeField] private int _maxPlayers    = 4;
        [SerializeField] private int _serverPort    = 7777;
        [SerializeField] private int _broadcastPort = 7778;

        // ─────────────────────────────────────────────────────────────────────
        //  Runtime state
        // ─────────────────────────────────────────────────────────────────────

        private readonly Dictionary<string, LobbyEntryCard>  _lobbyCards  = new(8);
        private readonly Dictionary<ulong, PlayerEntryCard>  _playerCards = new(8);
        private bool _localReady;

        // ─────────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake(); // subscribes to lobby manager events

            // Main panel buttons
            _hostButton          ?.onClick.AddListener(OnHostClicked);
            _lookForLobbiesButton?.onClick.AddListener(OnLookForLobbiesClicked);
            _playerNameInput     ?.onEndEdit.AddListener(RequestPlayerName);

            // Searching panel buttons
            _refreshButton?.onClick.AddListener(OnRefreshClicked);

            // In-lobby panel buttons
            _startButton ?.onClick.AddListener(OnStartClicked);
            _readyButton ?.onClick.AddListener(OnReadyClicked);
            _leaveButton ?.onClick.AddListener(OnLeaveClicked);

            // Network check panel buttons
            _openWifiButton   ?.onClick.AddListener(() => _lobbyManager?.OpenWiFiSettings());
            _openHotspotButton?.onClick.AddListener(() => _lobbyManager?.OpenHotspotSettings());
        }

        private void Start()
        {
            // Do NOT call GoToMain() or RequestStartSearch() here directly.
            // LocalLobbyManager.InitializeAsync() has a 0.1s WaitForSeconds before
            // _isInitialized becomes true. Any search call before that is silently dropped.
            // We subscribe to OnInitialized and navigate then.

            if (_lobbyManager == null)
            {
                MID_Logger.LogError(_logLevel,
                    "LocalLobbyManager is null — lobby UI will not work.",
                    nameof(ProjectileTestLobbyUI));
                return;
            }

            if (_lobbyManager.IsInitialized)
            {
                // Scene was reloaded or manager was already up — jump straight in.
                GoToMain();
            }
            else
            {
                _lobbyManager.OnInitialized += OnManagerInitialized;
            }
        }

        protected override void OnDestroy()
        {
            if (_lobbyManager != null)
                _lobbyManager.OnInitialized -= OnManagerInitialized;

            base.OnDestroy();
        }

        private void OnManagerInitialized()
        {
            _lobbyManager.OnInitialized -= OnManagerInitialized;

            // Manager is ready — show the main screen. Don't start search yet;
            // the user explicitly clicks "Look for Lobbies" to trigger that.
            GoToMain();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  LocalLobbyUIManager virtual hooks — data / card management only.
        //  Panel switching is handled by the state context; DO NOT call SetActive here.
        // ─────────────────────────────────────────────────────────────────────

        protected override void OnSearchStarted()
        {
            // Clear stale cards from any previous search session.
            ClearLobbyCards();
            SetText(_noLobbiesText, "Searching for nearby lobbies…");
        }

        protected override void OnLobbyDiscovered(LocalLobbyData lobby)
        {
            if (_lobbyCards.ContainsKey(lobby.Key)) return;
            if (_lobbyListContainer == null || _lobbyEntryCardPrefab == null) return;

            // Clear "no lobbies" text as soon as the first card appears.
            SetText(_noLobbiesText, "");

            var card = Instantiate(_lobbyEntryCardPrefab, _lobbyListContainer);
            card.Populate(lobby, OnJoinLobbyRequested);
            _lobbyCards[lobby.Key] = card;

            MID_Logger.LogDebug(_logLevel, $"Lobby card added: {lobby}",
                nameof(ProjectileTestLobbyUI));
        }

        protected override void OnLobbyRemoved(string lobbyKey)
        {
            if (!_lobbyCards.TryGetValue(lobbyKey, out var card)) return;
            if (card != null) Destroy(card.gameObject);
            _lobbyCards.Remove(lobbyKey);

            // Put the "no lobbies" hint back if the list is now empty.
            if (_lobbyCards.Count == 0)
                SetText(_noLobbiesText, "No lobbies found. Try refreshing.");
        }

        protected override void OnPlayerJoined(LocalLobbyPlayer player)
        {
            if (_playerCards.ContainsKey(player.ClientId)) return;
            if (_playerListContainer == null || _playerEntryCardPrefab == null) return;

            var card = Instantiate(_playerEntryCardPrefab, _playerListContainer);
            card.Populate(player);
            _playerCards[player.ClientId] = card;
            RefreshStartButton();

            MID_Logger.LogDebug(_logLevel, $"Player card added: {player}",
                nameof(ProjectileTestLobbyUI));
        }

        protected override void OnPlayerLeft(ulong clientId)
        {
            if (!_playerCards.TryGetValue(clientId, out var card)) return;
            if (card != null) Destroy(card.gameObject);
            _playerCards.Remove(clientId);
            RefreshStartButton();
        }

        protected override void OnPlayerReadyChanged(LocalLobbyPlayer player)
        {
            if (_playerCards.TryGetValue(player.ClientId, out var card))
                card.Refresh(player);
            RefreshStartButton();
        }

        protected override void OnHostResult(bool success)
        {
            // Base class already called GoToInLobby() on success or GoToMain() on failure.
            if (success)
            {
                string name = _lobbyManager?.PlayerName ?? "Host";
                SetText(_lobbyTitleText, $"{name}'s Test Lobby");
                SetStartButtonVisible(true); // only host sees the Start button
            }
            else
            {
                SetNetworkStatus("Failed to start host — check WiFi or hotspot.");
            }
        }

        protected override void OnJoinResult(bool success)
        {
            // Base class already called GoToInLobby() on success or GoToMain() on failure.
            if (success)
            {
                SetText(_lobbyTitleText, "Test Lobby");
                SetStartButtonVisible(false); // clients don't see Start
            }
            else
            {
                SetNetworkStatus("Failed to join — the host may have left.");
            }
        }

        protected override void OnLobbyDisbanded()
        {
            // Base class already called GoToMain(). Clean up card state.
            ClearPlayerCards();
            _localReady = false;
            SetText(_readyButtonLabel, "Ready");
        }

        protected override void OnNetworkStatusChanged(string status)
        {
            bool hasLan = status is "WIFI_CONNECTED" or "HOTSPOT";
            int  cur    = _lobbyContext != null ? _lobbyContext.CurrentState : 0;

            // Only hijack to the NetworkCheck panel if we're on a browsing screen
            // (Main or Searching). Don't interrupt an in-progress lobby.
            bool onBrowseScreen = IsStateActive(ProjLobbyUIState.Main)
                               || IsStateActive(ProjLobbyUIState.Searching)
                               || IsStateActive(ProjLobbyUIState.NetworkCheck);

            if (!hasLan && onBrowseScreen)
            {
                GoToNetworkCheck();
                SetText(_noNetworkText, status == "MOBILE_DATA"
                    ? "WiFi required for LAN play.\nMobile data cannot host or join."
                    : "No network connection detected.");
            }
            else if (hasLan && IsStateActive(ProjLobbyUIState.NetworkCheck))
            {
                GoToMain();
            }

            SetNetworkStatus(FriendlyStatus(status));
        }

        protected override void OnGameStartReceived(LocalLobbySnapshot snapshot)
        {
            // Base class already called GoToLoading().
            SetText(_loadingText, "Starting test session…");

            MID_Logger.LogInfo(_logLevel,
                $"Game start — {snapshot.Players.Count} players.",
                nameof(ProjectileTestLobbyUI));

            Invoke(nameof(HideUI), 0.5f);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Button handlers
        // ─────────────────────────────────────────────────────────────────────

        private void OnHostClicked()
        {
            string playerName = !string.IsNullOrWhiteSpace(_playerNameInput?.text)
                ? _playerNameInput.text.Trim()
                : "Player";

            RequestPlayerName(playerName);

            var cfg = new LocalLobbyConfig
            {
                LobbyName     = $"{playerName}'s Test",
                MaxPlayers    = _maxPlayers,
                GameMode      = "ProjectileTest",
                GameMap       = "TestScene",
                ServerPort    = _serverPort,
                BroadcastPort = _broadcastPort
            };

            SetText(_loadingText, "Starting host…");
            RequestHost(cfg); // → GoToLoading(), then StartHosting(), then OnHostResult()
        }

        private void OnLookForLobbiesClicked()
        {
            // RequestGoToSearching() from base class:
            //   1. Stops any running search
            //   2. Transitions to Searching state (shows the lobby list panel)
            //   3. Starts UDP discovery
            //   4. Calls OnSearchStarted() (clears cards, sets "Searching…" text)
            RequestGoToSearching();
        }

        private void OnRefreshClicked()
        {
            // Same as pressing Look for Lobbies — restarts search from scratch.
            RequestGoToSearching();
        }

        private void OnJoinLobbyRequested(LocalLobbyData lobby)
        {
            SetText(_loadingText, $"Joining {lobby.LobbyName}…");
            RequestStopSearch(); // stop broadcasting discovery requests
            RequestJoin(lobby);  // → GoToLoading(), then JoinLobby(), then OnJoinResult()
        }

        private void OnStartClicked()
        {
            if (!AreAllReady())
            {
                SetNetworkStatus("All players must be ready before starting.");
                return;
            }
            RequestGameStart();
        }

        private void OnReadyClicked()
        {
            if (NetworkManager.Singleton == null
                || !NetworkManager.Singleton.IsConnectedClient) return;

            _localReady = !_localReady;
            ulong id = NetworkManager.Singleton.LocalClientId;
            RequestSetReady(id, _localReady);
            SetText(_readyButtonLabel, _localReady ? "Unready" : "Ready");
        }

        private void OnLeaveClicked()
        {
            ClearPlayerCards();
            _localReady = false;
            SetText(_readyButtonLabel, "Ready");

            if (_lobbyManager != null && _lobbyManager.IsHosting)
                RequestStopHosting(); // → shuts lobby, calls GoToMain()
            else
                RequestLeave();       // → leaves lobby, calls GoToMain()
        }

        // ─────────────────────────────────────────────────────────────────────
        //  UI helpers
        // ─────────────────────────────────────────────────────────────────────

        private void RefreshStartButton()
        {
            if (_startButton == null) return;

            bool isHost   = _lobbyManager != null && _lobbyManager.IsHosting;
            bool allReady = AreAllReady() && GetPlayers().Count >= 1;

            _startButton.gameObject.SetActive(isHost);
            _startButton.interactable = allReady;
        }

        private void SetStartButtonVisible(bool visible)
        {
            if (_startButton != null)
                _startButton.gameObject.SetActive(visible);
        }

        private void SetNetworkStatus(string msg)
        {
            SetText(_networkStatusText, msg);
            MID_Logger.LogDebug(_logLevel, $"[Status] {msg}",
                nameof(ProjectileTestLobbyUI));
        }

        /// <summary>Returns true when the current context state matches the given flag.</summary>
        private bool IsStateActive(ProjLobbyUIState state) =>
            _lobbyContext != null && (_lobbyContext.CurrentState & (int)state) != 0;

        private void ClearPlayerCards()
        {
            foreach (var c in _playerCards.Values)
                if (c != null) Destroy(c.gameObject);
            _playerCards.Clear();
        }

        private void ClearLobbyCards()
        {
            foreach (var c in _lobbyCards.Values)
                if (c != null) Destroy(c.gameObject);
            _lobbyCards.Clear();
        }

        private void HideUI() => gameObject.SetActive(false);

        private static void SetText(TMP_Text t, string v)
        {
            if (t != null) t.text = v;
        }

        private static string FriendlyStatus(string raw) => raw switch
        {
            "WIFI_CONNECTED" => "WiFi Connected ✓",
            "HOTSPOT"        => "Hotspot Active — others can join",
            "MOBILE_DATA"    => "Mobile Data only — WiFi needed for LAN",
            "NO_NETWORK"     => "No Network ✗",
            _                => raw
        };
    }
}
