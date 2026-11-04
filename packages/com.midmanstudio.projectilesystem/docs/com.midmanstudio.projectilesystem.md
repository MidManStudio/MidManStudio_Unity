# com.midmanstudio.projectilesystem

## Overview

`com.midmanstudio.projectilesystem` is a Unity 2022.3 package for server-authoritative projectiles, in 2D and 3D, with Netcode for GameObjects integration. One entry point, `MID_MasterProjectileSystem`, routes every fire call to one of four simulation modes, chosen per config by `ProjectileTypeRouter`:

| Mode | What it is | Where it simulates |
|---|---|---|
| `RustSim` | Projectiles live in native buffers and are ticked, collided and drawn in bulk, with no per-projectile GameObject. | The Rust library `projectile_core`, reached by P/Invoke through `ProjectileLib`. The server (`ServerProjectileAuthority`) is authoritative, clients (`LocalProjectileManager`) predict and reconcile from snapshots. |
| `Raycast` | Hitscan. The weapon script casts the ray, the server validates the claim, a short visual flies to the hit. | `RaycastProjectileHandler`. |
| `PhysicsObject` | A pooled Rigidbody `NetworkObject` per projectile, simulated on the server only. | `PhysicsProjectileBase` with the 2D and 3D subclasses. |
| `LocalOnly` | Offline play, everything in the local backend. | `LocalProjectileManager`. |

**Native library.** The simulation core is the Rust crate `rust_lib/projectile_core` at the repository root (it is not inside the package folder). The package ships the compiled libraries under `Runtime/Plugins/Native` (`Windows`, `macOS`, `Linux`, `Android/{arm64-v8a, armeabi-v7a, x86_64}`, `iOS` device and simulator, `WebGL`), each with its `.meta`. They are produced by the `Build Rust Native Projectile Libs` workflow (see CI and Workflows), which copies them into the package and generates or validates the `.meta` files. At startup `MID_MasterProjectileSystem` calls `ProjectileLib.ValidateStructSizes`, which checks that the library loads and that every shared struct has the size C# expects (a mismatch or a missing library disables the system with a logged error).

**Folders.**
- `Runtime/Core` and `Runtime/Data`: the P/Invoke layer, the shared structs, the routing enums.
- `Runtime/Adapters`: `RustSimAdapter`, `BatchSpawnHelper`, `ProjectileTypeRouter`.
- `Runtime/Config`: the config assets (`ProjectileConfigSO`, patterns, shapes, `WeaponDefinitionSO`), `ProjectileRegistry`, the formula evaluator, and the config type generator's runtime assets (`Config/Generator`).
- `Runtime/Network`: the NGO bridge (`MID_ProjectileNetworkBridge`), `ProjectileDirectionResolver`, wire compression, client prediction, deterministic motion math, the networked projectile base.
- `Runtime/Managers`: the backends and managers, and the physics projectiles.
- `Runtime/Visual` and `Runtime/Shaders`: pooled visuals, the native-buffer renderers, trails, the impact handler.
- `Editor`: inspectors, the config type generator, the JSON import tools, scene-view shape authoring.
- `Tests`: a self-contained test and demo scene (see below).

**Dependencies** (from `package.json`): `com.midmanstudio.utilities`, `com.midmanstudio.netcode`, `com.midmanstudio.audio`, Netcode for GameObjects 1.7.1, Burst, Collections and Mathematics. The runtime assembly (`MidManStudio.ProjectileSystem`) references utilities and netcode but not audio. The `Tests` assemblies call `MID_NativeAudioBridge` from `com.midmanstudio.audio`, which is why the package lists audio as a dependency. Install the three MidMan packages before this one when installing from git URLs, because Unity's git resolver does not fetch dependencies that are not in a registry.

**The `Tests/` folder** is a test and demo scene, not a unit test suite: player movement and dimension switching, weapon firing and inventory, PvP health, shootable dummy targets, a local lobby, and a manual benchmark harness. It exercises the runtime package end to end. No automated test runner covers it (see CI and Workflows).

Every `.cs` file starts with a four-line NOTICE comment that points at its section under Modules. Each section has *What it does*, *How it works* and *Limits* (some also *Decisions*). The history of each file, with the problems found and the fixes made, is at the end under Fixes and Problems.

## Modules

### `MID_BuildTargets.cs`
**What it does:** Three compile-time constants. `IsServerBuild` is true when
`UNITY_SERVER` or `MIDMAN_SERVER_BUILD` is defined, and `IsClientBuild` is its
opposite. `IsEditor` is true when `UNITY_EDITOR` is defined.

**Limits:**
- Nothing in the package reads these constants.

### `NativeProjectile.cs`
**What it does:** The 2D projectile state struct that is shared with the native
library. It has an explicit layout of 72 bytes: 15 floats, two `ushort`, one
`uint` and four bytes.

| Offset | Field | Meaning |
|---|---|---|
| 0, 4 | `X`, `Y` | Position. Rust writes it every tick. |
| 8, 12 | `Vx`, `Vy` | Velocity. |
| 16, 20 | `Ax`, `Ay` | Acceleration. `Ay` carries gravity. For Wave and Circular they hold the perpendicular axis, for Guided the homing direction. |
| 24 | `AngleDeg` | Visual rotation in degrees. |
| 28 | `CurveT` | Arc or phase accumulator used by some movement types. |
| 32, 36 | `ScaleX`, `ScaleY` | Current scale in world units. |
| 40, 44 | `ScaleTarget`, `ScaleSpeed` | Growth target and rate. |
| 48, 52 | `Lifetime`, `MaxLifetime` | Seconds left and total seconds. A projectile whose `Lifetime` reaches 0 dies in the tick. |
| 56 | `TravelDist` | Distance flown, used for damage falloff. |
| 60, 62 | `ConfigId`, `OwnerId` | `ushort` identity, written once at spawn. |
| 64 | `ProjId` | `uint` projectile id, written once at spawn. |
| 68-71 | `CollisionCount`, `MovementType`, `PiercingType`, `Alive` | Flags. `Alive` is 0 for dead, 1 for alive. |

**How it works:** The layout has to match `NativeProjectile` in
`rust_lib/projectile_core/src/lib.rs`. Offsets and types were compared field by
field against that struct. At startup `ProjectileLib.ValidateStructSizes()`
compares the size (72) with what the loaded library reports. The 3D struct and
every other FFI struct are in `ProjectileLib.cs`.

### `ProjectileLib.cs`
**What it does:** The whole FFI layer for the native `projectile_core` library.
It is the only file in the package with `DllImport`. It holds the P/Invoke
bindings, the FFI structs and enums other than `NativeProjectile`, the
availability probe and the startup checks.

**Library resolution:** The binding name is `"__Internal"` on iOS and WebGL
players, where the static library is linked by Xcode or Emscripten, and
`"projectile_core"` everywhere else, including the Editor. The files under
`Runtime/Plugins/Native` are:

| Folder | File | Import settings in the `.meta` |
|---|---|---|
| `Android/arm64-v8a` | `libprojectile_core.so` | Android ARM64. Also enabled for the Editor and the Linux, macOS and Windows standalone targets. |
| `Android/armeabi-v7a` | `libprojectile_core.so` | Android ARMv7. Editor off. |
| `Android/x86_64` | `libprojectile_core.so` | Android X86_64. Also enabled for the Editor and the standalone targets, like arm64. |
| `Linux` | `libprojectile_core.so` | Linux64 standalone only. |
| `macOS` | `libprojectile_core.dylib` | Editor and macOS standalone. |
| `Windows` | `projectile_core.dll` | Any platform except the Editor, no per-platform entries. |
| `iOS` | `libprojectile_core.a`, `libprojectile_core_sim.a` | Any platform except the Editor, for both files. |
| `WebGL` | `libprojectile_core.a` | Any platform except the Editor. |

There is no 32-bit Android x86 build. The Windows library has no per-platform
import entries, so on Unity's side it is not restricted to Windows (see the
package findings for what to check in Unity).

**Structs and enums:**
- `SpawnRequest` (32 bytes), `HitResult` (24), `CollisionTarget` (20),
  `NativeProjectile3D` (84), `HitResult3D` (28) and `CollisionTarget3D` (24).
  Each has an explicit layout that has to match the Rust `repr(C)` struct. All
  sizes are checked at startup.
- `ShapeCollider2D` (76 bytes) and `ShapeCollider3D` (108). A shape is a sequence
  of up to `MaxPoints` = 8 points in world space, a `Thickness` (capsule or edge
  radius, 0 for a bare box or polygon edge), a `Closed` flag that wraps the last
  point to the first, and an `Active` flag. `PointCount` must be 2 to 8.
  `SetPoint(index, worldPos)` and `GetPoint(index)` take 0 to 7 and throw
  `ArgumentOutOfRangeException` outside it. A moving shape has to set its points
  again every tick it moves. `ShapeType` (`ShapeColliderType`: `Box`, `Capsule`,
  `Edge`, `Polygon`) is descriptive only. The native side runs the same
  closest-point-on-segment test for every shape.
- `Vec2Raw` and `Vec3Raw` are the point types embedded in the shape structs.
- `NativeProjectile3D` also has `IsAlive`, `CollisionRadius` (`ScaleX * 0.5`),
  `Position`, `VisualRotation()` (look rotation along the velocity, identity when
  the speed is almost 0) and `SetAcceleration(Vector3)`.
- `ProjectileMovementType`: `Straight` 0, `Guided` 2, `Teleport` 3, `Wave` 4,
  `Circular` 5 and `CustomCurve` 6. Value 1 is unassigned. `CustomCurve` is for
  physics projectiles only (`PhysicsProjectileBase.ApplyCustomCurve`). The native
  tick has no case for 6 and flies such a projectile straight, so it must not be
  spawned in the Rust sim.
- `ProjectilePiercingType`: `None` 0, `Piecer` 1, `Random` 2.
- `PatternId` (`Single`, `Spread3`, `Spread5`, `Spiral`, `Ring8`) belongs to the
  legacy `spawn_pattern` path. Nothing in the package uses it.
- `MovementTypeConstants` holds the five movement values read from the library, and
  `ProjectileLib.MovementTypes` is its single instance.

**Bindings:** The 32 `extern` bindings match the 32 exports in `lib.rs` by name,
parameter order and type.
- Layout checks: `projectile_struct_size`, `hit_result_struct_size`,
  `collision_target_struct_size`, `spawn_request_struct_size`,
  `projectile3d_struct_size`, `hit_result3d_struct_size`,
  `collision_target3d_struct_size`, `shape_collider_2d_struct_size`,
  `shape_collider_3d_struct_size` and `shape_collider_max_points`.
- Movement constants: `movement_type_straight`, `_guided`, `_teleport`, `_wave`
  and `_circular`. There is no arching function.
- Tick: `tick_projectiles` and `tick_projectiles_3d` take the pinned buffer, the
  count and the step, and return how many projectiles died. They are called by
  `ServerProjectileAuthority` and `LocalProjectileManager`.
- Collision: `check_hits_grid_ex` (2D, takes a cell size) and `check_hits_grid_3d`.
  `check_hits_shapes_2d` and `check_hits_shapes_3d` are called right after, with
  the same hit buffer and `hitOffset` set to the hit count just produced. They
  return the new total, so the result is assigned straight back.
- Spawn: `spawn_batch` and `spawn_batch_3d` copy `min(count, maxOut)` entries into
  the buffer as they are. They are called from `BatchSpawnHelper`.
- State: `save_state` and `restore_state`, called from `ServerProjectileAuthority`.
- Movement parameters: `register_wave_params`, `register_circular_params`,
  `unregister_wave_params`, `unregister_circular_params` (called from
  `ProjectileConfigSO`) and `clear_movement_params` (called from
  `ServerProjectileAuthority` and `MID_MasterProjectileSystem`).
- Not called by the package: `spawn_pattern` (legacy, with `SpawnRequest`) and
  `check_hits_grid`, which forwards to the `_ex` version with a cell size of 0.

**Startup checks:**
- `IsAvailable` makes one cheap probe call (`projectile_struct_size`) the first
  time it is read and caches the result. A `DllNotFoundException`,
  `EntryPointNotFoundException` or `BadImageFormatException` logs one error that
  names the Android plugin CPU setting and caches `false`.
- `ValidateStructSizes()` throws `InvalidOperationException` when the library is
  not available, and again after logging every mismatch. For the nine structs it
  checks that the C# `Marshal.SizeOf`, the size the library reports and the
  expected number (72, 24, 20, 32, 84, 28, 24, 76, 108) all agree. It also
  compares the shape point limit with `ShapeCollider2D.MaxPoints`, and calls
  `FetchMovementTypeConstants()`, which reads the five native movement values
  and logs an error for each one that differs from `ProjectileMovementType`.
  `ProjectileRegistry` and `MID_MasterProjectileSystem` call it once at startup.
- `ProjectileConfigSO` checks `IsAvailable` before registering Wave and Circular
  parameters with the library.

**Limits:**
- Only sizes and the five movement constants are checked at runtime. A field that
  moves inside a struct without changing its size is not detected. `lib.rs` also
  has compile-time layout assertions on the Rust side.
- `CustomCurve` has no native constant and is not part of the constant check.

### `ProjectileManager.cs`
**What it does:** An empty sealed marker `MonoBehaviour`, shown in the Add
Component menu as `MidMan/Projectile System/Projectile Manager`.
`ProjectileRenderer2D` and `TrailObjectPool` carry
`[RequireComponent(typeof(ProjectileManager))]`, so Unity adds it next to them.
`ProjectileRenderer3D` does not require it. `LocalProjectileManager` and
`ServerProjectileAuthority` do not look it up. They hold serialized references to
the renderer and pool components.

### `RustSpawnParams.cs`
**What it does:** The small struct that carries the per-fire-event values the
Rust sim and the fill jobs need, with no Unity objects in it. It is filled once
per spawn event, not per projectile, by `ProjectileConfigSO.GetRustSpawnParams(speedOverride)`.
`ProjectileRegistry.GetRustSpawnParams(configId, speedOverride)` is the entry
point the spawn paths use. It logs an error and returns `default` when the id is
unknown.

Fields: `Speed` (the override when it is above 0, otherwise the config's
resolved speed), `MovementType`, `PiercingType`, `MaxCollisions`, `Lifetime`,
`GravityAy`, `ScaleStart`, `ScaleTarget`, `ScaleSpeed` and `Is3D`.

**How it works:**
- Scale values are world units, not fractions. With growth on, `ScaleStart` is
  `FullSizeX` times the spawn scale fraction and `ScaleSpeed` is the growth speed.
  With growth off, `ScaleStart` and `ScaleTarget` are both `FullSizeX` and
  `ScaleSpeed` is 0. Only the X size is carried. `BatchSpawnHelper` writes it to
  every scale axis.
- The native tick moves the scale by `(ScaleTarget - scale) * ScaleSpeed * dt` and
  stops once it is within 0.001 of the target. `ScaleSpeed` 0 skips it.

**Limits:**
- `BatchSpawnHelper` does not read `MaxCollisions` or `Is3D`. The 2D or 3D choice
  is made by calling `SpawnBatch2D` or `SpawnBatch3D`, and the piercing budget
  comes from `ServerProjectileData`.

### `SimulationMode.cs`
**What it does:** Two byte enums. `SimulationMode` says how one fire event is
simulated and networked: `Raycast = 0`, `RustSim = 1`, `PhysicsObject = 3` and
`LocalOnly = 4`. `NetworkVariant` says who has authority: `None = 0` (no
network, used with `LocalOnly`) and `ServerAuth = 1` (the server runs the sim,
clients predict and reconcile). `ProjectileTypeRouter.Route` picks both for
every fire event, `MID_MasterProjectileSystem` switches on the mode, and
`ProjectileConfigSO` stores a per-config override in `_preferredSimMode`.

**Decisions:**
- There is no separate 3D mode. `ProjectileConfigSO.Is3D` selects the 2D or 3D
  Rust buffer, so `RustSim` covers both.
- Value 2 is unassigned. A config asset that still stores 2 shows "(2)" in the
  inspector and needs `RustSim` assigned again.

### `MID_AllProjectileNames.cs`
**What it does:** An enum in the `MidManStudio.Projectiles.Network` namespace
with the single value `Default`. It is only used as the first parameter of
`INetworkProjectileVisual.Initialize`. The file sits in `Runtime/Data`, but
the namespace is `Network`.

**Limits:**
- It is a placeholder. Nothing in the package implements `INetworkProjectileVisual`
  or passes a different value.

### `ServerProjectileData.cs`
**What it does:** A plain class (not a struct) that holds the server-side
gameplay data for one projectile, keyed by `projectileId_u32` in `RustSimAdapter`.
Rust never reads it, and it never stores a position, because Rust owns that.

