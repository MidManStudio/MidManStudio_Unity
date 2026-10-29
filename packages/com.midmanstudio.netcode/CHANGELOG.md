# com.midmanstudio.netcode

## 1.0.0

First release. Design decisions, limits and fix history per file are in `docs/com.midmanstudio.netcode.md`.

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

**Collections**
- `MID_BitMask32`, `MID_BitMask64`: single-bit flag sets that implement `INetworkSerializable`
- `MID_BitPacker64`, `MID_BitField`, `MID_BitFieldLayout`: several enum, int and bool fields packed into one synced `ulong`
- `MID_NetworkDictionary<TKey, TValue>`: event-based synced dictionary, adapted from the community `NetworkDictionary` (see `THIRD-PARTY-NOTICES.md`)
- `MID_NetworkEvent<T>`: fire-and-forget signal over a `[ClientRpc]`/`[ServerRpc]` method
- `MID_NetworkString<T>`: fixed-capacity synced string; capacity chosen by the `FixedString` type

**Lag Compensation**
- `MID_LagCompensator`: server-side rewind scope (`BeginRewind(tick)`), one snapshot per server tick
- `MID_LagCompensatedTarget`: per-hitbox ring buffer of position and rotation snapshots

**Components**
- `OwnerAuthoritativeNetworkTransform`: `NetworkTransform` whose authority is the owning client

**Fixed before release**
- `MID_NetworkConnectionManager`: the `HttpRequest` check read the request result before the request had finished and always reported no connection. It now waits for the request, and its timeout is at least one second.
- `MID_NetworkConnectionManager`: the `HttpPing` check changed `HttpClient.Timeout` before every request, which .NET does not allow after the first request. The timeout is now applied per request.
- `README.md`, `APICATALOG.md` and `Documentation~/index.md`: install tag is `netcode/v1.0.0`, the pool accessor is `Instance`, namespaces corrected, connection manager, timer and lobby descriptions corrected, collections, lag compensation and `OwnerAuthoritativeNetworkTransform` added.
- `package.json`: documentation, changelog and license URLs filled in.

**Known issues** (see `docs/com.midmanstudio.netcode.md`, Fixes and Problems)
- Lag compensation does not call `Physics.SyncTransforms()` after rewinding. Needs a check in Unity if auto sync of transforms is off.
- `MID_NetworkObjectPool.ReturnNetworkObject` has no check for an object that is already queued, and the pool's prefab handler also calls it when NGO destroys a pooled object. The docs tell game code to call it before `Despawn`. Needs a test in Unity that an object is not queued twice.
