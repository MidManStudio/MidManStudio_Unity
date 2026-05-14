// lib.rs — all public FFI exports
// Unity P/Invoke (DllImport / __Internal on iOS)
//
// 2D struct sizes:
//   NativeProjectile   = 72 bytes
//   HitResult          = 24 bytes
//   CollisionTarget    = 20 bytes
//   SpawnRequest       = 32 bytes
//
// 3D struct sizes:
//   NativeProjectile3D  = 84 bytes
//   HitResult3D         = 28 bytes
//   CollisionTarget3D   = 24 bytes
//
// SIMD acceleration (mid-math derived, see simd.rs):
//   x86/x86_64: SSE2 guaranteed — 4-wide tick batching, fast_atan2_x4,
//                rsqrt_nr for sqrt/normalize, SIMD narrow-phase collision.
//   All others: scalar fast_atan2 + Quake rsqrt (still ~6-8x vs libm).

mod simulation;
mod collision;
mod patterns;
mod state;
mod config_store;
mod simd;            // ← new

pub use simulation::*;
pub use collision::*;
pub use patterns::*;
pub use state::*;
pub use config_store::*;

use std::slice;

// ─────────────────────────────────────────────────────────────────────────────
//  Movement type constants
// ─────────────────────────────────────────────────────────────────────────────

#[no_mangle] pub extern "C" fn movement_type_straight()  -> u8 { simulation::MOVE_STRAIGHT  }
#[no_mangle] pub extern "C" fn movement_type_arching()   -> u8 { simulation::MOVE_ARCHING   }
#[no_mangle] pub extern "C" fn movement_type_guided()    -> u8 { simulation::MOVE_GUIDED    }
#[no_mangle] pub extern "C" fn movement_type_teleport()  -> u8 { simulation::MOVE_TELEPORT  }
#[no_mangle] pub extern "C" fn movement_type_wave()      -> u8 { simulation::MOVE_WAVE      }
#[no_mangle] pub extern "C" fn movement_type_circular()  -> u8 { simulation::MOVE_CIRCULAR  }

// ─────────────────────────────────────────────────────────────────────────────
//  2D data types
// ─────────────────────────────────────────────────────────────────────────────

/// Core 2D projectile state. 72 bytes.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct NativeProjectile {
    pub x:          f32,   // 0
    pub y:          f32,   // 4
    pub vx:         f32,   // 8
    pub vy:         f32,   // 12
    pub ax:         f32,   // 16
    pub ay:         f32,   // 20
    pub angle_deg:  f32,   // 24
    pub curve_t:    f32,   // 28
    pub scale_x:      f32, // 32
    pub scale_y:      f32, // 36
    pub scale_target: f32, // 40
    pub scale_speed:  f32, // 44
    pub lifetime:     f32, // 48
    pub max_lifetime: f32, // 52
    pub travel_dist:  f32, // 56
    pub config_id: u16,    // 60
    pub owner_id:  u16,    // 62
    pub proj_id:   u32,    // 64
    pub collision_count: u8, // 68
    pub movement_type:   u8, // 69
    pub piercing_type:   u8, // 70
    pub alive:           u8, // 71
}

/// 2D hit event. 24 bytes.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct HitResult {
    pub proj_id:     u32,  // 0
    pub proj_index:  u32,  // 4
    pub target_id:   u32,  // 8
    pub travel_dist: f32,  // 12
    pub hit_x:       f32,  // 16
    pub hit_y:       f32,  // 20
}

/// 2D collision target. 20 bytes.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct CollisionTarget {
    pub x:         f32,    // 0
    pub y:         f32,    // 4
    pub radius:    f32,    // 8
    pub target_id: u32,    // 12
    pub active:    u8,     // 16
    pub _pad:      [u8; 3],// 17
}

