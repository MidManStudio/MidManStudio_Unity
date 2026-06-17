# com.midmanstudio.netcode — API Catalog

`com.midmanstudio.netcode` v1.0.0  
Assembly: `MidManStudio.Netcode`  
Namespace root: `MidManStudio.Netcode`  
Requires: `com.midmanstudio.utilities 1.0.0`, `com.unity.netcode.gameobjects 1.7.1+`, `com.unity.collections 2.2.1+`

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

**Namespace:** `MidManStudio.Netcode`  
**File:** `Runtime/Singleton/NetworkSingleton.cs`

NGO-aware singleton. Instance is set in `Awake`; network features (RPCs, ownership, IsServer etc.) are only valid after `OnNetworkSpawn`. Use when the manager is always spawned by NGO.

```csharp
public class MyNetworkManager : NetworkSingleton<MyNetworkManager>,
                                INetworkSingletonLifecycle
{
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsServer) InitializeServerState();
    }

    public void OnNetworkSpawned(bool isServer, bool isHost, bool isClient, bool isOwner) { }
    public void OnNetworkDespawned() { }
    public void OnNetworkSceneChange(string prev, string curr) { }
}

MyNetworkManager.Instance.DoSomething();
bool ready = MyNetworkManager.IsNetworkActive();
```

**Static API**

| Member | Type | Description |
|---|---|---|
| `Instance` | `T` | Returns or finds existing instance; creates a new GO if none found |
| `HasInstance` | `bool` | Null-safe existence check |
| `TryGetInstance()` | `T` | Returns null if not found |
| `Current` | `T` | Alias for Instance |
| `IsNetworkActive()` | `bool` | True when spawned AND NetworkManager is listening |
| `IsServerAuthority()` | `bool` | True when `NetworkObject.IsOwnedByServer` |
| `Reset()` | `void` | Destroy + clear static refs |

**`INetworkSingletonLifecycle`**

```csharp
void OnNetworkSpawned(bool isServer, bool isHost, bool isClient, bool isOwner);
void OnNetworkDespawned();
void OnNetworkSceneChange(string previousScene, string currentScene);
```

---

### `HybridNetworkSingleton<T> : NetworkBehaviour`

**Namespace:** `MidManStudio.Netcode`  
**File:** `Runtime/Singleton/HybridNetworkSingleton.cs`

Instance is available immediately in `Awake` — before any NGO spawn. Network features layer on top when spawned. Persists across scenes by default. Use for managers that must function in both online and offline contexts.

```csharp
public class GameStateManager : HybridNetworkSingleton<GameStateManager>
{
    protected override void Awake()
    {
        base.Awake();
        LoadLocalData(); // safe — instance is ready; network is NOT required yet
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        // network features now active
    }
}

GameStateManager.Instance.DoWork();
bool online  = GameStateManager.IsNetworkReady();
bool exists  = GameStateManager.IsAvailable();
```

**Static API**

| Member | Type | Description |
|---|---|---|
| `Instance` | `T` | Always returns an instance; creates GO if none exists |
| `HasInstance` | `bool` | Null-safe existence check |
| `TryGetInstance()` | `T` | Returns null if not found |
| `Current` | `T` | Alias for Instance |
| `IsAvailable()` | `bool` | True if instance exists (online or offline) |
| `IsNetworkReady()` | `bool` | True when spawned AND NetworkManager is listening |
| `IsNetworkSpawned()` | `bool` | True when NGO has spawned this object |
| `IsServerAuthority()` | `bool` | True when `NetworkObject.IsOwnedByServer` |
| `GetExistingInstance()` | `T` | Find in scene without creating |
| `Reset()` | `void` | Destroy + clear static refs |

**Comparison with `NetworkSingleton<T>`**

| Feature | NetworkSingleton | HybridNetworkSingleton |
|---|---|---|
| Instance in Awake | ✓ | ✓ |
| Works offline | ✗ | ✓ |
| Persists across scenes by default | No | Yes |
| Auto-creates GO if missing | ✓ | ✓ |

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

**Namespace:** `MidManStudio.Netcode.Pools`  
**File:** `Runtime/PoolSystems/MID_NetworkObjectPool.cs`

Generic NGO-managed object pool. Uses `INetworkPrefabInstanceHandler` so NGO's own `Spawn`/`Despawn` path calls into the pool — no extra spawn code needed. **Auto-initializes in `OnNetworkSpawn`**; `InitializePool()` is idempotent if called manually.

**Setup**

1. Add component to a persistent `NetworkBehaviour` in your scene.
2. Fill `pooledPrefabsList` in inspector. Each prefab needs a `NetworkObject`.
3. Optionally add a component implementing `IPoolableNetworkObject` to each prefab for reset/retrieve hooks.

**Usage (server-side only)**

