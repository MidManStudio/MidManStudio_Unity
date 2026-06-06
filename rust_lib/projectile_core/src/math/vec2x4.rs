// math/vec2x4.rs
// 4-wide 2D vector — SoA layout: separate f32x4 for x and y.
//
// SoA (Structure of Arrays) vs AoS (Array of Structures):
//   AoS: [x0,y0, x1,y1, x2,y2, x3,y3] — natural for single structs
//   SoA: [x0,x1,x2,x3], [y0,y1,y2,y3] — SIMD-natural, no shuffle needed
//
// NativeProjectile stores AoS (x,y,vx,vy all in one struct).
// load_pos / load_vel gather from 4 consecutive structs into SoA for processing.
// store_pos / store_vel scatter the results back.
//
// All arithmetic is component-wise across 4 projectiles simultaneously.
// The rotate() method is the key addition over raw f32x4 — used by MOVE_CIRCULAR
// to rotate all 4 velocity vectors by omega*dt per tick without scalar fallback.

use crate::math::f32x4::{f32x4, Mask4};
use crate::NativeProjectile;

/// 4 simultaneous 2D vectors in SoA layout.
#[derive(Copy, Clone)]
pub struct Vec2x4 {
    pub x: f32x4,
    pub y: f32x4,
}

impl Vec2x4 {
    // ── Constructors ──────────────────────────────────────────────────────────

    #[inline(always)]
    pub fn new(x: f32x4, y: f32x4) -> Self { Self { x, y } }

    #[inline(always)]
    pub fn splat(x: f32, y: f32) -> Self {
        Self { x: f32x4::splat(x), y: f32x4::splat(y) }
    }

    #[inline(always)]
    pub fn zero() -> Self { Self { x: f32x4::zero(), y: f32x4::zero() } }

    // ── Load from NativeProjectile slice ─────────────────────────────────────
    // These gather one field from each of 4 consecutive structs into a SIMD lane.

    /// Load (x, y) positions from projs[base..base+4].
    #[inline(always)]
    pub fn load_pos(projs: &[NativeProjectile], base: usize) -> Self {
        Self {
            x: f32x4::load_from(projs, base, |p| p.x),
            y: f32x4::load_from(projs, base, |p| p.y),
        }
    }

    /// Load (vx, vy) velocities from projs[base..base+4].
    #[inline(always)]
    pub fn load_vel(projs: &[NativeProjectile], base: usize) -> Self {
        Self {
            x: f32x4::load_from(projs, base, |p| p.vx),
            y: f32x4::load_from(projs, base, |p| p.vy),
        }
    }

    /// Load (ax, ay) accelerations from projs[base..base+4].
    #[inline(always)]
    pub fn load_accel(projs: &[NativeProjectile], base: usize) -> Self {
        Self {
            x: f32x4::load_from(projs, base, |p| p.ax),
            y: f32x4::load_from(projs, base, |p| p.ay),
        }
    }

    // ── Store back to NativeProjectile slice ──────────────────────────────────

    #[inline(always)]
    pub fn store_pos(self, projs: &mut [NativeProjectile], base: usize) {
        self.x.store_to(projs, base, |p, v| p.x  = v);
        self.y.store_to(projs, base, |p, v| p.y  = v);
    }

    #[inline(always)]
    pub fn store_vel(self, projs: &mut [NativeProjectile], base: usize) {
        self.x.store_to(projs, base, |p, v| p.vx = v);
        self.y.store_to(projs, base, |p, v| p.vy = v);
    }

    // ── Math ──────────────────────────────────────────────────────────────────

    /// Component-wise dot product: x*rhs.x + y*rhs.y.
    /// Returns f32x4 where lane i = dot(self[i], rhs[i]).
    #[inline(always)]
    pub fn dot(self, rhs: Self) -> f32x4 {
        self.x * rhs.x + self.y * rhs.y
    }

    /// Squared length: x² + y².
    #[inline(always)]
    pub fn length_sq(self) -> f32x4 { self.dot(self) }

    /// Length via full IEEE754 sqrt.
    #[inline(always)]
    pub fn length(self) -> f32x4 { self.length_sq().sqrt() }

