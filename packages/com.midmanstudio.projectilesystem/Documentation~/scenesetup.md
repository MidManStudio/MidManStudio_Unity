# MidMan Projectile System — Test Scene Setup Guide

Step-by-step instructions for hand-building a manual test scene. If you just want a
working example fast, import the **Projectile System Test** sample from Package Manager
instead — this guide is for when you want to see exactly how each piece is wired.

## Prerequisites

- `com.midmanstudio.utilities` and `com.midmanstudio.netcode` installed and their own
  pool systems set up (see each package's own `scenesetup.md`) — physics-based
  projectiles spawn through the netcode package's `MID_NetworkObjectPool`, and visual
  pooling goes through the base utilities pool system.
- A `NetworkManager` already present in the scene, host-capable.

## Scene 1: Config + Pattern Assets

Do this once, reused by every mode below.

1. `MidManStudio > Projectile System > Projectile Config` — create at least one config.
   Paste JSON into its **Apply JSON** panel to fill in damage/movement/piercing fields
   quickly, e.g.:

   ```json
   {
     "_movementType": "Straight",
     "_minSpeed": 25, "_maxSpeed": 30,
     "_lifetime": 3, "_maxRange": 50,
     "_piercingType": "None"
   }
   ```

2. `MidManStudio > Projectile System > Config Type Provider` — create one, add your
   config(s) via its own JSON-import panel (just the names — it auto-matches each name
   against an existing `ProjectileConfigSO` asset).
3. `MidManStudio > Projectile System > Config Type Generator` → **Generate** — produces
   the `ProjectileConfigType` enum your firing code references.
4. Optional: `MidManStudio > Projectile System > Projectile Pattern` — create one if you
   want to test burst/shotgun spread rather than a single straight shot.

## Scene 2: Physics-Based Projectile

1. Create a prefab: `Rigidbody2D`/`Rigidbody` + a collider + `PhysicsProjectile2D` (or
   `PhysicsProjectile3D`) + a `NetworkObject` component.
2. Register it the same way as any netcode-package pooled object — a
   `NetworkPoolTypeProviderSO` entry (see the netcode package's setup guide), since
   physics projectiles spawn through `MID_NetworkObjectPool`.
3. Create empty GameObject `"ProjectileSystem"`
   - Add `MID_MasterProjectileSystem`
   - Add `ProjectileRegistry` (or confirm one already exists as a scene-placed
     singleton — only one should exist per scene)
4. Start a host, then fire server-side:

   ```csharp
   MID_MasterProjectileSystem.FireProjectile(
       yourConfigType, origin, direction,
       ownerMidId, firedByNetworkObjectId,
       systemMode: ProjectileSystemMode.PhysicsBased);
   ```

5. Expected result: a real Rigidbody-simulated projectile, server-authoritative
   position sync via `NetworkTransform`, visible to every client except (by design) the
   firing client — see the note on client prediction below for why.

## Scene 3: Raycast-Based Projectile

1. Create empty GameObject alongside the one from Scene 2 (or the same one)
   - Add `RaycastProjectileHandler`
   - Assign a hit layer mask
2. Fire the same way, just with `ProjectileSystemMode.RaycastBased` — or leave it
   `Auto` and let `DetermineOptimalSystem` pick based on the config's fire rate.
3. For pattern-based (shotgun/burst) raycast fire, use `FireMultipleProjectiles` with a
   pattern-resolved direction array, or call `RegisterRaycastPatternFire` directly if
   you're not going through the master system's convenience API.

## Scene 4: Client Prediction (why a shot looks different for the firer)

This is the one piece that trips people up if they skip straight to reading the code:

- The firing client sees their **own** local prediction ghost immediately — spawned by
  their own weapon-fire code via `ClientPredictionManager.SpawnLocalPhysicsVisual`, not
  the real networked projectile. It lives out its own lifetime and glides to the
  server-confirmed hit point on its own (`HitConfirmedClientRpc`, which broadcasts to
  everyone including the firer).
- Every **other** client (and the host) sees the real, server-simulated projectile
  directly — the firing client is deliberately excluded from ever receiving it
  (`NetworkHide`'d right after spawn), since they already have their own local stand-in.
- If you're testing with two clients and one host, fire from a non-host client and
  confirm: the firer sees their own ghost only, everyone else sees the real projectile
  only. If you see both, or neither, on the firer's screen, that's the thing to debug
  first — check `NetworkedDimensionPlayer.FirePhysics`/`FireRaycast` in the sample for
  the reference implementation of this pattern before writing your own weapon code
  against it.

## Scene 5: Rust-Sim (high-volume)

Only reach for this once physics/raycast are working and you actually need to fire more
projectiles than one `NetworkObject`-per-shot can reasonably support (bullet-hell
patterns, large-scale battles).

1. Confirm `com.unity.burst`, `com.unity.collections`, and `com.unity.mathematics` are
   present (package dependencies, should already be resolved).
2. Fire with `ProjectileSystemMode.RustSim` explicitly, or a very high fire-rate config
   under `Auto`.
3. Use Unity's Multiplayer Tools profiler to compare bytes-per-shot against the physics
   mode for the same weapon — this is the actual point of this mode, and the difference
   is much easier to appreciate looking at real profiler numbers than reading about it.

## Recommended persistent manager prefab order

```
Managers (DontDestroyOnLoad)
├── MID_Logger
├── MID_TickDispatcher
├── LocalObjectPool
├── LocalParticlePool
├── MID_NetworkConnectionManager
├── MID_NetworkObjectPool
├── ProjectileRegistry
├── ProjectileConfigManager        ← Start()s after ProjectileRegistry, registers all configs
├── MID_MasterProjectileSystem
├── RaycastProjectileHandler       (if using raycast mode)
└── ClientPredictionManager
```