```csharp
// Spawn
var netObj = MID_NetworkObjectPool.Singleton
    .GetNetworkObject(PoolableNetworkObjectType.MyWeapon, pos, rot);
netObj.Spawn();

// Return — call BEFORE Despawn
MID_NetworkObjectPool.Singleton
    .ReturnNetworkObject(netObj, PoolableNetworkObjectType.MyWeapon);
```

**Public API**

| Member | Returns | Description |
|---|---|---|
| `InitializePool()` | `void` | Idempotent — called automatically in `OnNetworkSpawn` |
| `GetNetworkObject(PoolableNetworkObjectType type, Vector3 pos, Quaternion rot)` | `NetworkObject` | Retrieve from pool; creates new instance if pool is empty |
| `GetNetworkObject(PoolableNetworkObjectType type)` | `NetworkObject` | Overload at `Vector3.zero / Quaternion.identity` |
| `ReturnNetworkObject(NetworkObject netObj, PoolableNetworkObjectType type)` | `void` | Reset and return to pool; call BEFORE `Despawn` |
| `IsRegistered(PoolableNetworkObjectType type)` | `bool` | Check if type has a pool entry |
| `ClearPool()` | `void` | Remove all handlers and empty all queues |
| `Singleton` | `MID_NetworkObjectPool` | Static instance reference |

---

### `IPoolableNetworkObject`

**Namespace:** `MidManStudio.Netcode.Pools`  
**File:** `Runtime/PoolSystems/IPoolableNetworkObject.cs`

Implement on any `NetworkBehaviour` component on a pooled prefab.

```csharp
public class MyWeapon : NetworkBehaviour, IPoolableNetworkObject
{
    public void OnPoolReset()
    {
        // Called on return: disable visuals, stop effects, clear references
        _spriteRenderer.enabled = false;
        _owner = null;
    }

    public void OnPoolRetrieve()
    {
        // Called just before handing to caller: apply spawn config
        _spriteRenderer.enabled = true;
    }
}
```

---

### `NetworkPoolTypeProviderSO : ScriptableObject`

**Namespace:** `MidManStudio.Netcode.Generator`  
**File:** `Runtime/PoolSystems/Config/NetworkPoolTypeProviderSO.cs`  
**Create via:** `MidManStudio > Netcode > Pool Type Provider (Network Object)`

Contributes entries to the generated `PoolableNetworkObjectType` enum.

| Field | Type | Description |
|---|---|---|
| `packageId` | `string` | Unique reverse-domain ID (e.g. `com.mygame`) |
| `displayName` | `string` | Generator window label |
| `priority` | `int` | 0 = netcode reserved, 10 = projectile system, 100+ = game code |
| `entries` | `List<PoolEntryDefinition>` | Pool type entries |

Run `MidManStudio > Utilities > Pool Type Generator > Generate Now` after adding entries.

---

### `NetworkPoolConfig`

**Namespace:** `MidManStudio.Netcode.Pools`

Inspector entry for `MID_NetworkObjectPool.pooledPrefabsList`.

| Field | Type | Description |
|---|---|---|
| `networkType` | `PoolableNetworkObjectType` | Matches a generated enum value |
| `displayName` | `string` | Inspector label (optional) |
| `prefab` | `GameObject` | Prefab with `NetworkObject` component |
| `prewarmCount` | `int` | Pre-instantiated instances on init |

---

### `PoolableNetworkObjectType` (generated enum)

**Namespace:** `MidManStudio.Core.Pools`  
**File:** `Runtime/PoolSystems/PoolableNetworkObjectType.cs`  
**Auto-generated** — do not edit manually. Regenerate via `MidManStudio > Utilities > Pool Type Generator`.

**Priority blocks:**

| Priority | Block | Reserved for |
|---|---|---|
| 0 | 0–99 | `com.midmanstudio.netcode` (no entries by default) |
| 10 | 100–199 | `com.midmanstudio.projectilesystem` |
| 100+ | 200+ | Your game |

---

## 3. Network Connection Manager

### `MID_NetworkConnectionManager : Singleton<MID_NetworkConnectionManager>`

**Namespace:** `MidManStudio.Netcode`  
**File:** `Runtime/Connection/MID_NetworkConnectionManager.cs`

Background internet connectivity monitor. Fires events on state change. No game-specific dependencies.

```csharp
MID_NetworkConnectionManager.StartContinuousCheck();
MID_NetworkConnectionManager.onConnectionStatusChanged += OnConnChanged;

void OnConnChanged(bool connected)
{
    if (!connected) ShowNoInternetPopup();
}

// Slow polling while showing error (3× normal interval)
MID_NetworkConnectionManager.SetIntervalMultiplier(3f);
// Restore
MID_NetworkConnectionManager.SetIntervalMultiplier(1f);

// One-off async check
bool ok = await MID_NetworkConnectionManager.ConfirmConnectionAsync();

// Synchronous fallback — blocks caller up to ~3 seconds
bool quick = MID_NetworkConnectionManager.CheckSynchronous();

MID_NetworkConnectionManager.StopContinuousCheck();
```

