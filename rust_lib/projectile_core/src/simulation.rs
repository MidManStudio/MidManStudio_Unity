// rust_lib/projectile_core/src/simulation.rs
//
// REFACTOR: Wide-vector batch paths now use crate::math::{Vec2x4, Vec3x4, f32x4}
// instead of raw SSE2-only intrinsics. Platform dispatch is entirely inside those
// types — simulation.rs has zero #[cfg(target_arch = "...")] guards.
//
// PLATFORM COVERAGE CHANGE:
//   Before: wide batch path = SSE2 only (x86/x86_64).
//           aarch64 (iOS, Android ARM64, Apple Silicon) used scalar loop.
//   After:  wide batch path = SSE2 | NEON | scalar-4wide.
//           aarch64 now uses NEON 4-wide path — same throughput class as x86.
//           WASM uses scalar-4wide (correct, no SIMD per WASM threading model).
//
// WHAT CHANGED:
//   - tick_all:          removed #[cfg] dispatch; single unified loop body.
//   - tick_all_3d:       same.
//   - tick_straight_x4:    REPLACED raw SSE2 → Vec2x4/f32x4. (Renamed from
//                           tick_straight_arching_x4 when MOVE_ARCHING was
//                           removed — see the REMOVED note further down.)
//   - tick_straight_x4_3d: REPLACED raw SSE2 → Vec3x4/f32x4. Same rename.
//   - tick_all_sse2:         REMOVED (SSE2-only entry point, no longer needed).
//   - tick_all_3d_sse2:      REMOVED.
//   - tick_all_scalar:       REMOVED (inlined into tick_all loop).
//   - tick_all_3d_scalar:    REMOVED (inlined into tick_all_3d loop).
//
// WHAT DID NOT CHANGE:
//   All scalar per-movement-type functions are byte-for-byte identical.
//   Batch eligibility rule: alive_and == 1 && mt_or == MOVE_STRAIGHT.
//   Semi-implicit Euler integration order: vel += accel*dt; pos += vel*dt.
//   travel_dist accumulation uses updated velocity (same as old SSE2 path).
//   All circular/wave/guided/teleport types still use the scalar path —
//   these require config store reads and branching that defeats 4-wide batching.
//
// REMOVED: MOVE_ARCHING. tick_arching/tick_arching_3d were byte-for-byte
// identical to tick_straight/tick_straight_3d — same vel += accel*dt;
// pos += vel*dt integration, same accel-gather (which already handled
// gravity via a nonzero Ay set at spawn time regardless of movement type).
// The only thing Arching did that Straight didn't was increment curve_t/
// timer_t, and nothing ever read either back for the Arching case specifically
// (curve_t/timer_t are shared accumulators genuinely used by Teleport/Wave/
// Circular's own ticks, not by Arching's). A gravity-affected arc is just
// Straight with Ay set — there was no second mode here, just one mode with
// a redundant name and a write-only counter nobody consumed. MOVE_ARCHING's
// old byte value (1) is intentionally left unassigned rather than reused —
// any old data still carrying it decodes through tick_scalar_one's default
// arm as tick_straight, which is byte-for-byte what it always computed anyway.
//
// MATH VERIFICATION (see DeterministicMotionMath.cs cross-check comments):
//   MOVE_CIRCULAR 2D:  velocity rotation by ω·dt → true arc, zero drift.
//                      Matches ∫ V·cos(θ₀+ωt), V·sin(θ₀+ωt) closed form. ✓
//   MOVE_WAVE 2D:      Euler integral A·sin(f·2π·t+φ)·dt.
//                      Matches A·(cos(φ) − cos(f·2π·t+φ))/(f·2π). ✓
//   MOVE_CIRCULAR 3D:  orbital velocity = d/dt[R·(cos(ωt)·u + sin(ωt)·v₂)].
//                      Integral = R·(cos(at)−cos(a₀))·u + R·(sin(at)−sin(a₀))·v₂. ✓
//   MOVE_WAVE 3D:      Same integral as 2D applied to 3D perp axis. ✓
//   Perp axes:         2D (−dir.y, dir.x) | 3D (−dir.y/xyLen, dir.x/xyLen, 0).
//                      Both match BatchSpawnHelper.GetAccel2D/3D exactly. ✓

use crate::{NativeProjectile, NativeProjectile3D};
use crate::config_store;
use crate::simd::{fast_atan2, fast_inv_sqrt, fast_sqrt};
// f32x4 module and f32x4 type share the same identifier; importing the TYPE
// requires the full submodule path to avoid ambiguity with the module name.
use crate::math::f32x4::f32x4;
use crate::math::{Vec2x4, Vec3x4};
#[cfg(target_arch = "x86_64")]
use crate::simd_avx2::{self, f32x8, Vec2x8, Vec3x8};

const RAD2DEG: f32 = 57.295_779_51_f32;

pub const MOVE_STRAIGHT: u8 = 0;
// MOVE_ARCHING (was 1) removed — see file header "REMOVED" note. Left
// unassigned, not reused, so old serialized data carrying byte value 1
// still decodes sensibly (falls through tick_scalar_one's default arm to
// tick_straight — the exact computation Arching always ran anyway).
pub const MOVE_GUIDED:   u8 = 2;
pub const MOVE_TELEPORT: u8 = 3;
pub const MOVE_WAVE:     u8 = 4;
pub const MOVE_CIRCULAR: u8 = 5;

