use crate::{NativeProjectile, SpawnRequest};

const PAT_SINGLE:  u8 = 0;
const PAT_SPREAD3: u8 = 1;
const PAT_SPREAD5: u8 = 2;
const PAT_SPIRAL:  u8 = 3;
const PAT_RING8:   u8 = 4;

pub fn generate(req: &SpawnRequest, out: &mut [NativeProjectile]) -> usize {
    match req.pattern_id {
        PAT_SINGLE  => gen_spread(req, out, 1, 0.0),
        PAT_SPREAD3 => gen_spread(req, out, 3, 20.0),
        PAT_SPREAD5 => gen_spread(req, out, 5, 15.0),
        PAT_SPIRAL  => gen_ring(req, out, 12),
        PAT_RING8   => gen_ring(req, out, 8),
        _           => gen_spread(req, out, 1, 0.0),
    }
}

fn gen_spread(
    req: &SpawnRequest,
    out: &mut [NativeProjectile],
    count: usize,
    spread_deg: f32,
) -> usize {
    let n    = count.min(out.len());
    let half = (n as f32 - 1.0) * 0.5;

    for i in 0..n {
        let offset_deg = (i as f32 - half) * spread_deg;
        let angle = (req.angle_deg + offset_deg).to_radians();
        let speed = req.speed * if i == 0 { 1.0 } else {
            lcg_f32(req.rng_seed.wrapping_add(i as u32)) * 0.1 + 0.95
        };
        out[i] = make_projectile(req, angle, speed, i);
    }
    n
}

fn gen_ring(req: &SpawnRequest, out: &mut [NativeProjectile], count: usize) -> usize {
    let n    = count.min(out.len());
    let step = std::f32::consts::TAU / n as f32;

    for i in 0..n {
        let angle = req.angle_deg.to_radians() + step * i as f32;
        out[i] = make_projectile(req, angle, req.speed, i);
    }
    n
}

/// Build a single NativeProjectile from a spawn request.
///
/// Scale fields are intentionally set to "full size, no growth":
///   scale_x / scale_y = 1.0  (rendered at actual size immediately)
///   scale_target       = 1.0  (no lerp needed)
///   scale_speed        = 0.0  (tells tick_scale to skip this projectile)
///
/// C# Spawn() overwrites these from ProjectileConfigSO AFTER generate() returns.
/// Only configs where SpawnScaleFraction < 1.0 will set scale_speed > 0,
/// enabling growth for that specific projectile type.
fn make_projectile(
    req: &SpawnRequest,
    angle_rad: f32,
    speed: f32,
    index: usize,
) -> NativeProjectile {
    NativeProjectile {
        x: req.origin_x,
        y: req.origin_y,
        vx: angle_rad.cos() * speed,
        vy: angle_rad.sin() * speed,
        ax: 0.0,
        ay: 0.0,
        angle_deg: angle_rad.to_degrees(),
        curve_t: 0.0,

        // Full size, no growth — C# overrides from config if the bullet type grows
        scale_x:      1.0,
        scale_y:      1.0,
        scale_target: 1.0,
        scale_speed:  0.0,

        lifetime:     0.0,   // C# fills from config
        max_lifetime: 0.0,   // C# fills from config
        travel_dist:  0.0,

        config_id:       req.config_id,
        owner_id:        req.owner_id,
        proj_id:         req.base_proj_id.wrapping_add(index as u32),
        collision_count: 0,
        movement_type:   0,   // C# fills from config
        piercing_type:   0,   // C# fills from config
        alive:           1,
    }
}

/// Minimal LCG for deterministic speed variance — same seed = same result on all clients
fn lcg_f32(seed: u32) -> f32 {
    let s = seed.wrapping_mul(1664525).wrapping_add(1013904223);
    (s >> 8) as f32 / 16777216.0
}
