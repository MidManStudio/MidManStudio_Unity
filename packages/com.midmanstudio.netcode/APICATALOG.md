# com.midmanstudio.netcode — API Catalog

`com.midmanstudio.netcode` v1.0.0  
Assembly: `MidManStudio.Netcode`  
Namespace root: `MidManStudio.Core.Netcode`  
Requires: `com.midmanstudio.utilities`, `com.unity.netcode.gameobjects 1.7.1+`

---

## Table of Contents

1. [Singletons](#1-singletons)
2. [Network Object Pool](#2-network-object-pool)
3. [Network Connection Manager](#3-network-connection-manager)
4. [Network RPC Queue](#4-network-rpc-queue)
5. [Local Multiplayer Lobby](#5-local-multiplayer-lobby)
6. [Network Scene Loader](#6-network-scene-loader)
7. [Network Timer](#7-network-timer)
8. [Assembly Definitions](#8-assembly-definitions)

---

## 1. Singletons

### `NetworkSingleton<T> : NetworkBehaviour`

NGO-aware singleton. Instance set in `Awake`, network features active after
`OnNetworkSpawn`. Subclass when your manager is always spawned by NGO.

```csharp
public class MyManager : NetworkSingleton<MyManager>, INetworkSingletonLifecycle
{
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsServer) InitServer();
    }
    public void OnNetworkSpawned(bool isServer, bool isHost, bool isClient, bool isOwner) { }
    public void OnNetworkDespawned() { }
    public void OnNetworkSceneChange(string prev, string curr) { }
}

MyManager.Instance.DoSomething();
bool active = MyManager.IsNetworkActive();
```

**Static API**

| Member | Description |
|---|---|
| `Instance` | Auto-creates if missing |
| `HasInstance` | Null-safe existence check |
| `TryGetInstance()` | Returns null if not found |
| `IsNetworkActive()` | True when spawned AND network is listening |
| `IsServerAuthority()` | True when `NetworkObject.IsOwnedByServer` |
| `Reset()` | Destroy + clear static refs |

**`INetworkSingletonLifecycle`**

```csharp
void OnNetworkSpawned(bool isServer, bool isHost, bool isClient, bool isOwner);
void OnNetworkDespawned();
void OnNetworkSceneChange(string previousScene, string currentScene);
```

---

### `HybridNetworkSingleton<T> : NetworkBehaviour`

Instance available immediately in `Awake` — before any NGO spawn.
Network features layer on top when spawned. Works offline too.
Use for managers that need to function in both online and offline contexts.

```csharp
public class GameStateManager : HybridNetworkSingleton<GameStateManager>
{
    protected override void Awake()
    {
        base.Awake();
        LoadLocalData(); // safe — instance ready now
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        // Network features now active
    }
}

GameStateManager.Instance.DoWork();     // always safe
bool online  = GameStateManager.IsNetworkReady();
bool offline = GameStateManager.IsAvailable();
```

**Differences from `NetworkSingleton<T>`**

| Feature | NetworkSingleton | HybridNetworkSingleton |
|---|---|---|
| Instance in Awake | ✓ | ✓ |
| Works offline | ✗ | ✓ |
| Auto-creates GO if missing | ✓ | ✓ |
| Persists by default | No | Yes |

**`IHybridNetworkSingletonLifecycle`**

```csharp
void OnNetworkSpawned(bool isServer, bool isHost, bool isClient, bool isOwner);
void OnNetworkDespawned();
void OnNetworkSceneChange(string previousScene, string currentScene);
void OnSceneChange(string sceneName); // non-NGO scene loads
```

---

## 2. Network Object Pool

### `MID_NetworkObjectPool : NetworkBehaviour`

Generic NGO-managed object pool. Uses `INetworkPrefabInstanceHandler` so NGO
calls the pool's `Instantiate`/`Destroy` internally — no extra spawn code needed.

**Setup**

1. Add component to a persistent NetworkBehaviour in your scene.
2. Fill `pooledPrefabsList` in inspector. Each prefab needs a `NetworkObject`.
3. Optionally add a component implementing `IPoolableNetworkObject` for reset/retrieve hooks.
4. Call `InitializePool()` before any spawning (server-side).

**Usage (server only)**

```csharp
MID_NetworkObjectPool.Singleton.InitializePool();

// Spawn
var netObj = MID_NetworkObjectPool.Singleton
    .GetNetworkObject(PoolableNetworkObjectType.MyWeapon, pos, rot);
netObj.Spawn();

// Return — call BEFORE Despawn
MID_NetworkObjectPool.Singleton
    .ReturnNetworkObject(netObj, PoolableNetworkObjectType.MyWeapon);
```

**Public API**

```csharp
void          InitializePool()
NetworkObject GetNetworkObject(PoolableNetworkObjectType type, Vector3 pos, Quaternion rot)
NetworkObject GetNetworkObject(int typeId, Vector3 pos, Quaternion rot)
void          ReturnNetworkObject(NetworkObject netObj, PoolableNetworkObjectType type)
void          ReturnNetworkObject(NetworkObject netObj, int typeId)
bool          IsRegistered(PoolableNetworkObjectType type)
bool          IsRegistered(int typeId)
void          ClearPool()
static MID_NetworkObjectPool Singleton { get; }
```

---

### `IPoolableNetworkObject`

Implement on any `NetworkBehaviour` on a pooled prefab.

```csharp
public class MyWeapon : NetworkBehaviour, IPoolableNetworkObject
{
    public void OnPoolReset()
    {
        // Called when returned to pool — disable visuals, clear state
        _spriteRenderer.enabled = false;
        _owner = null;
    }

    public void OnPoolRetrieve()
    {
        // Called just before handing to caller — apply spawn config
        _spriteRenderer.enabled = true;
    }
}
```

---

### `NetworkPoolTypeProviderSO : ScriptableObject`

Create via: `MidManStudio > Pool Type Provider (Network Object)`

Contributes entries to the generated `PoolableNetworkObjectType` enum.
Use the Pool Type Generator to rebuild after adding entries.

| Field | Description |
|---|---|
| `packageId` | Unique reverse-domain ID |
| `displayName` | Generator window label |
| `priority` | 0 = netcode reserved, 100+ = user game code |
| `entries` | List of `PoolEntryDefinition` |

---

### `NetworkPoolConfig`

Inspector pool entry for `MID_NetworkObjectPool`.

| Field | Description |
|---|---|
| `typeId` | Matches a `PoolableNetworkObjectType` value |
| `displayName` | Inspector label |
| `prefab` | Prefab with NetworkObject component |
| `prewarmCount` | Pre-instantiated instances on init |

---

## 3. Network Connection Manager

### `MID_NetworkConnectionManager : Singleton<MID_NetworkConnectionManager>`

Background internet connectivity monitor. Fires events on state change.
No game-specific dependencies.

```csharp
MID_NetworkConnectionManager.StartContinuousCheck();
MID_NetworkConnectionManager.onConnectionStatusChanged += OnConnChanged;

void OnConnChanged(bool connected)
{
    if (!connected) ShowNoInternetPopup();
}

// Slow down polling while showing error (3× normal interval)
MID_NetworkConnectionManager.SetIntervalMultiplier(3f);

// One-off check
bool ok = await MID_NetworkConnectionManager.ConfirmConnectionAsync();

// Synchronous fallback (blocks ~3s max)
bool quick = MID_NetworkConnectionManager.CheckSynchronous();

MID_NetworkConnectionManager.StopContinuousCheck();
```

**Static API**

```csharp
static void   StartContinuousCheck()
static void   StopContinuousCheck()
static Task<bool> ConfirmConnectionAsync()
static bool   CheckSynchronous()
static void   SetIntervalMultiplier(float multiplier) // 1.0 = default
static void   SetCheckMethod(ConnectionCheckMethod method)
static bool   IsConnected { get; }
static bool   IsChecking  { get; }
static event  Action<bool> onConnectionStatusChanged
static event  Action<bool> onCheckCompleted
```

**`ConnectionCheckMethod` enum**

```csharp
Ping          // ICMP to 1.1.1.1 (default)
HttpRequest   // GET unity3d.com
DnsLookup     // DNS resolution for cloudflare.com
TcpConnection // TCP port 443 to cloudflare.com
HttpPing      // HTTP GET speed.cloudflare.com
```

---

## 4. Network RPC Queue

### `MID_NetworkRPCQueue : NetworkBehaviour`

Batches NGO RPC payloads into one send per network tick.
Reduces packet overhead when many small state updates fire in one frame.

Payload types must implement both `IMIDRPCPayload` and `INetworkSerializable`.

```csharp
// Define a payload
public struct HitEvent : IMIDRPCPayload, INetworkSerializable
{
    public ulong TargetId;
    public float Damage;
    public string CollapseKey => null; // null = never deduplicate

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref TargetId);
        s.SerializeValue(ref Damage);
    }
}

// Register flush handler (in OnNetworkSpawn)
MID_NetworkRPCQueue.Instance.RegisterChannel<HitEvent>(FlushHits);

// Enqueue from any system — batched automatically
MID_NetworkRPCQueue.Instance.Enqueue(new HitEvent { TargetId = id, Damage = 10f });

// Flush handler receives the whole batch as one call
void FlushHits(List<HitEvent> batch) =>
    SendHitBatchClientRpc(batch.ToArray());
```

**`IMIDRPCPayload`**

```csharp
string CollapseKey { get; }
// Non-null same key in one flush window → last-write-wins deduplication
// Null → all payloads kept (ordered)
```

**Static API**

```csharp
void RegisterChannel<T>(Action<List<T>> flushHandler)
void Enqueue<T>(T payload)
void UnregisterChannel<T>()
int  TotalFlushes { get; }
int  TotalPending()
static MID_NetworkRPCQueue Instance   { get; }
static bool                HasInstance { get; }
```

---

## 5. Local Multiplayer Lobby

### `LocalLobbyManager : NetworkBehaviour`

LAN / WiFi offline lobby manager. Zero game-specific dependencies.
Broadcasts / receives UDP discovery, manages player list, fires events.

**Setup**

1. Add to a persistent `NetworkBehaviour` with a `NetworkObject`.
2. Assign `NetworkManager` and `UnityTransport` in inspector.
3. Subscribe to events before calling any `Start*/Join*` methods.
4. Optionally call `SetTeamProvider(provider)` with your team logic.

**Hosting & Joining**

```csharp
_lobbyManager.StartHosting(new LocalLobbyConfig
{
    LobbyName  = "My Game",
    MaxPlayers = 4,
    GameMode   = "TeamDeathmatch",
    GameMap    = "GrassyLand",
    ServerPort = 7777
});

_lobbyManager.OnHostResult += ok => { if (ok) ShowLobbyPanel(); };

// Client
_lobbyManager.StartSearching();
_lobbyManager.OnLobbyDiscovered += lobby => AddLobbyRow(lobby);
_lobbyManager.JoinLobby(selectedLobby);
_lobbyManager.OnJoinResult += ok => { if (ok) ShowLobbyPanel(); };
```

**Player management**

```csharp
_lobbyManager.SetPlayerName("Hamid");
_lobbyManager.SetPlayerReady(clientId, true);
bool allReady = _lobbyManager.AreAllPlayersReady();
_lobbyManager.SetFillWithBots(true);
List<LocalLobbyPlayer> players = _lobbyManager.GetPlayers();
```

**Game start**

```csharp
// Host only — validates all ready, calls team provider, fires on all clients
_lobbyManager.RequestGameStart();

// All clients (including host)
_lobbyManager.OnGameStartReceived += snapshot =>
{
    // snapshot.Players has final team assignments
    SceneManager.LoadScene("GameScene");
};
```

**Events**

| Event | Signature | Description |
|---|---|---|
| `OnLobbyDiscovered` | `Action<LocalLobbyData>` | New UDP broadcast found |
| `OnLobbyRemoved` | `Action<string>` | Discovery timed out |
| `OnPlayerJoined` | `Action<LocalLobbyPlayer>` | Player joined |
| `OnPlayerLeft` | `Action<ulong>` | Player disconnected |
| `OnPlayerReadyStatusChanged` | `Action<LocalLobbyPlayer>` | Ready toggled |
| `OnHostResult` | `Action<bool>` | StartHosting completed |
| `OnJoinResult` | `Action<bool>` | JoinLobby completed |
| `OnLobbyDisbanded` | `Action` | Host left, client received disconnect |
| `OnNetworkStatusChanged` | `Action<string>` | WiFi/hotspot status change |
| `OnGameStartReceived` | `Action<LocalLobbySnapshot>` | Game is starting |

---

### `ILocalLobbyTeamProvider`

Inject custom team logic without creating a package dependency.

```csharp
int  OnPlayerJoined(ulong clientId, bool isHost);
void OnPlayerLeft(ulong clientId);
bool TryChangeTeam(ulong clientId, int targetTeamId);
int  GetTeamId(ulong clientId);
void OnPrepareGameStart(List<LocalLobbyPlayer> allPlayers);
string SerializeState();
void   DeserializeState(string data);
```

---

### `LocalLobbyUIManager : MonoBehaviour` (abstract)

Base class for lobby UI. Connects to `LocalLobbyManager` events and uses a
`MID_UIStateContext` (from `com.midmanstudio.utilities`) for panel state.

**Setup**

1. Create a `MID_UIStateContext` SO with `contextName = "Lobby"` and states:
   `NetworkCheck`, `Browse`, `Hosting`, `Joining`, `Loading`
2. Run: `MidManStudio > Utilities > UI State Context Generator` → produces `LobbyUIState.cs`
3. Assign the context SO to `LobbyContext` in inspector
4. Subclass and override the `On*` virtual hooks

```csharp
public class MyLobbyUI : LocalLobbyUIManager
{
    [SerializeField] private GameObject _browsePanel;
    [SerializeField] private GameObject _hostPanel;

    protected override void OnHostResult(bool success)
    {
        if (!success) ShowError("Failed to host.");
    }

    protected override void OnGameStartReceived(LocalLobbySnapshot snapshot)
    {
        SceneManager.LoadScene("GameScene");
    }

    // Buttons in your UI call these:
    public void OnHostButtonClicked() =>
        RequestHost(new LocalLobbyConfig { LobbyName = "My Lobby" });

    public void OnLeaveButtonClicked() =>
        RequestLeave();
}
```

**State navigation (protected)**

```csharp
void GoToNetworkCheck()
void GoToBrowse()
void GoToHosting()
void GoToJoining()
void GoToLoading()
void GoBack()
void ChangeState(int rawState)  // use (int)LobbyUIState.Browse directly
```

---

### `LocalLobbyData`

Discovered lobby descriptor. Carried by UDP and passed to UI events.

| Field | Type | Description |
|---|---|---|
| `LobbyName` | string | |
| `HostName` | string | |
| `HostAddress` | string | IP of host device |
| `Port` | int | |
| `CurrentPlayers` | int | |
| `MaxPlayers` | int | |
| `GameMode` | string | Opaque game-defined string |
| `GameMap` | string | Opaque game-defined string |
| `CustomData` | string | Free-form JSON for game-specific fields |
| `IsFull` | bool | `CurrentPlayers >= MaxPlayers` |
| `Key` | string | `"ip:port"` — unique lobby identifier |

---

### `LocalLobbyPlayer`

Player representation inside a lobby session.

| Field | Type | Description |
|---|---|---|
| `ClientId` | ulong | NGO client ID (or >10000 for bots) |
| `PlayerName` | string | |
| `PlayerIconId` | string | Game-defined icon key |
| `IsReady` | bool | |
| `IsHost` | bool | |
| `IsBot` | bool | |
| `TeamId` | int | -1 = unassigned |

---

### `LocalLobbySnapshot`

Passed to `OnGameStartReceived`. Contains the final state at game start.

```csharp
LocalLobbyData              LobbyData  // config at game start
List<LocalLobbyPlayer>      Players    // final player list with team IDs
```

---

### `MobileNetworkStatusMonitor : MonoBehaviour`

Monitors WiFi / hotspot / mobile-data status on mobile devices.

```csharp
_monitor.OnNetworkStatusChanged += status =>
{
    bool canHost = status is "WIFI_CONNECTED" or "HOTSPOT";
    bool canJoin = status == "WIFI_CONNECTED";
    hostButton.interactable = canHost;
    joinButton.interactable = canJoin;
};

string msg = _monitor.GetStatusMessage();  // human-readable
```

**Status values**

| Value | Host | Join | Description |
|---|---|---|---|
| `WIFI_CONNECTED` | ✓ | ✓ | Standard WiFi |
| `HOTSPOT` | ✓ | ✗ | Device is the hotspot |
| `MOBILE_DATA` | ✗ | ✗ | Cellular only |
| `NO_NETWORK` | ✗ | ✗ | No connectivity |

---

### `PlayerOfflineIdentity : Singleton<PlayerOfflineIdentity>`

Persistent offline player identity. Saved to `PlayerPrefs`.

```csharp
PlayerOfflineIdentity.Instance.SetPlayerName("Hamid");
PlayerOfflineIdentity.Instance.SetPlayerIconId("warrior");

string name   = PlayerOfflineIdentity.Instance.PlayerName;
string iconId = PlayerOfflineIdentity.Instance.PlayerIconId;

// Export for online account migration
var snapshot = PlayerOfflineIdentity.Instance.ExportForOnlineAccount();
```

---

## 6. Network Scene Loader

### `MID_NetworkSceneLoader : HybridNetworkSingleton<...>` implements `ISceneLoader`

NGO-managed additive scene loader. Host/server triggers load, all clients receive
it automatically via NGO's scene manager.

```csharp
// Wire into MID_SceneTransitionController
MID_SceneTransitionController.Instance.SetNetworkLoader(
    MID_NetworkSceneLoader.Instance);

// Load (host only)
MID_NetworkSceneLoader.Instance.LoadScene(
    (int)SceneId.GameplayMap, SceneLoadType.NetworkAdditive);
```

**Additional API beyond ISceneLoader**

```csharp
void SetPlayerReady(ulong clientId, bool ready)
bool IsPlayerReady(ulong clientId)
bool AreAllPlayersReady()
int  GetCurrentActiveSceneId()
bool IsTransitionInProgress()
event Action<ulong, bool>              OnPlayerReadinessChanged
event Action<SceneEventProgressStatus> OnSceneEventProgressUpdate
```

---

## 7. Network Timer

### `NetworkTimer`

Lightweight fixed-interval tick timer for server/client loops.

```csharp
var timer = new NetworkTimer(serverTickRate: 60f);

void Update()
{
    timer.Update(Time.deltaTime);
    while (timer.ShouldTick())
        RunServerTick(timer.CurrentTick);
}

// Client interpolation
float alpha = timer.LerpFraction; // 0..1 between ticks
```

```csharp
NetworkTimer(float serverTickRate)
void  Update(float deltaTime)
bool  ShouldTick()
void  Reset()
void  SetTickRate(float tickRate)
float MinTimeBetweenTicks { get; }
int   CurrentTick         { get; }
float LerpFraction        { get; }
```

---

## 8. Assembly Definitions

### Runtime — `MidManStudio.Netcode`

**Path:** `packages/com.midmanstudio.netcode/Runtime/MidManStudio.Netcode.asmdef`

```json
{
  "name": "MidManStudio.Netcode",
  "rootNamespace": "MidManStudio.Core.Netcode",
  "references": ["MidManStudio.Utilities", "Unity.Netcode.Runtime"],
  "autoReferenced": true
}
```

### Editor — `MidManStudio.Netcode.Editor`

**Path:** `packages/com.midmanstudio.netcode/Editor/MidManStudio.Netcode.Editor.asmdef`

```json
{
  "name": "MidManStudio.Netcode.Editor",
  "rootNamespace": "MidManStudio.Core.Netcode.Editor",
  "references": [
    "MidManStudio.Utilities",
    "MidManStudio.Utilities.Editor",
    "MidManStudio.Netcode",
    "Unity.Netcode.Runtime"
  ],
  "includePlatforms": ["Editor"],
  "autoReferenced": false
}
```

### Reference Diagram

```
YourGame.asmdef
├── MidManStudio.Utilities      (autoReferenced — implicit)
└── MidManStudio.Netcode        (autoReferenced — implicit)
    └── Unity.Netcode.Runtime

YourGame.Editor.asmdef
├── MidManStudio.Utilities.Editor
└── MidManStudio.Netcode.Editor
```
