// packages/com.midmanstudio.projectilesystem/Tests/Runtime/ProjectileTestLobbyUI.cs
// Concrete subclass of LocalLobbyUIManager for the projectile system test scene.
//
// SETUP:
//   1. Create a Canvas GameObject in the lobby scene.
//      Add Canvas, CanvasScaler, GraphicRaycaster, and this component.
//   2. Build child panel GameObjects: PanelNetworkCheck, PanelBrowse, PanelHosting,
//      PanelJoining, PanelLoading — wire them to the inspector fields.
//   3. Add LocalLobbyManager and MobileNetworkStatusMonitor to the scene and
//      assign them in the inherited inspector fields.
//   4. The _lobbyContext field (from LocalLobbyUIManager) is optional for this
//      implementation — panels are driven by the virtual hook overrides instead.
//
// PANEL BUTTON NAMING CONVENTION expected by this script:
//   PanelBrowse  : Button "HostButton", TMP_InputField "NameInput", Text "StatusText"
//   PanelHosting : Button "StartButton", Button "ReadyButton", Button "LeaveButton"
//   PanelJoining : Button "ReadyButton", Button "LeaveButton"
//   LobbyEntry prefab : two TMP_Text children (Name, PlayerCount), one Button (Join)
//   PlayerEntry prefab: three TMP_Text children (Name, Role, Ready)

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
        #region Inspector

        [Header("Panels  — wire GameObjects here")]
        [SerializeField] private GameObject _panelNetworkCheck;
        [SerializeField] private GameObject _panelBrowse;
        [SerializeField] private GameObject _panelHosting;
        [SerializeField] private GameObject _panelJoining;
        [SerializeField] private GameObject _panelLoading;

        [Header("Browse Panel")]
        [SerializeField] private Button         _hostButton;
        [SerializeField] private TMP_InputField _playerNameInput;
        [SerializeField] private TMP_Text       _networkStatusText;
        [SerializeField] private Transform      _lobbyListContainer;
        [SerializeField] private GameObject     _lobbyEntryPrefab;

        [Header("In-Lobby Panel  (Hosting + Joining share these)")]
        [SerializeField] private TMP_Text   _lobbyTitleText;
        [SerializeField] private Transform  _playerListContainer;
        [SerializeField] private GameObject _playerEntryPrefab;
        [SerializeField] private Button     _startButton;    // host-only
        [SerializeField] private Button     _readyButton;
        [SerializeField] private Button     _leaveButton;
        [SerializeField] private TMP_Text   _readyButtonLabel;

        [Header("Network Check Panel")]
        [SerializeField] private TMP_Text   _noNetworkText;
        [SerializeField] private Button     _openWifiButton;
        [SerializeField] private Button     _openHotspotButton;

        [Header("Loading Panel")]
        [SerializeField] private TMP_Text   _loadingText;

        [Header("Lobby Config")]
        [SerializeField] private int  _maxPlayers  = 4;
        [SerializeField] private int  _serverPort   = 7777;
        [SerializeField] private int  _broadcastPort = 7778;

        #endregion

        #region Private State

        private readonly Dictionary<string, GameObject> _lobbyEntries  = new(8);
        private readonly Dictionary<ulong, GameObject>  _playerEntries = new(8);
        private bool _localReady;

        #endregion

        #region Lifecycle

        protected override void Awake()
        {
            base.Awake();

            // Wire Browse panel
            _hostButton?.onClick.AddListener(OnHostClicked);
            _playerNameInput?.onEndEdit.AddListener(name => RequestPlayerName(name));

            // Wire In-Lobby panel
            _startButton?.onClick.AddListener(OnStartClicked);
            _readyButton?.onClick.AddListener(OnReadyClicked);
            _leaveButton?.onClick.AddListener(OnLeaveClicked);

            // Wire Network Check panel
            _openWifiButton?   .onClick.AddListener(OpenWifi);
            _openHotspotButton?.onClick.AddListener(OpenHotspot);

            // Start hidden; Start() will navigate to the correct panel
            SetAllPanelsHidden();
        }

        private void Start()
        {
            RequestStartSearch();
            // Defer to first network status update — but default to Browse if no monitor
            ShowPanel(_panelBrowse);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
        }

        #endregion

        #region LocalLobbyUIManager Virtual Hooks

        protected override void OnLobbyDiscovered(LocalLobbyData lobby)
        {
            if (_lobbyEntries.ContainsKey(lobby.Key)) return;
            if (_lobbyListContainer == null || _lobbyEntryPrefab == null) return;

            var entry = Instantiate(_lobbyEntryPrefab, _lobbyListContainer);
            _lobbyEntries[lobby.Key] = entry;

            var texts = entry.GetComponentsInChildren<TMP_Text>(true);
            if (texts.Length > 0) texts[0].text = lobby.LobbyName;
            if (texts.Length > 1)
                texts[1].text = $"{lobby.CurrentPlayers}/{lobby.MaxPlayers}";

            var btn = entry.GetComponentInChildren<Button>(true);
            if (btn != null)
            {
                var captured = lobby;
                btn.onClick.AddListener(() => JoinLobby(captured));
            }

            MID_Logger.LogDebug(_logLevel,
                $"Lobby entry added: {lobby}",
                nameof(ProjectileTestLobbyUI));
        }

        protected override void OnLobbyRemoved(string lobbyKey)
        {
            if (!_lobbyEntries.TryGetValue(lobbyKey, out var go)) return;
            Destroy(go);
            _lobbyEntries.Remove(lobbyKey);
        }

        protected override void OnPlayerJoined(LocalLobbyPlayer player)
        {
            if (_playerListContainer == null || _playerEntryPrefab == null) return;
            if (_playerEntries.ContainsKey(player.ClientId)) return;

            var entry = Instantiate(_playerEntryPrefab, _playerListContainer);
            _playerEntries[player.ClientId] = entry;
            RefreshPlayerEntry(entry, player);

            RefreshStartButton();

            MID_Logger.LogDebug(_logLevel,
                $"Player entry added: {player}",
                nameof(ProjectileTestLobbyUI));
        }

        protected override void OnPlayerLeft(ulong clientId)
        {
            if (!_playerEntries.TryGetValue(clientId, out var go)) return;
            Destroy(go);
            _playerEntries.Remove(clientId);
            RefreshStartButton();
        }

        protected override void OnPlayerReadyChanged(LocalLobbyPlayer player)
        {
            if (!_playerEntries.TryGetValue(player.ClientId, out var go)) return;
            RefreshPlayerEntry(go, player);
            RefreshStartButton();
        }

        protected override void OnHostResult(bool success)
        {
            if (success)
            {
                ShowPanel(_panelHosting);
                if (_lobbyTitleText != null)
                    _lobbyTitleText.text = $"Hosting: {_lobbyManager?.PlayerName}'s Test Lobby";
                SetStartButtonVisible(true);
            }
            else
            {
                ShowPanel(_panelBrowse);
                SetStatusText("Host failed — check WiFi / hotspot.");
                MID_Logger.LogWarning(_logLevel,
                    "Host failed.",
                    nameof(ProjectileTestLobbyUI));
            }
        }

        protected override void OnJoinResult(bool success)
        {
            if (success)
            {
                ShowPanel(_panelJoining);
                if (_lobbyTitleText != null)
                    _lobbyTitleText.text = "In Lobby";
                SetStartButtonVisible(false);
            }
            else
            {
                ShowPanel(_panelBrowse);
                SetStatusText("Join failed — host may have left.");
                MID_Logger.LogWarning(_logLevel,
                    "Join failed.",
                    nameof(ProjectileTestLobbyUI));
            }
        }

        protected override void OnLobbyDisbanded()
        {
            ClearPlayerList();
            ShowPanel(_panelBrowse);
            SetStatusText("Lobby disbanded by host.");
        }

        protected override void OnNetworkStatusChanged(string status)
        {
            SetStatusText(FriendlyStatus(status));

            bool hasLAN = status is "WIFI_CONNECTED" or "HOTSPOT";

            if (!hasLAN)
            {
                // Only jump to network check if we're in Browse, not mid-lobby
                if (IsShowingPanel(_panelBrowse) || IsShowingPanel(_panelNetworkCheck))
                {
                    ShowPanel(_panelNetworkCheck);
                    if (_noNetworkText != null)
                        _noNetworkText.text = status == "MOBILE_DATA"
                            ? "WiFi required for LAN play.\nMobile data cannot host or join."
                            : "No network connection detected.";
                }
            }
            else if (IsShowingPanel(_panelNetworkCheck))
            {
                ShowPanel(_panelBrowse);
            }
        }

        protected override void OnGameStartReceived(LocalLobbySnapshot snapshot)
        {
            MID_Logger.LogInfo(_logLevel,
                $"Game start — {snapshot.Players.Count} players.",
                nameof(ProjectileTestLobbyUI));

            ShowPanel(_panelLoading);
            if (_loadingText != null) _loadingText.text = "Starting test session…";

            // Disable this UI root so the game scene takes over.
            // The game scene must be loaded by a SceneManager call in your game code.
            // For the test: just deactivate after a short delay.
            Invoke(nameof(HideUI), 0.5f);
        }

        #endregion

        #region Button Handlers

        private void OnHostClicked()
        {
            ShowPanel(_panelLoading);
            if (_loadingText != null) _loadingText.text = "Starting host…";

            string playerName = (_playerNameInput != null && !string.IsNullOrWhiteSpace(_playerNameInput.text))
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
            if (_loadingText != null) _loadingText.text = $"Joining {lobby.LobbyName}…";
            RequestJoin(lobby);
        }

        private void OnStartClicked()
        {
            if (!AreAllReady())
            {
                MID_Logger.LogWarning(_logLevel,
                    "Cannot start — not all players are ready.",
                    nameof(ProjectileTestLobbyUI));
                SetStatusText("All players must be ready to start.");
                return;
            }
            RequestGameStart();
        }

        private void OnReadyClicked()
        {
            if (NetworkManager.Singleton == null
                || !NetworkManager.Singleton.IsConnectedClient) return;

            _localReady = !_localReady;
            ulong localId = NetworkManager.Singleton.LocalClientId;
            RequestSetReady(localId, _localReady);

            if (_readyButtonLabel != null)
                _readyButtonLabel.text = _localReady ? "Unready" : "Ready";
        }

        private void OnLeaveClicked()
        {
            _localReady = false;
            if (_readyButtonLabel != null) _readyButtonLabel.text = "Ready";

            ClearPlayerList();
            ClearLobbyList();

            if (_lobbyManager != null && _lobbyManager.IsHosting)
                RequestStopHosting();
            else
                RequestLeave();

            ShowPanel(_panelBrowse);
        }

        private void OpenWifi()
        {
            _lobbyManager?.OpenWiFiSettings();
        }

        private void OpenHotspot()
        {
            _lobbyManager?.OpenHotspotSettings();
        }

        #endregion

        #region UI Helpers

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

        private bool IsShowingPanel(GameObject panel)
            => panel != null && panel.activeSelf;

        private void RefreshPlayerEntry(GameObject entry, LocalLobbyPlayer player)
        {
            if (entry == null) return;
            var texts = entry.GetComponentsInChildren<TMP_Text>(true);
            if (texts.Length > 0) texts[0].text = player.PlayerName + (player.IsBot ? " [BOT]" : "");
            if (texts.Length > 1) texts[1].text = player.IsHost ? "HOST" : "Player";
            if (texts.Length > 2)
            {
                texts[2].text  = player.IsReady ? "✓ Ready" : "…";
                texts[2].color = player.IsReady
                    ? new Color(0.3f, 1f, 0.4f)
                    : new Color(0.8f, 0.8f, 0.8f);
            }
        }

        private void RefreshStartButton()
        {
            bool canStart = AreAllReady() && GetPlayers().Count >= 1;
            if (_startButton != null)
                _startButton.interactable = canStart;
        }

        private void SetStartButtonVisible(bool visible)
        {
            if (_startButton != null)
                _startButton.gameObject.SetActive(visible);
        }

        private void SetStatusText(string msg)
        {
            if (_networkStatusText != null)
                _networkStatusText.text = msg;
            MID_Logger.LogDebug(_logLevel, $"[Status] {msg}", nameof(ProjectileTestLobbyUI));
        }

        private void ClearPlayerList()
        {
            foreach (var go in _playerEntries.Values)
                if (go != null) Destroy(go);
            _playerEntries.Clear();
        }

        private void ClearLobbyList()
        {
            foreach (var go in _lobbyEntries.Values)
                if (go != null) Destroy(go);
            _lobbyEntries.Clear();
        }

        private void HideUI() => gameObject.SetActive(false);

        private static string FriendlyStatus(string raw) => raw switch
        {
            "WIFI_CONNECTED" => "WiFi Connected ✓",
            "HOTSPOT"        => "Hotspot Active — others can join",
            "MOBILE_DATA"    => "Mobile Data only — WiFi needed for LAN",
            "NO_NETWORK"     => "No Network ✗",
            _                => raw
        };

        #endregion
    }
}
