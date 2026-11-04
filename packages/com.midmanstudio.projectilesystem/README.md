# com.midmanstudio.projectilesystem

**MidMan Projectile System** v1.0.0 — Server-authoritative high-performance projectile system for Unity 2022.3+.

> **2D PRIMARY:** `Is3D = false` is the fully tested production path. The Rust simulation, GPU instanced renderer, spatial-grid collision, and client prediction are all designed and verified for 2D.  
> **3D REFERENCE:** `Is3D = true` is a functional reference implementation. It uses the same Rust tick loop and follows the same architecture, but has received less real-world testing. Rendering quality on lower-end or integrated GPUs can vary due to billboard mesh overhead. Use it to understand the 3D extension pattern; test on your target hardware before shipping.

Design decisions, limits and fix history for every file are in `docs/com.midmanstudio.projectilesystem.md`. The full API reference is in `APICATALOG.md`.

---

## Requirements

| Dependency | Version |
|---|---|
| Unity | 2022.3 LTS |
| `com.midmanstudio.utilities` | 1.0.0 |
| `com.midmanstudio.netcode` | 1.0.0 |
| `com.midmanstudio.audio` | 1.0.0 |
| `com.unity.netcode.gameobjects` | 1.7.1 |
| `com.unity.burst` | 1.8.9 |
| `com.unity.collections` | 2.2.1 |
| `com.unity.mathematics` | 1.3.1 |

---

## Installation

Git URLs do not resolve dependencies that are themselves git URLs. Install the three MidManStudio dependencies first, then this package. Each URL is pinned to its release tag:

```
https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.utilities#utilities/v1.0.0
https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.netcode#netcode/v1.0.0
https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.audio#audio/v1.0.0
https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.projectilesystem#projectilesystem/v1.0.0
```

In Unity: `Window > Package Manager > + > Install package from git URL`, one URL at a time, in the order above. The Unity registry packages (`com.unity.netcode.gameobjects`, `com.unity.burst`, `com.unity.collections`, `com.unity.mathematics`) are resolved by Unity itself.

**Via local path (development, `manifest.json`):**
```json
"com.midmanstudio.projectilesystem": "file:../../packages/com.midmanstudio.projectilesystem"
```

---

## What's Included