**Static API**

| Member | Returns | Description |
|---|---|---|
| `StartContinuousCheck()` | `void` | Begin background polling loop |
| `StopContinuousCheck()` | `void` | Stop background polling loop |
| `ConfirmConnectionAsync()` | `Task<bool>` | One-off async check |
| `CheckSynchronous()` | `bool` | Blocking TCP check; max ~3s |
| `SetIntervalMultiplier(float multiplier)` | `void` | Scale polling interval; 1.0 = default |
| `SetCheckMethod(ConnectionCheckMethod method)` | `void` | Change check method at runtime |
| `IsConnected` | `bool` | Last known connection state |
| `IsChecking` | `bool` | True while background loop is running |
| `onConnectionStatusChanged` | `event Action<bool>` | Fires on the main thread when state changes |
| `onCheckCompleted` | `event Action<bool>` | Fires after every check (connected or not) |

---

### `ConnectionCheckMethod` (enum)

**Namespace:** `MidManStudio.Netcode`

| Value | Method | Target |
|---|---|---|
| `Ping` | ICMP ping | `1.1.1.1` (default) |
| `HttpRequest` | UnityWebRequest GET | `unity3d.com` |
| `DnsLookup` | `Dns.GetHostEntryAsync` | `cloudflare.com` |
| `TcpConnection` | TCP connect | `cloudflare.com:443` |
| `HttpPing` | `HttpClient` GET | `speed.cloudflare.com` |

---

## 4. Network RPC Queue

### `MID_NetworkRPCQueue : NetworkBehaviour`

**Namespace:** `MidManStudio.Netcode`  
**File:** `Runtime/RPCQueue/MID_NetworkRPCQueue.cs`

Batches NGO RPC payloads into one send per flush tick. Reduces packet overhead when many small state updates fire in the same frame. Payload type T must implement both `IMIDRPCPayload` and `INetworkSerializable`.

```csharp
// 1. Define payload
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

// 2. Register flush handler (in OnNetworkSpawn)
MID_NetworkRPCQueue.Instance.RegisterChannel<HitEvent>(FlushHits);

// 3. Enqueue — batched automatically each tick
MID_NetworkRPCQueue.Instance.Enqueue(new HitEvent { TargetId = id, Damage = 10f });

// 4. Flush handler receives the full batch as one call
void FlushHits(List<HitEvent> batch) =>
    SendHitBatchClientRpc(batch.ToArray());

// 5. Cleanup (in OnNetworkDespawn)
MID_NetworkRPCQueue.Instance.UnregisterChannel<HitEvent>();
```

**`IMIDRPCPayload`**

```csharp
string CollapseKey { get; }
// Non-null same key → last-write-wins within one flush window
// Null → all payloads kept in order
```

**Public API**

| Member | Returns | Description |
|---|---|---|
| `RegisterChannel<T>(Action<List<T>> flushHandler)` | `void` | Register flush handler for type T; overwrites if already registered |
| `Enqueue<T>(T payload)` | `void` | Add payload to T's channel; deduplicates if `CollapseKey` is non-null |
| `UnregisterChannel<T>()` | `void` | Remove channel for type T |
| `TotalFlushes` | `int` | Total flush cycles since startup |
| `TotalPending()` | `int` | Payloads pending across all channels |
| `Instance` | `MID_NetworkRPCQueue` | Static instance reference |
| `HasInstance` | `bool` | Null-safe existence check |

| Inspector Field | Type | Description |
|---|---|---|
| `_flushRate` | `float` | Flush cycles per second (default: 20) |

---

## 5. Local Multiplayer Lobby

### `LocalLobbyManager : NetworkBehaviour`

**Namespace:** `MidManStudio.Netcode.LocalMultiplayer`  
**File:** `Runtime/LocalMultiplayer/LocalLobbyManager.cs`

LAN/WiFi offline lobby manager using UDP broadcast discovery. Zero game-specific dependencies. Inject team logic via `SetTeamProvider`.

**Setup**
1. Add to a persistent `NetworkBehaviour` GameObject with a `NetworkObject` component.
2. Assign `NetworkManager` and `UnityTransport` in inspector.
3. Subscribe to events before calling any `Start*/Join*` methods.
4. Optionally call `SetTeamProvider(provider)` with your team logic.

> **Discovery interval note:** Default `_discoveryInterval` is 2 seconds. Do not lower it to 1 second on same-machine testing — at 1s with loopback + broadcast both firing, Unity Transport's default 128-packet receive queue fills up. Also increase `UnityTransport.MaxPacketQueueSize` to 256+ in the inspector if testing on one machine.

**Hosting & Joining**