/// True if the AVX2 8-wide tier should be attempted on this platform right
/// now — false immediately (no cpuid check at all) on anything that isn't
/// x86_64, true-or-false based on the cached runtime check on x86_64 itself.
/// See simd_avx2.rs's module header for why this is a runtime check, not a
/// compile-time target-feature gate.
#[inline]
fn avx2_tier_available() -> bool {
    #[cfg(target_arch = "x86_64")]
    { simd_avx2::avx2_available() }
    #[cfg(not(target_arch = "x86_64"))]
    { false }
}

#[inline]
fn batch_qualifies(projs: &[NativeProjectile], start: usize, count: usize) -> bool {
    let mut alive_and: u8 = 1;
    let mut mt_or: u8 = 0;
    for p in &projs[start..start + count] {
        alive_and &= p.alive;
        mt_or     |= p.movement_type;
    }
    alive_and == 1 && mt_or == MOVE_STRAIGHT
}

// =============================================================================
//  2D — tick entry point
// =============================================================================

/// Tick all 2D projectiles. Returns count that died this tick.
///
/// Dispatches Straight projectiles to the widest available batch tier at
/// each position — AVX2 8-wide first (x86_64 only, runtime-checked), then
/// SSE2/NEON/WASM/scalar 4-wide, falling to scalar 1-at-a-time for whatever
/// doesn't fill a batch or isn't Straight. All other movement types always
/// fall to scalar.
pub fn tick_all(projs: &mut [NativeProjectile], dt: f32) -> i32 {
    let n        = projs.len();
    let mut died = 0_i32;
    let mut i    = 0_usize;
    let avx2_ok  = avx2_tier_available();

    while i < n {
        #[cfg(target_arch = "x86_64")]
        if avx2_ok && i + 8 <= n && batch_qualifies(projs, i, 8) {
            // SAFETY: avx2_ok came from a runtime is_x86_feature_detected!
            // check (see avx2_tier_available/simd_avx2::avx2_available) —
            // the precondition #[target_feature(enable="avx2")] requires.
            unsafe { tick_straight_x8(&mut projs[i..i + 8], dt, &mut died); }
            i += 8;
            continue;
        }

        if i + 4 <= n && batch_qualifies(projs, i, 4) {
            tick_straight_x4(&mut projs[i..i + 4], dt, &mut died);
            i += 4;
            continue;
        }

        tick_scalar_one(&mut projs[i], dt, &mut died);
        i += 1;
    }
    died
}

// =============================================================================
//  2D — 4-wide batch tick (straight)
//
//  Vec2x4 dispatches internally to:
//    x86/x86_64 → SSE2   (_mm_add_ps, _mm_mul_ps, rsqrt+NR, fast_atan2_x4)
//    aarch64    → NEON   (vaddq_f32, vmulq_f32, vrsqrteq+NR, fast_atan2_x4)
//    others     → scalar 4-wide array (correct, no SIMD)
// =============================================================================

/// Process exactly 4 Straight 2D projectiles simultaneously.
///
/// Preconditions (checked by tick_all):
///   projs.len() == 4, all alive, movement_type == MOVE_STRAIGHT.
fn tick_straight_x4(projs: &mut [NativeProjectile], dt: f32, died: &mut i32) {
    debug_assert_eq!(projs.len(), 4);

    let dt4  = f32x4::splat(dt);
    let zero = f32x4::splat(0.0_f32);

    // ── Lifetime ──────────────────────────────────────────────────────────────
    // Subtract dt from all 4 lifetimes in one SIMD op, check for expiry.
    let lt     = f32x4::load_from(projs, 0, |p| p.lifetime);
    let lt_new = lt - dt4;
    let dead   = lt_new.cmple(zero);

    if dead.any() {
        // At least one died — write new lifetimes and mark dead per-lane (scalar).
        // This branch is rarely taken in a hot sim (most bullets live for >1 tick).
        let arr = lt_new.to_array();
        for j in 0..4 {
            projs[j].lifetime = arr[j];
            if projs[j].lifetime <= 0.0 {
                projs[j].alive = 0;
                *died += 1;
            }
        }
        return;
    }
    lt_new.store_to(projs, 0, |p, v| p.lifetime = v);

    // ── Velocity integration: vel += accel * dt ───────────────────────────────
    // ax = 0, ay = gravityAy when this config wants an arc — set by C# at
    // spawn via RustSpawnParams. Nothing movement-type-specific here: Straight
    // with a nonzero Ay already IS an arc, there's no separate mode for it.
    // The accel gather handles zero-accel (a true straight line) correctly too.
    let accel   = Vec2x4::load_accel(projs, 0);
    let mut vel = Vec2x4::load_vel(projs, 0);

    vel = vel + accel * dt4;      // semi-implicit: update vel first

    // ── Position integration: pos += vel_new * dt ────────────────────────────
    let mut pos = Vec2x4::load_pos(projs, 0);
    pos = pos + vel * dt4;        // then advance pos with updated vel

    // ── Travel distance: |vel_new| * dt ──────────────────────────────────────
    // Computed as length of step vector (vel_new * dt) = |vel_new| * |dt|.
    // Uses inv_sqrt approximation: ~23-bit on SSE2/NEON, exact on scalar.
    // Matches old SSE2: dist_add = sqrt(len_sq(dx, dy)) where dx = vx_new * dt.
    let step       = vel * dt4;           // displacement this tick
    let dist_delta = step.length_fast();  // sqrt(dx² + dy²)
    dist_delta.add_to(projs, 0, |p| &mut p.travel_dist);

    // ── Visual rotation angle: atan2(vy, vx) in degrees ──────────────────────
    // angle_deg drives the sprite/mesh rotation. Stored as degrees to match
    // NativeProjectile layout and Rust sim convention.
    let angle_deg = vel.angle_deg();
    angle_deg.store_to(projs, 0, |p, v| p.angle_deg = v);

    // ── Write back velocity and position ──────────────────────────────────────
    vel.store_vel(projs, 0);
    pos.store_pos(projs, 0);

    // ── Per-projectile tail: scale growth ──────────────────────────────────────
    // scale_speed == 0 in the vast majority of projectiles (optimised out early
    // inside tick_scale). This is intentionally scalar — it operates on a
    // rarely-mutated field and SIMD packing overhead would exceed the
    // computation cost.
    for j in 0..4 {
        tick_scale(&mut projs[j], dt);
    }
}

