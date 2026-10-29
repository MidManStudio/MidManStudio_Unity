# com.midmanstudio.netcode

## Overview

Utility package for Netcode for GameObjects (NGO 1.7.1) on Unity 2022.3 and
later. It builds on `com.midmanstudio.utilities` (logging, singletons, the
pool type generator, the scene registry, UI state contexts) and contains no
game-specific code. `com.midmanstudio.projectilesystem` depends on it for the
network object pool and for lag compensation.

What the package contains:

- Network-aware singletons (`NetworkSingleton<T>`, `HybridNetworkSingleton<T>`).
- A network object pool that plugs into NGO's prefab handler, plus the provider
  asset and generated enum that name its pool types.
- An internet connectivity monitor, a batched RPC queue and a fixed-interval
  tick timer.
- Networked collection types: bit masks and a bit packer, a dictionary, a
  fixed-size string and a fire-and-forget event.
- Server-side lag compensation and an owner-authoritative `NetworkTransform`.
- A LAN/WiFi local lobby (UDP discovery, host and join over NGO, ready flags,
  bots, optional team provider) with an abstract UI base class, a mobile
  network status monitor and an offline player identity.
- An NGO additive scene loader that implements `ISceneLoader`.

Namespaces: `MidManStudio.Netcode` (connection manager, RPC queue, timer),
`.Singleton`, `.Pools`, `.Generator`, `.Collections`, `.Components`,
`.LagCompensation`, `.LocalMultiplayer`, `.UI` and `.SceneManagement`.
Runtime code is in the `MidManStudio.Netcode` assembly and editor code in
`MidManStudio.Netcode.Editor`.

The package has no automated tests. `Documentation~/SceneSetup.md` describes
hand-built test scenes for the pool, the local lobby and lag compensation.
`README.md` holds the quick start, `APICATALOG.md` the API reference and
`Documentation~/index.md` the getting-started page.

## Modules

### `NetworkSingleton.cs`
**What it does:** `NetworkSingleton<T>`, a singleton base for `NetworkBehaviour`
components, and the `INetworkSingletonLifecycle` interface. The instance is
assigned in `Awake`. RPCs, ownership and the `IsServer`/`IsClient` flags are
only meaningful after `OnNetworkSpawn`. `Instance` finds an existing object
with `FindObjectsByType`, destroys the extra copies and otherwise creates a
GameObject named `_<TypeName>` with a `NetworkObject`. `HasInstance`,
`TryGetInstance()` and `Current` read the static field without creating
anything. `IsNetworkActive()` and `IsServerAuthority()` report spawn and
ownership state, and `Reset()` destroys the instance and clears the static
state.

Persistence is opt-in through `Remake(bool)` or `InitializeSingleton(bool)`.
`DontDestroyOnLoad` is applied once the object has spawned. From then on the
class listens to NGO scene events and, on `LoadComplete`, raises the static
`OnNetworkSceneChanged` event and the lifecycle `OnNetworkSceneChange`
callback. `previous` is the name of the active scene at the moment the event
arrives.

**Decisions:**
- The lifecycle interface is optional. The instance is cast to
  `INetworkSingletonLifecycle` and called only when it implements it.
- Every call into game code (lifecycle callbacks, the scene event) is wrapped in
  try/catch and logged, so one faulty subscriber cannot break NGO's spawn path.
- The auto-created object is not spawned by this class. A singleton that needs
  network features has to sit in a scene (NGO spawns in-scene `NetworkObject`s)
  or be spawned by game code.

### `HybridNetworkSingleton.cs`
**What it does:** `HybridNetworkSingleton<T>` and
`IHybridNetworkSingletonLifecycle`. Same idea as `NetworkSingleton<T>` for
components that must also work offline. `Instance` is usable from `Awake`,
persistence is on by default and is applied right away (in `Awake`, or inside
`Instance` when it finds or creates the object), and network features switch on
in `OnNetworkSpawn`. Static events: `OnNetworkStateChanged(bool)` and
`OnNetworkSceneChanged`. Helpers: `IsAvailable()`, `IsNetworkReady()` (spawned
and `NetworkManager` listening), `IsNetworkSpawned()`, `IsServerAuthority()`,
`GetExistingInstance()` (finds without creating) and `Reset()`.

**Decisions:**
- Scene change callbacks come from NGO's scene manager when one exists at the
  moment persistence is applied, and from `SceneManager.sceneLoaded` otherwise.
  That is why the lifecycle interface has both `OnNetworkSceneChange` and
  `OnSceneChange`.
- The auto-created `NetworkObject` sets `DontDestroyWithOwner`.
- Callbacks go through one `FireSafe` helper that catches and logs exceptions.

**Limits:**
- The choice between NGO scene events and `sceneLoaded` is made once. A
  singleton whose persistence was applied before `NetworkManager` had a scene
  manager keeps using `sceneLoaded` and never receives `OnNetworkSceneChange`.

### `MID_NetworkObjectPool.cs`
**What it does:** `MID_NetworkObjectPool` (a `NetworkSingleton`) and
`NetworkPoolConfig` (inspector entry: pool type, display name, prefab, prewarm
count). On `OnNetworkSpawn`, on the server and on clients, the pool registers
every configured prefab with NGO's `PrefabHandler` through a private
`INetworkPrefabInstanceHandler`. NGO then instantiates and destroys those
prefabs through the pool. It also creates the prewarm instances (reset,
deactivated, queued).