```csharp
_lobbyManager.OnHostResult += ok => { if (ok) ShowLobbyPanel(); };
_lobbyManager.StartHosting(new LocalLobbyConfig
{
    LobbyName  = "My Game",
    MaxPlayers = 4,
    GameMode   = "Deathmatch",
    ServerPort = 7777
});

// Client
_lobbyManager.StartSearching();
_lobbyManager.OnLobbyDiscovered += lobby => AddLobbyRow(lobby);
_lobbyManager.OnJoinResult += ok => { if (ok) ShowLobbyPanel(); };
_lobbyManager.JoinLobby(selectedLobby);
```

**Player management**

```csharp
_lobbyManager.SetPlayerName("Hamid");
_lobbyManager.SetPlayerReady(localClientId, true);
bool allReady = _lobbyManager.AreAllPlayersReady();
_lobbyManager.SetFillWithBots(true);
List<LocalLobbyPlayer> players = _lobbyManager.GetPlayers();
int realCount = _lobbyManager.RealPlayerCount; // excludes bots
```

**Game start (host only)**

```csharp
_lobbyManager.RequestGameStart(); // validates all ready, calls team provider, fires on all clients

_lobbyManager.OnGameStartReceived += snapshot =>
{
    // snapshot.Players has final team assignments
    SceneManager.LoadScene("GameScene");
};
```

**Team change**

```csharp
bool ok = _lobbyManager.TryChangeTeam(clientId, targetTeamId);
// Client: fires ServerRpc and returns true (optimistic); listen to sync events for result
// Server: calls provider.TryChangeTeam() and returns actual result
```

**Events**

| Event | Signature | Description |
|---|---|---|
| `OnLobbyDiscovered` | `Action<LocalLobbyData>` | New UDP broadcast found |
| `OnLobbyRemoved` | `Action<string>` | Discovery timed out (key = `"ip:port"`) |
| `OnPlayerJoined` | `Action<LocalLobbyPlayer>` | Player joined (including bots) |
| `OnPlayerLeft` | `Action<ulong>` | Player disconnected or bot removed |
| `OnPlayerReadyStatusChanged` | `Action<LocalLobbyPlayer>` | Ready toggled |
| `OnHostResult` | `Action<bool>` | `StartHosting()` completed |
| `OnJoinResult` | `Action<bool>` | `JoinLobby()` completed |
| `OnLobbyDisbanded` | `Action` | Host left; client received disconnect |
| `OnNetworkStatusChanged` | `Action<string>` | WiFi/hotspot status change string |
| `OnGameStartReceived` | `Action<LocalLobbySnapshot>` | Game is starting — load scene here |
| `OnInitialized` | `Action` | Async init complete (after 0.1s delay) |

**Public API**

| Member | Returns | Description |
|---|---|---|
| `Instance` | `LocalLobbyManager` | Static accessor (FindAnyObjectByType fallback) |
| `HasInstance` | `bool` | Null-safe existence check |
| `IsHosting` | `bool` | True when this device is the NGO host |
| `IsSearching` | `bool` | True while UDP discovery client is running |
| `IsInLobby` | `bool` | True when connected as host or client |
| `IsInitialized` | `bool` | True after async init completes |
| `PlayerName` | `string` | Current local player name |
| `RealPlayerCount` | `int` | Player count excluding bots |
| `SetTeamProvider(ILocalLobbyTeamProvider)` | `void` | Inject custom team logic |
| `StartHosting(LocalLobbyConfig config = null)` | `void` | Begin hosting; fires `OnHostResult` |
| `StopHosting()` | `void` | Disconnect all clients and stop |
| `JoinLobby(LocalLobbyData lobby)` | `void` | Connect to discovered lobby; fires `OnJoinResult` |
| `LeaveLobby()` | `void` | Disconnect and clear state |
| `StartSearching()` | `void` | Begin UDP discovery client |
| `StopSearching()` | `void` | Stop UDP discovery client |
| `GetDiscoveredLobbies()` | `IReadOnlyDictionary<string, LocalLobbyData>` | Current discovery results |
| `SetPlayerName(string name)` | `void` | Set local player name (persisted to PlayerPrefs) |
| `SetPlayerIconId(string iconId)` | `void` | Set local player icon key |
| `SetPlayerReady(ulong clientId, bool ready)` | `void` | Toggle ready state; synced to all clients |
| `AreAllPlayersReady()` | `bool` | True when all real players are ready |
| `SetFillWithBots(bool fill)` | `void` | Toggle bot fill (host only) |
| `GetPlayers()` | `List<LocalLobbyPlayer>` | Snapshot of current player list |
| `GetCurrentLobby()` | `LocalLobbyData` | Current lobby descriptor or null |
| `RequestGameStart()` | `void` | Host only — validates ready, fires snapshot on all clients |
| `TryChangeTeam(ulong clientId, int targetTeamId)` | `bool` | Request team change (routed via ServerRpc on client) |
| `OpenHotspotSettings()` | `void` | Opens device hotspot settings (Android/iOS) |
| `OpenWiFiSettings()` | `void` | Opens device WiFi settings (Android/iOS) |

---

