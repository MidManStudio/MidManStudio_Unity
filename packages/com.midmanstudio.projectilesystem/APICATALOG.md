# com.midmanstudio.projectilesystem — API Catalog

`com.midmanstudio.projectilesystem` v1.1.0  
Assembly: `MidManStudio.ProjectileSystem`  
Namespace root: `MidManStudio.Projectiles`  
Requires: `com.midmanstudio.utilities 1.0.0`, `com.midmanstudio.netcode 1.0.0`, `com.unity.netcode.gameobjects 1.7.1+`

> **2D PRIMARY** — The 2D simulation path (`Is3D = false`) is the fully tested production path.  
> **3D REFERENCE** — The 3D path (`Is3D = true`) is a functional reference implementation. Same architecture, less hardware-validated. Marked throughout as *(3D ref)*.

---

## Table of Contents

1. [Config System](#1-config-system)
2. [Pattern System](#2-pattern-system)
3. [Shape System](#3-shape-system)
4. [Math Formula Evaluator](#4-math-formula-evaluator)
5. [Core FFI Structs and Enums](#5-core-ffi-structs-and-enums)
6. [Simulation Routing](#6-simulation-routing)
7. [MID_MasterProjectileSystem](#7-mid_masterprojectilesystem)
8. [ProjectileRegistry](#8-projectileregistry)
9. [Rust Simulation Managers](#9-rust-simulation-managers)
10. [Raycast Handler](#10-raycast-handler)
11. [Physics Projectiles](#11-physics-projectiles)
12. [Network Bridge and Client Prediction](#12-network-bridge-and-client-prediction)
13. [Visual Systems](#13-visual-systems)
14. [Data Types](#14-data-types)
15. [Assembly Definitions](#15-assembly-definitions)

---

## 1. Config System

### `ProjectileConfigSO : ScriptableObject`

**Namespace:** `MidManStudio.Projectiles.Config`  
**Create via:** `Right-click → MidManStudio > Projectile System > Projectile Config`

Core per-projectile configuration asset. Drives the Rust simulation, renderer, and damage system.

**Simulation**

| Field / Property | Type | Description |
|---|---|---|
| `Is3D` | `bool` | False = 2D Rust buffer (primary). True = 3D buffer (reference impl). |
| `PreferredSimMode` | `SimulationMode` | Optional override. Default `RustSim` means router decides. Any other value forces that mode. |
| `HasSimModeOverride` | `bool` | True when `PreferredSimMode != RustSim`. |
| `RequiresPhysicsObject()` | `virtual bool` | Override in subclass to force `PhysicsObject` routing. Default returns false. |
| `IsRaycastEligible()` | `virtual bool` | Returns false if piercing, 3D, or physics. Override to customise. |

**Movement**

| Field / Property | Type | Description |
|---|---|---|
| `MovementType` | `ProjectileMovementType` | Straight / Arching / Guided / Teleport / Wave / Circular |
| `MinSpeed` / `MaxSpeed` | `float` | Speed range. `ResolveSpeed()` picks a random value between them. |
| `ResolveSpeed()` | `float` | Returns `MinSpeed` when equal, otherwise `Random.Range`. |
| `Lifetime` | `float` | Seconds before the projectile expires. |
| `GravityScale` | `float` | Gravity for `Arching` type; passed as `Ay` to Rust. |
| `MaxRange` | `float` | World units. Used for damage curve normalisation. |

**Wave params** (only active when `MovementType = Wave`)

| Property | Type | Description |
|---|---|---|
| `WaveAmplitude` | `float` | Oscillation amplitude in world units. |
| `WaveFrequency` | `float` | Oscillations per second. |
| `WavePhaseOffset` | `float` | Phase offset in radians. |
| `WaveVertical` | `bool` | Oscillate on vertical axis instead of perpendicular. |

**Circular params** (only active when `MovementType = Circular`)

| Property | Type | Description |
|---|---|---|
| `CircularRadius` | `float` | Orbit radius. |
| `CircularAngularSpeed` | `float` | Degrees per second. |
| `CircularStartAngle` | `float` | Starting angle in degrees. |

```csharp
// Wave/Circular params are registered with Rust automatically when the config
// is registered. Also called live in OnValidate during play mode.
config.RegisterMovementParams();   // called by ProjectileRegistry.Register()
config.UnregisterMovementParams(); // called on registry destroy
```

**Piercing**

| Field / Property | Type | Description |
|---|---|---|
| `PiercingType` | `ProjectilePiercingType` | None / Piecer / Random |
| `MaxCollisions` | `byte` | Pierce count for `Piecer`; max for `Random`. |

**Collision Layers**

| Field / Property | Type | Description |
|---|---|---|
| `HitLayers` | `LayerMask` | Which Unity layers this projectile registers hits against. Default = Everything (-1). |

**Scale / Visual**

| Field / Property | Type | Description |
|---|---|---|
| `FullSizeX` | `float` | Projectile width in world units. |
| `FullSizeY` | `float` | Projectile height in world units. |
| `UseScaleGrowth` | `bool` | Animate from `SpawnScaleFraction` → `FullSizeX` on spawn. |
| `SpawnScaleFraction` | `float` | 0–1 fraction of `FullSizeX` at spawn when growth is enabled. |
| `GrowthSpeed` | `float` | Lerp speed per second toward full scale. |
| `ProjectileSprite` | `Sprite` | Atlas sprite. Null = use shape mesh or white texture. |
| `UseSprite` | `bool` | False = skip sprite, render shape mesh only. |
| `CustomShape` | `ProjectileShapeSO` | Optional custom mesh shape. Null = default quad. |
| `ImpactEffectType` | `PoolableParticleType` | Particle type for `ProjectileImpactHandler` non-GlobalFX fallback. |

**Trail**

| Field / Property | Type | Description |
|---|---|---|
| `HasTrail` | `bool` | Enables `TrailObjectPool` slot acquisition for this projectile. |
| `TrailMaterial` | `Material` | Material for the trail renderer. |
| `UseGradientOverride` | `bool` | Apply `TrailGradient` instead of material colour. |
| `TrailGradient` | `Gradient` | |
| `TrailTime` | `float` | Trail fade time in seconds. |
| `TrailStartWidth` / `TrailEndWidth` | `float` | |
| `TrailMinVertexDistance` | `float` | |
| `TrailCapVertices` | `int` | |
| `UseSharedTrailMaterial` | `bool` | Use `sharedMaterial` (no instance) for performance. |

**Damage**

| Field / Property | Description |
|---|---|
| `DamageCurve` | `AnimationCurve` over normalised distance (0 = spawn, 1 = MaxRange). |
| `HeadshotMultiplier` | Damage multiplier on headshot. |
| `CritChance` | 0–1 probability of a critical hit. |
| `CritMultiplier` | Damage multiplier on crit. |
| `EvaluateDamage(float normalisedDistance)` | Returns curve value at distance. Called by `RustSimAdapter`. |
| `IsDamageConstant()` | True if curve is flat — used to skip curve evaluation in hot path. |

**Spawn helper**

```csharp
// Returns minimal RustSpawnParams for BatchSpawnHelper.
// speedOverride > 0 bypasses ResolveSpeed().
RustSpawnParams p = config.GetRustSpawnParams(speedOverride: -1f);
```

---

### `ProjectileRegistry : Singleton<ProjectileRegistry>`

**Namespace:** `MidManStudio.Projectiles.Config`  
**File:** `Runtime/Config/ProjectileRegistry.cs`

Runtime table mapping `ushort configId` to `ProjectileConfigSO`. IDs are session-stable but not persistent across sessions.

```csharp
// Auto-registers configs assigned in _autoRegister inspector list.
// Also calls ProjectileLib.ValidateStructSizes() — disables the component on mismatch.
// Place on the same persistent GameObject as ServerProjectileAuthority.
```

| Member | Returns | Description |
|---|---|---|
| `Register(ProjectileConfigSO config)` | `ushort` | Assigns session-stable ID; calls `RegisterMovementParams()`; idempotent by name. |
| `RegisterByResourcePath(string path)` | `ushort` | `Resources.Load` then register. |
| `Get(ushort configId)` | `ProjectileConfigSO` | Null if out of range. |
| `TryGetId(string configName, out ushort id)` | `bool` | Lookup by asset name. |
| `Is3D(ushort configId)` | `bool` | True if config has `Is3D = true`. |
| `GetRustSpawnParams(ushort configId, float speedOverride)` | `RustSpawnParams` | Called by `BatchSpawnHelper` — avoids passing full SO across the spawn path. |
| `GetUVRect(ushort configId)` | `Vector4` | Atlas UV rect `(x, y, w, h)` for the config's sprite. `(0,0,1,1)` if no sprite. |
| `Count` | `int` | Total registered configs. |
| `HasInstance` | `bool` | Singleton null-safe check. |

---

### `ProjectileConfigManager : Singleton<ProjectileConfigManager>`

**Namespace:** `MidManStudio.Projectiles.Config`  
**File:** `Runtime/Config/ProjectileConfigManager.cs`

Runtime bridge between the generated `ProjectileConfigType` enum and session-local `ushort configId` values.

**Setup:**
1. Generate enum + mapping: `MidManStudio > Projectile System > Config Type Generator > Generate Now`
2. Assign the generated `ProjectileConfigMappingSO` to `_mapping` in the inspector
3. Place on the same persistent GameObject as `ProjectileRegistry`
4. `RegisterAll()` is called in `Start()` — or call it explicitly in your bootstrapper

```csharp
// RegisterAll() is idempotent. Call explicitly when execution order matters:
configManager.RegisterAll(configMappingAsset);

// Get session configId from generated enum value:
ushort id = ProjectileConfigManager.Instance.GetConfigId((int)ProjectileConfigType.FireBall);

// Or use the extension method directly:
system.Fire((int)ProjectileConfigType.FireBall, spawnPoints, count, context);
```

| Member | Returns | Description |
|---|---|---|
| `RegisterAll(ProjectileConfigMappingSO mapping)` | `void` | Registers all non-null entries; builds enum→id dictionary. |
| `GetConfigId(int configTypeValue)` | `ushort` | Returns `ushort.MaxValue` and logs warning if not registered. |
| `HasInstance` | `bool` | Singleton null-safe check. |

---

### `ProjectileConfigMappingSO : ScriptableObject`

**Namespace:** `MidManStudio.Projectiles.Config`  
Auto-generated by Config Type Generator. `Configs[i]` maps to `ProjectileConfigType` int value `i`. Null slots are valid padding gaps between provider blocks. Assign to `ProjectileConfigManager._mapping`.

---

### `ProjectileConfigProviderSO : ScriptableObject`

**Namespace:** `MidManStudio.Projectiles.Config`  
**Create via:** `Right-click → MidManStudio > Projectile System > Config Type Provider`

Per-package list of named `ProjectileConfigSO` entries. Scanned by the generator to produce the enum.

| Field | Type | Description |
|---|---|---|
| `packageId` | `string` | Unique reverse-domain ID. |
| `displayName` | `string` | Generator window label. |
| `priority` | `int` | 0 = system reserved, 100+ = game code. |
| `entries` | `List<ProjectileConfigEntry>` | Named entries. |
| `EntryCount` | `int` | |

**`ProjectileConfigEntry` fields:**

| Field | Type | Description |
|---|---|---|
| `enumName` | `string` | PascalCase enum member name. If blank, config SO name is used (sanitised). |
| `configSO` | `ProjectileConfigSO` | The config asset this entry maps to. |
| `comment` | `string` | Written as `// comment` in generated enum file. |
| `explicitOffset` | `int` | `-1` = auto-assigned. `>=0` = pinned to this offset within the block. |

---

### `ProjectileConfigGeneratorSettingsSO : ScriptableObject`

**Namespace:** `MidManStudio.Projectiles.Config`  
**Create via:** `Right-click → MidManStudio > Projectile System > Config Generator Settings`

| Field | Default | Description |
|---|---|---|
| `enumOutputPath` | `Assets/MidManStudio/Generated/Projectiles/ProjectileConfigType.cs` | |
| `mappingAssetPath` | `Assets/MidManStudio/Generated/Projectiles/ProjectileConfigMapping.asset` | |
| `lockFilePath` | `Assets/MidManStudio/Generated/Projectiles/ConfigTypeLock.json` | Commit to source control. |
| `minimumBlockSize` | `50` | Block size per provider (rounded up to next multiple). |
| `generatedNamespace` | `MidManStudio.Projectiles.Config` | |
| `autoGenerateOnAssetChange` | `false` | Re-run generator when any provider asset changes. |

---

## 2. Pattern System

### `ProjectilePatternSO : ScriptableObject`

**Namespace:** `MidManStudio.Projectiles.Config`  
**Create via:** `Right-click → MidManStudio > Projectile System > Projectile Pattern`

Defines the angular distribution of projectiles in a single fire event. `SampleDirections()` returns `Vector2(horizontalDeg, verticalDeg)` pairs in local weapon space.

```csharp
Vector2[] dirs = pattern.SampleDirections(bulletCount);
for (int i = 0; i < dirs.Length; i++)
{
    Vector3 dir = Quaternion.Euler(-dirs[i].y, dirs[i].x, 0f) * weaponForward;
    spawnPoints[i] = new SpawnPoint { Origin = barrel, Direction = dir, Speed = 0f };
}
```

**Public API**

| Member | Returns | Description |
|---|---|---|
| `SampleDirections(int count = -1)` | `Vector2[]` | Returns `(H°, V°)` pairs. -1 uses `ProjectileCount` from inspector. |
| `EvaluateSpline(float t)` | `Vector2` | Spline-only: evaluates the control-point curve at `t ∈ [0,1]`. |
| `GetSpeedMultiplier(int index, uint seed)` | `float` | Speed variance multiplier for projectile at index. 1.0 if `SpeedVariance = 0`. |
| `Shape` | `PatternShape` | |
| `SplineType` | `PatternSplineType` | CatmullRom / Bezier / Linear |
| `ControlPoints` | `Vector2[]` | Spline control points: x=H°, y=V°. Draggable in inspector viewport. |
| `ProjectileCount` | `int` | Default count used when `SampleDirections(-1)` is called. |
| `SpeedVariance` | `float` | 0–0.5 random multiplier applied to each projectile's speed. |
| `PatternFormulaH` | `string` | H(i,n) expression (Formula shape only). |
| `PatternFormulaV` | `string` | V(i,n) expression (Formula shape only). |

**`PatternShape` enum**

| Value | Description |
|---|---|
| `Spline` | Catmull-Rom, Bezier, or Linear through control points |
| `Ring360` | Evenly spaced full ring |
| `Fan` | Arc spread within `FanHalfArcDeg` |
| `VShape` | Two arms at `VShapeAngleDeg`; optional centre bullet |
| `Shotgun` | Random cone within `ShotgunConeDeg` |
| `Star` | N-pointed star / polygon |
| `Spiral` | Ring + incremental angle step per bullet |
| `Formula` | `H = expr(i,n)`, `V = expr(i,n)` via `MathFormulaEvaluator` |

**Formula variables:** `t = i/n`, `i` (index float), `n` (count float), `pi`, `tau`, `e`

**Built-in formula examples:**

| Pattern | H formula | V formula |
|---|---|---|
| Ring | `i / n * 360` | `0` |
| Fan | `i / (n - 1) * 180 - 90` | `0` |
| Spiral | `i / n * 360 * 3` | `i / (n - 1) * 60 - 30` |
| Wave sphere | `i / n * 360` | `sin(i / n * tau * 2) * 30` |

**Default pattern assets** (created via `MidManStudio > Projectile System > Create Default Pattern Assets`):
`Ring_8`, `Ring_16`, `Fan_5_90deg`, `Fan_7_180deg`, `Shotgun_5`, `Shotgun_9`, `VShape_3`, `Pentagon_5`, `Hexagon_6`, `Spiral_12`, `Triangle_Linear`, `Square_Linear`, `Formula_Ring_12`, `Formula_WaveSphere_16`, `Formula_Spiral_3D_20`

---

## 3. Shape System

### `ProjectileShapeSO : ScriptableObject`

**Namespace:** `MidManStudio.Projectiles`  
**Create via:** `Right-click → MidManStudio > Projectile System > Projectile Shape`

Defines the mesh shape used by `ProjectileRenderer2D` and `ProjectileVisual_`. Assign to `ProjectileConfigSO.CustomShape`.

| Member | Returns | Description |
|---|---|---|
| `GetMesh()` | `Mesh` | Returns cached mesh; builds if cache is null. |
| `BuildMesh()` | `Mesh` | Rebuilds and returns mesh for current `Shape`. |
| `Shape` | `Preset` | Active shape preset. |
| `AspectRatio` | `float` | X:Y ratio applied to all built-in shapes. |
| `FormulaX` / `FormulaY` | `string` | X(t) / Y(t) expressions for `Formula` preset. |
| `FormulaSampleCount` | `int` | Perimeter vertices for formula shapes (3–128). |
| `Vertices` / `Triangles` / `UVs` | `List<>` | Custom shape data. Editable in inspector or via `ProjectileShapeEditor`. |

**`Preset` enum** (serialised int values — do not reorder)

`Quad` · `Needle` · `Diamond` · `Arrow` · `Cross` · `Chevron` · `Star4` · `Boomerang` · `LetterI` · `LetterT` · `LetterL` · `Custom` · `Formula`

**Formula preset** — samples X(t) and Y(t) for `t ∈ [0,1)` at `FormulaSampleCount` points, then builds a center-fan triangulation. Curve must wind CCW.

```
Circle:    X = cos(t * tau) * 0.5    Y = sin(t * tau) * 0.5
PetalStar: X = cos(t * tau) * (0.5 + 0.15 * cos(t * tau * 5))
           Y = sin(t * tau) * (0.5 + 0.15 * cos(t * tau * 5))
```

**Default shape assets** (created via `MidManStudio > Projectile System > Create Default Shape Assets`):
All presets + `Formula_Circle_16`, `Formula_PetalStar_64`

---

## 4. Math Formula Evaluator

### `MathFormulaEvaluator` (static class)

**Namespace:** `MidManStudio.Projectiles.Config`  
**File:** `Runtime/Config/MathFormulaEvaluator.cs`

Self-contained recursive-descent expression parser. No external dependencies. Thread-safe.

| Member | Returns | Description |
|---|---|---|
| `Evaluate(string formula, FormulaContext ctx, out string error)` | `float` | Evaluate formula with given context. Sets `error` on failure. |
| `Validate(string formula, out string error)` | `bool` | Test-evaluates with `t=0.5, i=0, n=8`. Returns true if no error. |
| `GetExamples(FormulaUsage usage)` | `string[]` | Built-in example strings for `ShapeX / ShapeY / PatternH / PatternV`. |

**`FormulaContext` struct**

| Field | Description |
|---|---|
| `t` | Normalised parameter `i/n` in `[0, 1)`. |
| `i` | Element index as float. |
| `n` | Total count as float. |

**Supported syntax:**
- Numbers: `1`, `1.5`, `.5`
- Variables: `t`, `i`, `n`, `pi`, `tau`, `e`
- Operators: `+`, `-`, `*`, `/`, `^` (power), `%`, unary `-`
- Functions: `sin cos tan asin acos atan atan2 sqrt abs floor ceil round sign frac saturate pow log log2 exp min max clamp lerp mod deg rad step smoothstep pingpong repeat`

---

## 5. Core FFI Structs and Enums

All sizes must exactly match the compiled Rust library. `ProjectileLib.ValidateStructSizes()` checks this at startup.

### `NativeProjectile` *(2D primary, 72 bytes)*

**Namespace:** `MidManStudio.Projectiles.Core`  
`[StructLayout(LayoutKind.Explicit, Size = 72)]`

The live 2D projectile state in the Rust simulation buffer. Rust writes physics fields every tick; C# writes identity fields once on spawn.

| Field | Offset | Type | Description |
|---|---|---|---|
| `X` / `Y` | 0 / 4 | `float` | World position |
| `Vx` / `Vy` | 8 / 12 | `float` | Velocity |
| `Ax` / `Ay` | 16 / 20 | `float` | Acceleration / homing direction / perpendicular axis |
| `AngleDeg` | 24 | `float` | Visual rotation (written by Rust) |
| `CurveT` | 28 | `float` | Arc / wave phase accumulator |
| `ScaleX` / `ScaleY` | 32 / 36 | `float` | Current visual scale |
| `ScaleTarget` / `ScaleSpeed` | 40 / 44 | `float` | Scale growth target and speed |
| `Lifetime` | 48 | `float` | Remaining lifetime in seconds |
| `MaxLifetime` | 52 | `float` | Initial lifetime (used for fade calculation) |
| `TravelDist` | 56 | `float` | Accumulated travel distance |
| `ConfigId` | 60 | `ushort` | Registry config ID |
| `OwnerId` | 62 | `ushort` | Firing entity ID |
| `ProjId` | 64 | `uint` | Unique projectile ID |
| `CollisionCount` | 68 | `byte` | Number of hits so far |
| `MovementType` | 69 | `byte` | `ProjectileMovementType` cast to byte |
| `PiercingType` | 70 | `byte` | `ProjectilePiercingType` cast to byte |
| `Alive` | 71 | `byte` | 0 = dead, 1 = alive |

### `NativeProjectile3D` *(3D ref, 84 bytes)*

Same layout as 2D but with `Z`, `Vz`, `Az`, `ScaleZ`, `TimerT`. `VisualRotation()` uses `Quaternion.LookRotation(velocity)`.

### `HitResult` *(2D, 24 bytes)* / `HitResult3D` *(3D ref, 28 bytes)*

Returned by `check_hits_grid_ex` / `check_hits_grid_3d`. Fields: `ProjId`, `ProjIndex`, `TargetId`, `TravelDist`, `HitX`, `HitY` (and `HitZ` for 3D).

### `CollisionTarget` *(2D, 20 bytes)* / `CollisionTarget3D` *(3D ref, 24 bytes)*

Registered sphere in the spatial grid. Fields: `X`, `Y` (`Z` for 3D), `Radius`, `TargetId`, `Active` (0/1 byte).

### `SpawnRequest` *(32 bytes)*

Legacy pattern-based spawn format. Used internally by `ProjectileLib.spawn_pattern`.

### `RustSpawnParams`

**Namespace:** `MidManStudio.Projectiles.Core`

Minimal data extracted from `ProjectileConfigSO` for `BatchSpawnHelper`. No Unity object references.

| Field | Type | Description |
|---|---|---|
| `Speed` | `float` | Resolved speed (already random-picked by caller) |
| `MovementType` | `byte` | |
| `PiercingType` | `byte` | |
| `MaxCollisions` | `byte` | |
| `Lifetime` | `float` | |
| `GravityAy` | `float` | Gravity acceleration (maps to `Ay` for Arching; ignored for Wave/Circular) |
| `ScaleStart` | `float` | Starting scale (full size or fraction if growth enabled) |
| `ScaleTarget` | `float` | Target scale |
| `ScaleSpeed` | `float` | Scale lerp speed; 0 = no growth |
| `Is3D` | `bool` | |

### `SimulationMode` (enum, byte)

| Value | Description |
|---|---|
| `Raycast = 0` | Instant hitscan; server casts ray; client visual travels to endpoint |
| `RustSim = 1` | Rust tick + spatial-grid collision; server-authoritative; client prediction |
| `PhysicsObject = 3` | Unity Rigidbody2D/3D; NGO NetworkObject; NetworkTransform |
| `LocalOnly = 4` | Full Rust sim; offline only; no NGO |

### `ProjectileMovementType` (enum, byte)

`Straight=0` · `Arching=1` · `Guided=2` · `Teleport=3` · `Wave=4` · `Circular=5`

### `ProjectilePiercingType` (enum, byte)

`None=0` · `Piecer=1` · `Random=2`

### `NetworkVariant` (enum, byte)

`None=0` (offline) · `ServerAuth=1` (server-authoritative)

---

## 6. Simulation Routing

### `ProjectileTypeRouter` (static class)

**Namespace:** `MidManStudio.Projectiles.Adapters`

Pure routing functions — deterministic, no side effects. Called by `MID_MasterProjectileSystem.Fire()`.

| Member | Returns | Description |
|---|---|---|
| `Route(ProjectileConfigSO config, WeaponFireContext context)` | `RoutingResult` | Primary entry point. Returns mode + network variant. |
| `RequiresPhysicsObject(ProjectileConfigSO config)` | `bool` | Delegates to `config.RequiresPhysicsObject()`. |
| `IsRaycastEligible(ProjectileConfigSO config)` | `bool` | Delegates to `config.IsRaycastEligible()`. |
| `ExplainRoute(ProjectileConfigSO config, WeaponFireContext context)` | `string` | Human-readable routing decision (editor/debug use). |

**`WeaponFireContext` struct**

| Field | Type | Description |
|---|---|---|
| `IsNetworked` | `bool` | False forces `LocalOnly`. |
| `IsRaycastWeapon` | `bool` | True + eligible config → `Raycast` mode. |
| `ProjectileCount` | `int` | Bullets in this fire event. |
| `LatencyCompensation` | `float` | Seconds to subtract from initial Lifetime (not position). |
| `OwnerMidId` | `ulong` | MID ID of the firing entity. |
| `FiredByNetworkObjectId` | `ulong` | NetworkObject ID of the weapon/character. |
| `IsBotOwner` | `bool` | |
| `WeaponLevel` | `byte` | |
| `DamageMultiplier` | `float` | From power-ups or abilities. |
| `FireRate` | `float` | Rounds per second (informational). |

**`RoutingResult` struct**

| Field | Description |
|---|---|
| `Mode` | `SimulationMode` |
| `Network` | `NetworkVariant` |
| `WasOverridden` | True when result came from `config.PreferredSimMode`. |

---

## 7. MID_MasterProjectileSystem

### `MID_MasterProjectileSystem : Singleton<MID_MasterProjectileSystem>`

**Namespace:** `MidManStudio.Projectiles.Managers`  
**Primary game code entry point.** Routes all fire events, owns references to all sub-systems.

**Properties**

| Member | Type | Description |
|---|---|---|
| `IsNetworked` | `bool` | True when NGO is listening and `_forceOfflineMode` is false. |
| `IsServer` | `bool` | True when `NetworkManager.IsServer`. |
| `IsHostMode` | `bool` | True when both `IsServer` and `IsClient`. |
| `GetAuthority()` | `ServerProjectileAuthority` | Server-side Rust buffer. Null on pure clients. |
| `GetBridge()` | `MID_ProjectileNetworkBridge` | NGO RPC bridge. |
| `GetRaycastHandler()` | `RaycastProjectileHandler` | |
| `GetPredictionManager()` | `ClientPredictionManager` | Physics visual manager. |
| `GetBridgeTick()` | `int` | Current server tick from bridge. |

**Core API**

```csharp
// Fire any simulation mode — router decides which path to take
void Fire(ushort configId, SpawnPoint[] spawnPoints, int count, WeaponFireContext context)

// Identity — call once after network connection is established
void SetLocalPlayerMidId(ulong midId)
```

**Target registration (shared between server + offline)**

```csharp
// 2D sphere target (primary)
void RegisterTarget2D(in CollisionTarget target, int unityLayer = 0)
void DeactivateTarget2D(uint targetId)

// 3D sphere target (reference impl)
void RegisterTarget3D(in CollisionTarget3D target, int unityLayer = 0)
void DeactivateTarget3D(uint targetId)

void ClearAllTargets()
```

> In networked mode, `RegisterTarget2D/3D` only pushes to `ServerProjectileAuthority` (server). On clients, targets are intentionally NOT registered in `LocalProjectileManager` — the server handles all authoritative collision, and keeping the client buffer empty means collision detection is automatically skipped.

**Raycast mode**

```csharp
// Call from weapon script after performing Physics2D.Raycast / Physics.Raycast
void RegisterRaycastFire(RaycastFireResult result, ushort configId, WeaponFireContext context)
```

**Physics pool mode**

```csharp
// Server only — spawns NGO NetworkObject from MID_NetworkObjectPool
NetworkObject SpawnPhysicsProjectile(PoolableNetworkObjectType type, Vector3 pos, Quaternion rot)

// Server only — return to pool (call instead of Despawn)
void ReturnPhysicsProjectile(NetworkObject netObj, PoolableNetworkObjectType type)
```

**Guided projectile steering**

```csharp
// Server or offline only
void SetHomingDirection2D(uint projId, Vector2 worldDir)
void SetHomingDirection3D(uint projId, Vector3 worldDir)
```

**State save/restore (server)**

```csharp
int SaveState2D(byte[] buf)
int RestoreState2D(byte[] buf, int byteCount)
```

---

## 8. ProjectileRegistry

See [Section 1 — Config System](#1-config-system). `ProjectileRegistry` is documented there alongside `ProjectileConfigSO`.

---

## 9. Rust Simulation Managers

### `ServerProjectileAuthority : MonoBehaviour`

**Namespace:** `MidManStudio.Projectiles.Managers`  
**Server-only.** Owns the authoritative Rust 2D and 3D simulation buffers. Runs `FixedUpdate` sim loop.

**Inspector references to assign:**
- `_renderer2D` — `ProjectileRenderer2D` (for host rendering)
- `_renderer3D` — `ProjectileRenderer3D` *(3D ref)*
- `_snapshotIntervalTicks` — how often to send position snapshots to clients (default: every 4 ticks)
- `NetworkBridge` / `TrailPool` — set by `MID_MasterProjectileSystem.Initialise()`

**Public API (called by bridge and master system)**

| Member | Returns | Description |
|---|---|---|
| `AddProjectile2D(in NativeProjectile proj, ServerProjectileData data)` | `bool` | Insert one projectile into 2D buffer. |
| `AddProjectile3D(in NativeProjectile3D proj, ServerProjectileData data)` | `bool` | *(3D ref)* |
| `NotifyBatchSpawned2D(int spawned, uint baseId, ServerProjectileData template)` | `void` | Register a full batch after `BatchSpawnHelper.SpawnBatch2D`. |
| `NotifyBatchSpawned3D(int spawned, uint baseId, ServerProjectileData template)` | `void` | *(3D ref)* |
| `Get2DWriteHead()` | `(IntPtr, int)` | Pointer + remaining slots for `BatchSpawnHelper`. |
| `Get3DWriteHead()` | `(IntPtr, int)` | *(3D ref)* |
| `AllocateProjIds(int count)` | `uint` | Returns base ID; increments global counter. |
| `RegisterTarget2D(in CollisionTarget, int layer)` | `void` | |
| `RegisterTarget3D(in CollisionTarget3D, int layer)` | `void` | *(3D ref)* |
| `DeactivateTarget2D/3D(uint targetId)` | `void` | Set `Active = 0` in collision buffer. |
| `ClearAllTargets()` | `void` | |
| `SetAcceleration2D(uint projId, Vector2 accelDir)` | `void` | Guided homing: writes `Ax/Ay` directly. |
| `SetAcceleration3D(uint projId, Vector3 accelDir)` | `void` | *(3D ref)* |
| `SaveState2D(byte[] buf)` | `int` | Serialise 2D buffer to bytes via Rust. |
| `RestoreState2D(byte[] buf, int byteCount)` | `int` | Restore 2D buffer from bytes. |
| `ActiveCount2D` / `ActiveCount3D` | `int` | Live projectile count. |
| `Adapter` | `RustSimAdapter` | Hit processor; subscribe to `Adapter.OnProjectileHit`. |

---

### `LocalProjectileManager : Singleton<LocalProjectileManager>`

**Namespace:** `MidManStudio.Projectiles.Managers`  
Manages the client-side (and offline) Rust simulation buffers. Handles:

- **Offline mode** — full Rust sim with local damage targets
- **Firing client** — immediate visual via temp-ID buffer entries (called automatically by `MID_MasterProjectileSystem`)
- **Other clients** — position catch-up spawn when receiving `SpawnConfirmedClientRpc`
- **All clients** — snapshot reconciliation to correct position drift

**Offline spawn (game code)**

```csharp
// Offline / LocalOnly mode — call directly
manager.Spawn2D(spawnPoints, count, configId, ownerLocalId, damageMultiplier);
manager.Spawn3D(spawnPoints, count, configId, ownerLocalId, damageMultiplier); // 3D ref
```

**Offline targets (game code)**

```csharp
manager.RegisterTarget2D(in CollisionTarget target, int unityLayer = 0);
manager.RegisterTarget3D(in CollisionTarget3D target, int unityLayer = 0); // 3D ref
manager.DeactivateTarget2D(uint targetId);
manager.ClearAllTargets();
```

**Events (offline hit handling)**

```csharp
event Action<LocalHitPayload> OnHit;         // offline damage events
event Action<uint>            OnProjectileDied;
```

**Networked API (called automatically by bridge — not typically called from game code)**

| Member | Description |
|---|---|
| `SpawnFiringClientBatch2D(pts, count, configId, speed)` | Immediate visual for firing client; returns temp base ID. |
| `SpawnNetworkBatch2D(SpawnConfirmation, elapsedSinceSpawn)` | Catch-up spawn for other clients. |
| `LinkNetworkProjectileBatch(realBaseId, count)` | Swap temp IDs to real server IDs. |
| `KillNetworkProjectile(uint projId)` | Kill on server hit confirmation. |
| `ReconcileSnapshots2D(snapshots, count, currentServerTick, tickInterval)` | Correct position drift. Extrapolates snapshot forward before comparing to eliminate zig-zag. |

**Properties**

| Member | Type | Description |
|---|---|---|
| `ActiveCount2D` / `ActiveCount3D` | `int` | Live projectile count. |
| `HasInstance` | `bool` | Singleton null-safe check. |

---

### `RustSimAdapter`

**Namespace:** `MidManStudio.Projectiles.Adapters`  
Hit processor owned by `ServerProjectileAuthority`. Maintains per-projectile `ServerProjectileData`, evaluates damage, fires events.

| Member | Description |
|---|---|
| `Register(ServerProjectileData data)` | Called by authority after spawn. |
| `Unregister(uint projId)` | Called when projectile should die. |
| `IsRegistered(uint projId)` | Checked by authority after each collision. |
| `ProcessHit(in HitResult hit, bool isHeadshot)` | 2D hit → compute damage → fire `OnProjectileHit` → `HandlePiercing`. |
| `ProcessHit3D(in HitResult3D hit, bool isHeadshot)` | *(3D ref)* |
| `NotifyDead(uint projId)` | Called by `CompactDead` for lifetime-expired projectiles. Guards against double-fire. |
| `SetHomingDirection2D(ref NativeProjectile, Vector2)` | Writes `Ax/Ay` for `Guided` movement. |
| `event Action<ProjectileHitPayload> OnProjectileHit` | Subscribe here for damage application. |
| `event Action<uint> OnProjectileDied` | Fires when any projectile dies (hit or lifetime). |

---

## 10. Raycast Handler

### `RaycastProjectileHandler : NetworkBehaviour`

**Namespace:** `MidManStudio.Projectiles.Managers`

Handles hitscan projectiles. Server re-validates the client's reported hit position; spawns a travelling pool visual on all other clients.

**Game code usage (client side)**

```csharp
// After performing your own raycast
system.RegisterRaycastFire(new RaycastFireResult
{
    Origin             = barrelTip,
    Direction          = aimDir.normalized,
    HitPoint           = hitPoint,
    DidHit             = didHit,
    HitTargetNetworkId = targetNetworkId,
    IsHeadshot         = isHeadshot,
    Is3D               = false         // 3D ref
}, configId, context);
```

**Inspector fields**

| Field | Default | Description |
|---|---|---|
| `_hitValidationTolerance` | `2f` | Max world-unit discrepancy before server rejects client hit. |
| `_serverRaycastLayers` | `Everything` | Layers the server-side validation raycast tests. |
| `_trustClientOnValidationMiss` | `true` | Fall back to client report when server raycast misses (handles desynced positions). |
| `_visualTravelSpeed` | `40f` | Speed of the travelling visual on other clients. |

**Events**

| Event | Signature | Description |
|---|---|---|
| `OnServerHitConfirmed` | `Action<ProjectileHitPayload>` | Server-confirmed hit with damage. Subscribe for damage application. |

> **2D raycast fix:** The server-side 2D validation uses `ContactFilter2D` with `useTriggers = true`, regardless of the `Physics2D.queriesHitTriggers` project setting. This ensures trigger colliders are always included in server re-validation.

---

## 11. Physics Projectiles

Physics projectiles use a `NetworkObject` pool rather than the Rust sim. The firing client spawns a temporary pool visual; the server spawns the real `NetworkObject` and runs Rigidbody physics.

### `PhysicsProjectileBase : NetworkTransform` (abstract)

**Namespace:** `MidManStudio.Projectiles.Managers`

Base for all physics projectiles. Manages pool visual lifecycle, damage from config, and NGO spawn.

```csharp
// Server only — call after SpawnPhysicsProjectile() returns the NetworkObject
proj.SetOwnerContext(ownerMidId, firedByNetworkObjectId, isBotOwner, weaponLevel, damageMultiplier);
proj.InitialiseProjectile(ownerMidId, firedByNetworkObjectId, bulletVelocity);
```

| Member | Description |
|---|---|
| `SetOwnerContext(...)` | Set attribution data before `InitialiseProjectile`. |
| `InitialiseProjectile(ulong ownerMidId, ulong firedByNetObjId, float bulletVelocity, ...)` | Server only — syncs NetworkVariables, triggers launch. |
| `ShouldAutoSpawnVisual` | `protected virtual bool` — override `false` in derived classes that manage their own pool visual. |
| `OnNetworkVelocityReceived()` | `protected virtual void` — called on clients when `BulletVelocity` NetworkVariable arrives. |
| `OnProjectileInitialised()` | `protected virtual void` — called server-side after initialise. |
| `OnHitServerConfirmed` | `event Action<ProjectileHitPayload>` — server-confirmed hit with computed damage. |

**Damage:** `PhysicsProjectileBase` evaluates the config's `DamageCurve` using travel distance from `_spawnPosition`. If no config is registered for `_visualConfigId`, falls back to `_baseDamage * damageMultiplier`.

---

### `PhysicsProjectile2D : PhysicsProjectileBase` *(2D primary)*

`[RequireComponent(typeof(Rigidbody2D))]`

Fires along `transform.right` at `BulletVelocity`. `GravityScale` sourced from registered config. Handles `OnCollisionEnter2D` and `OnTriggerEnter2D`.

---

### `PhysicsProjectile3D : PhysicsProjectileBase` *(3D reference)*

`[RequireComponent(typeof(Rigidbody)), RequireComponent(typeof(SphereCollider))]`

Fires along `transform.forward`. `useGravity` set from `config.GravityScale > 0`. Handles `OnCollisionEnter` and `OnTriggerEnter`.

---

## 12. Network Bridge and Client Prediction

### `MID_ProjectileNetworkBridge : NetworkBehaviour`

**Namespace:** `MidManStudio.Projectiles.Network`

All NGO RPCs for the projectile system. Attached to `MID_MasterProjectileSystem`'s references automatically.

**Game code typically does not call this directly** — `MID_MasterProjectileSystem` routes through it.

**RPCs (server→client)**

| RPC | Direction | Description |
|---|---|---|
| `SpawnConfirmedClientRpc` | Server → other clients | Spawns fresh Rust buffer entries for projectiles fired by a remote player. |
| `LinkProjectileIdsClientRpc` | Server → firing client only | Swaps temp IDs in client's existing buffer to real server IDs. No new entries spawned. |
| `HitConfirmedClientRpc` | Server → all | Kill projectile in client buffer; notify prediction; play impact. |
| `SendSnapshotClientRpc` | Server → all clients | Position snapshots for reconciliation. Wave/Circular excluded (filtered server-side). |

**RPCs (client→server)**

| RPC | Description |
|---|---|
| `FireServerRpc` | Process a `RustSim` fire request. |
| `RaycastFireServerRpc` | Process a raycast fire request with client hit report. |
| `FirePhysicsProjectileServerRpc` | Spawn a physics pool projectile. |

**Events**

| Event | Description |
|---|---|
| `OnHitConfirmedLocal` | `Action<HitConfirmation>` — fires on all clients (including server) for every confirmed hit. |

**`ProjectileFireRequest : INetworkSerializable`**

| Field | Type | Description |
|---|---|---|
| `ConfigId` | `ushort` | |
| `Origin` / `Direction` | `Vector3` | Primary bullet origin and direction. |
| `Speed` | `float` | Resolved speed. |
| `ProjectileCount` | `byte` | Bullet count (max 255). |
| `OwnerMidId` / `FiredByNetworkObjectId` | `ulong` | Attribution. |
| `ClientFireTick` | `int` | Used to compute latency compensation. |
| `ExtraDirectionCount` / `ExtraDirections` | `byte / Vector3[]` | Additional directions for multi-projectile patterns (max 63 extra). |

**`SpawnConfirmation : INetworkSerializable`**

| Field | Type | Description |
|---|---|---|
| `BaseProjId` | `uint` | Base of the server-assigned ID range. |
| `ProjectileCount` | `byte` | |
| `ConfigId` / `Speed` | `ushort / float` | |
| `Origin` / `Direction` | `Vector3` | |
| `ServerNetworkTime` | `float` | `NetworkManager.ServerTime.TimeAsFloat` at spawn; used by other clients to compute elapsed flight time for position catch-up. |
| `GetDirection(int i)` | `Vector3` | Returns `Direction` for i=0, `ExtraDirections[i-1]` for i>0. |

**`HitConfirmation : INetworkSerializable`**

| Field | Type | Description |
|---|---|---|
| `ProjId` | `uint` | |
| `TargetNetworkId` | `ulong` | NGO NetworkObject ID of the target. |
| `Damage` | `float` | Final computed damage. |
| `HitPosition` | `Vector3` | |
| `IsHeadshot` / `IsCrit` | `bool` | |
| `ConfigId` | `ushort` | |

---

### `ClientPredictionManager : MonoBehaviour`

**Namespace:** `MidManStudio.Projectiles.Network`

Manages temporary pool visuals for **physics projectiles only** during the RPC round-trip. The Rust sim path no longer uses this for visual management — `LocalProjectileManager` handles that directly.

```csharp
// Spawn a travelling visual for a physics projectile (firing client)
uint handle = prediction.SpawnLocalPhysicsVisual(configId, origin, direction, speed);

// Kill it when the real NetworkObject arrives on this client
prediction.KillPhysicsVisual(handle);
```

**Static rotation utilities** (used by `RaycastProjectileHandler` and `PhysicsProjectileBase`):

```csharp
Quaternion rot = ClientPredictionManager.GetDirectionRotation(dir);
ClientPredictionManager.ApplyDirectionRotation(transform, dir);
```

---

### `DeterministicMotionMath` (static internal class)

**Namespace:** `MidManStudio.Projectiles.Network`

Closed-form position and velocity-direction formulas for `Wave` and `Circular` movement types. Formulas exactly match the Rust tick equations in `simulation.rs` — any divergence causes visual oscillation.

> Used internally by the reconciliation system. Available to game code for custom client-side visualisation of Wave/Circular projectiles.

```csharp
// Perpendicular axis — MUST match BatchSpawnHelper.GetAccel2D/3D
Vector3 perp2D = DeterministicMotionMath.ComputePerpAxis2D(dir);
Vector3 perp3D = DeterministicMotionMath.ComputePerpAxis3D(dir);

// 2D position at timeAlive seconds
Vector3 pos = DeterministicMotionMath.CalculateWave2DPosition(
    origin, dirX, dirY, speed, amplitude, frequency, phaseOffset,
    perp2D.x, perp2D.y, timeAlive);

Vector3 pos2 = DeterministicMotionMath.CalculateCircular2DPosition(
    origin, vx0, vy0, angularSpeedRad, startAngleRad, timeAlive);
```

---

## 13. Visual Systems

### `ProjectileRenderer2D : MonoBehaviour` *(2D primary renderer)*

**Namespace:** `MidManStudio.Projectiles.Visuals`  
`[RequireComponent(typeof(ProjectileManager))]`

GPU `DrawMeshInstanced` path (hardware instancing) with `DrawMesh` combined-mesh fallback. Groups projectiles by config ID per draw call. Reads live positions directly from the Rust `NativeProjectile[]` buffer every `LateUpdate`.

```csharp
// Called by ServerProjectileAuthority.LateUpdate() (host) and LocalProjectileManager.LateUpdate() (client)
renderer2D.Render(NativeProjectile[] projs, int count);
```

**Inspector fields**

| Field | Description |
|---|---|
| `_atlasMaterial` | Assign `MidMan/InstancedProjectile` or `MidMan/InstancedProjectile_URP` material. |
| `_forceDrawMesh` | Force combined-mesh path even when hardware instancing is available. |

**Shader requirements:**
- Built-in RP: `MidMan/InstancedProjectile.shader` — reads per-instance `_UVRect` and `_Color` from `MaterialPropertyBlock`
- URP: `MidMan/InstancedProjectile_URP.shader` — same plus `#ifdef UNITY_INSTANCING_ENABLED` to correctly handle combined-mesh path

---

### `ProjectileRenderer3D : MonoBehaviour` *(3D reference renderer)*

**Namespace:** `MidManStudio.Projectiles.Visuals`

Billboard renderer — aligns an elongated quad along the velocity direction, perpendicular to the camera. Uses `Graphics.DrawMesh` with two separate meshes (sprite pass + shape pass) to prevent GPU geometry clobber.

```csharp
renderer3D.Render(NativeProjectile3D[] projs, int count);
```

---

### `ProjectileVisualBase : MonoBehaviour` (abstract)

**Namespace:** `MidManStudio.Projectiles.Visuals`

Abstract base for all pooled visual objects. Subclass to create 2D or 3D pool visuals.

```csharp
// Called by ClientPredictionManager / RaycastProjectileHandler
void InitializeClientVisual(ushort configId, Vector3 origin, Vector3 direction, float speed);

// Return to LocalObjectPool
void ReturnToPoolImmediate();

// Suppress rendering without returning to pool
virtual void HideProjectile();
```

**Properties:** `ConfigId`, `Origin`, `Direction`, `Speed`, `IsActive`

**Abstract hooks to override:**

| Method | Description |
|---|---|
| `OnInitialise(ProjectileConfigSO cfg)` | Apply sprite, mesh, trail, particles. `cfg` may be null. |
| `OnReturnToPool()` | Clear trail, stop particles, reset materials. |
| `ApplyRotation(Vector3 dir)` | Apply visual rotation toward travel direction. |

---

### `ProjectileVisual_ : ProjectileVisualBase` *(2D primary pool visual)*

**Namespace:** `MidManStudio.Projectiles.Visuals`  
*(Note the underscore — this is the actual class name.)*

2D pool visual for `PoolableObjectType.Projectile_Visual2D`. Handles sprite rendering, trail, and optional custom shape mesh.

**Prefab setup:**
- Add `ProjectileVisual_` component
- Add `SpriteRenderer` → assign to `projectileSpriteRend`
- Add `TrailRenderer` → assign to `projectileTrailRend`
- `MeshFilter` / `MeshRenderer` are added at runtime automatically when a config with `CustomShape` is first used — **do not pre-add them to the prefab**

**Inspector fields**

| Field | Description |
|---|---|
| `projectileSpriteRend` | Main sprite renderer. |
| `projectileTrailRend` | Trail renderer. |
| `_fallbackShapeMaterial` | Material for runtime-created shape mesh. Assign `InstancedProjectile.shader` material for correct atlas UV support. |
| `_spriteSortingOrder` / `_trailSortingOrder` / `_shapeSortingOrder` | Sorting orders. |

---

### `ProjectileVisual3D : ProjectileVisualBase` *(3D reference pool visual)*

**Namespace:** `MidManStudio.Projectiles.Visuals`

3D pool visual for `PoolableObjectType.Projectile_Visual3D`. Uses `MeshFilter + MeshRenderer` with a default procedural capsule mesh. `TrailRenderer` is auto-found in children if not assigned.

Virtual hooks for extension: `OnInitialise3D(cfg)` and `OnCleanup3D()`.

---

### `ProjectileImpactHandler : Singleton<ProjectileImpactHandler>`

**Namespace:** `MidManStudio.Projectiles.Visuals`

Client-side impact effect manager. Called by `MID_ProjectileNetworkBridge.HitConfirmedClientRpc`.

**Routing priority:**
1. `GlobalFXManager` (when `_preferGlobalFX = true` and instance is present)
2. Per-config `ImpactRegistration` strategy
3. Default `LocalParticlePool` fallback

```csharp
// Register per-config GlobalFX override (e.g. in Awake after configs are registered)
impactHandler.RegisterConfigEffectType(configId, EffectType.FleshSurface, particleCount: 10);
impactHandler.RegisterConfigEffectType(configId, EffectType.LargeExplosion, particleCount: 20);

// Register legacy strategy (non-GlobalFX path)
impactHandler.RegisterStrategy(configId, new ImpactRegistration
{
    Strategy     = ImpactStrategy.PooledParticleSystem,
    ParticleType = PoolableParticleType.Projectile_Impact
});

// Unregister
impactHandler.UnregisterConfigEffectType(configId);
impactHandler.UnregisterStrategy(configId);
```

**`PlayImpact(Vector3 position, ushort configId, bool isHeadshot = false)`** — called automatically by bridge.

**`ImpactStrategy` enum**

| Value | Description |
|---|---|
| `PooledParticleSystem` | `LocalParticlePool.GetObject()` — standard particle |
| `SpriteSheetFlipbook` | Pooled `GameObject` with `SpriteRenderer` + `ImpactFlipbook` |
| `SharedEmit` | `ParticleSystem.Emit()` — for very high hit rates |

---

### `TrailObjectPool : MonoBehaviour`

**Namespace:** `MidManStudio.Projectiles.Visuals`  
`[RequireComponent(typeof(ProjectileManager))]`

Manages `TrailRendererPool` slots for Rust-sim projectiles. Positions are synced from the Rust buffer every `FixedUpdate`.

```csharp
// Called by ServerProjectileAuthority and LocalProjectileManager — not typically called directly
trailPool.SyncToSimulation(NativeProjectile[] projs, int count);     // 2D primary
trailPool.SyncToSimulation3D(NativeProjectile3D[] projs, int count); // 3D ref
trailPool.NotifyDead(uint projId);   // Release slot on projectile death
trailPool.ReleaseAll();              // Release all slots (e.g. on scene change)
```

---

## 14. Data Types

### `ServerProjectileData`

**Namespace:** `MidManStudio.Projectiles.Data`

Server-side gameplay data for one projectile. Lives in `RustSimAdapter._projData`, keyed by `projectileId_u32`. Stores everything the damage system needs; position is owned by Rust and never duplicated here.

```csharp
// Create template for a fire event
var template = new ServerProjectileData(
    ownerMidId:         context.OwnerMidId,
    firedById:          context.FiredByNetworkObjectId,
    isBot:              context.IsBotOwner,
    level:              context.WeaponLevel,
    spawnPos2D:         new Vector2(origin.x, origin.y),
    damageMultiplierIn: context.DamageMultiplier,
    config:             cfg,
    killTypeRaw:        (int)MyGame.KillType.Bullet,   // game-specific, stored as int
    damageTypeRaw:      0,
    weaponTypeRaw:      0);

// Clone per projectile in a batch
ServerProjectileData data = template.CloneForSpawn(projId, configId);
ServerProjectileData data3D = template.CloneForSpawn3D(projId, configId, spawnPos3D);
```

**Key fields**

| Field | Type | Description |
|---|---|---|
| `projectileId_u32` | `uint` | Matches `NativeProjectile.ProjId`. Primary key. |
| `configId` | `ushort` | |
| `is3D` | `bool` | |
| `ownerClientId` | `ulong` | MID ID of firing entity. |
| `firedByNetworkObjectId` | `ulong` | NGO ID of weapon/character. |
| `damageMultiplier` | `float` | |
| `isCrit` | `bool` | Pre-rolled at spawn by `ServerProjectileAuthority`. |
| `critChance` | `float` | Cached from config. |
| `collisionsRemaining` | `byte` | Decremented by `HandlePiercing`. |
| `hasHit` | `bool` | Set true by `HandlePiercing` when projectile should die. |
| `KillTypeRaw` / `DamageTypeRaw` / `WeaponTypeRaw` | `int` | Game-specific attribution stored as raw int. Cast to your enums in your hit handler. |
| `IsDead()` | `bool` | True when `hasHit && collisionsRemaining <= 0`. |

---

### `ProjectileHitPayload`

**Namespace:** `MidManStudio.Projectiles.Adapters`

Payload fired by `RustSimAdapter.OnProjectileHit` (server) and `RaycastProjectileHandler.OnServerHitConfirmed` (server).

| Field | Type | Description |
|---|---|---|
| `ProjId` | `uint` | |
| `ConfigId` | `ushort` | |
| `Is3D` | `bool` | |
| `TargetId` | `uint` | Target's NGO NetworkObjectId (cast from ulong) |
| `Damage` | `float` | Final damage after curve + headshot + crit + multiplier |
| `IsHeadshot` / `IsCrit` | `bool` | |
| `HitPosition` | `Vector3` | |
| `OwnerMidId` | `ulong` | |
| `FiredByNetworkObjectId` | `ulong` | |
| `IsBotOwner` | `bool` | |
| `WeaponLevel` | `byte` | |
| `GameData` | `ServerProjectileData` | Full server data (may be null for raycast path) |

---

### `LocalHitPayload`

**Namespace:** `MidManStudio.Projectiles.Managers`

Payload fired by `LocalProjectileManager.OnHit` (offline mode only).

| Field | Type | Description |
|---|---|---|
| `ProjId` / `ConfigId` | `uint / ushort` | |
| `Is3D` | `bool` | |
| `Target` | `LocalDamageTarget` | The full offline target object (may be null for struct-based targets) |
| `RawTargetId` | `uint` | Target ID from collision result |
| `Damage` | `float` | |
| `IsHeadshot` / `IsCrit` | `bool` | |
| `HitPosition` | `Vector3` | |
| `OwnerLocalId` | `uint` | |

---

### `SpawnPoint`

**Namespace:** `MidManStudio.Projectiles.Adapters`

Input to `BatchSpawnHelper` and `MID_MasterProjectileSystem.Fire()`.

```csharp
public struct SpawnPoint
{
    public Vector3 Origin;
    public Vector3 Direction;  // normalised
    public float   Speed;      // 0 = use config speed
}
```

---

### `RaycastFireResult`

**Namespace:** `MidManStudio.Projectiles.Managers`

Input to `MID_MasterProjectileSystem.RegisterRaycastFire()`.

```csharp
public struct RaycastFireResult
{
    public Vector3 Origin;
    public Vector3 Direction;
    public Vector3 HitPoint;
    public bool    DidHit;
    public ulong   HitTargetNetworkId;  // NGO NetworkObjectId
    public bool    IsHeadshot;
    public bool    Is3D;
}
```

---

## 15. Assembly Definitions

### Runtime — `MidManStudio.ProjectileSystem`

**Path:** `packages/com.midmanstudio.projectilesystem/Runtime/MidManStudio.ProjectileSystem.asmdef`

```json
{
  "name": "MidManStudio.ProjectileSystem",
  "rootNamespace": "MidManStudio.Projectiles",
  "references": [
    "MidManStudio.Utilities",
    "MidManStudio.Netcode",
    "Unity.Netcode.Runtime",
    "Unity.Netcode.Components",
    "Unity.Burst",
    "Unity.Collections",
    "Unity.Mathematics"
  ],
  "allowUnsafeCode": true,
  "autoReferenced": true
}
```

`allowUnsafeCode: true` is required for `GCHandle.AddrOfPinnedObject()` used in the Rust FFI boundary.

### Editor — `MidManStudio.ProjectileSystem.Editor`

**Path:** `packages/com.midmanstudio.projectilesystem/Editor/MidManStudio.ProjectileSystem.Editor.asmdef`

```json
{
  "name": "MidManStudio.ProjectileSystem.Editor",
  "rootNamespace": "MidManStudio.Projectiles.Editor",
  "references": [
    "MidManStudio.ProjectileSystem",
    "MidManStudio.Utilities",
    "MidManStudio.Utilities.Editor",
    "MidManStudio.Netcode",
    "Unity.Netcode.Runtime"
  ],
  "includePlatforms": ["Editor"],
  "autoReferenced": false
}
```

### Reference Diagram

```
YourGame.asmdef
├── MidManStudio.Utilities      (autoReferenced — implicit)
├── MidManStudio.Netcode        (autoReferenced — implicit)
└── MidManStudio.ProjectileSystem  (autoReferenced — implicit)
    ├── MidManStudio.Utilities
    ├── MidManStudio.Netcode
    ├── Unity.Netcode.Runtime
    ├── Unity.Netcode.Components
    ├── Unity.Burst
    ├── Unity.Collections
    └── Unity.Mathematics

YourGame.Editor.asmdef
├── MidManStudio.Utilities.Editor
├── MidManStudio.Netcode.Editor
└── MidManStudio.ProjectileSystem.Editor
```