| System | Namespace | Description |
|---|---|---|
| `ProjectileConfigSO` | `MidManStudio.Projectiles.Config` | Config asset: movement, damage curve, visual, trail, piercing, scale |
| `ProjectileRegistry` | `MidManStudio.Projectiles.Config` | Runtime configId registry; struct-size validation against Rust |
| `ProjectileConfigManager` | `MidManStudio.Projectiles.Config` | Enum to ushort configId bridge; `Fire((int)ProjectileConfigType.X, ...)` extension |
| `ProjectilePatternSO` | `MidManStudio.Projectiles.Config` | Shot patterns: Spline / Ring360 / Fan / VShape / Shotgun / Star / Spiral / Formula, with burst timing |
| `ProjectileShapeSO` | `MidManStudio.Projectiles.Config` | Visual shapes: Quad / Needle / Diamond / Arrow / Cross / Chevron / Star4 / Boomerang / letters / Custom / Formula |
| `MathFormulaEvaluator` | `MidManStudio.Projectiles.Config` | Self-contained math expression parser (used by Formula patterns, shapes and custom paths) |
| `MID_MasterProjectileSystem` | `MidManStudio.Projectiles.Managers` | **Main game code entry point**: `Fire()`, targets, raycast, physics |
| `ServerProjectileAuthority` | `MidManStudio.Projectiles.Managers` | Server-only Rust buffer; FixedUpdate sim loop; hit detection |
| `LocalProjectileManager` | `MidManStudio.Projectiles.Managers` | Offline and client-side Rust buffer; snapshot reconciliation |
| `RaycastProjectileHandler` | `MidManStudio.Projectiles.Managers` | Server hitscan validation and travelling client visual |
| `PhysicsProjectile2D` / `PhysicsProjectile3D` | `MidManStudio.Projectiles.Managers` | Rigidbody2D (2D primary) and Rigidbody (3D reference) physics projectiles |
| `RustSimTargetRegistrar` | `MidManStudio.Projectiles.Managers` | Component that registers an object as a RustSim collision target |
| `RustSimCustomShapeAuthoring` | `MidManStudio.Projectiles.Managers` | Hand-authored collider shape for RustSim hits |
| `ProjectileGuidanceTracker` | `MidManStudio.Projectiles.Managers` | Assign a target once, a Guided projectile homes on it |
| `ProjectileVisualWarmupManager` | `MidManStudio.Projectiles.Managers` | Pre-warms pooled visuals at scene load |
| `MID_ProjectileNetworkBridge` | `MidManStudio.Projectiles.Network` | All NGO RPCs: fire, spawn, hit confirm, snapshots |
| `ClientPredictionManager` | `MidManStudio.Projectiles.Network` | Physics pool visual manager; rotation utilities |
| `ProjectileDirectionResolver` | `MidManStudio.Projectiles.Network` | Builds the same spawn points on every peer from a pattern and a seed |
| `DeterministicMotionMath` | `MidManStudio.Projectiles.Network` | Closed-form Wave/Circular position formulas matching Rust |
| `ProjectileRenderer2D` | `MidManStudio.Projectiles.Visuals` | **Primary renderer**: GPU `DrawMeshInstanced` with a combined-mesh fallback |
| `ProjectileRenderer3D` | `MidManStudio.Projectiles.Visuals` | 3D billboard renderer (reference) |
| `ProjectileVisual_2D` / `ProjectileVisual_3D` | `MidManStudio.Projectiles.Visuals` | Pooled visuals (sprite or shape mesh, trail) |
| `ProjectileImpactHandler` | `MidManStudio.Projectiles.Visuals` | GlobalFXManager integration and strategy-based impact particles |
| `NativeProjectile` | `MidManStudio.Projectiles.Core` | 72-byte 2D Rust sim struct (FFI boundary) |
| `ProjectileLib` | `MidManStudio.Projectiles.Core` | All P/Invoke bindings to the `projectile_core` Rust library |

Prebuilt native libraries for Windows, macOS, Linux, Android (arm64-v8a, armeabi-v7a, x86_64), iOS and WebGL ship under `Runtime/Plugins/Native`.

---

## Architecture Overview

```
Game Code
    └── MID_MasterProjectileSystem.Fire()
              │
              ├── [Offline]  LocalProjectileManager.Spawn2D()
              │                     └── Rust sim tick, collision, render
              │
              └── [Networked]
                    ├── LocalProjectileManager.SpawnFiringClientBatch2D()  ← firing client
                    │     └── Immediate visual in client's Rust buffer (temp IDs)
                    │
                    ├── MID_ProjectileNetworkBridge.FireServerRpc()
                    │     └── Server: BatchSpawnHelper → ServerProjectileAuthority
                    │                     ├── Rust tick + spatial-grid collision
                    │                     ├── RustSimAdapter.ProcessHit() → damage event
                    │                     └── SendSnapshotClientRpc() for reconciliation
                    │
                    ├── LinkProjectileIdsClientRpc  → firing client: temp→real ID swap
                    └── SpawnConfirmedClientRpc     → other clients: fresh Rust buffer entries
```

`Fire()` handles the `LocalOnly` and `RustSim` modes. Raycast and physics shots use their own calls, `RegisterRaycastFire()` and `SpawnPhysicsProjectile()`, described below.

---

## Quick Start (2D)

### 1. Scene Setup

Put these on a persistent GameObject (or a small set of them) in your scene:
- `ProjectileRegistry`
- `MID_MasterProjectileSystem`
- `ServerProjectileAuthority`
- `LocalProjectileManager`
- `MID_ProjectileNetworkBridge` (a `NetworkBehaviour`, so its GameObject also needs a `NetworkObject`)
- `ClientPredictionManager`
- `RaycastProjectileHandler` (also a `NetworkBehaviour`; only if you use raycast mode)
- `ProjectileRenderer2D` (assign the `InstancedProjectile` or `InstancedProjectile_URP` material)
- `ProjectileImpactHandler`
- `TrailObjectPool`

