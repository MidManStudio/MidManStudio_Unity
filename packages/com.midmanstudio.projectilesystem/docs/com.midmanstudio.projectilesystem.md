# com.midmanstudio.projectilesystem

## Overview

Unity package for projectile simulation (RustSim, raycast, and physics paths)
backed by the native projectile_core Rust library, with Netcode for
GameObjects integration. This document currently covers the `Tests/` folder
plus the runtime files listed under Modules that carry a `Runtime` note:
a self-contained test/demo scene (player movement and dimension switching,
weapon firing and inventory, PvP health, shootable dummy targets, a local
lobby, and a manual benchmark harness) used to exercise the runtime package
end to end. These are test-scene components, not a unit test suite; there is
no automated test runner covering them (see CI and Workflows below).

The `Tests/` scripts call `MID_NativeAudioBridge`, which lives in
`com.midmanstudio.audio`. The Tests assembly therefore references
`MidManStudio.Audio`, and the package lists the audio package as a dependency.
The runtime assembly has no audio dependency.

## Modules

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

### `WeaponDefinitionSO.cs` (Runtime)
**What it does:** Per-weapon tuning asset. `SwitchAnimTrigger` is no longer
read by anything; the field stays so existing assets keep their data.

### `ProjectileConfigScriptableObjectEditor.cs` (Editor)
**What it does:** Inspector for `ProjectileConfigSO` and every subclass of it.
Draws all serialized fields except the custom-path ones, then the custom path
panel (single selection only) and the Apply JSON panel. Because
`ProjectileConfigSO` derives from `MID_BaseSO` but this editor takes
precedence over `MID_BaseSOEditor`, it also does that editor's icon-cache
invalidation when the custom icon changes.

### `PhysicsProjectileBase.cs` (Runtime)
**What it does:** Shared base for the 2D and 3D physics projectiles, including
the per-tick movement types (Wave, Circular, Guided, Teleport, CustomCurve).
`CustomCurve` is physics-only: the native sim has no tick for movement type 6
and flies such a shot straight. The path is absolute geometry, so the path's
own length is the total distance covered over the config's `Lifetime` and the
config's bullet velocity does not change it.

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

### `TestTarget.cs` / `TestTarget2D.cs`
**What they do:** Shootable dummy targets implementing `IDamageable`, for the
3D and 2D dimensions respectively. Registered with `TestSceneBootstrapper` by
a `RegistrationId` so hits can be routed back generically.

### `NetworkTurretTarget.cs`
**What it does:** A networked turret-style target that checks
`PlayerHealth.IsAlive` on whatever it is tracking before acting on it.

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

### `NetworkPlayerObjectObserverProvider.cs`
**What it does:** `IProjectileObserverProvider` implementation used for
network-visibility/culling decisions around player NetworkObjects.

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

### `ProjectileSystemBenchmarkWindow.cs`
**What it does:** Editor window UI for configuring and running
`ProjectileSystemBenchmark` from inside the Unity Editor.

### `RustSimTargetRegistrar.cs` / `RustSimCustomShapeAuthoring.cs` (Runtime)
**What it does:** Components that register a GameObject as a RustSim collision
target (auto-detected circle or shape for the registrar, hand-authored point
loop for the custom shape authoring). Moving targets re-register on the update
cadence; a target with Is Static on registers, then only refreshes on a slow
real-time interval (`_staticRefreshSeconds`, default 0.5, 0 = never).

### `MID_MasterProjectileSystem.cs` (Runtime)
**What it does:** Entry point for target and shape registration. Routes each
call to `ServerProjectileAuthority` when this is a server and to
`LocalProjectileManager` when offline, using the network state at the moment of
the call. `CanAcceptTargets` reports whether a call made now would reach a
backend. The `Log System Status` context menu prints backend target and shape
counts.

`Fire` takes an optional `fireSeed`. It must be the seed that was passed to
`ProjectileDirectionResolver.Resolve` when the spawn points were built. The Rust
sim path sends it in `ProjectileFireRequest`, and the local path does not need it
because it uses the spawn points directly.

### `ServerProjectileAuthority.cs` / `LocalProjectileManager.cs` (Runtime)
**What it does:** The two projectile backends. Each owns its own target and
shape buffers (upserted by TargetId) that are passed to the native library
every tick. Both expose `TargetCount2D/3D` and `ShapeCount2D/3D` for
diagnostics.

**Burst timing:** each backend owns a `DelayedSpawnQueue` and advances it at the
start of `FixedUpdate`, before the sim tick. A shot whose `SpawnPoint[]` has a
start delay on any entry takes a one-projectile-at-a-time path:
`ServerProjectileAuthority.SpawnStaggered` on the server,
`SpawnStaggeredOffline`, `SpawnFiringClientStaggered` and `SpawnNetworkStaggered`
in `LocalProjectileManager`. Shots with no delay keep the original batch path
unchanged. A delayed projectile is written to the buffer when its time comes, so
the native sim, collision, snapshots and renderers never see a pending state.

