# MidMan Projectile System

High-performance, server-authoritative projectile system for Unity 2022.3+. Four
simulation modes (Rust sim, raycast, physics object and offline local) routed from one
fire entry point, a data-driven config/pattern system with
its own JSON-import tooling, and client-side prediction so shots feel instant without
giving up server authority.

## What's included

| System | Purpose |
|---|---|
| Master Fire Entry Point | `MID_MasterProjectileSystem` — one entry point; `ProjectileTypeRouter` picks the simulation mode per shot from the config and the fire context, or a config can force one with `_preferredSimMode` |
| Physics-Based | `PhysicsProjectileBase`/`PhysicsProjectile2D`/`PhysicsProjectile3D` — real Rigidbody-simulated projectiles, pooled via the netcode package's `MID_NetworkObjectPool` |
| Raycast-Based | `RaycastProjectileHandler` — instant-hit weapons, pattern-aware (shotgun/burst spreads) |
| Rust-Sim | `RustSimAdapter` + native `ProjectileLib` core — high-volume server-side simulation for bullet-hell-scale projectile counts without per-object GameObject overhead |
| Config System | `ProjectileConfigSO`/`ProjectileConfigScriptableObject` — damage, piercing, movement, and visual data per projectile, with a JSON-apply Inspector panel for bulk field entry |
| Config Type Generator | `ProjectileConfigProviderSO` + Config Type Generator — auto-generates a stable `ProjectileConfigType` enum from your config assets, same code-gen pattern as the base pool system |
| Pattern System | `ProjectilePatternSO`/`ProjectilePatternRegistry`/`ProjectileDirectionResolver` — reusable burst/shotgun/spread shapes, shared across the firing modes |
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
`com.midmanstudio.audio`, `com.unity.netcode.gameobjects` (1.7.1), `com.unity.burst`
(1.8.9), `com.unity.collections` (2.2.1), and `com.unity.mathematics` (1.3.1). Install the
three MidManStudio dependencies first, each from its own tag-pinned git URL (see the
README) — Unity resolves the registry packages itself, but not git dependencies.

## Getting started

1. Install the package and its dependencies (above).
2. Import the **Projectile System Test** sample from Package Manager. It's a complete
   working scene: 2D and 3D projectiles, the firing modes, pattern-based spread,
   fully networked (host + client), driven by a test player you can drop into any scene.
   Press **F** to fire once it's running. Install Unity's Multiplayer Tools package
   first if you want to profile bandwidth — the sample's own description calls this out
   specifically, since seeing the actual bytes-per-shot is the fastest way to understand
   what Rust-sim buys you over one `NetworkObject` per projectile.
3. Follow [`scenesetup.md`](./scenesetup.md) to hand-build the same pieces yourself,
   system by system, if you want to see exactly how each one is wired instead of
   starting from the sample.

## Choosing a firing mode

You do not pick a mode in the call. `MID_MasterProjectileSystem.Fire()` sends every shot through `ProjectileTypeRouter`, which chooses a `SimulationMode` from the config and the `WeaponFireContext`, in this order:

| Mode | Picked when | How you fire it |
|---|---|---|
| `LocalOnly` | `context.IsNetworked` is false (offline, no NGO) | `Fire()` |
| any mode | The config's `_preferredSimMode` is anything other than `RustSim` (applies when networked) | the call for the mode it names |
| `PhysicsObject` | `config.RequiresPhysicsObject()` is true (false on the base config, override it in a subclass) | `SpawnPhysicsProjectile()` |
| `Raycast` | `context.IsRaycastWeapon` is true and `config.IsRaycastEligible()` is true (a non-piercing 2D config that needs no physics) | `RegisterRaycastFire()` |
| `RustSim` | everything else (the default networked path) | `Fire()` |

`Fire()` simulates `LocalOnly` and `RustSim` shots. For a `Raycast` or `PhysicsObject` result it logs a warning and does nothing, because those two need the weapon script to do part of the work (the raycast, or the NGO spawn). See `scenesetup.md` for each call.

```csharp
var system = MID_MasterProjectileSystem.Instance;
system.Fire(configId, spawnPoints, spawnPoints.Length, context);

// or with the generated enum:
system.Fire((int)ProjectileConfigType.Bullet, spawnPoints, spawnPoints.Length, context);
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
   — define pellet count, spread shape, per-pellet speed variance, burst timing.
2. Reference it from wherever your weapon fires (a `ProjectilePatternSO` field on your
   own weapon config), and resolve directions through the shared resolver so the
   firing client, the server and every other client produce the same shape for the same pattern:

   ```csharp
   var resolved = ProjectileDirectionResolver.Resolve(
       pattern.PatternId, origin, baseDirection,
       pelletCount, spreadDeg, speed, is3D, fireSeed);
   ```

   Pass the same `patternId`, `spreadDeg`, base direction, `is3D` and `fireSeed` on to
   `Fire()`, so the server and the other clients rebuild the same shot. The README's
   "Shot Patterns" section has the full call.

## Version history

See [`CHANGELOG.md`](../CHANGELOG.md) for what shipped in each release.

## Support

Open an issue on the [GitHub repo](https://github.com/MidManStudio/MidManStudio_Unity/issues).