Then assign them in the `MID_MasterProjectileSystem` inspector. `Documentation~/scenesetup.md` walks through a hand-built scene step by step.

### 2. Create a Config Asset

`Right-click Project → MidManStudio > Projectile System > Projectile Config`

Key fields to set:

```
Is3D:             false           ← 2D mode
MovementType:     Straight
MinSpeed/MaxSpeed: 15 / 15
Lifetime:         2.5
MaxRange:         40
PiercingType:     None
FullSizeX:        0.25            ← world-unit width
FullSizeY:        0.08            ← world-unit height
HasTrail:         true
Sprite:           (your bullet sprite)
```

The **Apply JSON** panel in the config inspector fills primitive and enum fields from pasted JSON. Asset references (sprites, materials, audio) still need manual assignment.

### 3. Register Configs

**Option A, direct (simple):**
```csharp
// In Awake or Start, before any Fire() calls
ushort bulletId = ProjectileRegistry.Instance.Register(myBulletConfig);
```

**Option B, enum system (recommended for larger projects):**

1. `Right-click → MidManStudio > Projectile System > Config Type Provider`
2. Set `packageId = "com.mygame"`, `priority = 100`, add entries
3. `MidManStudio > Projectile System > Config Type Generator`, then **Generate Now**
4. Assign the generated `ProjectileConfigMapping.asset` to `ProjectileConfigManager._mapping`

```csharp
// Use the generated enum directly
system.Fire((int)ProjectileConfigType.Bullet, spawnPoints, count, context);
```

The numbers in the generated enum stay stable when you add, remove or reorder entries.

### 4. Register Targets

Add `RustSimTargetRegistrar` to any object that projectiles should hit. It registers the object with the system and keeps its position current, and it reads the object's real Unity layer.

To register by hand instead:

```csharp
// Called when a damageable object spawns or moves
system.RegisterTarget2D(new CollisionTarget
{
    X        = transform.position.x,
    Y        = transform.position.y,
    Radius   = 0.5f,
    TargetId = myUniqueId,   // your uint ID for this target
    Active   = 1
}, gameObject);              // the GameObject overload reads the real layer

// When target dies or is disabled
system.DeactivateTarget2D(myUniqueId);
```

Pass the `GameObject` (or `Component`) overload, not a bare layer number. The `int unityLayer` overload defaults to layer 0, so any object on another layer would not match the config's `HitLayers`.

### 5. Fire

```csharp
var spawnPoints = new SpawnPoint[]
{
    new SpawnPoint
    {
        Origin    = barrelTip.position,
        Direction = aimDirection.normalized,
        Speed     = 0f   // 0 = use config speed
    }
};

var context = new WeaponFireContext
{
    IsNetworked            = NetworkManager.Singleton.IsListening,
    OwnerMidId             = localPlayerMidId,
    FiredByNetworkObjectId = weaponNetworkObject.NetworkObjectId,
    DamageMultiplier       = 1f,
    ProjectileCount        = 1,
    FireRate               = 10f
};

MID_MasterProjectileSystem.Instance.Fire(bulletConfigId, spawnPoints, 1, context);
```

### 6. Handle Hits (server-side damage)

```csharp
// Subscribe on the server once the system is up
system.GetAuthority().Adapter.OnProjectileHit += OnHit;

void OnHit(ProjectileHitPayload payload)
{
    // payload.TargetId, payload.Damage, payload.HitPosition
    // payload.IsHeadshot, payload.IsCrit
    // payload.OwnerMidId, payload.FiredByNetworkObjectId
    ApplyDamage(payload.TargetId, payload.Damage);
}
```

For client-side reactions (effects, hit markers), subscribe to `MID_MasterProjectileSystem.OnRustSimHit` instead. It fires offline, on clients when the server confirms a hit (and on the host for its own hits), and also for a client's predicted raycast hit. Its `RustSimHitPayload` fills `RawTargetId` offline and `TargetNetworkObjectId` when networked, never both.

---

## Shot Patterns