| Group | Fields |
|---|---|
| Identity | `projectileId_u32`, `configId`, `is3D` |
| Owner | `ownerClientId` (`ulong`), `firedByNetworkObjectId`, `isBotOwner`, `weaponLevel` |
| Damage | `damageMultiplier`, `isCrit`, `critChance` |
| Collision | `collisionsRemaining` (`byte`), `hasHit` |
| Spawn position | `spawnPosition2D`, `spawnPosition3D` |
| Attribution | `KillTypeRaw`, `DamageTypeRaw`, `WeaponTypeRaw` (plain `int`, cast to the game's own enums) |

**How it works:**
- The constructor takes the owner, weapon, spawn position, damage multiplier, the
  config and the three attribution values. It copies `config.CritChance` and sets
  `collisionsRemaining` from the piercing type: `None` and unknown types 1,
  `Piecer` the config's `MaxCollisions`, `Random` a random value from 1 to
  `MaxCollisions` (inclusive). With no config it is 1.
- Spawn code builds one template per fire event and calls
  `CloneForSpawn(projId, cfgId)` for each projectile. It uses `MemberwiseClone`
  and then resets `hasHit` and `isCrit`. `CloneForSpawn3D` also sets `is3D` and
  `spawnPosition3D`.
- `ServerProjectileAuthority` rolls `isCrit` after cloning, as
  `Random.value < critChance`.
- `IsDead()` is `hasHit && collisionsRemaining <= 0`. It does not look at the Rust
  `Alive` flag.

**Limits:**
- The package creates plain `ServerProjectileData` instances in its own spawn
  paths (`MID_ProjectileNetworkBridge`, `RaycastProjectileHandler`). A subclass
  with extra game fields is only used where game code creates and registers it.

### `BatchSpawnHelper.cs`
**What it does:** Writes `SpawnPoint[]` into the native projectile buffers.
`SpawnBatch2D` and `SpawnBatch3D` build the `NativeProjectile` or
`NativeProjectile3D` entries, apply latency compensation and hand them to the
native `spawn_batch` call. The file also defines `SpawnPoint` (`Origin`,
`Direction`, `Speed`, `StartDelay`) and the two Burst jobs, `BurstFill2DJob` and
`BurstFill3DJob`.

`SpawnBatch2D(spawnPoints, count, config, rustParams, configId, ownerId, nextProjId, projsOutPtr, bufferRemaining, latencyCompensation = 0)`
and `SpawnBatch3D(...)` without the `config` parameter return how many entries
the native call wrote. `Initialise()` pins the two static staging arrays (256
entries each) and `Shutdown()` frees them. Every spawn call initialises them
again if they were freed, so a manager shutting down does not break another.

**How it works:**
1. The count is `min(count, 256, bufferRemaining)`. Zero or less returns 0.
2. Batches of 8 or more (`BurstThreshold`) use the Burst job, smaller ones a
   managed loop. Both write the same fields.
3. Per projectile: position from `Origin`; velocity is `Direction * speed`, where
   speed is `SpawnPoint.Speed` when it is above 0 and `RustSpawnParams.Speed`
   otherwise; scale from `RustSpawnParams`; `Lifetime` and `MaxLifetime` from
   `RustSpawnParams.Lifetime`; `ProjId = nextProjId + index`; `ConfigId`,
   `OwnerId`, movement and piercing type from the arguments; `CollisionCount` 0
   and `Alive` 1. In 2D, `AngleDeg` is `atan2(Direction.y, Direction.x)` in
   degrees.
4. `Ax` and `Ay` (and `Az`): Wave and Circular get the perpendicular of the
   direction. In 2D that is `(-Direction.y, Direction.x)`. In 3D it is the same
   in the XY plane, normalised, and `(1, 0, 0)` when the direction is almost
   straight along Z. Every other type gets `(0, GravityAy)` (and `Az` 0).
5. Latency compensation above 0 subtracts that many seconds from `Lifetime` and
   sets `Alive` to 0 when nothing is left. It never moves the projectile.
6. `spawn_batch` or `spawn_batch_3d` copies the entries into the buffer.

**Limits:**
- Each call writes at most 256 projectiles. A larger batch is cut at 256 and the
  returned count is smaller than `count`.
- The native copy does not skip entries with `Alive` 0. A projectile that is dead
  on arrival (latency larger than its lifetime) still takes a buffer slot until
  the next compaction.
- `SpawnBatch2D` takes a `config` parameter that it does not read.
- `SpawnPoint.StartDelay` is carried by the struct and not read here.
  `DelayedSpawnQueue` has already held the projectile back by the time it reaches
  this file.
- The Burst path copies the whole `spawnPoints` array into a temporary
  `NativeArray`, not only the first `n` entries.

### `ProjectileTypeRouter.cs`
**What it does:** Decides how one fire event is simulated. The file has three
public types.
- `WeaponFireContext`: what the weapon passes in. Fields: `FireRate` (rounds per
  second), `IsRaycastWeapon` (the weapon script runs the Physics2D/3D raycast
  itself, the system only handles visuals and RPCs), `ProjectileCount`,
  `IsNetworked` (false forces `LocalOnly`), `LatencyCompensation` (seconds),
  `OwnerMidId`, `FiredByNetworkObjectId`, `IsBotOwner`, `WeaponLevel` and
  `DamageMultiplier`.
- `RoutingResult`: `Mode`, `Network` and `WasOverridden`.
- `ProjectileTypeRouter`: static, pure functions with no side effects.

**How it works:** `Route(config, context)` checks in this order:
1. Not networked: `LocalOnly` with `NetworkVariant.None`.
2. The config has a simulation mode override (`HasSimModeOverride`, any
   `_preferredSimMode` other than `RustSim`): that mode, `ServerAuth`,
   `WasOverridden = true`.
3. `config.RequiresPhysicsObject()`: `PhysicsObject`.
4. `context.IsRaycastWeapon` and `config.IsRaycastEligible()`: `Raycast`.
5. Otherwise `RustSim`. `Is3D` on the config picks the 2D or 3D buffer later.

`RequiresPhysicsObject` and `IsRaycastEligible` are virtual methods on
`ProjectileConfigSO`, so game code changes routing by overriding them in a derived
config. `ExplainRoute` returns a text for the same decision and is meant for
editor tools and debug panels. `RequiresPhysicsObject(config)` and
`IsRaycastEligible(config)` on the router are shortcuts to the config methods.

**Limits:**
- `Route` does not check for a null config.
- An override to `LocalOnly` or `PhysicsObject` on a networked session still gets
  `NetworkVariant.ServerAuth`.

### `RustSimAdapter.cs`
**What it does:** The server-side bridge between native hit results and game
damage. The file defines `ProjectileHitPayload` and `RustSimAdapter`, a sealed
class. `ServerProjectileAuthority` owns one instance.

`RustSimAdapter` keeps a `Dictionary<uint, ServerProjectileData>` of live
projectiles (initial capacity 512) and offers `Register`, `Unregister`,
`IsRegistered` and `TryGetData`. Events: `OnProjectileHit(ProjectileHitPayload)`
and `OnProjectileDied(uint)`.

`ProjectileHitPayload` fields: `ProjId`, `ConfigId`, `Is3D`, `TargetId`,
`Damage`, `IsHeadshot`, `IsCrit`, `HitPosition`, `OwnerMidId`,
`FiredByNetworkObjectId`, `IsBotOwner`, `WeaponLevel`, `DamageMultiplier`,
`GameData` (the `ServerProjectileData`) and `HitObject` (the exact object that was
hit, null for explosion damage). The package never reads `HitObject`. It lets
game code reach a component on the hit collider, a hitbox for headshot detection
for example.

**How it works:**
- `ProcessHit(in HitResult, bool isHeadshot)` and `ProcessHit3D(in HitResult3D, bool)`
  look up the projectile data by `ProjId` (a miss is ignored) and the config in
  `ProjectileRegistry`, compute the damage, fire `OnProjectileHit` and then run the
  piercing logic. A missing config is logged as an error in the 2D method and
  ignored silently in the 3D one.
- Damage: the normalised distance is `clamp01(TravelDist / MaxRange)` (0 when
  `MaxRange` is 0). The damage is `config.EvaluateDamage(normalisedDistance)`,
  times `HeadshotMultiplier` for a headshot, times `CritMultiplier` when `isCrit`
  is set, times the data's `damageMultiplier`.
- `HandlePiercing`: `PiercingType.None` unregisters the projectile at once and
  fires `OnProjectileDied`. `ServerProjectileAuthority.Collision2D` and
  `Collision3D` check `!Adapter.IsRegistered` after each hit and then clear
  `Alive` in the Rust buffer, so unregistering is what kills it. `Piecer` and
  `Random` decrement `collisionsRemaining` and unregister only at 0. An unknown
  type is treated like `None`.
- `NotifyDead(projId)` is called by `CompactDead2D` and `CompactDead3D` for each
  projectile that Rust killed by lifetime. It does nothing when the projectile is
  already unregistered, so a hit-killed projectile does not fire
  `OnProjectileDied` twice.
- `SetHomingDirection2D(ref NativeProjectile, Vector2)` and
  `SetHomingDirection3D(ref NativeProjectile3D, Vector3)` write the normalised
  direction into `Ax`, `Ay` (and `Az`), which the Guided tick reads.

**Limits:**
- This path leaves `ProjectileHitPayload.DamageMultiplier` at 0. The multiplier is
  already inside `Damage`, and `GameData.damageMultiplier` holds the value. The
  physics path sets the payload field.
- `Random` piercing differs from `Piecer` only in how many collisions are
  allowed. The random count is chosen at spawn in `ServerProjectileData`.

### `MathFormulaEvaluator.cs`
**What it does:** A small recursive-descent parser for one-line math expressions,
with no dependencies. It is used by `ProjectileShapeSO` (mesh vertices),
`ProjectilePatternSO` (shot angles) and `ProjectileConfigSO` (custom movement
path in Formula mode). The editor panels use `Validate` and `GetExamples` for live
feedback.

**API:**
- `FormulaContext` carries `t`, `i` and `n`. `FormulaContext.For(t, i = 0, n = 1)`
  builds one.
- `Evaluate(formula, ctx, out error)` returns the value. On any failure it returns
  0 and sets `error`. On success `error` is null.
- `Validate(formula, out error)` evaluates with `t = 0.5`, `i = 0`, `n = 8` and
  returns true when there is no error.
- `GetExamples(FormulaUsage)` returns four preset strings for each of `ShapeX`,
  `ShapeY`, `PatternH`, `PatternV`, `PathX` and `PathY`.

**Syntax:**
- Numbers (`1`, `1.5`, `.5`), parsed with the invariant culture. There is no
  exponent notation such as `1e5`.
- Variables `t`, `i`, `n`, `pi`, `tau` and `e`.
- Operators `+ - * / ^ %`, a unary minus and parentheses. `^` accepts one use per
  expression level: `2^3^2` fails, `2^(3^2)` works. A unary minus binds tighter
  than `^`, so `-2^2` is 4.
- Functions: `sin cos tan asin acos atan atan2(y,x) sqrt abs floor ceil round sign
  frac saturate pow(x,y) log log2 exp min max clamp lerp mod deg rad step
  smoothstep pingpong repeat`.
- Variable and function names are not case sensitive.

**How it works:**
- `/`, `%` and `mod` by zero, an unknown name, a missing parenthesis, and a result
  that is NaN or infinite are errors. `sqrt` of a negative number gives 0, `log`
  of a value at or below 0 uses a tiny positive number, and `asin` and `acos`
  clamp their input, so those three never raise an error.
- Extra function arguments are ignored. Too few arguments give "Unknown function".
- The class is static and keeps no shared state, so it is thread safe.

**Limits:**
- Nothing is cached. Every call builds a lexer and a parser, and every function
  call allocates a small list, so a formula that is evaluated every frame
  produces garbage.

### `ProjectileConfigEntry.cs`
**What it does:** A serializable row inside a provider: `enumName`, `configSO`,
`comment` (written next to the generated enum member) and `explicitOffset`. A
`-1` offset means the generator assigns the position. A value of 0 or more pins
the entry to that offset inside the provider's block, so an enum value that a
serialized field refers to does not move when entries are added above it.
`Name` is the enum name, else the asset name, else "Unnamed Entry". It implements
`IArrayElementTitle` (utilities) for the list drawer. Entries with a null
`configSO` are skipped by the generator.

### `ProjectileConfigGeneratorSettingsSO.cs`
**What it does:** The project-wide settings for the enum generator. Defaults:

| Field | Default |
|---|---|
| `enumOutputPath` | `Assets/MidManStudio/Generated/Projectiles/ProjectileConfigType.cs` |
| `mappingAssetPath` | `Assets/MidManStudio/Generated/Projectiles/ProjectileConfigMapping.asset` |
| `lockFilePath` | `Assets/MidManStudio/Generated/Projectiles/ConfigTypeLock.json` (commit it, it keeps the enum values stable) |
| `minimumBlockSize` | 50, at least 10. Each provider's block is a multiple of this. |
| `generatedNamespace` | `MidManStudio.Projectiles.Config` |
| `autoGenerateOnAssetChange` | false |

The menu entry is `MidManStudio > Projectile System > Config Generator Settings`.
The generator itself is described under `ProjectileConfigGenerator.cs`.

### `ProjectileConfigManager.cs`
**What it does:** Bridges the generated `ProjectileConfigType` enum to the
session-local config ids. It is a singleton with `[DefaultExecutionOrder(-150)]`,
placed after `ProjectileRegistry` (-200) and meant to sit on the same object.
Assign the generated `ProjectileConfigMappingSO` to `_mapping`. The file also
holds `ProjectileConfigTypeExtensions`.

**How it works:**
- `Awake` calls `RegisterAll(_mapping)` when a mapping is assigned.
- `RegisterAll(mapping)` returns quietly when `ProjectileRegistry` does not exist.
  Otherwise it clears the table and, for each non-null slot `i`, registers the
  config and stores `i -> id`. Null slots are padding between provider blocks and
  are skipped. The registry assigns ids in registration order, so an id is not
  guaranteed to equal the enum value. Configs that `ProjectileRegistry`
  auto-registered earlier are not registered twice.
- `GetConfigId(int)` returns the id, or logs a warning and returns
  `ushort.MaxValue` when the enum value is unknown.
- `ProjectileConfigTypeExtensions.Fire(this MID_MasterProjectileSystem, int
  configTypeValue, SpawnPoint[], int count, WeaponFireContext)` looks the id up and
  calls `system.Fire(id, spawnPoints, count, context)`. It logs an error and
  returns when the manager does not exist, and returns without a message when the
  lookup fails.
- A context menu entry logs the table.

**Limits:**
- The extension takes the enum as an `int`, so call it with
  `(int)ProjectileConfigType.X`. It forwards only the four plain arguments, not
  `patternId`, `spreadDeg`, `baseDirection`, `guidedTarget` or `fireSeed`. A
  weapon that needs those calls `GetConfigId` and the full `Fire` itself.
- `_LogLevel` defaults to `None`, so the "registry not found" message and the
  `GetConfigId` warning are silent by default. The "registry not found" text still
  mentions `Start()`, although registration runs in `Awake()`.

### `ProjectileConfigMappingSO.cs`
**What it does:** A plain `ScriptableObject` that holds the generated array
`Configs`, where index `i` is `ProjectileConfigType` value `i`. Null entries pad
the gaps between provider blocks. It is created by `ProjectileConfigGenerator`
(`SetConfigs` is for the generator only) and assigned to
`ProjectileConfigManager`. The menu entry is
`MidManStudio > Projectile System > Config Type Mapping`. Do not edit it by hand,
because the next generation rewrites it.

### `ProjectileConfigProviderSO.cs`
**What it does:** One provider per package or game assembly that contributes
named configs to the generated enum. Fields: `packageId` (reverse-domain, unique
across providers), `displayName`, `priority` (lower means an earlier block in the
enum, 0 is reserved for system use and 100 is the suggested start for game code)
and the `entries` list. Created from
`MidManStudio > Projectile System > Config Type Provider`.

### `ProjectileConfigSO.cs`
**What it does:** One `ProjectileConfigSO` describes one projectile type. It is
the single source of truth for movement, damage, size, visuals, trail, impact
effect and routing. The asset derives from `MID_BaseSO` (utilities, adds an
optional per-asset icon) and is created from
`MidManStudio > Projectile System > Projectile Config`. The file also declares
two small enums used by the custom path, `PathShape` (`Points`, `Formula`) and
`PathSplineType` (`Linear`, `CatmullRom`, `Bezier`).

`ConfigId` is a public hidden `ushort` that `ProjectileRegistry.Register` writes
at runtime. It is the id that travels in spawn requests, native structs and RPCs.
It is not a stable asset id and must not be authored by hand.

**Fields and who reads them:**

| Group | Fields | Used by |
|---|---|---|
| Simulation | `Is3D`, `PreferredSimMode` | `ProjectileTypeRouter`, `ProjectileRegistry` (2D or 3D buffer). `HasSimModeOverride` is true for any mode other than `RustSim`. |
| Routing hooks | `RequiresPhysicsObject()` (virtual, false), `IsRaycastEligible()` (virtual) | `ProjectileTypeRouter`. Eligible when piercing is `None`, the config is 2D and `RequiresPhysicsObject()` is false. |
| Movement | `MovementType`, `MinSpeed`, `MaxSpeed`, `Lifetime`, `GravityScale`, `MaxRange` | `GetRustSpawnParams`, the physics projectiles, damage falloff |
| Piercing | `PiercingType`, `MaxCollisions` (1 to 16) | `ServerProjectileData`, `RustSimAdapter` |
| Collision layers | `HitLayers` (`LayerMask`, default everything) | `ServerProjectileAuthority.PassesLayerMask` on the Rust hit path, `PhysicsProjectileBase` for the direct hit and the explosion overlap |
| Sorting | `SortingLayerName`, `SortingOrderInLayer`, `SortingPriority` | `ProjectileRenderer2D` (Z offset), `TrailObjectPool` (real `TrailRenderer` sorting) |
| Size | `FullSizeX`, `FullSizeY`, `UseScaleGrowth`, `SpawnScaleFraction` (0.01 to 1), `GrowthSpeed` (1 to 30) | `GetRustSpawnParams`, the renderers and visuals |
| Damage | `DamageCurve`, `HeadshotMultiplier` (1 to 5), `CritChance` (0 to 1), `CritMultiplier` (1 to 5) | `EvaluateDamage`, `RustSimAdapter`, `ServerProjectileData` |
| Visual | `ProjectileSprite`, `UseSprite`, `CustomShape` | the renderers, `ProjectileVisual_2D/3D`, the registry's atlas warm-up |
| Trail | `HasTrail`, `TrailMaterial`, `UseGradientOverride`, `TrailGradient`, `TrailTime`, `TrailStartWidth`, `TrailEndWidth`, `TrailMinVertexDistance`, `TrailCapVertices`, `UseSharedTrailMaterial` | `ProjectileVisual_2D/3D` and `TrailObjectPool` |
| Impact | `ImpactEffectType` (`PoolableParticleType`) | `ProjectileImpactHandler` |
| Wave | `WaveAmplitude`, `WaveFrequency`, `WavePhaseOffset`, `WaveVertical` | native `register_wave_params`, `PhysicsProjectileBase` |
| Circular | `CircularRadius`, `CircularAngularSpeed` (degrees per second), `CircularStartAngle` (degrees) | native `register_circular_params`, `PhysicsProjectileBase` |
| Custom curve | `CustomCurveSpeedMultiplier`, `CustomCurveDuration`, `CustomCurveLoop`, `CustomPathShape`, `CustomPathSplineType`, `CustomPathPoints`, `CustomPathFormulaX`, `CustomPathFormulaY` | physics projectiles only, and the editor path preview |

**How it works:**
- `ResolveSpeed()` returns `MinSpeed` when min and max are equal, otherwise a
  random value between them. `GetRustSpawnParams(speedOverride)` uses the override
  when it is above 0. The rest of what that call writes is described under
  `RustSpawnParams.cs`.
- `EvaluateDamage(d)` evaluates `DamageCurve` at `d` clamped to 0..1. `d` is
  distance flown divided by `MaxRange`.
- `SortingPriority` is `SortingLayer.GetLayerValueFromName(layer) * 100000 +
  order`. Only the relative order between two configs means anything. A sorting
  order outside roughly +/-100000 can overlap the next layer.
- `GravityScale` means different things per mode. In the Rust sim it is written
  as the Y acceleration, in world units per second squared, so a positive value
  pushes up. `PhysicsProjectile2D` copies it into `Rigidbody2D.gravityScale`.
  `PhysicsProjectile3D` only uses it as a switch: above 0 turns `useGravity` on,
  anything else leaves the prefab setting. Wave, Circular and Teleport shots are
  forced to no gravity on physics projectiles.
- `RegisterMovementParams()` registers Wave or Circular parameters with the
  native sim under `ConfigId`. It does nothing for other movement types, and
  logs a warning and returns when `ProjectileLib.IsAvailable` is false.
  `UnregisterMovementParams()` is the reverse. `ProjectileRegistry` calls both.
- `EvaluateCustomPath(t)` returns the offset `(forward, sideways)` from the spawn
  point at progress `t` (clamped to 0..1). Forward is along the launch direction
  and sideways is the Wave axis.
  - Points mode: the spawn point `(0, 0)` is an implicit first point and the
    listed points follow it, so `n` is the list length plus one. `Linear` gives
    equal `t` spacing per segment, not equal distance. `CatmullRom` passes through
    every point, with the end segments clamped to the first and last point.
    `Bezier` runs De Casteljau over all `n` points, so it does not pass through
    the interior points. `t = 0` is exactly `(0, 0)` and `t = 1` is exactly the
    last listed point in all three modes. An empty list returns `(0, 0)` for
    every `t`.
  - Formula mode: `X(t)` and `Y(t)` come from `MathFormulaEvaluator` with
    `FormulaContext.For(t)`. The result is `X(t) - X(0)` and `Y(t) - Y(0)`, so the
    path always starts at the spawn point. The error text is discarded here
    (`out _`) and the evaluator returns 0 on any failure, so a broken `X(t)` gives
    a forward offset of 0 and a broken `Y(t)` a sideways offset of 0, with no log
    message.
- `OnValidate` (editor only) keeps `MaxSpeed >= MinSpeed`, resets a size of 0 or
  less to 0.05, and in play mode calls `RegisterMovementParams()` so Wave and
  Circular edits apply live.

**Limits:**
- `CustomCurve` is for physics projectiles only. Nothing stops it being set on a
  RustSim config, and the native tick then flies it straight.
- `IsDamageConstant()` has no callers.
- `OnValidate` in play mode re-registers for any config that is edited, including
  one the registry never registered. Such a config has `ConfigId` 0 and would
  overwrite the Wave or Circular parameters of config 0.
- `ConfigId` is a serialized field that is written at runtime. In the editor that
  value can end up in the asset file if the asset is saved after a play session.
  It is overwritten again at the next registration.
- Formula mode evaluates the formula four times per call (`X`, `Y`, `X(0)`,
  `Y(0)`).

### `ProjectilePatternRegistry.cs`
**What it does:** The table from `ushort patternId` to `ProjectilePatternSO`. It is
a singleton that sits on the same persistent object as `ProjectileRegistry`.
Patterns that are fired over the network go in its `_autoRegister` array.

**How it works:**
- The id is a stable hash of the asset name, not its list position. It is a 32-bit
  FNV-1a over the UTF-8 bytes of the name, folded to 16 bits and shifted so it is
  in 1 to 65534. 0 stays reserved as "no pattern". The same name gives the same id
  in every build, so list order and scene differences cannot make two peers
  disagree.
- `Register(pattern)` returns the id and stores it in `pattern.PatternId`. It
  returns the existing id for a name that is already registered, and 0 for a null
  asset or for a hash collision with a different pattern (after logging an error,
  so rename one of the two assets).
- `Get(id)` returns the pattern, or null for 0 or an unknown id. Callers treat both
  as "use the plain spread path". `TryGetId(name, out id)` and `Count` are also
  there.
- A context menu entry (editor only) logs every pattern.

**Limits:**
- Renaming a pattern asset changes its id. Every connected process has to register
  the same assets under the same names, or an id sent by one side does not resolve
  on the other.
- With 16-bit ids a collision between two pattern names is possible, though rare
  for a handful of patterns.
- There is no `[DefaultExecutionOrder]`, so `PatternId` on the asset can read 0
  before registration has run. Go through `Get`, `TryGetId` or `Register`.
- The log level defaults to `None`, which hides the collision error.

### `ProjectilePatternSO.cs`
**What it does:** A pattern asset for a multi-projectile shot. `SampleDirections`
returns one `(horizontal degrees, vertical degrees)` pair per projectile, in
weapon space. The burst settings give each projectile a start delay, and the speed
variance gives each one a speed multiplier. `ProjectileDirectionResolver` turns
these into `SpawnPoint[]`. The file also defines `PatternShape`,
`PatternSplineType` and `PatternBurstOrder`. The menu entry is
`MidManStudio/Projectile System/Projectile Pattern`.

`PatternId` is a hidden `ushort` that `ProjectilePatternRegistry.Register`
writes. 0 means "not registered" and is also the wire value for "no pattern".

**Shapes (`PatternShape`) and what they return for `n` projectiles:**

| Shape | Result |
|---|---|
| `Spline` | Points along the control-point spline at `i / (n - 1)`. A single projectile uses the middle of the spline. No control points gives all zeros. |
| `Ring360` | Horizontal `i * 360 / n`, vertical 0. |
| `Fan` | Horizontal from `-FanHalfArcDeg` to `+FanHalfArcDeg`, vertical `_fanVerticalDeg`. A single projectile gets horizontal 0 and the same vertical value. |
| `VShape` | Two arms of `max(n / 2, 1)` projectiles at up to `+/- _vShapeAngleDeg`, and one centre projectile when `_vShapeIncludeCenter` is set (then `n - 1` is split). The positive arm comes first, then the negative arm, then the centre. |
| `Shotgun` | Random pairs inside a cone: horizontal within `+/-_shotgunConeDeg`, vertical within half of that. Seeded, see below. |
| `Star` | Identical to `Ring360`: the star point count cancels out of the formula, so `Star Points` changes nothing. |
| `Spiral` | Horizontal `(i * 360 / n + i * _spiralAngleStep) mod 360`, vertical 0. |
| `Formula` | `PatternFormulaH` and `PatternFormulaV` evaluated per projectile with `t = i / n` (0 when `n` is 1), `i` and `n`. A projectile whose formula fails gets `(0, 0)`, without a log message. |

`SampleDirections(count)` uses `ProjectileCount` (1 to 64) when `count` is 0 or
less. `VShape` can return a different number than asked for. Use
`GetFiredProjectileCount()` for ammo cost, which gives the real number without
allocating.

The spline control points are angle pairs. `Linear` is straight segments with equal
`t` spacing per segment, `CatmullRom` passes through every point, and `Bezier` uses
every point as a control vertex (so it does not pass through the interior points).
The enum order differs from `PathSplineType` in `ProjectileConfigSO` (here
`CatmullRom` is 0), so serialized values are not interchangeable.

**Burst timing:** `BurstInterval` (0 to 0.5 s) is the gap between consecutive
projectiles, and 0 releases them all at once. `_burstJitter` changes each delay by
up to that fraction of the interval. `_burstOrder` is `Index`, `Reverse` or
`CenterOut` (the middle index first, symmetric pairs leave together, and for an
even count the two middle ones go first). The order works on the array index. For
the V shape, which lists both arms before the centre, the middle index is not the
centre projectile. `GetStartDelay(index, count, seed)` is a pure function of its
inputs and is clamped to `MaxStartDelay` (2 s). It returns 0 when the interval is
0 or there is one projectile.

**Fire seed:** `MixSeed(fireSeed)` combines the asset's `RngSeed` with the 16-bit
per-shot seed. A fire seed of 0 returns the asset seed, which gives the same layout
every shot. `SampleDirections(count, seed)` uses the seed for `Shotgun`, and
`GetSpeedMultiplier(index, seed)` and `GetStartDelay` use it for speed variance
(`1 +/- SpeedVariance`, up to 0.5) and jitter. All of it uses a small linear
congruential generator with fixed constants, so the same inputs give the same
numbers on every machine.

**Limits:**
- `EvaluateSpline` clones the control point array on every `Bezier` sample.
- A context menu command in the editor (`MidManStudio/Projectile System/Create
  Default Pattern Assets`) creates 15 patterns under
  `Assets/MidManStudio/ProjectileSystem/Patterns` and skips any that exist.

### `ProjectileRegistry.cs`
**What it does:** The runtime table from `ushort configId` to
`ProjectileConfigSO`. It is a singleton (`Singleton<ProjectileRegistry>` from
utilities) with `[DefaultExecutionOrder(-200)]`, meant to sit on the same
persistent object as `ServerProjectileAuthority`. Config ids are assigned in
registration order, are stable for the session only, and are never saved.

**API:**
- `Register(config)` returns the id. It returns the existing id when a config with
  the same asset name is already registered, and `ushort.MaxValue` for a null
  config or when 65534 configs are registered. For a new config it assigns
  `ConfigId`, records the 3D flag, calls `RegisterMovementParams()` inside a
  try/catch, and reads `ProjectileSprite.texture` once (atlas warm-up).
- `RegisterByResourcePath(path)` loads from `Resources` and registers.
- `Get(id)` returns the config, or null when the id is out of range.
- `TryGetId(name, out id)`, `Is3D(id)`, `Count`, `AllConfigs` (read-only, in id
  order, used by `ProjectileVisualWarmupManager`).
- `GetRustSpawnParams(id, speedOverride)` returns the spawn parameters, or logs an
  error and returns `default` when the id is unknown.
- `GetUVRect(id)` returns the sprite's rect in 0-1 texture space, or `(0,0,1,1)`
  when there is no sprite or texture.
- A context menu entry (editor only) logs every registered config.

**How it works:**
- `Awake` first calls `ProjectileLib.ValidateStructSizes()`. On
  `InvalidOperationException` it logs, sets `enabled = false` and returns without
  registering anything. Otherwise it registers every config in the `_autoRegister`
  array.
- `OnDestroy` calls `UnregisterMovementParams()` on every config.

**Limits:**
- Configs are keyed by asset name. Two assets with the same name collide: the
  second `Register` returns the first one's id and its own `ConfigId` stays at
  its old value (0 by default). Keep config asset names unique.
- The `_LogLevel` field defaults to `None`, and `MID_Logger` shows nothing at
  that level, errors included. The null-config, unknown-id and struct-size
  messages from this class are therefore silent until the level is raised. The
  struct-size message from `ProjectileLib` itself goes to `Debug.LogError` and
  is not affected.
- `OnDestroy` does not call `base.OnDestroy()`, so the singleton base class does
  not clear its static instance reference. `Singleton.Instance` still recovers,
  because it treats a destroyed instance as missing.

### `ProjectileShapeSO.cs`
**What it does:** Builds the 2D mesh that a RustSim or physics projectile is drawn
with, instead of a plain quad. Assign one to `ProjectileConfigSO.CustomShape`. The
namespace is `MidManStudio.Projectiles`, not `.Config`. The asset menu entry is
`MidManStudio/Projectile System/Projectile Shape`.

**Presets (`Shape`):** `Quad`, `Needle`, `Diamond`, `Arrow`, `Cross`, `Chevron`,
`Star4`, `Boomerang`, `LetterI`, `LetterT`, `LetterL`, `Custom` and `Formula`.
`Formula` is last on purpose, because assets store the enum as an int.

**How it works:**
- Every mesh lies in the XY plane at z = 0, is 1 unit tall (+/-0.5) and
  `AspectRatio` units wide (0.1 to 8, default 2). The renderers scale it to the
  projectile.
- `GetMesh()` builds the mesh on first use and caches it, and rebuilds it if the
  cached one is empty. `BuildMesh()` always builds a fresh one. In the editor,
  `OnValidate` clears the cache.
- `Custom` takes `Vertices`, `Triangles` and optional `UVs`. Fewer than three
  vertices logs a warning and gives a quad. When `UVs` has a different length from
  `Vertices`, planar UVs are generated from the bounding box
  (`GeneratePlanarUVsPublic` exposes that).
- `Formula` samples `X(t)` and `Y(t)` at `t = k / n` for `k` from 0 to `n - 1`,
  with 3 to 128 samples (default 16), also sets `i` and `n`, adds a centre vertex
  and builds a centre fan. The curve has to run counter-clockwise to face the
  camera, and the shape has to be star-shaped around the origin. Use `Custom` for
  anything else. An empty formula or a formula error logs a warning and gives a
  quad. The UVs divide X by the largest absolute coordinate but add 0.5 to Y
  directly, so keep Y within +/-0.5.
- The preset UVs map the shape's width to 0..1 and its height to 0..1.
- A context menu command in the editor (`MidManStudio/Projectile System/Create
  Default Shape Assets`) creates 11 presets plus `Formula_Circle_16` and
  `Formula_PetalStar_64` under `Assets/MidManStudio/ProjectileSystem/Shapes`, and
  skips any that exist.

**Limits:**
- `AspectRatio` has no effect on `Custom` and `Formula` shapes.
- `Custom` does not check the triangle indices against the vertex count.
- When a cached mesh is replaced it is not destroyed, so repeated edits in the
  editor leave meshes behind until the next domain reload.
- The public accessors `FormulaX`, `FormulaY` and `FormulaSampleCount` are not used
  by the package. The shape editor reads the private fields through
  `SerializedProperty`.

### `WeaponDefinitionSO.cs`
**What it does:** Per-weapon tuning as an asset (`MidManStudio/Projectile System/
Weapon Definition`), derived from `MID_LibraryItemSO` in utilities. Only the test
and sample scripts (`WeaponController`, `NetworkTurretTarget`) read it, and nothing
under `Runtime/` or `Editor/` uses it. A game can use it as is, or copy the idea
into its own weapon data.

**Fields:**

| Group | Fields |
|---|---|
| Identity | `WeaponId` (compact `ushort` synced as the equipped weapon, unique per project), `DisplayName` (falls back to `ItemId`), `WeaponModelPrefab` (optional), `SwitchAnimTrigger` |
| Config ids | `ConfigTypeId2D`, `ConfigTypeId3D`: `ProjectileConfigType` values cast to `int` |
| Fire | `FireRate`, `PelletsPerShot` (1 to 64), `SpreadDeg` (0 to 45), `RaycastPelletCount` (1 to 32), `ShotPattern` |
| Raycast | `RaycastLayers`, `RaycastRange` |
| Physics | `PhysicsPoolType2D`, `PhysicsPoolType3D` (default to the base projectile blueprints), `PhysicsProjectileSpeed`, `PhysicsDamageMultiplier` |
| Audio | `FireSoundClipIndex`, `FireSoundVolume`, `FallbackFireClip`, `FallbackVolume`, `FallbackPitchVariance` |
| Muzzle flash | `MuzzleFlashParticleCount`, `MuzzleFlashVolume` |

`WeaponController.ResolveConfigId` picks the 3D or 2D config type, asks
`ProjectileConfigManager` for the id when it exists, and otherwise casts the value
to `ushort` directly.

**Limits:**
- `SwitchAnimTrigger` is not read by anything. `WeaponController` plays a
  procedural switch animation and no longer fires an Animator trigger. The field
  stays so existing assets keep their serialized data.

### `ClientPredictionManager.cs`
**What it does:** Client-side help for physics projectiles. Rust sim projectiles are
not handled here: `LocalProjectileManager` draws those with the GPU instanced
renderer. A scene holds one `ClientPredictionManager`, and
`MID_MasterProjectileSystem` hands it to the bridge as `Prediction`.

**API:**
- `SpawnLocalPhysicsVisual(configId, origin, direction, speed)` spawns a
  temporary pool visual on the firing client that covers the round trip to the
  server. It flies in a straight line (`origin + direction * speed * elapsed`) for
  the config's `Lifetime`. It returns a handle, or 0 when it did nothing (on the
  server, for an unknown config, or when the pool returns nothing). The 2D or 3D
  pool type comes from `cfg.Is3D` and the two inspector fields
  (`_visualPoolType2D`, `_visualPoolType3D`).
- `KillPhysicsVisual(handle)` returns that visual to its pool at once.
- `OnHitConfirmed(HitConfirmation)` is called by `HitConfirmedClientRpc`. It finds
  the visual nearest to the hit position (within 20 units) and marks it so that it
  glides to the hit point at its own speed and returns to the pool within 0.05
  units.
- `GetDirectionRotation(dir)` and `ApplyDirectionRotation(transform, dir)` are
  public static helpers used by `RaycastProjectileHandler`, `PhysicsProjectileBase`
  and this class. A direction with a Z component below 0.01 gets a Z rotation (2D),
  anything else gets `LookRotation` (with forward as the up hint when the aim is
  near vertical).
- `SetLocalPlayerMidId(ulong)` stores an id that nothing in the class reads.
- `OnRealPhysicsProjectileSpawned(Vector3)` is obsolete and nothing calls it. It
  matched the real object to the nearest predicted visual (within 0.5 units) and
  killed that visual. The firing client no longer receives the real object,
  because `MID_MasterProjectileSystem.SpawnPhysicsProjectile` hides it from that
  client. When called it logs with `Debug.LogError` and a `[PHYSDIAG]` tag.

**How it works:**
- Each visual is an entry in a dictionary keyed by a local handle (1, 2, 3, ...)
  because a physics visual has no server projectile id on the firing client.
- `Update` moves every entry. An entry that passes its lifetime is returned to the
  pool. A confirmed one glides to the hit point. Others follow the straight line.

**Limits:**
- The package never calls `SpawnLocalPhysicsVisual` itself. Only the sample
  `WeaponController` (Tests/Runtime) does, so a game has to call it when it fires
  a physics projectile.
- The hit match is best effort (nearest visual within 20 units). With several shots
  in flight the glide can be applied to a visual from another shot.
- `Update` allocates a new list every frame while any visual exists.
- The straight line ignores gravity and the config's movement type, so the visual
  of a Wave or Circular physics projectile leaves its real path.

### `DeterministicMotionMath.cs`
**What it does:** Closed-form position and velocity formulas for the Wave and
Circular movement types, written to match the Rust tick functions in
`simulation.rs`. The class is `internal` to the package assembly.

**Who uses it:** only `PhysicsProjectileBase`. Each physics tick it calls
`Calculate*VelocityDirection` for Wave or Circular and sets the Rigidbody velocity
from the result. It also uses `ComputePerpAxis2D` and `ComputePerpAxis3D` once at
launch. The four `Calculate*Position` functions and the two look-rotation helpers
have no callers in the package, and `ClientPredictionManager` does not use this
class.

**Functions:**
- `CalculateCircular2DPosition` and `CalculateCircular2DVelocityDirection`: a true
  arc, `P(t) = origin + (V/w) * (sin(t0 + w t) - sin(t0))` on X and the matching
  cosine form on Y, where `t0` is the launch angle after the start-angle
  pre-rotation. Below 0.001 rad/s the arc becomes a straight line.
- `CalculateCircular3DPosition` and `CalculateCircular3DVelocityDirection`: a
  helix. Forward travel plus an orbit of radius `R` in the plane spanned by the
  perpendicular axis `u` and `v2 = cross(forward, u)`.
- `CalculateWave2DPosition` and `CalculateWave3DPosition`: straight travel plus
  `amplitude * (cos(phase) - cos(a t + phase)) / a` along the perpendicular axis,
  where `a = frequency * 2 * pi`. Below a frequency of 0.001 the wave is dropped.
- `CalculateWave2DVelocityDirection` and `CalculateWave3DVelocityDirection`:
  base velocity plus the perpendicular axis times a wave term.
