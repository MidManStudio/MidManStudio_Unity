// packages/com.midmanstudio.projectilesystem/Tests/Runtime/ProjectileTestLobbyUI.cs
//
// FIXES vs previous version:
//   • Start() no longer calls RequestStartSearch() directly — that was a race
//     condition because LocalLobbyManager._isInitialized is false for 0.1 s
//     after Awake. Now we subscribe to OnInitialized and start search there.
//   • BeginSearch() is the single search entry point: it stops any running
//     search, clears lobby cards, then calls RequestStartSearch().
//   • BeginSearch() is called on: initialization, returning to browse panel
//     (OnLobbyDisbanded, OnJoinResult=false, OnHostResult=false, OnLeaveClicked).
//   • OnHostResult=false now goes back to browse AND restarts search.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using MidManStudio.Core.Logging;
using MidManStudio.Netcode.LocalMultiplayer;
using MidManStudio.Netcode.UI;

namespace TestGame
{
    [RequireComponent(typeof(Canvas))]
    public class ProjectileTestLobbyUI : LocalLobbyUIManager
    {
        // ── Panels ────────────────────────────────────────────────────────────

        [Header("Panels — wire GameObjects here")]
        [SerializeField] private GameObject _panelNetworkCheck;
        [SerializeField] private GameObject _panelBrowse;
        [SerializeField] private GameObject _panelHosting;
        [SerializeField] private GameObject _panelJoining;
        [SerializeField] private GameObject _panelLoading;

        // ── Browse panel ──────────────────────────────────────────────────────

        [Header("Browse Panel")]
        [SerializeField] private Button         _hostButton;
        [SerializeField] private TMP_InputField _playerNameInput;
        [SerializeField] private TMP_Text       _networkStatusText;
        [SerializeField] private Transform      _lobbyListContainer;
        [Tooltip("Prefab must have a LobbyEntryCard component.")]
        [SerializeField] private LobbyEntryCard _lobbyEntryCardPrefab;

        // ── In-lobby panels (Hosting + Joining) ───────────────────────────────

        [Header("In-Lobby Panels (Hosting + Joining share these)")]
        [SerializeField] private TMP_Text  _lobbyTitleText;
        [SerializeField] private Transform _playerListContainer;
        [Tooltip("Prefab must have a PlayerEntryCard component.")]
        [SerializeField] private PlayerEntryCard _playerEntryCardPrefab;
        [SerializeField] private Button    _startButton;
        [SerializeField] private Button    _readyButton;
        [SerializeField] private Button    _leaveButton;
        [SerializeField] private TMP_Text  _readyButtonLabel;

        // ── Network Check panel ───────────────────────────────────────────────

        [Header("Network Check Panel")]
        [SerializeField] private TMP_Text _noNetworkText;
        [SerializeField] private Button   _openWifiButton;
        [SerializeField] private Button   _openHotspotButton;

        // ── Loading panel ─────────────────────────────────────────────────────

        [Header("Loading Panel")]
        [SerializeField] private TMP_Text _loadingText;

        // ── Lobby config ──────────────────────────────────────────────────────

        [Header("Lobby Config")]
        [SerializeField] private int _maxPlayers    = 4;
        [SerializeField] private int _serverPort    = 7777;
        [SerializeField] private int _broadcastPort = 7778;

        // ─────────────────────────────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────────────────────────────

        private readonly Dictionary<string, LobbyEntryCard>  _lobbyCards  = new(8);
        private readonly Dictionary<ulong, PlayerEntryCard>  _playerCards = new(8);
        private bool _localReady;

        // ─────────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();

            _hostButton?.onClick.AddListener(OnHostClicked);
            _playerNameInput?.onEndEdit.AddListener(RequestPlayerName);

            _startButton?.onClick.AddListener(OnStartClicked);
            _readyButton?.onClick.AddListener(OnReadyClicked);
            _leaveButton?.onClick.AddListener(OnLeaveClicked);

            _openWifiButton?   .onClick.AddListener(() => _lobbyManager?.OpenWiFiSettings());
            _openHotspotButton?.onClick.AddListener(() => _lobbyManager?.OpenHotspotSettings());

