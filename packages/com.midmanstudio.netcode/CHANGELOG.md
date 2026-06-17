# com.midmanstudio.netcode — Package Catalog
**MidMan Studio Netcode Utilities** v1.0.0 | Unity 2022.3+  
Last updated: 2026-06-17

> ⚠ **Discrepancy fixes vs. the previous APICATALOG are marked with ⚠ FIX.**  
> 🗑 Items marked **DELETE** are placeholder or incorrect content that should be removed.

---

## Full Folder Tree

```
com.midmanstudio.netcode/
│
├── package.json                                      ← ⚠ FIX: add com.unity.collections 2.2.1 dependency
├── CHANGELOG.md
├── LICENSE.md
├── README.md
├── APICATALOG.md
│
├── Runtime/                                          ← MidManStudio.Netcode.asmdef
│   ├── MidManStudio.Netcode.asmdef                  ← Correct — autoReferenced: true
│   │
│   ├── Singleton/
│   │   ├── NetworkSingleton.cs                       ← NGO-aware singleton; instance set in Awake,
│   │   │                                                network features active after OnNetworkSpawn
│   │   └── HybridNetworkSingleton.cs                ← Instance available in Awake before spawn;
│   │                                                    works offline too; persists across scenes
│   │
│   ├── Connection/
│   │   └── MID_NetworkConnectionManager.cs          ← Background internet connectivity monitor;
│   │                                                    Ping / HttpRequest / DnsLookup / Tcp / HttpPing
│   │
│   ├── LocalMultiplayer/
│   │   ├── ILocalLobbyTeamProvider.cs               ← Injectable team assignment interface
│   │   ├── LocalLobbyData.cs                        ← Lobby descriptor carried over UDP broadcasts
│   │   ├── LocalLobbyManager.cs                     ← LAN/WiFi offline lobby (UDP discovery + NGO host)
│   │   │                                               Contains: LocalLobbyConfig (same file)
│   │   ├── LocalLobbyPlayer.cs                      ← Player class + NetworkLobbyPlayerData wire struct
│   │   │                                               + LocalLobbySnapshot (same file)
│   │   ├── LocalLobbyUIManager.cs                   ← ⚠ FIX: states rewritten; now backed by
│   │   │                                               MID_UIStateContext (Main/Searching/InLobby/
│   │   │                                               Loading/NetworkCheck); old Browse/Hosting/Joining
│   │   │                                               are backward-compat aliases
│   │   ├── MobileNetworkStatusMonitor.cs            ← WIFI_CONNECTED / HOTSPOT / MOBILE_DATA / NO_NETWORK
│   │   └── PlayerOfflineIdentity.cs                 ← Persistent offline identity (PlayerPrefs-backed)
│   │
│   ├── PoolSystems/
│   │   ├── IPoolableNetworkObject.cs                ← OnPoolReset / OnPoolRetrieve interface
│   │   ├── MID_NetworkObjectPool.cs                 ← ⚠ FIX: auto-initializes in OnNetworkSpawn;
│   │   │                                               manual InitializePool() call no longer required
│   │   ├── PoolableNetworkObjectType.cs             ← AUTO-GENERATED — do not edit manually
│   │   └── Config/
│   │       └── NetworkPoolTypeProviderSO.cs         ← Per-package pool type list SO
│   │
│   ├── RPCQueue/
│   │   └── MID_NetworkRPCQueue.cs                   ← Tick-driven batch queue; collapse-key dedup;
│   │                                                   T must implement IMIDRPCPayload + INetworkSerializable
│   │
│   ├── SceneManagement/
│   │   └── MID_NetworkSceneLoader.cs                ← HybridNetworkSingleton; NGO additive scene loader;
│   │                                                   implements ISceneLoader from utilities
│   │
│   └── Timer/
│       └── NetworkTimer.cs                          ← Fixed-interval server tick timer; LerpFraction
│                                                       for client interpolation
│
└── Editor/                                           ← MidManStudio.Netcode.Editor.asmdef
    ├── MidManStudio.Netcode.Editor.asmdef            ← autoReferenced: false; includePlatforms: Editor
    └── NetcodePoolProviderBootstrapper.cs            ← [InitializeOnLoad] auto-creates default
                                                         NetworkPoolTypeProviderSO asset on first import
```

---

## Discrepancy Fix Summary

| # | Location | Issue | Fix |
|---|---|---|---|
| 1 | `package.json` | `com.unity.collections` referenced in asmdef but missing from package dependencies | Added `com.unity.collections: 2.2.1` |
| 2 | Old APICATALOG — Assembly section | `rootNamespace` listed as `MidManStudio.Core.Netcode` | Corrected to `MidManStudio.Netcode` |
| 3 | Old APICATALOG — MID_NetworkObjectPool | Documented `InitializePool()` as a required manual call | Corrected: auto-initializes in `OnNetworkSpawn`; manual call is still safe (idempotent) |
| 4 | Old APICATALOG — MID_NetworkObjectPool | Listed `GetNetworkObject(int typeId, ...)` and `IsRegistered(int typeId)` overloads | Removed — these overloads do not exist in implementation |
| 5 | Old APICATALOG — LocalLobbyUIManager | Documented states: `NetworkCheck / Browse / Hosting / Joining / Loading` | Corrected to `Main / Searching / InLobby / Loading / NetworkCheck`; old names are now backward-compat aliases |
| 6 | Old APICATALOG — LocalLobbyUIManager | Did not document `RequestGoToSearching()`, `OnSearchStarted()`, `CurrentState`, `CanGoBack` | Added |
| 7 | Old APICATALOG — LocalLobbyConfig | Not documented | Added |
| 8 | Old APICATALOG — NetworkLobbyPlayerData | Wire format struct not documented | Added |
| 9 | Old CHANGELOG.md | Copy-pasted placeholder content from utilities (listed MID_TickDispatcher etc.) | Replaced with correct netcode-specific content |
| 10 | Old APICATALOG — Namespace header | Said `MidManStudio.Core.Netcode` as namespace root | Corrected to `MidManStudio.Netcode` |