`GetNetworkObject(type, position, rotation)` takes an instance from the queue
or instantiates one, activates it, sets the pose and calls `OnPoolRetrieve` on
every `IPoolableNetworkObject` on it. `ReturnNetworkObject(obj, type)` calls
`OnPoolReset`, unparents the object, zeroes its transform, deactivates it and
queues it. `IsRegistered(type)` and `ClearPool()` complete the API.
`ClearPool()` also runs on `OnNetworkDespawn`.

**Decisions:**
- Initialization happens on `OnNetworkSpawn` so that clients get their prefab
  handlers without game code calling anything. `InitializePool()` is still
  public and does nothing when the pool is already initialized.
- Pooled instances are moved into the scene that holds the pool, so they go
  away with that scene.
- The pool does not know any game type. Prefabs reset themselves through
  `IPoolableNetworkObject`.
- Exceptions thrown by the hooks are caught and logged, so one bad prefab does
  not stop a spawn.
- The check for a missing `NetworkObject` uses `UnityEngine.Assertions`, which
  runs in the Editor and in development builds only.

**Limits:**
- `Awake` is overridden without calling `base.Awake()`. It only records the
  scene. The static instance is therefore not set until `Instance` is read
  for the first time (it then finds the object with `FindObjectsByType`), and
  the duplicate check in `NetworkSingleton<T>.Awake` does not run.
- `ClearPool()` removes the prefab handlers and drops the queues but does not
  destroy the pooled instances. They stay in the pool's scene until it unloads.
- Registering two entries with the same pool type keeps the last one.

### `IPoolableNetworkObject.cs`
**What it does:** The two hooks a pooled prefab implements. `OnPoolReset()` runs
when the object is first created and again every time it returns to the pool.
`OnPoolRetrieve()` runs just before the object is handed to the caller, after
the pool has set its position and rotation.

**Decisions:**
- The hooks live on the prefab, so the pool contains no game logic.

### `NetworkPoolTypeProviderSO.cs`
**What it does:** A `ScriptableObject` that contributes named entries to the
generated `PoolableNetworkObjectType` enum. It implements
`IPoolEntryProviderSO` from `com.midmanstudio.utilities`, so the Pool Type
Generator treats it like the other pool providers. Create one through
`MidManStudio > Netcode > Pool Type Provider (Network Object)`.

**Decisions:**
- Priority decides the order of the blocks in the enum: 0 is reserved for this
  package, 10 for `com.midmanstudio.projectilesystem`, and 100 and up is for
  game code.
- `packageId` must be unique across all network pool providers.

### `PoolableNetworkObjectType.cs`
**What it does:** The generated enum of network pool type ids. It has no NOTICE
header on purpose, because the Pool Type Generator rewrites the whole file
(header included) on every run. Regenerate it with
`MidManStudio > Utilities > Pool Type Generator`.

**Decisions:**
- The committed copy contains the two pinned projectile blueprint entries
  (`BaseProjectileBlueprint_2D` = 100, `BaseProjectileBlueprint_3D` = 101),
  because this repository ships both packages together. A project that uses
  only this package regenerates the file and the block stays empty.
- Number handling (blocks, pins, retired numbers) is the generator's, see
  `docs/com.midmanstudio.utilities/enum-generators.md`.

### `NetcodePoolProviderBootstrapper.cs`
**What it does:** Editor-only `[InitializeOnLoad]` class that creates
`Assets/MidManStudio/Netcode/PoolProviders/NetworkPoolTypeProvider_Netcode.asset`
(`packageId` `com.midmanstudio.netcode`, priority 0) when it does not exist.
The menu item `MidManStudio > Netcode > Internal > Recreate Netcode Pool
Providers` runs the same check by hand.

**Decisions:**
- The asset is created one editor tick after load through
  `EditorApplication.delayCall`, not inside the static constructor.
- The default provider has no entries. Game code adds its own provider.

### `NetworkPoolTypeProviderEditor.cs`
**What it does:** Custom inspector for `NetworkPoolTypeProviderSO`. It derives
from `PoolTypeProviderEditorBase` in `com.midmanstudio.utilities`, which draws
the whole UI including the "Import Entries from JSON" panel.

### `MID_NetworkConnectionManager.cs`
**What it does:** Internet connectivity monitor derived from the utilities
`Singleton<T>` (a `MonoBehaviour`, so it works without NGO). Five check methods
are selectable in the inspector or through `SetCheckMethod`:

| Method | What it does |
|---|---|
| `Ping` | Unity `Ping` to 1.1.1.1 |
| `HttpRequest` | `UnityWebRequest` GET of https://unity3d.com, two attempts one second apart |
| `DnsLookup` | resolves cloudflare.com |
| `TcpConnection` | TCP connect to cloudflare.com:443 |
| `HttpPing` | `HttpClient` GET of https://speed.cloudflare.com |

`StartContinuousCheck()` and `StopContinuousCheck()` control a background loop
(default interval 5000 ms, timeout 5000 ms). `ConfirmConnectionAsync()` does
one check. `onConnectionStatusChanged` fires only when the state flips,
`onCheckCompleted` fires after every check. `SetIntervalMultiplier()` slows the
loop down while an error popup is open. `CheckSynchronous()` is a blocking
fallback for critical paths.

**Decisions:**
- The loop waits one interval before its first check, so `IsConnected` is false
  until the first check finishes. Call `ConfirmConnectionAsync()` when an answer
  is needed right away.
- `ConfirmConnectionAsync()` pauses a running loop (150 ms), checks, then
  restarts it, so two checks never overlap.
