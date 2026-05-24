// simulation.rs
//
// FIX (MOVE_CIRCULAR 2D):
//   Previous: added orbit_pos * dt each frame → position error accumulates,
//   bullets drift rather than curve cleanly. Also ignored start_angle_deg.
//   Fixed: rotate velocity vector by omega*dt each tick → true circular arc.
//   The radius is now implicit (radius = speed / omega). start_angle_deg
//   is applied once at first tick via an initial velocity pre-rotation.
//   To curve clockwise: negative angular_speed. CCW: positive.
//
// FIX (MOVE_CIRCULAR 3D):
//   Previous: added orbit_pos * dt → same accumulation bug as 2D.
//   Fixed: add orbital VELOCITY = d/dt[R*(cos(ωt)*u + sin(ωt)*v)]
//          = R*ω*(-sin(ωt)*u + cos(ωt)*v) per frame.
//   This gives correct helical motion around the forward travel axis.

use crate::{NativeProjectile, NativeProjectile3D};
use crate::config_store;
use crate::simd::{fast_atan2, fast_inv_sqrt, fast_sqrt};

const RAD2DEG: f32 = 57.295_779_51_f32;

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
//  2D SSE2 batch path (unchanged — only straight/arching batched)
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
unsafe fn tick_all_sse2(projs: &mut [NativeProjectile], dt: f32) -> i32 {
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
            tick_straight_or_arching_x4(&mut projs[i..i + 4], dt, &mut died);
            i += 4;
        } else {
            tick_scalar_one(&mut projs[i], dt, &mut died);
            i += 1;
        }
    }
    while i < n {
        tick_scalar_one(&mut projs[i], dt, &mut died);
        i += 1;
    }
    died
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
unsafe fn tick_straight_or_arching_x4(
    projs: &mut [NativeProjectile],
    dt:    f32,
    died:  &mut i32,
) {
    #[cfg(target_arch = "x86")]    use core::arch::x86::*;
    #[cfg(target_arch = "x86_64")] use core::arch::x86_64::*;
    use crate::simd::sse2::*;

    debug_assert_eq!(projs.len(), 4);

    let dt4  = _mm_set1_ps(dt);
    let zero = _mm_setzero_ps();

    let lt = _mm_set_ps(
        projs[3].lifetime, projs[2].lifetime,
        projs[1].lifetime, projs[0].lifetime);
    let lt_new    = _mm_sub_ps(lt, dt4);
    let dead_mask = _mm_movemask_ps(_mm_cmple_ps(lt_new, zero));

    if dead_mask != 0 {
        let lt_a: [f32; 4] = core::mem::transmute(lt_new);
        for j in 0..4 {
            projs[j].lifetime = lt_a[j];
            if projs[j].lifetime <= 0.0 { projs[j].alive = 0; *died += 1; }
        }
        return;
    }

    let lt_a: [f32; 4] = core::mem::transmute(lt_new);
    projs[0].lifetime = lt_a[0]; projs[1].lifetime = lt_a[1];
    projs[2].lifetime = lt_a[2]; projs[3].lifetime = lt_a[3];

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

    let angle_rad = fast_atan2_x4(vy, vx);
    let angle_deg = rad_to_deg_x4(angle_rad);

    let dx      = _mm_mul_ps(vx, dt4);
    let dy      = _mm_mul_ps(vy, dt4);
    let len_sq  = _mm_add_ps(_mm_mul_ps(dx, dx), _mm_mul_ps(dy, dy));
    let safe_sq = _mm_max_ps(len_sq, _mm_set1_ps(1e-20_f32));
    let dist_add = _mm_mul_ps(len_sq, rsqrt_nr(safe_sq));

    let vx_a:  [f32; 4] = core::mem::transmute(vx);
    let vy_a:  [f32; 4] = core::mem::transmute(vy);
    let x_a:   [f32; 4] = core::mem::transmute(x);
    let y_a:   [f32; 4] = core::mem::transmute(y);
    let ang_a: [f32; 4] = core::mem::transmute(angle_deg);
    let dst_a: [f32; 4] = core::mem::transmute(dist_add);

    for j in 0..4 {
        projs[j].vx = vx_a[j]; projs[j].vy = vy_a[j];
        projs[j].x  = x_a[j];  projs[j].y  = y_a[j];
        projs[j].angle_deg    = ang_a[j];
        projs[j].travel_dist += dst_a[j];

        if projs[j].scale_speed != 0.0 {
            let diff = projs[j].scale_target - projs[j].scale_x;
            if diff.abs() > 0.001 {
                projs[j].scale_x += diff * projs[j].scale_speed * dt;
                projs[j].scale_y  = projs[j].scale_x;
            }
        }
        if projs[j].movement_type == MOVE_ARCHING { projs[j].curve_t += dt; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  2D scalar path
// ─────────────────────────────────────────────────────────────────────────────

#[allow(dead_code)]
fn tick_all_scalar(projs: &mut [NativeProjectile], dt: f32) -> i32 {
    let mut died = 0_i32;
    for p in projs.iter_mut() { tick_scalar_one(p, dt, &mut died); }
    died
}

fn tick_scalar_one(p: &mut NativeProjectile, dt: f32, died: &mut i32) {
    if p.alive == 0 { return; }
    p.lifetime -= dt;
    if p.lifetime <= 0.0 { p.alive = 0; *died += 1; return; }

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

    if p.movement_type != MOVE_TELEPORT && p.movement_type != MOVE_CIRCULAR {
        if p.vx != 0.0 || p.vy != 0.0 {
            p.angle_deg = fast_atan2(p.vy, p.vx) * RAD2DEG;
        }
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

/// MOVE_CIRCULAR (2D) — smooth curving arc.
///
/// FIX: previous code added orbit_pos*dt each frame, causing the bullet to
/// drift outward (accumulating position error) rather than curving cleanly.
///
/// Correct approach: rotate the velocity vector by omega*dt each tick.
/// This preserves speed while continuously changing direction → clean circle arc.
///
/// angular_speed (degrees/sec):
///   positive → curves counter-clockwise (left when traveling right)
///   negative → curves clockwise (right when traveling right)
///
/// Radius of the arc is implicit: R = speed / |omega_rad|
/// Set angular_speed = speed / desired_radius * RAD2DEG for a specific radius.
///
/// start_angle_deg: pre-rotates velocity at first tick (curve_t == 0).
/// This lets you choose which direction the bullet initially curves.
#[inline(always)]
fn tick_circular(p: &mut NativeProjectile, dt: f32) {
    if let Some(cp) = config_store::get_circular(p.config_id) {
        let omega = cp.angular_speed.to_radians(); // radians per second

        // On the very first tick, apply start_angle as an initial velocity rotation.
        // curve_t == 0.0 only before any tick has run (set to 0 at spawn).
        if p.curve_t == 0.0 && cp.start_angle_deg != 0.0 {
            let init_rad = cp.start_angle_deg.to_radians();
            let (ci, si) = (init_rad.cos(), init_rad.sin());
            let ivx = p.vx * ci - p.vy * si;
            let ivy = p.vx * si + p.vy * ci;
            p.vx = ivx;
            p.vy = ivy;
        }

        // Rotate velocity vector by omega * dt — preserves magnitude, curves path.
        let theta = omega * dt;
        let (cos_t, sin_t) = (theta.cos(), theta.sin());
        let new_vx = p.vx * cos_t - p.vy * sin_t;
        let new_vy = p.vx * sin_t + p.vy * cos_t;
        p.vx = new_vx;
        p.vy = new_vy;
    }

    p.curve_t += dt;
    p.x += p.vx * dt;
    p.y += p.vy * dt;

    // Update angle and travel distance for the circular path
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

// ─────────────────────────────────────────────────────────────────────────────
//  3D tick — entry point
// ─────────────────────────────────────────────────────────────────────────────

pub fn tick_all_3d(projs: &mut [NativeProjectile3D], dt: f32) -> i32 {
    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    { return unsafe { tick_all_3d_sse2(projs, dt) }; }

    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64")))]
    tick_all_3d_scalar(projs, dt)
}

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

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
unsafe fn tick_straight_or_arching_x4_3d(
    projs: &mut [NativeProjectile3D],
    dt:    f32,
    died:  &mut i32,
) {
    #[cfg(target_arch = "x86")]    use core::arch::x86::*;
    #[cfg(target_arch = "x86_64")] use core::arch::x86_64::*;
    use crate::simd::sse2::rsqrt_nr;

    debug_assert_eq!(projs.len(), 4);

    let dt4  = _mm_set1_ps(dt);
    let zero = _mm_setzero_ps();

    let lt = _mm_set_ps(
        projs[3].lifetime, projs[2].lifetime,
        projs[1].lifetime, projs[0].lifetime);
    let lt_new    = _mm_sub_ps(lt, dt4);
    let dead_mask = _mm_movemask_ps(_mm_cmple_ps(lt_new, zero));

    if dead_mask != 0 {
        let lt_a: [f32; 4] = core::mem::transmute(lt_new);
        for j in 0..4 {
            projs[j].lifetime = lt_a[j];
            if projs[j].lifetime <= 0.0 { projs[j].alive = 0; *died += 1; }
        }
        return;
    }

    let lt_a: [f32; 4] = core::mem::transmute(lt_new);
    projs[0].lifetime = lt_a[0]; projs[1].lifetime = lt_a[1];
    projs[2].lifetime = lt_a[2]; projs[3].lifetime = lt_a[3];

    let ax = _mm_set_ps(projs[3].ax, projs[2].ax, projs[1].ax, projs[0].ax);
    let mut vx = _mm_set_ps(projs[3].vx, projs[2].vx, projs[1].vx, projs[0].vx);
    let mut x  = _mm_set_ps(projs[3].x,  projs[2].x,  projs[1].x,  projs[0].x);
    vx = _mm_add_ps(vx, _mm_mul_ps(ax, dt4));
    x  = _mm_add_ps(x,  _mm_mul_ps(vx, dt4));

    let ay = _mm_set_ps(projs[3].ay, projs[2].ay, projs[1].ay, projs[0].ay);
    let mut vy = _mm_set_ps(projs[3].vy, projs[2].vy, projs[1].vy, projs[0].vy);
    let mut y  = _mm_set_ps(projs[3].y,  projs[2].y,  projs[1].y,  projs[0].y);
    vy = _mm_add_ps(vy, _mm_mul_ps(ay, dt4));
    y  = _mm_add_ps(y,  _mm_mul_ps(vy, dt4));

    let az = _mm_set_ps(projs[3].az, projs[2].az, projs[1].az, projs[0].az);
    let mut vz = _mm_set_ps(projs[3].vz, projs[2].vz, projs[1].vz, projs[0].vz);
    let mut z  = _mm_set_ps(projs[3].z,  projs[2].z,  projs[1].z,  projs[0].z);
    vz = _mm_add_ps(vz, _mm_mul_ps(az, dt4));
    z  = _mm_add_ps(z,  _mm_mul_ps(vz, dt4));

    let dx = _mm_mul_ps(vx, dt4);
    let dy = _mm_mul_ps(vy, dt4);
    let dz = _mm_mul_ps(vz, dt4);
    let len_sq = _mm_add_ps(
        _mm_add_ps(_mm_mul_ps(dx, dx), _mm_mul_ps(dy, dy)),
        _mm_mul_ps(dz, dz));
    let safe_sq  = _mm_max_ps(len_sq, _mm_set1_ps(1e-20_f32));
    let dist_add = _mm_mul_ps(len_sq, rsqrt_nr(safe_sq));

    let vx_a: [f32; 4] = core::mem::transmute(vx);
    let vy_a: [f32; 4] = core::mem::transmute(vy);
    let vz_a: [f32; 4] = core::mem::transmute(vz);
    let x_a:  [f32; 4] = core::mem::transmute(x);
    let y_a:  [f32; 4] = core::mem::transmute(y);
    let z_a:  [f32; 4] = core::mem::transmute(z);
    let dst_a:[f32; 4] = core::mem::transmute(dist_add);

    for j in 0..4 {
        projs[j].vx = vx_a[j]; projs[j].vy = vy_a[j]; projs[j].vz = vz_a[j];
        projs[j].x  = x_a[j];  projs[j].y  = y_a[j];  projs[j].z  = z_a[j];
        projs[j].travel_dist += dst_a[j];

        if projs[j].scale_speed != 0.0 {
            let diff  = projs[j].scale_target - projs[j].scale_x;
            if diff.abs() > 0.001 {
                let delta     = diff * projs[j].scale_speed * dt;
                projs[j].scale_x += delta;
                projs[j].scale_y += delta;
                projs[j].scale_z += delta;
            }
        }
        if projs[j].movement_type == MOVE_ARCHING { projs[j].timer_t += dt; }
    }
}

#[allow(dead_code)]
fn tick_all_3d_scalar(projs: &mut [NativeProjectile3D], dt: f32) -> i32 {
    let mut died = 0_i32;
    for p in projs.iter_mut() { tick_scalar_one_3d(p, dt, &mut died); }
    died
}

fn tick_scalar_one_3d(p: &mut NativeProjectile3D, dt: f32, died: &mut i32) {
    if p.alive == 0 { return; }
    p.lifetime -= dt;
    if p.lifetime <= 0.0 { p.alive = 0; *died += 1; return; }

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

    if p.movement_type != MOVE_TELEPORT && p.movement_type != MOVE_CIRCULAR {
        let dx = p.vx * dt; let dy = p.vy * dt; let dz = p.vz * dt;
        p.travel_dist += fast_sqrt(dx*dx + dy*dy + dz*dz);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  3D movement implementations
// ─────────────────────────────────────────────────────────────────────────────

#[inline(always)]
fn tick_straight_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.vx += p.ax * dt; p.vy += p.ay * dt; p.vz += p.az * dt;
    p.x  += p.vx * dt; p.y  += p.vy * dt; p.z  += p.vz * dt;
}

#[inline(always)]
fn tick_arching_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.vx += p.ax * dt; p.vy += p.ay * dt; p.vz += p.az * dt;
    p.x  += p.vx * dt; p.y  += p.vy * dt; p.z  += p.vz * dt;
    p.timer_t += dt;
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

/// MOVE_CIRCULAR (3D) — helical motion around the forward travel axis.
///
/// FIX: previous code added orbit_pos * dt each frame (position accumulation bug).
/// The bullet was "drifting outward" rather than orbiting.
///
/// Correct approach: compute orbital VELOCITY as the time-derivative of the
/// orbit position function R*(cos(ωt)*u + sin(ωt)*v2):
///   d/dt = R*ω*(-sin(ωt)*u + cos(ωt)*v2)
/// Add this orbital velocity to the forward velocity each frame.
///
/// u  = first perpendicular axis (ax, ay, az) set at spawn by C#
/// v2 = forward × u = second perpendicular, computed each tick
///
/// angular_speed: degrees per second of orbit rotation
/// radius: orbit radius in world units
/// start_angle_deg: initial orbit phase offset
#[inline(always)]
fn tick_circular_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.timer_t += dt;

    if let Some(cp) = config_store::get_circular(p.config_id) {
        let omega = cp.angular_speed.to_radians(); // rad/s
        let angle = p.timer_t * omega + cp.start_angle_deg.to_radians();

        // Forward direction (normalised)
        let spd_sq  = p.vx*p.vx + p.vy*p.vy + p.vz*p.vz;
        let inv_spd = fast_inv_sqrt(spd_sq.max(1e-8));
        let (fx, fy, fz) = (p.vx*inv_spd, p.vy*inv_spd, p.vz*inv_spd);

        // First perp axis stored at spawn in (ax, ay, az)
        let (ux, uy, uz) = (p.ax, p.ay, p.az);

        // Second perp = forward × first_perp
        let (vx2, vy2, vz2) = (
            fy*uz - fz*uy,
            fz*ux - fx*uz,
            fx*uy - fy*ux,
        );

        // Orbital velocity = R*ω * (-sin(angle)*u + cos(angle)*v2)
        // This is d/dt of [R*(cos(ωt)*u + sin(ωt)*v2)]
        let sin_a = angle.sin();
        let cos_a = angle.cos();
        let orb_vx = cp.radius * omega * (-sin_a * ux + cos_a * vx2);
        let orb_vy = cp.radius * omega * (-sin_a * uy + cos_a * vy2);
        let orb_vz = cp.radius * omega * (-sin_a * uz + cos_a * vz2);

        // Advance position by forward + orbital velocity
        p.x += (p.vx + orb_vx) * dt;
        p.y += (p.vy + orb_vy) * dt;
        p.z += (p.vz + orb_vz) * dt;

        // Travel distance from forward component only
        let dx = p.vx * dt; let dy = p.vy * dt; let dz = p.vz * dt;
        p.travel_dist += fast_sqrt(dx*dx + dy*dy + dz*dz);
    } else {
        // No params registered — fall back to straight
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
        let delta = diff * p.scale_speed * dt;
        p.scale_x += delta; p.scale_y += delta; p.scale_z += delta;
    }
}