// =============================================================================
//  2D — 8-wide batch tick (straight) — AVX2, x86_64 only, runtime-dispatched
//
//  Byte-for-byte the same math as tick_straight_x4 above, 8 lanes instead of
//  4 — cross-check that function's comments for the reasoning behind each
//  step; not repeated here. The one structural difference: f32x8/Vec2x8 use
//  inherent unsafe methods (.add()/.mul()) instead of operator overloading —
//  see simd_avx2.rs's header for why (#[target_feature] is incompatible
//  with core::ops trait signatures).
// =============================================================================

/// Process exactly 8 Straight 2D projectiles simultaneously.
///
/// Preconditions (checked by tick_all): projs.len() == 8, all alive,
/// movement_type == MOVE_STRAIGHT, AND the caller has already confirmed
/// AVX2 is available on this CPU via a runtime check (avx2_tier_available/
/// simd_avx2::avx2_available) — that's what makes the unsafe block below
/// sound. See simd_avx2.rs's module header for the full safety story.
#[cfg(target_arch = "x86_64")]
#[target_feature(enable = "avx2")]
unsafe fn tick_straight_x8(projs: &mut [NativeProjectile], dt: f32, died: &mut i32) {
    debug_assert_eq!(projs.len(), 8);

    let dt8  = f32x8::splat(dt);
    let zero = f32x8::zero();

    // ── Lifetime ──────────────────────────────────────────────────────────────
    let lt     = f32x8::load_from(projs, 0, |p| p.lifetime);
    let lt_new = lt.sub(dt8);
    let dead   = lt_new.cmple(zero);

    if dead.any() {
        // At least one of the 8 died — same rare-branch handling as tick_straight_x4.
        let arr = lt_new.to_array();
        for j in 0..8 {
            projs[j].lifetime = arr[j];
            if projs[j].lifetime <= 0.0 {
                projs[j].alive = 0;
                *died += 1;
            }
        }
        return;
    }
    lt_new.store_to(projs, 0, |p, v| p.lifetime = v);

    // ── Velocity integration: vel += accel * dt ───────────────────────────────
    let accel   = Vec2x8::load_accel(projs, 0);
    let mut vel = Vec2x8::load_vel(projs, 0);
    vel = vel.add(accel.mul_wide(dt8));

    // ── Position integration: pos += vel_new * dt ────────────────────────────
    let mut pos = Vec2x8::load_pos(projs, 0);
    pos = pos.add(vel.mul_wide(dt8));

    // ── Travel distance: |vel_new| * dt ──────────────────────────────────────
    let step       = vel.mul_wide(dt8);
    let dist_delta = step.length_fast();
    dist_delta.add_to(projs, 0, |p| &mut p.travel_dist);

    // ── Visual rotation angle: atan2(vy, vx) in degrees ──────────────────────
    let angle_deg = vel.angle_deg();
    angle_deg.store_to(projs, 0, |p, v| p.angle_deg = v);

    // ── Write back velocity and position ──────────────────────────────────────
    vel.store_vel(projs, 0);
    pos.store_pos(projs, 0);

    // ── Per-projectile tail: scale growth ──────────────────────────────────────
    for j in 0..8 {
        tick_scale(&mut projs[j], dt);
    }
}

// =============================================================================
//  2D — scalar per-projectile tick  (unchanged)
// =============================================================================

fn tick_scalar_one(p: &mut NativeProjectile, dt: f32, died: &mut i32) {
    if p.alive == 0 { return; }
    p.lifetime -= dt;
    if p.lifetime <= 0.0 { p.alive = 0; *died += 1; return; }

    match p.movement_type {
        MOVE_STRAIGHT => tick_straight(p, dt),
        MOVE_GUIDED   => tick_guided(p, dt),
        MOVE_TELEPORT => tick_teleport(p, dt),
        MOVE_WAVE     => tick_wave(p, dt),
        MOVE_CIRCULAR => tick_circular(p, dt),
        // Default arm also catches old data still carrying the removed
        // MOVE_ARCHING byte value (1) — see file header "REMOVED" note.
        _             => tick_straight(p, dt),
    }

    tick_scale(p, dt);

    if p.movement_type != MOVE_TELEPORT && p.movement_type != MOVE_CIRCULAR {
        if p.vx != 0.0 || p.vy != 0.0 {
            p.angle_deg = fast_atan2(p.vy, p.vx) * RAD2DEG;
        }
        let dx = p.vx * dt;
        let dy = p.vy * dt;
        p.travel_dist += fast_sqrt(dx * dx + dy * dy);
    }
}

