# projectile_core

## Overview

Native Rust simulation core for the `com.midmanstudio.projectilesystem` Unity
package (RustSim mode). Built as a `cdylib`/`staticlib`/`rlib` and called from
C# through a plain `extern "C"` surface in `lib.rs`. The crate holds no
persistent target or projectile lists of its own for the hot path: `ticked`
projectile arrays, collision targets, and shape colliders are all owned and
upserted on the C# side (`ServerProjectileAuthority`/`LocalProjectileManager`
in the Unity package) and passed in by pointer every call. The one exception
is per-config Wave/Circular movement parameters (`config_store.rs`), which are
registered once and read every tick from a small Rust-side store, since they
are tuning data rather than per-instance state.

## Modules

### `lib.rs`
**What it does:** The exported `extern "C"` surface every other module is
reached through — movement/tick functions, the two collision-check entry
points (grid-based circle/sphere and shape), save/restore for client
reconciliation, movement-pattern-param registration, and struct-size getters
C# uses to validate its own marshalled struct layouts match this build.

### `collision.rs`
**What it does:** Broadphase (spatial hash grid) plus narrow-phase circle/
sphere overlap test for plain `CollisionTarget`/`CollisionTarget3D` entries
(the common case: players, enemies, simple round targets). SIMD-batched where
available.

### `shapes.rs`
**What it does:** A second, additive narrow-phase for `ShapeCollider2D`/
`ShapeCollider3D` — Box, Capsule, Edge, and hand-authored Polygon/curve
targets, represented as a shared point-loop + thickness format. Deliberately
separate from `collision.rs` so the circle/sphere SIMD fast path never has to
carry a variable-length point buffer. See Fixes and Problems below.

### `simulation.rs`
**What it does:** Per-tick projectile integration — position from velocity,
guided/homing acceleration, wave/circular motion (via `config_store.rs`),
lifetime and max-range expiry. SIMD batch paths (AVX2 and a portable
fallback) are tested directly against a scalar reference implementation for
equivalence.

### `patterns.rs`
**What it does:** Expands one `SpawnRequest` into the actual projectile
array for a shot pattern (single, 3/5-way spread, spiral, ring-of-8).

### `state.rs`
**What it does:** Raw byte snapshot save/restore of the projectile array, for
client-side reconciliation against a server snapshot.

### `config_store.rs`
**What it does:** Per-config-id parameter store for the Wave and Circular
movement types — the one piece of state this crate keeps between calls.
`RwLock<HashMap>` on desktop/mobile, `thread_local!` + `RefCell` on WASM
(single-threaded, no `std::sync`), selected at compile time.

### `simd.rs` / `simd_avx2.rs` / `math/`
**What it does:** SIMD abstraction layers (portable 4-wide, AVX2 8-wide) and
the `Vec2`/`Vec3`/`f32x4`-style math primitives `simulation.rs` and
`collision.rs` build on.

## Fixes and Problems

### `shapes.rs`
- `check_hits_shapes_2d`/`check_hits_shapes_3d` tested every shape — closed
  ones (Box, closed Polygon) included — purely by distance from the
  projectile to the nearest edge SEGMENT, with no separate test for whether
  the projectile's center had simply entered the shape's interior. A closed
  shape is meant to read as a solid: a Box target (a typical wall) only
  registered a hit within roughly one projectile-radius of one of its four
  edges, so a shot anywhere else on the wall's face — the middle of it, which
  is most of the wall for anything of normal size — passed straight through
  with no collision at all. Open shapes (Capsule, Edge) have no well-defined
  "interior" and were never affected; the bug was specific to `closed != 0`.
  Reproduced with a new test (`closed_shape_interior_counts_as_a_hit`:
  projectile at the exact center of a 2x2 box, 1 unit from every edge) that
  failed before the fix and passes after. Fixed in `check_hits_shapes_2d` by
  adding an even-odd point-in-polygon test, used as an alternative hit
  condition alongside the existing edge-distance test (`point_in_closed_shape_2d`,
  only consulted when `s.closed != 0`); `open_shape_interior_style_point_still_needs_proximity`
  guards against the fix accidentally treating open shapes as filled.
  `check_hits_shapes_3d` was not touched — see below.
- The equivalent 3D case (a `BoxCollider` approximated as a capsule by
  `RustSimTargetRegistrar.BakeBoxApprox3D` on the Unity side) is a different
  mechanism, not this bug: 3D box approximation always registers as an OPEN
  shape (`closed: false`), so it already used the correct edge-distance test
  for what it actually is — a capsule. The remaining gap there is that a
  single capsule radius (the two non-spine half-extents averaged together)
  under-covers a wide, tall, thin wall's full height, not a missing interior
  test. No change made in this pass — averaging vs. taking the max of the two
  cross-section extents trades one shape profile's coverage for another's
  (a tall thin wall improves, a wide thin floor/ceiling gets worse), and nothing
  in the current `ShapeCollider3D` format (one radius, not an ellipse) can fix
  both without a real change to how 3D boxes are represented. For a 3D static
  box that needs to register hits reliably across its whole face right now,
  `RustSimCustomShapeAuthoring`'s point-by-point authoring is the documented
  way to get an exact shape instead of the auto-detected approximation.