### `LocalLobbyConfig`

**Namespace:** `MidManStudio.Netcode.LocalMultiplayer`  
**File:** `Runtime/LocalMultiplayer/LocalLobbyManager.cs`

```csharp
public class LocalLobbyConfig
{
    public string LobbyName     = "Local Game";
    public int    MaxPlayers    = 4;
    public string GameMode      = "";   // opaque — lobby system carries verbatim
    public string GameMap       = "";   // opaque — lobby system carries verbatim
    public string CustomData    = "";   // free-form JSON for game-specific fields
    public int    ServerPort    = 7777;
    public int    BroadcastPort = 7778;
}
```

---

### `ILocalLobbyTeamProvider`

**Namespace:** `MidManStudio.Netcode.LocalMultiplayer`  
**File:** `Runtime/LocalMultiplayer/ILocalLobbyTeamProvider.cs`

Inject custom team logic without creating a package dependency.

```csharp
int    OnPlayerJoined(ulong clientId, bool isHost);          // server — returns assigned team ID
void   OnPlayerLeft(ulong clientId);                         // server
bool   TryChangeTeam(ulong clientId, int targetTeamId);      // server — return false if invalid/full
int    GetTeamId(ulong clientId);                            // -1 if unassigned
void   OnPrepareGameStart(List<LocalLobbyPlayer> allPlayers); // server — balance bots here
string SerializeState();                                     // for client sync RPC
void   DeserializeState(string data);                        // apply server state on client
```

---

### `LocalLobbyData`

**Namespace:** `MidManStudio.Netcode.LocalMultiplayer`  
**File:** `Runtime/LocalMultiplayer/LocalLobbyData.cs`

Discovered lobby descriptor. Carried by UDP and passed to UI events.

| Field | Type | Description |
|---|---|---|
| `LobbyName` | `string` | |
| `HostName` | `string` | |
| `HostAddress` | `string` | IP of host device |
| `Port` | `int` | |
| `CurrentPlayers` | `int` | |
| `MaxPlayers` | `int` | |
| `GameMode` | `string` | Opaque game-defined string |
| `GameMap` | `string` | Opaque game-defined string |
| `CustomData` | `string` | Free-form JSON; lobby system carries verbatim |
| `IsFull` | `bool` | `CurrentPlayers >= MaxPlayers` |
| `Key` | `string` | `"ip:port"` — unique lobby identifier |

---

### `LocalLobbyPlayer`

**Namespace:** `MidManStudio.Netcode.LocalMultiplayer`  
**File:** `Runtime/LocalMultiplayer/LocalLobbyPlayer.cs`

```csharp
public class LocalLobbyPlayer
{
    public ulong  ClientId;
    public string PlayerName;
    public string PlayerIconId;
    public bool   IsReady;
    public bool   IsHost;
    public bool   IsBot;
    public int    TeamId = -1; // -1 = unassigned; meaning defined by game

    public LocalLobbyPlayer(ulong clientId, string playerName,
                            bool isHost = false, bool isBot = false);
}
```

---

### `NetworkLobbyPlayerData` (struct)

**Namespace:** `MidManStudio.Netcode.LocalMultiplayer`  
**File:** `Runtime/LocalMultiplayer/LocalLobbyPlayer.cs`

NGO `NetworkList` wire format. `IsBot` is NOT in the wire format — bots are local to the host.

```csharp
public struct NetworkLobbyPlayerData : INetworkSerializable, IEquatable<NetworkLobbyPlayerData>
{
    public ulong              ClientId;
    public FixedString128Bytes PlayerName;
    public FixedString64Bytes  PlayerIconId;
    public bool               IsReady;
    public bool               IsHost;
    public int                TeamId;
}
```

---

### `LocalLobbySnapshot`

**Namespace:** `MidManStudio.Netcode.LocalMultiplayer`  
**File:** `Runtime/LocalMultiplayer/LocalLobbyPlayer.cs`

Passed to `OnGameStartReceived`. Contains the final state at game-start time.

```csharp
public class LocalLobbySnapshot
{
    public LocalLobbyData         LobbyData; // config at game start
    public List<LocalLobbyPlayer> Players;   // final player list with team IDs
}
```

---

### `LocalLobbyUIManager : MonoBehaviour` (abstract)

**Namespace:** `MidManStudio.Netcode.UI`  
**File:** `Runtime/LocalMultiplayer/LocalLobbyUIManager.cs`

Base class for lobby UI. Connects to `LocalLobbyManager` events and drives panel state via a `MID_UIStateContext` SO from `com.midmanstudio.utilities`.

**Setup**
1. Create a `MID_UIStateContext` SO with `contextName = "ProjLobby"` and states: `Main`, `Searching`, `InLobby`, `Loading`, `NetworkCheck`
2. Run `MidManStudio > Utilities > UI State Context Generator` → produces `ProjLobbyUIState.cs`
3. Assign the SO to `LobbyContext` in the inspector
4. Subclass and override virtual hooks