- `ComputePerpAxis2D(dir)` is `(-dir.y, dir.x, 0)`. `ComputePerpAxis3D(dir)` is
  `(-dir.y, dir.x, 0)` divided by the XY length, or `(1, 0, 0)` when the aim is
  along Z. Both must stay identical to `BatchSpawnHelper.GetAccel2D/3D`, or a
  physics projectile oscillates the opposite way to the Rust one.

**Limits:**
- **The wave velocity functions do not match Rust.** Rust's `tick_wave` and
  `tick_wave_3d` move the projectile by `amplitude * sin(a t + phase)` per second
  along the perpendicular axis. The C# velocity functions use
  `-amplitude * a * sin(a t + phase)`. A physics wave projectile therefore swings
  `a` times wider than the same config on the Rust sim (a factor of 2 pi times the
  frequency) and starts toward the opposite side. The position functions have the
  correct integral, so the mismatch only affects the Rigidbody motion. See
  Fixes and Problems.

### `INetworkProjectileVisual.cs`
**What it does:** The contract for a game-side visual object that follows a
`NetworkProjectileBase`. A visual class opts in by declaring
`: INetworkProjectileVisual`. `NetworkProjectileBase` finds it with `GetComponent`
on the pooled visual object.

**Members:**
- `HasHitTarget` is a read-only flag.
- `Initialize(projectileName, currentPosition, targetPosition, positionDelta,
  adjustedOffset, visualSynch, networkManager)` sets the visual up.
- `UpdatePositionInterpolator(position, tick)` receives each networked position.
- `SetHitPosition(hitPosition, tick)` receives the server's hit point.
- `ReturnToPoolImmediate()` hands the visual back to its pool.

**Limits:**
- Nothing in the package implements this interface, and `PhysicsProjectileBase`
  (the only subclass of `NetworkProjectileBase` here) manages its own visual and
  never reaches this path. The auto-spawned visual of the base class is therefore
  only used by a game's own subclass.
- `Initialize` is declared but `NetworkProjectileBase` never calls it. The base
  class calls `UpdatePositionInterpolator`, `SetHitPosition` and
  `ReturnToPoolImmediate`, and leaves `Initialize` to the game's own
  `InitialiseVisual` override.

### `IProjectileObserverProvider.cs`
**What it does:** The one hook the package needs for distance-based interest
management. The package does not know what a player is in your game, so you
implement `TryGetObserverPosition(ulong clientId, out Vector3 position)` and hand
it over:

```csharp
ServerProjectileAuthority.Instance.ObserverProvider = myProvider;
```

**How it works:**
- Return true and a world position when you know where a client is looking from.
  Return false when you do not (still loading, spectating with no target). A client
  you return false for always receives everything, because "unknown" is never
  treated as "far away".
- It is opt-in. With no provider set, or with `Enable Distance Culling` off on
  `ServerProjectileAuthority`, every client receives every snapshot and every
  spawn.
- Two places read it: the periodic snapshot dispatch in `ServerProjectileAuthority`
  and the spawn dispatch in `MID_ProjectileNetworkBridge.FireServerRpc`. Both
  compare against `ServerProjectileAuthority.CullVisRange`.
- The interface returns a `Vector3`, so it serves 2D and 3D games alike. In the
  spawn check, a 2D config ignores Z in the distance test and a 3D config includes
  it.
- `NetworkPlayerObjectObserverProvider` (Tests/Runtime) is the reference
  implementation, wired by `TestSceneBootstrapper`.

**Limits:**
- The spawn check happens once, at fire time. A client that is out of range then
  never spawns that shot, even if it walks into range while the shot is in flight.

### `MID_ProjectileNetworkBridge.cs`
**What it does:** The NGO layer for the whole projectile system. A single
`NetworkBehaviour` that carries the fire requests to the server, spawns the shots
there, tells the other clients and returns hits and snapshots. It also defines the
three wire structs. `MID_MasterProjectileSystem` sets its four references on start
(`Authority`, `Prediction`, `RaycastHandler`, `ImpactHandler`) and listens to its
`OnHitConfirmedLocal` event.

**Wire structs:**
- `ProjectileFireRequest` is 56 bytes. It carries `ConfigId`, `Origin`,
  `Direction`, `Speed`, `ProjectileCount`, `PatternId`, `SpreadDeg`, `PatternIs3D`,
  `OwnerMidId`, `FiredByNetworkObjectId`, `IsBotOwner`, `WeaponLevel`,
  `DamageMultiplier`, `ClientFireTick`, `TargetNetworkObjectId` and `FireSeed`.
  There are no direction or speed arrays. Direction goes through octahedral
  encoding, `Origin` through half precision, `SpreadDeg` through one byte, and the
  two bools share one byte (`WireCompression.cs`). `PatternIs3D` is sent on
  purpose: the client's own 3D convention can differ from the config's `Is3D`, and
  the server cannot rebuild the client's rotation basis from the config alone.
- `SpawnConfirmation` is 43 bytes. It carries `BaseProjId`, `ProjectileCount`,
  `ConfigId`, `ServerSpawnTick`, `Origin`, `Direction`, `Speed`, `OwnerMidId`,
  `PatternId`, `SpreadDeg`, `PatternIs3D`, `ServerNetworkTime` and `FireSeed`. It
  goes to every client except the shooter, so every recipient re-expands the shot
  with `ProjectileDirectionResolver.Resolve`.
- `HitConfirmation` is 32 bytes and not compressed: `ProjId`, `TargetNetworkId`,
  `Damage`, `HitPosition`, `IsHeadshot`, `IsCrit`, `ConfigId`.

**Client to server RPCs** (`ServerRpc`, `RequireOwnership = false`, so any
connected client can call them):
- `FireServerRpc(ProjectileFireRequest)`. The Rust sim fire path, in this order:
  1. Unknown `ConfigId` is logged and dropped.
  2. `Speed` is clamped to the config's `MinSpeed` and `MaxSpeed`.
  3. Latency compensation is `(server tick - ClientFireTick)` times the tick
     interval, clamped to 0 to 0.5 s.
  4. A `ProjectileCount` of 0 or above 128 is rejected.
  5. `ProjectileDirectionResolver.Resolve` rebuilds the shot. A client can only
     choose which registered pattern or spread it asks for.
  6. Projectile ids are reserved for the whole shot (`AllocateProjIds`), so
     projectile `i` is `baseId + i` on every peer, even if it spawns later.
  7. A shot with any start delay goes through
     `ServerProjectileAuthority.SpawnStaggered`. Otherwise `BatchSpawnHelper`
     writes into the 2D or 3D Rust buffer.
  8. If nothing was written, the call ends there and no confirmation is sent.
  9. For a Guided config with a `TargetNetworkObjectId` that resolves to a spawned
     object, each id is registered with `ProjectileGuidanceTracker`. A staggered
     shot passes the resolved target to `SpawnStaggered` instead.
  10. The sender gets `LinkProjectileIdsClientRpc` (it already runs the shot under
      temporary ids). Every other client gets `SpawnConfirmedClientRpc`, filtered
      by distance when culling is on.
- `RaycastFireServerRpc(request, clientHitPoint, clientDidHit, clientIsHeadshot,
  clientHitTargetId, clientIs3D)` passes the client's hit claim to
  `RaycastProjectileHandler.ServerHandleFire`, which decides what to accept.
- `RaycastPatternFireServerRpc(request)` resolves a pattern or spread and lets the
  server cast every ray itself. There is no client hit claim.
- `FirePhysicsProjectileServerRpc(origin, baseDirection, rotation, poolType, speed,
  damageMultiplier, ownerMidId, firedByNetObjId, configId = 0, patternId = 0,
  spreadCount = 1, spreadDeg = 0, patternIs3D = false, targetNetworkObjectId = 0)`
  spawns one server-owned physics `NetworkObject` per resolved direction through
  `MID_MasterProjectileSystem.SpawnPhysicsProjectile`, then sets owner context,
  initialises it and sets the guided target. The caller's client id is passed along
  so the real object is hidden from the firing client, which already has its own
  predicted visual. Every projectile of one call shares the guided target.

**Server to client RPCs:**
- `SpawnConfirmedClientRpc` (unreliable) spawns the shot in the receiver's Rust
  buffer under the real server ids, advanced by the elapsed server time (clamped to
  0.5 s). Unreliable on purpose: a dropped packet costs one client one shot, nothing
  else depends on it, and a reliable send for every shot in a large lobby would
  cost more than it saves. It skips the host and dedicated server.
- `LinkProjectileIdsClientRpc` (reliable, sender only) swaps the shooter's
  temporary ids for the real ones.
- `HitConfirmedClientRpc` (reliable, all clients) kills the projectile in the local
  buffer, calls `Prediction.OnHitConfirmed`, plays the impact through
  `ImpactHandler` and raises `OnHitConfirmedLocal`.
- `SendSnapshotClientRpc` (unreliable) carries the periodic position snapshots. It
  passes the current tick and the tick interval to
  `LocalProjectileManager.ReconcileSnapshots2D/3D`. Unreliable because each
  snapshot is replaced by the next one a few ticks later. It is the most frequent
  RPC in the system.

**Other members:**
- `ConfigureTransportForHighThroughput()` (static, called from
  `MID_MasterProjectileSystem` when it initialises) sets `UnityTransport`'s
  `MaxSendQueueSize` to 16 MiB and `MaxPacketQueueSize` to 2048. The second one
  bounds how many separate packets the transport holds per frame, which per-client
  culled snapshot dispatch can exceed with the default (the transport then logs
  "Receive queue is full, some packets could be dropped"). It returns quietly when
  `NetworkManager.Singleton` is null, so call it after the manager exists and
  before the host, server or client starts.
- `GetServerTick()` returns the current server tick.
- `SetLocalPlayerMidId(ulong)` stores an id that nothing in the class reads.
- `_logLevel` (default `Info`) sets the log level used for the bridge's messages.

**Limits and trust:**
- The server copies `OwnerMidId`, `FiredByNetworkObjectId`, `IsBotOwner`,
  `WeaponLevel` and `DamageMultiplier` from the request. It does not check that the
  caller owns that object or that the numbers are allowed, so a client can fire in
  another player's name.
- `Origin` is not compared with the sender's position.
- `ClientFireTick` is client supplied. An old tick earns the full 0.5 s of latency
  compensation.
- `Mathf.Clamp` does not replace NaN, so a NaN `Speed` passes the clamp.
- `FirePhysicsProjectileServerRpc` is checked much less than `FireServerRpc`:
  `speed` is not clamped to the config range, `spreadCount` (a byte) has no cap, and
  `configId` and `poolType` are not validated.
- None of the RPCs is rate limited.
- The shooter predicts a spread with the exact `SpreadDeg` and the server uses the
  byte-quantized one, so an outer pellet of a 30 degree spread can differ by about
  0.35 degrees. Snapshot reconciliation closes the gap.
- A client that culling leaves out never receives that shot, and an unreliable
  `SpawnConfirmedClientRpc` that is dropped is not resent.
- `FireServerRpc` uses `Authority` without a null check. `MID_MasterProjectileSystem`
  assigns it on start.
- `OnDestroy` is overridden without calling `base.OnDestroy()`.
- Raycast pattern fire and physics fire resolve with seed 0 and ignore start
  delays. Burst timing applies to the Rust sim and to local fire.

### `NetworkProjectileBase.cs`
**What it does:** A `NetworkTransform` base class for a projectile that exists as a
real `NetworkObject` (the physics path). It owns the server-side lifetime, the
replicated projectile data, the pooled visual and the impact RPCs. In this package
only `PhysicsProjectileBase` derives from it.

**Inspector:**
- `TimeToLive` (default 8 s) is how long the server lets the projectile live.
  `PhysicsProjectileBase` overwrites it with the config's `Lifetime`.
- `EolPositionDelta` (0.20) is declared with a tooltip about the visual lerping to
  the hit point, but nothing reads it.
- `_visualPoolType` is the `PoolableObjectType` of the visual. The default is
  `Projectile_Visual2D`.
- `_networkPoolType` is a `PoolableNetworkObjectType` that only the unused
  `ReturnToNGOPool` reads.

**Replicated data (`NetworkVariable`s, written by the server):** owner id, fired-by
`NetworkObjectId`, bullet velocity, visual-synch flag (default true), bot flag,
weapon level and a "server is actually owner" flag. Protected accessors expose
them: `OwnerMidId`, `FiredByNetObjId`, `BulletVelocity`, `VisualSynchEnabled`,
`IsBotOwned`, `WeaponLevel` and `ServerIsActuallyOwner`.

**How it works:**
- `OnNetworkSpawn` resets the state, spawns a pooled visual (unless
  `ShouldAutoSpawnVisual` is false), subscribes to the velocity variable and calls
  `base.OnNetworkSpawn()`. `OnNetworkDespawn` unsubscribes, unparents the visual and
  returns it to its pool.
- `InitialiseProjectile(ownerMidId, firedByNetworkObjectId, bulletVelocity,
  isBotOwned = false, weaponLevel = 0, serverIsActualOwner = false,
  enableVisualSynch = true)` is server only. It writes the variables, sets the
  end-of-life time to server time plus `TimeToLive`, parents the visual to the
  projectile, then calls `OnProjectileInitialised()` and `InitialiseVisual()`.
- When the velocity variable changes to a value above 0 on a client,
  `OnNetworkVelocityReceived()` runs. It never runs on the server.
- `Update` always calls `base.Update()` first, because `NetworkTransform` needs it
  on every peer (the owner side detects changes to send, the other peers advance
  the interpolator). Only the lifetime check after it is server only: when server
  time passes the end-of-life time, `DestroyProjectile()` runs.
- `DestroyProjectile()` is protected and server only. It marks the projectile as
  destroying (so it runs once), sends `NotifyCollisionClientRpc`, calls
  `OnImpactServer()`, returns the visual, sends `HasHitObjectClientRpc(position,
  tick)` and despawns the `NetworkObject`.
- `OnNetworkTransformStateUpdated` forwards each position change to the visual's
  `UpdatePositionInterpolator` while the visual-synch flag is on.
- Two protected RPCs, `SpawnImpactEffectClientRpc` and `SpawnKillEffectClientRpc`,
  call the matching client hooks.

**Virtual hooks:** `ShouldAutoSpawnVisual` (default true),
`OnNetworkVelocityReceived`, `OnProjectileInitialised`, `OnImpactServer`,
`OnCollisionNotifiedClient`, `OnSpawnImpactEffectClient`,
`OnSpawnKillEffectClient` and `InitialiseVisual`. `PhysicsProjectileBase` overrides
`ShouldAutoSpawnVisual` (false), `OnNetworkVelocityReceived`,
`OnProjectileInitialised`, `OnImpactServer`, `OnCollisionNotifiedClient` (empty),
`OnSpawnImpactEffectClient` and `OnSpawnKillEffectClient` (empty).

**Limits:**
- `EolPositionDelta`, `_networkPoolType` and `ReturnToNGOPool` have no effect.
- `FiredByNetObjId`, `IsBotOwned`, `VisualSynchEnabled` and `ServerIsActuallyOwner`
  are replicated but nothing in the package reads them. They are there for a game
  subclass.
- The pooled auto visual only works with a visual that implements
  `INetworkProjectileVisual`, and the package ships none.

### `ProjectileDirectionResolver.cs`
**What it does:** The one place that expands a fire event into a `SpawnPoint[]`
(origin, direction, speed and start delay per projectile). The firing client, the
server and every other client call it with the same inputs, so no per-projectile
direction ever has to cross the network. Namespace `MidManStudio.Projectiles.Network`.

**API:**
- `Resolve(patternId, origin, baseDirection, pelletCount, spreadDeg, baseSpeed,
  is3D, fireSeed = 0)` returns the projectiles of one shot.
- `NewFireSeed()` picks the seed for one shot. It returns 1 to 65535 and never 0.
  Only the firing side calls it. The seed then travels in `ProjectileFireRequest`
  and `SpawnConfirmation`, and every peer passes it to `Resolve`.

**How it works:**
- `Resolve` is a pure function of its arguments: no `Random`, no `Time`, no state.
  That is what lets every peer rebuild an identical shot.
- With `patternId != 0` and a pattern registered under that id, the pattern path
  runs. The pattern asset decides the projectile count (`pelletCount` is ignored).
  The seed is `pattern.MixSeed(fireSeed)`. `SampleDirections` gives one horizontal
  and one vertical angle per projectile. Speed is `baseSpeed` times
  `GetSpeedMultiplier(i, seed)`, and `StartDelay` is `GetStartDelay(i, count, seed)`.
- In 2D the horizontal angle rotates the aim about Z and the vertical angle is
  ignored. In 3D the horizontal angle rotates about the local up axis and the
  vertical angle (positive up) about the local right axis. The local frame uses
  world up, or world forward when the aim is within about 11 degrees of straight up
  or down.
- Without a pattern, `pelletCount > 1` gives a plain spread: `pelletCount`
  directions across a total arc of `spreadDeg`, the first at minus half and the
  last at plus half. 2D rotates about Z, 3D about the world Y axis. Speed is
  `baseSpeed` and there is no start delay.
- Otherwise the result is one projectile along `baseDirection` (a normalized copy,
  or forward when the aim is near zero).
- A `patternId` that is not registered on this peer falls through to the spread or
  single-shot path. The shot then differs from what a peer with the asset gets.
- `fireSeed` 0 means no seed: the pattern uses its own fixed seed, so every shot of
  that pattern looks the same.

**Limits:**
- The plain spread ignores `fireSeed` and never produces a start delay.
- The 3D plain spread yaws about the world Y axis only. Aimed straight up or down
  it does not fan out.
- Raycast pattern fire and physics fire resolve without a seed (the physics RPC
  carries none), so they use the pattern's fixed layout.

### `WireCompression.cs`
**What it does:** Encode and decode pairs used inside the `NetworkSerialize`
methods of `ProjectileFireRequest` and `SpawnConfirmation`. Code outside those
methods still works with plain `Vector3` and `float` values. Only the bytes on the
wire shrink, and what a receiver reads back is the quantized value.

**API:**
- `EncodeDirection(Vector3, out short, out short)` and `DecodeDirection(short,
  short)`: octahedral encoding of a unit vector, 12 bytes down to 4. A zero-length
  input encodes as forward (+Z), and a decode always returns a normalized vector.
- `EncodePosition(Vector3, out ushort x3)` and `DecodePosition(ushort x3)`: IEEE
  half precision per axis, 12 bytes down to 6.
- `EncodeDegrees0to360(float)` and `DecodeDegrees0to360(byte)`: an angle wrapped
  into 0 to 360 and stored in one byte, 4 bytes down to 1.

**Precision:**
- Direction: 16 bits per axis. A 400,000 direction sweep of a port of the code
  gave a worst case of about 0.004 degrees.
- Position: the spacing between half values is 2^(e-10) for values in
  [2^e, 2^(e+1)), so the largest rounding error is about 0.001 below 4, 0.03 from
  64 to 128, 0.0625 from 128 to 256, 0.125 from 256 to 512 and 0.25 from 512 to
  1024 world units. The reconciliation skip band is 0.08, so keep play coordinates
  within about 256 units of the origin. Values above 65504 become infinity.
- Angle: one step is 360/255, about 1.41 degrees, so the error is up to 0.7 degrees.
  Only the plain spread uses it (`SpreadDeg`).

**Notes:**
- The snapshot structs in `ServerProjectileAuthority.cs` apply the same half
  conversion (`Mathf.FloatToHalf`) directly and do not call this class.

### `NetworkedDimensionPlayer.cs`
**What it does:** Player rig: movement, dash, mouse look, dimension (2D/3D)
switching and the Rigidbody constraints that go with it, and the owner/remote
tint. Auto-creates its head pivot and shot point transforms if left unassigned.
`Set3DShotpoint()` lets `WeaponController` redirect the 3D shot point at the
currently equipped weapon's own muzzle; passing null restores this rig's own
default point rather than leaving a previous weapon's muzzle active.

**Decisions:**
- Everything fire- and weapon-specific used to live on this class directly.
  It now lives on the sibling `WeaponController`, and everything health- and
  death-specific lives on the sibling `PlayerHealth`. This class exposes a
  small public surface those two components need: `MeshRenderers`,
  `CurrentDimension`, `ControlEnabled`, `SetControlAndVisibilityEnabled()`,
  `RefreshTint()`, `Use3DConvention()`, `ResolveFireDir()`, `ResolveShotPoint()`,
  and `ReportWeaponUses3DConvention()`.
- `Use3DConvention()` covers the ThreeD dimension and, through
  `ReportWeaponUses3DConvention()`, a 3D-convention shoot mode selected by
  `WeaponController` even while still in the TwoD dimension. This keeps the
  rig helpers (`ResolveFireDir`/`ResolveShotPoint`) and the movement/mouse-look
  gating behaving exactly as they did back when shoot mode lived here.
- `ControlEnabled` gates this player's own movement, dash, mouse look, and
  dimension switching, and `WeaponController` checks the same flag before
  handling fire input. `PlayerHealth` is the only thing that flips it, on
  death and respawn.

### `NetworkTurretTarget.cs`
**What it does:** A networked turret-style target that checks
`PlayerHealth.IsAlive` on whatever it is tracking before acting on it.

### `NetworkPlayerObjectObserverProvider.cs`
**What it does:** `IProjectileObserverProvider` implementation used for
network-visibility/culling decisions around player NetworkObjects.

### `DelayedSpawnQueue.cs`
**What it does:** Managed queue of projectiles that start after the shot. Entries carry the spawn point, the projectile id, the config id, the base speed, the seconds already flown, the spawn path (`DelayedSpawnKind`) and an owner-specific context object. `Enqueue(entry)` adds one, `Advance(dt, spawn)` moves a clock by the fixed step and passes due entries to the owner in queue order, `Relink(oldId, newId)` swaps a temp id for a real one, `Remove(projId)` drops an entry, `Clear()` empties it, and the static `AnyDelayed(points, count)` tells a spawn path whether it needs the queue at all.

**How it works:**
- The clock is the sum of fixed steps, the same time base the sim runs on, so a delay of 0.08 s is four 0.02 s steps whatever the frame rate.
- A due entry spawns at the start of the next fixed step, before the sim tick, so it moves in the step it appears in.
- Entries are removed by compacting the list in one pass, which keeps queue order.

**Limits:**
- A queued projectile spawns even if its shooter dies before it leaves. A burst is over in well under a second at the highest settings the inspector allows for normal weapons, so this was left alone.
- A queued projectile that was linked to its server id after the server confirmation arrived is relinked in `LinkNetworkProjectileBatch`. If the confirmation never arrives, the projectile spawns with its temp id and is removed by lifetime like any other.

### `LocalProjectileManager.cs`
**What it does:** The local backend: a `Singleton<LocalProjectileManager>` that simulates and draws RustSim projectiles on this machine, in three roles:
1. Offline play: it is the whole backend, including target registration and hit events.
2. The firing client of a networked session: it holds the predicted copy of its own shots (temporary ids), which are linked to the server's real ids when the spawn confirmation arrives.
3. Every other client: it spawns the projectiles the server announced (`SpawnNetworkBatch2D/3D`) and corrects them from the server's snapshots.

Public surface:
- Events: `OnHit` (`LocalHitPayload`, raised for local collisions and for raycast hits through `FireHitEvent`) and `OnProjectileDied`.
- Spawning: `Spawn2D/3D(spawnPoints, count, configId, ownerLocalId, damageMultiplier, guidedTarget)`, `SpawnFiringClientBatch2D/3D` (predicted copy with temp ids), `SpawnNetworkBatch2D/3D(conf, elapsedSinceSpawn)`, `LinkNetworkProjectileBatch(realBaseId, count)`, `KillNetworkProjectile(projId)`.
- Reconciliation: `ReconcileSnapshots2D/3D(snapshots, count, currentServerTick, tickInterval)`.
- Targets and shapes: `RegisterTarget`/`UpdateTarget`/`DeactivateTarget`/`RemoveTarget` (the older `LocalDamageTarget` API), `RegisterTarget2D/3D`, `DeactivateTarget2D/3D`, `RegisterShape2D/3D`, `DeactivateShape2D/3D`, `ClearAllTargets()`.
- Guided helpers: `SetAcceleration2D/3D`, `TryGetPosition2D/3D`. Counts: `TargetCount2D/3D`, `ShapeCount2D/3D`, `ActiveCount2D/3D`.

Inspector fields: `_maxProjectiles2D` (2048), `_maxProjectiles3D` (512), `_maxTargets` (128), `_maxShapes` (64), `_maxHitsPerTick` (256), `_cellSize` (4), `_renderer2D`, `_renderer3D`, `_trailPool`, `_logLevel` (`Info`).

**How it works:** `FixedUpdate` advances the `DelayedSpawnQueue`, ticks the native sim for each buffer, runs the collision pass only when targets or shapes are registered (`_targetCount > 0 || _shapeCount > 0`; in a networked session they are not, so only the server decides hits), syncs trails and draws through the renderers. Burst timing and its decisions are described under `ServerProjectileAuthority.cs`.

Id linking. The firing client spawns with temporary ids from a counter that starts at `0xFFFE0000` and remembers each batch in a queue. When `SpawnConfirmedClientRpc` arrives, `LinkNetworkProjectileBatch` swaps the temporary ids for the real ones in place, relinks entries still in the delay queue, re-keys the trail slots (`TrailObjectPool.RelinkProjectileId`, otherwise the next sync would acquire a second slot and orphan the first) and adds the real ids to `_locallyOwnedProjIds`. `KillNetworkProjectile` removes them again.

Reconciliation. `ReconcileSnapshots2D/3D` extrapolate each snapshot forward to the current time with the local velocity (`expected = snapshot + V * staleTime`, minus `0.5 * Ay * staleTime^2` on Y in 2D), then compare with the local position. An error under `kSkip` (0.08) is ignored, an error over `kHardSnap` (4) snaps to the expected position, and anything between is corrected by `kLerp` (0.65, so the error is mostly gone in about three snapshots). Ids in `_locallyOwnedProjIds` are skipped completely: the server sends one shared array to everyone, and nudging a client's own, already correct shots toward a separately computed extrapolation made them wiggle. The late-spawn catch-up is shared by the batch and queue paths in `ApplyCatchUp2D` and `ApplyCatchUp3D`.

**Limits:**
- Reconciliation finds each snapshot's projectile with a linear scan of the buffer, so a tick costs snapshots times live projectiles.
- Guided movement is reconciled as a straight line between snapshots, so a curving guided shot is approximated.
- Positions arrive as half-precision floats (see `ServerProjectileAuthority.cs`), so beyond about 256 units from the origin the snapshot error alone can exceed `kSkip`.
- `OnHit` also fires for raycast hits through `FireHitEvent`, and `MID_MasterProjectileSystem` relays it as `OnRustSimHit`.
- `OnDestroy` of this manager and of the authority both free their own buffers; they share the static native state (`clear_movement_params`).

### `MID_MasterProjectileSystem.cs`
**What it does:** `MID_MasterProjectileSystem` is the single scene-level entry point of the projectile system, a `Singleton<MID_MasterProjectileSystem>` from the utilities package. Game code calls it to fire projectiles, register collision targets and shapes, steer guided shots and read live positions. It holds no simulation state. Each call is routed to the backend that fits the session:

| Session | Backend |
|---|---|
| Offline (no running `NetworkManager`, or `_forceOfflineMode`) | `LocalProjectileManager` |
| Server or host | `ServerProjectileAuthority` |
| Pure client | A predicted copy in `LocalProjectileManager`, plus `MID_ProjectileNetworkBridge.FireServerRpc` to the server |

The file also declares the struct `RustSimHitPayload`, the payload of the `OnRustSimHit` event.

Inspector fields:

| Field | Meaning |
|---|---|
| `_registry` | The `ProjectileRegistry` used to resolve a `configId` to a `ProjectileConfigSO`. |
| `_authority` | The `ServerProjectileAuthority`. |
| `_localManager` | The `LocalProjectileManager`. If left empty, `Initialise` looks for one with `FindAnyObjectByType`. |
| `_networkBridge` | The `MID_ProjectileNetworkBridge`. |
| `_predictionManager` | The `ClientPredictionManager`, handed to the bridge. |
| `_raycastHandler` | The `RaycastProjectileHandler`, handed to the bridge and called for raycast fire. |
| `_renderer2D`, `_renderer3D` | Declared and serialized but never read in this class. |
| `_trailPool` | The `TrailObjectPool`, handed to the authority. |
| `_impactHandler` | The `ProjectileImpactHandler`, handed to the bridge. |
| `_networkObjectPool` | The `MID_NetworkObjectPool` that supplies physics projectile instances. |
| `_forceOfflineMode` | When true, `IsNetworked` is false even if a `NetworkManager` is running. |
| `_logLevel` | `MID_Logger` level, default `Info`. |