// =============================================================================
//  2D — per-movement-type scalar implementations  (all unchanged from prior)
// =============================================================================

#[inline(always)]
fn tick_straight(p: &mut NativeProjectile, dt: f32) {
    p.vx += p.ax * dt;
    p.vy += p.ay * dt;
    p.x  += p.vx * dt;
    p.y  += p.vy * dt;
}

#[inline(always)]
fn tick_guided(p: &mut NativeProjectile, dt: f32) {
    let turn_rate = core::f32::consts::PI * dt;

    let cur_len_sq = p.vx * p.vx + p.vy * p.vy;
    let inv_cur    = fast_inv_sqrt(cur_len_sq.max(1e-8));
    let cur_angle  = fast_atan2(p.vy * inv_cur, p.vx * inv_cur);

    let tgt_len_sq = p.ax * p.ax + p.ay * p.ay;
    let inv_tgt    = fast_inv_sqrt(tgt_len_sq.max(1e-8));
    let tgt_angle  = fast_atan2(p.ay * inv_tgt, p.ax * inv_tgt);

    let mut delta = tgt_angle - cur_angle;
    if delta >  core::f32::consts::PI { delta -= core::f32::consts::TAU; }
    if delta < -core::f32::consts::PI { delta += core::f32::consts::TAU; }
    let delta     = delta.clamp(-turn_rate, turn_rate);
    let new_angle = cur_angle + delta;

    let speed = fast_sqrt(cur_len_sq);
    p.vx = new_angle.cos() * speed;
    p.vy = new_angle.sin() * speed;
    p.x += p.vx * dt;
    p.y += p.vy * dt;
}

#[inline(always)]
fn tick_teleport(p: &mut NativeProjectile, dt: f32) {
    const INTERVAL: f32 = 0.12;
    p.curve_t += dt;
    if p.curve_t >= INTERVAL {
        p.curve_t -= INTERVAL;
        let spd_sq = p.vx * p.vx + p.vy * p.vy;
        let inv    = fast_inv_sqrt(spd_sq.max(1e-8));
        let jump   = INTERVAL / inv;
        p.x += p.vx * inv * jump;
        p.y += p.vy * inv * jump;
        p.travel_dist += jump;
    }
}

#[inline(always)]
fn tick_wave(p: &mut NativeProjectile, dt: f32) {
    p.x       += p.vx * dt;
    p.y       += p.vy * dt;
    p.curve_t += dt;

    if let Some(wp) = config_store::get_wave(p.config_id) {
        let phase  = p.curve_t * wp.frequency * core::f32::consts::TAU + wp.phase_offset;
        let offset = wp.amplitude * phase.sin();
        p.x += p.ax * offset * dt;
        p.y += p.ay * offset * dt;
    }
}

/// MOVE_CIRCULAR 2D — smooth curving arc via velocity rotation.
///
/// Rotates the velocity vector by ω·dt each tick.
/// This preserves speed and produces a clean circular arc with zero position drift.
///
/// start_angle_deg: applied once at the first tick (curve_t == 0) as a velocity
/// pre-rotation, letting callers choose which quadrant the curve opens toward.
///
/// Closed-form match: integrating V·(cos(θ₀+ωt), sin(θ₀+ωt)) gives
///   P(t) = origin + (V/ω)·(sin(θ₀+ωt)−sin(θ₀)) x̂ − (cos(θ₀+ωt)−cos(θ₀)) ŷ
/// exactly as DeterministicMotionMath.CalculateCircular2DPosition. ✓
#[inline(always)]
fn tick_circular(p: &mut NativeProjectile, dt: f32) {
    if let Some(cp) = config_store::get_circular(p.config_id) {
        let omega = cp.angular_speed.to_radians();

        // First tick pre-rotation (curve_t == 0 only before any tick has run).
        if p.curve_t == 0.0 && cp.start_angle_deg != 0.0 {
            let init_rad = cp.start_angle_deg.to_radians();
            let (ci, si) = (init_rad.cos(), init_rad.sin());
            let ivx = p.vx * ci - p.vy * si;
            let ivy = p.vx * si + p.vy * ci;
            p.vx = ivx;
            p.vy = ivy;
        }

        // Rotate velocity by omega*dt — preserves magnitude, continuously curves path.
        let theta        = omega * dt;
        let (cos_t, sin_t) = (theta.cos(), theta.sin());
        let new_vx       = p.vx * cos_t - p.vy * sin_t;
        let new_vy       = p.vx * sin_t + p.vy * cos_t;
        p.vx = new_vx;
        p.vy = new_vy;
    }

    p.curve_t += dt;
    p.x += p.vx * dt;
    p.y += p.vy * dt;

    if p.vx != 0.0 || p.vy != 0.0 {
        p.angle_deg = fast_atan2(p.vy, p.vx) * RAD2DEG;
    }
    let dx = p.vx * dt;
    let dy = p.vy * dt;
    p.travel_dist += fast_sqrt(dx * dx + dy * dy);
}

