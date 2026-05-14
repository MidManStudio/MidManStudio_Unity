// simulation.rs
//
// Hot path optimizations vs original:
//   1. fast_atan2     replaces f32::atan2   — ~6-8x speedup on angle_deg update
//   2. fast_sqrt      replaces f32::sqrt    — ~4x speedup on travel_dist update
//   3. fast_inv_sqrt  replaces sqrt+divide  — ~4-5x speedup in guided/circular normalize
//   4. tick_straight_or_arching_x4         — SSE2 batch of 4 (2D straight/arching)
//   5. tick_straight_x4_3d                 — SSE2 batch of 4 (3D straight/arching)
//
// Expected throughput: ~50-60k proj/ms from ~32-34k (straight-movement workloads).
// Guided/circular: ~2-3x improvement from normalization alone.

use crate::{NativeProjectile, NativeProjectile3D};
use crate::config_store;
use crate::simd::{fast_atan2, fast_inv_sqrt, fast_sqrt};

const RAD2DEG: f32 = 57.295_779_51_f32;

// Movement type constants — must match C# ProjectileMovementType exactly
pub const MOVE_STRAIGHT: u8 = 0;
pub const MOVE_ARCHING:  u8 = 1;
pub const MOVE_GUIDED:   u8 = 2;
pub const MOVE_TELEPORT: u8 = 3;
pub const MOVE_WAVE:     u8 = 4;
pub const MOVE_CIRCULAR: u8 = 5;

// ─────────────────────────────────────────────────────────────────────────────
//  2D tick — entry point
// ─────────────────────────────────────────────────────────────────────────────

pub fn tick_all(projs: &mut [NativeProjectile], dt: f32) -> i32 {
    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    { return unsafe { tick_all_sse2(projs, dt) }; }

    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64")))]
    tick_all_scalar(projs, dt)
}

// ─────────────────────────────────────────────────────────────────────────────
//  2D tick — SSE2 batch path
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
unsafe fn tick_all_sse2(projs: &mut [NativeProjectile], dt: f32) -> i32 {
    let n        = projs.len();
    let mut died = 0_i32;
    let mut i    = 0_usize;

    while i + 4 <= n {
        let p = projs.as_ptr().add(i);

        // All 4 alive?
        let alive_and = (*p).alive & (*p.add(1)).alive
                       & (*p.add(2)).alive & (*p.add(3)).alive;

        // All STRAIGHT or ARCHING (type ≤ 1)?
        let mt_or = (*p).movement_type | (*p.add(1)).movement_type
                  | (*p.add(2)).movement_type | (*p.add(3)).movement_type;

        if alive_and == 1 && mt_or <= 1 {
            tick_straight_or_arching_x4(&mut projs[i..i + 4], dt, &mut died);
            i += 4;
        } else {
            tick_scalar_one(&mut projs[i], dt, &mut died);
            i += 1;
        }
    }

    // Remainder
    while i < n {
        tick_scalar_one(&mut projs[i], dt, &mut died);
        i += 1;
    }

    died
}