Properties:
- `IsNetworked`: not `_forceOfflineMode`, a `NetworkManager.Singleton` exists, and it is a server or a client (that is, it is listening).
- `IsServer`: a `NetworkManager.Singleton` exists and `IsServer` is true (a host counts).
- `IsHostMode`: server and client at once.
- `CanAcceptTargets`: true when a `RegisterTarget` or `RegisterShape` call made right now would land in a backend. That is a server with an authority, or an offline session with a local manager. A pure client or a missing backend would silently drop the call, so `RustSimTargetRegistrar` and `RustSimCustomShapeAuthoring` stay unregistered while this is false.
- `GetAuthority()`, `GetBridge()`, `GetRaycastHandler()`, `GetPredictionManager()` return the wired references. `GetBridgeTick()` returns the bridge's server tick, or 0 without a bridge.
- `IsReady` (static): the instance exists and has finished `Initialise`.

**How it works:**

Initialisation (`Awake`, then `Initialise`, run once):
1. `ProjectileLib.ValidateStructSizes()`. It throws `InvalidOperationException` both for a struct size mismatch and when the native library cannot be loaded. `Initialise` catches that, logs the error, sets `enabled = false` and returns without setting `_initialised`. Every public method that checks `_initialised` (`Fire`, the raycast calls) then does nothing.
2. `BatchSpawnHelper.Initialise()` and `MID_ProjectileNetworkBridge.ConfigureTransportForHighThroughput()`.
3. Finds the local manager if the field is empty, and subscribes `RelayLocalRustSimHit` to its `OnHit` (unsubscribing first, so a repeated call never subscribes twice).
4. Gives the authority its `TrailPool` and `NetworkBridge`.
5. Gives the bridge its `Authority`, `Prediction`, `RaycastHandler` and `ImpactHandler`, and subscribes `RelayNetworkedRustSimHit` to the bridge's `OnHitConfirmedLocal`.
6. Sets `_initialised` and raises the static `OnSystemReady`.

Readiness. `OnSystemReady` is static so a scene object can subscribe before the singleton exists. A subscriber that attaches after `Initialise` would never hear it, so a consumer checks first: `if (IsReady) { RegisterNow(); } else { OnSystemReady += Handler; }`. `RustSimTargetRegistrar` and `RustSimCustomShapeAuthoring` do exactly this, and also keep a per-tick retry in `Update`/`FixedUpdate` as a second safety net.

Identity. `SetLocalPlayerMidId(ulong)` stores the id on `ClientPredictionManager` and on the bridge. Neither class reads it back, so the call changes no routing today.

Firing a RustSim or local projectile, `Fire(configId, spawnPoints, count, context, patternId, spreadDeg, baseDirection, patternIs3D, guidedTarget, fireSeed)`:
- Returns if not initialised. Looks the config up in the registry and warns and returns if it is not registered.
- Calls `ProjectileTypeRouter.Route(cfg, context)`. The router reads `context.IsNetworked`, which the caller sets. It does not read this class's own `IsNetworked`.
- `LocalOnly` goes to `FireLocal`, which calls `LocalProjectileManager.Spawn2D` or `Spawn3D` (by `cfg.Is3D`) with the owner id narrowed to `uint`, the damage multiplier and the guided target.
- `RustSim` goes to `FireNetworkedSim`.
- `Raycast` and `PhysicsObject` only log a warning that says to use `RegisterRaycastFire` or `SpawnPhysicsProjectile`. Nothing is fired.

`FireNetworkedSim` builds a `ProjectileFireRequest` and sends it:
- Speed: `spawnPoints[0].Speed` when it is above 0, otherwise `cfg.ResolveSpeed()`.
- Direction: the raw aim direction in `baseDirection` (normalised), not `spawnPoints[0].Direction`. For a pattern or spread of more than one pellet, pellet 0 already carries its own angle within the arc, and every recipient re-applies the pattern around the direction it receives, so sending the offset direction would skew the whole shot. `spawnPoints[0].Direction` is only the fallback when `baseDirection` is left at its default, which is correct for a single shot only.
- Only `patternId`, `spreadDeg`, `fireSeed` and the base direction travel. Every recipient regenerates the same pellet set with `ProjectileDirectionResolver.Resolve`. `fireSeed` must be the value that was passed to `Resolve` when `spawnPoints` was built (0 means the fixed layout from the pattern asset). `spawnPoints` itself is used only for the shooter's own predicted visual.
- `ProjectileCount` is `Min(count, 255)`, `ClientFireTick` is the bridge's server tick, and the owner, bot flag, weapon level and damage multiplier come from the `WeaponFireContext`.
- Guided target: a `Transform` means nothing on the wire, so it is sent as the `NetworkObjectId` of the nearest `NetworkObject` on it or above it (`GetComponentInParent`). 0 means none.
- A pure client (`!IsServer`) first spawns its own copy with `LocalProjectileManager.SpawnFiringClientBatch2D` or `3D`, using the resolved speed. The temporary ids are linked to the server's real ids when `SpawnConfirmedClientRpc` arrives (`LocalProjectileManager.LinkNetworkProjectileBatch`). The host skips this copy because it renders from the authority's own buffer.
- Last, `_networkBridge.FireServerRpc(request)` is called for every networked caller, host included (on a host the call runs locally).

Physics projectiles, `SpawnPhysicsProjectile(type, position, rotation, configId = 0, firingClientId = ulong.MaxValue)`. Server only; returns null on a client, without a pool, or when the pool gives nothing:
1. Takes an instance from `_networkObjectPool`.
2. `Spawn()`. The instance is always server-owned.
3. `PhysicsProjectileBase.SetVisualConfigId(configId)`. 0 keeps the prefab's inspector default.
4. If `firingClientId` is set and the firer is not the host, `NetworkHide(firingClientId)`. The firing client already has its own prediction ghost, which lives out its own `MaxLifetime` and glides to the confirmed hit through `HitConfirmedClientRpc` and `ClientPredictionManager.OnHitConfirmed`. Showing it the real object as well would put two copies of one shot on its screen. A host firer is exempt because it is expected to have no ghost of its own.
5. Subscribes `RelayPhysicsHit` to the instance's `OnHitServerConfirmed`, unsubscribing first because the instance may be a reused pooled one.

The method does not call `InitialiseProjectile` and takes no guided target. The caller does that afterwards, in this order: `SetOwnerContext`, then `InitialiseProjectile`, then `SetGuidedTarget`. `InitialiseProjectile` resets the guided-target state on every fresh launch, so a target set before it is wiped. `MID_ProjectileNetworkBridge.FirePhysicsProjectileServerRpc` follows exactly this order and is the reference call site in the package. Choosing the target is left to the caller. `ReturnPhysicsProjectile(netObj, type)` hands an instance back to the pool.

Raycast fire:
- `RegisterRaycastFire(result, configId, context)`. Not networked: `RaycastProjectileHandler.OfflineHandleFire`. Server: `ServerHandleFire(result, context, configId)`. Remote client: `RaycastFireServerRpc` carrying the request plus the client's own ray result (hit point, hit flag, headshot flag, hit target `NetworkObjectId`, 3D flag), then `OfflineHandleFire` with a damage multiplier of 1 so the shooter sees their own ray at once. The RPC send is wrapped in its own `try`/`catch` so an exception in the send (for example from `ProjectileFireRequest.NetworkSerialize`) cannot skip the local visual. The catch only logs, with the `[RAYDIAG]` prefix.
- `RegisterRaycastPatternFire(origin, baseDirection, is3D, configId, patternId, pelletCount, spreadDeg, context)` is the networked pattern version. Server: `ServerHandleFirePattern`. Remote client: `RaycastPatternFireServerRpc`, then `ClientPredictPatternLocal`. The prediction call is needed because `ServerHandleFirePattern`'s ClientRpc leaves the firing client out of its target list (`BuildTargetList(senderClientId)`), on the assumption that a local prediction exists.

Targets and shapes. `RegisterTarget2D/3D`, `DeactivateTarget2D/3D`, `RegisterShape2D/3D`, `DeactivateShape2D/3D` and `ClearAllTargets` call the authority when `IsServer`, and call the local manager only when `!IsNetworked`. In a networked session the local manager's target buffers therefore stay empty, and its `FixedUpdate` skips the hit pass when both the target count and the shape count are 0, so only the server decides hits. Each register method has a `GameObject` and a `Component` overload that reads the Unity layer off the object. Prefer them: the `int unityLayer = 0` overload silently puts every target on layer 0 when the argument is forgotten.

State. `SaveState2D(byte[])` and `RestoreState2D(byte[], int)` go to the authority only and return 0 without one (there is no 3D version and no local-manager version).

Guided helpers. `SetHomingDirection2D/3D(projId, worldDir)` call `SetAcceleration2D/3D` on the authority when `IsServer` and on the local manager when `!IsNetworked`. The two are separate buffers, and a non-networked game has `IsServer` false, so one combined condition would write into the wrong, empty buffer. `TryGetProjectilePosition2D/3D` use the same routing and are what `ProjectileGuidanceTracker` calls every frame.

Events:
- `OnPhysicsHit` (`ProjectileHitPayload`): every spawned physics projectile's `OnHitServerConfirmed`, re-raised on the server. The per-instance event cannot be subscribed to once at session start, so this relay is the session-wide one, like `RustSimAdapter.OnProjectileHit` and `RaycastProjectileHandler.OnServerHitConfirmed`.
- `OnRustSimHit` (`RustSimHitPayload`): relays `LocalProjectileManager.OnHit` (offline) and `MID_ProjectileNetworkBridge.OnHitConfirmedLocal` (networked clients, and the host for its own hits). Only one of `TargetNetworkObjectId` and `RawTargetId` is filled: `RawTargetId` offline, `TargetNetworkObjectId` networked, because the networked `HitConfirmation` carries no raw RustSim target id. It deliberately does not relay the server's `ServerProjectileAuthority.Adapter.OnProjectileHit`. That one is raised only on the server, is the event the bridge itself listens to in order to send `HitConfirmedClientRpc`, and on a host would fire next to `OnRustSimHit` for the same hit. Use `OnRustSimHit` for reactions to something that visibly happened (VFX, sound, UI) and `Adapter.OnProjectileHit` for authoritative damage and game state.
- `OnSystemReady` (static): see Readiness.

Debug. The `Log System Status` context menu prints initialisation, mode, active counts, registry size, `CanAcceptTargets` and the target and shape counts of both backends.

**Limits:**
- `OnDestroy` is overridden without calling `base.OnDestroy()`. The `Singleton` base cleanup (clearing the static instance reference, resetting its persistence flag, unsubscribing its scene-loaded handler) never runs.
- `OnDestroy` also calls `ProjectileLib.clear_movement_params()` without checking `ProjectileLib.IsAvailable`. On a device where the native library failed to load, the system disables itself in `Initialise`, and destroying it still calls the native function, which throws.
- `_renderer2D` and `_renderer3D` are never read here.
- The relay handlers (`LocalProjectileManager.OnHit`, the bridge's `OnHitConfirmedLocal`, each physics instance's `OnHitServerConfirmed`) are not unsubscribed in `OnDestroy`.
- `FireNetworkedSim` sends `Min(count, 255)` projectiles, while the server rejects a request whose count is 0 or above `MaxProjectileCountSanity` (128). A fire of 129 to 255 pellets is spawned on the shooter's screen and dropped by the server.
- The predicted copy on the firing client uses the resolved speed as it is. The server clamps the speed to the config's `MinSpeed` and `MaxSpeed`, so a spawn point speed outside that range makes the shooter's copy and the server's projectile differ.
- `Fire` assumes `spawnPoints` holds at least `count` entries. It reads `spawnPoints[0]` whenever `count > 0`.
- `Fire` returns silently without a bridge on the RustSim path (nothing is spawned, not even the predicted copy), and without a local manager on the `LocalOnly` path (a warning is logged).
- The owner id is narrowed to `uint` on the local paths (`FireLocal`, `RegisterRaycastFire`, `ClientPredictPatternLocal`), while the networked request keeps the full `ulong`.
- `RegisterRaycastPatternFire` is for networked sessions. Called offline it takes the client branch and the RPC send throws, which is caught and logged with the `[RAYDIAG]` prefix before the local prediction runs. The two `[RAYDIAG]` messages use `Debug.LogError`, not `MID_Logger`, so a `MID_Logger` level of `None` does not silence them.
- `SpawnPhysicsProjectile` calls `NetworkHide` after `Spawn()`, so the firing client is an observer at the moment of the spawn. Whether NGO has already queued the spawn message for it by then, and what `NetworkHide` does for a client id that is not an observer, were not checked against the NGO source.
- `SpawnPhysicsProjectile` on a prefab without a `PhysicsProjectileBase` still spawns and returns the object, only without the config id and the hit relay.
- `SetLocalPlayerMidId` has no effect today (see Identity).
- `SaveState2D` and `RestoreState2D` have no offline path and no 3D version.

### `PhysicsProjectile2D.cs`
**What it does:** The Rigidbody2D implementation of `PhysicsProjectileBase`. The prefab needs this script, a `Rigidbody2D` (required), a `CapsuleCollider2D` or a `CircleCollider2D`, a `NetworkObject`, and a 2D poolable object type.

**Inspector:** `_drag` (default 0) and `_gravityScale` (default 0). The gravity scale is only used when no config resolves for `VisualConfigId`.

**How it works:**
- `OnPhysicsSetup` caches the `Rigidbody2D` and logs an error when it is missing.
- `OnLaunch` takes the gravity scale from `cfg.GravityScale` when a config resolves, otherwise from `_gravityScale`. It sets gravity to 0 for the `Wave`, `Circular` and `Teleport` movement types, which drive the body directly and do not combine with gravity (the native sim does not combine them either). `Guided` keeps gravity, so a homing shot still arcs. It then sets drag, makes the body non-kinematic, sets the velocity to `transform.right * bulletVelocity` (the 2D convention: shots fly along `transform.right`) and returns `transform.right`.
- `StopPhysics` zeroes the velocity and makes the body kinematic.
- `ApplyMovementVelocity` and `GetCurrentVelocity` read and write `Rigidbody2D.velocity`. `TeleportBody` sets `Rigidbody2D.position`.
- `ApplyColliderSize(sizeX, sizeY)` finds the collider once. A `CapsuleCollider2D` gets `size` (sizeX, sizeY) for a horizontal capsule and (sizeY, sizeX) for a vertical one, so the long axis always carries the travel-direction length and the prefab's `.direction` is left alone. A `CircleCollider2D` gets `radius = sizeY * 0.5`. With neither, it logs one warning.
- `OnCollisionEnter2D` (first contact point, or the projectile position when there is none) and `OnTriggerEnter2D` (the projectile position) call `HandleHit2D`.

**Limits:**
- A circle collider cannot be elongated, so its radius follows the cross section (`sizeY`) and the length (`sizeX`) is ignored. Use a capsule for an exact fit.
- The tooltip that says "Overridden by config.GravityScale" sits on `_drag` instead of `_gravityScale`. It is a string literal and was left unchanged.

### `PhysicsProjectile3D.cs`
**What it does:** The Rigidbody implementation of `PhysicsProjectileBase`. The prefab needs this script, a `Rigidbody` and a `SphereCollider` (both required by the class), a `NetworkObject` and a 3D poolable object type.

**Inspector:** `_drag` (default 0), `_angularDrag` (0.05) and `_useGravity` (false, the fallback when no config resolves).

**How it works:**
- `OnLaunch` decides gravity like this: a config with `GravityScale > 0` turns Unity gravity on whatever the inspector says. A config with `GravityScale == 0` turns it off unless `_useGravity` is on. `Wave`, `Circular` and `Teleport` always turn it off, and `Guided` keeps it. With no config the inspector toggle decides. It then sets drag and angular drag, makes the body non-kinematic, sets the velocity to `transform.forward * bulletVelocity` and returns `transform.forward`. The spawner is expected to set the rotation with `Quaternion.LookRotation(direction)`.
- `StopPhysics`, `ApplyMovementVelocity`, `GetCurrentVelocity` and `TeleportBody` work on the `Rigidbody` the same way as in 2D (`TeleportBody` sets `Rigidbody.position`).
- `ApplyColliderSize(sizeX, sizeY)` sets the sphere radius to `sizeY * 0.5`.
- `OnCollisionEnter` (first contact point) and `OnTriggerEnter` (the projectile position) call `HandleHit3D`.

**Limits:**
- In 3D the config's `GravityScale` only switches gravity on or off. It does not scale it, unlike the 2D version, where it is the Rigidbody2D gravity scale.
- A config with `GravityScale == 0` does not turn gravity off when the prefab's `_useGravity` is on.
- The sphere collider follows the cross section, so the length (`sizeX`) is ignored.

### `PhysicsProjectileBase.cs`
**What it does:** `PhysicsProjectileBase` is the abstract base of the Rigidbody projectiles `PhysicsProjectile2D` and `PhysicsProjectile3D`. It derives from `NetworkProjectileBase` (a `NetworkTransform` that carries `TimeToLive`, the owner and velocity NetworkVariables and the server lifetime watchdog) and requires a `NetworkObject`. Instances come from the network object pool through `MID_MasterProjectileSystem.SpawnPhysicsProjectile`. Unity physics moves them on the server only, and `NetworkTransform` replicates the pose to clients. The base class owns everything that does not depend on 2D or 3D: which `ProjectileConfigSO` a shot represents, the config-driven speed, lifetime, collider size and hit layers, the per-tick movement types, hit and damage handling, and the cosmetic pool visual. The subclasses only supply the Rigidbody and collider details through abstract hooks. The class sets `ShouldAutoSpawnVisual` to `false` because it manages its own visual.

