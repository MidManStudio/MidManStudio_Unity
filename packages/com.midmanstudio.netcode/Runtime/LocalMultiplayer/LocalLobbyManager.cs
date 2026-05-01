// LocalLobbyManager.cs
// Generic LAN / WiFi offline lobby manager for Unity Netcode for GameObjects.
// Zero game-specific dependencies — all game hooks are exposed as events.
//
// WHAT THIS DOES:
//   • Host a LAN lobby via NGO (UnityTransport)
//   • Broadcast and discover lobbies via UDP
//   • Manage the player list (join, leave, ready states, bots)
//   • Optionally delegate team logic to an ILocalLobbyTeamProvider
//   • Fire OnGameStartReceived on all clients when the host triggers game start
//
// WHAT THIS DOES NOT DO:
//   • Load scenes            — subscribe to OnGameStartReceived and do it yourself
//   • Show intro animations  — same
//   • Define game modes/maps — pass them as strings in LocalLobbyConfig
//   • Manage teams           — implement ILocalLobbyTeamProvider and inject it
//
// SETUP:
//   1. Add this component to a persistent NetworkBehaviour GameObject.
//      The GameObject MUST have a NetworkObject component.
//   2. Assign NetworkManager and UnityTransport in the inspector.
//   3. Subscribe to events before calling any Start*/Join* methods.
//   4. Optionally call SetTeamProvider() with your game's team manager.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Netcode.LocalMultiplayer
{
    // ── Config passed to StartHosting ─────────────────────────────────────────

    [Serializable]
    public class LocalLobbyConfig
    {
        public string LobbyName    = "Local Game";
        public int    MaxPlayers   = 4;
        public string GameMode     = "";
        public string GameMap      = "";
        public string CustomData   = "";
        public int    ServerPort   = 7777;
        public int    BroadcastPort = 7778;
    }

    // ─────────────────────────────────────────────────────────────────────────

    public class LocalLobbyManager : NetworkBehaviour
    {
        #region Inspector

        [Header("References")]
        [SerializeField] private NetworkManager    _networkManager;
        [SerializeField] private UnityTransport    _transport;

        [Header("Discovery")]
        [SerializeField] private float _discoveryInterval = 1f;
        [SerializeField] private float _lobbyTimeout      = 5f;

        [Header("Bots")]
        [SerializeField] private bool     _fillWithBots   = false;
        [SerializeField] private int      _maxBots        = 4;
        [SerializeField] private string[] _botNamePrefixes = { "Player", "Bot", "Pro", "Noob" };
        [SerializeField] private string[] _botNameSuffixes = { "123", "007", "42", "99" };

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Events — subscribe before calling Start*/Join*

        public Action<LocalLobbyData>   OnLobbyDiscovered;
        public Action<string>           OnLobbyRemoved;         // lobby key
        public Action<LocalLobbyPlayer> OnPlayerJoined;
        public Action<ulong>            OnPlayerLeft;
        public Action<LocalLobbyPlayer> OnPlayerReadyStatusChanged;
        public Action<bool>             OnHostResult;
        public Action<bool>             OnJoinResult;
        public Action                   OnLobbyDisbanded;
        public Action<string>           OnNetworkStatusChanged; // status string

        /// <summary>
        /// Fires on ALL clients (including host) when the host triggers game start.
        /// Subscribe here and load your scene / show your intro.
        /// The snapshot contains the final player list with team assignments.
        /// </summary>
        public Action<LocalLobbySnapshot> OnGameStartReceived;

        #endregion

        #region Singleton

        private static LocalLobbyManager _instance;

        public static LocalLobbyManager Instance
        {
            get
            {
                if (_instance == null)
                    _instance = FindAnyObjectByType<LocalLobbyManager>();
                return _instance;
            }
        }

        public static bool HasInstance => _instance != null;

        #endregion

        #region State

        private bool _isHosting;
        private bool _isSearching;
        private bool _isInitialized;
        private bool _isShuttingDown;

        private UdpClient _udpServer;
        private UdpClient _udpClient;

        private LocalLobbyConfig               _activeConfig;
        private LocalLobbyData                 _currentLobby;
        private readonly List<LocalLobbyPlayer> _players     = new();
        private readonly Dictionary<string, LocalLobbyData> _discovered = new();
        private NetworkList<NetworkLobbyPlayerData> _netPlayers;

        private string _playerName  = "Player";
        private string _playerIconId = "default";

        private ILocalLobbyTeamProvider _teamProvider;

        #endregion

        #region Properties

        public bool IsHosting     => _isHosting;
        public bool IsSearching   => _isSearching;
        public bool IsInLobby     => _networkManager != null &&
                                     (_networkManager.IsConnectedClient || _networkManager.IsHost);
        public string PlayerName  => _playerName;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;

            _netPlayers = new NetworkList<NetworkLobbyPlayerData>();

            LoadPlayerIdentity();
            StartCoroutine(InitAsync());
        }

        private IEnumerator InitAsync()
        {
            yield return new WaitForSeconds(0.1f);

            if (_networkManager == null)
                _networkManager = FindAnyObjectByType<NetworkManager>();

            if (_transport == null && _networkManager != null)
                _transport = _networkManager.GetComponent<UnityTransport>();

            if (!ValidateNetworkManager()) yield break;

            _networkManager.OnClientConnectedCallback    += HandleClientConnected;
            _networkManager.OnClientDisconnectCallback   += HandleClientDisconnected;

            _isInitialized = true;
            MID_Logger.LogInfo(_logLevel, "LocalLobbyManager initialized.",
                nameof(LocalLobbyManager));
        }

        private void Update()
        {
            if (!_isInitialized || _isShuttingDown) return;
            TickLobbyTimeout();
        }

        public override void OnDestroy()
        {
            if (!_isInitialized) return;
            _isShuttingDown = true;

            if (_networkManager != null)
            {
                _networkManager.OnClientConnectedCallback  -= HandleClientConnected;
                _networkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
            }

            StopDiscoveryServer();
            StopDiscoveryClient();
            _players.Clear();
            _discovered.Clear();

            StartCoroutine(SafeShutdown());
            base.OnDestroy();
        }

        private IEnumerator SafeShutdown()
        {
            if (_networkManager != null && _networkManager.IsListening)
            {
                if (_networkManager.IsServer)
                {
                    foreach (var id in _networkManager.ConnectedClientsIds.ToList())
                        if (id != _networkManager.LocalClientId)
                            _networkManager.DisconnectClient(id);
                }
                yield return new WaitForSeconds(0.2f);
                _networkManager.Shutdown();
                yield return new WaitForSeconds(0.3f);
            }
        }

        #endregion

        #region NGO Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _netPlayers.OnListChanged += OnNetPlayersChanged;
        }

        public override void OnNetworkDespawn()
        {
            if (_netPlayers != null)
                _netPlayers.OnListChanged -= OnNetPlayersChanged;
            base.OnNetworkDespawn();
        }

        private void OnNetPlayersChanged(NetworkListEvent<NetworkLobbyPlayerData> ev)
        {
            if (!IsServer) SyncPlayersFromNetList();
        }

        private void SyncPlayersFromNetList()
        {
            _players.RemoveAll(p => !p.IsBot);

            foreach (var np in _netPlayers)
            {
                var existing = _players.Find(p => p.ClientId == np.ClientId);
                if (existing == null)
                {
                    var player = new LocalLobbyPlayer(np.ClientId, np.PlayerName.ToString(),
                                                      np.IsHost)
                    {
                        IsReady      = np.IsReady,
                        PlayerIconId = np.PlayerIconId.ToString(),
                        TeamId       = np.TeamId
                    };
                    _players.Add(player);
                    OnPlayerJoined?.Invoke(player);
                }
                else if (existing.IsReady != np.IsReady)
                {
                    existing.IsReady = np.IsReady;
                    OnPlayerReadyStatusChanged?.Invoke(existing);
                }
            }
        }

        #endregion

        #region Network Callbacks

        private void HandleClientConnected(ulong clientId)
        {
            if (!IsSpawned)
            {
                MID_Logger.LogError(_logLevel,
                    "LocalLobbyManager is NOT spawned! Ensure a NetworkObject is attached.",
                    nameof(LocalLobbyManager));
                return;
            }

            MID_Logger.LogDebug(_logLevel, $"Client {clientId} connected.",
                nameof(LocalLobbyManager));

            if (_networkManager.IsServer)
            {
                bool isHost = clientId == _networkManager.LocalClientId;
                string name = isHost ? _playerName : $"Player {clientId}";

                int teamId = _teamProvider?.OnPlayerJoined(clientId, isHost) ?? -1;

                AddPlayerLocal(clientId, name, _playerIconId, isHost, false, teamId);
                StartCoroutine(AddToNetListDelayed(clientId, name, isHost, teamId));

                if (_currentLobby != null)
                    _currentLobby.CurrentPlayers = RealPlayerCount;

                if (clientId != _networkManager.LocalClientId)
                    StartCoroutine(RequestNameDelayed(clientId));

                if (_teamProvider != null)
                    SyncTeamStateClientRpc(_teamProvider.SerializeState());

                if (_fillWithBots) FillWithBots();
            }
            else
            {
                StartCoroutine(SendNameDelayed());
            }
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            MID_Logger.LogDebug(_logLevel, $"Client {clientId} disconnected.",
                nameof(LocalLobbyManager));

            if (_networkManager.IsServer)
            {
                for (int i = _netPlayers.Count - 1; i >= 0; i--)
                    if (_netPlayers[i].ClientId == clientId) { _netPlayers.RemoveAt(i); break; }

                _teamProvider?.OnPlayerLeft(clientId);
                RemovePlayerLocal(clientId);

                if (_currentLobby != null)
                    _currentLobby.CurrentPlayers = RealPlayerCount;

                NotifyPlayerLeftClientRpc(clientId);
            }
            else if (_networkManager.IsClient && !_networkManager.IsConnectedClient)
            {
                _currentLobby = null;
                _players.Clear();
                _netPlayers?.Clear();
                OnLobbyDisbanded?.Invoke();
            }
        }

        #endregion

        #region Public API — Hosting

        public void SetTeamProvider(ILocalLobbyTeamProvider provider)
        {
            _teamProvider = provider;
            MID_Logger.LogInfo(_logLevel, "Team provider set.",
                nameof(LocalLobbyManager));
        }

        public async void StartHosting(LocalLobbyConfig config = null)
        {
            if (!_isInitialized || _isShuttingDown)
            {
                OnHostResult?.Invoke(false); return;
            }

            _activeConfig = config ?? new LocalLobbyConfig();

            if (_isHosting || _networkManager.IsListening)
                await CleanupNetworkAsync();

            _currentLobby = new LocalLobbyData
            {
                LobbyName      = _activeConfig.LobbyName,
                HostName       = _playerName,
                HostAddress    = GetLocalIP(),
                Port           = _activeConfig.ServerPort,
                CurrentPlayers = 1,
                MaxPlayers     = _activeConfig.MaxPlayers,
                GameMode       = _activeConfig.GameMode,
                GameMap        = _activeConfig.GameMap,
                CustomData     = _activeConfig.CustomData,
                LastDiscoveryTime = Time.time,
                TimeoutTime    = Time.time + _lobbyTimeout
            };

            try
            {
                if (!ValidateNetworkManager()) throw new Exception("NetworkManager invalid.");

                _transport.ConnectionData.Address = _currentLobby.HostAddress;
                _transport.ConnectionData.Port    = (ushort)_activeConfig.ServerPort;

                _isHosting = true;
                if (!_networkManager.StartHost())
                    throw new Exception("StartHost() returned false.");

                await Task.Delay(100);
                StartDiscoveryServer(_activeConfig.BroadcastPort);

                OnHostResult?.Invoke(true);
                MID_Logger.LogInfo(_logLevel,
                    $"Hosting lobby '{_currentLobby.LobbyName}' on {_currentLobby.HostAddress}:{_currentLobby.Port}",
                    nameof(LocalLobbyManager));
            }
            catch (Exception e)
            {
                MID_Logger.LogError(_logLevel, $"StartHosting failed: {e.Message}",
                    nameof(LocalLobbyManager));
                _isHosting    = false;
                _currentLobby = null;
                OnHostResult?.Invoke(false);
            }
        }

        public void StopHosting()
        {
            if (!_isHosting) return;
            _isHosting = false;
            StopDiscoveryServer();
            StartCoroutine(StopHostCoroutine());
        }

        private IEnumerator StopHostCoroutine()
        {
            _currentLobby = null;
            _players.Clear();
            _netPlayers?.Clear();

            if (_networkManager != null && (_networkManager.IsHost || _networkManager.IsServer))
            {
                foreach (var id in (_networkManager.ConnectedClientsIds ?? Enumerable.Empty<ulong>()).ToList())
                    if (id != _networkManager.LocalClientId)
                        _networkManager.DisconnectClient(id);

                yield return new WaitForSeconds(0.2f);
                _networkManager.Shutdown();
                yield return new WaitForSeconds(0.3f);
            }
        }

        #endregion

        #region Public API — Joining / Leaving

        public async void JoinLobby(LocalLobbyData lobby)
        {
            if (!_isInitialized || _isShuttingDown)
            {
                OnJoinResult?.Invoke(false); return;
            }

            if (_networkManager.IsListening)
                await CleanupNetworkAsync();

            try
            {
                _transport.ConnectionData.Address = lobby.HostAddress;
                _transport.ConnectionData.Port    = (ushort)lobby.Port;
                _players.Clear();
                _currentLobby = lobby;

                if (!_networkManager.StartClient())
                    throw new Exception("StartClient() returned false.");

                for (int attempt = 0; attempt < 100; attempt++)
                {
                    await Task.Delay(100);
                    if (_networkManager.IsConnectedClient && IsSpawned)
                    {
                        OnJoinResult?.Invoke(true);
                        return;
                    }
                }

                throw new Exception("Connection timeout.");
            }
            catch (Exception e)
            {
                MID_Logger.LogError(_logLevel, $"JoinLobby failed: {e.Message}",
                    nameof(LocalLobbyManager));
                _currentLobby = null;
                OnJoinResult?.Invoke(false);
            }
        }

        public void LeaveLobby()
        {
            StartCoroutine(LeaveCoroutine());
        }

        private IEnumerator LeaveCoroutine()
        {
            _currentLobby = null;
            _players.Clear();
            _netPlayers?.Clear();

            if (_networkManager != null && _networkManager.IsConnectedClient)
            {
                _networkManager.Shutdown();
                yield return new WaitForSeconds(0.5f);
            }
        }

        #endregion

        #region Public API — Discovery

        public void StartSearching()
        {
            if (!_isInitialized || _isSearching) return;
            _isSearching = true;
            _discovered.Clear();
            StartDiscoveryClient(_activeConfig?.BroadcastPort ?? 7778);
        }

        public void StopSearching()
        {
            if (!_isSearching) return;
            _isSearching = false;
            StopDiscoveryClient();
            _discovered.Clear();
        }

        public IReadOnlyDictionary<string, LocalLobbyData> GetDiscoveredLobbies() =>
            _discovered;

        #endregion

        #region Public API — Players

        public void SetPlayerName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            _playerName = name.Trim();
            PlayerPrefs.SetString("LocalLobby_PlayerName", _playerName);
            PlayerPrefs.Save();
        }

        public void SetPlayerIconId(string iconId)
        {
            if (string.IsNullOrWhiteSpace(iconId)) return;
            _playerIconId = iconId;
        }

        public void SetPlayerReady(ulong clientId, bool ready)
        {
            if (!IsSpawned || !_networkManager.IsConnectedClient) return;

            var player = _players.Find(p => p.ClientId == clientId);
            if (player != null) { player.IsReady = ready; OnPlayerReadyStatusChanged?.Invoke(player); }

            if (IsServer)
            {
                UpdateNetListReady(clientId, ready);
                NotifyReadyStatusClientRpc(clientId, ready);
            }
            else
            {
                SetReadyServerRpc(clientId, ready);
            }
        }

        public bool AreAllPlayersReady()
        {
            var real = _players.Where(p => !p.IsBot).ToList();
            return real.Count > 0 && real.All(p => p.IsReady);
        }

        public void SetFillWithBots(bool fill)
        {
            _fillWithBots = fill;
            if (_isHosting) { if (fill) FillWithBots(); else RemoveAllBots(); }
        }

        public List<LocalLobbyPlayer> GetPlayers() => new List<LocalLobbyPlayer>(_players);
        public LocalLobbyData GetCurrentLobby() => _currentLobby;
        public int RealPlayerCount => _players.Count(p => !p.IsBot);

        #endregion

        #region Public API — Game Start

        /// <summary>
        /// Host calls this to start the game.
        /// Validates all players ready, prepares team assignments via the team provider,
        /// then fires OnGameStartReceived on all connected clients (including host).
        /// </summary>
        public void RequestGameStart()
        {
            if (!IsHost) { MID_Logger.LogError(_logLevel, "Only host can start the game.", nameof(LocalLobbyManager)); return; }
            if (!AreAllPlayersReady()) { MID_Logger.LogError(_logLevel, "Not all players are ready.", nameof(LocalLobbyManager)); return; }

            _teamProvider?.OnPrepareGameStart(_players);

            string serializedTeams = _teamProvider?.SerializeState() ?? "";
            GameStartClientRpc(serializedTeams);

            MID_Logger.LogInfo(_logLevel, "Game start triggered — sent to all clients.",
                nameof(LocalLobbyManager));
        }

        public bool TryChangeTeam(ulong clientId, int targetTeamId)
        {
            if (!_networkManager.IsConnectedClient) return false;

            if (IsServer)
            {
                if (_teamProvider == null) return false;
                bool ok = _teamProvider.TryChangeTeam(clientId, targetTeamId);
                if (ok) SyncTeamStateClientRpc(_teamProvider.SerializeState());
                return ok;
            }
            else
            {
                RequestTeamChangeServerRpc(clientId, targetTeamId);
                return true; // result arrives via ClientRpc
            }
        }

        #endregion

        #region Mobile Utilities

        public void OpenHotspotSettings()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unityClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity   = unityClass.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent     = new AndroidJavaObject("android.content.Intent", "android.settings.WIRELESS_SETTINGS");
                activity.Call("startActivity", intent);
            }
            catch (Exception e) { MID_Logger.LogError(_logLevel, $"Cannot open hotspot settings: {e.Message}", nameof(LocalLobbyManager)); }
