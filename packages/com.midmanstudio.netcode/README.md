# com.midmanstudio.netcode

**MidMan Studio Netcode Utilities** v1.0.0 — NGO-specific utilities for Unity 2022.3+.  
No game-specific dependencies. Builds on `com.midmanstudio.utilities`.

---

## Requirements

| Dependency | Version |
|---|---|
| Unity | 2022.3 LTS |
| `com.midmanstudio.utilities` | 1.0.0 |
| `com.unity.netcode.gameobjects` | 1.7.1+ |
| `com.unity.collections` | 2.2.1+ |

---

## Installation

**Via git URL:**
```
https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.netcode#v1.0.0
```

**Via local path (development — manifest.json):**
```json
"com.midmanstudio.netcode": "file:../../packages/com.midmanstudio.netcode"
```

---

## What's Included

| System | Namespace | Description |
|---|---|---|
| `NetworkSingleton<T>` | `MidManStudio.Netcode` | NGO-aware singleton; network features post-spawn |
| `HybridNetworkSingleton<T>` | `MidManStudio.Netcode` | Instance in Awake; works offline; persists across scenes |
| `MID_NetworkObjectPool` | `MidManStudio.Netcode.Pools` | Generic NGO object pool; auto-initializes on spawn |
| `IPoolableNetworkObject` | `MidManStudio.Netcode.Pools` | Reset/retrieve hooks for pooled prefabs |
| `NetworkPoolTypeProviderSO` | `MidManStudio.Netcode.Generator` | SO that contributes entries to the pool type enum |
| `MID_NetworkConnectionManager` | `MidManStudio.Netcode` | Background connectivity monitor; five check methods |
| `MID_NetworkRPCQueue` | `MidManStudio.Netcode` | Tick-driven batched RPC queue with collapse-key dedup |
| `LocalLobbyManager` | `MidManStudio.Netcode.LocalMultiplayer` | LAN/WiFi lobby; UDP discovery; team provider support |
| `LocalLobbyUIManager` | `MidManStudio.Netcode.UI` | Abstract base UI; MID_UIStateContext-backed state machine |
| `MobileNetworkStatusMonitor` | `MidManStudio.Netcode.LocalMultiplayer` | WiFi/hotspot/mobile-data status on mobile |
| `PlayerOfflineIdentity` | `MidManStudio.Netcode.LocalMultiplayer` | Persistent offline player identity |
| `MID_NetworkSceneLoader` | `MidManStudio.Netcode.SceneManagement` | NGO-managed additive scene loader; per-client readiness |
| `NetworkTimer` | `MidManStudio.Netcode` | Fixed-interval server tick timer with lerp fraction |

---

## Quick Start

### NetworkSingleton

Use when your manager is always spawned by NGO and only needs to function online.

```csharp
public class MyNetworkManager : NetworkSingleton<MyNetworkManager>,
                                INetworkSingletonLifecycle
{
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        // Network is ready here — safe to use IsServer, IsHost, IsClient
        if (IsServer) InitializeServerState();
    }

    public void OnNetworkSpawned(bool isServer, bool isHost, bool isClient, bool isOwner) { }
    public void OnNetworkDespawned() { }
    public void OnNetworkSceneChange(string prev, string curr) { }
}

// Access anywhere
MyNetworkManager.Instance.DoSomething();
bool ready = MyNetworkManager.IsNetworkActive();
```

---

### HybridNetworkSingleton

Use when your manager needs to work both online and offline. Instance is available immediately in `Awake`.

```csharp
public class GameStateManager : HybridNetworkSingleton<GameStateManager>
{
    protected override void Awake()
    {
        base.Awake();
        LoadLocalData(); // safe — instance is ready, network is NOT required
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        // Network features now active
    }
}

GameStateManager.Instance.DoWork();
bool online  = GameStateManager.IsNetworkReady();
bool exists  = GameStateManager.IsAvailable();
```

---

### Network Object Pool

The pool auto-initializes when its `NetworkObject` spawns — no manual `InitializePool()` call needed.

```csharp
// 1. Create a NetworkPoolTypeProviderSO for your game entries
//    MidManStudio > Netcode > Pool Type Provider (Network Object)
//    Set packageId, priority >= 100, add entry names

// 2. Generate: MidManStudio > Utilities > Pool Type Generator > Generate Now

// 3. Add MID_NetworkObjectPool to a persistent NetworkBehaviour in your scene.
//    Assign prefabs to pooledPrefabsList in the inspector.

// 4. Implement IPoolableNetworkObject on your prefab
public class MyWeapon : NetworkBehaviour, IPoolableNetworkObject
{
    public void OnPoolReset()
    {
        // Called on return — disable visuals, clear references
        _spriteRenderer.enabled = false;
        _owner = null;
    }

    public void OnPoolRetrieve()
    {
        // Called just before handing to caller
        _spriteRenderer.enabled = true;
    }
}

// 5. Spawn (server-side)
var netObj = MID_NetworkObjectPool.Singleton
    .GetNetworkObject(PoolableNetworkObjectType.MyWeapon, pos, rot);
netObj.Spawn();

// 6. Return (server-side — call BEFORE Despawn)
MID_NetworkObjectPool.Singleton
    .ReturnNetworkObject(netObj, PoolableNetworkObjectType.MyWeapon);
```