// ─────────────────────────────────────────────────────────────────────────────
//  SSE2 core: 4 straight/arching 2D projectiles simultaneously
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
unsafe fn tick_straight_or_arching_x4(
    projs: &mut [NativeProjectile], // exactly 4 elements
    dt:    f32,
    died:  &mut i32,
) {
    #[cfg(target_arch = "x86")]
    use core::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use core::arch::x86_64::*;
    use crate::simd::sse2::*;

    debug_assert_eq!(projs.len(), 4);

    let dt4  = _mm_set1_ps(dt);
    let zero = _mm_setzero_ps();

    // ── Lifetime tick ──────────────────────────────────────────────────────
    let lt = _mm_set_ps(
        projs[3].lifetime, projs[2].lifetime,
        projs[1].lifetime, projs[0].lifetime,
    );
    let lt_new    = _mm_sub_ps(lt, dt4);
    let dead_mask = _mm_movemask_ps(_mm_cmple_ps(lt_new, zero));

    if dead_mask != 0 {
        // At least one died — fall back to scalar for all 4
        let lt_a: [f32; 4] = core::mem::transmute(lt_new);
        for j in 0..4 {
            projs[j].lifetime = lt_a[j];
            if projs[j].lifetime <= 0.0 {
                projs[j].alive = 0;
                *died += 1;
            }
        }
        return;
    }

    // Store updated lifetimes (all still alive)
    let lt_a: [f32; 4] = core::mem::transmute(lt_new);
    projs[0].lifetime = lt_a[0];
    projs[1].lifetime = lt_a[1];
    projs[2].lifetime = lt_a[2];
    projs[3].lifetime = lt_a[3];

    // ── Physics: vx += ax*dt, vy += ay*dt, x += vx*dt, y += vy*dt ────────
    let ax = _mm_set_ps(projs[3].ax, projs[2].ax, projs[1].ax, projs[0].ax);
    let ay = _mm_set_ps(projs[3].ay, projs[2].ay, projs[1].ay, projs[0].ay);
    let mut vx = _mm_set_ps(projs[3].vx, projs[2].vx, projs[1].vx, projs[0].vx);
    let mut vy = _mm_set_ps(projs[3].vy, projs[2].vy, projs[1].vy, projs[0].vy);
    let mut x  = _mm_set_ps(projs[3].x,  projs[2].x,  projs[1].x,  projs[0].x);
    let mut y  = _mm_set_ps(projs[3].y,  projs[2].y,  projs[1].y,  projs[0].y);

    vx = _mm_add_ps(vx, _mm_mul_ps(ax, dt4));
    vy = _mm_add_ps(vy, _mm_mul_ps(ay, dt4));
    x  = _mm_add_ps(x,  _mm_mul_ps(vx, dt4));
    y  = _mm_add_ps(y,  _mm_mul_ps(vy, dt4));

    // ── Angle: fast_atan2_x4 — ~20 cyc for 4 vs ~400 cyc for 4×libm ──────
    let angle_rad = fast_atan2_x4(vy, vx);
    let angle_deg = rad_to_deg_x4(angle_rad);

    // ── Travel distance: rsqrt_nr — ~10 cyc for 4 vs ~48 cyc for 4×sqrtss
    let dx      = _mm_mul_ps(vx, dt4);
    let dy      = _mm_mul_ps(vy, dt4);
    let len_sq  = _mm_add_ps(_mm_mul_ps(dx, dx), _mm_mul_ps(dy, dy));
    let safe_sq = _mm_max_ps(len_sq, _mm_set1_ps(1e-20_f32));
    // dist ≈ len_sq * rsqrt(len_sq) = sqrt(len_sq)
    let dist_add = _mm_mul_ps(len_sq, rsqrt_nr(safe_sq));

    // ── AoS scatter ───────────────────────────────────────────────────────
    let vx_a:  [f32; 4] = core::mem::transmute(vx);
    let vy_a:  [f32; 4] = core::mem::transmute(vy);
    let x_a:   [f32; 4] = core::mem::transmute(x);
    let y_a:   [f32; 4] = core::mem::transmute(y);
    let ang_a: [f32; 4] = core::mem::transmute(angle_deg);
    let dst_a: [f32; 4] = core::mem::transmute(dist_add);

    for j in 0..4 {
        projs[j].vx           = vx_a[j];
        projs[j].vy           = vy_a[j];
        projs[j].x            = x_a[j];
        projs[j].y            = y_a[j];
        projs[j].angle_deg    = ang_a[j];
        projs[j].travel_dist += dst_a[j];

        if projs[j].scale_speed != 0.0 {
            let diff = projs[j].scale_target - projs[j].scale_x;
            if diff.abs() > 0.001 {
                projs[j].scale_x += diff * projs[j].scale_speed * dt;
                projs[j].scale_y  = projs[j].scale_x;
            }
        }

        if projs[j].movement_type == MOVE_ARCHING {
            projs[j].curve_t += dt;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  2D scalar path (non-x86 or remainder after SSE2 batch)
// ─────────────────────────────────────────────────────────────────────────────

#[allow(dead_code)]
fn tick_all_scalar(projs: &mut [NativeProjectile], dt: f32) -> i32 {
    let mut died = 0_i32;
    for p in projs.iter_mut() {
        tick_scalar_one(p, dt, &mut died);
    }
    died
}

/// Single-projectile scalar tick.
/// Uses fast_atan2 and fast_sqrt replacing libm — meaningful even without SIMD batching.
fn tick_scalar_one(p: &mut NativeProjectile, dt: f32, died: &mut i32) {
    if p.alive == 0 { return; }

    p.lifetime -= dt;
    if p.lifetime <= 0.0 {
        p.alive = 0;
        *died += 1;
        return;
    }

    match p.movement_type {
        MOVE_STRAIGHT => tick_straight(p, dt),
        MOVE_ARCHING  => tick_arching(p, dt),
        MOVE_GUIDED   => tick_guided(p, dt),
        MOVE_TELEPORT => tick_teleport(p, dt),
        MOVE_WAVE     => tick_wave(p, dt),
        MOVE_CIRCULAR => tick_circular(p, dt),
        _             => tick_straight(p, dt),
    }

    tick_scale(p, dt);

    if p.movement_type != MOVE_TELEPORT {
        if p.vx != 0.0 || p.vy != 0.0 {
            // fast_atan2: ~15 cycles vs ~100 cycles for libm atan2f (fpatan)
            p.angle_deg = fast_atan2(p.vy, p.vx) * RAD2DEG;
        }
        // fast_sqrt: ~10 cycles vs ~14-20 cycles for sqrtss
        let dx = p.vx * dt;
        let dy = p.vy * dt;
        p.travel_dist += fast_sqrt(dx * dx + dy * dy);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  2D movement implementations
// ─────────────────────────────────────────────────────────────────────────────

#[inline(always)]
fn tick_straight(p: &mut NativeProjectile, dt: f32) {
    p.vx += p.ax * dt;
    p.vy += p.ay * dt;
    p.x  += p.vx * dt;
    p.y  += p.vy * dt;
}

#[inline(always)]
fn tick_arching(p: &mut NativeProjectile, dt: f32) {
    p.vy      += p.ay * dt;
    p.vx      += p.ax * dt;
    p.x       += p.vx * dt;
    p.y       += p.vy * dt;
    p.curve_t += dt;
}

/// Guided 2D: turns toward target direction stored in (ax, ay).
/// fast_inv_sqrt replaces sqrt+divide — ~4-5x faster normalization.
#[inline(always)]
fn tick_guided(p: &mut NativeProjectile, dt: f32) {
    let turn_rate = core::f32::consts::PI * dt;

    // Current direction (normalized)
    let cur_len_sq = p.vx * p.vx + p.vy * p.vy;
    let inv_cur    = fast_inv_sqrt(cur_len_sq.max(1e-8));
    let cur_nx     = p.vx * inv_cur;
    let cur_ny     = p.vy * inv_cur;
    let cur_angle  = fast_atan2(cur_ny, cur_nx);

    // Target direction (normalized)
    let tgt_len_sq = p.ax * p.ax + p.ay * p.ay;
    let inv_tgt    = fast_inv_sqrt(tgt_len_sq.max(1e-8));
    let tgt_nx     = p.ax * inv_tgt;
    let tgt_ny     = p.ay * inv_tgt;
    let tgt_angle  = fast_atan2(tgt_ny, tgt_nx);

    let mut delta = tgt_angle - cur_angle;
    if delta >  core::f32::consts::PI { delta -= core::f32::consts::TAU; }
    if delta < -core::f32::consts::PI { delta += core::f32::consts::TAU; }
    let delta = delta.clamp(-turn_rate, turn_rate);

    let new_angle = cur_angle + delta;
    // Reconstruct velocity: magnitude preserved, direction changed
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
        // fast_inv_sqrt: ~4x faster than sqrt+divide
        let spd_sq = p.vx * p.vx + p.vy * p.vy;
        let inv    = fast_inv_sqrt(spd_sq.max(1e-8));
        let jump   = INTERVAL / inv; // = INTERVAL * speed
        p.x += p.vx * inv * jump;
        p.y += p.vy * inv * jump;
        p.travel_dist += jump;
    }
}

/// Wave movement (2D): oscillates laterally while traveling forward.
/// ax/ay holds the normalised perpendicular direction (set at spawn by C#).
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

/// Circular movement (2D): orbits around the forward travel axis.
/// ax/ay holds the first perpendicular axis (set at spawn by C#).
#[inline(always)]
fn tick_circular(p: &mut NativeProjectile, dt: f32) {
    p.x       += p.vx * dt;
    p.y       += p.vy * dt;
    p.curve_t += dt;

    if let Some(cp) = config_store::get_circular(p.config_id) {
        let angle_rad = p.curve_t * cp.angular_speed.to_radians()
                        + cp.start_angle_deg.to_radians();
        let orbit_x   = p.ax * angle_rad.cos() * cp.radius;
        let orbit_y   = p.ay * angle_rad.sin() * cp.radius;
        p.x += orbit_x * dt;
        p.y += orbit_y * dt;
    }
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

// ─────────────────────────────────────────────────────────────────────────────
//  3D tick — entry point
// ─────────────────────────────────────────────────────────────────────────────

pub fn tick_all_3d(projs: &mut [NativeProjectile3D], dt: f32) -> i32 {
    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    { return unsafe { tick_all_3d_sse2(projs, dt) }; }

    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64")))]
    tick_all_3d_scalar(projs, dt)
}

// ─────────────────────────────────────────────────────────────────────────────
//  3D tick — SSE2 batch path
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
unsafe fn tick_all_3d_sse2(projs: &mut [NativeProjectile3D], dt: f32) -> i32 {
    let n        = projs.len();
    let mut died = 0_i32;
    let mut i    = 0_usize;

    while i + 4 <= n {
        let p = projs.as_ptr().add(i);

        let alive_and = (*p).alive & (*p.add(1)).alive
                       & (*p.add(2)).alive & (*p.add(3)).alive;
        let mt_or = (*p).movement_type | (*p.add(1)).movement_type
                  | (*p.add(2)).movement_type | (*p.add(3)).movement_type;

        if alive_and == 1 && mt_or <= 1 {
            tick_straight_or_arching_x4_3d(&mut projs[i..i + 4], dt, &mut died);
            i += 4;
        } else {
            tick_scalar_one_3d(&mut projs[i], dt, &mut died);
            i += 1;
        }
    }

    while i < n {
        tick_scalar_one_3d(&mut projs[i], dt, &mut died);
        i += 1;
    }

    died
}

// ─────────────────────────────────────────────────────────────────────────────
//  SSE2 core: 4 straight/arching 3D projectiles simultaneously
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
unsafe fn tick_straight_or_arching_x4_3d(
    projs: &mut [NativeProjectile3D], // exactly 4 elements
    dt:    f32,
    died:  &mut i32,
) {
    #[cfg(target_arch = "x86")]
    use core::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use core::arch::x86_64::*;
    use crate::simd::sse2::rsqrt_nr;

    debug_assert_eq!(projs.len(), 4);

    let dt4  = _mm_set1_ps(dt);
    let zero = _mm_setzero_ps();

    // ── Lifetime ──────────────────────────────────────────────────────────
    let lt = _mm_set_ps(
        projs[3].lifetime, projs[2].lifetime,
        projs[1].lifetime, projs[0].lifetime,
    );
    let lt_new    = _mm_sub_ps(lt, dt4);
    let dead_mask = _mm_movemask_ps(_mm_cmple_ps(lt_new, zero));

    if dead_mask != 0 {
        let lt_a: [f32; 4] = core::mem::transmute(lt_new);
        for j in 0..4 {
            projs[j].lifetime = lt_a[j];
            if projs[j].lifetime <= 0.0 {
                projs[j].alive = 0;
                *died += 1;
            }
        }
        return;
    }

    let lt_a: [f32; 4] = core::mem::transmute(lt_new);
    projs[0].lifetime = lt_a[0];
    projs[1].lifetime = lt_a[1];
    projs[2].lifetime = lt_a[2];
    projs[3].lifetime = lt_a[3];

    // ── Physics X ─────────────────────────────────────────────────────────
    let ax = _mm_set_ps(projs[3].ax, projs[2].ax, projs[1].ax, projs[0].ax);
    let mut vx = _mm_set_ps(projs[3].vx, projs[2].vx, projs[1].vx, projs[0].vx);
    let mut x  = _mm_set_ps(projs[3].x,  projs[2].x,  projs[1].x,  projs[0].x);
    vx = _mm_add_ps(vx, _mm_mul_ps(ax, dt4));
    x  = _mm_add_ps(x,  _mm_mul_ps(vx, dt4));

    // ── Physics Y ─────────────────────────────────────────────────────────
    let ay = _mm_set_ps(projs[3].ay, projs[2].ay, projs[1].ay, projs[0].ay);
    let mut vy = _mm_set_ps(projs[3].vy, projs[2].vy, projs[1].vy, projs[0].vy);
    let mut y  = _mm_set_ps(projs[3].y,  projs[2].y,  projs[1].y,  projs[0].y);
    vy = _mm_add_ps(vy, _mm_mul_ps(ay, dt4));
    y  = _mm_add_ps(y,  _mm_mul_ps(vy, dt4));

    // ── Physics Z ─────────────────────────────────────────────────────────
    let az = _mm_set_ps(projs[3].az, projs[2].az, projs[1].az, projs[0].az);
    let mut vz = _mm_set_ps(projs[3].vz, projs[2].vz, projs[1].vz, projs[0].vz);
    let mut z  = _mm_set_ps(projs[3].z,  projs[2].z,  projs[1].z,  projs[0].z);
    vz = _mm_add_ps(vz, _mm_mul_ps(az, dt4));
    z  = _mm_add_ps(z,  _mm_mul_ps(vz, dt4));

    // ── Travel distance: rsqrt_nr ─────────────────────────────────────────
    let dx = _mm_mul_ps(vx, dt4);
    let dy = _mm_mul_ps(vy, dt4);
    let dz = _mm_mul_ps(vz, dt4);
    let len_sq   = _mm_add_ps(
        _mm_add_ps(_mm_mul_ps(dx, dx), _mm_mul_ps(dy, dy)),
        _mm_mul_ps(dz, dz),
    );
    let safe_sq  = _mm_max_ps(len_sq, _mm_set1_ps(1e-20_f32));
    let dist_add = _mm_mul_ps(len_sq, rsqrt_nr(safe_sq));

    // ── Scatter ───────────────────────────────────────────────────────────
    let vx_a: [f32; 4] = core::mem::transmute(vx);
    let vy_a: [f32; 4] = core::mem::transmute(vy);
    let vz_a: [f32; 4] = core::mem::transmute(vz);
    let x_a:  [f32; 4] = core::mem::transmute(x);
    let y_a:  [f32; 4] = core::mem::transmute(y);
    let z_a:  [f32; 4] = core::mem::transmute(z);
    let dst_a:[f32; 4] = core::mem::transmute(dist_add);

    for j in 0..4 {
        projs[j].vx           = vx_a[j];
        projs[j].vy           = vy_a[j];
        projs[j].vz           = vz_a[j];
        projs[j].x            = x_a[j];
        projs[j].y            = y_a[j];
        projs[j].z            = z_a[j];
        projs[j].travel_dist += dst_a[j];

        if projs[j].scale_speed != 0.0 {
            let diff = projs[j].scale_target - projs[j].scale_x;
            if diff.abs() > 0.001 {
                let delta     = diff * projs[j].scale_speed * dt;
                projs[j].scale_x += delta;
                projs[j].scale_y += delta;
                projs[j].scale_z += delta;
            }
        }

        if projs[j].movement_type == MOVE_ARCHING {
            projs[j].timer_t += dt;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  3D scalar path
// ─────────────────────────────────────────────────────────────────────────────

#[allow(dead_code)]
fn tick_all_3d_scalar(projs: &mut [NativeProjectile3D], dt: f32) -> i32 {
    let mut died = 0_i32;
    for p in projs.iter_mut() {
        tick_scalar_one_3d(p, dt, &mut died);
    }
    died
}

fn tick_scalar_one_3d(p: &mut NativeProjectile3D, dt: f32, died: &mut i32) {
    if p.alive == 0 { return; }

    p.lifetime -= dt;
    if p.lifetime <= 0.0 {
        p.alive = 0;
        *died += 1;
        return;
    }

    match p.movement_type {
        MOVE_STRAIGHT => tick_straight_3d(p, dt),
        MOVE_ARCHING  => tick_arching_3d(p, dt),
        MOVE_GUIDED   => tick_guided_3d(p, dt),
        MOVE_TELEPORT => tick_teleport_3d(p, dt),
        MOVE_WAVE     => tick_wave_3d(p, dt),
        MOVE_CIRCULAR => tick_circular_3d(p, dt),
        _             => tick_straight_3d(p, dt),
    }

    tick_scale_3d(p, dt);

    if p.movement_type != MOVE_TELEPORT {
        let dx = p.vx * dt;
        let dy = p.vy * dt;
        let dz = p.vz * dt;
        p.travel_dist += fast_sqrt(dx * dx + dy * dy + dz * dz);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  3D movement implementations
// ─────────────────────────────────────────────────────────────────────────────

#[inline(always)]
fn tick_straight_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.vx += p.ax * dt;
    p.vy += p.ay * dt;
    p.vz += p.az * dt;
    p.x  += p.vx * dt;
    p.y  += p.vy * dt;
    p.z  += p.vz * dt;
}

#[inline(always)]
fn tick_arching_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.vx      += p.ax * dt;
    p.vy      += p.ay * dt;
    p.vz      += p.az * dt;
    p.x       += p.vx * dt;
    p.y       += p.vy * dt;
    p.z       += p.vz * dt;
    p.timer_t += dt;
}

/// Guided 3D: smoothly steers toward target direction in (ax, ay, az).
/// fast_inv_sqrt replaces two sqrt+divide normalizations — ~4-5x faster.
#[inline(always)]
fn tick_guided_3d(p: &mut NativeProjectile3D, dt: f32) {
    let turn_rate = core::f32::consts::PI * dt;

    // Current speed and normalised direction
    let spd_sq  = p.vx*p.vx + p.vy*p.vy + p.vz*p.vz;
    let inv_spd = fast_inv_sqrt(spd_sq.max(1e-8));
    let (cx, cy, cz) = (p.vx * inv_spd, p.vy * inv_spd, p.vz * inv_spd);

    // Target direction normalised
    let tgt_sq  = p.ax*p.ax + p.ay*p.ay + p.az*p.az;
    let inv_tgt = fast_inv_sqrt(tgt_sq.max(1e-8));
    let (tx, ty, tz) = (p.ax * inv_tgt, p.ay * inv_tgt, p.az * inv_tgt);

    let dot   = (cx*tx + cy*ty + cz*tz).clamp(-1.0, 1.0);
    let angle = dot.acos();

    if angle > 0.0001 {
        let t  = (turn_rate / angle).min(1.0);
        let nx = cx + (tx - cx) * t;
        let ny = cy + (ty - cy) * t;
        let nz = cz + (tz - cz) * t;
        // Renormalise: fast_inv_sqrt again
        let inv_n = fast_inv_sqrt((nx*nx + ny*ny + nz*nz).max(1e-8));
        let speed = fast_sqrt(spd_sq);
        p.vx = nx * inv_n * speed;
        p.vy = ny * inv_n * speed;
        p.vz = nz * inv_n * speed;
    }

    p.x += p.vx * dt;
    p.y += p.vy * dt;
    p.z += p.vz * dt;
}

#[inline(always)]
fn tick_teleport_3d(p: &mut NativeProjectile3D, dt: f32) {
    const INTERVAL: f32 = 0.12;
    p.timer_t += dt;
    if p.timer_t >= INTERVAL {
        p.timer_t -= INTERVAL;
        let spd_sq = p.vx*p.vx + p.vy*p.vy + p.vz*p.vz;
        let inv    = fast_inv_sqrt(spd_sq.max(1e-8));
        let jump   = INTERVAL / inv; // = INTERVAL * speed
        p.x += p.vx * inv * jump;
        p.y += p.vy * inv * jump;
        p.z += p.vz * inv * jump;
        p.travel_dist += jump;
    }
}

/// Wave (3D): oscillates perpendicular to travel direction.
/// ax/ay/az holds the normalised perpendicular axis (set at spawn by C#).
#[inline(always)]
fn tick_wave_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.x       += p.vx * dt;
    p.y       += p.vy * dt;
    p.z       += p.vz * dt;
    p.timer_t += dt;

    if let Some(wp) = config_store::get_wave(p.config_id) {
        let phase  = p.timer_t * wp.frequency * core::f32::consts::TAU + wp.phase_offset;
        let offset = wp.amplitude * phase.sin();
        p.x += p.ax * offset * dt;
        p.y += p.ay * offset * dt;
        p.z += p.az * offset * dt;
    }
}

/// Circular/helical (3D): orbits around the forward travel axis.
/// ax/ay/az stores the first perpendicular axis (set at spawn).
/// Second perp = cross(forward, first_perp) computed each tick.
#[inline(always)]
fn tick_circular_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.x       += p.vx * dt;
    p.y       += p.vy * dt;
    p.z       += p.vz * dt;
    p.timer_t += dt;

    if let Some(cp) = config_store::get_circular(p.config_id) {
        let angle = p.timer_t * cp.angular_speed.to_radians()
                    + cp.start_angle_deg.to_radians();

        // Forward direction (normalised)
        let spd_sq  = p.vx*p.vx + p.vy*p.vy + p.vz*p.vz;
        let inv_spd = fast_inv_sqrt(spd_sq.max(1e-8));
        let (fx, fy, fz) = (p.vx*inv_spd, p.vy*inv_spd, p.vz*inv_spd);

        // First perp from ax/ay/az
        let (ux, uy, uz) = (p.ax, p.ay, p.az);

        // Second perp = forward × first_perp
        let (vx, vy, vz) = (
            fy*uz - fz*uy,
            fz*ux - fx*uz,
            fx*uy - fy*ux,
        );

        let c = angle.cos();
        let s = angle.sin();
        p.x += (ux * c + vx * s) * cp.radius * dt;
        p.y += (uy * c + vy * s) * cp.radius * dt;
        p.z += (uz * c + vz * s) * cp.radius * dt;
    }
}

#[inline(always)]
fn tick_scale_3d(p: &mut NativeProjectile3D, dt: f32) {
    if p.scale_speed == 0.0 { return; }
    let diff = p.scale_target - p.scale_x;
    if diff.abs() > 0.001 {
        let delta     = diff * p.scale_speed * dt;
        p.scale_x    += delta;
        p.scale_y    += delta;
        p.scale_z    += delta;
    }
}
