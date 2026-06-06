// math/vec3x4.rs
// 4-wide 3D vector — SoA layout.
//
// Same design as Vec2x4 with a z component added.
// Used by the 3D simulation tick and 3D collision narrow phase.
//
// The cross() method here is the key gain over vec2x4 — it lets tick_circular_3d
// compute the second perpendicular axis (v2 = forward × first_perp) for all 4
// projectiles in a batch with no scalar fallback and no per-lane branching.

use crate::math::f32x4::{f32x4, Mask4};
use crate::NativeProjectile3D;

/// 4 simultaneous 3D vectors in SoA layout.
#[derive(Copy, Clone)]
pub struct Vec3x4 {
    pub x: f32x4,
    pub y: f32x4,
    pub z: f32x4,
}

impl Vec3x4 {
    // ── Constructors ──────────────────────────────────────────────────────────

    #[inline(always)]
    pub fn new(x: f32x4, y: f32x4, z: f32x4) -> Self { Self { x, y, z } }

    #[inline(always)]
    pub fn splat(x: f32, y: f32, z: f32) -> Self {
        Self {
            x: f32x4::splat(x),
            y: f32x4::splat(y),
            z: f32x4::splat(z),
        }
    }

    #[inline(always)]
    pub fn zero() -> Self {
        Self { x: f32x4::zero(), y: f32x4::zero(), z: f32x4::zero() }
    }

    // ── Load from NativeProjectile3D slice ────────────────────────────────────

    /// Load (x, y, z) positions from projs[base..base+4].
    #[inline(always)]
    pub fn load_pos(projs: &[NativeProjectile3D], base: usize) -> Self {
        Self {
            x: f32x4::load_from(projs, base, |p| p.x),
            y: f32x4::load_from(projs, base, |p| p.y),
            z: f32x4::load_from(projs, base, |p| p.z),
        }
    }

    /// Load (vx, vy, vz) velocities from projs[base..base+4].
    #[inline(always)]
    pub fn load_vel(projs: &[NativeProjectile3D], base: usize) -> Self {
        Self {
            x: f32x4::load_from(projs, base, |p| p.vx),
            y: f32x4::load_from(projs, base, |p| p.vy),
            z: f32x4::load_from(projs, base, |p| p.vz),
        }
    }

    /// Load (ax, ay, az) from projs[base..base+4].
    /// For MOVE_CIRCULAR 3D: (ax,ay,az) stores the first perpendicular axis.
    /// For MOVE_WAVE 3D:     (ax,ay,az) stores the oscillation axis.
    #[inline(always)]
    pub fn load_accel(projs: &[NativeProjectile3D], base: usize) -> Self {
        Self {
            x: f32x4::load_from(projs, base, |p| p.ax),
            y: f32x4::load_from(projs, base, |p| p.ay),
            z: f32x4::load_from(projs, base, |p| p.az),
        }
    }

    // ── Store back ────────────────────────────────────────────────────────────

    #[inline(always)]
    pub fn store_pos(self, projs: &mut [NativeProjectile3D], base: usize) {
        self.x.store_to(projs, base, |p, v| p.x  = v);
        self.y.store_to(projs, base, |p, v| p.y  = v);
        self.z.store_to(projs, base, |p, v| p.z  = v);
    }

    #[inline(always)]
    pub fn store_vel(self, projs: &mut [NativeProjectile3D], base: usize) {
        self.x.store_to(projs, base, |p, v| p.vx = v);
        self.y.store_to(projs, base, |p, v| p.vy = v);
        self.z.store_to(projs, base, |p, v| p.vz = v);
    }

    // ── Math ──────────────────────────────────────────────────────────────────

    /// Dot product: x*rhs.x + y*rhs.y + z*rhs.z.
    /// Lane i = dot(self[i], rhs[i]).
    #[inline(always)]
    pub fn dot(self, rhs: Self) -> f32x4 {
        self.x * rhs.x + self.y * rhs.y + self.z * rhs.z
    }

    #[inline(always)]
    pub fn length_sq(self) -> f32x4 { self.dot(self) }