            SetAllPanelsHidden();
        }

        private void Start()
        {
            // FIX: do NOT call RequestStartSearch() here.
            // LocalLobbyManager.InitializeAsync() has a 0.1 s WaitForSeconds before it
            // sets _isInitialized = true. Calling StartSearching() before that returns
            // immediately due to the guard check, so discovery never starts.
            // Instead we subscribe to OnInitialized, which fires after the delay.
            ShowPanel(_panelBrowse);

            if (_lobbyManager != null)
            {
                if (_lobbyManager.IsInitialized)
                {
                    // Already initialized (e.g. scene was reloaded) — start immediately.
                    BeginSearch();
                }
                else
                {
                    // Normal case: wait for the manager's 0.1 s init delay.
                    _lobbyManager.OnInitialized += OnLobbyManagerInitialized;
                }
            }
            else
            {
                MID_Logger.LogError(_logLevel,
                    "LocalLobbyManager reference is null in ProjectileTestLobbyUI.",
                    nameof(ProjectileTestLobbyUI));
            }
        }

        protected override void OnDestroy()
        {
            // Unsubscribe in case Start fired but OnInitialized hadn't yet
            if (_lobbyManager != null)
                _lobbyManager.OnInitialized -= OnLobbyManagerInitialized;

            base.OnDestroy();
        }