/// Spawn request. 32 bytes.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct SpawnRequest {
    pub origin_x:     f32,    // 0
    pub origin_y:     f32,    // 4
    pub angle_deg:    f32,    // 8
    pub speed:        f32,    // 12
    pub config_id:    u16,    // 16
    pub owner_id:     u16,    // 18
    pub pattern_id:   u8,     // 20
    pub _pad:         [u8; 3],// 21
    pub rng_seed:     u32,    // 24
    pub base_proj_id: u32,    // 28
}

// ─────────────────────────────────────────────────────────────────────────────
//  3D data types
// ─────────────────────────────────────────────────────────────────────────────

/// Core 3D projectile state. 84 bytes.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct NativeProjectile3D {
    pub x:  f32,  // 0
    pub y:  f32,  // 4
    pub z:  f32,  // 8
    pub vx: f32,  // 12
    pub vy: f32,  // 16
    pub vz: f32,  // 20
    pub ax: f32,  // 24
    pub ay: f32,  // 28
    pub az: f32,  // 32
    pub scale_x:      f32, // 36
    pub scale_y:      f32, // 40
    pub scale_z:      f32, // 44
    pub scale_target: f32, // 48
    pub scale_speed:  f32, // 52
    pub lifetime:     f32, // 56
    pub max_lifetime: f32, // 60
    pub travel_dist:  f32, // 64
    pub timer_t:      f32, // 68
    pub config_id: u16,    // 72
    pub owner_id:  u16,    // 74
    pub proj_id:   u32,    // 76
    pub collision_count: u8, // 80
    pub movement_type:   u8, // 81
    pub piercing_type:   u8, // 82
    pub alive:           u8, // 83
}

/// 3D hit event. 28 bytes.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct HitResult3D {
    pub proj_id:     u32,  // 0
    pub proj_index:  u32,  // 4
    pub target_id:   u32,  // 8
    pub travel_dist: f32,  // 12
    pub hit_x:       f32,  // 16
    pub hit_y:       f32,  // 20
    pub hit_z:       f32,  // 24
}

/// 3D collision target. 24 bytes.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct CollisionTarget3D {
    pub x:         f32,    // 0
    pub y:         f32,    // 4
    pub z:         f32,    // 8
    pub radius:    f32,    // 12
    pub target_id: u32,    // 16
    pub active:    u8,     // 20
    pub _pad:      [u8; 3],// 21
}

// ─────────────────────────────────────────────────────────────────────────────
//  Tick — 2D
// ─────────────────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn tick_projectiles(
    projs: *mut NativeProjectile,
    count: i32,
    dt:    f32,
) -> i32 {
    if projs.is_null() || count <= 0 { return 0; }
    let slice = slice::from_raw_parts_mut(projs, count as usize);
    simulation::tick_all(slice, dt)
}

// ─────────────────────────────────────────────────────────────────────────────
//  Tick — 3D
// ─────────────────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn tick_projectiles_3d(
    projs: *mut NativeProjectile3D,
    count: i32,
    dt:    f32,
) -> i32 {
    if projs.is_null() || count <= 0 { return 0; }
    let slice = slice::from_raw_parts_mut(projs, count as usize);
    simulation::tick_all_3d(slice, dt)
}

// ─────────────────────────────────────────────────────────────────────────────
//  Collision — 2D
// ─────────────────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn check_hits_grid(
    projs:         *const NativeProjectile,
    proj_count:    i32,
    targets:       *const CollisionTarget,
    target_count:  i32,
    out_hits:      *mut HitResult,
    max_hits:      i32,
    out_hit_count: *mut i32,
) {
    check_hits_grid_ex(
        projs, proj_count, targets, target_count,
        out_hits, max_hits, 0.0, out_hit_count);
}