```csharp
[RequireComponent(typeof(Canvas))]
public class MyLobbyUI : LocalLobbyUIManager
{
    // Buttons call the protected helpers
    public void OnHostClicked()   => RequestHost(new LocalLobbyConfig { LobbyName = "My Game" });
    public void OnSearchClicked() => RequestGoToSearching();
    public void OnJoinClicked(LocalLobbyData lobby) => RequestJoin(lobby);
    public void OnLeaveClicked()  => RequestLeave();
    public void OnReadyClicked()  => RequestSetReady(localClientId, !isReady);
    public void OnStartClicked()  => RequestGameStart();

    protected override void OnSearchStarted()
    {
        // Clear stale lobby cards, show spinner
    }

    protected override void OnLobbyDiscovered(LocalLobbyData lobby)
    {
        // Add lobby card to list
    }

    protected override void OnHostResult(bool success)
    {
        // On success: already navigated to InLobby
        // On failure: already navigated back to Main
        if (!success) ShowError("Failed to host.");
    }

    protected override void OnGameStartReceived(LocalLobbySnapshot snapshot)
    {
        // Already navigated to Loading
        SceneManager.LoadScene("GameScene");
    }
}
```

**State machine**

| State | Bit | Description |
|---|---|---|
| `Main` | 1 | Player name field, Host button, Look for Lobbies button |
| `Searching` | 2 | Lobby list panel |
| `InLobby` | 4 | Lobby room (shared by host and joining client) |
| `Loading` | 8 | Connecting / loading overlay |
| `NetworkCheck` | 16 | No-network warning panel |

> `GoToBrowse()`, `GoToHosting()`, `GoToJoining()` are backward-compatible aliases for `GoToMain()` and `GoToInLobby()` respectively.

**Public properties**

| Member | Type | Description |
|---|---|---|
| `CurrentState` | `int` | Raw current state value from context |
| `CanGoBack` | `bool` | True if back navigation is available |

**Protected state navigation**

| Method | Description |
|---|---|
| `ChangeState(int newState)` | Transition by raw state int (from generated enum) |
| `GoBack()` | Pop context history one level |
| `GoToMain()` | Main screen (bit 1) |
| `GoToSearching()` | Lobby list screen (bit 2) |
| `GoToInLobby()` | In-lobby room (bit 4) |
| `GoToLoading()` | Loading overlay (bit 8) |
| `GoToNetworkCheck()` | Network warning panel (bit 16) |
| `GoToBrowse()` | Alias for `GoToMain()` |
| `GoToHosting()` | Alias for `GoToInLobby()` |
| `GoToJoining()` | Alias for `GoToInLobby()` |

**Protected action helpers**

| Method | Description |
|---|---|
| `RequestGoToSearching()` | Stop current search → `GoToSearching()` → start fresh search → `OnSearchStarted()` |
| `RequestHost(LocalLobbyConfig config)` | `GoToLoading()` + `StartHosting()` |
| `RequestJoin(LocalLobbyData lobby)` | `GoToLoading()` + `JoinLobby()` |
| `RequestLeave()` | `LeaveLobby()` + `GoToMain()` |
| `RequestStopHosting()` | `StopHosting()` + `GoToMain()` |
| `RequestStartSearch()` | `StartSearching()` |
| `RequestStopSearch()` | `StopSearching()` |
| `RequestSetReady(ulong clientId, bool ready)` | `SetPlayerReady()` passthrough |
| `RequestGameStart()` | `RequestGameStart()` passthrough |
| `RequestPlayerName(string name)` | `SetPlayerName()` passthrough |
| `CanHost()` | `MobileNetworkStatusMonitor.CanHost()` (true if monitor absent) |
| `CanJoin()` | `MobileNetworkStatusMonitor.CanJoin()` (true if monitor absent) |
| `GetDiscoveredLobbies()` | `IReadOnlyDictionary<string, LocalLobbyData>` |
| `GetPlayers()` | `List<LocalLobbyPlayer>` |
| `AreAllReady()` | `bool` |

**Virtual hooks**

| Method | When called | State already navigated |
|---|---|---|
| `OnLobbyDiscovered(LocalLobbyData lobby)` | New lobby found in scan | No |
| `OnLobbyRemoved(string lobbyKey)` | Discovered lobby timed out | No |
| `OnPlayerJoined(LocalLobbyPlayer player)` | Player joined | No |
| `OnPlayerLeft(ulong clientId)` | Player disconnected | No |
| `OnPlayerReadyChanged(LocalLobbyPlayer player)` | Ready toggled | No |
| `OnHostResult(bool success)` | `StartHosting` completed | InLobby (ok) / Main (fail) |
| `OnJoinResult(bool success)` | `JoinLobby` completed | InLobby (ok) / Main (fail) |
| `OnLobbyDisbanded()` | Host left | Main |
| `OnNetworkStatusChanged(string status)` | WiFi/hotspot status changed | No |
| `OnGameStartReceived(LocalLobbySnapshot snapshot)` | Game starting | Loading |
| `OnSearchStarted()` | After `RequestGoToSearching()` | Searching |