**Decisions:**
- The native struct has no start delay field, and changing it would need all
  platform libraries rebuilt and the SIMD tick and collision code changed. The
  managed queue avoids that.
- The whole id block is reserved up front. Projectile `i` always has id
  `baseId + i`, spawned now or later, so the firing client's temp-to-real id link
  keeps working. `LinkNetworkProjectileBatch` also relinks entries still in the
  queue, and `KillNetworkProjectile` drops them.
- On the server, latency compensation only shortens the projectile's lifetime,
  it never moves the projectile. Taking it from the start delay would spawn the
  delayed projectiles on top of each other, so every projectile keeps its full
  delay and gets the same lifetime cut. Other clients do move the projectile
  forward, so they subtract the time since the server spawn from each delay and
  advance the projectile by what remains. The spacing between projectiles stays
  the same.
- A delayed projectile spawns at the origin the shot had when it was fired. The
  muzzle position is not on the wire, so following the muzzle would differ between
  peers.
- The catch-up math for late spawns moved from two inline loops into
  `ApplyCatchUp2D` and `ApplyCatchUp3D`, shared by the batch and queue paths.

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

### `ProjectilePatternSO.cs` / `ProjectilePatternEditor.cs` (Runtime and Editor)
**What it does:** Pattern asset for multi-projectile shots, and its inspector.
The shape settings give one angle pair per projectile. The burst settings give
each projectile a start delay.

**Burst timing:** `BurstInterval` is the gap between consecutive projectiles in
seconds, 0 releases the whole pattern at once. `BurstJitter` changes each delay
by a random fraction of the interval. `BurstOrder` picks which projectile leaves
first: `Index`, `Reverse`, or `CenterOut` (middle index first, symmetric pairs
leave together). The order works on the index, so for the V shape, which lists
both arms before the center, the middle index is not the center projectile. `GetStartDelay(index, count, seed)` is a pure function of its inputs
and is clamped to `MaxStartDelay` (2 s). The inspector shows the total length of
the burst.

**Fire seed:** `MixSeed(fireSeed)` combines the asset's own `RngSeed` with the
per-shot seed. `SampleDirections(count, seed)` uses it for the shotgun shape, and
`GetSpeedMultiplier` and `GetStartDelay` use it for speed variance and jitter. A
fire seed of 0 returns the asset seed, which keeps the old fixed layout.

**Ammo count:** `GetFiredProjectileCount()` returns the number of projectiles one
shot fires without allocating. It differs from `ProjectileCount` for the V shape,
which builds symmetric arms.

### `ProjectileDirectionResolver.cs` (Runtime)
**What it does:** Expands a fire event into `SpawnPoint[]` with direction, speed
and start delay per projectile. The firing client, the server and every other
client call it with the same inputs. `Resolve` takes an optional `fireSeed`,
`NewFireSeed()` picks one on the firing side. The seed is 16 bits and travels in
`ProjectileFireRequest` and `SpawnConfirmation`.

### `BatchSpawnHelper.cs` (Runtime)
**What it does:** Writes `SpawnPoint[]` into the native projectile buffers, with a
Burst job for batches of 8 or more. `SpawnPoint.StartDelay` is carried by the
struct but not read here. `DelayedSpawnQueue` has already held the projectile back
by the time it reaches this file.

### `DelayedSpawnQueue.cs` (Runtime)
**What it does:** Managed queue of projectiles that start after the shot. Entries
carry the spawn point, the projectile id, the config id, the base speed, the
seconds already flown, the spawn path (`DelayedSpawnKind`) and an owner specific
context object. `Advance(dt, spawn)` moves a clock by the fixed step and passes
due entries to the owner in queue order. `Relink` swaps a temp id for a real one,
`Remove` drops an entry, `AnyDelayed` tells a spawn path whether it needs the
queue at all.

**Decisions:**
- The clock is the sum of fixed steps, the same time base the sim runs on, so a
  delay of 0.08 s is four 0.02 s steps whatever the frame rate.
- A due entry spawns at the start of the next fixed step, before the sim tick, so
  it moves in the step it appears in.
- Entries are removed by compacting the list in one pass, which keeps queue order.

### `MID_ProjectileNetworkBridge.cs` (Runtime)
**What it does:** Network bridge between the client fire call and the server sim.
`ProjectileFireRequest` and `SpawnConfirmation` each gained a `FireSeed` field,
2 bytes on the wire. `FireServerRpc` resolves the shot with it, and a shot with
any start delay goes through `ServerProjectileAuthority.SpawnStaggered`. The
confirmation count is the number of projectiles spawned or queued.