#[inline(always)]
fn tick_scale(p: &mut NativeProjectile, dt: f32) {
    if p.scale_speed == 0.0 { return; }
    let diff = p.scale_target - p.scale_x;
    if diff.abs() > 0.001 {
        p.scale_x += diff * p.scale_speed * dt;
        p.scale_y  = p.scale_x;
    }
}

// =============================================================================
//  3D — tick entry point
// =============================================================================

#[inline]
fn batch_qualifies_3d(projs: &[NativeProjectile3D], start: usize, count: usize) -> bool {
    let mut alive_and: u8 = 1;
    let mut mt_or: u8 = 0;
    for p in &projs[start..start + count] {
        alive_and &= p.alive;
        mt_or     |= p.movement_type;
    }
    alive_and == 1 && mt_or == MOVE_STRAIGHT
}

/// Tick all 3D projectiles. Returns count that died this tick.
///
/// Same dispatch order as tick_all (2D): AVX2 8-wide first (x86_64, runtime-
/// checked), then SSE2/NEON/WASM/scalar 4-wide, falling to scalar 1-at-a-time.
pub fn tick_all_3d(projs: &mut [NativeProjectile3D], dt: f32) -> i32 {
    let n        = projs.len();
    let mut died = 0_i32;
    let mut i    = 0_usize;
    let avx2_ok  = avx2_tier_available();

    while i < n {
        #[cfg(target_arch = "x86_64")]
        if avx2_ok && i + 8 <= n && batch_qualifies_3d(projs, i, 8) {
            // SAFETY: see tick_straight_x8's matching comment — identical
            // precondition, identical guarantee.
            unsafe { tick_straight_x8_3d(&mut projs[i..i + 8], dt, &mut died); }
            i += 8;
            continue;
        }

        if i + 4 <= n && batch_qualifies_3d(projs, i, 4) {
            tick_straight_x4_3d(&mut projs[i..i + 4], dt, &mut died);
            i += 4;
            continue;
        }

        tick_scalar_one_3d(&mut projs[i], dt, &mut died);
        i += 1;
    }
    died
}

// =============================================================================
//  3D — 4-wide batch tick (straight)
//
//  Vec3x4 dispatches internally to:
//    x86/x86_64 → SSE2  (3 × f32x4 SoA, same intrinsics as old path)
//    aarch64    → NEON  (3 × float32x4_t SoA via Vec3x4)
//    others     → scalar 4-wide array
//
//  No angle_deg update — 3D rotation is derived from velocity direction
//  in NativeProjectile3D.VisualRotation() (C# Quaternion.LookRotation).
// =============================================================================

fn tick_straight_x4_3d(projs: &mut [NativeProjectile3D], dt: f32, died: &mut i32) {
    debug_assert_eq!(projs.len(), 4);

    let dt4  = f32x4::splat(dt);
    let zero = f32x4::splat(0.0_f32);

    // ── Lifetime ──────────────────────────────────────────────────────────────
    let lt     = f32x4::load_from(projs, 0, |p| p.lifetime);
    let lt_new = lt - dt4;
    let dead   = lt_new.cmple(zero);

    if dead.any() {
        let arr = lt_new.to_array();
        for j in 0..4 {
            projs[j].lifetime = arr[j];
            if projs[j].lifetime <= 0.0 {
                projs[j].alive = 0;
                *died += 1;
            }
        }
        return;
    }
    lt_new.store_to(projs, 0, |p, v| p.lifetime = v);

    // ── Velocity integration: vel += accel * dt ───────────────────────────────
    // (ax, ay, az) = (0, gravityAy, 0) when this config wants an arc. Nothing
    // movement-type-specific here — see the 2D batch tick's matching comment.
    let accel   = Vec3x4::load_accel(projs, 0);
    let mut vel = Vec3x4::load_vel(projs, 0);

    vel = vel + accel * dt4;      // vel += accel * dt

    // ── Position integration: pos += vel_new * dt ────────────────────────────
    let mut pos = Vec3x4::load_pos(projs, 0);
    pos = pos + vel * dt4;        // pos += updated vel * dt

    // ── Travel distance: |vel_new * dt| ──────────────────────────────────────
    // step = vel_new * dt (displacement vector this tick).
    // length_fast = sqrt(dx² + dy² + dz²) via inv_sqrt approximation.
    // Identical semantics to old SSE2 path: dist_add = sqrt(len_sq(dx,dy,dz)).
    let step       = vel * dt4;
    let dist_delta = step.length_fast();
    dist_delta.add_to(projs, 0, |p| &mut p.travel_dist);

    // ── Write back ────────────────────────────────────────────────────────────
    vel.store_vel(projs, 0);
    pos.store_pos(projs, 0);

    // ── Per-projectile tail: scale growth ──────────────────────────────────────
    for j in 0..4 {
        tick_scale_3d(&mut projs[j], dt);
    }
}

// =============================================================================
//  3D — 8-wide batch tick (straight) — AVX2, x86_64 only, runtime-dispatched
//
//  Byte-for-byte the same math as tick_straight_x4_3d above — see that
//  function's comments; not repeated here. Same inherent-method-vs-operator
//  note as tick_straight_x8 (2D). No angle_deg update, same reason as the
//  4-wide 3D tier: 3D rotation is derived from velocity direction in C#.
// =============================================================================