**Network pool priority ranges:**

| Priority | Reserved for |
|---|---|
| 0 | `com.midmanstudio.netcode` (no entries by default) |
| 10 | `com.midmanstudio.projectilesystem` |
| 100+ | Your game |

---

### Network Connection Manager

```csharp
// Start background check
MID_NetworkConnectionManager.StartContinuousCheck();
MID_NetworkConnectionManager.onConnectionStatusChanged += OnConnChanged;

void OnConnChanged(bool connected)
{
    if (!connected) ShowNoInternetPopup();
}

// Slow down polling while showing an error (3× normal interval)
MID_NetworkConnectionManager.SetIntervalMultiplier(3f);
// Restore
MID_NetworkConnectionManager.SetIntervalMultiplier(1f);

// One-off async check
bool ok = await MID_NetworkConnectionManager.ConfirmConnectionAsync();

// Synchronous fallback for critical paths (blocks ~3s max)
bool quick = MID_NetworkConnectionManager.CheckSynchronous();

MID_NetworkConnectionManager.StopContinuousCheck();
```

Check methods (set in inspector or at runtime via `SetCheckMethod`):  
`Ping` · `HttpRequest` · `DnsLookup` · `TcpConnection` · `HttpPing`

---

### Network RPC Queue

Batch many small state updates into one RPC per tick. Payload type T must implement `IMIDRPCPayload` AND `INetworkSerializable`.

```csharp
// Define payload
public struct HitEvent : IMIDRPCPayload, INetworkSerializable
{
    public ulong TargetId;
    public float Damage;
    public string CollapseKey => null; // null = never collapse; all hits kept

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref TargetId);
        s.SerializeValue(ref Damage);
    }
}

// In OnNetworkSpawn — register flush handler
MID_NetworkRPCQueue.Instance.RegisterChannel<HitEvent>(FlushHits);

// Enqueue from anywhere — batches automatically
MID_NetworkRPCQueue.Instance.Enqueue(new HitEvent { TargetId = id, Damage = 10f });

// Flush handler receives the full batch as one call per tick
void FlushHits(List<HitEvent> batch)
{
    SendHitBatchClientRpc(batch.ToArray());
}

// Cleanup in OnNetworkDespawn
MID_NetworkRPCQueue.Instance.UnregisterChannel<HitEvent>();
```

**Collapse key** — payloads with the same non-null key in one flush window deduplicate to last-write-wins. Return `null` to keep all payloads.

---

### Local Multiplayer Lobby

LAN/WiFi lobby using UDP broadcast discovery and Netcode for GameObjects. Zero game-specific dependencies — inject your team logic via `ILocalLobbyTeamProvider`.

**Hosting:**
```csharp
_lobbyManager.SetPlayerName("Hamid");
_lobbyManager.OnHostResult += ok => { if (ok) ShowLobbyPanel(); };
_lobbyManager.StartHosting(new LocalLobbyConfig
{
    LobbyName  = "My Game",
    MaxPlayers = 4,
    GameMode   = "Deathmatch",
    GameMap    = "GrassyLand",
    ServerPort = 7777
});
```

**Joining:**
```csharp
_lobbyManager.StartSearching();
_lobbyManager.OnLobbyDiscovered += lobby => AddLobbyCard(lobby);

// After player selects a lobby card
_lobbyManager.OnJoinResult += ok => { if (ok) ShowLobbyPanel(); };
_lobbyManager.JoinLobby(selectedLobby);
```

**In-lobby:**
```csharp
_lobbyManager.SetPlayerReady(localClientId, true);
bool canStart = _lobbyManager.AreAllPlayersReady();

// Host only
_lobbyManager.RequestGameStart();
_lobbyManager.OnGameStartReceived += snapshot =>
{
    // snapshot.Players has final team assignments
    SceneManager.LoadScene("GameScene");
};
```

