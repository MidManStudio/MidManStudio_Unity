# com.midmanstudio.projectilesystem

**MidMan Projectile System** v1.1.0 — Server-authoritative high-performance projectile system for Unity 2022.3+.

> **2D PRIMARY:** `Is3D = false` is the fully tested production path. The Rust simulation, GPU instanced renderer, spatial-grid collision, and client prediction are all designed and verified for 2D.  
> **3D REFERENCE:** `Is3D = true` is a functional reference implementation. It uses the same Rust tick loop and follows the same architecture, but has received less real-world testing. Rendering quality on lower-end or integrated GPUs can vary due to billboard mesh instancing overhead. Use it to understand the 3D extension pattern; test on your target hardware before shipping.

---

## Requirements

| Dependency | Version |
|---|---|
| Unity | 2022.3 LTS |
| `com.midmanstudio.utilities` | 1.0.0 |
| `com.midmanstudio.netcode` | 1.0.0 |
| `com.unity.netcode.gameobjects` | 1.7.1+ |
| `com.unity.burst` | 1.8.9+ |
| `com.unity.collections` | 2.2.1+ |
| `com.unity.mathematics` | 1.3.1+ |

---

## What's Included

| System | Namespace | Description |
|---|---|---|
| `ProjectileConfigSO` | `MidManStudio.Projectiles.Config` | Config asset: movement, damage curve, visual, trail, piercing, scale |
| `ProjectileRegistry` | `MidManStudio.Projectiles.Config` | Runtime configId registry; struct-size validation against Rust |
| `ProjectileConfigManager` | `MidManStudio.Projectiles.Config` | Enum → ushort configId bridge; `Fire(ProjectileConfigType)` extension |
| `ProjectilePatternSO` | `MidManStudio.Projectiles.Config` | Shot patterns: Spline / Fan / Ring360 / VShape / Shotgun / Formula |
| `ProjectileShapeSO` | `MidManStudio.Projectiles.Config` | Visual shapes: Quad / Needle / Diamond / Cross / Custom / Formula |
| `MathFormulaEvaluator` | `MidManStudio.Projectiles.Config` | Self-contained math expression parser (used by Formula presets) |
| `MID_MasterProjectileSystem` | `MidManStudio.Projectiles.Managers` | **Main game code entry point** — `Fire()`, targets, raycast, physics |
| `ServerProjectileAuthority` | `MidManStudio.Projectiles.Managers` | Server-only Rust buffer; FixedUpdate sim loop; hit detection |
| `LocalProjectileManager` | `MidManStudio.Projectiles.Managers` | Offline + client-side Rust buffer; snapshot reconciliation |
| `RaycastProjectileHandler` | `MidManStudio.Projectiles.Managers` | Server hitscan validation + travelling client visual |
| `PhysicsProjectile2D` | `MidManStudio.Projectiles.Managers` | Rigidbody2D physics projectile (2D primary) |
| `PhysicsProjectile3D` | `MidManStudio.Projectiles.Managers` | Rigidbody physics projectile (3D reference) |
| `MID_ProjectileNetworkBridge` | `MidManStudio.Projectiles.Network` | All NGO RPCs: fire, spawn, hit confirm, snapshots |
| `ClientPredictionManager` | `MidManStudio.Projectiles.Network` | Physics pool visual manager; rotation utilities |
| `DeterministicMotionMath` | `MidManStudio.Projectiles.Network` | Closed-form Wave/Circular position formulas matching Rust |
| `ProjectileRenderer2D` | `MidManStudio.Projectiles.Visuals` | **Primary renderer** — GPU `DrawMeshInstanced` + combined-mesh fallback |
| `ProjectileRenderer3D` | `MidManStudio.Projectiles.Visuals` | 3D billboard renderer (reference) |
| `ProjectileVisual_` | `MidManStudio.Projectiles.Visuals` | 2D pool visual (SpriteRenderer + TrailRenderer + optional shape mesh) |
| `ProjectileImpactHandler` | `MidManStudio.Projectiles.Visuals` | GlobalFXManager integration + strategy-based impact particles |
| `NativeProjectile` | `MidManStudio.Projectiles.Core` | 72-byte 2D Rust sim struct (FFI boundary) |
| `ProjectileLib` | `MidManStudio.Projectiles.Core` | All P/Invoke bindings to `projectile_core` Rust library |

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