**Limits:** raycast fire and physics fire resolve with seed 0 and ignore start
delays, so they keep the fixed layout. Burst timing applies to the Rust sim and to
local fire.

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

## CI and Workflows

None of the workflows below run the `Tests/` scene or any automated test
suite; `build.yml` builds the app for Android and Windows only.

- `.github/workflows/build-projectile-rust-libs.yml` - builds the native
  `projectile_core` Rust library for macOS and Windows on changes under
  `rust_lib/projectile_core/**`.
- `.github/workflows/bench-projectile.yml` - runs the Criterion benchmarks
  for `projectile_core` (simulation tick and collision) on push to `main`
  under `rust_lib/projectile_core/**`, or manually. Posts a markdown summary
  and commits `.github/bench-results/projectile-bench-results.json` and
  `index.html`.
- `.github/workflows/release-packages.yml` - tags and releases this package
  (`com.midmanstudio.projectilesystem`) from `packages/com.midmanstudio.projectilesystem`
  on a `projectilesystem/v*` tag or manual dispatch.

## Fixes and Problems

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

### `RustSimTargetRegistrar.cs` / `RustSimCustomShapeAuthoring.cs`
- The earlier static-target fix (keep ticking until the first registration
  succeeds) only checked that the projectile system existed, then marked the
  target registered. `MID_MasterProjectileSystem` sends each call to one of two
  separate buffers based on the network state at that instant (server
  authority when `IsServer`, local manager when offline), and silently does
  nothing if the matching backend is null or this is a pure client. A static
  target registers once, so a call that landed in the wrong buffer or nowhere
  was never repeated: registered offline and then started as host, the target
  sat in the local buffer while projectiles simulated in the server buffer. A
  moving target hid this because it re-registers every tick. Fixed two ways:
  `RegisterNow()` stays unregistered while `CanAcceptTargets` is false, and a
  static target now refreshes its registration every `_staticRefreshSeconds`
  (real time) instead of stopping for good, so it lands in whichever backend is
  active. Setting the field to 0 restores the old register-once behavior.

### `MID_MasterProjectileSystem.cs`
- Added `CanAcceptTargets`, and backend target and shape counts to
  `Log System Status`, so it is visible which buffer holds which targets.

### `ServerProjectileAuthority.cs` / `LocalProjectileManager.cs`
- Added read-only `TargetCount2D/3D` and `ShapeCount2D/3D` properties.

### `PhysicsProjectileBase.cs`
- A `CustomCurve` path formula that divides by zero or takes the root of a
  negative number gives a non-finite position, and `ApplyCustomCurve()` passed
  the resulting NaN or infinite velocity to the Rigidbody, which is an engine
  error and leaves the projectile unusable. The update is now skipped when the
  velocity is not finite, with a one-time warning per launch that names the
  config. This was added as hardening; it is not confirmed to be the cause of
  any reported symptom.

### `WeaponDefinitionSO.cs`
- The `SwitchAnimTrigger` tooltip described a trigger that is no longer fired.
  It now says the field is unused.

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

### `ProjectileImpactHandler.cs`
- Removed an unused `using MidManStudio.Core.Audio;`. The file uses no audio
  type. The directive would stop the file from compiling once the audio code
  left the utilities assembly for `com.midmanstudio.audio`, since the runtime
  assembly does not reference the audio assembly.

### `ProjectilePatternSO.cs`
- Shotgun patterns fired the same layout every shot. The seed was baked into the
  asset, so every peer agreed but no two shots differed. The shotgun shape, speed
  variance and burst jitter now take a per-shot seed from the fire request.
- Removed the `Debug.Log` after creating the default pattern assets in the editor
  menu command.

### `ProjectileDirectionResolver.cs`
- Resolve no longer ignores the shot. The seed argument is optional, so existing
  callers compile unchanged and keep the old fixed layout until they pass one.

### `LocalProjectileManager.cs`
- The late-spawn catch-up code existed twice, inline in `SpawnNetworkBatch2D` and
  `SpawnNetworkBatch3D`. It moved into `ApplyCatchUp2D` and `ApplyCatchUp3D` with
  the same arithmetic.

### `ServerProjectileAuthority.cs`
- A burst fired by a non-host client came out as one shot. The latency of the
  shot was taken from each start delay, and a delay shorter than the latency
  spawned the projectile at once on the same point as the first. The delay is
  kept whole now.

### `DelayedSpawnQueue.cs`
- Known limit: a queued projectile spawns even if its shooter dies before it
  leaves. The burst is over in well under a second at the highest settings the
  inspector allows for normal weapons, so this was left alone.
- Known limit: a queued projectile that was linked to its server id after the
  server confirmation arrived is relinked in `LinkNetworkProjectileBatch`. If the
  confirmation never arrives, the projectile spawns with its temp id and is
  removed by lifetime like any other.

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