---

### `MobileNetworkStatusMonitor : MonoBehaviour`

**Namespace:** `MidManStudio.Netcode.LocalMultiplayer`  
**File:** `Runtime/LocalMultiplayer/MobileNetworkStatusMonitor.cs`

Monitors WiFi / hotspot / mobile-data status on mobile devices. Polls on an interval; fires events on state change.

```csharp
_monitor.OnNetworkStatusChanged += status =>
{
    hostButton.interactable = _monitor.CanHost();
    joinButton.interactable = _monitor.CanJoin();
    statusLabel.text = _monitor.GetStatusMessage();
};
```

| Member | Returns | Description |
|---|---|---|
| `StartMonitoring()` | `void` | Begin polling (called automatically on `OnEnable`) |
| `StopMonitoring()` | `void` | Stop polling (called automatically on `OnDisable`) |
| `ForceCheck()` | `void` | Immediate check and fire event if changed |
| `GetCurrentStatus()` | `string` | Current status string |
| `GetStatusMessage()` | `string` | Human-readable description |
| `CanHost()` | `bool` | True when status is `WIFI_CONNECTED` or `HOTSPOT` |
| `CanJoin()` | `bool` | True when status is `WIFI_CONNECTED` |
| `HasNetwork` | `bool` | True unless `NotReachable` |
| `OnNetworkStatusChanged` | `event Action<string>` | Fires when status string changes |

**Status values**

| Value | `CanHost()` | `CanJoin()` | Description |
|---|---|---|---|
| `WIFI_CONNECTED` | ✓ | ✓ | Standard WiFi |
| `HOTSPOT` | ✓ | ✗ | Device is running a hotspot |
| `MOBILE_DATA` | ✗ | ✗ | Cellular only |
| `NO_NETWORK` | ✗ | ✗ | No connectivity |

> In Editor, always returns `WIFI_CONNECTED`.

---

### `PlayerOfflineIdentity : Singleton<PlayerOfflineIdentity>`

**Namespace:** `MidManStudio.Netcode.LocalMultiplayer`  
**File:** `Runtime/LocalMultiplayer/PlayerOfflineIdentity.cs`

Persistent offline player identity. Survives scene loads via `DontDestroyOnLoad`. Saved to `PlayerPrefs`. `LocalLobbyManager` reads this automatically on `Awake` if an instance exists.

```csharp
PlayerOfflineIdentity.Instance.SetPlayerName("Hamid");
PlayerOfflineIdentity.Instance.SetPlayerIconId("warrior");

string name   = PlayerOfflineIdentity.Instance.PlayerName;
string iconId = PlayerOfflineIdentity.Instance.PlayerIconId;

// Export for online account migration — do not persist the snapshot itself
var snapshot = PlayerOfflineIdentity.Instance.ExportForOnlineAccount();
// snapshot.PlayerName, snapshot.PlayerIconId, snapshot.ExportedAtUtc
```

| Member | Returns | Description |
|---|---|---|
| `PlayerName` | `string` | Current player name |
| `PlayerIconId` | `string` | Current player icon key |
| `SetPlayerName(string name)` | `void` | Update name; persisted to PlayerPrefs; fires `OnPlayerNameChanged` |
| `SetPlayerIconId(string iconId)` | `void` | Update icon; persisted to PlayerPrefs; fires `OnPlayerIconIdChanged` |
| `ExportForOnlineAccount()` | `OfflineIdentitySnapshot` | Snapshot for online migration |
| `OnPlayerNameChanged` | `event Action<string>` | Fires when name changes |
| `OnPlayerIconIdChanged` | `event Action<string>` | Fires when icon changes |

---

## 6. Network Scene Loader

### `MID_NetworkSceneLoader : HybridNetworkSingleton<MID_NetworkSceneLoader>` implements `ISceneLoader`

**Namespace:** `MidManStudio.Netcode.SceneManagement`  
**File:** `Runtime/SceneManagement/MID_NetworkSceneLoader.cs`

NGO-managed additive scene loader. Implements `ISceneLoader` from `com.midmanstudio.utilities`. Host/server triggers loads; all clients receive scene events automatically via NGO's scene manager.

**Setup**
1. Add to a persistent `NetworkBehaviour` GameObject with a `NetworkObject` component.
2. Wire into the utilities scene controller: `MID_SceneTransitionController.Instance.SetNetworkLoader(MID_NetworkSceneLoader.Instance)`.
3. Only the host/server calls `LoadScene` — clients are synced automatically.