---

## Quick Start (2D)

### 1. Scene Setup

Add to a persistent `NetworkBehaviour` GameObject in your scene:
- `ProjectileRegistry`
- `MID_MasterProjectileSystem`
- `ServerProjectileAuthority`
- `LocalProjectileManager`
- `MID_ProjectileNetworkBridge`
- `ClientPredictionManager`
- `ProjectileRenderer2D` (assign the `InstancedProjectile` or `InstancedProjectile_URP` material)
- `ProjectileImpactHandler`
- `TrailObjectPool`

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

### 3. Register Configs

**Option A — Direct (simple):**
```csharp
// In Awake or Start, before any Fire() calls
ushort bulletId = ProjectileRegistry.Instance.Register(myBulletConfig);
```

**Option B — Enum system (recommended for larger projects):**

1. `Right-click → MidManStudio > Projectile System > Config Type Provider`
2. Set `packageId = "com.mygame"`, `priority = 100`, add entries
3. `MidManStudio > Projectile System > Config Type Generator > Generate Now`
4. Assign the generated `ProjectileConfigMapping.asset` to `ProjectileConfigManager._mapping`

```csharp
// Use generated enum directly
system.Fire((int)ProjectileConfigType.Bullet, spawnPoints, count, context);
```

### 4. Register Targets

```csharp
// Called when a damageable object spawns or moves
system.RegisterTarget2D(new CollisionTarget
{
    X        = transform.position.x,
    Y        = transform.position.y,
    Radius   = 0.5f,
    TargetId = myUniqueId,   // your uint ID for this target
    Active   = 1
}, gameObject.layer);

// When target dies or is disabled
system.DeactivateTarget2D(myUniqueId);
```

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

system.Fire(bulletConfigId, spawnPoints, 1, context);
```

### 6. Handle Hits (server-side damage)

```csharp
// Subscribe in OnNetworkSpawn on the server
system.GetAuthority().Adapter.OnProjectileHit += OnHit;