**Team logic:**
```csharp
public class MyTeamProvider : ILocalLobbyTeamProvider
{
    private Dictionary<ulong, int> _teams = new();

    public int OnPlayerJoined(ulong clientId, bool isHost) =>
        isHost ? 0 : _teams.Count % 2; // simple round-robin

    public void OnPlayerLeft(ulong clientId) => _teams.Remove(clientId);
    public bool TryChangeTeam(ulong clientId, int target) { _teams[clientId] = target; return true; }
    public int GetTeamId(ulong clientId) => _teams.TryGetValue(clientId, out int t) ? t : -1;
    public void OnPrepareGameStart(List<LocalLobbyPlayer> all) { }
    public string SerializeState() => JsonUtility.ToJson(new { teams = _teams });
    public void DeserializeState(string data) { /* parse and apply */ }
}

_lobbyManager.SetTeamProvider(new MyTeamProvider());
```

---

### Local Lobby UI

Subclass `LocalLobbyUIManager` and wire your panels to the provided hooks.

**Required setup:**
1. Create a `MID_UIStateContext` SO with `contextName = "ProjLobby"` and states: `Main`, `Searching`, `InLobby`, `Loading`, `NetworkCheck`
2. Run `MidManStudio > Utilities > UI State Context Generator` — produces `ProjLobbyUIState.cs`
3. Assign the SO to `LobbyContext` in the inspector on your subclass

```csharp
public class MyLobbyUI : LocalLobbyUIManager
{
    [SerializeField] private GameObject _searchingPanel;

    // Called by "Host" button
    public void OnHostClicked() =>
        RequestHost(new LocalLobbyConfig { LobbyName = "My Game" });

    // Called by "Look for Lobbies" button — stops any running search,
    // transitions to Searching state, starts a fresh scan, calls OnSearchStarted()
    public void OnSearchClicked() => RequestGoToSearching();

    protected override void OnSearchStarted()
    {
        // Clear stale lobby cards, show spinner
    }

    protected override void OnLobbyDiscovered(LocalLobbyData lobby)
    {
        // Add lobby row to list
    }

    protected override void OnHostResult(bool success)
    {
        if (!success) ShowError("Failed to host.");
    }

    protected override void OnGameStartReceived(LocalLobbySnapshot snapshot)
    {
        SceneManager.LoadScene("GameScene");
    }

    // Called by "Leave" button
    public void OnLeaveClicked() => RequestLeave();
}
```

**State machine:**

| State | Bit | Shows |
|---|---|---|
| `Main` | 1 | Player name field, Host button, Look for Lobbies button |
| `Searching` | 2 | Lobby list, back button |
| `InLobby` | 4 | Lobby room (host and client share this panel) |
| `Loading` | 8 | Connecting / loading overlay |
| `NetworkCheck` | 16 | No-network warning panel |

`GoToHosting()` and `GoToJoining()` are backward-compatible aliases for `GoToInLobby()`.

---

### Network Scene Loader

NGO-managed additive scene loader. Only the host/server triggers loads — all clients receive the event automatically via NGO's scene manager.

```csharp
// Wire into the utilities scene transition controller
MID_SceneTransitionController.Instance.SetNetworkLoader(
    MID_NetworkSceneLoader.Instance);

// Load (host only)
MID_NetworkSceneLoader.Instance.LoadScene(
    (int)SceneId.GameplayMap, SceneLoadType.NetworkAdditive);

// Track per-client readiness
MID_NetworkSceneLoader.Instance.OnPlayerReadinessChanged += (clientId, ready) =>
    UpdateReadyIndicator(clientId, ready);

MID_NetworkSceneLoader.Instance.SetPlayerReady(localClientId, true);
bool allReady = MID_NetworkSceneLoader.Instance.AreAllPlayersReady();
```

---

### Network Timer

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

---

## Mobile Network Monitor

Detect WiFi vs hotspot vs mobile-data vs none on Android/iOS. Determines whether the local device can host, join, or neither.

```csharp
_monitor.OnNetworkStatusChanged += status =>
{
    hostButton.interactable = _monitor.CanHost(); // WiFi or hotspot
    joinButton.interactable = _monitor.CanJoin(); // WiFi only
    statusLabel.text = _monitor.GetStatusMessage();
};
```

| Status | `CanHost()` | `CanJoin()` | Description |
|---|---|---|---|
| `WIFI_CONNECTED` | ✓ | ✓ | Standard WiFi |
| `HOTSPOT` | ✓ | ✗ | Device is the hotspot |
| `MOBILE_DATA` | ✗ | ✗ | Cellular only |
| `NO_NETWORK` | ✗ | ✗ | No connectivity |

---

## Supported Unity Versions

| Unity | Status |
|---|---|
| 2022.3 LTS | ✅ Primary target |
| 2023.x | ✅ Compatible |

---

## License

MIT — see `LICENSE.md`.  
Copyright © 2026 Abdulhamid Manman Suleiman / MidMan Studio