#[cfg(target_arch = "x86_64")]
#[target_feature(enable = "avx2")]
unsafe fn tick_straight_x8_3d(projs: &mut [NativeProjectile3D], dt: f32, died: &mut i32) {
    debug_assert_eq!(projs.len(), 8);

    let dt8  = f32x8::splat(dt);
    let zero = f32x8::zero();

    // ── Lifetime ──────────────────────────────────────────────────────────────
    let lt     = f32x8::load_from(projs, 0, |p| p.lifetime);
    let lt_new = lt.sub(dt8);
    let dead   = lt_new.cmple(zero);

    if dead.any() {
        let arr = lt_new.to_array();
        for j in 0..8 {
            projs[j].lifetime = arr[j];
            if projs[j].lifetime <= 0.0 {
                projs[j].alive = 0;
                *died += 1;
            }
        }
        return;
    }
    lt_new.store_to(projs, 0, |p, v| p.lifetime = v);

    // ── Velocity integration: vel += accel * dt ───────────────────────────────
    let accel   = Vec3x8::load_accel(projs, 0);
    let mut vel = Vec3x8::load_vel(projs, 0);
    vel = vel.add(accel.mul_wide(dt8));

    // ── Position integration: pos += vel_new * dt ────────────────────────────
    let mut pos = Vec3x8::load_pos(projs, 0);
    pos = pos.add(vel.mul_wide(dt8));

    // ── Travel distance: |vel_new * dt| ──────────────────────────────────────
    let step       = vel.mul_wide(dt8);
    let dist_delta = step.length_fast();
    dist_delta.add_to(projs, 0, |p| &mut p.travel_dist);

    // ── Write back ────────────────────────────────────────────────────────────
    vel.store_vel(projs, 0);
    pos.store_pos(projs, 0);

    // ── Per-projectile tail: scale growth ──────────────────────────────────────
    for j in 0..8 {
        tick_scale_3d(&mut projs[j], dt);
    }
}

// =============================================================================
//  3D — scalar per-projectile tick  (unchanged)
// =============================================================================

fn tick_scalar_one_3d(p: &mut NativeProjectile3D, dt: f32, died: &mut i32) {
    if p.alive == 0 { return; }
    p.lifetime -= dt;
    if p.lifetime <= 0.0 { p.alive = 0; *died += 1; return; }

    match p.movement_type {
        MOVE_STRAIGHT => tick_straight_3d(p, dt),
        MOVE_GUIDED   => tick_guided_3d(p, dt),
        MOVE_TELEPORT => tick_teleport_3d(p, dt),
        MOVE_WAVE     => tick_wave_3d(p, dt),
        MOVE_CIRCULAR => tick_circular_3d(p, dt),
        // Default arm also catches old data still carrying the removed
        // MOVE_ARCHING byte value (1) — see file header "REMOVED" note.
        _             => tick_straight_3d(p, dt),
    }

    tick_scale_3d(p, dt);

    if p.movement_type != MOVE_TELEPORT && p.movement_type != MOVE_CIRCULAR {
        let dx = p.vx * dt; let dy = p.vy * dt; let dz = p.vz * dt;
        p.travel_dist += fast_sqrt(dx*dx + dy*dy + dz*dz);
    }
}

// =============================================================================
//  3D — per-movement-type scalar implementations  (all unchanged from prior)
// =============================================================================

#[inline(always)]
fn tick_straight_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.vx += p.ax * dt; p.vy += p.ay * dt; p.vz += p.az * dt;
    p.x  += p.vx * dt; p.y  += p.vy * dt; p.z  += p.vz * dt;
}

#[inline(always)]
fn tick_guided_3d(p: &mut NativeProjectile3D, dt: f32) {
    let turn_rate = core::f32::consts::PI * dt;

    let spd_sq  = p.vx*p.vx + p.vy*p.vy + p.vz*p.vz;
    let inv_spd = fast_inv_sqrt(spd_sq.max(1e-8));
    let (cx, cy, cz) = (p.vx*inv_spd, p.vy*inv_spd, p.vz*inv_spd);

    let tgt_sq  = p.ax*p.ax + p.ay*p.ay + p.az*p.az;
    let inv_tgt = fast_inv_sqrt(tgt_sq.max(1e-8));
    let (tx, ty, tz) = (p.ax*inv_tgt, p.ay*inv_tgt, p.az*inv_tgt);

    let dot   = (cx*tx + cy*ty + cz*tz).clamp(-1.0, 1.0);
    let angle = dot.acos();

    if angle > 0.0001 {
        let t  = (turn_rate / angle).min(1.0);
        let nx = cx + (tx - cx) * t;
        let ny = cy + (ty - cy) * t;
        let nz = cz + (tz - cz) * t;
        let inv_n = fast_inv_sqrt((nx*nx + ny*ny + nz*nz).max(1e-8));
        let speed = fast_sqrt(spd_sq);
        p.vx = nx*inv_n*speed; p.vy = ny*inv_n*speed; p.vz = nz*inv_n*speed;
    }
    p.x += p.vx * dt; p.y += p.vy * dt; p.z += p.vz * dt;
}