- The interval multiplier is clamped to a minimum of one second.
- `CheckSynchronous()` opens a TCP connection to 1.1.1.1:53 and waits at most
  three seconds. It blocks the calling thread.

### `MID_NetworkRPCQueue.cs`
**What it does:** Batches small RPC payloads into one send per flush.
`IMIDRPCPayload` exposes a `CollapseKey`. A payload type must also implement
`INetworkSerializable`. Game code calls `RegisterChannel<T>(handler)` once,
`Enqueue(payload)` any time, and `UnregisterChannel<T>()` when its owner
despawns. A `NetworkTimer` driven from `Update` flushes all channels at
`_flushRate` per second (default 20). Each flush hands the handler a
`List<T>` with everything queued since the last one. `TotalFlushes` and
`TotalPending()` are there for profiling. The queue is a
`HybridNetworkSingleton`, so it also works offline. It does not send anything
itself, the flush handler does.

**Decisions:**
- Payloads with the same non-empty `CollapseKey` inside one flush window keep
  only the last one. A null or empty key keeps everything.
- Each channel is a generic `RPCChannel<T>` behind a small internal interface,
  so `FlushAll` does not need reflection.
- The flush list is reused between flushes. A handler that wants to keep the
  data must copy it (for example `batch.ToArray()`).
- An exception in one handler is caught and logged, the other channels still
  flush.

**Limits:**
- Collapsed payloads are written to the flush list before the uncollapsed ones,
  so the relative order between the two groups is not preserved.
- Registering a channel for a type that already has one replaces it and drops
  its pending payloads. `UnregisterChannel<T>()` drops them too.

### `NetworkTimer.cs`
**What it does:** A plain class that turns frame time into fixed ticks.
`Update(dt)` adds to an accumulator, `ShouldTick()` removes one interval and
increments `CurrentTick` while enough time has built up, and `LerpFraction`
reports the progress toward the next tick. `Reset()` and `SetTickRate()` clear
the accumulator. A tick rate of zero or less falls back to 60.

**Limits:**
- There is no cap on catch-up. After a long frame, a `while (ShouldTick())`
  loop runs every missed tick in one go.
- `LerpFraction` is in [0, 1) only after the tick loop has drained the
  accumulator.

### `MID_NetworkSceneLoader.cs`
**What it does:** `HybridNetworkSingleton` that implements `ISceneLoader` from
`com.midmanstudio.utilities`. `LoadScene(sceneId, ...)` resolves the name
through the generated `SceneRegistry` and starts an NGO additive load. Only the
host or server may load, clients follow NGO's scene events. The loader tracks a
ready flag per connected client (`SetPlayerReady`, `IsPlayerReady`,
`AreAllPlayersReady`) and exposes `OnPlayerReadinessChanged` and
`OnSceneEventProgressUpdate` next to the `ISceneLoader` callbacks. Wire it into
the transition controller with
`MID_SceneTransitionController.Instance.SetNetworkLoader(MID_NetworkSceneLoader.Instance)`.

**Decisions:**
- The client table is filled from `OnClientConnectedCallback` after the loader
  has spawned, and every entry is reset to not ready when a load starts and
  when it completes.
- `OnClientLoadComplete` reports progress as (clients marked ready + 1) divided
  by the number of tracked clients.
- Event subscriptions are made in `OnNetworkSpawn` and removed in
  `OnNetworkDespawn` and `OnDestroy`.

**Limits:**
- `LoadScene` ignores its `loadType` and `delayMs` arguments. It always does an
  NGO additive load.
- `UnloadScene(sceneId)` unloads the scene the loader last loaded and uses
  `sceneId` for the log line only.
- Progress is derived from the ready flags that game code sets, not from NGO's
  own per-client load results, so it is approximate.
- A client that connected before the loader spawned is not in the table, and
  `SetPlayerReady` ignores unknown clients.

### `MID_BitPacker.cs`
**What it does:** Two tools for compact network state.
`MID_BitMask32` and `MID_BitMask64` store one flag per bit (`Get`, `Set`,
`Clear`, `Toggle`, `Any`, `All`, `None`, counts, set operations, `Matches`,
`IsDisjoint`, `IsSubsetOf`, `IterOnes`, operators). `MID_BitPacker64`,
`MID_BitField` and `MID_BitFieldLayout` pack several multi-bit fields (enums,
small integers, booleans) into one `ulong`. The layout hands out
non-overlapping fields (`Reserve`, `ReserveBool`, `ReserveEnum<T>`,
`ReserveForMaxValue`), the packer reads and writes them. All three structs
implement `INetworkSerializable`, so they work directly as the value of a
`NetworkVariable`.

**Decisions:**
- The mask API is a direct port of the storage mask family in mid-math
  (`Mid-D-Man/mid-engine`, `crates/mid-math/src/storage/storage_mask.rs`),
  with names changed from snake_case to PascalCase. Only the 32 and 64 bit
  widths are ported. A 128 or 256 bit mask would follow the same shape with
  two or four `ulong` words.
- Packing exists because every `NetworkVariable` has a fixed cost (dirty
  tracking, header, delta message) no matter how small its value is. Five
  3-bit enums as five variables cost five times that overhead for 15 bits of
  data. In one `MID_BitPacker64` they cost one variable, one `ulong` and one
  dirty check.
- `SetField` masks a value that is wider than its field to the low bits. It
  neither clamps nor throws, because an exception on a hot networked write path
  is worse than a wrong but bounded value.
