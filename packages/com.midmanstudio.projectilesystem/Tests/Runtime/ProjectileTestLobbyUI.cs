
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
        #region Inspector — Main Panel (mask = 1)

        [Header("Main Panel Elements")]
        [SerializeField] private TMP_InputField _playerNameInput;
        [SerializeField] private Button         _hostButton;
        [SerializeField] private Button         _lookForLobbiesButton;
        [SerializeField] private TMP_Text       _networkStatusText;

        #endregion

        #region Inspector — Searching Panel (mask = 2)

        [Header("Searching Panel Elements")]
        [SerializeField] private Transform    _lobbyListContainer;
        [SerializeField] private LobbyEntryCard _lobbyEntryCardPrefab;
        [SerializeField] private Button       _refreshButton;
        [SerializeField] private TMP_Text     _noLobbiesText;

        #endregion

        #region Inspector — In-Lobby Panel (mask = 4)

        [Header("In-Lobby Panel Elements")]
        [SerializeField] private TMP_Text     _lobbyTitleText;
        [SerializeField] private Transform    _playerListContainer;
        [SerializeField] private PlayerEntryCard _playerEntryCardPrefab;
        [SerializeField] private Button       _startButton;
        [SerializeField] private Button       _readyButton;
        [SerializeField] private TMP_Text     _readyButtonLabel;
        [SerializeField] private Button       _leaveButton;

        #endregion

        #region Inspector — Loading Panel (mask = 8)

        [Header("Loading Panel Elements")]
        [SerializeField] private TMP_Text _loadingText;

        #endregion

        #region Inspector — Network Check Panel (mask = 16)

        [Header("Network Check Panel Elements")]
        [SerializeField] private TMP_Text _noNetworkText;
        [SerializeField] private Button   _openWifiButton;
        [SerializeField] private Button   _openHotspotButton;

        #endregion

        #region Inspector — Config

        [Header("Lobby Config")]
        [SerializeField] private int _maxPlayers    = 4;
        [SerializeField] private int _serverPort    = 7777;
        [SerializeField] private int _broadcastPort = 7778;

        #endregion

        #region Runtime State

        private readonly Dictionary<string, LobbyEntryCard>  _lobbyCards  = new(8);
        private readonly Dictionary<ulong, PlayerEntryCard>  _playerCards = new(8);
        private bool _localReady;

        #endregion

        #region Lifecycle

        protected override void Awake()
        {
            base.Awake();

            _hostButton          ?.onClick.AddListener(OnHostClicked);
            _lookForLobbiesButton?.onClick.AddListener(OnLookForLobbiesClicked);
            _playerNameInput     ?.onEndEdit.AddListener(RequestPlayerName);

            _refreshButton?.onClick.AddListener(OnRefreshClicked);

            _startButton ?.onClick.AddListener(OnStartClicked);
            _readyButton ?.onClick.AddListener(OnReadyClicked);
            _leaveButton ?.onClick.AddListener(OnLeaveClicked);

            _openWifiButton   ?.onClick.AddListener(() => _lobbyManager?.OpenWiFiSettings());
            _openHotspotButton?.onClick.AddListener(() => _lobbyManager?.OpenHotspotSettings());
        }

        private void Start()
        {
            if (_lobbyManager == null)
            {
                MID_Logger.LogError(_logLevel,
                    "LocalLobbyManager is null — lobby UI will not work.",
                    nameof(ProjectileTestLobbyUI));
                return;
            }

            if (_lobbyManager.IsInitialized)
                GoToMain();
            else
                _lobbyManager.OnInitialized += OnManagerInitialized;
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
            GoToMain();
        }

        #endregion

        #region LocalLobbyUIManager Overrides

        protected override void OnSearchStarted()
        {
            ClearLobbyCards();
            SetText(_noLobbiesText, "Searching for nearby lobbies...");
        }

        protected override void OnLobbyDiscovered(LocalLobbyData lobby)
        {
            if (_lobbyCards.ContainsKey(lobby.Key)) return;
            if (_lobbyListContainer == null || _lobbyEntryCardPrefab == null) return;

            SetText(_noLobbiesText, "");

            var card = Instantiate(_lobbyEntryCardPrefab, _lobbyListContainer);
            card.Populate(lobby, OnJoinLobbyRequested);
            _lobbyCards[lobby.Key] = card;
        }

        protected override void OnLobbyRemoved(string lobbyKey)
        {
            if (!_lobbyCards.TryGetValue(lobbyKey, out var card)) return;
            if (card != null) Destroy(card.gameObject);
            _lobbyCards.Remove(lobbyKey);

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
            if (success)
            {
                string name = _lobbyManager?.PlayerName ?? "Host";
                SetText(_lobbyTitleText, $"{name}'s Test Lobby");
                SetStartButtonVisible(true);
            }
            else
            {
                SetNetworkStatus("Failed to start host — check WiFi or hotspot.");
            }
        }

        protected override void OnJoinResult(bool success)
        {
            if (success)
            {
                SetText(_lobbyTitleText, "Test Lobby");
                SetStartButtonVisible(false);
            }
            else
            {
                SetNetworkStatus("Failed to join — the host may have left.");
            }
        }

        protected override void OnLobbyDisbanded()
        {
            ClearPlayerCards();
            _localReady = false;
            SetText(_readyButtonLabel, "Ready");
        }

        protected override void OnNetworkStatusChanged(string status)
        {
            bool hasLan = status is "WIFI_CONNECTED" or "HOTSPOT";

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
            SetText(_loadingText, "Starting test session...");
            MID_Logger.LogInfo(_logLevel,
                $"Game start — {snapshot.Players.Count} players.",
                nameof(ProjectileTestLobbyUI));
            Invoke(nameof(HideUI), 0.5f);
        }

        #endregion

        #region Button Handlers

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

            SetText(_loadingText, "Starting host...");
            RequestHost(cfg);
        }

        private void OnLookForLobbiesClicked() => RequestGoToSearching();

        private void OnRefreshClicked() => RequestGoToSearching();

        private void OnJoinLobbyRequested(LocalLobbyData lobby)
        {
            SetText(_loadingText, $"Joining {lobby.LobbyName}...");
            RequestStopSearch();
            RequestJoin(lobby);
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
                RequestStopHosting();
            else
                RequestLeave();
        }

        #endregion

        #region Helpers

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

        private void SetNetworkStatus(string msg) => SetText(_networkStatusText, msg);

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
            // FIX: removed \u2713 (✓) — not in LiberationSans SDF, caused TMP glyph warning
            // on the "NetStat" text object.
            "WIFI_CONNECTED" => "WiFi Connected",
            "HOTSPOT"        => "Hotspot Active — others can join",
            "MOBILE_DATA"    => "Mobile Data only — WiFi needed for LAN",
            "NO_NETWORK"     => "No Network",
            _                => raw
        };

        #endregion
    }
}