        // Called once when the lobby manager finishes its startup delay.
        private void OnLobbyManagerInitialized()
        {
            _lobbyManager.OnInitialized -= OnLobbyManagerInitialized;
            BeginSearch();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Search helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Single entry point for starting/restarting lobby discovery.
        /// Clears the lobby card list, stops any running search, then starts a fresh one.
        /// Call this whenever the browse panel becomes visible.
        /// </summary>
        private void BeginSearch()
        {
            ClearLobbyCards();
            RequestStopSearch();
            RequestStartSearch();
            SetStatusText("Searching for nearby lobbies…");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  LocalLobbyUIManager hooks
        // ─────────────────────────────────────────────────────────────────────

        protected override void OnLobbyDiscovered(LocalLobbyData lobby)
        {
            if (_lobbyCards.ContainsKey(lobby.Key)) return;
            if (_lobbyListContainer == null || _lobbyEntryCardPrefab == null) return;

            var card = Instantiate(_lobbyEntryCardPrefab, _lobbyListContainer);
            card.Populate(lobby, JoinLobby);
            _lobbyCards[lobby.Key] = card;

            MID_Logger.LogDebug(_logLevel,
                $"Lobby card added: {lobby}", nameof(ProjectileTestLobbyUI));
        }

        protected override void OnLobbyRemoved(string lobbyKey)
        {
            if (!_lobbyCards.TryGetValue(lobbyKey, out var card)) return;
            if (card != null) Destroy(card.gameObject);
            _lobbyCards.Remove(lobbyKey);
        }

        protected override void OnPlayerJoined(LocalLobbyPlayer player)
        {
            if (_playerCards.ContainsKey(player.ClientId)) return;
            if (_playerListContainer == null || _playerEntryCardPrefab == null) return;

            var card = Instantiate(_playerEntryCardPrefab, _playerListContainer);
            card.Populate(player);
            _playerCards[player.ClientId] = card;

            RefreshStartButton();

            MID_Logger.LogDebug(_logLevel,
                $"Player card added: {player}", nameof(ProjectileTestLobbyUI));
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
                ShowPanel(_panelHosting);
                string name = _lobbyManager?.PlayerName ?? "Host";
                SetText(_lobbyTitleText, $"Hosting: {name}'s Test Lobby");
                SetStartButtonVisible(true);
            }
            else
            {
                // FIX: restart search when falling back to browse
                ShowPanel(_panelBrowse);
                SetStatusText("Host failed — check WiFi / hotspot.");
                BeginSearch();
            }
        }

        protected override void OnJoinResult(bool success)
        {
            if (success)
            {
                ShowPanel(_panelJoining);
                SetText(_lobbyTitleText, "In Lobby");
                SetStartButtonVisible(false);
            }
            else
            {
                // FIX: restart search when falling back to browse
                ShowPanel(_panelBrowse);
                SetStatusText("Join failed — host may have left.");
                BeginSearch();
            }
        }

        protected override void OnLobbyDisbanded()
        {
            ClearPlayerCards();
            ShowPanel(_panelBrowse);
            SetStatusText("Lobby disbanded by host.");
            // FIX: restart search so the list isn't permanently empty
            BeginSearch();
        }

        protected override void OnNetworkStatusChanged(string status)
        {
            SetStatusText(FriendlyStatus(status));

            bool hasLan = status is "WIFI_CONNECTED" or "HOTSPOT";

            if (!hasLan && (IsShowingPanel(_panelBrowse) || IsShowingPanel(_panelNetworkCheck)))
            {
                ShowPanel(_panelNetworkCheck);
                SetText(_noNetworkText, status == "MOBILE_DATA"
                    ? "WiFi required for LAN play.\nMobile data cannot host or join."
                    : "No network connection detected.");
            }
            else if (hasLan && IsShowingPanel(_panelNetworkCheck))
            {
                ShowPanel(_panelBrowse);
                BeginSearch();
            }
        }

        protected override void OnGameStartReceived(LocalLobbySnapshot snapshot)
        {
            MID_Logger.LogInfo(_logLevel,
                $"Game start — {snapshot.Players.Count} players.",
                nameof(ProjectileTestLobbyUI));

            ShowPanel(_panelLoading);
            SetText(_loadingText, "Starting test session…");

            Invoke(nameof(HideUI), 0.5f);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Button handlers
        // ─────────────────────────────────────────────────────────────────────

        private void OnHostClicked()
        {
            ShowPanel(_panelLoading);
            SetText(_loadingText, "Starting host…");

            string playerName = (!string.IsNullOrWhiteSpace(_playerNameInput?.text))
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

            RequestHost(cfg);
        }

        private void JoinLobby(LocalLobbyData lobby)
        {
            ShowPanel(_panelLoading);
            SetText(_loadingText, $"Joining {lobby.LobbyName}…");
            RequestJoin(lobby);
        }

        private void OnStartClicked()
        {
            if (!AreAllReady())
            {
                SetStatusText("All players must be ready before starting.");
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
            _localReady = false;
            SetText(_readyButtonLabel, "Ready");
            ClearPlayerCards();
            ClearLobbyCards();

            if (_lobbyManager != null && _lobbyManager.IsHosting)
                RequestStopHosting();
            else
                RequestLeave();

            // FIX: restart search after returning to browse
            ShowPanel(_panelBrowse);
            BeginSearch();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  UI helpers
        // ─────────────────────────────────────────────────────────────────────

        private void ShowPanel(GameObject active)
        {
            _panelNetworkCheck?.SetActive(active == _panelNetworkCheck);
            _panelBrowse      ?.SetActive(active == _panelBrowse);
            _panelHosting     ?.SetActive(active == _panelHosting);
            _panelJoining     ?.SetActive(active == _panelJoining);
            _panelLoading     ?.SetActive(active == _panelLoading);
        }

        private void SetAllPanelsHidden()
        {
            _panelNetworkCheck?.SetActive(false);
            _panelBrowse      ?.SetActive(false);
            _panelHosting     ?.SetActive(false);
            _panelJoining     ?.SetActive(false);
            _panelLoading     ?.SetActive(false);
        }

        private bool IsShowingPanel(GameObject p) => p != null && p.activeSelf;

        private void RefreshStartButton()
        {
            bool canStart = AreAllReady() && GetPlayers().Count >= 1;
            if (_startButton != null) _startButton.interactable = canStart;
        }

        private void SetStartButtonVisible(bool v)
        {
            if (_startButton != null) _startButton.gameObject.SetActive(v);
        }

        private void SetStatusText(string msg)
        {
            SetText(_networkStatusText, msg);
            MID_Logger.LogDebug(_logLevel, $"[Status] {msg}",
                nameof(ProjectileTestLobbyUI));
        }

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