Public surface:
- `SetVisualConfigId(ushort configId)`: tells the instance which `ProjectileConfigSO` (by `ProjectileRegistry` id) it represents. On the server it writes the NetworkVariable `n_VisualConfigId` (`ushort`, default 0, read by everyone, written by the server), which replicates to every client. On a non-server instance it does not write, but still re-initialises the local visual and the config-dependent state. `VisualConfigId` (protected) reads the NetworkVariable.
- `SetOwnerContext(ulong ownerMidId, ulong firedByNetworkObjectId, bool isBotOwner, byte weaponLevel, float damageMultiplier = 1f)`: attribution data copied into every `ProjectileHitPayload` this shot produces.
- `InitialiseProjectile(ulong ownerMidId, ulong firedByNetworkObjectId, float bulletVelocity, bool isBotOwned = false, byte weaponLevel = 0, bool serverIsActualOwner = false, bool enableVisualSynch = true)`: the launch call (server only, see `NetworkProjectileBase`). The override replaces `bulletVelocity` with the config speed in the cases listed under Speed.
- `SetGuidedTarget(Transform)`, `SetGuidedTarget(Vector3)`, `ClearGuidedTarget()`: steering target for configs whose `MovementType` is `Guided`.
- `event Action<ProjectileHitPayload> OnHitServerConfirmed`: raised on the server for every confirmed hit with the computed damage. `MID_MasterProjectileSystem` subscribes to it in `SpawnPhysicsProjectile` and republishes it as `OnPhysicsHit`.
- Protected: `VisualConfigId`, `HasHit`, `HandleHit2D(GameObject, Vector3)` and `HandleHit3D(GameObject, Vector3)` (the subclasses call them from their collision and trigger callbacks), `virtual bool CheckHeadshotPhysics(GameObject, Vector3)`, `virtual void FixedUpdate()`, `virtual void ApplyCustomCurve()`.
- Abstract hooks the subclass implements: `OnPhysicsSetup()`, `Vector3 OnLaunch(float bulletVelocity)` (applies the launch velocity and returns the launch direction), `StopPhysics()`, `bool Is2D`, `ApplyColliderSize(float sizeX, float sizeY)`, `ApplyMovementVelocity(Vector3)`, `Vector3 GetCurrentVelocity()` (zero when there is no Rigidbody), `TeleportBody(Vector3)` (sets the Rigidbody's own `position`).

Call order for one shot, as `MID_ProjectileNetworkBridge.FirePhysicsProjectileServerRpc` does it: `SpawnPhysicsProjectile(type, position, rotation, configId, firingClientId)` (gets the object from the pool, `Spawn()`, then `SetVisualConfigId(configId)`, then `NetworkHide` from the firing client unless that client is the host), then `SetOwnerContext`, then `InitialiseProjectile`, then `SetGuidedTarget` when the shot is guided. `SetGuidedTarget` has to come after `InitialiseProjectile`, because the launch clears any guided target.

Inspector fields:

| Field | Default | Meaning |
|---|---|---|
| `_baseDamage` | 30 | Damage when no config resolves for `VisualConfigId`. |
| `_explosionRadius` | 0 (min 0) | World-unit blast radius. Above 0.01 the shot explodes on first contact. Not read from the config. |
| `_damageLayerMask` | -1 | Layers the explosion overlap query uses. Overwritten from `cfg.HitLayers` whenever a config resolves. |
| `n_VisualConfigId` | 0 | The synced config id (see `SetVisualConfigId`). |
| `_configReferenceForEditorOnly` | none | A `ProjectileConfigSO` reference kept only so the id can be read at a glance in the inspector. No runtime code reads it. |
| `_visual2DPoolType`, `_visual3DPoolType` | `Projectile_Visual2D`, `Projectile_Visual3D` | Pool types for the cosmetic visual. |
| `_allowCallerVelocityOverride` | false | See Speed. |
| `_logLevel` | `Info` | `MID_Logger` level. |

**How it works:**

Lifecycle:
- `OnNetworkSpawn`: calls the base, clears `HasHit`, subscribes to `n_VisualConfigId.OnValueChanged`, calls `OnPhysicsSetup()`, applies the config-dependent state for the currently resolved config, spawns the pool visual, and starts two coroutines: `RetrySpawnVisual` (only when the visual could not be created) and `RevalidateConfigAfterSpawn`. Nothing in this method is specific to the owner client, because `SpawnPhysicsProjectile` hides the object from the firing client (unless that client is the host).
- `OnNetworkDespawn`: unsubscribes, stops the three coroutines (retry, revalidate, collider growth), returns the pool visual, clears `_launched` and calls the base.
- `OnProjectileInitialised` (called by `InitialiseProjectile` on the server): clears `HasHit`, sets `_launched`, stores the spawn position, resets the piercing state, spawns the visual if it is missing, calls `OnLaunch(BulletVelocity)` for the launch direction, calls `SetupMovementType(direction)`, and re-initialises the visual with that direction. The visual speed is `BulletVelocity`, or 10 when `BulletVelocity` is not above 0.
- `Update` (server only, after `base.Update()`): when launched and not yet hit, and the resolved config has `MaxRange > 0`, the shot ends once the straight-line distance from the spawn position reaches `MaxRange`: `HasHit = true`, `StopPhysics()`, `DestroyProjectile()`.
- Every server-side `DestroyProjectile()` (hit, `MaxRange`, `TimeToLive`) calls `OnImpactServer()`, which sends `SpawnImpactEffectClientRpc`. On each client `OnSpawnImpactEffectClient` plays `ProjectileImpactHandler.PlayImpact(position, VisualConfigId)` when the handler exists and then returns the pool visual. `OnCollisionNotifiedClient` and `OnSpawnKillEffectClient` do nothing.
- The `n_VisualConfigId` change callback (`HandleVisualConfigChanged`) refreshes the config-dependent state and, when a pool visual exists, calls `InitializeClientVisual` with the new id, the default launch direction and the current speed.
- `OnNetworkVelocityReceived` (clients, when the `BulletVelocity` NetworkVariable arrives): re-initialises the visual with the default launch direction (`transform.right` for 2D, `transform.forward` for 3D) and the received speed.

Config-dependent state. `RefreshConfigDependentState(cfg)` runs from `OnNetworkSpawn`, `SetVisualConfigId`, the `n_VisualConfigId` change callback and `RevalidateConfigAfterSpawn`. It applies three things, each only when `cfg` is not null:
- Lifetime: `TimeToLive = cfg.Lifetime`. `NetworkProjectileBase.InitialiseProjectile` reads `TimeToLive` once to compute the end of life, so the config id has to be set before `InitialiseProjectile`.
- Hit layers: `_damageLayerMask = cfg.HitLayers`.
- Collider size (`ApplyConfigScale`): the target is `FullSizeX` by `FullSizeY`, each floored at 0.001. Without `UseScaleGrowth`, or on a non-server instance, the subclass `ApplyColliderSize` gets the target at once. With `UseScaleGrowth` on the server, `GrowColliderRoutine` starts at target times `SpawnScaleFraction` and every frame (`Time.deltaTime`) moves each axis by `(target - current) * GrowthSpeed * dt` until both axes are within 0.001, then applies the exact target. That is the formula of `tick_scale` in `rust_lib/projectile_core/src/simulation.rs`. Any growth coroutine that is still running is stopped before a new size is applied.

`RevalidateConfigAfterSpawn` records the config id and the resolved config at spawn, waits two frames, and compares. When either differs it logs a warning that names both ids and configs, re-applies the config-dependent state and re-initialises the visual. It is a safety net for a config that is not settled at spawn time.

Speed. `InitialiseProjectile` resolves the config first. With `_allowCallerVelocityOverride` off (default) and a resolved config, the caller's `bulletVelocity` is replaced by `cfg.ResolveSpeed()` (the fixed `MinSpeed`/`MaxSpeed` value, or a random value between them). With the flag on, the caller's value is kept and `cfg.ResolveSpeed()` is used only when the caller passes 0 or less. When no config resolves, the caller's value is used.

Movement types. `SetupMovementType(direction)` runs at every launch. It reads `cfg.MovementType` (`Straight` when there is no config), zeroes the teleport timer, and clears the guided target so a pooled instance never inherits one. For `Wave`, `Circular`, `Teleport` and `CustomCurve` it stores `_movementLaunchVelocity` (the normalised direction times `BulletVelocity`) and the server time of launch. `Wave`, `Circular` and `CustomCurve` also store a perpendicular axis from `DeterministicMotionMath.ComputePerpAxis2D` or `ComputePerpAxis3D`, which use the same formulas as `BatchSpawnHelper.GetAccel2D/3D` for native projectiles. `FixedUpdate` (server only, launched, not hit) then switches on the type:
- `Straight`: nothing. The Rigidbody integrates velocity and gravity itself, which is also how an arc is produced. Gravity is set by the subclass `OnLaunch` from the config's `GravityScale`.
- `Wave` and `Circular` (`ApplyWaveCircular`): each tick the Rigidbody velocity is set to the closed-form `DeterministicMotionMath` velocity for the time since launch (`CalculateWave2D/3DVelocityDirection`, `CalculateCircular2D/3DVelocityDirection`) using the config's wave amplitude, frequency and phase offset, or circular angular speed (degrees per second, converted to radians), start angle and radius. The body is still a real Rigidbody, so collision detection is unchanged.
- `Guided` (`ApplyGuided`): with a target set, each tick the current velocity direction is turned toward the target at a fixed `GUIDED_TURN_RATE_RAD_PER_SEC` of pi radians per second (180 degrees per second) using `Vector3.Slerp`, clamped so one tick never passes the target direction. Speed is kept. A `Transform` target is followed live. When that `Transform` has been destroyed, the position it had when `SetGuidedTarget(Transform)` was called is used. With no target the shot is a normal physics projectile. The rate equals the hard-coded rate of `tick_guided` and `tick_guided_3d` in the native sim.
- `Teleport` (`ApplyTeleport`): velocity is set to zero every tick, a timer accumulates `Time.fixedDeltaTime`, and each time it passes `TELEPORT_INTERVAL_SECONDS` (0.12) the body jumps `speed * 0.12` along the launch direction through `TeleportBody`. The interval equals the one in `tick_teleport` and `tick_teleport_3d`.
- `CustomCurve` (`ApplyCustomCurve`, `protected virtual`): physics only. The native sim has no tick for movement type 6 and flies such a shot straight. The duration is `cfg.CustomCurveDuration`, or `cfg.Lifetime` when that is 0 or less (when neither is positive the method returns and the velocity is left alone). Linear progress is elapsed time over duration, wrapped with `Mathf.Repeat` when `CustomCurveLoop` is on and clamped to 0..1 otherwise. `EvaluateSpeedWarp` maps it to path progress, `cfg.EvaluateCustomPath` returns a (forward, sideways) offset, and the target position is `spawn position + launch direction * offset.x + perpendicular axis * offset.y`. The velocity is `(target - current position) / Time.fixedDeltaTime` (zero when `fixedDeltaTime` is not above 0.0001). The path is absolute geometry, so its own length is the total distance covered over the duration, and the config's bullet velocity does not change it. The `MaxRange` check of `Update` still applies. A non-finite velocity (a path formula that divides by zero or takes the root of a negative number) skips the update and logs one warning per launch that names the config id.
- The speed curve `cfg.CustomCurveSpeedMultiplier` is not a velocity multiplier. `BuildPathWarpTable` samples it at 17 evenly spaced points (`PATH_WARP_TABLE_SIZE`), floors each sample at 0.0001, builds the cumulative sum and normalises it so the last entry is 1. `EvaluateSpeedWarp` interpolates that table, and returns the linear value when the config has no speed curve.

Hits and damage. The subclass collision and trigger callbacks call `HandleHit2D` or `HandleHit3D`, which return when the instance is not the server or `HasHit` is already set. The steps:
- The layer of the hit object must pass `cfg.HitLayers` (a mask of -1, or no config, passes everything). A failed check returns without damage and without ending the shot.
- When the hit object has a `NetworkObject` in its parents, its id is added to `_hitTargetIds`. A second contact with the same `NetworkObject` is ignored, so physics re-triggering one collider cannot use up several piercing charges.
- `ResolvePiercingAndDamage` decides damage and whether the shot ends. With `_explosionRadius` above 0.01 the shot explodes at the hit point (`Physics.OverlapSphereNonAlloc` or `Physics2D.OverlapCircleNonAlloc` into a 32-slot array, filtered by `_damageLayerMask`) and always ends. Each collider with a `NetworkObject` gets damage scaled by `1 - clamp01(distance / _explosionRadius)`, where distance runs from the blast centre to the collider's transform position. A hit on an object without a `NetworkObject` (terrain) always ends the shot. Otherwise one direct-hit event is raised and the piercing budget decides: with `PiercingType.None` the shot ends, otherwise `_collisionsRemaining` (set at launch to `Max(cfg.MaxCollisions, 1)` for `Piecer` and `Random`, and 1 for everything else) drops by one and the shot ends at 0.
- When the shot ends: `HasHit = true`, `StopPhysics()`, `DestroyProjectile()`.
- Damage (`ComputeConfigDamage`) with a config: `cfg.EvaluateDamage(normalisedDistance)` where the distance is the straight line from the spawn position to the hit point divided by `MaxRange` (0 when `MaxRange` is 0 or less), then times `HeadshotMultiplier` when `CheckHeadshotPhysics` returns true, then times `CritMultiplier` when `Random.value < CritChance`, then times `_damageMultiplier`. Without a config it is `_baseDamage * _damageMultiplier`.
- `FireHitEvent` builds a `ProjectileHitPayload` with `ProjId` 0, `ConfigId`, `Is3D`, the target `NetworkObjectId` cast to `uint`, damage, `IsHeadshot`, `IsCrit`, hit position (the blast centre for explosions), the owner fields from `SetOwnerContext`, and `HitObject` (the collider's `GameObject`).

Pool visual. `SpawnPoolVisual` needs `LocalObjectPool.Instance`. It picks `_visual3DPoolType` when the config says `Is3D` (or, with no config, when the subclass is not 2D) and `_visual2DPoolType` otherwise, takes an object from the pool at the projectile pose rotated by `ClientPredictionManager.GetDirectionRotation`, turns off the pooled object's auto-return, calls `InitializeClientVisual(VisualConfigId, position, direction, speed)` when the object has a `ProjectileVisualBase`, and parents it under the projectile with identity local pose. A missing pool or an empty pool result logs a warning. `RetrySpawnVisual` tries once more one frame later. `ReturnPoolVisual` unparents the object and calls `ReturnToPoolImmediate()` on the visual, or returns it straight to `LocalObjectPool` when it has no `ProjectileVisualBase`.

**Decisions:**
- The collider is resized directly instead of scaling the root transform, because the pooled visual is parented under the projectile and applies its own `FullSizeX`/`FullSizeY` scale, so scaling the root would double the visible size.
- The static collider size needs no extra NetworkVariable or RPC, because every peer resolves the same `VisualConfigId` to the same shared `ProjectileConfigSO` and computes the same size.
- Growth runs on the server only. A non-server peer applies the full size at once, because nothing there reads the collider for hit detection.
- Wave, Circular and CustomCurve drive the Rigidbody velocity instead of moving the position, so the physics engine keeps doing collision detection. `CustomCurve` recomputes its target position from elapsed time every tick, so float error cannot accumulate and the last path point is reached when the duration ends, and the normalised warp table keeps that endpoint exact whatever the speed curve looks like.
- A path authored as (forward, sideways) offsets works in 2D and 3D unchanged, because the offsets are applied on the launch direction and the perpendicular axis.
- `FixedUpdate` and `ApplyCustomCurve` are `protected virtual` so a subclass can replace all movement or just the path math.
- `Guided` uses one `Slerp` implementation for both dimensions (a 2D velocity has no Z component and stays in plane).
- The guided target is cleared at each launch, because pooled instances are reused.
- Config speed wins over the caller's speed by default, because the weapon script that fires physics projectiles passes its own non-zero speed, so a "use the config when the caller passes 0" rule never applied. `_allowCallerVelocityOverride` is for weapons that vary speed per shot on purpose (charged or randomised shots).
- Explosion configs always end on first contact, because an area blast and multi-target piercing are not meant to be combined on one config.
- A hit on an object without a `NetworkObject` always ends the shot, because piercing lets a shot pass through damageable targets, not walls.
- `CheckHeadshotPhysics` returns false by default and is virtual, because the package has no knowledge of head hitboxes. A game subclass overrides it, as it overrides `LocalProjectileManager.CheckHeadshotLocal` for the local path.
- The layer check follows `LocalProjectileManager.PassesLayerMask` (mask -1 means everything).
- The piercing budget follows `RustSimAdapter.HandlePiercing`, so one config behaves the same on the physics and the native path.
- `isHeadshot` is not passed to `PlayImpact`, because that would change the signature of the protected virtual `NetworkProjectileBase.OnSpawnImpactEffectClient`, which game subclasses may override.
- The collider and lifetime refresh runs from `HandleVisualConfigChanged` even when the pool visual does not exist yet, because neither depends on the visual.

**Limits:**
- Movement types and all physics run on the server only. Clients show the replicated pose.
- When no config resolves (no registry, or an id outside the registry), `TimeToLive`, `_damageLayerMask` and the collider size keep the values of the previous shot on a pooled instance, damage is `_baseDamage`, and the caller's speed is used.
- The `HitLayers` filter changes damage only. The projectile still collides physically with excluded layers and carries on afterwards.
- `MaxRange` ends a shot by straight-line distance from the spawn position, not path length.
- Explosion damage allocates a 32-element array per blast, finds at most 32 colliders and raises one event per collider, so a `NetworkObject` with several colliders can be damaged several times by one blast. Falloff is 0 at the radius edge.
- `ProjectileHitPayload.ProjId` is always 0 and `GameData` is not set for physics hits. A target `NetworkObjectId` above `uint.MaxValue` would be truncated by the cast.
- Impact effects play on every server-side destroy, including `MaxRange` and `TimeToLive` expiry, and never use the headshot variant.
- `Teleport` moves the body without a sweep, so thin colliders can be passed through. The native sim has the same property.
- The guided turn rate is fixed at 180 degrees per second and is not a config field.
- The subclasses switch gravity off for `Wave`, `Circular` and `Teleport` (not for `Guided` or `CustomCurve`), see `PhysicsProjectile2D.cs` and `PhysicsProjectile3D.cs`.
- A `Guided` shot with no target flies as a plain physics projectile.
- The prefab must carry the Rigidbody and collider the subclass needs. The base does not check this.

### `ProjectileGuidanceTracker.cs`
**What it does:** `ProjectileGuidanceTracker` is a `Singleton` (auto-created like the other managers) that makes RustSim projectiles with the `Guided` movement type home automatically. `MID_MasterProjectileSystem.SetHomingDirection2D/3D` writes a desired heading into the native sim's acceleration fields for one `ProjId`, and the native guided tick turns the velocity toward that heading at a fixed rate (pi radians per second in 2D, the same turn rate in 3D) while keeping the speed. The heading has to be refreshed as the target moves. This class does that: register a `ProjId` against a `Transform` or a fixed point once, and every frame it pushes the direction from the projectile's current position to the target.

Public surface:
- `RegisterGuidedTarget2D(uint projId, Transform)` and `(uint projId, Vector2 fixedPoint)`, and the 3D pair. A null `Transform` is ignored. Registering the same `ProjId` again replaces its entry.
- `Unregister(projId)`, `IsTracking(projId)`, `ClearAll()`.
- `event Action<uint> OnGuidanceEnded`: raised when tracking stops because the projectile is gone (the position lookup failed) or the target `Transform` was destroyed. It is not raised by `Unregister` or `ClearAll`.

**How it works:** `Update` (not `FixedUpdate`; the sim consumes the heading once per fixed step, and `Update` always runs at least once before the next one, so no Script Execution Order setup is needed) loops over the entries. For each one it reads the live position with `TryGetProjectilePosition2D/3D`, and calls `SetHomingDirection2D/3D` with `target - position` unless that vector is shorter than 0.01. Entries to end are collected and removed after the loop.

**Limits:**
- It drives only the authoritative copy. `SetHomingDirection2D/3D` write only on a server or when the session is not networked, so on a pure client the calls are harmless no-ops.
- Client-side prediction of the curved path is not handled: `LocalProjectileManager` reconciles Guided movement as a straight line between snapshots.
- It does nothing until `MID_MasterProjectileSystem` exists.
- Physics projectiles do not use this class. They have their own `SetGuidedTarget` on `PhysicsProjectileBase`.

### `ProjectileVisualWarmupManager.cs`
**What it does:** `ProjectileVisualWarmupManager` is a `Singleton` that pre-warms the pooled projectile visuals for every registered config at scene load, so the cost of the first few real shots is paid up front. Unity resolves a packed Sprite Atlas texture lazily, and `ProjectileRegistry.Register()` already touches every config's `sprite.texture` once, which is enough for a plain `SpriteRenderer`. The shape-mesh path (`ProjectileVisual_2D.ApplyShapeMeshOptimised`, `ProjectileVisual_3D`'s instanced material) snapshots `sprite.texture` and a UV rect once per `OnInitialise` call. If the snapshot lands before the atlas has resolved, the wrong texture stays baked into that pooled instance until its next `OnInitialise`.

Inspector fields:

| Field | Default | Meaning |
|---|---|---|
| `_extraPoolTypesToWarm` | empty | Pool types to warm in addition to `Projectile_Visual2D` and `Projectile_Visual3D`. Needed only when a prefab variant overrides its visual pool type, because configs carry no pool type. |
| `_framesToWaitBeforeWarmup` | 2 (1 to 10) | Frames to wait after both dependencies are ready. |
| `_framesBeforeSecondPass` | 3 (0 to 10) | Frames before a second full pass. 0 disables it. |
| `_dependencyTimeoutSeconds` | 10 | Longest wait for the registry and pool before giving up with a warning. |
| `_logLevel` | `Info` | `MID_Logger` level. |

Public surface: `WarmupComplete` (true once one pass has finished) and `RequestWarmup()` (re-runs the routine, for example after registering configs at runtime; does nothing while a pass is running).

**How it works:** `Start` runs a coroutine. It waits until `ProjectileRegistry` has a config and `LocalObjectPool` is initialised (up to the timeout), waits the configured frames, runs a pass, waits again and runs a second pass. A pass visits each pool type once. For each, it reads `LocalObjectPool.GetQueuedCount(type)` and borrows and returns that many instances (the pool is a FIFO queue, so that visits every idle instance once). Each borrowed instance is initialised with `InitializeClientVisual` for every config whose dimensionality fits the type (2D configs for `Projectile_Visual2D`, 3D configs for `Projectile_Visual3D`, every config for an extra type) and then `ReturnToPoolImmediate()`d. The pass logs how many instances and combinations it touched.

**Limits:**
- Warming is per pooled instance, so only instances idle at the time of the pass are touched. A pool that grows later creates cold instances.
- A pool type whose prefab has no `ProjectileVisualBase` is skipped with a warning.
- It only reduces the first-use lag. It does not fix a wrong visual that has another cause.

### `RaycastProjectileHandler.cs`
**What it does:** `RaycastProjectileHandler` is a `NetworkBehaviour` that handles hitscan ("raycast") weapons. The weapon script casts the ray itself and passes the result in as a `RaycastFireResult` (`Origin`, `Direction`, `HitPoint`, `DidHit`, `HitTargetNetworkId`, `IsHeadshot`, `Is3D`). The handler validates a client's claim on the server, raises `OnServerHitConfirmed` (a `ProjectileHitPayload`) for each confirmed hit, and sends a short-lived visual to the other clients. It supports 2D and 3D and a multi-pellet pattern path.

Inspector fields:

| Field | Default | Meaning |
|---|---|---|
| `_visualTravelSpeed` | 40 | Speed in units per second of the cosmetic visual flying from origin to hit point. |
| `_visualPoolType2D`, `_visualPoolType3D` | `Projectile_Visual2D`, `Projectile_Visual3D` | `LocalObjectPool` types for the visual. |
| `_hitValidationTolerance` | 2 | Largest distance between the client's reported hit point and the server's own raycast hit point that still counts as the same hit. |
| `_serverRaycastLayers` | -1 (everything) | Layers the server raycasts test. Must include the layers your targets are on. |
| `_trustClientOnValidationMiss` | true | When the server's own raycast disagrees with the client, accept the client's reported hit anyway if it names a target. |
| `_logLevel` | `Info` | `MID_Logger` level. |

**How it works:**

Single ray, `ServerHandleFire(clientResult, context, configId, senderClientId = ulong.MaxValue, clientFireTick = 0)` (server only):
1. Looks the config up, logs an error and returns if it is not registered.
2. If the client reported a hit, `ValidateHitServer` runs inside `MID_LagCompensator.BeginRewind(clientFireTick)`, so the registered targets are at their historical poses for the duration of the check (a tick of 0 or less means no rewind).
3. `ValidateHitServer` casts the server's own ray from the client-supplied `Origin` along the client-supplied `Direction`, 1000 units long (a fixed number, not `cfg.MaxRange`), against `_serverRaycastLayers`, triggers included (2D uses a `ContactFilter2D` with `useTriggers = true`, so the project's "queries hit triggers" setting does not matter). If it hits within `_hitValidationTolerance` of the client's hit point, the hit is confirmed. The target is the `NetworkObject` found on the hit collider or above it. If the collider has none and `_trustClientOnValidationMiss` is on, the client's reported target id is used. The headshot flag is taken from the client.
4. If the server ray misses or lands outside the tolerance, and `_trustClientOnValidationMiss` is on, and the client named a non-zero target, and the client's origin to hit point distance is at most 1000, the client's hit point, target and headshot flag are accepted as they are. With the option off, the shot is rejected.
5. For a confirmed hit with a non-zero target the server computes damage as `cfg.EvaluateDamage(0)`, times `cfg.HeadshotMultiplier` for a headshot, times `cfg.CritMultiplier` after a server-side crit roll against `cfg.CritChance`, times `context.DamageMultiplier`. It raises `OnServerHitConfirmed` with the owner data from the `WeaponFireContext` and a `ServerProjectileData` built by `BuildRaycastGameData`. The target id is narrowed to `uint` in the payload.
6. `SpawnVisualClientRpc` (reliable) goes to every connected client except `senderClientId`, because the sender already drew its own visual. The `confirmedHit` flag tells clients whether to play the impact effect on arrival.

Pattern, `ServerHandleFirePattern(patternId, origin, baseDirection, pelletCount, spreadDeg, is3D, context, configId, senderClientId, clientFireTick)` (server only). There is no client hit claim at all. The directions come from `ProjectileDirectionResolver.Resolve` (fire seed fixed at the default), and the server casts one ray per pellet (`cfg.MaxRange`, or 1000 when it is 0 or less), all inside one lag-compensation rewind. Each hit raises `OnServerHitConfirmed` with a crit roll but no headshot. All pellets' hit points go to the clients in one unreliable `SpawnPatternVisualClientRpc`, again excluding the sender.

Client visuals. `SpawnVisualLocal` takes an object from `LocalObjectPool`, initialises its `ProjectileVisualBase` with `InitializeClientVisual`, and `Update` moves it toward the hit point with `Vector3.MoveTowards` at `_visualTravelSpeed`. At 0.05 units from the hit point the impact effect plays (when `PlayImpactOnArrival`) through `ProjectileImpactHandler.PlayImpact`, and the object is returned to the pool. A visual whose object was destroyed is dropped.

