# MidMan Studio Netcode Utilities — Test Scene Setup Guide

Step-by-step instructions for hand-building a manual test scene for each system.

## Scene 1: Network Object Pool

1. Create new scene: `"NetworkPoolTestScene"`
2. Create prefab(s) for whatever you want pooled — each needs a `NetworkObject` component
   and, if it should participate in pool-return logic, an `IPoolableNetworkObject`
   implementation on one of its components.
3. Right-click Project → `MidManStudio > Netcode > Pool Type Provider (Network Object)`
   - Name it, set `packageId`/`priority`
   - Add entries (paste JSON via the Import panel if you have several, same as the base
     pool system's provider editors)
4. Run the generator to produce the `PoolableNetworkObjectType` enum members for your
   new entries (see the Projectile System package's Config Type Generator if this is
   feeding into that — the netcode pool provider itself doesn't have a separate
   generator menu; it shares the base pool system's).
5. Create empty GameObject `"NetworkPools"`
   - Add `MID_NetworkObjectPool`
   - Assign your `NetworkPoolConfig` entries (type, prefab, prewarm count) in the
     Inspector
   - This GameObject needs its own `NetworkObject` — the pool itself spawns/despawns
     with the network session
6. Start a host (see Scene 2 below, or just call `NetworkManager.Singleton.StartHost()`
   directly for a minimal test)
7. From server-only code:

   ```csharp
   var netObj = MID_NetworkObjectPool.Instance.GetNetworkObject(
       PoolableNetworkObjectType.YourType, spawnPos, Quaternion.identity);
   netObj.Spawn();
   ```

8. Confirm in the Hierarchy: the object appears under the pool's despawned-object
   tracking when returned via `ReturnNetworkObject`, and is fully despawned (not just
   deactivated) if the whole session ends.

## Scene 2: Local Lobby (LAN/WiFi)

Needs two running instances to actually test discovery — either two Editor instances
via ParrelSync, or one Editor + one built player.

1. Create new scene `"LobbyTestScene"`
2. Create empty GameObject `"LobbyManager"`
   - Add `LocalLobbyManager`
   - Leave default ports (7777/7778) unless they're already in use on your network
3. Create a minimal UI: a "Host" button, a "Search" button, and a scrollable list for
   discovered lobbies
4. Wire the buttons:

   ```csharp
   public void OnHostClicked()
   {
       LocalLobbyManager.Instance.StartHosting(new LocalLobbyConfig
       {
           LobbyName = "Test Lobby", MaxPlayers = 4
       });
   }

   public void OnSearchClicked() => LocalLobbyManager.Instance.StartSearching();
   ```

5. Subscribe to discovery/join events to populate the list and react to results:

   ```csharp
   private void OnEnable()
   {
       LocalLobbyManager.Instance.OnLobbyDiscovered += AddToList;
       LocalLobbyManager.Instance.OnHostResult += ok => Debug.Log($"Host result: {ok}");
       LocalLobbyManager.Instance.OnJoinResult += ok => Debug.Log($"Join result: {ok}");
   }
   ```

6. On instance A: click Host. On instance B: click Search, wait for the lobby to appear
   in the list, click it to call `JoinLobby`.
7. Expected result: `OnPlayerJoined` fires on the host, `OnGameStartReceived` fires on
   both once the host actually starts the match (however your game defines "start" —
   this manager handles discovery/connection, not match flow).

> **If discovery doesn't find anything:** confirm both instances are on the same
> physical network/subnet, and that nothing (OS firewall, corporate network policy) is
> blocking UDP broadcast on the configured `BroadcastPort`. This is the most common
> "works on my machine, not in a demo" issue with LAN discovery.

## Scene 3: Lag Compensation

1. Create a scene with a simple server-authoritative moving target (a `NetworkObject`
   with a `NetworkTransform`, moving in a fixed pattern so hits are reproducible)
2. Add `MID_LagCompensatedTarget` to the target
3. Add `MID_LagCompensator` to a persistent manager object
4. From your hit-detection code (server-side), rewind to the client's observed time
   before resolving the hit — see [`APICATALOG.md`](../APICATALOG.md) for the exact
   rewind/restore call shape, since it depends on whether you're compensating a raycast
   or an overlap check.
5. Test with an artificial latency simulator (Unity's Multiplayer Tools package, or
   Netcode for GameObjects' own simulated latency in the Editor) to confirm a shot that
   visually lines up on the client's screen actually registers server-side, even though
   the target has moved further by the time the server processes it.

## Recommended persistent manager prefab order

If you're also using `com.midmanstudio.utilities`, the netcode pieces slot in after the
base managers:

```
Managers (DontDestroyOnLoad)
├── MID_Logger
├── MID_TickDispatcher
├── LocalObjectPool
├── LocalParticlePool
├── MID_NetworkConnectionManager
├── MID_NetworkObjectPool      ← needs a NetworkObject, spawns with the session
├── MID_NetworkRPCQueue
├── LocalLobbyManager
└── MID_LagCompensator
```