A `ProjectilePatternSO` describes where each projectile of a shot goes (and, if you set a burst interval, when it starts). Build the spawn points through `ProjectileDirectionResolver`, so the firing client, the server and every other client produce the same layout:

```csharp
ushort fireSeed = ProjectileDirectionResolver.NewFireSeed();   // 0 = fixed layout from the asset

SpawnPoint[] spawnPoints = ProjectileDirectionResolver.Resolve(
    pattern.PatternId, barrelTip.position, aimDirection,
    pelletCount, spreadDeg, baseSpeed, is3D, fireSeed);

system.Fire(configId, spawnPoints, spawnPoints.Length, context,
            patternId: pattern.PatternId, spreadDeg: spreadDeg,
            baseDirection: aimDirection, patternIs3D: is3D, fireSeed: fireSeed);
```

Only the pattern id, spread, seed and base direction travel over the network. Every machine rebuilds the same pellets from them.

**Available shapes:** `Spline` (Catmull-Rom / Bezier / Linear) · `Ring360` · `Fan` · `VShape` · `Shotgun` · `Star` · `Spiral` · `Formula`

**Formula example** (360° ring with vertical wave):
```
H: i / n * 360
V: sin(i / n * tau) * 20
```

`MidManStudio > Projectile System > Create Default Pattern Assets` creates a starter set.

---

## Simulation Modes

`ProjectileTypeRouter` picks the mode for each shot, in this order:

| Mode | Picked when | How you fire it |
|---|---|---|
| `LocalOnly` | `context.IsNetworked = false` | `Fire()` |
| (per-config override) | The config's `_preferredSimMode` is anything except `RustSim` (networked only) | by the mode it names |
| `PhysicsObject` | `config.RequiresPhysicsObject()` returns true | `SpawnPhysicsProjectile()` |
| `Raycast` | `context.IsRaycastWeapon = true` and `config.IsRaycastEligible()` | `RegisterRaycastFire()` |
| `RustSim` | Everything else (default networked path) | `Fire()` |

`Fire()` only simulates `LocalOnly` and `RustSim`. For a `Raycast` or `PhysicsObject` result it logs a warning and does nothing, so call `RegisterRaycastFire()` or `SpawnPhysicsProjectile()` from the weapon script for those. `RequiresPhysicsObject()` returns false on the base config; override it in a subclass. `IsRaycastEligible()` is false for 3D configs, piercing configs and configs that need physics.

---

## Movement Types

| Type | Description | Notes |
|---|---|---|
| `Straight` | Constant velocity | Default; most efficient. A gravity arc is `Straight` with a nonzero `GravityScale` |
| `Guided` | C# sets the heading each frame | Call `system.SetHomingDirection2D(projId, dir)`, or use `ProjectileGuidanceTracker` to assign a target once |
| `Wave` | Oscillates perpendicular to travel | Parameters in the config inspector |
| `Circular` | Orbits around the travel axis | Parameters in the config inspector |
| `Teleport` | Moves in discrete jumps instead of continuously | Thin colliders can be passed through, because the body is not swept |
| `CustomCurve` | Speed and sideways offset follow authored `AnimationCurve`s | Physics projectiles only |

---

## Visual Setup

**Pool prefab for 2D (`PoolableObjectType.Projectile_Visual2D`):**
- Add the `ProjectileVisual_2D` component
- Add a `SpriteRenderer` (assign to `projectileSpriteRend`)
- Add a `TrailRenderer` (assign to `projectileTrailRend`)
- `MeshFilter` / `MeshRenderer` are added automatically at runtime when a config uses a `CustomShape`

**Shader:** Assign `MidMan/InstancedProjectile` (Built-in RP) or `MidMan/InstancedProjectile_URP` to your atlas material on `ProjectileRenderer2D`.

**Impact effects:** `ProjectileImpactHandler` routes through `GlobalFXManager` by default when present. Register per-config overrides:
```csharp
impactHandler.RegisterConfigEffectType(configId, EffectType.FleshSurface, particleCount: 8);
```

Add `ProjectileVisualWarmupManager` to a persistent object to pre-warm the pooled visuals at scene load, so the first real shots do not stall.