#[no_mangle]
pub unsafe extern "C" fn check_hits_grid_ex(
    projs:         *const NativeProjectile,
    proj_count:    i32,
    targets:       *const CollisionTarget,
    target_count:  i32,
    out_hits:      *mut HitResult,
    max_hits:      i32,
    cell_size:     f32,
    out_hit_count: *mut i32,
) {
    unsafe fn zero(p: *mut i32) { if !p.is_null() { *p = 0; } }

    if projs.is_null() || targets.is_null() || out_hits.is_null() {
        zero(out_hit_count); return;
    }
    let projs_s   = slice::from_raw_parts(projs,    proj_count   as usize);
    let targets_s = slice::from_raw_parts(targets,  target_count as usize);
    let hits_s    = slice::from_raw_parts_mut(out_hits, max_hits  as usize);
    let count     = collision::check_hits(projs_s, targets_s, hits_s, cell_size);
    if !out_hit_count.is_null() { *out_hit_count = count as i32; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Collision — 3D
// ─────────────────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn check_hits_grid_3d(
    projs:         *const NativeProjectile3D,
    proj_count:    i32,
    targets:       *const CollisionTarget3D,
    target_count:  i32,
    out_hits:      *mut HitResult3D,
    max_hits:      i32,
    cell_size:     f32,
    out_hit_count: *mut i32,
) {
    unsafe fn zero(p: *mut i32) { if !p.is_null() { *p = 0; } }

    if projs.is_null() || targets.is_null() || out_hits.is_null() {
        zero(out_hit_count); return;
    }
    let projs_s   = slice::from_raw_parts(projs,    proj_count   as usize);
    let targets_s = slice::from_raw_parts(targets,  target_count as usize);
    let hits_s    = slice::from_raw_parts_mut(out_hits, max_hits  as usize);
    let count     = collision::check_hits_3d(projs_s, targets_s, hits_s, cell_size);
    if !out_hit_count.is_null() { *out_hit_count = count as i32; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Spawn — pattern path (legacy)
// ─────────────────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn spawn_pattern(
    req:       *const SpawnRequest,
    out_projs: *mut NativeProjectile,
    max_out:   i32,
    out_count: *mut i32,
) {
    if req.is_null() || out_projs.is_null() {
        if !out_count.is_null() { *out_count = 0; }
        return;
    }
    let req_ref = &*req;
    let out_s   = slice::from_raw_parts_mut(out_projs, max_out as usize);
    let count   = patterns::generate(req_ref, out_s);
    if !out_count.is_null() { *out_count = count as i32; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Spawn — batch path
// ─────────────────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn spawn_batch(
    projs_in:  *const NativeProjectile,
    count:     i32,
    projs_out: *mut NativeProjectile,
    max_out:   i32,
    out_count: *mut i32,
) {
    if projs_in.is_null() || projs_out.is_null() || count <= 0 {
        if !out_count.is_null() { *out_count = 0; }
        return;
    }
    let n   = (count as usize).min(max_out as usize);
    let src = slice::from_raw_parts(projs_in, n);
    let dst = slice::from_raw_parts_mut(projs_out, n);
    dst.copy_from_slice(src);
    if !out_count.is_null() { *out_count = n as i32; }
}

#[no_mangle]
pub unsafe extern "C" fn spawn_batch_3d(
    projs_in:  *const NativeProjectile3D,
    count:     i32,
    projs_out: *mut NativeProjectile3D,
    max_out:   i32,
    out_count: *mut i32,
) {
    if projs_in.is_null() || projs_out.is_null() || count <= 0 {
        if !out_count.is_null() { *out_count = 0; }
        return;
    }
    let n   = (count as usize).min(max_out as usize);
    let src = slice::from_raw_parts(projs_in, n);
    let dst = slice::from_raw_parts_mut(projs_out, n);
    dst.copy_from_slice(src);
    if !out_count.is_null() { *out_count = n as i32; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  State save / restore
// ─────────────────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn save_state(
    projs:   *const NativeProjectile,
    count:   i32,
    buf:     *mut u8,
    buf_len: i32,
) -> i32 {
    if projs.is_null() || buf.is_null() { return 0; }
    let slice = slice::from_raw_parts(projs, count as usize);
    state::save(slice, buf, buf_len as usize) as i32
}

#[no_mangle]
pub unsafe extern "C" fn restore_state(
    out_projs:  *mut NativeProjectile,
    max_count:  i32,
    buf:        *const u8,
    buf_len:    i32,
    out_count:  *mut i32,
) {
    if out_projs.is_null() || buf.is_null() {
        if !out_count.is_null() { *out_count = 0; }
        return;
    }
    let out_s = slice::from_raw_parts_mut(out_projs, max_count as usize);
    let n     = state::restore(out_s, buf, buf_len as usize);
    if !out_count.is_null() { *out_count = n as i32; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Movement parameter registration
// ─────────────────────────────────────────────────────────────────────────────

#[no_mangle]
pub extern "C" fn register_wave_params(
    config_id:    u16,
    amplitude:    f32,
    frequency:    f32,
    phase_offset: f32,
    vertical:     u8,
) {
    config_store::register_wave(config_id, config_store::WaveParams {
        amplitude,
        frequency,
        phase_offset,
        vertical: vertical != 0,
        _pad: [0u8; 3],
    });
}

#[no_mangle]
pub extern "C" fn register_circular_params(
    config_id:     u16,
    radius:        f32,
    angular_speed: f32,
    start_angle:   f32,
) {
    config_store::register_circular(config_id, config_store::CircularParams {
        radius,
        angular_speed,
        start_angle_deg: start_angle,
    });
}

#[no_mangle]
pub extern "C" fn unregister_wave_params(config_id: u16) {
    config_store::unregister_wave(config_id);
}

#[no_mangle]
pub extern "C" fn unregister_circular_params(config_id: u16) {
    config_store::unregister_circular(config_id);
}

#[no_mangle]
pub extern "C" fn clear_movement_params() {
    config_store::clear_all();
}

// ─────────────────────────────────────────────────────────────────────────────
//  Layout validation
// ─────────────────────────────────────────────────────────────────────────────

#[no_mangle] pub extern "C" fn projectile_struct_size()      -> i32 { core::mem::size_of::<NativeProjectile>()   as i32 }
#[no_mangle] pub extern "C" fn hit_result_struct_size()      -> i32 { core::mem::size_of::<HitResult>()          as i32 }
#[no_mangle] pub extern "C" fn collision_target_struct_size()-> i32 { core::mem::size_of::<CollisionTarget>()    as i32 }
#[no_mangle] pub extern "C" fn spawn_request_struct_size()   -> i32 { core::mem::size_of::<SpawnRequest>()       as i32 }
#[no_mangle] pub extern "C" fn projectile3d_struct_size()    -> i32 { core::mem::size_of::<NativeProjectile3D>() as i32 }
#[no_mangle] pub extern "C" fn hit_result3d_struct_size()    -> i32 { core::mem::size_of::<HitResult3D>()        as i32 }
#[no_mangle] pub extern "C" fn collision_target3d_struct_size()->i32 { core::mem::size_of::<CollisionTarget3D>() as i32 }

// ─────────────────────────────────────────────────────────────────────────────
//  Compile-time layout assertions
// ─────────────────────────────────────────────────────────────────────────────

const _: () = assert!(core::mem::size_of::<NativeProjectile>()   == 72);
const _: () = assert!(core::mem::size_of::<HitResult>()          == 24);
const _: () = assert!(core::mem::size_of::<CollisionTarget>()     == 20);
const _: () = assert!(core::mem::size_of::<SpawnRequest>()        == 32);
const _: () = assert!(core::mem::size_of::<NativeProjectile3D>()  == 84);
const _: () = assert!(core::mem::size_of::<HitResult3D>()         == 28);
const _: () = assert!(core::mem::size_of::<CollisionTarget3D>()   == 24);
