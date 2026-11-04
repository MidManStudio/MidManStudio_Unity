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

**Via git URL** (Window > Package Manager > Add package from git URL). Tags in this repository are
`<package>/v<version>`:
```
https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.netcode#netcode/v1.0.0
```

Unity resolves the dependencies in `package.json` from a registry, not from git. When you install from git,
add `com.midmanstudio.utilities` first:
```
https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.utilities#utilities/v1.0.0
```

**Via local path (development — manifest.json):**
```json
"com.midmanstudio.netcode": "file:../../packages/com.midmanstudio.netcode"
```

---

## What's Included

| System | Namespace | Description |
|---|---|---|
| `NetworkSingleton<T>` | `MidManStudio.Netcode.Singleton` | NGO-aware singleton; network features post-spawn |
| `HybridNetworkSingleton<T>` | `MidManStudio.Netcode.Singleton` | Instance in Awake; works offline; persists across scenes |
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
| `MID_BitMask32` / `MID_BitMask64` | `MidManStudio.Netcode.Collections` | Single-bit flag sets that sync as one `NetworkVariable` |
| `MID_BitPacker64` + `MID_BitFieldLayout` | `MidManStudio.Netcode.Collections` | Several small enum/int/bool fields packed into one synced `ulong` |
| `MID_NetworkDictionary<TKey, TValue>` | `MidManStudio.Netcode.Collections` | Event-based synced dictionary |
| `MID_NetworkEvent<T>` | `MidManStudio.Netcode.Collections` | Fire-and-forget "raise it, everyone hears it" signal over an RPC |
| `MID_NetworkString<T>` | `MidManStudio.Netcode.Collections` | Fixed-capacity synced string; capacity set by the `FixedString` type |
| `MID_LagCompensator` + `MID_LagCompensatedTarget` | `MidManStudio.Netcode.LagCompensation` | Server-side rewind of hitboxes to a client's fire tick |
| `OwnerAuthoritativeNetworkTransform` | `MidManStudio.Netcode.Components` | `NetworkTransform` driven by the owning client |

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
var netObj = MID_NetworkObjectPool.Instance
    .GetNetworkObject(PoolableNetworkObjectType.MyWeapon, pos, rot);
netObj.Spawn();

// 6. Return (server-side — call BEFORE Despawn)
MID_NetworkObjectPool.Instance
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
// Needs: using System.Linq; using System.Collections.Generic;
public class MyTeamProvider : ILocalLobbyTeamProvider
{
    private readonly Dictionary<ulong, int> _teams = new();

    public int OnPlayerJoined(ulong clientId, bool isHost)
    {
        int team = isHost ? 0 : _teams.Count % 2; // simple round-robin
        _teams[clientId] = team;
        return team;
    }

    public void OnPlayerLeft(ulong clientId) => _teams.Remove(clientId);
    public bool TryChangeTeam(ulong clientId, int target) { _teams[clientId] = target; return true; }
    public int GetTeamId(ulong clientId) => _teams.TryGetValue(clientId, out int t) ? t : -1;
    public void OnPrepareGameStart(List<LocalLobbyPlayer> all) { }

    // The state travels to clients as a string. "id:team,id:team" is enough here.
    public string SerializeState() =>
        string.Join(",", _teams.Select(kv => $"{kv.Key}:{kv.Value}"));

    public void DeserializeState(string data)
    {
        _teams.Clear();
        if (string.IsNullOrEmpty(data)) return;
        foreach (var part in data.Split(','))
        {
            var kv = part.Split(':');
            _teams[ulong.Parse(kv[0])] = int.Parse(kv[1]);
        }
    }
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

// Load (host only). The loader always does an NGO additive load;
// the loadType and delayMs arguments are ignored.
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

### Networked Collections

`MidManStudio.Netcode.Collections`. The bit masks, the bit packer and the string are values you put inside
a `NetworkVariable<T>`. `MID_NetworkDictionary` is a network variable itself. `MID_NetworkEvent` is plain
C# plus one RPC and does not sync state.

```csharp
// Several small fields in one synced ulong. Build the layout once.
private static readonly MID_BitFieldLayout Layout = new MID_BitFieldLayout();
private static readonly MID_BitField StateField  = Layout.ReserveEnum<WeaponState>();
private static readonly MID_BitField ChargeField = Layout.Reserve(7);   // 0-127
private readonly NetworkVariable<MID_BitPacker64> _packed = new();

var packed = _packed.Value;
packed.SetEnum(StateField, WeaponState.Reloading);
packed.SetField(ChargeField, 42);
_packed.Value = packed;                       // one write, one delta

// Fixed-capacity string. The FixedString type sets the size (61 usable bytes here).
private readonly NetworkVariable<MID_NetworkString<FixedString64Bytes>> _name = new(
    new MID_NetworkString<FixedString64Bytes>("Player"));
_name.Value = "New Name";

// Synced dictionary with change events. TKey must be unmanaged + IEquatable, TValue unmanaged.
private readonly MID_NetworkDictionary<ulong, int> _scores = new();
_scores[clientId] = 10;
_scores.OnDictionaryChanged += evt => Debug.Log($"{evt.Type} {evt.Key} {evt.Value}");

// Fire-and-forget signal: one thin RPC per usage site, a C# event around it.
public readonly MID_NetworkEvent<Vector3> OnExplosion = new();
public void TriggerExplosion(Vector3 p) => OnExplosion.Raise(p, ExplosionClientRpc);
[ClientRpc] private void ExplosionClientRpc(Vector3 p) => OnExplosion.Receive(p);
```

`MID_NetworkEvent<T>` keeps no state, so a client that joins later does not see past events.

---

### Lag Compensation

Server-side. Add `MID_LagCompensator` to one persistent object in the networked scene and
`MID_LagCompensatedTarget` to every hitbox root that should be rewindable. Pass the shooter's fire tick
(a Netcode server tick) to `BeginRewind`; the scope restores every target when it is disposed.

```csharp
using (MID_LagCompensator.BeginRewind(clientFireTick))
{
    Physics.Raycast(...);   // targets are at their pose for clientFireTick here
}
```

`BeginRewind` does nothing (returns an empty scope) when no compensator exists or the tick is 0 or less.
A target registers with the compensator when it spawns, so the compensator has to be in the scene first.
Scopes cannot be nested.

---

### Owner-Authoritative Transform

The stock `NetworkTransform` is server-authoritative. Swap it for `OwnerAuthoritativeNetworkTransform`
(`MidManStudio.Netcode.Components`) on prefabs whose owning client moves them. It has the same Inspector
fields, so configured axes, thresholds and interpolation carry over. `NetworkRigidbody` follows the
authority of its `NetworkTransform`.

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

## Documentation

| File | Content |
|---|---|
| `APICATALOG.md` | API reference for every public type |
| `docs/com.midmanstudio.netcode.md` | Design decisions, limits and fix history per file |
| `Documentation~/index.md` | Getting started |
| `Documentation~/SceneSetup.md` | Hand-built test scenes for the pool, the lobby and lag compensation |
| `CHANGELOG.md` | Release notes |

---

## License

MIT — see `LICENSE.md`. `MID_NetworkDictionary` is adapted from MIT-licensed code, see
`THIRD-PARTY-NOTICES.md`.  
Copyright © 2026 Abdulhamid Manman Suleiman / MidMan Studio