#[inline(always)]
fn tick_teleport_3d(p: &mut NativeProjectile3D, dt: f32) {
    const INTERVAL: f32 = 0.12;
    p.timer_t += dt;
    if p.timer_t >= INTERVAL {
        p.timer_t -= INTERVAL;
        let spd_sq = p.vx*p.vx + p.vy*p.vy + p.vz*p.vz;
        let inv    = fast_inv_sqrt(spd_sq.max(1e-8));
        let jump   = INTERVAL / inv;
        p.x += p.vx*inv*jump; p.y += p.vy*inv*jump; p.z += p.vz*inv*jump;
        p.travel_dist += jump;
    }
}

#[inline(always)]
fn tick_wave_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.x += p.vx * dt; p.y += p.vy * dt; p.z += p.vz * dt;
    p.timer_t += dt;

    if let Some(wp) = config_store::get_wave(p.config_id) {
        let phase  = p.timer_t * wp.frequency * core::f32::consts::TAU + wp.phase_offset;
        let offset = wp.amplitude * phase.sin();
        p.x += p.ax * offset * dt;
        p.y += p.ay * offset * dt;
        p.z += p.az * offset * dt;
    }
}

/// MOVE_CIRCULAR 3D — helical motion around the forward travel axis.
///
/// Adds orbital velocity = d/dt[R·(cos(ωt+a₀)·u + sin(ωt+a₀)·v₂)] per frame:
///   orb_vel = R·ω·(−sin(ωt+a₀)·u + cos(ωt+a₀)·v₂)
///
/// u  = first perpendicular axis (ax, ay, az) set at spawn by BatchSpawnHelper.
/// v₂ = normalize(forward) × u (second perpendicular, computed each tick).
///
/// Closed-form match: integrating orb_vel over [0,t] gives
///   R·(cos(at)−cos(a₀))·u + R·(sin(at)−sin(a₀))·v₂
/// exactly as DeterministicMotionMath.CalculateCircular3DPosition. ✓
#[inline(always)]
fn tick_circular_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.timer_t += dt;

    if let Some(cp) = config_store::get_circular(p.config_id) {
        let omega = cp.angular_speed.to_radians();
        let angle = p.timer_t * omega + cp.start_angle_deg.to_radians();

        // Forward direction (normalised velocity)
        let spd_sq  = p.vx*p.vx + p.vy*p.vy + p.vz*p.vz;
        let inv_spd = fast_inv_sqrt(spd_sq.max(1e-8));
        let (fx, fy, fz) = (p.vx*inv_spd, p.vy*inv_spd, p.vz*inv_spd);

        // First perp axis u (stored in ax, ay, az at spawn by BatchSpawnHelper)
        let (ux, uy, uz) = (p.ax, p.ay, p.az);

        // Second perp v₂ = forward × u  (right-hand cross product)
        let (vx2, vy2, vz2) = (
            fy*uz - fz*uy,
            fz*ux - fx*uz,
            fx*uy - fy*ux,
        );

        // Orbital velocity: d/dt[R·(cos(ωt+a₀)·u + sin(ωt+a₀)·v₂)]
        let sin_a  = angle.sin();
        let cos_a  = angle.cos();
        let orb_vx = cp.radius * omega * (-sin_a * ux + cos_a * vx2);
        let orb_vy = cp.radius * omega * (-sin_a * uy + cos_a * vy2);
        let orb_vz = cp.radius * omega * (-sin_a * uz + cos_a * vz2);

        // Advance by forward velocity + orbital velocity
        p.x += (p.vx + orb_vx) * dt;
        p.y += (p.vy + orb_vy) * dt;
        p.z += (p.vz + orb_vz) * dt;

        // Travel distance from forward component only (orbit is perpendicular)
        let dx = p.vx * dt; let dy = p.vy * dt; let dz = p.vz * dt;
        p.travel_dist += fast_sqrt(dx*dx + dy*dy + dz*dz);
    } else {
        // No circular params registered — degrade to straight (no crash)
        p.x += p.vx * dt; p.y += p.vy * dt; p.z += p.vz * dt;
        let dx = p.vx * dt; let dy = p.vy * dt; let dz = p.vz * dt;
        p.travel_dist += fast_sqrt(dx*dx + dy*dy + dz*dz);
    }
}

