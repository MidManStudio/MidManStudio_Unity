# com.midmanstudio.netcode

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