void OnHit(ProjectileHitPayload payload)
{
    // payload.TargetId, payload.Damage, payload.HitPosition
    // payload.IsHeadshot, payload.IsCrit
    // payload.OwnerMidId, payload.FiredByNetworkObjectId
    ApplyDamage(payload.TargetId, payload.Damage);
}
```

---

## Shot Patterns

`ProjectilePatternSO.SampleDirections(count)` returns an array of `Vector2(horizontalDeg, verticalDeg)`.

```csharp
var pattern = myPatternSO;
Vector2[] directions = pattern.SampleDirections(bulletCount);
var spawnPoints = new SpawnPoint[bulletCount];
for (int i = 0; i < bulletCount; i++)
{
    Vector3 dir = Quaternion.Euler(-directions[i].y, directions[i].x, 0f) * aimForward;
    spawnPoints[i] = new SpawnPoint { Origin = barrelTip, Direction = dir, Speed = 0f };
}
```

**Available shapes:** `Spline` (Catmull-Rom/Bezier/Linear) · `Ring360` · `Fan` · `VShape` · `Shotgun` · `Star` · `Spiral` · `Formula`

**Formula example** (360° ring with vertical wave):
```
H: i / n * 360
V: sin(i / n * tau) * 20
```

---

## Simulation Modes

| Mode | Description | Condition |
|---|---|---|
| `LocalOnly` | Offline Rust sim, no NGO | `context.IsNetworked = false` |
| `RustSim` | Server-authoritative Rust sim; client prediction | Default networked path |
| `Raycast` | Instant hitscan; server validates | `context.IsRaycastWeapon = true` + `config.IsRaycastEligible()` |
| `PhysicsObject` | NGO NetworkObject + Rigidbody | `config.RequiresPhysicsObject()` returns true |

Override per config: set `_preferredSimMode` in the inspector (anything except `RustSim` is an override).

---

## Movement Types

| Type | Description | Notes |
|---|---|---|
| `Straight` | Constant velocity | Default; most efficient |
| `Arching` | Straight + gravity (GravityScale) | 2D gravity-affected projectile |
| `Guided` | C# sets Ax/Ay each frame | Call `system.SetHomingDirection2D(projId, dir)` |
| `Wave` | Oscillates perpendicular to travel | Register params in config inspector |
| `Circular` | Orbits around travel axis | Register params in config inspector |
| `Teleport` | Reserved | Not yet fully implemented |

---

## Visual Setup

**Pool prefab for 2D (`PoolableObjectType.Projectile_Visual2D`):**
- Add `ProjectileVisual_` component
- Add `SpriteRenderer` (assign to `projectileSpriteRend`)
- Add `TrailRenderer` (assign to `projectileTrailRend`)
- `MeshFilter/MeshRenderer` are added automatically at runtime when a config uses `CustomShape`

**Shader:** Assign `MidMan/InstancedProjectile` (Built-in RP) or `MidMan/InstancedProjectile_URP` to your atlas material on `ProjectileRenderer2D`.

**Impact effects:** `ProjectileImpactHandler` routes through `GlobalFXManager` by default when present. Register per-config overrides:
```csharp
impactHandler.RegisterConfigEffectType(configId, EffectType.FleshSurface, particleCount: 8);
```

---

## Raycast Mode

```csharp
// In weapon's fire method (client)
var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
bool hit = Physics2D.Raycast(ray.origin, ray.direction, out RaycastHit2D h, 100f);

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

---

## Physics Projectile Mode

```csharp
// On server — spawn from NGO pool
NetworkObject netObj = system.SpawnPhysicsProjectile(
    PoolableNetworkObjectType.BaseProjectileBlueprint_2D,
    barrelTip.position,
    Quaternion.Euler(0f, 0f, angleDeg));

var proj = netObj.GetComponent<PhysicsProjectileBase>();
proj.SetOwnerContext(ownerMidId, firedByNetObjId, false, weaponLevel, 1f);
proj.InitialiseProjectile(ownerMidId, firedByNetObjId, speed);

// On firing client — optional temporary pool visual during RTT
uint handle = predictionManager.SpawnLocalPhysicsVisual(configId, origin, dir, speed);
// Kill it when you receive the hit confirmation, or let it expire naturally
```

---

## 3D Reference Implementation

Set `Is3D = true` on `ProjectileConfigSO` to use the 3D path. Everything routes through the same `MID_MasterProjectileSystem.Fire()` call — `ProjectileTypeRouter` selects the 3D Rust buffer automatically.

```csharp
// 3D fire context is identical to 2D
spawnPoints[0].Direction = Vector3.forward;
system.Fire(my3DConfigId, spawnPoints, 1, context);

// 3D target registration
system.RegisterTarget3D(new CollisionTarget3D
{
    X = pos.x, Y = pos.y, Z = pos.z,
    Radius   = 0.6f,
    TargetId = myId,
    Active   = 1
}, gameObject.layer);
```

The `ProjectileRenderer3D` renders billboard quads oriented along velocity. `ProjectileVisual3D` provides a capsule mesh pool visual. Both are wired the same way as their 2D counterparts.

> **Hardware note:** 3D billboard rendering uses `Graphics.DrawMesh` with per-frame world-space vertex updates. On integrated GPUs this can be a bottleneck at high projectile counts. Profile on target hardware; reduce `_maxProjectiles3D` if needed.

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