#[inline(always)]
fn tick_scale_3d(p: &mut NativeProjectile3D, dt: f32) {
    if p.scale_speed == 0.0 { return; }
    let diff = p.scale_target - p.scale_x;
    if diff.abs() > 0.001 {
        let delta     = diff * p.scale_speed * dt;
        p.scale_x += delta;
        p.scale_y += delta;
        p.scale_z += delta;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn mk_straight(x: f32, y: f32, vx: f32, vy: f32, ay: f32) -> NativeProjectile {
        NativeProjectile {
            x, y, vx, vy, ax: 0.0, ay,
            alive: 1, movement_type: MOVE_STRAIGHT, lifetime: 5.0,
            scale_x: 1.0, scale_y: 1.0,
            ..Default::default()
        }
    }

    fn mk_straight_3d(x: f32, y: f32, z: f32, vx: f32, vy: f32, vz: f32, ay: f32) -> NativeProjectile3D {
        NativeProjectile3D {
            x, y, z, vx, vy, vz, ax: 0.0, ay, az: 0.0,
            alive: 1, movement_type: MOVE_STRAIGHT, lifetime: 5.0,
            scale_x: 1.0, scale_y: 1.0, scale_z: 1.0,
            ..Default::default()
        }
    }

    /// CORRECTNESS CROSS-CHECK: an 8-projectile batch run through tick_all
    /// (which uses the AVX2 8-wide path when available — this test machine
    /// has AVX2) must produce results identical (within float tolerance) to
    /// running the exact same projectiles through tick_scalar_one directly,
    /// one at a time, bypassing all batching. This is the test that would
    /// catch a subtle bug in the hand-written AVX2 intrinsics port — the
    /// shape/behavior tests elsewhere check individual pieces, this one
    /// checks the whole 8-wide tier against ground truth.
    #[test]
    fn avx2_batch_matches_scalar_2d() {
        let seed: Vec<NativeProjectile> = vec![
            mk_straight(0.0,  0.0,  10.0,  0.0,  0.0),
            mk_straight(1.0,  2.0,   5.0,  5.0, -9.8),
            mk_straight(-3.0, 0.5,  -2.0,  8.0,  0.0),
            mk_straight(0.0, -1.0,   0.0, 12.0, -9.8),
            mk_straight(5.0,  5.0,  -7.0, -3.0,  0.0),
            mk_straight(2.2, -2.2,   3.3,  1.1, -9.8),
            mk_straight(-1.0, -1.0,  9.0,  0.5,  0.0),
            mk_straight(0.3,  0.7,  -4.4,  6.6, -9.8),
        ];

        let mut batched = seed.clone();
        let mut scalar   = seed.clone();

        for _ in 0..30 {
            tick_all(&mut batched, 1.0 / 60.0);
            let mut died = 0;
            for p in scalar.iter_mut() { tick_scalar_one(p, 1.0 / 60.0, &mut died); }
        }

        for i in 0..seed.len() {
            let b = &batched[i];
            let s = &scalar[i];
            assert!((b.x - s.x).abs() < 1e-3, "lane {i} x: batched={} scalar={}", b.x, s.x);
            assert!((b.y - s.y).abs() < 1e-3, "lane {i} y: batched={} scalar={}", b.y, s.y);
            assert!((b.vx - s.vx).abs() < 1e-3, "lane {i} vx: batched={} scalar={}", b.vx, s.vx);
            assert!((b.vy - s.vy).abs() < 1e-3, "lane {i} vy: batched={} scalar={}", b.vy, s.vy);
            assert!((b.travel_dist - s.travel_dist).abs() < 1e-2,
                "lane {i} travel_dist: batched={} scalar={}", b.travel_dist, s.travel_dist);
            assert!((b.angle_deg - s.angle_deg).abs() < 1e-1,
                "lane {i} angle_deg: batched={} scalar={}", b.angle_deg, s.angle_deg);
        }
    }

    #[test]
    fn avx2_batch_matches_scalar_3d() {
        let seed: Vec<NativeProjectile3D> = vec![
            mk_straight_3d(0.0, 0.0, 0.0,   10.0, 0.0, 2.0,  0.0),
            mk_straight_3d(1.0, 2.0, -1.0,   5.0, 5.0, 0.0, -9.8),
            mk_straight_3d(-3.0, 0.5, 3.0,  -2.0, 8.0, 1.0,  0.0),
            mk_straight_3d(0.0, -1.0, 0.5,   0.0, 12.0, -3.0, -9.8),
            mk_straight_3d(5.0, 5.0, -2.0,  -7.0, -3.0, 4.0,  0.0),
            mk_straight_3d(2.2, -2.2, 1.1,   3.3, 1.1, -2.2, -9.8),
            mk_straight_3d(-1.0, -1.0, 0.0,  9.0, 0.5, 0.0,   0.0),
            mk_straight_3d(0.3, 0.7, -0.4,  -4.4, 6.6, 1.3,  -9.8),
        ];

        let mut batched = seed.clone();
        let mut scalar   = seed.clone();

        for _ in 0..30 {
            tick_all_3d(&mut batched, 1.0 / 60.0);
            let mut died = 0;
            for p in scalar.iter_mut() { tick_scalar_one_3d(p, 1.0 / 60.0, &mut died); }
        }

        for i in 0..seed.len() {
            let b = &batched[i];
            let s = &scalar[i];
            assert!((b.x - s.x).abs() < 1e-3, "lane {i} x: batched={} scalar={}", b.x, s.x);
            assert!((b.y - s.y).abs() < 1e-3, "lane {i} y: batched={} scalar={}", b.y, s.y);
            assert!((b.z - s.z).abs() < 1e-3, "lane {i} z: batched={} scalar={}", b.z, s.z);
            assert!((b.travel_dist - s.travel_dist).abs() < 1e-2,
                "lane {i} travel_dist: batched={} scalar={}", b.travel_dist, s.travel_dist);
        }
    }

    /// Batches that DON'T divide evenly by 8 (or 4) must still tick every
    /// projectile exactly once — no dropped or double-ticked lanes at the
    /// tail where AVX2/SSE2/scalar boundaries meet.
    #[test]
    fn odd_sized_batch_ticks_every_projectile_exactly_once() {
        let mut projs: Vec<NativeProjectile> = (0..13)
            .map(|i| mk_straight(i as f32, 0.0, 1.0, 0.0, 0.0))
            .collect();
        tick_all(&mut projs, 1.0);
        for (i, p) in projs.iter().enumerate() {
            assert!((p.x - (i as f32 + 1.0)).abs() < 1e-4, "lane {i}: x={}", p.x);
        }
    }
}
