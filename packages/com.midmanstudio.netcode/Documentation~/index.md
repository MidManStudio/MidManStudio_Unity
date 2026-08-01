# MidMan Studio Netcode Utilities

NGO-specific utilities for Unity 2022.3+, built on top of `com.midmanstudio.utilities`.
Network-aware singletons, a network object pool, connection/RPC helpers, lag
compensation, and a full LAN/WiFi local-lobby system — everything you need before
gameplay-specific netcode (like the Projectile System) comes into play.

## What's included

| System | Purpose |
|---|---|
| Network Singletons | `NetworkSingleton<T>` / `HybridNetworkSingleton<T>` — singleton access that's still network-aware |
| Network Object Pool | `MID_NetworkObjectPool` + `IPoolableNetworkObject` — pooled `NetworkObject` spawn/despawn |
| Connection Manager | `MID_NetworkConnectionManager` — connection approval, reconnection handling |
| RPC Queue | `MID_NetworkRPCQueue` — batches/throttles outgoing RPCs |
| Collections | `MID_NetworkDictionary`, `MID_BitPacker` — networked dictionary and bit-level packing for compact payloads |
| Lag Compensation | `MID_LagCompensator` + `MID_LagCompensatedTarget` — server-side rewind for hit registration |
| Local Lobby | `LocalLobbyManager` — host/discover/join over LAN or WiFi Direct, no external service |
| Scene Loader | `MID_NetworkSceneLoader` — networked scene loads with progress callbacks |
| Network Timer | `NetworkTimer` — synced countdown/stopwatch |

Full per-system API reference lives in [`APICATALOG.md`](../APICATALOG.md) — this page is
the getting-started overview, not the reference.

## Installation

Add via git URL through the Unity Package Manager, pinned to a release tag:

```
https://github.com/MidManStudio/MidManStudio_Unity.git?path=packages/com.midmanstudio.netcode#netcode/v1.0.0
```

Depends on `com.midmanstudio.utilities`, `com.unity.netcode.gameobjects` (1.7.1), and
`com.unity.collections` (2.2.1) — the package manager will resolve these automatically if
they're reachable. See the [root README](https://github.com/MidManStudio/MidManStudio_Unity#readme)
for the full list of published tags and general repo layout.

## Getting started

1. Install the package (above) — pull in `com.midmanstudio.utilities` first if you
   haven't already, since the pool system and a few helpers build directly on it.
2. Follow [`scenesetup.md`](./scenesetup.md) to hand-build test scenes for the network
   object pool and the local lobby system, the two pieces most projects touch first.
3. If you're here on the way to the Projectile System package, this is a dependency of
   that one — the network object pool in particular is what physics-based projectiles
   spawn through.

## Network Object Pool — quick start

Same shape as the base Pool System from `com.midmanstudio.utilities`, just for
`NetworkObject`s instead of plain `GameObject`s — spawn/despawn replaces
instantiate/destroy, and only the server should ever call `GetNetworkObject`:

```csharp
// Server-only
var netObj = MID_NetworkObjectPool.Instance.GetNetworkObject(
    PoolableNetworkObjectType.YourType, position, rotation);
netObj.Spawn();

// ...later, when done with it
MID_NetworkObjectPool.Instance.ReturnNetworkObject(netObj, PoolableNetworkObjectType.YourType);
```

Register your types the same way as the base pool system — a
`NetworkPoolTypeProviderSO` (`MidManStudio > Netcode > Pool Type Provider (Network Object)`)
per group of related types, then `MidManStudio > Netcode > Internal > Recreate Netcode
Pool Providers` if you need to reset the built-in ones.

## Local Lobby — quick start

No external lobby service required — this discovers and connects over LAN/WiFi directly:

```csharp
// Host
LocalLobbyManager.Instance.StartHosting(new LocalLobbyConfig
{
    LobbyName = "My Game", MaxPlayers = 4
});

// Client
LocalLobbyManager.Instance.OnLobbyDiscovered += lobby =>
{
    LocalLobbyManager.Instance.JoinLobby(lobby);
};
LocalLobbyManager.Instance.StartSearching();
```

`OnHostResult`/`OnJoinResult`/`OnPlayerJoined`/`OnGameStartReceived` cover the rest of the
connection lifecycle — see [`APICATALOG.md`](../APICATALOG.md) for the full event list.

## Version history

See [`CHANGELOG.md`](../CHANGELOG.md) for what shipped in each release.

## Support

Open an issue on the [GitHub repo](https://github.com/MidManStudio/MidManStudio_Unity/issues).