    /// Length via full IEEE754 sqrt.
    #[inline(always)]
    pub fn length(self) -> f32x4 { self.length_sq().sqrt() }

    /// Fast length via inv_sqrt approximation.
    #[inline(always)]
    pub fn length_fast(self) -> f32x4 {
        let sq = self.length_sq();
        sq * sq.inv_sqrt()
    }

    /// Normalize. Zero-length vectors return zero (no NaN).
    #[inline(always)]
    pub fn normalize(self) -> Self {
        let sq    = self.length_sq();
        let eps   = f32x4::splat(1e-16_f32);
        let valid = sq.cmpgt(eps);
        let inv   = sq.inv_sqrt();
        Self {
            x: valid.blend(self.x * inv, f32x4::zero()),
            y: valid.blend(self.y * inv, f32x4::zero()),
            z: valid.blend(self.z * inv, f32x4::zero()),
        }
    }

    #[inline(always)]
    pub fn scale(self, s: f32) -> Self {
        let sv = f32x4::splat(s);
        Self { x: self.x * sv, y: self.y * sv, z: self.z * sv }
    }

    #[inline(always)]
    pub fn scale_lanes(self, s: f32x4) -> Self {
        Self { x: self.x * s, y: self.y * s, z: self.z * s }
    }

    /// Cross product per lane: lane i = cross(self[i], rhs[i]).
    ///
    /// cross(a, b) = ( a.y*b.z - a.z*b.y,
    ///                 a.z*b.x - a.x*b.z,
    ///                 a.x*b.y - a.y*b.x )
    ///
    /// Used in tick_circular_3d SIMD path to compute the second perpendicular
    /// orbit axis: v2 = normalize(forward) × first_perp (stored in ax,ay,az).
    /// Computing cross for 4 projectiles simultaneously with 6 mul + 3 sub SIMD
    /// operations vs 4 × (6 scalar mul + 3 scalar sub).
    #[inline(always)]
    pub fn cross(self, rhs: Self) -> Self {
        Self {
            x: self.y * rhs.z - self.z * rhs.y,
            y: self.z * rhs.x - self.x * rhs.z,
            z: self.x * rhs.y - self.y * rhs.x,
        }
    }

    /// Per-lane select: where mask → self, else → other.
    #[inline(always)]
    pub fn select(self, other: Self, mask: Mask4) -> Self {
        Self {
            x: mask.blend(self.x, other.x),
            y: mask.blend(self.y, other.y),
            z: mask.blend(self.z, other.z),
        }
    }

    /// Fused multiply-add per lane: self + a * b.
    /// Useful for position integration: pos + vel * dt.
    #[inline(always)]
    pub fn mul_add(self, a: Self, b: f32x4) -> Self {
        Self {
            x: self.x + a.x * b,
            y: self.y + a.y * b,
            z: self.z + a.z * b,
        }
    }
}

// ── Arithmetic operators ──────────────────────────────────────────────────────

use core::ops::{Add, Sub, Mul, Neg};

impl Add for Vec3x4 {
    type Output = Self;
    #[inline(always)]
    fn add(self, rhs: Self) -> Self {
        Self { x: self.x + rhs.x, y: self.y + rhs.y, z: self.z + rhs.z }
    }
}

impl Sub for Vec3x4 {
    type Output = Self;
    #[inline(always)]
    fn sub(self, rhs: Self) -> Self {
        Self { x: self.x - rhs.x, y: self.y - rhs.y, z: self.z - rhs.z }
    }
}

impl Mul<f32x4> for Vec3x4 {
    type Output = Self;
    #[inline(always)]
    fn mul(self, rhs: f32x4) -> Self {
        Self { x: self.x * rhs, y: self.y * rhs, z: self.z * rhs }
    }
}

impl Mul<f32> for Vec3x4 {
    type Output = Self;
    #[inline(always)]
    fn mul(self, rhs: f32) -> Self { self.scale(rhs) }
}

impl Neg for Vec3x4 {
    type Output = Self;
    #[inline(always)]
    fn neg(self) -> Self {
        Self { x: -self.x, y: -self.y, z: -self.z }
    }
      }