```csharp
// Load (host only)
MID_NetworkSceneLoader.Instance.LoadScene(
    (int)SceneId.GameplayMap, SceneLoadType.NetworkAdditive);

// Unload (host only)
MID_NetworkSceneLoader.Instance.UnloadScene((int)SceneId.GameplayMap);

// Track load progress
MID_NetworkSceneLoader.Instance.OnLoadProgressChanged += progress =>
    loadBar.value = progress; // 0..1 as clients report load complete

MID_NetworkSceneLoader.Instance.OnSceneLoadCompleted += sceneId =>
    Debug.Log($"Scene {sceneId} loaded on all clients.");
```

**ISceneLoader members**

| Member | Type | Description |
|---|---|---|
| `IsLoadingScene` | `bool` | True during NGO scene load |
| `CurrentLoadingSceneId` | `int` | Build index of scene being loaded; -1 if none |
| `OnLoadProgressChanged` | `Action<float>` | Progress 0..1 as clients complete load |
| `OnSceneLoadCompleted` | `Action<int>` | Fired when all clients finish loading |
| `OnSceneLoadFailed` | `Action<string>` | Fired on load error with error message |
| `LoadScene(int sceneId, SceneLoadType loadType, short delayMs)` | `void` | Host/server only |
| `UnloadScene(int sceneId)` | `void` | Host/server only |
| `IsSceneLoaded(int sceneId)` | `bool` | Check Unity scene manager |

**Additional API**

| Member | Returns | Description |
|---|---|---|
| `SetPlayerReady(ulong clientId, bool ready)` | `void` | Mark a client as ready for the next phase |
| `IsPlayerReady(ulong clientId)` | `bool` | Check one client's readiness |
| `AreAllPlayersReady()` | `bool` | True when all connected clients are marked ready |
| `GetCurrentActiveSceneId()` | `int` | Build index of last successfully loaded gameplay scene |
| `IsTransitionInProgress()` | `bool` | True during a load sequence |
| `OnPlayerReadinessChanged` | `event Action<ulong, bool>` | Fires when a client's readiness changes |
| `OnSceneEventProgressUpdate` | `event Action<SceneEventProgressStatus>` | Fires on NGO scene event milestones |

---

## 7. Network Timer

### `NetworkTimer`

**Namespace:** `MidManStudio.Netcode`  
**File:** `Runtime/Timer/NetworkTimer.cs`

Lightweight fixed-interval tick timer for server/client loops. Plain C# class — no MonoBehaviour.

```csharp
var timer = new NetworkTimer(serverTickRate: 60f);

void Update()
{
    timer.Update(Time.deltaTime);
    while (timer.ShouldTick())
        RunServerTick(timer.CurrentTick);
}

// Client interpolation between ticks
float alpha = timer.LerpFraction; // 0..1
```

| Member | Type | Description |
|---|---|---|
| `NetworkTimer(float serverTickRate)` | — | Constructor; sets `MinTimeBetweenTicks = 1 / tickRate` |
| `Update(float deltaTime)` | `void` | Advance accumulator; call once per `Update` or `FixedUpdate` |
| `ShouldTick()` | `bool` | Returns true and increments `CurrentTick` if enough time has elapsed; call in a `while` loop |
| `Reset()` | `void` | Zero accumulator and tick counter |
| `SetTickRate(float tickRate)` | `void` | Change tick rate at runtime; resets accumulator |
| `MinTimeBetweenTicks` | `float` | Seconds between ticks (1 / tickRate) |
| `CurrentTick` | `int` | Total ticks fired since creation or last `Reset()` |
| `LerpFraction` | `float` | Fractional progress toward next tick (0..1); use for client interpolation |

---

## 8. Assembly Definitions

### Runtime — `MidManStudio.Netcode`

**Path:** `packages/com.midmanstudio.netcode/Runtime/MidManStudio.Netcode.asmdef`

```json
{
  "name": "MidManStudio.Netcode",
  "rootNamespace": "MidManStudio.Netcode",
  "references": [
    "MidManStudio.Utilities",
    "Unity.Netcode.Runtime",
    "Unity.Collections"
  ],
  "autoReferenced": true,
  "versionDefines": [
    {
      "name": "com.unity.netcode.gameobjects",
      "expression": "1.0.0",
      "define": "MIDMAN_NGO"
    }
  ]
}
```

### Editor — `MidManStudio.Netcode.Editor`

**Path:** `packages/com.midmanstudio.netcode/Editor/MidManStudio.Netcode.Editor.asmdef`

```json
{
  "name": "MidManStudio.Netcode.Editor",
  "rootNamespace": "MidManStudio.Netcode.Editor",
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
    ├── MidManStudio.Utilities
    ├── Unity.Netcode.Runtime
    └── Unity.Collections

YourGame.Editor.asmdef
├── MidManStudio.Utilities.Editor
└── MidManStudio.Netcode.Editor
    ├── MidManStudio.Utilities
    ├── MidManStudio.Utilities.Editor
    ├── MidManStudio.Netcode
    └── Unity.Netcode.Runtime
```