    /// Fast length via inv_sqrt approximation: len_sq * inv_sqrt(len_sq).
    /// x86: ~23-bit | aarch64: ~16-bit. Fine for travel_dist accumulation.
    #[inline(always)]
    pub fn length_fast(self) -> f32x4 {
        let sq = self.length_sq();
        sq * sq.inv_sqrt()
    }

    /// Normalize using inv_sqrt. Zero-length vectors return zero rather than NaN.
    #[inline(always)]
    pub fn normalize(self) -> Self {
        let sq    = self.length_sq();
        let eps   = f32x4::splat(1e-16_f32);
        let valid = sq.cmpgt(eps);        // Mask4: true where sq > epsilon
        let inv   = sq.inv_sqrt();
        Self {
            x: valid.blend(self.x * inv, f32x4::zero()),
            y: valid.blend(self.y * inv, f32x4::zero()),
        }
    }

    /// Scale all components by a scalar f32.
    #[inline(always)]
    pub fn scale(self, s: f32) -> Self {
        let sv = f32x4::splat(s);
        Self { x: self.x * sv, y: self.y * sv }
    }

    /// Scale each lane independently by f32x4.
    #[inline(always)]
    pub fn scale_lanes(self, s: f32x4) -> Self {
        Self { x: self.x * s, y: self.y * s }
    }

    /// 2D cross product (scalar per lane): self.x*rhs.y - self.y*rhs.x.
    /// Used for perpendicular generation and rotation direction detection.
    #[inline(always)]
    pub fn cross(self, rhs: Self) -> f32x4 {
        self.x * rhs.y - self.y * rhs.x
    }

    /// Rotate each velocity vector by angle given as (cos, sin) per lane.
    ///
    /// Rotation matrix: [ cos, -sin ]   x' = cos*x - sin*y
    ///                  [ sin,  cos ]   y' = sin*x + cos*y
    ///
    /// Used by MOVE_CIRCULAR 2D tick — rotate all 4 velocity vectors by omega*dt
    /// simultaneously rather than a per-projectile trig call.
    ///
    /// Since all 4 projectiles in a batch use the same omega*dt (same config),
    /// cos and sin are typically splats: cos = f32x4::splat(theta.cos()).
    /// For different configs in the same batch, pass per-lane cos/sin.
    #[inline(always)]
    pub fn rotate(self, cos: f32x4, sin: f32x4) -> Self {
        Self {
            x: self.x * cos - self.y * sin,
            y: self.x * sin + self.y * cos,
        }
    }

    /// Per-lane atan2(y, x) in radians.
    #[inline(always)]
    pub fn angle_rad(self) -> f32x4 { f32x4::atan2(self.y, self.x) }

    /// Per-lane atan2(y, x) in degrees. Used for angle_deg field update.
    #[inline(always)]
    pub fn angle_deg(self) -> f32x4 { self.angle_rad().to_degrees() }

    // ── Per-lane select ───────────────────────────────────────────────────────

    /// where mask: use self, else: use other.
    #[inline(always)]
    pub fn select(self, other: Self, mask: Mask4) -> Self {
        Self {
            x: mask.blend(self.x, other.x),
            y: mask.blend(self.y, other.y),
        }
    }
}

// ── Arithmetic operators ──────────────────────────────────────────────────────

use core::ops::{Add, Sub, Mul, Neg};

impl Add for Vec2x4 {
    type Output = Self;
    #[inline(always)]
    fn add(self, rhs: Self) -> Self {
        Self { x: self.x + rhs.x, y: self.y + rhs.y }
    }
}

impl Sub for Vec2x4 {
    type Output = Self;
    #[inline(always)]
    fn sub(self, rhs: Self) -> Self {
        Self { x: self.x - rhs.x, y: self.y - rhs.y }
    }
}

impl Mul<f32x4> for Vec2x4 {
    type Output = Self;
    #[inline(always)]
    fn mul(self, rhs: f32x4) -> Self {
        Self { x: self.x * rhs, y: self.y * rhs }
    }
}

impl Mul<f32> for Vec2x4 {
    type Output = Self;
    #[inline(always)]
    fn mul(self, rhs: f32) -> Self { self.scale(rhs) }
}

impl Neg for Vec2x4 {
    type Output = Self;
    #[inline(always)]
    fn neg(self) -> Self { Self { x: -self.x, y: -self.y } }
  }