Prediction and offline:
- `OfflineHandleFire(result, configId, ownerLocalId, damageMultiplier)` is used offline and by a client for its own shot. When the ray hit and a `LocalProjectileManager` exists, it computes damage the same way and raises `LocalProjectileManager.FireHitEvent` (which is the manager's `OnHit`), then spawns the local visual.
- `ClientPredictPatternLocal(...)` resolves the same pellet directions as the server, casts one plain local ray per pellet (no lag compensation) and calls `OfflineHandleFire` for each. It exists because the server's pattern broadcast leaves the firing client out.

**Limits:**
- With the default `_trustClientOnValidationMiss = true`, a client can make the server accept a hit it did not earn. If the server's own ray misses, the client's claimed hit point and target id are taken as sent, and the only checks are a non-zero target id and a distance of at most 1000 from the client-supplied origin. Nothing checks that the target exists, is near the ray, or that the origin is near the shooter. The ray itself is also built from the client's `Origin` and `Direction`. The option's tooltip says it is meant for cooperative or trusted-client games and should be off for competitive ones.
- With trust on, a hit on geometry that has no `NetworkObject` still takes the client's target id when the client reported one, whether or not that id belongs to anything near the hit.
- The single-ray path validates against a fixed 1000 units, not the config's `MaxRange`, and the headshot flag is not validated.
- Damage uses `EvaluateDamage(0)`, so range falloff is never applied on the raycast paths.
- The pattern path never reports a headshot.
- `OfflineHandleFire` raises `LocalProjectileManager.OnHit`, which `MID_MasterProjectileSystem` relays as `OnRustSimHit`. A client's own predicted raycast hit therefore raises `OnRustSimHit` (damage computed with multiplier 1, `RawTargetId` set), separately from any server confirmation.
- `SpawnPatternVisualClientRpc` is unreliable, so a dropped packet drops that shot's visuals on that client. The hit itself is unaffected, since it is decided on the server.
- `Update` allocates a `List<int>` each frame while visuals are active, and each server cast allocates a one-element `RaycastHit2D` array.
- `ServerHandleFire` called without a `clientFireTick` (the host path in `MID_MasterProjectileSystem.RegisterRaycastFire`) does no lag compensation.

### `RustSimCustomShapeAuthoring.cs`
**What it does:** A hand-authored collider shape for the RustSim impact detector, for anything Unity's built-in colliders cannot represent (or that `RustSimTargetRegistrar` only approximates, such as an exact 3D box). The author places control points in the Scene view with `RustSimCustomShapeAuthoringEditor`, and the component bakes them to a sampled point sequence with one of three interpolation modes (`ShapeSplineType`: `Linear`, `CatmullRom`, `Bezier`, the same three as `ProjectilePatternSO`, generalised from `Vector2` to `Vector3` so one component covers 2D and 3D) and registers the result as a `ShapeCollider2D/3D`.

Inspector fields: `_is3D` (a 2D shape ignores Z on every point), `_controlPoints` (default two points at (-0.5, 0, 0) and (0.5, 0, 0)), `_splineType` (default `Linear`), `_closedLoop`, `_explicitTargetId`, `_isNetworkedObject`, `_isStatic` with its refresh interval, `_useFixedUpdate` and the update cadence, the same way the registrar has them.

**How it works:** The baked points are stored relative to this transform and re-projected to world space with `transform.TransformPoint` on every registration, so a shape on a moving platform follows it. The curve itself is re-baked only when the control points are edited or `Rebake()` is called, because the shape relative to the transform does not change when the transform moves. Readiness, target id and static refresh work as in `RustSimTargetRegistrar`.

**Limits:**
- The baked point count is bounded by `ShapeCollider2D.MaxPoints` (checked against the Rust side by `ProjectileLib.ValidateStructSizes`).

### `RustSimTargetRegistrar.cs`
**What it does:** A component you drop on any object that should take part in RustSim (native) projectile hit detection (players, enemies, breakables). It registers the object as a collision target with `MID_MasterProjectileSystem` and keeps that registration current. It does three things for every target:
1. A stable `TargetId`: the `NetworkObjectId` when the object has a `NetworkObject`, otherwise a locally generated id (or `_explicitTargetId` when set).
2. Position kept current. `RegisterTarget2D/3D` is an upsert (the backend scans for an entry with the same `TargetId` and overwrites it, appending only if none is found), so registering again with a new position is how a target moves. The component does that on a cadence.
3. The real Unity layer. It always uses the `(target, GameObject)` overload, which reads `gameObject.layer`, so the `int unityLayer = 0` default (silently wrong for any object not on layer 0, and the reason `HitLayers` would then not match) cannot happen.

It works with or without Netcode. The registration calls already do nothing on a pure client and register directly in offline mode, so the component does not need to know the mode.

Inspector fields: `_is3D`; `_forceCircleOrSphere` (skip shape detection); `_radius` (0 = derive from the collider); `_explicitTargetId` (0 = automatic); `_isNetworkedObject` (wait for the `NetworkObject` to spawn before resolving the id); `_isStatic` with `_staticRefreshSeconds` (default 0.5, real time, 0 = register once and never refresh); `_useFixedUpdate` (default true); `_updateEveryNTicks`.

**How it works:**
- Shape detection. Beyond `CircleCollider2D` and `SphereCollider`, it detects `BoxCollider2D`, `CapsuleCollider2D`, `EdgeCollider2D`, `PolygonCollider2D` (2D) and `CapsuleCollider` (3D), and registers a `ShapeCollider2D/3D` instead of a plain circle. Priority, first match wins: Circle or Sphere, Box, Capsule, Edge, Polygon. The 2D shapes are exact.
- A 3D `BoxCollider` is approximated as a capsule along its longest local axis. A cuboid's twelve edges cannot be traced unambiguously by `ShapeCollider3D`'s single closed point loop. For an exact 3D box, author it point by point with `RustSimCustomShapeAuthoring`.
- Readiness. `TryRegisterOrWaitForReady` checks `MID_MasterProjectileSystem.IsReady` first and registers at once when the system is up, and subscribes to `OnSystemReady` only when it is not, so an object spawned minutes into a session works the same as a scene object racing startup. A per-tick retry in `Update`/`FixedUpdate` is a second safety net.
- `RegisterNow` stays unregistered while `CanAcceptTargets` is false (no backend would take the call), and, for a networked object, until `NetworkObject.IsSpawned` is true. A `NetworkObjectId` read before the spawn is a placeholder, probably the same for every such object, so the ids would collide.
- A moving target re-registers on its cadence. A static target registers, then refreshes only every `_staticRefreshSeconds`, so it lands in whichever backend is active after a mode change.

**Limits:**
- `RegisterTarget2D/3D` scans every registered target for the id, so registering N targets every tick costs O(N squared) per frame. That is negligible for tens of targets. With hundreds of active targets raise `_updateEveryNTicks`.
- A `Tooltip` string on `_isNetworkedObject` still carries the label "NETWORK OBJECT ID TIMING FIX" (a string literal, left unchanged in the comment-only pass).

### `ServerProjectileAuthority.cs`
**What it does:** The server-side backend for RustSim (native) projectiles, a `MonoBehaviour` (`MID_MasterProjectileSystem` wires it in). It owns the authoritative 2D and 3D projectile buffers, the target and shape buffers (upserted by `TargetId`) that are passed to the native library every tick, and a `RustSimAdapter` (`Adapter`) that turns native hits into `ProjectileHitPayload` events. It does nothing unless `NetworkManager.Singleton.IsServer`. It also declares `ProjectileSnapshot2D` and `ProjectileSnapshot3D`, the wire structs of the position snapshots.

Public surface:
- Properties: `Adapter`, `TrailPool`, `ObserverProvider` (an `IProjectileObserverProvider`, used for distance culling), `NetworkBridge`, `EnableDistanceCulling`, `CullVisRange`, `TargetCount2D/3D`, `ShapeCount2D/3D`, `ActiveCount2D/3D`.
- Spawning: `AddProjectile2D/3D`, `SpawnStaggered(...)` (burst path), `NotifyBatchSpawned2D/3D`, `Get2DWriteHead()`/`Get3DWriteHead()` (the native batch writer's destination) and `AllocateProjIds(count)`.
- Targets and shapes: `RegisterTarget2D/3D`, `DeactivateTarget2D/3D`, `RegisterShape2D/3D`, `DeactivateShape2D/3D`, `ClearAllTargets()`.
- Guided helpers: `SetAcceleration2D/3D(projId, dir)` and `TryGetPosition2D/3D(projId, out pos)`.
- State: `SaveState2D(byte[])` and `RestoreState2D(byte[], int)` (2D only).

Inspector fields:

| Field | Default | Meaning |
|---|---|---|
| `_maxProjectiles2D`, `_maxProjectiles3D` | 2048, 512 | Buffer capacity. |
| `_maxTargets2D`, `_maxTargets3D` | 128, 64 | Circle target capacity. |
| `_maxShapes2D`, `_maxShapes3D` | 64, 32 | Shape collider capacity (box, capsule, edge, polygon). |
| `_maxHitsPerTick` | 256 | Hit buffer size per collision pass. |
| `_cellSize2D`, `_cellSize3D` | 4, 4 | Cell size of the native broad-phase grid. |
| `_snapshotIntervalTicks` | 4 | A snapshot goes out every this many fixed steps. |
| `_enableDistanceCulling` | false | Per-client snapshot filtering by distance (see below). |
| `_cullVisRange` | 60 | Radius around a client's observer position, in world units. |
| `_renderer2D`, `_renderer3D` | none | The scene renderers, drawn each frame so the host sees the authoritative projectiles. |
| `_enableLogs` | false | Verbose logging. |

**How it works:**

Fixed step (server only): advance the `DelayedSpawnQueue`, then for the 2D buffer tick the native sim, run the collision pass (`check_hits_grid_ex`, circle targets and shape colliders), sync trails and compact dead entries, then the same for 3D, then send a snapshot when the step counter is a multiple of `_snapshotIntervalTicks`. Native hits go through `Adapter`, whose `OnProjectileHit` event is what `MID_ProjectileNetworkBridge` listens to in order to send `HitConfirmedClientRpc`, and what server game code subscribes to for authoritative damage.

Burst timing. Each backend owns a `DelayedSpawnQueue` and advances it at the start of `FixedUpdate`, before the sim tick. A shot whose `SpawnPoint[]` has a start delay on any entry takes a one-projectile-at-a-time path: `ServerProjectileAuthority.SpawnStaggered` on the server, and `SpawnStaggeredOffline`, `SpawnFiringClientStaggered` and `SpawnNetworkStaggered` in `LocalProjectileManager`. Shots with no delay keep the batch path. A delayed projectile is written to the buffer when its time comes, so the native sim, collision, snapshots and renderers never see a pending state.

Decisions:
- The native struct has no start delay field, and adding one would need all platform libraries rebuilt and the SIMD tick and collision code changed. The managed queue avoids that.
- The whole id block is reserved up front. Projectile `i` always has id `baseId + i`, spawned now or later, so the firing client's temp-to-real id link keeps working. `LinkNetworkProjectileBatch` also relinks entries still in the queue, and `KillNetworkProjectile` drops them.
- On the server, latency compensation only shortens the projectile's lifetime and never moves it. Taking it from the start delay would spawn the delayed projectiles on top of each other, so every projectile keeps its full delay and gets the same lifetime cut. Other clients do move the projectile forward: they subtract the time since the server spawn from each delay and advance the projectile by what remains, so the spacing between projectiles stays the same.
- A delayed projectile spawns at the origin the shot had when it was fired. The muzzle position is not on the wire, so following the muzzle would differ between peers.

Snapshots. `SendSnapshots` collects the live projectiles into `ProjectileSnapshot2D` (`ProjId`, `X`, `Y`, `ServerTick`) and `ProjectileSnapshot3D` (plus `Z`), leaving out Wave and Circular ones (the client's own native sim reproduces them, and the linear reconciliation does not describe them). X, Y and Z serialize as half-precision floats; `ProjId` and `ServerTick` stay full size. With culling off, one combined array goes to everyone. With `_enableDistanceCulling` on and an `ObserverProvider` set, `SendSnapshotsCulled` filters per connected client (distance from that client's observer position within `_cullVisRange`, and the projectiles that client owns are excluded) and sends one targeted RPC per client. A client with no resolvable observer position gets the unfiltered set. Every send goes through the chunked sender, which resizes each chunk to exactly its entries (NGO serializes an array by its real length, and the buffers are pre-allocated at full capacity) and caps a chunk to a 900-byte payload budget (entries are 12 bytes in 2D and 14 in 3D, 48 bytes of RPC overhead), so a large tick becomes two or three unreliable RPCs. A dropped chunk costs those projectiles one update.

**Limits:**
- Half-precision positions: the rounding error is at most about 0.0005 per unit of magnitude (0.06 between 128 and 256 units from the origin, 0.125 between 256 and 512). The client skips a correction below 0.08, so past roughly 256 units from the origin the quantisation error alone can trigger corrections.
- `_snapshotIntervalTicks` has no minimum. Set to 0, the `%` in `FixedUpdate` throws a `DivideByZeroException` every fixed step.
- A culled client never receives a spawn it was out of range for, and a dropped unreliable spawn packet is not resent.
- 2D culling compares X and Y only (the observer's Z is ignored), and 3D culling compares all three axes.
- `OnDestroy` calls `ProjectileLib.clear_movement_params()` without checking `ProjectileLib.IsAvailable` (so does `MID_MasterProjectileSystem`).
- Only 2D has `SaveState2D` and `RestoreState2D`.

### `ProjectileRenderer2D.cs`
**What it does:** Draws the RustSim (native) 2D projectiles from `NativeProjectile[]` with no per-projectile GameObject. `Render(projs, count)` is called by the owner of the buffer, `ServerProjectileAuthority` (each frame, for its 2D and 3D buffers) and `LocalProjectileManager`. It requires a `ProjectileManager` on the same object.

Inspector fields: `_atlasMaterial` (required; the render is skipped with a warning when it is missing), `_forceDrawMesh` (false) and `_sortDepthStep` (0.0001).

**How it works:** In `Awake` the path is chosen. With hardware instancing available (and `_forceDrawMesh` off) it uses the instanced path: projectiles are grouped per config id and drawn with `Graphics.DrawMeshInstanced` in batches of 1023, with per-instance matrix, `_UVRect` and `_Color` arrays set through a `MaterialPropertyBlock`. Otherwise it uses the combined-mesh path: one dynamic mesh for the sprite pass and another for the shape pass (two meshes, so the second `Clear` cannot clobber the first draw), built on the CPU with room for 12 vertices and 30 triangle indices per slot, drawn with `Graphics.DrawMesh`. Shapes with other than four vertices are laid out by their real vertex and index counts. The fade-in and fade-out tint comes from the projectile's lifetime. The chosen path is logged with `Debug.Log`.

Sorting. `Graphics.DrawMesh*` has no sorting layer or order, so each projectile's Z is nudged by `-SortingPriority * _sortDepthStep`. That orders RustSim projectiles among themselves. Whether they sort correctly against `SpriteRenderer` objects depends on the camera's Transparency Sort Mode (it needs a sort axis the Z offset moves along, for example orthographic with axis (0, 0, 1)); check it in your own project.

Shaders (`Runtime/Shaders`): `InstancedProjectile.shader` (built-in render pipeline) and `InstancedProjectile_URP.shader` (URP) read `_MainTex`, a `_UVRect` and a `_Color` per instance and multiply the vertex colour in, so the combined-mesh path can carry the fade in vertex colour. The URP version has `#ifdef UNITY_INSTANCING_ENABLED` branches: without them the non-instanced path would read the material's baked default `_UVRect` and corrupt every UV in the combined batch.

**Limits:**
- Both render paths need the atlas material to use one of the two shaders (or an equivalent that reads the same properties).
- The Z sorting trick is only as good as the camera setting described above.
- The instanced path groups by config, so draw order inside one config follows the buffer order.

### `ProjectileRenderer3D.cs`
**What it does:** Draws RustSim 3D projectiles from `NativeProjectile3D[]` as camera-facing quads (billboards around the velocity axis). `Render(projs, count)` runs two passes, sprites and shapes, each into its own dynamic mesh so the second pass cannot clear the first.

Inspector fields: `_atlasMaterial`, `_maxQuads` (512), `_renderCamera` (falls back to `Camera.main`), `_fadeInFraction` (0.10) and `_fadeOutFraction` (0.15) (0 to 0.3 each).

**How it works:** For each live projectile with a config, the quad is spanned by the velocity direction and a perpendicular vector (`cross(forward, camera to projectile)`, with fallbacks when that is degenerate), with half length `ScaleX / 2` and half width `FullSizeY / 2`. Sprite configs use the UV rect from `ProjectileRegistry.GetUVRect`, others use the full rect. The tint fades by lifetime progress. Each pass uploads its quads and draws them with `Graphics.DrawMesh` and a huge bounding box.

**Limits:**
- Each pass draws at most `_maxQuads` projectiles and silently ignores the rest.
- All sprite quads in a pass use the texture of the first sprite config encountered, so the sprites must come from one atlas.
- Shape configs are drawn as quads here, not as their custom meshes.

### `ProjectileVisual_2D.cs`
**What it does:** The 2D pooled visual. It draws with a `SpriteRenderer` for a plain sprite config, or with a child `MeshRenderer` for a config that has a `CustomShape`, plus an optional `TrailRenderer`. It rotates with `Atan2` about Z.

Inspector fields: `projectileSpriteRend`, `projectileTrailRend`, the sorting layer name and `_spriteSortingOrder` (1), `_trailSortingOrder` (0), `_forceSpriteRendererOnly` (false; skips the shape mesh whatever the config says), `_shapeMeshChild`/`_shapeMeshFilter`/`_shapeMeshRenderer` (a child object auto-created at runtime when missing), `_shapeSortingOrder` (1) and `_fallbackShapeMaterial` (assign a material that uses `InstancedProjectile` or `InstancedProjectile_URP` so atlas UVs work).

**How it works:** `OnInitialise` first turns both renderers off, then decides: with a `CustomShape` whose mesh has vertices (and `_forceSpriteRendererOnly` off) it applies the shape mesh, otherwise the sprite. The shape path sets `transform.localScale` to `(FullSizeX, FullSizeY, 1)`, assigns the mesh and writes a `MaterialPropertyBlock` with `_MainTex` and `_UVRect` (from `ProjectileRegistry.GetUVRect`; `Texture2D.whiteTexture` and the full rect when there is no sprite). The sprite path sets the same scale (`Vector3.one` when the config is null) and uses the sprite only when `cfg.UseSprite` is true, as the Rust-sim renderers do. It then calls `RefreshScaleGrowth` and applies the trail. `OnReturnToPool` resets the renderers, clears the trail and drops the cached sprite and config state. The fallback sprite and texture are static native objects, destroyed before an editor domain reload so Unity does not report a leak.

**Limits:**
- The shape and sprite snapshots are taken once per `OnInitialise`, so they can go stale if the sprite atlas has not resolved yet. `ProjectileVisualWarmupManager` exists to reduce this.

### `ProjectileVisual_3D.cs`
**What it does:** The 3D pooled visual: a `MeshFilter` and `MeshRenderer` with an optional `TrailRenderer`, oriented with `LookRotation`.

Inspector fields: `_meshFilter`, `_meshRenderer`, `_trailRenderer` (found automatically when empty), `_scaleMultiplier` (1, a multiplier on the config's `FullSizeX`) and `_fallbackMaterial`. Two protected hooks, `OnInitialise3D(cfg)` and `OnCleanup3D()`, let a subclass add behaviour.

**How it works:** `OnInitialise` applies the mesh (the config's custom shape mesh, or a lazily generated default capsule), the material, the scale, scale growth, the trail and the hook. With a sprite it gives the renderer an instanced copy of the material with `mainTexture` and `_UVRect` set. With no sprite, or an unresolved config, it uses `_fallbackMaterial` or a lazily generated shared default, so something is always visible. The scale is `length = FullSizeX * _scaleMultiplier` and `width = (FullSizeY / FullSizeX) * length`, applied as `(width, width, length)`; `ApplyScaleAtSize` reproduces it for growth. The default mesh and material are static native objects, destroyed before an editor domain reload.

**Limits:**
- The width is the same on both cross axes, so a 3D visual is always round in section.

### `ProjectileVisualBase.cs`
**What it does:** `ProjectileVisualBase` is the abstract `MonoBehaviour` base of every pooled projectile visual, 2D and 3D (`[DisallowMultipleComponent]`). It is an abstract class and not an interface so subclasses share the pool-return logic and the common state (config id, origin, direction, speed). Pool visuals are fire-and-forget client cosmetics; a visual that travels with a `NetworkObject` is a separate system (`INetworkProjectileVisual`, see `NetworkProjectileBase.cs`).

Public surface:
- Read-only state: `ConfigId`, `Origin`, `Direction`, `Speed`, `IsActive`.
- `InitializeClientVisual(ushort configId, Vector3 origin, Vector3 direction, float speed)`: stores the state (a direction shorter than about 0.03 becomes `Vector3.forward`, otherwise it is normalised), moves the object to `origin`, calls `ApplyRotation`, resolves the config from `ProjectileRegistry` (null when the id is not registered) and calls the abstract `OnInitialise(cfg)`.
- `ReturnToPoolImmediate()`: clears `IsActive`, stops scale growth, calls `OnReturnToPool()` and then `LocalPoolReturn.ReturnToPoolNow()`.
- `virtual HideProjectile()`: hides rendering without returning to the pool (empty by default).
- Subclass hooks: `abstract OnInitialise(ProjectileConfigSO)`, `abstract OnReturnToPool()`, `virtual ApplyRotation(Vector3)` (the base does nothing), `virtual ApplyScaleAtSize(float sizeX, float sizeY)` (default: `localScale = (sizeX, sizeY, 1)`), and `RefreshScaleGrowth(cfg)`.

Inspector: `_poolReturn` (a `LocalPoolReturn`, found with `GetComponent` in `Awake` when empty) and `_logLevel` (default `None`).

To extend, derive in the game assembly and override `OnInitialise` and `OnReturnToPool` (for example to set a trail colour from the config and clear it on return).

**How it works:** Scale growth. When `cfg.UseScaleGrowth` is set, `RefreshScaleGrowth` starts a coroutine that starts at `FullSizeX/Y * SpawnScaleFraction` and moves toward the full size with `current += (target - current) * GrowthSpeed * dt`, the same formula as the native `tick_scale`. Subclasses call it from `OnInitialise` after their own one-shot full-size scale, so with growth off the one-shot value stands at no per-frame cost, and with growth on the first `ApplyScaleAtSize` lands in the same frame (no one-frame flash at full size). It is cosmetic and local; every peer runs it from the same config. The server-authoritative collider of a physics projectile is grown separately by `PhysicsProjectileBase`.

**Limits:**
- `_logLevel` defaults to `None`, which hides everything including errors from this class.
- `OnInitialise` can receive a null config, so a subclass must handle that.

### `TrailObjectPool.cs`
**What it does:** Bridges the native simulation and `TrailRendererPool` (from the utilities package). `ServerProjectileAuthority` and `LocalProjectileManager` call `SyncToSimulation(projs, count)` (2D) or `SyncToSimulation3D` (3D) from their fixed-step loops, and the authority calls `NotifyDead` when a projectile dies. For each live projectile whose config `HasTrail` it acquires a trail slot on first sight, builds a `TrailConfig` from the config (material, optional gradient override, time, widths, cap vertices, sorting layer and order) and moves the slot to the projectile's position. It requires a `ProjectileManager` on the same object.

Public surface: `NotifyDead(projId)` releases the slot so the trail fades out, `RelinkProjectileId(oldId, newId)` re-keys a slot when a firing client's temporary id is swapped for the server's real id (`LocalProjectileManager.LinkNetworkProjectileBatch` mutates `ProjId` in place; without the re-key the next sync would acquire a second slot and orphan the first), and `ReleaseAll()`.

Inspector: `_trailPool` (found as `TrailRendererPool.Instance` when empty; a missing pool logs an error and trails do not render).

**Limits:**
- The config is looked up with `ProjectileRegistry.Instance.Get` for every live projectile every sync, with no null check on the instance.
- A projectile that dies without `NotifyDead` keeps its slot until `ReleaseAll`.

### `ProjectileImpactHandler.cs` (Runtime)
**What it does:** Client-side singleton that plays the impact effect for each
confirmed hit. `PlayImpact(position, configId, isHeadshot)` is called by the
network bridge on the hit RPC. It sends the effect through `GlobalFXManager`
when one exists, and otherwise through a strategy registered per config:
a pooled particle system, a pooled sprite-sheet flipbook, or a shared particle
system that is emitted into.

**How it works:**
- Routing order: `GlobalFXManager` when `_preferGlobalFX` is on and an instance
  exists, then the strategy registered for the config id (`RegisterStrategy`),
  then the default particle type (`_defaultParticleType`).
- On the `GlobalFXManager` route a headshot uses `_headshotEffectType` when it
  differs from `_defaultGlobalFXType`. Any other hit uses the binding for the
  config (`RegisterConfigEffectType` or the `_configEffectTypeBindings` list).
  A binding with no particle count falls back to `_defaultGlobalFXParticleCount`.
- A config with no binding is mapped from its impact particle type: the three
  explosion types give `SmallExplosion`, `MediumExplosion` and `LargeExplosion`,
  the headshot particle gives `FleshSurface`, and everything else gives
  `Generic`. Headshots return `_headshotEffectType`.
- The impact normal passed to `GlobalFXManager` is always `Vector3.up`.

**Decisions:**
- `GlobalFXManager` is preferred so impacts share its in-scene particle
  systems instead of a second pool. The strategy path stays for scenes that have
  no `GlobalFXManager`.

### `MiniJsonParser.cs` (Editor)
**What it does:** A small JSON parser with no dependencies, used by the editor tooling that imports JSON into assets. It returns plain `Dictionary<string, object>`, `List<object>`, `string`, `double`, `bool` and `null`. It exists because `JsonUtility` can only fill a class that is declared in advance, and the importers here accept free-form shapes.

**Limits:** No `\uXXXX` escape support. It is an import helper, not a general-purpose parser.

### `NetworkProjectileBaseEditor.cs` (Editor)
**What it does:** The inspector for every class derived from `NetworkProjectileBase` (the physics projectile bases and their 2D and 3D variants). Declared as `[CustomEditor(typeof(NetworkProjectileBase), true)]` with `[CanEditMultipleObjects]`.

**Why it exists:** `NetworkProjectileBase` derives from NGO's `NetworkTransform`. NGO 1.7.1's `NetworkTransformEditor` is declared `[CustomEditor(typeof(NetworkTransform), true)]` and draws a curated field list without ever calling `DrawDefaultInspector`, so every serialized field added by a subclass (config references, pool types, debug toggles) disappears from the inspector. Unity picks the most derived `[CustomEditor]` target, so this editor wins for every projectile class and `NetworkTransformEditor` keeps handling plain `NetworkTransform` components.

**How it works:** It draws a help box, then `DrawDefaultInspector()`, so every field shows. In Play Mode, for `PhysicsProjectileBase` only, it adds a runtime preview that reads `n_VisualConfigId`, looks the config up in `ProjectileRegistry` and shows its name.

**Limits:** The layout is plainer than NGO's own grouping and help boxes, because every field (NGO's included) is drawn by `DrawDefaultInspector()`. That avoids depending on NGO's editor internals, which may change between NGO versions.

### `ProjectileConfigBootstrapper.cs` (Editor)
**What it does:** `[InitializeOnLoad]` bootstrapper (runs after a `delayCall`, safe to run any number of times) that creates the default config assets a fresh install needs, but only the ones that do not exist yet:
- `Assets/MidManStudio/ProjectileSystem/Configs/DefaultProjectile.asset`
- `Assets/MidManStudio/Generated/Projectiles/ProjectileConfigProvider_ProjectileSystem.asset`
- `Assets/MidManStudio/Generated/Projectiles/ProjectileConfigGeneratorSettings.asset`

Menu: `MidManStudio > Projectile System > Internal > Recreate Default Config Assets` runs it by hand. It never overwrites an existing asset.

**Limits:** Its header comment shows an example `system.Fire((int)ProjectileConfigType.Default, ...)`. `Fire` takes a `ushort` config id, so cast to `ushort` (or use the generated id constants).

### `ProjectileConfigEntryJsonImporter.cs` (Editor)
**What it does:** Turns JSON text into `ProjectileConfigEntry` rows for a `ProjectileConfigProviderSO`. Two shapes are accepted: a list of plain names, or a list of detailed objects with `enumName`, `name`, `comment`, `description`, `explicitOffset`, `offset` and `value`. `configSO` is resolved by searching the project for a `ProjectileConfigSO` whose name matches the entry. A missing or ambiguous match leaves the field empty and logs a warning. `TryParse` reports failure without throwing.

### `ProjectileConfigJsonPanel.cs` (Editor)
**What it does:** A reusable "Apply JSON" panel (`Draw(SerializedObject)`), extracted from `ProjectileConfigScriptableObjectEditor`, that fills the fields of a config asset from pasted JSON through `ScriptableObjectJsonApplier`. `ProjectileConfigScriptableObjectEditor` itself uses it.

**Why it exists:** `ProjectileConfigScriptableObjectEditor` is registered as `[CustomEditor(typeof(ProjectileConfigSO), true)]`, so a game's own `ProjectileConfigSO` subclass already gets the default inspector (subclass fields included) plus this panel, and needs no editor of its own. A game that wants extra UI for its subclass writes a `[CustomEditor(typeof(YourConfigSO))]` for its exact type (Unity picks the more derived match) and embeds this class to keep the JSON feature. That editor cannot derive from `ProjectileConfigScriptableObjectEditor`, which is sealed. Usage inside such an editor: create one `new ProjectileConfigJsonPanel()` field, call `DrawDefaultInspector()`, then `_jsonPanel.Draw(serializedObject)`.

### `ProjectileConfigProviderEditor.cs` (Editor)
**What it does:** Inspector for `ProjectileConfigProviderSO`. It draws the default inspector and adds an "Import Entries from JSON" paste panel that calls `ProjectileConfigEntryJsonImporter`. The panel only edits the asset when the user presses the button.

### `ProjectileCustomPathPanel.cs` (Editor)
**What it does:** A plain reusable class (not itself a `[CustomEditor]`), `Draw(SerializedObject, ProjectileConfigSO)`, that edits and previews the custom path of a config: `_customPathShape`, `_customPathSplineType`, `_customPathPoints`, `_customPathFormulaX` and `_customPathFormulaY`. The preview calls `ProjectileConfigSO.EvaluateCustomPath(t)`, the same function `PhysicsProjectileBase.ApplyCustomCurve()` uses at runtime, so what the editor draws is what the projectile follows.

### `ProjectilePatternEditor.cs` (Editor)
**What it does:** Inspector for `ProjectilePatternSO` (`[CustomEditor(typeof(ProjectilePatternSO))]`).
- Formula fields with live validation, so a malformed expression is flagged while typing.
- Example dropdowns filled from `MathFormulaEvaluator.GetExamples()`.
- A "Refresh Scene Gizmo" button that redraws the pattern gizmo.
- The total burst length of the pattern.

### `ProjectileShapeEditor.cs` (Editor)
**What it does:** Inspector for `ProjectileShapeSO` (`[CustomEditor(typeof(ProjectileShapeSO))]`).
- Formula fields, a sample-count slider, and an "Insert Example" popup.
- A live canvas preview of the shape as it will be sampled.
- `RequiresConstantRepaint()` returns true, so the preview redraws every editor frame while the asset is selected (cheap, but it does keep the inspector repainting).

### `ProjectileSystemPoolProviderBootstrapper.cs` (Editor)
**What it does:** `[InitializeOnLoad]` bootstrapper for the pool type providers the projectile system needs, created under `Assets/MidManStudio/ProjectileSystem/PoolProviders` when missing:
- `ObjectPoolTypeProvider_ProjectileSystem.asset` with `Projectile_Visual2D`, `Projectile_Visual3D`, `Projectile_Flipbook`
- `ParticlePoolTypeProvider_ProjectileSystem.asset` with `Projectile_Impact`, `Projectile_Explosion_Small`, `Projectile_Explosion_Medium`, `Projectile_Explosion_Large`, `Projectile_Headshot`, `Projectile_Tracer`
- `NetworkObjectPoolTypeProvider_ProjectileSystem.asset`

Menu: `MidManStudio > Projectile System > Internal > Recreate Projectile Pool Providers`. Existing assets are not overwritten.

### `RustSimCustomShapeAuthoringEditor.cs` (Editor)
**What it does:** Scene-view editor for `RustSimCustomShapeAuthoring`. A drag handle moves a control point, Ctrl+click adds a point, Alt+click removes one. A "Rebake Now" button re-bakes the sampled curve. The control polygon draws gray and the baked curve over it.

### `ScriptableObjectJsonApplier.cs` (Editor)
**What it does:** `Apply(SerializedObject, json)` writes JSON values into the serialized properties of any `ScriptableObject`. Keys are `SerializedProperty` names, so private serialized fields keep their leading underscore (`_speed`, not `speed`). Enums are accepted by name (case-insensitive) or by index. It returns an `ApplyResult` with four lists: `applied`, `unmatchedKeys` (no property with that name), `unsupported` (the property exists but its type cannot be set from JSON) and `errors` (invalid JSON, or a top level that is not an object). Vector2 values are `{"x": 0.2, "y": 0.08}`. Not settable from JSON, and reported rather than skipped silently: asset references (`Sprite`, `Material`, `AudioClip`, `GameObject`, `ProjectileShapeSO` and the like), `AnimationCurve`, `Gradient`, and `LayerMask` bit patterns beyond a plain int. Those need manual assignment in the inspector.

### `ProjectileConfigScriptableObjectEditor.cs` (Editor)
**What it does:** Inspector for `ProjectileConfigSO` and every subclass of it.
Draws all serialized fields except the custom-path ones, then the custom path
panel (single selection only) and the Apply JSON panel. Because
`ProjectileConfigSO` derives from `MID_BaseSO` but this editor takes
precedence over `MID_BaseSOEditor`, it also does that editor's icon-cache
invalidation when the custom icon changes.

### `ProjectileSystemBenchmarkWindow.cs`
**What it does:** Editor window UI for configuring and running
`ProjectileSystemBenchmark` from inside the Unity Editor.

### `ProjectileConfigGenerator.cs` (Editor)
**What it does:** Reads every `ProjectileConfigProviderSO` in the project, gives each entry an enum value, writes
`ProjectileConfigType.cs`, fills `ProjectileConfigMappingSO` and saves the lock file. It runs from
`MidManStudio > Projectile System > Config Type Generator > Generate Now`, and from the same window's auto-generate
option when `autoGenerateOnAssetChange` is on. `ProjectileConfigMappingSO.Configs` is an array indexed by the enum
value. A slot that no entry uses (the rest of a block, the number of a removed entry) is null.

**How the numbers are chosen:** The rules are in `EnumBlockLogic` in `com.midmanstudio.utilities`, shared with the
FX and pool generators and written up in `docs/com.midmanstudio.utilities/enum-generators.md`. In short:
- Each provider owns a block of numbers. The block size is the minimum block size, rounded up so every entry and
  every pinned offset fits. Priority only decides where a provider goes the first time.
- An entry keeps its number wherever it sits in the list. Inserting an entry at the top or reordering the list does
  not move anything.
- A new entry takes the lowest free number in its block that no removed entry used.
- A removed entry keeps its number reserved. If an entry with the same name comes back, it gets the same number.
- A pinned entry (offset 0 or more) gets the block start plus the offset.
- A provider that is missing keeps its block and its numbers in the lock file.

**Lock file:** `ConfigTypeLock.json` has three lists: `entries` (`packageId`, `enumName`, `value`), `blocks`
(`packageId`, `start`, `size`) and `retired` (the same fields as `entries`, for removed entries). A lock file written
before this change only has `entries`. The blocks are then worked out from where the entries sit, so the first run
does not move anything and writes the real blocks. An older version of the package that reads a new lock file
ignores the extra lists. Commit the file.

**Decisions:**
- The generator calls `EnumBlockLogic.Assign` instead of keeping its own copy of the block code. The package
  already depends on `com.midmanstudio.utilities`, and its editor assembly already references
  `MidManStudio.Utilities.Editor`.
- The lock file keeps the field name `enumName`, so lock files that are already in projects read without a change.
  The shared logic uses `name`, and two small conversions in `ConfigLockFile` map between them.
- The numbers for all providers are worked out before a file is written. An error (a name that is not a valid C#
  member name, a duplicate name, a duplicate package id) leaves the enum file, the mapping asset and the lock file
  as they were.
- A lock file that cannot be read stops the run with an error. Starting over would renumber every projectile config,
  and the numbers are stored in the mapping asset and in scenes and prefabs. Deleting the lock file lets all numbers
  be assigned again.
- An entry with no `enumName` and a config asset takes the asset name with characters that are not allowed replaced
  by `_`. An entry with neither is skipped. Two entries that end up with the same name are an error.
- A comment is written on one line. A line break inside a comment used to leave the second line outside the `//`
  and the enum file did not compile.
- A failure while writing the enum file, the mapping asset or the lock file is reported as an error in the result,
  and the lock file is only written after the other two succeeded.
- The auto-generate hook writes the warnings of a run to the console as warnings. It only logged errors before. A
  warning can mean that a number changed.

**Limits:** The auto-generate hook runs when a provider asset is imported or moved. A provider asset that is deleted
does not start a run. Generate by hand after deleting one. The numbers of that provider stay in the lock file.

**Checked outside Unity:** The file was compiled against hand-written Unity stand-ins and run on real files. The
real `ConfigTypeLock.json` from `PackageSandbox` (12 entries, old format, with a byte order mark) gave no changed
number and no warning, a second run changed nothing, and a new entry at the top of the provider took 12. The
insert-first, reorder, remove and add back, corrupt lock, bad name, duplicate name, write failure, and block growth
cases all behaved as described. A fresh run of the old and the new generator on the same providers gave the same
mapping and the same lock entries, and enum files that match line for line apart from the timestamp and the
comment with a line break. Six deliberate breakages of the new code (forgetting the retired numbers, forgetting
the block ranges, ignoring the lock file, not saving the lock file, filling the mapping in list order, treating an
unreadable lock file as a warning) each made at least one check fail. **Not checked in Unity:** `JsonUtility`
reading and writing the new lists, the generator window and the auto-generate hook. The stand-in for `JsonUtility`
is not the real one.

### `TestTarget.cs`
**What it does:** A shootable dummy target for the 3D dimension that implements `IDamageable`. It is registered with `TestSceneBootstrapper` by a `RegistrationId` so hits can be routed back generically.

### `TestTarget2D.cs`
**What it does:** The 2D counterpart of `TestTarget`: a `NetworkBehaviour` with a `CircleCollider2D` (the only physics component allowed on its prefab; no `Rigidbody`, `SphereCollider` or any 3D physics component). It has a health value (`_maxHealth` 100) and a respawn delay (`_respawnDelay` 3 s), implements `IDamageable`, and is registered with `TestSceneBootstrapper` by a `RegistrationId`.

### `WeaponController.cs`
**What it does:** Shoot-mode dispatch (LocalOnly/RustSim/Raycast/Physics,
2D and 3D), shot pattern and spread, config-id resolution, the actual
fire/raycast/physics calls into `MID_MasterProjectileSystem`, and a weapon
inventory (pickup, ownership, switching). A switch plays a procedural
animation on the weapon holder (`_weaponSocket`): it dips down and tilts,
the model is swapped at the bottom, then it returns to the holder's authored
rest pose. No Animator is involved. `SwapModel()` instantiates the weapon
model under the holder, applies `_modelRotationOffsetEuler` (a local
rotation correction for however this asset pack's models are authored
relative to the holder), calls `WeaponRef.EnsureOn()` on the instance, and
points the player's 3D shot point at the resulting `ShotPoint`. Firing is
locked out for the length of the switch animation.

**Decisions:**
- Reads weapon tuning (fire rate, pellets, spread, config type ids, raycast
  and physics settings, audio, muzzle flash) from whichever
  `WeaponDefinitionSO` is currently equipped, rather than from local
  inspector fields the way `NetworkedDimensionPlayer` used to.
- `SetShootMode()` is the one place `_shootMode` is assigned. Every call site
  used to assign the field directly; centralizing it means every assignment
  also calls `_player.ReportWeaponUses3DConvention()`, keeping the player's
  aim-convention rig in sync with this component's own shoot mode.
- `[RequireComponent(typeof(NetworkedDimensionPlayer))]` was added. Most of
  this class calls `_player` members with no null check, so a missing player
  component on the GameObject would previously throw at runtime instead of
  being caught by the editor when the component is added.
- `PlayerShootMode` is declared in this file rather than
  `NetworkedDimensionPlayer.cs`, since shoot mode is now entirely this
  class's concern.

### `WeaponRef.cs`
**What it does:** Marker component for a weapon model, holding a single
`ShotPoint` transform at the barrel tip. `WeaponRef.EnsureOn()` is called by
`WeaponController.SwapModel()` on every instantiated model: it adds a
`WeaponRef` if the prefab has none, keeps a `ShotPoint` the prefab already
assigns, and otherwise fills it with the utilities package's
`MID_AutoReferenceResolver` (the class is `[MID_AutoRefable]`). If the
resolver result scores below `_shotPointMinMatchScore`, or only the model root
matched, a short list of common muzzle names (`ShotPoint`, `Muzzle`,
`FirePoint`, `BarrelEnd`, `BulletSpawn` and similar) is tried against the
model's children. If nothing matches, `ShotPoint` stays null, a warning is
logged, and the player's own default shot point is used.

**Decisions:**
- The resolver matches on the field name, so it only scores names close to
  `ShotPoint`. Measured against the real `MID_NameMatcher`: `ShotPoint` and
  `Shot_Point` 1.00, `ShotPoint (1)` 0.89, `ShootPoint` 0.52, `FirePoint`
  0.37, `Muzzle` 0.00. `Muzzle` is a very common name in weapon asset packs,
  which is why the name-list fallback exists.
- A resolver pick that is the model root itself is rejected. With a
  single-transform model the resolver reports the root as the only candidate
  with a perfect score, which would put the shot point at the model's pivot.

### `PlayerHealth.cs`
**What it does:** PvP health, damage, death, and respawn for players.
Mirrors `TestTarget.cs`'s pattern (server-owned NetworkVariable health, hit
flash, death/respawn coroutine, ClientRpc-driven visuals) without the
offline branch, since PvP inherently needs networking.

**Decisions:**
- Routed to generically through `TestSceneBootstrapper.ApplyHit()`'s
  `IDamageable` branch rather than registering itself anywhere.
- Falls back to `NetworkedDimensionPlayer.MeshRenderers` for the hit flash
  when `_bodyRenderers` is left empty, and hands tint control back to
  `NetworkedDimensionPlayer.RefreshTint()` once the flash coroutine ends,
  rather than guessing a default color.

### `IDamageable.cs`
**What it does:** Interface for anything `TestSceneBootstrapper.ApplyHit()`
can route a hit to: `IsAlive` and `TakeDamage(amount, attackerClientId)`.
Implemented by `PlayerHealth`, `TestTarget`, and `TestTarget2D`.

### `TestSceneBootstrapper.cs`
**What it does:** Scene entry point: lobby/session routing, pool
initialisation, projectile config registration (manual list and/or enum-based
`ProjectileConfigManager`), player and target spawning, and generic hit
routing via `IDamageable`.

**Decisions:**
- Subscribes to `OnGameStartReceived` and shows the lobby UI before running
  config registration, since registration can fail independently of lobby
  state (see Fixes and Problems).

### `WeaponPickup.cs`
**What it does:** Trigger volume that grants a `WeaponDefinitionSO` to
whichever player's `WeaponController` overlaps it, via
`WeaponController.PickupWeapon()`.

### `DimensionManager.cs`
**What it does:** Scene singleton (`Singleton<DimensionManager>`) tracking
the current `Dimension` (TwoD/ThreeD), whether a switch is in progress, and
firing `OnDimensionChanged` for subscribers.

### `DimensionCameraController.cs`
**What it does:** Registers and unregisters the local player's follow camera
targets for the 2D and 3D dimensions as players spawn and despawn.

### `MID_TouchJoystick.cs`
**What it does:** On-screen joystick for mobile movement input. Exposes
`Value` (Vector2) and `IsHeld`, read by `NetworkedDimensionPlayer.GetMoveAxes()`.

### `MID_TouchShootButton.cs`
**What it does:** On-screen shoot button for mobile fire input. Exposes
`IsPressed` and `Pressed`/`Released` events, read by `WeaponController.HandleFire()`.

### `ProjectileTestLobbyUI.cs`
**What it does:** Lobby screen wiring: player list, ready/start controls, and
network status text.

**Decisions:**
- `FriendlyStatus()` maps raw network-status strings to plain ASCII display
  text (see Fixes and Problems).

### `LobbyEntryCard.cs`
**What it does:** UI card for one entry in the lobby room list.

### `LocalLobbyData.cs`
**What it does:** Small local data holder backing the lobby UI.

### `PlayerEntryCard.cs`
**What it does:** UI card representing one player inside the lobby room
panel (name, role, ready state, ping, host/bot indicators).

**Decisions:**
- Ready-state text is a plain ASCII string ("READY"/"WAITING"), not a
  Unicode checkmark or ellipsis (see Fixes and Problems).

### `ProjectileSystemBenchmark.cs`
**What it does:** Runtime benchmark harness for the projectile system
(simulation tick and firing throughput under load).

**Benchmarks:** This file is the benchmark harness itself; there is no
separate bench data file. Run it through `ProjectileSystemBenchmarkWindow`
(Editor) rather than as part of any automated CI job.

## CI and Workflows

None of the workflows below run the `Tests/` scene or any automated test suite. `build.yml` builds the demo app for Android and Windows only.

- `.github/workflows/build-projectile-rust-libs.yml` (`Build Rust Native Projectile Libs`): on a push to `master` that touches `rust_lib/projectile_core/**`, or manually. It builds the native library for macOS (universal x86_64 and arm64 dylib), Windows x64 (cross-compiled with mingw on Linux), Linux x64, Android (`arm64-v8a`, `armeabi-v7a`, `x86_64`, through `cargo-ndk`, with a step that verifies each ABI output to catch stub or broken builds), iOS (device static library and a simulator one) and WebGL (a static library for Emscripten). The `commit-to-repo` job downloads every artifact, copies the libraries into `packages/com.midmanstudio.projectilesystem/Runtime/Plugins/Native`, generates or validates the `.meta` files, and commits the result. It runs with `if: always()`, so it also runs when one platform job failed. A missing `arm64-v8a` Android library is treated as a hard failure in the script, because every modern Android device needs it.
- `.github/workflows/bench-projectile.yml` (`Projectile Core - Benchmarks`): Criterion benchmarks for `projectile_core` (simulation tick and collision). It posts a markdown table to the step summary and commits `.github/bench-results/projectile-bench-results.json` and `index.html`. Its push trigger is `branches: [main]`, but this repository's default branch is `master`, so the push trigger never fires and the workflow runs only when dispatched manually.
- `.github/workflows/release-packages.yml`: tags and releases a package from `packages/<package>` on a `<package>/v<version>` tag (for this package, `projectilesystem/v1.0.0`) or by manual dispatch. The tag has to equal `<package>/v<version from package.json>`, and the release notes are taken from the `## <version>` section of the package's `CHANGELOG.md`.
- `.github/workflows/apply-replacements.yml` and `apply-patch.yml` are the delivery pipeline of the repository (they apply files and replacements committed to it), not part of the package build.

## Fixes and Problems

### `ProjectileLib.cs`
- The Arching movement type was removed. The native `tick_arching` and
  `tick_arching_3d` were identical to the straight ticks, because gravity already
  comes from a nonzero `Ay` set at spawn. An arc was never a second mode.
  Value 1 stays unassigned instead of being reused, so a config that still stores
  it reaches the native default arm and runs the straight tick, which is what it
  always computed.
- A missing or misconfigured per-architecture library threw `DllNotFoundException`
  from the first extern call, including from call sites that never ran
  `ValidateStructSizes`. Escaping from `Awake` or a coroutine, it stopped
  everything queued after it (seen in `TestSceneBootstrapper.Start()`).
  `IsAvailable` now probes once and caches the result, callers check it before
  other extern calls, and `ValidateStructSizes()` throws `InvalidOperationException`
  for a missing library, so existing `catch (InvalidOperationException)` sites
  cover that case too.

### `ProjectileManager.cs`
- The comments listed `ProjectileRenderer3D` among the components that require this
  marker. Only `ProjectileRenderer2D` and `TrailObjectPool` do.

### `RustSpawnParams.cs`
- The field comments described `ScaleStart` and `ScaleTarget` as fractions and the
  growth speed as a lerp speed. `ProjectileConfigSO` fills them with world-unit
  sizes, and the native tick approaches the target by `diff * speed * dt`. The
  comments were corrected.

### `SimulationMode.cs`
- The 3D value (2, `RustSim3D`) was removed because `Is3D` on the config selects the
  buffer. Value 1 kept the number it had under its earlier name, so existing
  config assets keep their meaning. An asset that stored 2 shows "(2)".

### `MID_AllProjectileNames.cs`
- The header comment was a copy of the `INetworkProjectileVisual` header and did not
  describe the enum. It was replaced.

### `BatchSpawnHelper.cs`
- Wave and Circular projectiles used to spawn with `(Ax, Ay) = (0, GravityAy)`.
  They now get the perpendicular of the fire direction, which is what the native
  Wave and Circular ticks read as their axis.
- Latency compensation used to add `velocity * latencyCompensation` to the
  position before the projectile went into the buffer. The host renders from that
  buffer, so it saw the bullet ahead of the barrel. It now only reduces `Lifetime`.
- The unused `MOVE_ARCHING` constant was removed along with the Arching type.

### `RustSimAdapter.cs`
- Projectiles with `PiercingType.None` were never unregistered after a hit, so
  `Alive` was never cleared in the Rust buffer and they lived on regardless of the
  piercing setting. `HandlePiercing` now unregisters them at once.
- The `NotifyDead` and `HandlePiercing` comments named `CompactDeadSlots` and
  `Collision2D` only. They now name `CompactDead2D`, `CompactDead3D`,
  `Collision2D` and `Collision3D`.

### `MathFormulaEvaluator.cs`
- The grammar comment called `^` right-associative with a repeat. The parser
  accepts one `^` per level, and a second one in a row fails. The comment now says
  so. The "used by" list also left out `ProjectileConfigSO.EvaluateCustomPath`.

### `ProjectileConfigManager.cs`
- `RegisterAll()` used to run in `Start()`, with the same ordering race as the
  registry. It now runs in `Awake()` at order -150, right after the registry.
- The `RegisterAll` comment said the assigned config id matches the enum value.
  That only holds when nothing else registered first and there are no padding
  slots, so the comment now says to translate through `GetConfigId`.
- The usage comment showed `system.Fire(ProjectileConfigType.FireBall, ...)`, which
  does not compile because the extension takes an `int`. It now shows the cast.

### `ProjectileConfigSO.cs`
- RustSim projectiles are drawn with `Graphics.DrawMeshInstanced`, which has no
  sorting layer or order, so there was no way to order projectiles against 2D
  sprites. `SortingLayerName`, `SortingOrderInLayer` and `SortingPriority` were
  added. `ProjectileRenderer2D` turns the priority into a Z offset, and
  `TrailObjectPool` sets the real `TrailRenderer` sorting values.
- `RegisterMovementParams()` used to throw `DllNotFoundException` when the native
  library was missing. Called from a coroutine such as
  `TestSceneBootstrapper.Start()`, that aborted everything queued after it,
  including lobby event subscriptions that never use the native library. It now
  checks `ProjectileLib.IsAvailable`, logs a warning and returns.
- The comments on the Wave and Circular accessors said `DeterministicMotionMath`
  reads them. `PhysicsProjectileBase` reads them and passes the values on.

### `ProjectilePatternRegistry.cs`
- Pattern ids used to be the position in the `_autoRegister` array, which nothing
  keeps the same between builds or processes. Reordering the list, inserting a
  pattern in the middle, or running a host and client with different scene setups
  made one side resolve an id to a different asset. Ids are now a stable FNV-1a
  hash of the asset name, with 0 kept as the "no pattern" value. A renamed asset
  gets a new id.
- The hash comment said the range was 1 to 65535. The fold produces 1 to 65534, and
  the comment now says so.

### `ProjectilePatternSO.cs`
- The `Star` shape's point count cancels out of its angle formula, so it returns
  the same layout as `Ring360`. This is recorded as a limit and the code is
  unchanged.
- The `Formula` section header and the `MixSeed` comment carried "NEW" and "old
  fixed layout" wording from earlier edits. Both were reworded.

### `ProjectileRegistry.cs`
- The first shot of a session could use the wrong config. `ProjectileConfigSO.ConfigId`
  is only assigned inside `Register()`, and nothing guaranteed when that ran
  relative to other scripts. A weapon script that read `ConfigId` directly before
  registration got 0, which is whichever config sits at index 0. Later shots read
  the field again and got the right id. `[DefaultExecutionOrder(-200)]` now makes
  the registry's `Awake` run before every other script's `Awake`, and therefore
  before any `Start`.
- Physics projectile visuals showed the wrong texture for the first few shots after
  the sprite atlases moved to the `.spriteatlasv2` format. Unity resolves a packed
  sprite's `.texture` lazily, and the visuals only corrected themselves once the
  atlas had caught up. `Register` now touches `ProjectileSprite.texture` once, so
  that cost is paid at scene load. This does not speed up the resolution, it only
  moves it. `ProjectileVisual_2D._forceSpriteRendererOnly` avoids the window
  entirely for 2D configs that need no custom shape mesh.
- `RegisterMovementParams()` is wrapped in a try/catch so a failure for one config
  cannot stop the rest of a batch from registering.
- The class comment on `ProjectileRegistry` and the `UVRect` comment named
  `ProjectileRenderer2D` as a caller of `GetUVRect`. It computes its own rect. The
  `TryGetId` summary claimed a `ushort.MaxValue` return that the `bool` method does
  not have. Both comments were corrected.

### `ProjectileShapeSO.cs`
- The `Formula` preset was added at the end of the enum on purpose, because the
  enum is stored as an int in existing assets. The comment now explains that
  instead of saying "NEW".
- The comment on the formula accessors said the shape editor uses them. It reads
  the fields through `SerializedProperty`, so the comment was corrected.

### `WeaponDefinitionSO.cs`
- The `SwitchAnimTrigger` tooltip described a trigger that is no longer fired.
  It now says the field is unused.
- The class comment described the file in terms of the player script it was
  extracted from, and said nothing about who reads it. It now states that only the
  test and sample scripts read it.

### `ClientPredictionManager.cs`
- The firing client used to match the real, server-confirmed physics object to its
  predicted visual by spawn position (`OnRealPhysicsProjectileSpawned`). That fixed
  the wrong problem: the firing client should never see the real object. The server
  now spawns it server-owned and hides it from that client (see
  `MID_MasterProjectileSystem.SpawnPhysicsProjectile`), so the predicted visual
  lives out its own lifetime and glides to the confirmed hit point. The old method
  stays as an obsolete diagnostic that logs with `Debug.LogError`.
- The Rust sim prediction path was removed from this class (`SpawnImmediatePrediction`,
  `OnSpawnConfirmed`, `ReconcileSnapshot`, `PredictedProjectile` and the circular
  buffer). `LocalProjectileManager` replaced it: `SpawnFiringClientBatch*` for the
  temporary shot, `LinkNetworkProjectileBatch` for id linking and
  `ReconcileSnapshots2D/3D` for position correction. The header comment listed the
  removed members and now describes what remains.
- Recorded as problems, code unchanged: the stored player id is never read, the
  per-frame `List` allocation, and the best-effort hit match.

### `DeterministicMotionMath.cs`
- The header listed users that do not exist and said every formula matches Rust.
  It now names the real caller (`PhysicsProjectileBase`) and states which
  functions are unused.
- **Open problem, code unchanged:** `CalculateWave2DVelocityDirection` and
  `CalculateWave3DVelocityDirection` use `-amplitude * a * sin(a t + phase)`, while
  Rust's `tick_wave` and `tick_wave_3d` apply `amplitude * sin(a t + phase)`, and
  the position functions integrate that second form. Physics wave projectiles
  therefore swing `a` (2 pi times the frequency) times wider than the Rust ones and
  start toward the opposite side. The fix is one line in each function:
  `float waveVel = amplitude * Mathf.Sin(a * timeAlive + phaseOffset);`. It is held
  back until it is approved, because it changes how existing physics wave
  projectiles move.

### `INetworkProjectileVisual.cs`
- The comment now states that nothing in the package implements the interface, and
  that `NetworkProjectileBase` never calls `Initialize`.

### `MID_ProjectileNetworkBridge.cs`
- **Receive queue overflow.** Per-client culled snapshot dispatch sends many small
  RPCs per tick, and the transport's default packet-count queue logged "Receive
  queue is full, some packets could be dropped". `ConfigureTransportForHighThroughput`
  now also sets `MaxPacketQueueSize` to 2048, a different setting from
  `MaxSendQueueSize`.
- **Physics projectiles showed the wrong sprite.** `FirePhysicsProjectileServerRpc`
  had no config id, so every physics projectile used the prefab's inspector value
  (default 0). The RPC now carries `configId`, and `SpawnPhysicsProjectile` pushes it
  into `PhysicsProjectileBase.SetVisualConfigId`.
- **Physics projectiles fired twice on the client.** The first fix spawned the
  object with the shooter as owner and matched it to the predicted visual. The final
  fix spawns it server-owned, passes the sender's client id (`firingClientId`) and
  hides the object from that client with `NetworkHide`.
- **Pattern and spread for physics and raycast fire.** The physics RPC gained
  `patternId`, `spreadCount`, `spreadDeg` and `patternIs3D`. Raycast fire gained
  `RaycastPatternFireServerRpc`, which casts every ray on the server. Before that,
  raycast fire could only cast one ray.
- **Guided targets never reached the server.** `PhysicsProjectileBase.SetGuidedTarget`
  worked, but nothing called it over the network. The physics RPC now carries
  `targetNetworkObjectId` and calls `SetGuidedTarget` after `InitialiseProjectile`
  (that call chain resets the guided target). The Rust sim path got the same plumbing
  through `ProjectileFireRequest.TargetNetworkObjectId` and
  `ProjectileGuidanceTracker`.
- **Sender and other clients are routed differently.** The shooter already runs the
  shot under temporary ids, so it gets `LinkProjectileIdsClientRpc` (swap the ids,
  spawn nothing) and only the other clients get `SpawnConfirmedClientRpc`.
- **Culling did not cull shots.** Distance culling first applied only to periodic
  snapshots, so every shot still reached every client at fire time and kept running
  from there. The spawn RPC is now filtered by distance too.
- **Per-pellet directions were trusted.** The server used to build spawn points from
  directions sent by the client (`BuildServerSpawnPoints`, `ExtraDirections`,
  `GetDirection(i)`). Directions are now rebuilt with `ProjectileDirectionResolver`
  from the pattern id, count, spread and fire seed.
- **Spawns and snapshots are unreliable.** Both are cosmetic or replaced by the next
  packet, so reliable delivery would only cost bandwidth. The link RPC and the hit
  confirmation stay reliable.
- **Snapshot extrapolation.** `SendSnapshotClientRpc` passes the current server tick
  and the tick interval so a stale snapshot is extrapolated forward before it is
  compared with the local position.
- **Wire sizes.** Earlier notes quoted 46 bytes for the fire request and 41 for the
  confirmation. Those predate the 8 byte guided target and the 2 byte fire seed. The
  sizes are now 56 and 43 bytes.
- Recorded as problems, code unchanged (details under Limits and trust in the
  module section): client-supplied owner, damage multiplier, weapon level, fire tick
  and origin are accepted as sent; the physics RPC is not clamped or capped; no RPC
  is rate limited; a NaN speed passes the clamp; `OnDestroy` does not call the base
  implementation; the stored player id is never read.

### `NetworkProjectileBase.cs`
- `Update` always calls `base.Update()`. `NetworkTransform` needs it on every peer
  to detect changes to send and to advance the interpolator on the receiving peers.
  Only the lifetime check is server only.
- `ShouldAutoSpawnVisual` exists so a subclass that manages its own visual
  (`PhysicsProjectileBase`) does not get a second, unparented visual from the base
  class.
- Recorded as problems, code unchanged: `EolPositionDelta`, `_networkPoolType` and
  `ReturnToNGOPool` do nothing, and four replicated values have no reader in the
  package.

### `ProjectileDirectionResolver.cs`
- `Resolve` used to ignore the shot, so a random pattern or a speed variance gave
  the same result on every shot. It now takes a 16-bit fire seed, picked once by the
  firing side and sent with the request. The argument is optional, so existing
  callers compile unchanged and keep the fixed layout until they pass a seed.
- Directions were once taken from the client. They are now always rebuilt from the
  registered pattern or the spread values, on the server and on every other client.
- Recorded as limits, code unchanged: the plain spread ignores the seed and yaws
  about the world Y axis in 3D, and raycast pattern fire and physics fire pass no
  seed.

### `WireCompression.cs`
- The position comment now says that half-precision error grows with distance from
  the origin (0.0625 at 128 to 256 units, 0.125 at 256 to 512). It also corrects a
  claim that the snapshot structs call this class. They apply the same half
  conversion themselves.
- Recorded as a problem, code unchanged: coordinates beyond about 256 units lose
  more precision than the 0.08 reconciliation band, so large worlds need a
  different encoding or a floating origin.

### `NetworkedDimensionPlayer.cs`
- `PlayerHealth` and `WeaponController` referenced `MeshRenderers`,
  `SetControlAndVisibilityEnabled()`, `RefreshTint()`, `ControlEnabled`, and
  `CurrentDimension`, none of which existed on this class, and called
  `ResolveShotPoint()`/`ResolveFireDir()`/`Use3DConvention()`, which existed
  but were private. This broke the build. The underlying cause was an
  unfinished split: `WeaponController` was written to hold the fire/weapon
  logic that used to live here, but this class's own copy of that logic was
  never removed. Fixed by deleting the old fire/shoot-mode code from this
  class (an equivalent, already-working copy exists on `WeaponController`)
  and adding the small public surface the two sibling components need.
- Because both classes still had their own complete firing pipeline, this
  class's `Update()` was unconditionally calling its own `HandleFire()` and
  its own Alpha1-7 shoot-mode switching every frame, identical to what
  `WeaponController.Update()` does. Simply widening the private members to
  public (without removing the duplicate logic) would have made the project
  compile again while causing every shot to fire twice and the two
  components' shoot-mode state to drift apart, since each tracked its own
  `_shootMode`/`_netShootMode`. Removing the duplicate logic instead of
  patching access levels avoided this.
- `Use3DConvention()` used to also read true for a 3D-convention shoot mode
  (RustSim3D/Raycast3D/Physics3D) regardless of dimension, since shoot mode
  lived on this class. `ResolveShotPoint()`/`ResolveFireDir()`/the cursor
  lock logic in `WeaponController.ChangeMode()` all depend on that combined
  meaning. Since shoot mode now lives entirely on `WeaponController`, added
  `ReportWeaponUses3DConvention(bool)`, called from
  `WeaponController.SetShootMode()` on every shoot-mode change, so the
  combined behavior is unchanged.
- `HandleMovement()` branches on the Rigidbody's actual kinematic state,
  not `Use3DConvention()`. `Use3DConvention()` reads true for a 3D shoot
  mode even in the 2D dimension, and checking it directly in the movement
  branch used to throw "Setting linear velocity of a kinematic body is not
  supported" every `FixedUpdate` for that combination, since the rigidbody
  was still kinematic from the 2D dimension's constraints. The exception
  aborted the rest of the method, so movement stopped dead for whoever hit
  that combination. The dash branch already checked `_rb.isKinematic`
  directly for the same reason; movement was written to match.
- `OnCollisionStay` set `_grounded = true` for any collision at all, wall or
  ceiling included, with no check on the contact normal. Jumping into a wall
  could therefore read as grounded, and `HandleMovement()`'s jump check read
  `Input.GetButton("Jump")` (held-down, true every `FixedUpdate` the key
  stayed down) rather than a single press — holding Jump while pressed
  against a wall applied the impulse again every physics tick, stacking
  velocity that only became visible once the player broke free of the wall,
  launching far higher than a normal jump. Fixed by checking each contact's
  normal (`normal.y > 0.5f` counts as ground) and by capturing the press as
  a single-frame edge in `Update()` (`_jumpQueued`, where `GetButtonDown` is
  reliable) consumed exactly once in the next `HandleMovement()`.
- The owner's own Rigidbody was never given `RigidbodyInterpolation
  .Interpolate` — only the remote/non-owner branch of `OnNetworkSpawn` set
  it. Movement and turning, both applied in `FixedUpdate` via
  `MoveRotation`/velocity, only visibly updated at the physics tick rate,
  which reads as stepped/choppy on any display faster than that — most
  noticeable while turning, and on anything rigidly parented to the player,
  such as a held weapon. Added the same interpolation setting to the owner
  branch.
- `Set3DShotpoint(Transform)` assigned `_shotPoint3D` unconditionally,
  including `null`. `WeaponController.EquipLocal()` needs to reset to this
  rig's own default point when the newly equipped weapon has no model or no
  `WeaponRef`, and passing null previously would have left `_shotPoint3D`
  itself null (a NullReferenceException wherever it's later dereferenced)
  rather than falling back. `EnsureShotPoints()` now also records whatever
  `_shotPoint3D` resolves to (inspector-assigned or auto-created) as
  `_defaultShotPoint3D`, and `Set3DShotpoint(null)` falls back to it instead
  of accepting null directly.

### `DelayedSpawnQueue.cs`
- Burst fire was added: a queued projectile spawns when its start delay is due. See the module section for the known limits.

### `LocalProjectileManager.cs`
- A client's own shots wiggled. The server sends one shared snapshot array to every client, and the client kept nudging its own, already correct shots toward a separately computed extrapolation. Ids fired by this client are now remembered (`_locallyOwnedProjIds`, filled in `LinkNetworkProjectileBatch`, cleared in `KillNetworkProjectile`) and skipped by `ReconcileSnapshots2D/3D`.
- Trail streaks and a slow slot leak on continuous fan fire: the temp-to-real id swap left `TrailObjectPool` keyed by the old id. `LinkNetworkProjectileBatch` now calls `TrailObjectPool.RelinkProjectileId`.
- Snapshot reconciliation compared the stale snapshot position with the current local position, so a normal straight flight triggered a correction on every snapshot (zig-zag). The snapshot is now extrapolated to the current time first, and only genuine discrepancies are corrected.
- `Guided` configs had no way to get a target in the local path. `Spawn2D/3D` take an optional `guidedTarget` and register the batch with `ProjectileGuidanceTracker` for Guided configs.
- `SetAcceleration2D/3D` and `TryGetPosition2D/3D` were added so `MID_MasterProjectileSystem.SetHomingDirection2D/3D` also works in a non-networked game. Before that the calls went only to the authority and were a silent no-op offline.
- The movement type constant for Arching (value 1) was removed. `Straight` carries the constant `Ay` for an arc.
- The late-spawn catch-up code existed twice, inline in `SpawnNetworkBatch2D` and `SpawnNetworkBatch3D`. It moved into `ApplyCatchUp2D` and `ApplyCatchUp3D` with the same arithmetic.
- Added read-only `TargetCount2D/3D` and `ShapeCount2D/3D` properties.

### `MID_MasterProjectileSystem.cs`
- Physics projectiles dealt no damage: the physics hit was never relayed to the system. `OnPhysicsHit` now relays it to the same hit event the other simulation modes use.
- `SpawnPhysicsProjectile` could not choose which config the physics body used. It now takes a `configId` parameter.
- The firing client saw its own physics shot twice, once from its prediction and once from the server's networked object. The server-owned body is now hidden from the firing client with `NetworkHide` (the host is exempt) and `firingClientId` is passed in. How `NetworkHide` behaves when called right after `Spawn` is NGO behaviour that has not been verified here.
- The raycast fire RPC send is wrapped in a try/catch so a send failure no longer stops the offline path, and `OfflineHandleFire` runs outside the try block. The failure is logged with a `[RAYDIAG]` prefix. The root cause of the original failure was not recorded; this is a defensive change.
- Raycast pattern fire had no client prediction. `ClientPredictPatternLocal` now predicts it.
- `FireNetworkedSim` took its base direction from `spawnPoints[0].Direction`. It now uses `baseDirection`.
- Targets were also registered in the local manager while networked, so they were counted twice. Registration is skipped when networked.
- `SetHomingDirection` did nothing offline because of a combined condition. The condition is split so the offline path works.
- The `GameObject` and `Component` register overloads exist so the real Unity layer is used. The plain overload defaults to layer 0, which silently breaks `HitLayers` for any object not on layer 0.
- A native library that fails to load is now caught: `ValidateStructSizes` throws `InvalidOperationException` and the system reports it instead of failing later.
- The guided target is passed through `Fire`, so a Guided projectile starts homing at spawn.
- `OnSystemReady` and `IsReady` were added so components that start early (target registrars) can wait for the system, or register at once when it is already up.
- `OnRustSimHit` was added, so game code can react to RustSim hits without subscribing to a backend.
- `Log System Status` reports `CanAcceptTargets` and the backend counts.

### `PhysicsProjectile2D.cs`
- Gravity: the config's `GravityScale` replaces the inspector value, and `Wave`, `Circular` and `Teleport` force it to 0, because the base class drives those movement types directly and the native ticks do not combine with gravity. `Guided` keeps gravity on purpose.
- The collider is now sized from the config through `ApplyColliderSize`. The prefab used to keep its authored size for every config.
- Recorded as a problem, code unchanged: the tooltip about the config overriding the gravity scale is on `_drag`, not `_gravityScale`.

### `PhysicsProjectile3D.cs`
- Gravity: the inspector `_useGravity` used to be the only control. A config with `GravityScale > 0` now turns gravity on, and `Wave`, `Circular` and `Teleport` force it off, for the same reason as in 2D.
- The collider is now sized from the config through `ApplyColliderSize`.
- Recorded as a problem, code unchanged: the config's `GravityScale` is only an on and off switch in 3D.

### `PhysicsProjectileBase.cs`
- The config id was a plain field, so only the host showed the right visual: a plain C# field written on the server instance never reaches remote clients. It is now the NetworkVariable `n_VisualConfigId` (server writes, everyone reads), and `HandleVisualConfigChanged` re-applies the visual and the config-dependent state on every client when the value arrives, including the first delta after spawn.
- Nothing told a spawned instance which config it represented, so every instance used the `ProjectileConfigSO` registered under id 0 (the prefab default), or a placeholder sprite. `SetVisualConfigId(ushort)` was added and `MID_MasterProjectileSystem.SpawnPhysicsProjectile` calls it.
- `OnNetworkSpawn` used to reconcile the firing client's own predicted visual with the real projectile (an `IsOwner` check plus `ClientPredictionManager.OnRealPhysicsProjectileSpawned`). That solved the wrong problem. `MID_MasterProjectileSystem.SpawnPhysicsProjectile` now `NetworkHide`s the object from the firing client (the host is exempt), whose local prediction ghost lives out its own `MaxLifetime` and glides to the confirmed hit through `HitConfirmedClientRpc`. The class does nothing owner-specific on spawn.
- `MaxRange` was only used for damage falloff, so nothing stopped a physics projectile past that distance (a raycast is bounded by its own distance and a native projectile by the native max-range cutoff). With low drag and no gravity it could fly until `TimeToLive`. `Update` now ends the shot on the server once the straight-line distance from the spawn position reaches `MaxRange`.
- The movement types did not work for physics projectiles ("it should offset the physics body since the visual is meant to follow the physics body"). `FixedUpdate` now drives the Rigidbody velocity for `Wave` and `Circular` from `DeterministicMotionMath`, so collision detection is unaffected.
- `Guided` had no way to set a target. `MID_MasterProjectileSystem.SetHomingDirection2D/3D` only wrote a direction once for native projectiles, and nothing tracked a target over time, and nothing existed for physics projectiles. `SetGuidedTarget(Transform)`, `SetGuidedTarget(Vector3)` and `ClearGuidedTarget()` plus `ApplyGuided` were added. The turn rate (pi rad/s) is the hard-coded rate of `tick_guided`, not a new value.
- `Teleport` ("shifts to a specific forward per tick") was not available for physics projectiles. `ApplyTeleport` ports `tick_teleport` (0.12 s interval, velocity zero between hops). It does not sweep, which is the same as the native tick.
- `CustomCurve` path movement: the path is authored as points or formulas (the same way `ProjectilePatternSO` authors spawn patterns), not as an `AnimationCurve`; only the pacing (`CustomCurveSpeedMultiplier`) stayed a curve. The earlier AnimationCurve-only version scaled the instantaneous velocity by the curve, which does not preserve the total distance covered and could miss the endpoint. The position is now recomputed from elapsed time each tick and the speed curve is a normalised lookup table, so the last path point is reached when the duration ends.
- A `CustomCurve` path formula that divides by zero or takes the root of a negative number gives a non-finite position, and `ApplyCustomCurve()` passed the resulting NaN or infinite velocity to the Rigidbody, which is an engine error and leaves the projectile unusable. The update is now skipped when the velocity is not finite, with a one-time warning per launch that names the config. This was added as hardening; it is not confirmed to be the cause of any reported symptom.
- `TimeToLive` was whatever fixed value the prefab inspector held (8 unless overridden), completely decoupled from `ProjectileConfigSO.Lifetime`. `NetworkProjectileBase` does not know `ProjectileConfigSO`, and nothing else in the package referenced `TimeToLive`, so nothing bridged the two. The lifetime watchdog itself was correct and just measured the wrong duration. `ApplyConfigLifetime` now sets `TimeToLive = cfg.Lifetime`, which has to happen before `InitialiseProjectile` reads it.
- `ProjectileConfigSO.HitLayers` was not used by physics projectiles. Direct hits registered against any collider, and the explosion overlap used whatever `_damageLayerMask` the prefab had (-1). `_damageLayerMask` is now set from `cfg.HitLayers`, and `PassesConfigLayerMask` filters direct hits with the same rule as `LocalProjectileManager.PassesLayerMask` (mask -1 means everything). The filter only affects damage: the projectile still collides physically with excluded layers. Making it pass through them would need `Physics.IgnoreCollision` per instance or trigger colliders with manual movement, and was not done.
- Config speed was ignored. `cfg.ResolveSpeed()` was called only in `MID_MasterProjectileSystem.FireNetworkedSim` (raycast and native path); `SpawnPhysicsProjectile` never calls `InitialiseProjectile`, and nothing bridged the config speed into the caller's `bulletVelocity`. A first fix (use the config speed when the caller passes 0 or less) was confirmed by testing not to help, because the calling weapon script (`NetworkedDimensionPlayer.FirePhysics`, which lives in the game project) always passes a non-zero speed of its own. The config speed now wins whenever a config resolves. `_allowCallerVelocityOverride` (default false) restores the caller-wins rule per prefab for charged or randomised shots.
- The collider had a fixed prefab size, independent of `FullSizeX`/`FullSizeY`, so every physics projectile hit-tested at the same size even though the cosmetic visual read those fields. `ApplyConfigScale` and the subclass `ApplyColliderSize` now size the collider from the config. With `UseScaleGrowth`, the projectile used to spawn at full size instead of growing, and now the collider grows on the server with the formula of the native `tick_scale`. The growth coroutine is also stopped explicitly in `OnNetworkDespawn`, rather than relying on the pool's `SetActive(false)` to stop it.
- `OnSpawnImpactEffectClient` only returned the pool visual, so physics projectiles spawned no impact effect whatever `ImpactEffectType` was set to (the field was not referenced anywhere in the file). It now calls `ProjectileImpactHandler.PlayImpact`, the same call `MID_ProjectileNetworkBridge` makes on `HitConfirmedClientRpc` for the raycast and native path. The headshot variant of `PlayImpact` is not used, because passing the flag would change the signature of the protected virtual `NetworkProjectileBase.OnSpawnImpactEffectClient`.
- `isCrit` was computed in `ComputeConfigDamage` but `FireHitEvent` hard-coded `IsCrit = false` in the payload, so every computed crit was thrown away. It is now returned through an `out` parameter and put into the payload by both callers (direct hit and explosion).
- `cfg.HeadshotMultiplier` was never applied. `CheckHeadshotPhysics` was added as an overridable hook (default false, like `LocalProjectileManager.CheckHeadshotLocal`) and the multiplier is applied when it returns true.
- Piercing was added (`_collisionsRemaining`, `_hitTargetIds`, `ResolvePiercingAndDamage`) so a physics shot and a native shot behave the same for one config. Non-piercing configs are unchanged: one hit ends the shot, and a hit on an object without a `NetworkObject` ended the shot before as well.
- Symptom reported against pooled instances: "physics projectile visual/collider only becomes correct on the second fire of a given pooled instance", where the first fire shows a real but wrong config's sprite, with and without `ForceSpriteRendererOnly` and with and without a `CustomShape`. Two explanations were traced against NGO 1.7.1 and ruled out: the `NetworkVariable.Value` setter updates its backing field at once, and on a remote client `NetworkObject.AddSceneObject` applies the incoming NetworkVariable data (`SynchronizeNetworkBehaviours`) before `SpawnNetworkObjectLocally` raises `OnNetworkSpawn`. The mechanism stays unidentified. `RevalidateConfigAfterSpawn` re-checks the config id and the resolved config two frames after spawn and re-applies everything config-dependent when either changed, with a warning that says the safety net fired. It masks the symptom and is not a confirmed root-cause fix. If the wrong visual still shows on the first fire and the warning never logs, the value was already correct two frames in and the cause is elsewhere (suggested next step: log `VisualConfigId`, `Time.frameCount`, `IsServer` and `IsClient` at the top of `OnNetworkSpawn` and in `SpawnPoolVisual`, and compare a first and a second fire of the same pooled instance).
- The `n_VisualConfigId` and `_allowCallerVelocityOverride` tooltips still describe the earlier behaviour ("it used to be a plain field", "old behaviour"). Tooltips are string literals, which a comment-only pass cannot change, so they are left as they are.

### `ProjectileGuidanceTracker.cs`
- Before this class, `Guided` was a selectable movement type and `SetHomingDirection2D/3D` could write a heading, but nothing called it more than once and nothing tracked a target over time, so a caller had to compute and push a direction every tick. The tracker does that.

### `ProjectileVisualWarmupManager.cs`
- The first version cycled one borrowed instance through every config. That warms only that instance, while every other idle instance (the prewarm count can be above 1, and a shot can draw any of them) stayed cold and showed the wrong texture on its first use. Each pass now sweeps the pool's full idle count (`LocalObjectPool.GetQueuedCount`).
- A second pass after a few frames was added for sprites whose atlas resolution was still in flight during the first pass.

### `RaycastProjectileHandler.cs`
- A client firing a pattern raycast shot saw nothing of its own shot. `ServerHandleFirePattern`'s ClientRpc leaves the firing client out (`BuildTargetList(senderClientId)`) on the assumption that a local prediction exists, which was true for the single ray and not for patterns. `ClientPredictPatternLocal` was added, and `MID_MasterProjectileSystem.RegisterRaycastPatternFire` calls it.
- Pattern fire was added for raycast weapons: the server resolves the directions and casts every ray itself, so no client hit claim is involved, with one batched visual RPC for all pellets.
- The 2D server ray uses a `ContactFilter2D` with `useTriggers = true` instead of the plain `Physics2D.Raycast` overload, because that overload follows the project setting `Physics2D.queriesHitTriggers`, which may be false.
- `_trustClientOnValidationMiss` was added for server-side animation that is not replicated: a server ray can miss a target the client aimed at correctly. The cost is the trust problem described under Limits.

### `RustSimCustomShapeAuthoring.cs`
- Shares the registration fixes listed under `RustSimTargetRegistrar.cs` (readiness, `CanAcceptTargets`, static refresh).

### `RustSimTargetRegistrar.cs`
- The earlier static-target fix (keep ticking until the first registration succeeds) only checked that the projectile system existed, then marked the target registered. `MID_MasterProjectileSystem` sends each call to one of two separate buffers based on the network state at that instant (server authority when `IsServer`, local manager when offline), and silently does nothing if the matching backend is null or this is a pure client. A static target registers once, so a call that landed in the wrong buffer or nowhere was never repeated: registered offline and then started as host, the target sat in the local buffer while projectiles simulated in the server buffer. A moving target hid this because it re-registers every tick. Fixed two ways: `RegisterNow()` stays unregistered while `CanAcceptTargets` is false, and a static target now refreshes its registration every `_staticRefreshSeconds` (real time) instead of stopping for good, so it lands in whichever backend is active. Setting the field to 0 restores the old register-once behaviour.
- Objects spawned after `OnSystemReady` had already fired waited forever for an event that never fires again. The readiness check (`IsReady` first, subscribe only if not ready) fixes it for scene objects and dynamically instantiated ones alike.
- A `NetworkObject`'s id read before the spawn was a shared placeholder, so TargetIds collided. Registration now waits for `IsSpawned`.
- Anything without a circle or sphere collider fell back to a hardcoded 0.5 radius circle whatever its real shape. Box, capsule, edge and polygon colliders are now detected and registered exactly (a 3D box as a capsule).

### `ServerProjectileAuthority.cs`
- A burst fired by a non-host client came out as one shot. The latency of the shot was taken from each start delay, and a delay shorter than the latency spawned the projectile at once on the same point as the first. The delay is kept whole now.
- Snapshot X and Y were full 32-bit floats. They now serialize as half-precision floats (2 bytes each) to cut bandwidth. The cost is the precision limit described in the module section.
- Wave and Circular projectiles were included in the snapshot payload even though they do not fit the client's linear reconciliation. They are now left out, which also reduced the "Receive queue is full" UTP warning at high fire rates.
- Snapshot RPC crash: NGO serializes an array parameter by its real length, and the pre-allocated full-capacity buffers (2048 slots, about 24.5 KB for 2D) were handed to the RPC whole. Reliable delivery fragmented that silently, and unreliable delivery hit the size ceiling at once. Every chunk is now resized to exactly its entries and capped to a byte budget, so a large tick is sent as two or three RPCs.
- Optional per-client distance culling of snapshots was added (`_enableDistanceCulling`, `ObserverProvider`), off by default.
- Added read-only `TargetCount2D/3D` and `ShapeCount2D/3D` properties.

### `ProjectileRenderer2D.cs`
- The combined-mesh path assumed four vertices per shape and silently corrupted the geometry of Arrow, Needle, Cross, LetterI and custom shapes. It now advances by each shape's real vertex and index counts.
- The URP shader's non-instanced path read the material's default `_UVRect` (0.24, 0, 4.08, 1.2) and corrupted every UV in the combined batch. `#ifdef UNITY_INSTANCING_ENABLED` branches were added so the non-instanced path passes UVs straight through and uses vertex colour for the fade.
- Sorting between RustSim projectiles was not possible with `Graphics.DrawMesh*`, so a Z offset from `SortingPriority` was added (see the module section for the camera caveat).

### `ProjectileVisual_2D.cs`
- A shape-mesh projectile set `_MainTex` only, and only when a texture existed, so `_UVRect` was never set and the material's baked default (0.24, 0, 4.08, 1.2, left over from the shared-atlas renderer) made every shape-mesh projectile sample the wrong corner of its texture. A config with no sprite also kept whatever the previous use of that pooled instance had bound. The property block is now always set, with `ProjectileRegistry.GetUVRect` and a white-texture fallback.
- The plain sprite path never applied `FullSizeX/Y`, so a config with a sprite (the only path physics projectiles use unless a `CustomShape` is assigned) rendered at whatever scale the prefab had. It now sets the scale like the shape path.
- The sprite path passed `cfg.ProjectileSprite` even when `UseSprite` was false. It now applies the same gate as the Rust-sim renderers.
- Client pool prefabs do not pre-add the shape-mesh components, so `EnsureShapeMeshComponents` creates them when needed.
- Symptom reported: the first fire of a pooled instance shows the wrong visual and the second is correct, with and without a `CustomShape`. The branch logic was traced and is deterministic from the config alone, so it is not the cause. The leading suspicion was the shape-mesh child's `MeshRenderer` saving enabled in the prefab. `OnInitialise` now forces both renderers off before deciding, which is a defensive change and not a confirmed root-cause fix. If the symptom persists, log `ConfigId`, the config name and `Time.frameCount` at the top of `OnInitialise` and compare a first and a second fire of the same pooled instance. `ProjectileVisualWarmupManager` addresses a related cause (atlas resolution).
- Static `Sprite` and `Texture2D` caches were native objects that were never destroyed, which gave Unity's "Leak Detected : Persistent allocates N individual allocations" warning on the next domain reload. They are now destroyed in `AssemblyReloadEvents.beforeAssemblyReload` (editor only).

### `ProjectileVisual_3D.cs`
- A config with no sprite (or an unresolved config id) left the renderer with whatever material it already had, often none or the error shader, so the 3D visual looked as if nothing was set while 2D always showed a fallback. It now uses `_fallbackMaterial` or a lazily generated default.
- The static default mesh and material had the same native leak as the 2D caches and are now destroyed before a domain reload.
- Scale growth was added with `ApplyScaleAtSize`, which reproduces `ApplyScale`.

### `ProjectileVisualBase.cs`
- Pooled visuals jumped straight to full size in one `OnInitialise` call, so `UseScaleGrowth`, `SpawnScaleFraction` and `GrowthSpeed` (already applied natively for RustSim projectiles) had no effect on physics projectiles' cosmetic visual or on the pooled visual of the raycast and RustSim path. `RefreshScaleGrowth` and a growth coroutine with the native formula were added, and `ReturnToPoolImmediate` stops the coroutine explicitly instead of relying on the GameObject deactivating.

### `TrailObjectPool.cs`
- A firing client swaps a projectile's temporary id for the server's real id in place. The slot map was keyed by the old id, so the next sync missed the lookup, acquired a new slot (a visible trail break) and orphaned the old one (a slow leak of one slot per pellet per shot). `RelinkProjectileId` re-keys the slot.

### `ProjectileImpactHandler.cs`
- Removed an unused `using MidManStudio.Core.Audio;`. The file uses no audio
  type. The directive would stop the file from compiling once the audio code
  left the utilities assembly for `com.midmanstudio.audio`, since the runtime
  assembly does not reference the audio assembly.

### `NetworkProjectileBaseEditor.cs`
- Subclass fields of every networked projectile (config references, log level, damage fields, `TimeToLive`) were invisible in the inspector, because NGO 1.7.1's `NetworkTransformEditor` (registered for `NetworkTransform` and all child classes) draws only its own fields and never calls `DrawDefaultInspector()`. This also made the per-instance log level impossible to change from the inspector. Fixed by registering a more derived `[CustomEditor]` for `NetworkProjectileBase`, which wins by Unity's most-derived rule. Inheriting NGO's editor to reuse its field list was rejected, because that class is not guaranteed to stay public, unsealed or stable across NGO versions.

### `ProjectileConfigScriptableObjectEditor.cs`
- A custom icon set on a projectile config would not stick. A change that
  replaced `DrawDefaultInspector()` with `DrawPropertiesExcluding(
  serializedObject, ...)` (to hide the custom-path fields behind a panel)
  dropped the `serializedObject.Update()` and `ApplyModifiedProperties()` that
  `DrawDefaultInspector()` does internally; `DrawPropertiesExcluding` on a
  `SerializedObject` does neither. Every field edit made in the inspector,
  the icon included, was discarded on the next repaint. Restored the pair
  around the draw call. It also covers multi-selection. This is the cause
  found by reading the history and the Unity API contract; it has not been
  run in the editor yet.
- That change also left out the Project-window icon cache invalidation that
  `MID_BaseSOEditor` does (it is not the editor in use for these assets), so
  a changed icon would not repaint until the next domain reload. The editor now
  calls `MID_BaseSOProjectIconDrawer.InvalidateCache` for each selected asset,
  but only when the icon property itself changed. Doing it on every field
  change would walk every selected asset and repaint the Project window on
  each frame of a slider drag.
- Multi-edit crashing the editor after a while was not reproduced and no
  cause was found. The other editor code in both packages was checked for
  leaked textures, un-removed callbacks and unbounded property loops, and the
  ScriptableObject viewer window disposes its cached editors correctly.
  The missing `Update()`/`ApplyModifiedProperties()` pair is the only defect
  found in this editor and is a plausible contributor, but it is not
  confirmed. If the crash continues, the end of `Editor.log` (or
  `Editor-prev.log` after a restart) from the crash will show what failed.

### `ProjectileConfigGenerator.cs`
- The generator laid out the numbers again on every run. An entry inserted before others pushed them all up by one.
  Running the old generator with the entries A, B, C (0, 1, 2) and then adding New at the top gave New 0, A 1, B 2,
  C 3, with three warnings, and the mapping asset followed. A `ProjectileConfigType` value that is stored in a
  scene, a prefab or a ScriptableObject then points at a different projectile config. The same run now gives A 0,
  B 1, C 2 and New 3, with no warnings.
- An unreadable lock file only logged a warning and the run started over with no remembered numbers, then wrote a
  new lock file over the old one. The run now stops with an error.
- A comment with a line break in it produced an enum file that did not compile.
- Known limit: a provider that moves to a new block because it outgrew its old one and the numbers after it are
  taken changes its values, pinned entries included, because a pin is an offset inside the block. The result lists a
  warning for the move and one for every entry that changed. A provider that is expected to grow needs a large
  enough `minimumBlockSize` before the first generate, since a block that already exists keeps its size.
- Known limit: the lock file has to be committed. A project that does not commit it gets the numbers from the entry
  order again on a fresh checkout.

### `WeaponController.cs`
- `SpawnPhysicsProjectileLocal()`'s `SetGuidedTarget()` call must run after
  `InitialiseProjectile()`, because `SetupMovementType` resets any guided
  target on every fresh launch (see the matching note in
  `MID_ProjectileNetworkBridge.FirePhysicsProjectileServerRpc`). This
  constraint was documented on the original `NetworkedDimensionPlayer` copy
  of this code, but the comment was not carried over when the logic was
  copied into this file. Added it back.
- `EquipLocal()`'s model-swap block had three separate problems. It called
  `_player.Set3DShotpoint(shotPoint)` with `shotPoint` typed as `WeaponRef`
  against a method that takes a `Transform` — a compile error, fixed by
  passing `shotPoint.ShotPoint`. It read `GetComponent<WeaponRef>()` off
  `weapon.WeaponModelPrefab`, the source prefab asset, rather than
  `_currentModelInstance`, the instantiated copy actually in the scene — the
  prefab asset's own `WeaponRef.ShotPoint` is never part of the live
  hierarchy, so it would not have moved with the equipped weapon. And
  `_currentModelInstance.transform.rotation = new Quaternion(0, -180, 0, 0)`
  ran unconditionally, including when `weapon.WeaponModelPrefab` was null (an
  anticipated case — see MANIFEST.txt on weapons still being wired up without
  art) and `_currentModelInstance` was therefore still null or already
  destroyed, throwing. Separately, `new Quaternion(x, y, z, w)` takes raw
  quaternion components, not Euler degrees; `(0, -180, 0, 0)` is not a unit
  quaternion, and it only produced a visually correct 180-degree turn because
  a lone nonzero y-component happens to normalize to the right answer by
  coincidence for this one axis — the moment any other axis needed a
  correction too this would stop working. Also set as world-space `.rotation`
  rather than local, which does not itself desync from the socket (Unity
  keeps a child's world rotation following its parent automatically) but
  bakes in whatever the socket's orientation happened to be at that exact
  equip instant rather than a fixed, predictable offset. Fixed by guarding
  the whole block on `weapon.WeaponModelPrefab != null`, reading `WeaponRef`
  off `_currentModelInstance`, and replacing the rotation assignment with
  `_currentModelInstance.transform.localRotation =
  Quaternion.Euler(_modelRotationOffsetEuler)` — a new serialized field
  (default `(0, 180, 0)`, matching the value already in use) instead of a
  hardcoded magic number, applied relative to the socket.
- The switch animation was an Animator trigger. In use, the trigger left the
  switch state playing indefinitely, and the clip animated the holder. The
  Animator controller and clip are not in the repo, so the cause of the weapon
  turning sideways was inferred and not confirmed: a clip with absolute
  position or rotation curves on the holder replaces the holder's authored
  pose every frame, and it fights the code-set model rotation. Replaced with a
  coroutine that captures the holder's rest pose once and applies the dip as
  an offset from it (`_switchLoweredOffset`, `_switchLoweredEuler`,
  `_switchLowerDuration`, `_switchRaiseDuration`, `_switchEase`), so the
  authored pose is always what it returns to. The model swaps at the bottom of
  the dip. A switch that is interrupted continues from the holder's current
  position, and disabling the component mid-switch finishes the pending swap
  on re-enable. The first equip and any equip with no previous model swap
  immediately with no dip. The fire lockout is at least the animation length.
  `_animator` and `_defaultSwitchAnimTrigger` were removed from this class;
  an Animator on the player must not carry curves for the holder.
- The model swap moved into `SwapModel()`. The shot point is now found by
  `WeaponRef.EnsureOn()` (see its section) instead of requiring each prefab to
  carry a `WeaponRef` with `ShotPoint` already assigned.
- `FireSim()` now logs a one-time warning (per config id) when the config's
  movement type is `CustomCurve`, since the native sim flies it straight. The
  physics fire path now says that physics projectiles need a Host or Server
  when the pool call returns null while not a server: physics projectiles are
  server-owned NetworkObjects, so the offline case previously only logged
  "Pool null".

### `PlayerEntryCard.cs`
- The ready-state text used `\u2713` (checkmark) and `\u2026` (ellipsis).
  LiberationSans SDF, TextMeshPro's default font, does not include those
  glyphs, which produced a "character not found" warning and rendered a box
  fallback character. Replaced with plain ASCII ("READY"/"WAITING").

### `ProjectileTestLobbyUI.cs`
- `FriendlyStatus()`'s "Hotspot Active" status used `\u2713` (checkmark) in
  the "NetStat" text object, causing the same LiberationSans SDF
  glyph-fallback warning as `PlayerEntryCard.cs` above. Removed.

### `TestSceneBootstrapper.cs`
- The `OnGameStartReceived` subscription and showing the lobby UI now happen
  before config registration runs. Registration touches the native
  `projectile_core` library, and a missing or misconfigured native lib for
  the device's architecture used to throw uncaught partway through this
  coroutine, skipping the subscription entirely and silently breaking
  "Start Game" for the whole session (nothing was left listening on the
  host). Reordering means Start Game still works, in a degraded (no local
  projectile visuals) state, even when the native lib is broken.
- A single config failing to register (for example, the native lib being
  unavailable on that device) no longer aborts the rest of this coroutine,
  including the offline auto-spawn further down; registration now runs
  inside its own try/catch per config.
