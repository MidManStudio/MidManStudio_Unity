// simd_avx2.rs — 8-wide AVX2 tier for the Straight-movement batch tick.
//
// RUNTIME-DISPATCHED, NOT COMPILE-TIME GATED — this is the important
// difference from a naive `#[cfg(target_feature = "avx2")]` approach. A
// compile-time gate means whoever BUILDS this binary decides once, for
// every end user, whether AVX2 is required — ship that and it's an illegal-
// instruction crash on any player without AVX2 (which is most CPUs older
// than Haswell, 2013). Instead: both this 8-wide tier AND the existing
// SSE2 4-wide tier are compiled into the SAME binary, and tick_all/
// tick_all_3d in simulation.rs choose between them at runtime via
// is_x86_feature_detected!("avx2"), checked once and cached (see
// avx2_available() below) rather than re-checked every tick. One binary,
// safe on every x86_64 CPU, faster on the ones that support it.
//
// Every function in this module that actually executes an AVX2 instruction
// is marked #[target_feature(enable = "avx2")] and unsafe — Rust's rule for
// these is that calling one is only sound from inside an unsafe block
// reached after a runtime is_x86_feature_detected!("avx2") check has
// already returned true (simulation.rs's dispatch does exactly that; this
// module never calls itself without going through that gate).
//
// SCOPE: deliberately minimal — only what tick_straight_x8/tick_straight_x8_3d
// (simulation.rs) actually use. Not a general-purpose 8-wide math library the
// way math/f32x4.rs's 4-wide tier is (that one backs Vec2x4/Vec3x4, used by
// collision.rs's narrow phase and several movement types, so it carries a
// wider API surface). This one exists for exactly one job: batch-tick 8
// Straight projectiles at once. atan2 is NOT vectorized here either, same
// reasoning as the WASM SIMD128 tier in f32x4.rs: extract to scalar, call
// crate::simd::fast_atan2 eight times, reassemble — not worth a vectorized
// trig polynomial for one call per batch.

#![allow(non_camel_case_types)]

use core::arch::x86_64::*;
use core::sync::atomic::{AtomicU8, Ordering};

use crate::{NativeProjectile, NativeProjectile3D};

// ─────────────────────────────────────────────────────────────────────────────
//  Cached runtime feature detection
// ─────────────────────────────────────────────────────────────────────────────

const UNCHECKED: u8 = 0;
const AVAILABLE:  u8 = 1;
const UNAVAILABLE: u8 = 2;

static AVX2_STATE: AtomicU8 = AtomicU8::new(UNCHECKED);

