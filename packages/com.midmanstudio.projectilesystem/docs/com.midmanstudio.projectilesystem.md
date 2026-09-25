# com.midmanstudio.projectilesystem

## Overview

Unity package for projectile simulation (RustSim, raycast, and physics paths)
backed by the native projectile_core Rust library, with Netcode for
GameObjects integration. This document currently covers the `Tests/` folder:
a self-contained test/demo scene (player movement and dimension switching,
weapon firing and inventory, PvP health, shootable dummy targets, a local
lobby, and a manual benchmark harness) used to exercise the runtime package
end to end. These are test-scene components, not a unit test suite; there is
no automated test runner covering them (see CI and Workflows below).

## Modules

### `NetworkedDimensionPlayer.cs`
**What it does:** Player rig: movement, dash, mouse look, dimension (2D/3D)
switching and the Rigidbody constraints that go with it, and the owner/remote
tint. Auto-creates its head pivot and shot point transforms if left unassigned.

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
inventory (pickup, ownership, switching with an Animator trigger).

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

### `WeaponController.cs`
- `SpawnPhysicsProjectileLocal()`'s `SetGuidedTarget()` call must run after
  `InitialiseProjectile()`, because `SetupMovementType` resets any guided
  target on every fresh launch (see the matching note in
  `MID_ProjectileNetworkBridge.FirePhysicsProjectileServerRpc`). This
  constraint was documented on the original `NetworkedDimensionPlayer` copy
  of this code, but the comment was not carried over when the logic was
  copied into this file. Added it back.

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