- `MID_BitFieldLayout` throws as soon as the reserved widths pass 64 bits.
  Build the layout once in a static initializer so a mistake shows at startup.
- `ReserveEnum<T>` sizes the field from the largest underlying value, not from
  the member count, so `{A = 0, B = 8}` needs 4 bits. It assumes non-negative
  values.
- `GetEnum` and `SetEnum` box the enum value. That is fine for state that
  changes now and then. A field written every tick would need an
  `UnsafeUtility` based cast instead.

### `MID_NetworkDictionary.cs`
**What it does:** `MID_NetworkDictionary<TKey, TValue>`, a `NetworkVariableBase`
that syncs a dictionary with an event for every change
(`OnDictionaryChanged`, event types `Add`, `Remove`, `Value`, `Clear`, `Full`).
Keys and values live in two `NativeList`s. `TKey` must be unmanaged and
`IEquatable`, `TValue` must be unmanaged. It implements `Add`, `Remove`,
`Clear`, `TryGetValue`, `ContainsKey`, `Count`, `Keys`, `Values`, the indexer
and an enumerator. A packed value type such as `MID_BitPacker64` can be used as
`TValue`.

**Decisions:**
- Adapted from the community `NetworkDictionary` in
  `Unity-Technologies/multiplayer-community-contributions`
  (`com.community.netcode.extensions`), which is MIT licensed. The notice is in
  `THIRD-PARTY-NOTICES.md` at the package root. The storage layout, the dirty
  event queue and the scene object handling are the same design as the
  reference.
- `WriteField` writes the snapshot taken at the last `ResetDirty` for in-scene
  placed objects and the live lists for spawned objects. A single list does not
  work for both because their spawn order differs.

**Limits:**
- `Keys` and `Values` allocate a temporary array on every access.
- Lookups are linear (`NativeList.IndexOf`), so this suits small dictionaries.
- `Add` with an existing key and the indexer getter with a missing key throw.

### `MID_NetworkEvent.cs`
**What it does:** `MID_NetworkEvent<T>` and the `None` payload struct. A
fire-and-forget signal on top of `[ClientRpc]` or `[ServerRpc]`. It holds no
state, so a late joiner does not get a replay: an RPC only reaches the peers
that are connected when it is sent. `Raise(value, sendRpc)` fires `OnRaised`
locally (when `invokeLocally` is true, the default) and calls the delegate that
sends the RPC. `Receive(value)` is called from the body of the RPC method.

**Decisions:**
- It is composition, not a base class. Netcode generates the RPC code for the
  `NetworkBehaviour` that declares the `[ClientRpc]`/`[ServerRpc]` method, and no
  source confirmed that RPC methods inherited from a shared base class work
  across `NetworkBehaviour` subclasses on NGO 1.7.1. Each usage site therefore
  declares one thin forwarding RPC, and this class supplies the C# event
  (`+=`/`-=`) around it.
- `T` has no constraint. This class does no serialization, so what `T` may be
  is decided by the signature of the RPC method. Plain `string` works too, at
  the cost of an allocation per call, which is fine for a one-off signal.
- `None` exists because NGO has no built-in payload-less type.

### `MID_NetworkString.cs`
**What it does:** `MID_NetworkString<T>`, a `NetworkVariable`-friendly string
whose capacity is chosen by `T`, one of Unity's `FixedString` types: 29 usable
bytes for `FixedString32Bytes`, 61 for 64, 125 for 128, 509 for 512 and 4093
for 4096. The numbers are bytes, so multi-byte UTF-8 characters use more than
one. `WasTruncated` tells whether the source string was cut. It converts
implicitly to and from `string`. The static `MID_NetworkString` class has
factories `Short`, `Small`, `Default`, `Long` and `VeryLong`. The struct is
unmanaged, so it also works as the value of `MID_NetworkDictionary`.

**Decisions:**
- One generic wrapper instead of five copies. Every `FixedString` type
  implements `INativeList<byte>` and `IUTF8Bytes`, and NGO 1.7.1 has a
  `BufferSerializer<T>.SerializeValue` overload constrained to exactly those two
  interfaces, so every size takes the same path that sends only the used bytes.
- There is no growable option here. A `FixedString` is a fixed buffer inside the
  struct, which is what keeps it allocation free. Unbounded text as continuously
  synced state would need `NativeList<byte>`, which the owner has to allocate
  and dispose.
- `Equals` compares bytes through the `INativeList<byte>` indexer so it does not
  depend on `T` implementing `IEquatable<T>`.
- `CopyFromTruncated` returns a `CopyError` on `com.unity.collections` 2.x
  (this package pins 2.2.1) and `void` on 1.x. With a pin below 2.0 the line
  would stop compiling.
- C# has no generic aliases through `using`, so the factory class is the closest
  shortcut to spelling out `MID_NetworkString<FixedString64Bytes>`.

### `OwnerAuthoritativeNetworkTransform.cs`
**What it does:** A `NetworkTransform` whose authority is the owning client
instead of the server (`OnIsServerAuthoritative()` returns false). It adds no
serialized fields, so it can replace the stock component on a prefab without
losing the configured axes, thresholds and interpolation.

**Decisions:**
- Use it together with `NetworkRigidbody` when the owner drives a Rigidbody.
  Per Unity's documentation, `NetworkRigidbody` mirrors whatever authority model
  its `NetworkTransform` reports.