/// True if this CPU supports AVX2 — checked once (std::is_x86_feature_detected!
/// itself has its own internal caching too via a cpuid read, but this adds a
/// second, even cheaper layer: a single atomic load on every call after the
/// first, versus is_x86_feature_detected!'s own — still fast — path).
#[inline]
pub fn avx2_available() -> bool {
    match AVX2_STATE.load(Ordering::Relaxed) {
        AVAILABLE   => true,
        UNAVAILABLE => false,
        _ => {
            let detected = is_x86_feature_detected!("avx2");
            AVX2_STATE.store(if detected { AVAILABLE } else { UNAVAILABLE }, Ordering::Relaxed);
            detected
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  f32x8
// ─────────────────────────────────────────────────────────────────────────────

/// 8-wide f32 — AVX2 backend (__m256 is available under plain AVX, but this
/// module is gated to AVX2 as a whole since that's the feature this crate
/// promises callers, and AVX2 implies AVX is present too).
#[derive(Copy, Clone)]
pub struct f32x8(__m256);

/// AVX comparison mask — each lane 0xFFFFFFFF (true) or 0x00000000 (false),
/// same convention as SSE2/NEON/WASM's Mask4.
#[derive(Copy, Clone)]
pub struct Mask8(__m256);

impl f32x8 {
    #[target_feature(enable = "avx2")]
    pub unsafe fn splat(v: f32) -> Self { Self(_mm256_set1_ps(v)) }

    #[target_feature(enable = "avx2")]
    pub unsafe fn zero() -> Self { Self(_mm256_setzero_ps()) }

    #[target_feature(enable = "avx2")]
    pub unsafe fn from_array(a: [f32; 8]) -> Self { Self(_mm256_loadu_ps(a.as_ptr())) }

    #[target_feature(enable = "avx2")]
    pub unsafe fn to_array(self) -> [f32; 8] {
        let mut out = [0f32; 8];
        _mm256_storeu_ps(out.as_mut_ptr(), self.0);
        out
    }

    /// Gather one f32 field from 8 consecutive slice elements.
    #[target_feature(enable = "avx2")]
    pub unsafe fn load_from<T>(slice: &[T], base: usize, f: impl Fn(&T) -> f32) -> Self {
        debug_assert!(base + 8 <= slice.len());
        Self::from_array([
            f(&slice[base]),     f(&slice[base + 1]), f(&slice[base + 2]), f(&slice[base + 3]),
            f(&slice[base + 4]), f(&slice[base + 5]), f(&slice[base + 6]), f(&slice[base + 7]),
        ])
    }

    /// Scatter 8 lane values back into one f32 field of 8 consecutive elements.
    #[target_feature(enable = "avx2")]
    pub unsafe fn store_to<T>(self, slice: &mut [T], base: usize, f: impl Fn(&mut T, f32)) {
        debug_assert!(base + 8 <= slice.len());
        let a = self.to_array();
        for i in 0..8 { f(&mut slice[base + i], a[i]); }
    }

    /// Add self to an f32 field in-place across 8 slice elements — mirrors
    /// f32x4::add_to (used for travel_dist accumulation).
    #[target_feature(enable = "avx2")]
    pub unsafe fn add_to<T>(self, slice: &mut [T], base: usize, f: impl Fn(&mut T) -> &mut f32) {
        debug_assert!(base + 8 <= slice.len());
        let a = self.to_array();
        for i in 0..8 { *f(&mut slice[base + i]) += a[i]; }
    }

    #[target_feature(enable = "avx2")]
    pub unsafe fn min(self, rhs: Self) -> Self { Self(_mm256_min_ps(self.0, rhs.0)) }

    #[target_feature(enable = "avx2")]
    pub unsafe fn max(self, rhs: Self) -> Self { Self(_mm256_max_ps(self.0, rhs.0)) }

    /// sqrt(x) — full IEEE754. Guards negative lanes to 0 first, same
    /// convention as every other platform tier.
    #[target_feature(enable = "avx2")]
    pub unsafe fn sqrt(self) -> Self {
        let safe = _mm256_max_ps(self.0, _mm256_setzero_ps());
        Self(_mm256_sqrt_ps(safe))
    }

    /// 1/sqrt(x) via _mm256_rsqrt_ps (hardware approximate reciprocal sqrt,
    /// ~12-bit precision) + one Newton-Raphson refinement — same shape as
    /// SSE2's rsqrtps+NR, just 8 lanes at once. Clamps to ≥ 1e-20 first,
    /// same guard as every other platform tier.
    #[target_feature(enable = "avx2")]
    pub unsafe fn inv_sqrt(self) -> Self {
        let safe  = _mm256_max_ps(self.0, _mm256_set1_ps(1e-20_f32));
        let r0    = _mm256_rsqrt_ps(safe);
        let half  = _mm256_set1_ps(0.5_f32);
        let three = _mm256_set1_ps(3.0_f32);
        let xrr   = _mm256_mul_ps(safe, _mm256_mul_ps(r0, r0)); // x*r²
        // r_new = 0.5 * r * (3 - x*r²)
        Self(_mm256_mul_ps(_mm256_mul_ps(half, r0), _mm256_sub_ps(three, xrr)))
    }

    #[target_feature(enable = "avx2")]
    pub unsafe fn cmple(self, rhs: Self) -> Mask8 {
        Mask8(_mm256_cmp_ps(self.0, rhs.0, _CMP_LE_OQ))
    }

    /// atan2(y, x) for 8 lanes — NOT vectorized, see module header. Extracts
    /// to scalar, calls crate::simd::fast_atan2 eight times, reassembles.
    #[target_feature(enable = "avx2")]
    pub unsafe fn atan2(y: Self, x: Self) -> Self {
        let ya = y.to_array();
        let xa = x.to_array();
        let mut out = [0f32; 8];
        for i in 0..8 { out[i] = crate::simd::fast_atan2(ya[i], xa[i]); }
        Self::from_array(out)
    }

    #[target_feature(enable = "avx2")]
    pub unsafe fn to_degrees(self) -> Self {
        Self(_mm256_mul_ps(self.0, _mm256_set1_ps(57.295_779_51_f32)))
    }
}

impl Mask8 {
    /// True if any lane is non-zero.
    #[target_feature(enable = "avx2")]
    pub unsafe fn any(self) -> bool { _mm256_movemask_ps(self.0) != 0 }
}

// NOT core::ops::Add/Sub/Mul — those traits require a safe `fn` signature,
// which is incompatible with #[target_feature]+unsafe (confirmed by trying:
// rustc rejects it with "expected normal fn, found unsafe fn"). Plain
// inherent methods instead, called as a.add(b) rather than a + b — the same
// pattern real SIMD crates (wide, packed_simd) use for exactly this reason.
impl f32x8 {
    #[target_feature(enable = "avx2")]
    pub unsafe fn add(self, r: Self) -> Self { Self(_mm256_add_ps(self.0, r.0)) }
    #[target_feature(enable = "avx2")]
    pub unsafe fn sub(self, r: Self) -> Self { Self(_mm256_sub_ps(self.0, r.0)) }
    #[target_feature(enable = "avx2")]
    pub unsafe fn mul(self, r: Self) -> Self { Self(_mm256_mul_ps(self.0, r.0)) }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Vec2x8 / Vec3x8 — SoA, same shape as math/vec2x4.rs and vec3x4.rs
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Copy, Clone)]
pub struct Vec2x8 { pub x: f32x8, pub y: f32x8 }

impl Vec2x8 {
    #[target_feature(enable = "avx2")]
    pub unsafe fn load_pos(projs: &[NativeProjectile], base: usize) -> Self {
        Self { x: f32x8::load_from(projs, base, |p| p.x), y: f32x8::load_from(projs, base, |p| p.y) }
    }
    #[target_feature(enable = "avx2")]
    pub unsafe fn load_vel(projs: &[NativeProjectile], base: usize) -> Self {
        Self { x: f32x8::load_from(projs, base, |p| p.vx), y: f32x8::load_from(projs, base, |p| p.vy) }
    }
    #[target_feature(enable = "avx2")]
    pub unsafe fn load_accel(projs: &[NativeProjectile], base: usize) -> Self {
        Self { x: f32x8::load_from(projs, base, |p| p.ax), y: f32x8::load_from(projs, base, |p| p.ay) }
    }
    #[target_feature(enable = "avx2")]
    pub unsafe fn store_pos(self, projs: &mut [NativeProjectile], base: usize) {
        self.x.store_to(projs, base, |p, v| p.x = v);
        self.y.store_to(projs, base, |p, v| p.y = v);
    }
    #[target_feature(enable = "avx2")]
    pub unsafe fn store_vel(self, projs: &mut [NativeProjectile], base: usize) {
        self.x.store_to(projs, base, |p, v| p.vx = v);
        self.y.store_to(projs, base, |p, v| p.vy = v);
    }
    #[target_feature(enable = "avx2")]
    pub unsafe fn add(self, rhs: Self) -> Self { Self { x: self.x.add(rhs.x), y: self.y.add(rhs.y) } }
    #[target_feature(enable = "avx2")]
    pub unsafe fn mul_wide(self, s: f32x8) -> Self { Self { x: self.x.mul(s), y: self.y.mul(s) } }
    #[target_feature(enable = "avx2")]
    pub unsafe fn length_fast(self) -> f32x8 {
        let sq = self.x.mul(self.x).add(self.y.mul(self.y));
        sq.mul(sq.inv_sqrt())
    }
    #[target_feature(enable = "avx2")]
    pub unsafe fn angle_deg(self) -> f32x8 { f32x8::atan2(self.y, self.x).to_degrees() }
}

#[derive(Copy, Clone)]
pub struct Vec3x8 { pub x: f32x8, pub y: f32x8, pub z: f32x8 }

impl Vec3x8 {
    #[target_feature(enable = "avx2")]
    pub unsafe fn load_pos(projs: &[NativeProjectile3D], base: usize) -> Self {
        Self {
            x: f32x8::load_from(projs, base, |p| p.x),
            y: f32x8::load_from(projs, base, |p| p.y),
            z: f32x8::load_from(projs, base, |p| p.z),
        }
    }
    #[target_feature(enable = "avx2")]
    pub unsafe fn load_vel(projs: &[NativeProjectile3D], base: usize) -> Self {
        Self {
            x: f32x8::load_from(projs, base, |p| p.vx),
            y: f32x8::load_from(projs, base, |p| p.vy),
            z: f32x8::load_from(projs, base, |p| p.vz),
        }
    }
    #[target_feature(enable = "avx2")]
    pub unsafe fn load_accel(projs: &[NativeProjectile3D], base: usize) -> Self {
        Self {
            x: f32x8::load_from(projs, base, |p| p.ax),
            y: f32x8::load_from(projs, base, |p| p.ay),
            z: f32x8::load_from(projs, base, |p| p.az),
        }
    }
    #[target_feature(enable = "avx2")]
    pub unsafe fn store_pos(self, projs: &mut [NativeProjectile3D], base: usize) {
        self.x.store_to(projs, base, |p, v| p.x = v);
        self.y.store_to(projs, base, |p, v| p.y = v);
        self.z.store_to(projs, base, |p, v| p.z = v);
    }
    #[target_feature(enable = "avx2")]
    pub unsafe fn store_vel(self, projs: &mut [NativeProjectile3D], base: usize) {
        self.x.store_to(projs, base, |p, v| p.vx = v);
        self.y.store_to(projs, base, |p, v| p.vy = v);
        self.z.store_to(projs, base, |p, v| p.vz = v);
    }
    #[target_feature(enable = "avx2")]
    pub unsafe fn add(self, rhs: Self) -> Self {
        Self { x: self.x.add(rhs.x), y: self.y.add(rhs.y), z: self.z.add(rhs.z) }
    }
    #[target_feature(enable = "avx2")]
    pub unsafe fn mul_wide(self, s: f32x8) -> Self {
        Self { x: self.x.mul(s), y: self.y.mul(s), z: self.z.mul(s) }
    }
    #[target_feature(enable = "avx2")]
    pub unsafe fn length_fast(self) -> f32x8 {
        let sq = self.x.mul(self.x).add(self.y.mul(self.y)).add(self.z.mul(self.z));
        sq.mul(sq.inv_sqrt())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // These tests only run any actual AVX2 instructions when the CI/test
    // machine genuinely has AVX2 — guarded the same way production code is,
    // so this test suite is itself a demonstration of the safe-dispatch
    // pattern, not an exception to it.

    #[test]
    fn splat_and_array_roundtrip() {
        if !avx2_available() { return; }
        unsafe {
            let v = f32x8::splat(3.5);
            assert_eq!(v.to_array(), [3.5f32; 8]);
        }
    }

    #[test]
    fn arithmetic_matches_scalar() {
        if !avx2_available() { return; }
        unsafe {
            let a = f32x8::from_array([1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0]);
            let b = f32x8::from_array([8.0, 7.0, 6.0, 5.0, 4.0, 3.0, 2.0, 1.0]);
            let sum = a.add(b);
            assert_eq!(sum.to_array(), [9.0f32; 8]);
            let prod = a.mul(b);
            assert_eq!(prod.to_array(), [8.0, 14.0, 18.0, 20.0, 20.0, 18.0, 14.0, 8.0]);
        }
    }

    #[test]
    fn inv_sqrt_matches_scalar_within_tolerance() {
        if !avx2_available() { return; }
        unsafe {
            let v = f32x8::from_array([1.0, 4.0, 9.0, 16.0, 25.0, 36.0, 49.0, 64.0]);
            let r = v.inv_sqrt().to_array();
            for (i, &val) in [1.0f32, 4.0, 9.0, 16.0, 25.0, 36.0, 49.0, 64.0].iter().enumerate() {
                let expected = 1.0 / val.sqrt();
                assert!((r[i] - expected).abs() < 1e-3, "lane {i}: got {}, expected {expected}", r[i]);
            }
        }
    }

    #[test]
    fn vec2x8_gather_scatter_roundtrip() {
        if !avx2_available() { return; }
        unsafe {
            let mut projs: Vec<NativeProjectile> = (0..8).map(|i| {
                let mut p = NativeProjectile::default();
                p.x = i as f32; p.y = (i * 2) as f32;
                p
            }).collect();

            let pos = Vec2x8::load_pos(&projs, 0);
            let moved = pos.add(Vec2x8 { x: f32x8::splat(10.0), y: f32x8::splat(20.0) });
            moved.store_pos(&mut projs, 0);

            for (i, p) in projs.iter().enumerate() {
                assert_eq!(p.x, i as f32 + 10.0);
                assert_eq!(p.y, (i * 2) as f32 + 20.0);
            }
        }
    }
}