#elif UNITY_IOS && !UNITY_EDITOR
            UnityEngine.Application.OpenURL("App-Prefs:root=WIFI");
#endif
        }

        public void OpenWiFiSettings()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unityClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity   = unityClass.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent     = new AndroidJavaObject("android.content.Intent", "android.settings.WIFI_SETTINGS");
                activity.Call("startActivity", intent);
            }
            catch (Exception e) { MID_Logger.LogError(_logLevel, $"Cannot open WiFi settings: {e.Message}", nameof(LocalLobbyManager)); }
#elif UNITY_IOS && !UNITY_EDITOR
            UnityEngine.Application.OpenURL("App-Prefs:root=WIFI");
#endif
        }

        #endregion

        #region RPCs

        [ServerRpc(RequireOwnership = false)]
        private void SetReadyServerRpc(ulong clientId, bool ready)
        {
            UpdateNetListReady(clientId, ready);
            var p = _players.Find(x => x.ClientId == clientId);
            if (p != null) { p.IsReady = ready; OnPlayerReadyStatusChanged?.Invoke(p); }
            NotifyReadyStatusClientRpc(clientId, ready);
        }

        [ClientRpc]
        private void NotifyReadyStatusClientRpc(ulong clientId, bool ready)
        {
            if (IsServer) return;
            var p = _players.Find(x => x.ClientId == clientId);
            if (p != null) { p.IsReady = ready; OnPlayerReadyStatusChanged?.Invoke(p); }
        }

        [ClientRpc]
        private void NotifyPlayerLeftClientRpc(ulong clientId)
        {
            if (IsServer) return;
            RemovePlayerLocal(clientId);
        }

        [ClientRpc]
        private void SyncPlayerListClientRpc()
        {
            if (IsServer) return;
            SyncPlayersFromNetList();
        }

        [ServerRpc(RequireOwnership = false)]
        private void SendPlayerNameServerRpc(ulong clientId, string name, string iconId)
        {
            var p = _players.Find(x => x.ClientId == clientId);
            if (p != null) { p.PlayerName = name; p.PlayerIconId = iconId; }

            for (int i = 0; i < _netPlayers.Count; i++)
            {
                if (_netPlayers[i].ClientId != clientId) continue;
                var np = _netPlayers[i];
                np.PlayerName  = name;
                np.PlayerIconId = iconId;
                _netPlayers[i] = np;
                break;
            }
            BroadcastNameUpdateClientRpc(clientId, name, iconId);
        }

        [ClientRpc]
        private void RequestNameClientRpc(ulong target)
        {
            if (NetworkManager.Singleton.LocalClientId == target)
                SendPlayerNameServerRpc(target, _playerName, _playerIconId);
        }

        [ClientRpc]
        private void BroadcastNameUpdateClientRpc(ulong clientId, string name, string iconId)
        {
            if (IsServer) return;
            var p = _players.Find(x => x.ClientId == clientId);
            if (p != null) { p.PlayerName = name; p.PlayerIconId = iconId; OnPlayerJoined?.Invoke(p); }
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestTeamChangeServerRpc(ulong clientId, int targetTeamId)
        {
            if (_teamProvider == null) return;
            bool ok = _teamProvider.TryChangeTeam(clientId, targetTeamId);
            if (ok) SyncTeamStateClientRpc(_teamProvider.SerializeState());
            NotifyTeamChangeResultClientRpc(clientId, ok, targetTeamId);
        }

        [ClientRpc]
        private void SyncTeamStateClientRpc(string serializedState)
        {
            _teamProvider?.DeserializeState(serializedState);
        }

        [ClientRpc]
        private void NotifyTeamChangeResultClientRpc(ulong target, bool success, int teamId)
        {
            if (NetworkManager.Singleton.LocalClientId != target) return;
            MID_Logger.LogDebug(_logLevel, $"Team change result: {success} → team {teamId}",
                nameof(LocalLobbyManager));
        }

        [ClientRpc]
        private void GameStartClientRpc(string serializedTeamState)
        {
            if (!string.IsNullOrEmpty(serializedTeamState))
                _teamProvider?.DeserializeState(serializedTeamState);

            var snapshot = new LocalLobbySnapshot(_currentLobby, _players);
            OnGameStartReceived?.Invoke(snapshot);

            MID_Logger.LogInfo(_logLevel, "Game start received.",
                nameof(LocalLobbyManager));
        }

        #endregion

        #region UDP Discovery — Server Side

        private void StartDiscoveryServer(int broadcastPort)
        {
            try
            {
                _udpServer = new UdpClient();
                _udpServer.EnableBroadcast = true;
                _udpServer.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpServer.Client.Bind(new IPEndPoint(IPAddress.Any, broadcastPort));
                ListenAndBroadcast(broadcastPort);
                MID_Logger.LogInfo(_logLevel, $"Discovery server started on port {broadcastPort}.",
                    nameof(LocalLobbyManager));
            }
            catch (Exception e)
            {
                MID_Logger.LogError(_logLevel, $"Discovery server failed: {e.Message}",
                    nameof(LocalLobbyManager));
                StopDiscoveryServer();
            }
        }

        private async void ListenAndBroadcast(int broadcastPort)
        {
            var broadcastTask = BroadcastLoop(broadcastPort);
            var listenTask    = ListenForDiscoveryRequests();
            await Task.WhenAll(broadcastTask, listenTask);
        }

        private async Task BroadcastLoop(int broadcastPort)
        {
            while (_isHosting && _udpServer != null)
            {
                try
                {
                    _currentLobby.LastDiscoveryTime = Time.time;
                    _currentLobby.TimeoutTime       = Time.time + _lobbyTimeout;

                    string json = JsonUtility.ToJson(_currentLobby);
                    byte[] data = System.Text.Encoding.UTF8.GetBytes(json);

                    foreach (var addr in GetBroadcastAddresses())
                        await _udpServer.SendAsync(data, data.Length, new IPEndPoint(addr, broadcastPort));

                    await Task.Delay((int)(_discoveryInterval * 1000));
                }
                catch (Exception e) { if (_isHosting) MID_Logger.LogError(_logLevel, $"Broadcast error: {e.Message}", nameof(LocalLobbyManager)); }
            }
        }

        private async Task ListenForDiscoveryRequests()
        {
            while (_isHosting && _udpServer != null)
            {
                try
                {
                    var result  = await _udpServer.ReceiveAsync();
                    string msg  = System.Text.Encoding.UTF8.GetString(result.Buffer);
                    if (msg == "DISCOVER_LOBBY")
                    {
                        byte[] resp = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(_currentLobby));
                        await _udpServer.SendAsync(resp, resp.Length, result.RemoteEndPoint);
                    }
                }
                catch (Exception e) { if (_isHosting) MID_Logger.LogError(_logLevel, $"Discovery listen error: {e.Message}", nameof(LocalLobbyManager)); }
            }
        }

        private void StopDiscoveryServer()
        {
            if (_udpServer == null) return;
            _udpServer.Close(); _udpServer.Dispose(); _udpServer = null;
        }

        #endregion

        #region UDP Discovery — Client Side

        private void StartDiscoveryClient(int broadcastPort)
        {
            try
            {
                _udpClient = new UdpClient();
                _udpClient.EnableBroadcast = true;
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                ListenForBroadcasts();
                SendDiscoveryRequests(broadcastPort);
            }
            catch (Exception e)
            {
                MID_Logger.LogError(_logLevel, $"Discovery client failed: {e.Message}",
                    nameof(LocalLobbyManager));
                StopDiscoveryClient();
            }
        }

        private async void ListenForBroadcasts()
        {
            while (_isSearching && _udpClient != null)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync();
                    string msg = System.Text.Encoding.UTF8.GetString(result.Buffer);

                    try
                    {
                        var lobby = JsonUtility.FromJson<LocalLobbyData>(msg);
                        if (lobby == null || string.IsNullOrEmpty(lobby.HostAddress)) continue;

                        lobby.LastDiscoveryTime = Time.time;
                        lobby.TimeoutTime       = Time.time + _lobbyTimeout;

                        string key = lobby.Key;
                        bool isNew = !_discovered.ContainsKey(key);
                        _discovered[key] = lobby;
                        if (isNew) OnLobbyDiscovered?.Invoke(lobby);
                    }
                    catch { /* malformed packet */ }
                }
                catch (Exception e) { if (_isSearching && _udpClient != null) MID_Logger.LogError(_logLevel, $"Listen error: {e.Message}", nameof(LocalLobbyManager)); }
            }
        }

        private async void SendDiscoveryRequests(int broadcastPort)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("DISCOVER_LOBBY");
            while (_isSearching && _udpClient != null)
            {
                try
                {
                    foreach (var addr in GetBroadcastAddresses())
                        await _udpClient.SendAsync(data, data.Length, new IPEndPoint(addr, broadcastPort));
                    await Task.Delay((int)(_discoveryInterval * 1000));
                }
                catch (Exception e) { if (_isSearching) MID_Logger.LogError(_logLevel, $"Discovery send error: {e.Message}", nameof(LocalLobbyManager)); }
            }
        }

        private void StopDiscoveryClient()
        {
            if (_udpClient == null) return;
            _udpClient.Close(); _udpClient.Dispose(); _udpClient = null;
        }

        private void TickLobbyTimeout()
        {
            if (!_isSearching) return;
            float now = Time.time;
            foreach (var key in _discovered.Keys.ToList())
            {
                if (_discovered[key].TimeoutTime > 0 && now > _discovered[key].TimeoutTime)
                {
                    _discovered.Remove(key);
                    OnLobbyRemoved?.Invoke(key);
                }
            }
        }

        #endregion

        #region Bots

        private void FillWithBots()
        {
            if (!_isHosting || _currentLobby == null || !_fillWithBots) return;

            int needed  = _currentLobby.MaxPlayers - RealPlayerCount;
            needed      = Mathf.Clamp(needed, 0, _maxBots);
            int current = _players.Count(p => p.IsBot);

            if (current > needed)
            {
                foreach (var b in _players.Where(p => p.IsBot).Take(current - needed).ToList())
                    RemovePlayerLocal(b.ClientId);
            }
            else
            {
                for (int i = 0; i < needed - current; i++)
                {
                    ulong botId = NextBotId();
                    string name = BotName();
                    int teamId  = _teamProvider?.OnPlayerJoined(botId, false) ?? -1;
                    var bot     = new LocalLobbyPlayer(botId, name, false, true) { IsReady = true, TeamId = teamId };
                    _players.Add(bot);
                    OnPlayerJoined?.Invoke(bot);
                    OnPlayerReadyStatusChanged?.Invoke(bot);
                }
            }
        }

        private void RemoveAllBots()
        {
            foreach (var b in _players.Where(p => p.IsBot).ToList())
                RemovePlayerLocal(b.ClientId);
        }

        private ulong NextBotId()
        {
            ulong id = 10000;
            while (_players.Any(p => p.ClientId == id)) id++;
            return id;
        }

        private string BotName()
        {
            string pre = _botNamePrefixes[UnityEngine.Random.Range(0, _botNamePrefixes.Length)];
            string suf = _botNameSuffixes[UnityEngine.Random.Range(0, _botNameSuffixes.Length)];
            return pre + suf;
        }

        #endregion

        #region Helpers

        private void LoadPlayerIdentity()
        {
            if (PlayerOfflineIdentity.HasInstance)
            {
                _playerName   = PlayerOfflineIdentity.Instance.PlayerName;
                _playerIconId = PlayerOfflineIdentity.Instance.PlayerIconId;
            }
            else
            {
                _playerName = PlayerPrefs.GetString("LocalLobby_PlayerName",
                              PlayerPrefs.GetString("PlayerName", "Player"));
                if (string.IsNullOrWhiteSpace(_playerName)) _playerName = "Player";
            }
        }

        private void AddPlayerLocal(ulong id, string name, string iconId,
                                    bool isHost, bool isBot, int teamId)
        {
            var p = new LocalLobbyPlayer(id, name, isHost, isBot)
            { PlayerIconId = iconId, TeamId = teamId };
            _players.Add(p);
            OnPlayerJoined?.Invoke(p);
        }

        private void RemovePlayerLocal(ulong clientId)
        {
            var p = _players.Find(x => x.ClientId == clientId);
            if (p == null) return;
            _players.Remove(p);
            OnPlayerLeft?.Invoke(clientId);
        }

        private void UpdateNetListReady(ulong clientId, bool ready)
        {
            for (int i = 0; i < _netPlayers.Count; i++)
            {
                if (_netPlayers[i].ClientId != clientId) continue;
                var np = _netPlayers[i]; np.IsReady = ready; _netPlayers[i] = np; break;
            }
        }

        private IEnumerator AddToNetListDelayed(ulong clientId, string name, bool isHost, int teamId)
        {
            yield return new WaitForSeconds(0.5f);
            if (!IsServer || !IsSpawned) yield break;

            var p = _players.Find(x => x.ClientId == clientId);

            _netPlayers.Add(new NetworkLobbyPlayerData
            {
                ClientId     = clientId,
                PlayerName   = name,
                PlayerIconId = p?.PlayerIconId ?? "",
                IsHost       = isHost,
                IsReady      = false,
                TeamId       = teamId
            });
            SyncPlayerListClientRpc();
        }

        private IEnumerator RequestNameDelayed(ulong clientId)
        {
            yield return new WaitForSeconds(0.2f);
            RequestNameClientRpc(clientId);
        }

        private IEnumerator SendNameDelayed()
        {
            yield return new WaitForSeconds(0.2f);
            SendPlayerNameServerRpc(_networkManager.LocalClientId, _playerName, _playerIconId);
        }

        private bool ValidateNetworkManager()
        {
            if (_networkManager == null)
            {
                MID_Logger.LogError(_logLevel, "NetworkManager is null.", nameof(LocalLobbyManager));
                return false;
            }
            if (_networkManager.NetworkConfig == null)
                _networkManager.NetworkConfig = new NetworkConfig();
            if (_networkManager.NetworkConfig.Prefabs == null)
                _networkManager.NetworkConfig.Prefabs = new NetworkPrefabs();
            return true;
        }

        private async Task CleanupNetworkAsync()
        {
            if (_networkManager == null || !_networkManager.IsListening) return;
            try
            {
                if (_networkManager.IsServer)
                    foreach (var id in _networkManager.ConnectedClientsIds.ToList())
                        if (id != _networkManager.LocalClientId)
                            _networkManager.DisconnectClient(id);

                await Task.Delay(200);
                _networkManager.Shutdown();
                await Task.Delay(500);
            }
            catch (Exception e)
            {
                MID_Logger.LogError(_logLevel, $"Cleanup error: {e.Message}", nameof(LocalLobbyManager));
            }
        }

        internal string GetLocalIP()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();

                // Priority 1: WiFi/Ethernet — non-hotspot
                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
                        ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;

                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = addr.Address.ToString();
                        if (!ip.StartsWith("192.168.43.") && !ip.StartsWith("192.168.49.") &&
                            !ip.StartsWith("172.20.10.") && !IPAddress.IsLoopback(addr.Address))
                            return ip;
                    }
                }

                // Priority 2: Hotspot IPs (host-side)
                foreach (var ni in interfaces)
                {
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = addr.Address.ToString();
                        if (ip.StartsWith("192.168.43.") || ip.StartsWith("192.168.49.") ||
                            ip.StartsWith("172.20.10."))
                            return ip;
                    }
                }

                // Priority 3: Any non-loopback
                foreach (var ni in interfaces)
                {
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(addr.Address))
                            return addr.Address.ToString();
                    }
                }
            }
            catch (Exception e)
            {
                MID_Logger.LogError(_logLevel, $"GetLocalIP error: {e.Message}", nameof(LocalLobbyManager));
            }

            return "127.0.0.1";
        }

        private List<IPAddress> GetBroadcastAddresses()
        {
            var list = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
                        ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork ||
                            IPAddress.IsLoopback(ua.Address)) continue;

                        byte[] ip   = ua.Address.GetAddressBytes();
                        byte[] mask = ua.IPv4Mask.GetAddressBytes();
                        var bc      = new byte[4];
                        for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                        list.Add(new IPAddress(bc));
                    }
                }
            }
            catch (Exception e)
            {
                MID_Logger.LogError(_logLevel, $"GetBroadcastAddresses error: {e.Message}", nameof(LocalLobbyManager));
            }
            return list;
        }

        #endregion
    }
}
