# MidMan Projectile System

High-performance, server-authoritative projectile system for Unity 2022.3+. Three
interchangeable firing modes behind one API, a data-driven config/pattern system with
its own JSON-import tooling, and client-side prediction so shots feel instant without
giving up server authority.

## What's included

| System | Purpose |
|---|---|
| Master Fire Entry Point | `MID_MasterProjectileSystem` — one API surface for all three firing modes, auto-picks the best one per shot or takes an explicit override |
| Physics-Based | `PhysicsProjectileBase`/`PhysicsProjectile2D`/`PhysicsProjectile3D` — real Rigidbody-simulated projectiles, pooled via the netcode package's `MID_NetworkObjectPool` |
| Raycast-Based | `RaycastProjectileHandler` — instant-hit weapons, pattern-aware (shotgun/burst spreads) |
| Rust-Sim | `RustSimAdapter` + native `ProjectileLib` core — high-volume server-side simulation for bullet-hell-scale projectile counts without per-object GameObject overhead |
| Config System | `ProjectileConfigSO`/`ProjectileConfigScriptableObject` — damage, piercing, movement, and visual data per projectile, with a JSON-apply Inspector panel for bulk field entry |
| Config Type Generator | `ProjectileConfigProviderSO` + Config Type Generator — auto-generates a stable `ProjectileConfigType` enum from your config assets, same code-gen pattern as the base pool system |
| Pattern System | `ProjectilePatternSO`/`ProjectilePatternRegistry`/`ProjectileDirectionResolver` — reusable burst/shotgun/spread shapes, shared across all three firing modes |
| Client Prediction | `ClientPredictionManager` — local instant-feedback ghosts that reconcile against (or are superseded by) the server-authoritative result |
| Visual System | `ProjectileVisualBase`/`ProjectileVisual_2D`/`ProjectileVisual_3D` — pooled visuals decoupled from simulation, so a raycast hit and a physics projectile can share the same look |

Full per-system API reference lives in [`APICATALOG.md`](../APICATALOG.md) — this page is
the getting-started overview, not the reference.

## Installation

Add via git URL through the Unity Package Manager, pinned to a release tag:

```
https://github.com/MidManStudio/MidManStudio_Unity.git?path=packages/com.midmanstudio.projectilesystem#projectilesystem/v1.0.0
```

Depends on `com.midmanstudio.utilities`, `com.midmanstudio.netcode`,
`com.unity.netcode.gameobjects` (1.7.1), `com.unity.burst` (1.8.9),
`com.unity.collections` (2.2.1), and `com.unity.mathematics` (1.3.1). Install both
MidManStudio dependencies first — the package manager should resolve the rest.

## Getting started

1. Install the package and its dependencies (above).
2. Import the **Projectile System Test** sample from Package Manager. It's a complete
   working scene: 2D and 3D projectiles, all three firing modes, pattern-based spread,
   fully networked (host + client), driven by a test player you can drop into any scene.
   Press **F** to fire once it's running. Install Unity's Multiplayer Tools package
   first if you want to profile bandwidth — the sample's own description calls this out
   specifically, since seeing the actual bytes-per-shot is the fastest way to understand
   what Rust-sim buys you over one `NetworkObject` per projectile.
3. Follow [`scenesetup.md`](./scenesetup.md) to hand-build the same pieces yourself,
   system by system, if you want to see exactly how each one is wired instead of
   starting from the sample.

## Choosing a firing mode

You don't have to pick one — `MID_MasterProjectileSystem.FireProjectile`/
`FireMultipleProjectiles` take a `ProjectileSystemMode` (`Auto`, `PhysicsBased`,
`RaycastBased`, `RustSim`) and `DetermineOptimalSystem` will choose for you based on the
config's own properties (an explosive config forces physics; a very high fire rate
pushes toward raycast/rust-sim over spawning a `NetworkObject` per shot) when you leave
it on `Auto`. Force a specific mode only when you have a concrete reason to override the
heuristic for one particular weapon.

```csharp
MID_MasterProjectileSystem.FireProjectile(
    yourConfigType, origin, direction,
    ownerMidId, firedByNetworkObjectId,
    systemMode: ProjectileSystemMode.Auto);
```

## Config assets — quick start

1. `MidManStudio > Projectile System > Projectile Config` to create a `ProjectileConfigSO`
   (or your own subclass) per projectile. Use the **Apply JSON** panel in its Inspector
   to bulk-fill primitive/enum fields instead of clicking through each one by hand —
   asset references (sprites, audio, materials) still need manual assignment, and the
   panel tells you exactly which fields those are after an apply.
2. `MidManStudio > Projectile System > Config Type Provider` to create a
   `ProjectileConfigProviderSO`, and add your configs to it (JSON-import supported here
   too — see that Inspector's own panel; entries auto-match an existing `ProjectileConfigSO`
   by asset name, since a direct object reference can't come from JSON).
3. `MidManStudio > Projectile System > Config Type Generator` → generate. This produces
   your `ProjectileConfigType` enum and wires `ProjectileConfigManager` to resolve it
   against `ProjectileRegistry` at runtime.

## Patterns — quick start

1. `MidManStudio > Projectile System > Projectile Pattern` to create a `ProjectilePatternSO`
   — define pellet count, spread shape, per-pellet speed variance.
2. Reference it from wherever your weapon fires (a `ProjectilePatternSO` field on your
   own weapon config), and resolve directions through the shared resolver so every
   firing mode produces the same shape for the same pattern:

   ```csharp
   var resolved = ProjectileDirectionResolver.Resolve(
       pattern.PatternId, origin, baseDirection,
       pelletCount, spreadDeg, speed, is3D);
   ```

## Version history

See [`CHANGELOG.md`](../CHANGELOG.md) for what shipped in each release.

## Support

Open an issue on the [GitHub repo](https://github.com/MidManStudio/MidManStudio_Unity/issues).