---

## Raycast Mode

The weapon script does the Physics raycast itself, then reports the result:

```csharp
// In the weapon's fire method (client)
var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
RaycastHit2D h = Physics2D.Raycast(ray.origin, ray.direction, 100f);
bool hit = h.collider != null;

system.RegisterRaycastFire(new RaycastFireResult
{
    Origin             = barrelTip.position,
    Direction          = ray.direction,
    HitPoint           = hit ? (Vector3)h.point : ray.origin + ray.direction * 100f,
    DidHit             = hit,
    HitTargetNetworkId = hit ? h.collider.GetComponentInParent<NetworkObject>()?.NetworkObjectId ?? 0 : 0,
    IsHeadshot         = false,
    Is3D               = false
}, configId, context);
```

The server re-checks the hit with its own raycast. `RaycastProjectileHandler._trustClientOnValidationMiss` (default on) accepts the client's report when the server's check misses because targets have drifted apart. Turn it off for competitive games.

---

## Physics Projectile Mode

```csharp
// On the server: spawn from the NGO pool
NetworkObject netObj = system.SpawnPhysicsProjectile(
    PoolableNetworkObjectType.BaseProjectileBlueprint_2D,
    barrelTip.position,
    Quaternion.Euler(0f, 0f, angleDeg),
    configId,
    firingClientId);      // hides the server's body from the firing client

var proj = netObj.GetComponent<PhysicsProjectileBase>();
proj.SetOwnerContext(ownerMidId, firedByNetObjId, false, weaponLevel, 1f);
proj.InitialiseProjectile(ownerMidId, firedByNetObjId, speed);

// On the firing client: a temporary pool visual during the round trip
uint handle = predictionManager.SpawnLocalPhysicsVisual(configId, origin, dir, speed);
// Kill it when the hit confirmation arrives, or let it expire naturally
```

The firing client sees its own predicted visual only, every other client sees the real networked projectile.

---

## 3D Reference Implementation

Set `Is3D = true` on `ProjectileConfigSO` to use the 3D path. Everything routes through the same `MID_MasterProjectileSystem.Fire()` call, and `ProjectileTypeRouter` selects the 3D Rust buffer automatically.

```csharp
// The 3D fire context is identical to 2D
spawnPoints[0].Direction = Vector3.forward;
system.Fire(my3DConfigId, spawnPoints, 1, context);

// 3D target registration
system.RegisterTarget3D(new CollisionTarget3D
{
    X = pos.x, Y = pos.y, Z = pos.z,
    Radius   = 0.6f,
    TargetId = myId,
    Active   = 1
}, gameObject);
```

`ProjectileRenderer3D` draws camera-facing quads along the velocity direction. `ProjectileVisual_3D` is the pooled 3D visual (a mesh with a default capsule). Both are wired the same way as their 2D counterparts.

> **Hardware note:** `ProjectileRenderer3D` draws with `Graphics.DrawMesh` and uploads its quads every frame, up to `_maxQuads` (default 512) per pass. On integrated GPUs this can be a bottleneck at high projectile counts. Profile on your target hardware, and lower `_maxProjectiles3D` on `ServerProjectileAuthority` if needed.

---

## Known Limits

The details, with reasons, are in the `Fixes and Problems` section of `docs/com.midmanstudio.projectilesystem.md`. The ones that matter most when you first use the package:

- The server trusts several client-supplied values in the fire requests (owner id, damage multiplier, weapon level). Do not use the package as it ships for a game where clients cannot be trusted.
- Guided projectiles are predicted as a straight line between snapshots on clients.
- Snapshot positions are half precision, so reconciliation error grows beyond roughly 256 world units from the origin.
- `ProjectileRenderer3D` caps each pass at `_maxQuads` projectiles and draws shape configs as quads.

---

## Supported Unity Versions

| Unity | Status |
|---|---|
| 2022.3 LTS | ✅ Primary target |
| 2023.x | ✅ Compatible |

---

## License

MIT — see `LICENSE.md`.  
Copyright © 2026 Abdulhamid Manman Suleiman / MidMan Studio