### `MID_LagCompensator.cs`
**What it does:** Server-side lag compensation coordinator, a utilities
`Singleton<T>`. Every client sees the world slightly in the past (network delay
plus its interpolation buffer). The compensator rewinds every registered
`MID_LagCompensatedTarget` to its pose at the shooter's fire tick before the
server runs its own hit check, then restores them. Use it like this:

```csharp
using (MID_LagCompensator.BeginRewind(clientFireTick))
{
    Physics.Raycast(...); // targets are at their historical pose here
}                         // restored on dispose, also on early return or exception
```

Add the component to one persistent GameObject in the networked scene. It does
nothing on clients. In `FixedUpdate` on the server it records one snapshot per
network tick for every target.

**Decisions:**
- `BeginRewind` is static and always safe to call. With no compensator in the
  scene, or a tick of zero or less, it returns a default scope that does
  nothing. It uses `TryGetInstance()` instead of the auto-creating `Instance`,
  so a raycast fired before anyone opted in never spawns an empty compensator.
- History is recorded only when the server tick has changed, so it holds exactly
  one entry per tick whatever the ratio of physics step to tick rate is.
- Scopes cannot be nested. A second `BeginRewind` while one is open logs a
  warning and returns a no-op scope. The projectile system only ever needs one
  scope at a time (a whole pattern's pellets share one rewind), so a stack was
  not worth the complexity.
- `MID_LagCompensationScope` is a `readonly struct` implementing `IDisposable`,
  so the usual `using` block needs no allocation.
- The tick is a Netcode server tick (`NetworkManager.ServerTime.Tick`). The
  projectile system's `ProjectileFireRequest.ClientFireTick` is already in that
  coordinate system.

**Limits:**
- See the open item under Fixes and Problems about `Physics.SyncTransforms()`.

### `MID_LagCompensatedTarget.cs`
**What it does:** `NetworkBehaviour` to attach to the root of a hitbox (player
capsule and similar). On the server it keeps a ring buffer of
`(tick, position, rotation)` snapshots (`_historyCapacity`, default 64, minimum
4). `Rewind(tick)` moves the hitbox root to the interpolated pose at that tick
and remembers the current one, `Restore()` puts it back. Colliders have to be
under `_hitboxRoot` (it defaults to this object's transform). Clients do
nothing. A selected target draws its stored positions as gizmos in the Editor.

**Decisions:**
- The ring buffer has the same shape as `ConsoleBufferInner` in mid-log's
  `console_buffer.rs`: fixed array, wrapping write head, monotonic count, the
  oldest entry silently overwritten when full.
- At a 30 Hz server tick, 64 entries cover about 2.1 seconds. Keep the capacity
  well above the worst expected round trip. The memory cost is small (a vector
  and a quaternion per tick per target).
- A tick newer than the newest snapshot gets the newest pose, and a tick older
  than the whole window gets the oldest. A rewind further back than the window
  cannot be exact.
- `OnNetworkDespawn` restores a rewound target as a safety net against
  something destroying it while a scope is open.
- Registration uses `TryGetInstance()` for the same reason as `BeginRewind`.

**Limits:**
- A target that spawns before the compensator exists is never registered.

### `LocalLobbyManager.cs`
**What it does:** `LocalLobbyManager` (a `HybridNetworkSingleton`) and
`LocalLobbyConfig`. A LAN/WiFi lobby on NGO with
`UnityTransport`:

- **Discovery.** The host binds UDP port 7778 (`BroadcastPort`) and every
  `_discoveryInterval` (2 s) sends its `LocalLobbyData` as JSON to the limited
  broadcast address, to each interface's directed broadcast address and to
  loopback. It also answers a `DISCOVER_LOBBY` request with the same JSON. A
  searching client sends that request to the same addresses every interval and
  listens for replies. A lobby that has not been seen for `_lobbyTimeout` (5 s)
  is removed and `OnLobbyRemoved` fires.
- **Host.** `StartHosting` cleans up an existing session, builds the lobby data
  with the local IPv4 address, points the transport at it
  (`ServerListenAddress` 0.0.0.0, port 7777 by default), calls `StartHost`,
  starts discovery and raises `OnHostResult`.
- **Join.** `JoinLobby` points the transport at the lobby and calls
  `StartClient`, then polls every 100 ms for up to 10 s for a connected,
  spawned client. It raises `OnJoinResult`.
- **Players.** The server keeps the player list. A `NetworkList` of
  `NetworkLobbyPlayerData` replicates the non-bot players, and clients mirror it
  into their own list. Names and icons are exchanged through RPCs after a client
  connects. Ready flags go through `SetPlayerReady`.
- **Bots.** `SetFillWithBots` or the inspector flag fills free slots (up to
  `_maxBots`) with generated names, ready from the start, ids from 10000 up.
- **Teams.** An optional `ILocalLobbyTeamProvider` assigns and changes teams.
  Its state is a string synced to clients by RPC.
- **Game start.** The host calls `RequestGameStart`, which needs every real
  player ready. Clients get `GameStartClientRpc` and raise
  `OnGameStartReceived` with a `LocalLobbySnapshot`.
- **Identity.** The player name and icon come from `PlayerOfflineIdentity` when
  one exists, otherwise from PlayerPrefs (`LocalLobby_PlayerName`, then
  `PlayerName`).
- **Settings helpers.** `OpenWiFiSettings()` and `OpenHotspotSettings()` open the
  system settings screens on Android and iOS.

**Decisions:**
- The discovery interval is 2 seconds instead of 1. With loopback and
  broadcast both firing on one machine, a 1 second interval fills Unity
  Transport's 128-packet receive queue in same-machine tests. Raising
  `MaxPacketQueueSize` on the `UnityTransport` component helps as well.
- Shutdown is proactive and synchronous. The manager calls
  `NetworkManager.Shutdown(discardMessageQueue: true)` when play mode starts to
  exit (Editor hook) and on `OnApplicationQuit`. Every other shutdown path
  (`OnDestroy`, the safe-shutdown coroutine, `StopHosting`, `LeaveLobby`,
  cleanup before hosting or joining) passes the same flag. See Fixes and
  Problems for the reason.
- `OnDestroy` falls back to a synchronous shutdown when the GameObject is
  inactive, because `StartCoroutine` throws on an inactive object (for example
  when a parent canvas is disabled during scene teardown).
- A joining client waits up to 5 s for the lobby object to spawn before it
  sends its name. The server adds a player to the `NetworkList` 0.5 s after the
  connection, and only if the lobby object is still spawned by then.
- Discovery sockets stop after five consecutive receive failures.
- `LocalLobbyData.Key` (`address:port`) identifies a lobby in the discovered
  table.
- Host address choice (`GetLocalIP`): ordinary Wi-Fi or Ethernet addresses
  first, then addresses in the common hotspot subnets (`192.168.43.`,
  `192.168.49.`, `172.20.10.`), then any non-loopback IPv4 address, then
  `127.0.0.1`.

**Limits:**
- The lobby RPCs (`SetReadyServerRpc`, `SendPlayerNameServerRpc`,
  `RequestTeamChangeServerRpc`) act on the `clientId` argument that the caller
  passes and do not check it against the sender. That suits friendly LAN play
  and is not a cheat-proof design.
- Bots exist on the host only. They are not in the `NetworkList`, so clients do
  not see them and their `LocalLobbySnapshot` has no bots.
- A discovered lobby's entry is replaced on every packet, but
  `OnLobbyDiscovered` fires only for a new key. A changing player count is not
  signalled. UI code that shows it has to read `GetDiscoveredLobbies()` again.
- `GetLocalIP` does not treat `192.168.137.` (Windows hotspot) as a hotspot
  subnet, while `MobileNetworkStatusMonitor` does.
- On iOS both settings helpers open the Wi-Fi settings URL, because iOS has no
  separate hotspot URL. On Android both open a system settings screen
  (`WIRELESS_SETTINGS` for hotspot, `WIFI_SETTINGS` for Wi-Fi).
- `SetPlayerName` writes `LocalLobby_PlayerName` and does not update
  `PlayerOfflineIdentity`, so the two can differ until game code sets both.
- `Awake` calls `InitializeSingleton(false)` after `base.Awake()`. The base
  call has already applied the hybrid default of persisting across scenes, so
  the `false` changes the flag but not the outcome.

### `LocalLobbyUIManager.cs`
**What it does:** Abstract `MonoBehaviour` (requires a `Canvas`) that subscribes
to every `LocalLobbyManager` event and to `MobileNetworkStatusMonitor`, moves a
`MID_UIStateContext` between the states `Main` (1), `Searching` (2), `InLobby`
(4), `Loading` (8) and `NetworkCheck` (16), and calls overridable hooks
(`OnLobbyDiscovered`, `OnPlayerJoined`, `OnHostResult`, `OnGameStartReceived`,
`OnSearchStarted` and so on). Protected `Request*` helpers wrap the manager
calls. `RequestGoToSearching()` stops a running search, changes state, starts a
fresh search and calls `OnSearchStarted()`. Create a `MID_UIStateContext` with
`contextName` `ProjLobby` and the five states, and run the UI State Context
Generator.

**Decisions:**
- State changes go by name, resolved through the context's `enumTypeName` with
  reflection over loaded assemblies, so this package does not depend on the
  generated enum.
- A failed host or join result navigates back to `Main` before the hook runs.
- With no context assigned the hooks still fire, but panels do not show or hide.
- `GoToBrowse()`, `GoToHosting()` and `GoToJoining()` stay as aliases of
  `GoToMain()` and `GoToInLobby()`.
- `RequestHost` and `RequestJoin` switch to `Loading` first, then call the
  manager.

### `MobileNetworkStatusMonitor.cs`
**What it does:** `MonoBehaviour` that polls the network type every
`_checkInterval` seconds and raises `OnNetworkStatusChanged` with one of
`WIFI_CONNECTED`, `HOTSPOT`, `MOBILE_DATA` or `NO_NETWORK`. `CanHost()` is true
for Wi-Fi and hotspot, `CanJoin()` for Wi-Fi only. `GetStatusMessage()` returns
display text, `ForceCheck()` checks now.

**Decisions:**
- Android: `ConnectivityManager` plus `NetworkCapabilities` give the connection
  type (the current API, instead of the deprecated `NetworkInfo`/`getType()`),
  and `WifiManager.isWifiApEnabled()` gives the hotspot state. That method is
  hidden, but it is the long-standing way to read (not change) the access point
  state, and there is no public API for it.
- If a native call fails (OEM restrictions, hidden API lockdown on newer
  Android), the monitor falls back to the interface/IP heuristic. iOS has no
  hotspot API and always uses the heuristic. The heuristic logs every detected
  interface at Debug level, so a wrong subnet prefix can be spotted and added.
- The Editor always reports `WIFI_CONNECTED`. Other platforms use
  `Application.internetReachability`.

### `PlayerOfflineIdentity.cs`
**What it does:** Utilities `Singleton<T>` that persists across scenes and
stores the player name and icon id in PlayerPrefs
(`OfflineIdentity_PlayerName`, `OfflineIdentity_PlayerIconId`, defaults
`Player` and `default`). `OnPlayerNameChanged` and `OnPlayerIconIdChanged` fire
on change. `ExportForOnlineAccount()` returns an `OfflineIdentitySnapshot`
(name, icon, UTC export time).

**Decisions:**
- The snapshot is meant to be used when the player goes online and not
  persisted itself, so a later online account can start from the offline
  identity.

### `LocalLobbyPlayer.cs`
**What it does:** `LocalLobbyPlayer` (client id, name, icon, ready, host, bot,
team id, where -1 means unassigned), `NetworkLobbyPlayerData` (the
`INetworkSerializable` struct that goes into the `NetworkList`, with a
`FixedString128Bytes` name and a `FixedString64Bytes` icon id) and
`LocalLobbySnapshot` (lobby data plus a copy of the player list at game start).

**Decisions:**
- The lobby treats `TeamId` as an opaque integer. Game code decides what it
  means.

**Limits:**
- `NetworkLobbyPlayerData.Equals` ignores `PlayerIconId`, so an update that
  changes only the icon compares equal to the old value.

### `LocalLobbyData.cs`
**What it does:** The lobby descriptor that travels over UDP as JSON
(`JsonUtility`): name, host name and address, port, player counts, game mode,
map, a free-form `CustomData` string, and the discovery and timeout times. It
adds `IsFull`, `Key` and `ToString()`.

**Decisions:**
- No game-specific fields. `GameMode` and `GameMap` are opaque strings, and
  `CustomData` is a JSON payload that the lobby carries without reading it.

### `ILocalLobbyTeamProvider.cs`
**What it does:** The interface a game implements to plug team logic into
`LocalLobbyManager.SetTeamProvider()`: join, leave, change request, lookup,
pre-start balancing, and string serialization of its state. Without a provider
every player has `TeamId` -1.

### Assembly definitions
**What they do:** `Runtime/MidManStudio.Netcode.asmdef` references
`MidManStudio.Utilities`, `Unity.Netcode.Runtime`, `Unity.Collections` and
`Unity.Netcode.Components`. It is auto-referenced and defines `MIDMAN_NGO` when
`com.unity.netcode.gameobjects` 1.0.0 or later is installed. `Editor/MidManStudio.Netcode.Editor.asmdef`
is editor-only, not auto-referenced, and references both utilities assemblies,
the runtime assembly and `Unity.Netcode.Runtime`.

**Decisions:**
- Nothing in this package reads the `MIDMAN_NGO` define. It stays because
  `com.midmanstudio.projectilesystem` defines the same symbol the same way.

## CI and Workflows

- `.github/workflows/release-packages.yml` runs when a tag matching `netcode/v*`
  is pushed (the same file serves the other packages) and on manual dispatch.
  It maps the tag prefix or the chosen package to
  `packages/com.midmanstudio.netcode`, reads `version` from that folder's
  `package.json`, checks that a pushed tag equals `netcode/v<version>`, and
  creates a GitHub Release named `com.midmanstudio.netcode <tag>` whose body
  is the `## <version>` section of the package `CHANGELOG.md` (the whole file, with
  a warning, when that section is missing). On manual dispatch
  the tag name is built from `package.json`, so it cannot disagree with it.
- `openupm/com.midmanstudio.netcode.yml` is the OpenUPM package entry. It is not
  read by any workflow here. It is meant to be added to the OpenUPM repository
  (`data/packages/`). OpenUPM builds from the same `netcode/v<version>` tags
  (`gitTagPrefix: "netcode/"`), so the tag must exist, match `package.json`, and
  `com.midmanstudio.utilities` must already be published there.

## Fixes and Problems

### `LocalLobbyManager.cs`
- Fixed "message handle errors" when closing the game: a
  `NullReferenceException` in `Unity.Netcode.NamedMessage.Handle` during
  `NetworkManager`'s own shutdown. This is a known rough edge in NGO's shutdown
  sequence. Unity's 1.5.x/1.6.0 release notes describe the same class of bug
  (refactored shutdown prevented custom messages from being invoked while
  `OnClientDisconnected` callbacks run during a shutdown). In the Editor,
  pressing Stop fires `NetworkManager`'s own `playModeStateChanged` handler,
  which calls `ShutdownInternal()` synchronously and can race the message queue
  drain while messages are still being processed. The manager's own shutdown
  paths run as coroutines or async tasks that may not finish before Unity leaves
  play mode. Fix: shut down synchronously with `discardMessageQueue: true` on
  `ExitingPlayMode` (Editor) and `OnApplicationQuit` (builds), and pass the same
  flag in every other shutdown path. With `IsListening` already false, NGO's own
  handler finds nothing to do.
- Fixed `StartCoroutine` throwing "Coroutine couldn't be started because the
  game object is inactive" from `OnDestroy` when the object was inactive (for
  example a parent canvas disabled during scene teardown). `OnDestroy` now
  checks `activeInHierarchy` and shuts down synchronously otherwise.
- Changed the default discovery interval from 1 s to 2 s (see Decisions).
- Open: bots are host-only, the lobby RPCs trust `clientId` arguments, a
  discovered lobby's player count is not signalled, and the identity and hotspot
  subnet inconsistencies listed under Limits.

### `LocalLobbyUIManager.cs`
- Fixed the UI staying on the Loading panel when hosting or joining failed.
  A failed result now goes back to `Main`.

### `MID_NetworkConnectionManager.cs`
- Fixed the `HttpRequest` check always reporting "not connected". It called
  `SendWebRequest()` and read `request.result` right away without waiting, so
  the result was still `InProgress`. It now waits for the operation to finish
  (yielding each frame) before reading the result.
- Fixed a request timeout of zero. `UnityWebRequest.timeout = 0` means no
  timeout, and `_timeoutMs / 1000` is zero for any timeout below one second. The
  value is now at least one second.
- Fixed the `HttpPing` check changing `HttpClient.Timeout` before every request.
  .NET throws `InvalidOperationException` when the property is set after the
  instance has started a request, which the method's catch-all turned into a
  `false` result from the second check on. The timeout is now applied per
  request through a `CancellationTokenSource`. The behavior of Unity's runtime
  for the old code was not verified here, the new code is correct either way.

### `MID_NetworkDictionary.cs`
- Fixed an unhandled `Full` delta. When the variable's own base dirty flag is set,
  `WriteDelta` writes one `EventType.Full` entry followed by the whole field.
  The reference implementation's `ReadDelta` had cases for `Add`, `Remove`,
  `Value` and `Clear` but none for `Full`, so the receiver read nothing for that
  entry, silently dropped the resync and desynced without an error. `ReadDelta`
  now has the missing case. It clears the lists, reads the full field the way
  `ReadField` does and raises `OnDictionaryChanged` with `Type = Full`.

### `OwnerAuthoritativeNetworkTransform.cs`
- Root cause of "the client's player cannot move". The stock `NetworkTransform`
  is server-authoritative by default (`OnIsServerAuthoritative()` is true), and
  `NetworkRigidbody` mirrors whatever authority the `NetworkTransform` reports.
  On the host, `IsServer` is true for the host's own player, so server and owner
  authority are the same object and movement works. On a pure client, `IsServer`
  is false even for the client's own player. Game code correctly checks
  `IsOwner` and makes the Rigidbody non-kinematic, but the stock pair checks
  `IsServer`, decides the instance is not authoritative and forces
  `isKinematic` back to true every frame. Nothing asks the server to move the
  object either (movement is local and owner-driven), so the player stays frozen
  on that client. The mix-up between "is this the server" and "is this the
  owner" is in NGO's defaults. Fix: this component flips authority to the owner.

### `MID_NetworkObjectPool.cs`
- Changed initialization from a manual `InitializePool()` call to
  `OnNetworkSpawn`. Existing calls keep working because the method is
  idempotent.
- Open: `Awake` does not call `base.Awake()` and `ClearPool()` does not destroy
  the pooled instances (see Limits).
- Open, needs a test in Unity: `README.md` and `APICATALOG.md` tell game code
  to call `ReturnNetworkObject` before `Despawn`. The pool's own prefab handler
  also calls `ReturnNetworkObject` from `INetworkPrefabInstanceHandler.Destroy`,
  and `ReturnNetworkObject` has no check for an object that is already queued.
  If NGO calls the handler's `Destroy` for that despawn, the same instance is
  queued twice and can be handed out twice. Whether NGO calls it in that
  sequence was not confirmed here, so the documented order is unverified.

### `MID_LagCompensator.cs`
- Open, needs a test in Unity: nothing in the repository calls
  `Physics.SyncTransforms()`. `BeginRewind` moves transforms and
  `RaycastProjectileHandler` casts right afterwards, outside `FixedUpdate`.
  Unity's documentation says to use `Physics.SyncTransforms()` after transform
  changes in `Update()` or `LateUpdate()` when a physics query follows
  immediately, because transforms are otherwise synced before the next physics
  step. If the project does not have auto sync enabled, the cast may still see
  the target at its current position, and the restore at the end of the scope
  may be applied late as well. The likely fix is one `Physics.SyncTransforms()`
  call after the rewind and one after the restore. It was not applied in 1.0.0
  because it cannot be tested here.

### `MID_NetworkSceneLoader.cs`
- Open: `loadType` and `delayMs` are ignored, `UnloadScene` ignores its
  argument, and progress is approximate (see Limits).

### `HybridNetworkSingleton.cs`
- Open: the scene event source is chosen once (see Limits).

### Package documentation
- `README.md`: the git install URL used the tag `#v1.0.0`. Tags in this
  repository are `netcode/v1.0.0`. The pool example called
  `MID_NetworkObjectPool.Singleton`, which does not exist, and now uses
  `Instance`. The singletons were listed under `MidManStudio.Netcode` and are in
  `MidManStudio.Netcode.Singleton`. Collections, lag compensation and
  `OwnerAuthoritativeNetworkTransform` were missing and are now listed. A note
  says to install `com.midmanstudio.utilities` first when installing from git.
- `Documentation~/index.md`: the connection manager was described as connection
  approval and reconnection handling, but it checks internet connectivity. The
  timer was described as a synced countdown or stopwatch, but it is a
  fixed-interval tick accumulator. The lobby was described as working over WiFi
  Direct, but it uses UDP broadcast on an existing network. The link to
  `SceneSetup.md` used the wrong case (`scenesetup.md`), which breaks on
  case-sensitive file systems.
- `APICATALOG.md`: the pool example and the pool's member table used
  `.Singleton` where the accessor is `.Instance`.
- Added `THIRD-PARTY-NOTICES.md` with the MIT notice for the adapted
  `MID_NetworkDictionary`.
