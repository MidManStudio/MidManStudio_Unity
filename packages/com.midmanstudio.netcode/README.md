# com.midmanstudio.netcode

**MidMan Studio Netcode Utilities** — NGO-specific utilities for Unity 2022.3+.  
Requires Unity Netcode for GameObjects (`com.unity.netcode.gameobjects`).  
Depends on `com.midmanstudio.utilities`.

---

## Installation

**Via git URL:**
```
https://github.com/YOUR_USERNAME/MidManStudio.git?path=/packages/com.midmanstudio.netcode#v1.0.0
```

**Dependencies:**
| Package | Version |
|---|---|
| `com.midmanstudio.utilities` | 1.1.0+ |
| `com.unity.netcode.gameobjects` | 1.7.1+ |

---

## What's included

| System | Description |
|---|---|
| **`NetworkSingleton<T>`** | NGO-aware singleton. Instance set in Awake, network features active post-spawn |
| **`HybridNetworkSingleton<T>`** | Instance available immediately in Awake, network features layer on spawn. Works offline too |
| **`MID_NetworkObjectPool`** | Generic NGO object pool. Game-specific cleanup via `IPoolableNetworkObject` interface |
| **`IPoolableNetworkObject`** | Interface for pooled NetworkBehaviours — implement for custom reset/retrieve logic |
| **`NetworkPoolTypeProviderSO`** | Provider asset contributing entries to the generated `PoolableNetworkObjectType` enum |
| **`MID_NetworkConnectionManager`** | Background internet connectivity monitor — Ping, HTTP, DNS, TCP check methods |
| **`NetworkTimer`** | Fixed-interval server tick timer with lerp fraction for client interpolation |

---

## Quick Start

### NetworkSingleton

Use when your manager is spawned by NGO and you need network authority checks.

```csharp
public class MyNetworkManager : NetworkSingleton<MyNetworkManager>,
                                INetworkSingletonLifecycle
{
    protected override void Awake()
    {
        base.Awake();
        // Instance is set here, but network is NOT ready yet
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        // Network is ready — safe to use IsServer, IsHost, IsClient
        if (IsServer) InitializeServerState();
    }

    // INetworkSingletonLifecycle
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

Use when your manager needs to work both online and offline.  
Instance is available before NGO spawns it.

```csharp
public class GameStateManager : HybridNetworkSingleton<GameStateManager>
{
    protected override void Awake()
    {
        base.Awake();
        // Instance immediately available — safe for offline use
        LoadLocalData();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        // Network features now active
    }
}

// Works offline and online
GameStateManager.Instance.DoWork();
bool online = GameStateManager.IsNetworkReady();
bool exists = GameStateManager.IsAvailable();
```

---

### Network Object Pool

```csharp
// 1. Create a NetworkPoolTypeProviderSO asset for your game entries
//    MidManStudio > Pool Type Provider (Network Object)
//    Set packageId, priority >= 100, add entry names

// 2. Generate: MidManStudio > Pool Type Generator > Generate Now

// 3. Add MID_NetworkObjectPool to a persistent NetworkBehaviour in your scene
//    Assign prefabs to the pooledPrefabsList inspector list

// 4. Implement IPoolableNetworkObject on your prefab
public class MyWeapon : NetworkBehaviour, IPoolableNetworkObject
{
    public void OnPoolReset()
    {
        // Disable visuals, clear references, reset state
        _spriteRenderer.enabled = false;
        _owner = null;
    }

    public void OnPoolRetrieve()
    {
        // Apply spawn config
        _spriteRenderer.enabled = true;
    }
}

// 5. Initialize (server-side, before any spawning)
MID_NetworkObjectPool.Singleton.InitializePool();

// 6. Spawn (server-side)
var netObj = MID_NetworkObjectPool.Singleton
    .GetNetworkObject(PoolableNetworkObjectType.MyWeapon, pos, rot);
netObj.Spawn();

// 7. Return (server-side, BEFORE Despawn)
MID_NetworkObjectPool.Singleton
    .ReturnNetworkObject(netObj, PoolableNetworkObjectType.MyWeapon);
```

---

### Network Connection Manager

```csharp
// Start background check (e.g. from a ServicesInitializer)
MID_NetworkConnectionManager.StartContinuousCheck();

// Subscribe to state changes
MID_NetworkConnectionManager.onConnectionStatusChanged += OnConnectionChanged;

void OnConnectionChanged(bool connected)
{
    if (!connected) ShowNoInternetPopup();
}

// Slow down polling when showing an error popup
MID_NetworkConnectionManager.SetIntervalMultiplier(3f); // 3x slower
// Restore
MID_NetworkConnectionManager.SetIntervalMultiplier(1f);

// One-off check
bool ok = await MID_NetworkConnectionManager.ConfirmConnectionAsync();

// Synchronous fallback for critical operations
bool quickCheck = MID_NetworkConnectionManager.CheckSynchronous();

// Stop when entering offline mode
MID_NetworkConnectionManager.StopContinuousCheck();
```

Check methods (set in inspector or at runtime):
`Ping` · `HttpRequest` · `DnsLookup` · `TcpConnection` · `HttpPing`

---

### NetworkTimer

```csharp
// Create with tick rate (ticks per second)
var timer = new NetworkTimer(serverTickRate: 60f);

// In Update:
timer.Update(Time.deltaTime);
while (timer.ShouldTick())
{
    RunServerTick(timer.CurrentTick);
}

// Client interpolation
float alpha = timer.LerpFraction; // 0..1 between ticks
```

---

## Network Pool Type Generator

The netcode package uses the same generator as utilities.  
Add a `NetworkPoolTypeProviderSO` to contribute entries to `PoolableNetworkObjectType`.

**Priority ranges for network pool:**
| Priority | Package |
|---|---|
| 0 | `com.midmanstudio.netcode` (reserved — no entries by default) |
| 10 | `com.midmanstudio.projectilesystem` |
| 100+ | Your game |

**Steps:**
1. `MidManStudio > Pool Type Provider (Network Object)`
2. Set `packageId`, `priority ≥ 100`, add entry names
3. `MidManStudio > Pool Type Generator > Generate Now`

---

## Coming Soon

- LAN / WiFi offline network manager
- Local multiplayer session manager  
- Client prediction utilities

---

## Supported Unity Versions

| Unity | Status |
|---|---|
| 2022.3 LTS | ✅ Primary target |
| 2023.x | ✅ Compatible |

---

## License

MIT — see `LICENSE.md`.