---

## Namespace Map

| Folder | Namespace |
|---|---|
| `Singleton/` | `MidManStudio.Netcode` |
| `Connection/` | `MidManStudio.Netcode` |
| `LocalMultiplayer/` | `MidManStudio.Netcode.LocalMultiplayer` |
| `LocalMultiplayer/` (UI) | `MidManStudio.Netcode.UI` |
| `PoolSystems/` | `MidManStudio.Netcode.Pools` |
| `PoolSystems/Config/` | `MidManStudio.Netcode.Generator` |
| `RPCQueue/` | `MidManStudio.Netcode` |
| `SceneManagement/` | `MidManStudio.Netcode.SceneManagement` |
| `Timer/` | `MidManStudio.Netcode` |
| `Editor/` | `MidManStudio.Netcode.Editor` |

---

## Assembly Structure

```
MidManStudio.Netcode                      autoReferenced: true  | allowUnsafeCode: false
├── MidManStudio.Utilities
├── Unity.Netcode.Runtime
└── Unity.Collections

MidManStudio.Netcode.Editor               autoReferenced: false | Editor only
├── MidManStudio.Utilities
├── MidManStudio.Utilities.Editor
├── MidManStudio.Netcode
└── Unity.Netcode.Runtime
```

Your game assembly sees `MidManStudio.Netcode` automatically (`autoReferenced: true`).

---

## [1.0.0] — Unreleased

### Added

**Singletons**
- `NetworkSingleton<T>` — NGO-aware singleton; instance in `Awake`, network features post-spawn
- `HybridNetworkSingleton<T>` — instance immediately in `Awake`, works offline, persists by default
- `INetworkSingletonLifecycle` — lifecycle callbacks for `NetworkSingleton<T>`
- `IHybridNetworkSingletonLifecycle` — lifecycle callbacks including non-NGO scene change

**Pool System**
- `MID_NetworkObjectPool` — generic NGO object pool; auto-initializes on `OnNetworkSpawn`; uses `INetworkPrefabInstanceHandler` so NGO's own spawn/despawn path calls into the pool
- `IPoolableNetworkObject` — `OnPoolReset()` / `OnPoolRetrieve()` interface for pooled prefabs
- `NetworkPoolConfig` — inspector entry (type, prefab, prewarm count)
- `NetworkPoolTypeProviderSO` — SO contributing entries to the generated `PoolableNetworkObjectType` enum
- `PoolableNetworkObjectType` — auto-generated enum (do not edit manually)
- `NetcodePoolProviderBootstrapper` — editor-only `[InitializeOnLoad]` that creates the default netcode pool provider asset on first import

**Connection**
- `MID_NetworkConnectionManager` — background connectivity monitor; five check methods (Ping, HttpRequest, DnsLookup, TcpConnection, HttpPing); `SetIntervalMultiplier()` for polling throttle; sync fallback `CheckSynchronous()`

**RPC Queue**
- `MID_NetworkRPCQueue` — tick-driven batch queue; collapse-key last-write-wins deduplication; configurable flush rate; `RegisterChannel<T>` / `Enqueue<T>` / `UnregisterChannel<T>`
- `IMIDRPCPayload` — `CollapseKey` contract; T must also implement `INetworkSerializable`

**Local Multiplayer**
- `LocalLobbyManager` — LAN/WiFi offline lobby; UDP broadcast discovery; host/join flow; player list with bot fill; team provider injection; game-start snapshot; mobile hotspot/WiFi helpers
- `LocalLobbyConfig` — hosting configuration (name, max players, mode, map, ports)
- `LocalLobbyData` — lobby descriptor carried by UDP (supports opaque `CustomData` JSON)
- `LocalLobbyPlayer` — player state (clientId, name, icon, ready, host, bot, teamId)
- `NetworkLobbyPlayerData` — NGO `NetworkList` wire struct (`FixedString128Bytes` name, `FixedString64Bytes` icon)
- `LocalLobbySnapshot` — final player list + lobby config at game-start time
- `ILocalLobbyTeamProvider` — injectable team logic (join/leave/change/serialize/deserialize)
- `LocalLobbyUIManager` — abstract base; `MID_UIStateContext`-backed state machine (Main → Searching → InLobby → Loading / NetworkCheck)
- `MobileNetworkStatusMonitor` — `WIFI_CONNECTED` / `HOTSPOT` / `MOBILE_DATA` / `NO_NETWORK`; `CanHost()` / `CanJoin()` helpers
- `PlayerOfflineIdentity` — persistent name + icon; `ExportForOnlineAccount()` migration helper

**Scene Management**
- `MID_NetworkSceneLoader` — `HybridNetworkSingleton` implementing `ISceneLoader`; NGO additive scene loading; per-client readiness tracking; `OnPlayerReadinessChanged` and `OnSceneEventProgressUpdate` events

**Timer**
- `NetworkTimer` — fixed-interval tick timer; `ShouldTick()` loop driver; `LerpFraction` for client interpolation
