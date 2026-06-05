// math/f32x4.rs
// 4-wide f32 SIMD type — SSE2 | NEON | scalar fallback.
//
// Three mutually-exclusive `mod platform` blocks (only one compiled per target).
// Re-exported flat via `pub use platform::{f32x4, Mask4}`.
// Platform-independent utilities (load_from, store_to) follow the pub use,
// implemented in terms of from_array/to_array which each platform provides.
//
// Calls into crate::simd::{sse2, neon}::fast_atan2_x4 to avoid duplicating
// the Nvidia-Cg polynomial. The scalar atan2 uses crate::simd::fast_atan2.
//
// Accuracy:
//   inv_sqrt x86:    rsqrtps + NR    → ~23-bit
//   inv_sqrt aarch64: vrsqrteq + NR → ~16-bit  (fine for game physics)
//   sqrt     x86:    sqrtps          → IEEE754 exact
//   sqrt     aarch64: vsqrtq_f32     → IEEE754 exact (AArch64 has FSQRT.4S)
//   sqrt     scalar: f32::sqrt       → IEEE754 exact
//   atan2    all:    Horner poly      → ~0.005 rad max error (~0.3°)

#![allow(non_camel_case_types)]

// ─────────────────────────────────────────────────────────────────────────────
//  SSE2 — x86 / x86_64
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
mod platform {
    #[cfg(target_arch = "x86")]    use core::arch::x86::*;
    #[cfg(target_arch = "x86_64")] use core::arch::x86_64::*;
    use core::ops::{Add, Sub, Mul, Div, Neg};

    /// 4-wide f32 — SSE2 backend. Inner `__m128` is pub(crate) for simd.rs callsites.
    #[derive(Copy, Clone)]
    pub struct f32x4(pub(crate) __m128);

    /// SSE2 comparison mask. Each lane is 0xFFFF_FFFF (true) or 0x0000_0000 (false).
    #[derive(Copy, Clone)]
    pub struct Mask4(pub(crate) __m128);

    impl f32x4 {
        #[inline(always)]
        pub fn splat(v: f32) -> Self { unsafe { Self(_mm_set1_ps(v)) } }

        #[inline(always)]
        pub fn zero() -> Self { unsafe { Self(_mm_setzero_ps()) } }

        /// Load from array. Lane 0 = a[0], lane 3 = a[3].
        /// Uses _mm_loadu_ps — natural memory order, no reversal.
        #[inline(always)]
        pub fn from_array(a: [f32; 4]) -> Self {
            unsafe { Self(_mm_loadu_ps(a.as_ptr())) }
        }

        /// Store to array. [0] = lane 0, [3] = lane 3.
        #[inline(always)]
        pub fn to_array(self) -> [f32; 4] {
            // __m128 in-memory: bits[31:0] at lowest address = lane 0.
            // transmute preserves natural lane order.
            unsafe { core::mem::transmute(self.0) }
        }

        #[inline(always)]
        pub fn abs(self) -> Self {
            // Clear sign bit: ANDNOT(-0.0, x) = |x|
            unsafe { Self(_mm_andnot_ps(_mm_set1_ps(-0.0_f32), self.0)) }
        }

        #[inline(always)]
        pub fn min(self, rhs: Self) -> Self { unsafe { Self(_mm_min_ps(self.0, rhs.0)) } }

        #[inline(always)]
        pub fn max(self, rhs: Self) -> Self { unsafe { Self(_mm_max_ps(self.0, rhs.0)) } }

        /// 1/sqrt(x). rsqrtps (~11-bit) + one Newton-Raphson step → ~23-bit.
        /// Clamps lanes to ≥ 1e-20 before estimate to guard zero/negative.
        #[inline(always)]
        pub fn inv_sqrt(self) -> Self {
            unsafe {
                let safe  = _mm_max_ps(self.0, _mm_set1_ps(1e-20_f32));
                let r     = _mm_rsqrt_ps(safe);
                let half  = _mm_set1_ps(0.5_f32);
                let three = _mm_set1_ps(3.0_f32);
                let xrr   = _mm_mul_ps(safe, _mm_mul_ps(r, r)); // x*r²
                // r_new = 0.5 * r * (3 - x*r²)
                Self(_mm_mul_ps(_mm_mul_ps(half, r), _mm_sub_ps(three, xrr)))
            }
        }

        /// sqrt(x) via sqrtps — full IEEE754 (~14 cycles). Guards negative lanes to 0.
        #[inline(always)]
        pub fn sqrt(self) -> Self {
            unsafe {
                let safe = _mm_max_ps(self.0, _mm_setzero_ps());
                Self(_mm_sqrt_ps(safe))
            }
        }

        // ── Comparisons ───────────────────────────────────────────────────────

        #[inline(always)]
        pub fn cmple(self, rhs: Self) -> Mask4 { unsafe { Mask4(_mm_cmple_ps(self.0, rhs.0)) } }
        #[inline(always)]
        pub fn cmplt(self, rhs: Self) -> Mask4 { unsafe { Mask4(_mm_cmplt_ps(self.0, rhs.0)) } }
        #[inline(always)]
        pub fn cmpgt(self, rhs: Self) -> Mask4 { unsafe { Mask4(_mm_cmpgt_ps(self.0, rhs.0)) } }

        // ── Trig ──────────────────────────────────────────────────────────────

        /// atan2(y, x) for 4 lanes in radians. Delegates to simd::sse2::fast_atan2_x4.
        #[inline(always)]
        pub fn atan2(y: Self, x: Self) -> Self {
            unsafe { Self(crate::simd::sse2::fast_atan2_x4(y.0, x.0)) }
        }

        /// Radians → degrees for all lanes.
        #[inline(always)]
        pub fn to_degrees(self) -> Self {
            unsafe { Self(_mm_mul_ps(self.0, _mm_set1_ps(57.295_779_51_f32))) }
        }

        // ── Misc ──────────────────────────────────────────────────────────────

        /// Negate lanes where mask is true, pass-through where false.
        /// Implemented as XOR of sign bit with mask.
        #[inline(always)]
        pub fn neg_if(self, mask: Mask4) -> Self {
            unsafe {
                // sign_bits = mask & -0.0 — only sign bit set per active lane
                let sign_bits = _mm_and_ps(mask.0, _mm_set1_ps(-0.0_f32));
                Self(_mm_xor_ps(self.0, sign_bits))
            }
        }
    }

    // ── Arithmetic operators ──────────────────────────────────────────────────

    impl Add for f32x4 { type Output=Self; #[inline(always)] fn add(self,r:Self)->Self { unsafe{Self(_mm_add_ps(self.0,r.0))} } }
    impl Sub for f32x4 { type Output=Self; #[inline(always)] fn sub(self,r:Self)->Self { unsafe{Self(_mm_sub_ps(self.0,r.0))} } }
    impl Mul for f32x4 { type Output=Self; #[inline(always)] fn mul(self,r:Self)->Self { unsafe{Self(_mm_mul_ps(self.0,r.0))} } }
    impl Div for f32x4 { type Output=Self; #[inline(always)] fn div(self,r:Self)->Self { unsafe{Self(_mm_div_ps(self.0,r.0))} } }
    impl Neg for f32x4 { type Output=Self; #[inline(always)] fn neg(self)->Self { unsafe{Self(_mm_xor_ps(self.0,_mm_set1_ps(-0.0_f32)))} } }

    // Scalar rhs — promotes to splat then calls the Self variant
    impl Add<f32> for f32x4 { type Output=Self; #[inline(always)] fn add(self,r:f32)->Self { self+Self::splat(r) } }
    impl Sub<f32> for f32x4 { type Output=Self; #[inline(always)] fn sub(self,r:f32)->Self { self-Self::splat(r) } }
    impl Mul<f32> for f32x4 { type Output=Self; #[inline(always)] fn mul(self,r:f32)->Self { self*Self::splat(r) } }
    impl Div<f32> for f32x4 { type Output=Self; #[inline(always)] fn div(self,r:f32)->Self { self/Self::splat(r) } }

    // ── Mask4 ─────────────────────────────────────────────────────────────────

    impl Mask4 {
        /// True if any lane is non-zero.
        #[inline(always)]
        pub fn any(self) -> bool {
            unsafe { _mm_movemask_ps(self.0) != 0 }
        }

        /// Pack MSB of each lane into a 4-bit integer. Bit 0 = lane 0.
        /// Used by collision code to find the first hit lane via trailing_zeros().
        #[inline(always)]
        pub fn movemask(self) -> u32 {
            unsafe { _mm_movemask_ps(self.0) as u32 }
        }

        /// Per-lane select: true lane → if_true, false lane → if_false.
        /// SSE2: AND + ANDNOT + OR (SSE4.1 blendvps not assumed).
        #[inline(always)]
        pub fn blend(self, if_true: f32x4, if_false: f32x4) -> f32x4 {
            unsafe {
                f32x4(_mm_or_ps(
                    _mm_and_ps   (self.0, if_true.0),
                    _mm_andnot_ps(self.0, if_false.0),
                ))
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  NEON — aarch64
//
//  NEON is mandatory on all AArch64 — no target_feature gate needed.
//
//  Key differences from SSE2:
//    Comparisons return uint32x4_t (not float32x4_t).
//    vbslq_f32(mask: uint32x4_t, a, b) handles blend directly.
//    vsqrtq_f32 / vdivq_f32 are AArch64-only (FSQRT.4S / FDIV.4S) — safe here.
//    Sign negate: XOR float bits with masked 0x80000000.
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(target_arch = "aarch64")]
mod platform {
    use core::arch::aarch64::*;
    use core::ops::{Add, Sub, Mul, Div, Neg};

    #[derive(Copy, Clone)]
    pub struct f32x4(pub(crate) float32x4_t);

    /// NEON comparison mask. Each lane: 0xFFFF_FFFF = true, 0 = false.
    #[derive(Copy, Clone)]
    pub struct Mask4(pub(crate) uint32x4_t);

    impl f32x4 {
        #[inline(always)]
        pub fn splat(v: f32) -> Self { unsafe { Self(vdupq_n_f32(v)) } }

        #[inline(always)]
        pub fn zero() -> Self { unsafe { Self(vdupq_n_f32(0.0_f32)) } }

        /// vld1q_f32: load 4 consecutive f32s in natural memory order.
        #[inline(always)]
        pub fn from_array(a: [f32; 4]) -> Self {
            unsafe { Self(vld1q_f32(a.as_ptr())) }
        }

        /// vst1q_f32: store 4 lanes in natural memory order.
        #[inline(always)]
        pub fn to_array(self) -> [f32; 4] {
            let mut a = [0.0_f32; 4];
            unsafe { vst1q_f32(a.as_mut_ptr(), self.0); }
            a
        }

        #[inline(always)]
        pub fn abs(self) -> Self { unsafe { Self(vabsq_f32(self.0)) } }

        #[inline(always)]
        pub fn min(self, rhs: Self) -> Self { unsafe { Self(vminq_f32(self.0, rhs.0)) } }

        #[inline(always)]
        pub fn max(self, rhs: Self) -> Self { unsafe { Self(vmaxq_f32(self.0, rhs.0)) } }

        /// 1/sqrt(x). vrsqrteq (~8-bit) + vrsqrtsq NR step → ~16-bit.
        /// vrsqrtsq_f32(a, b) computes (3 - a*b) / 2 in one instruction.
        #[inline(always)]
        pub fn inv_sqrt(self) -> Self {
            unsafe {
                let safe = vmaxq_f32(self.0, vdupq_n_f32(1e-20_f32));
                let r    = vrsqrteq_f32(safe);
                // r_new = r * ((3 - safe*r*r) / 2)
                Self(vmulq_f32(r, vrsqrtsq_f32(vmulq_f32(safe, r), r)))
            }
        }

        /// vsqrtq_f32: AArch64 FSQRT.4S — full IEEE754. Guards negative to 0.
        #[inline(always)]
        pub fn sqrt(self) -> Self {
            unsafe {
                let safe = vmaxq_f32(self.0, vdupq_n_f32(0.0_f32));
                Self(vsqrtq_f32(safe))
            }
        }

        // ── Comparisons — return uint32x4_t mask ─────────────────────────────

        #[inline(always)]
        pub fn cmple(self, rhs: Self) -> Mask4 { unsafe { Mask4(vcleq_f32(self.0, rhs.0)) } }
        #[inline(always)]
        pub fn cmplt(self, rhs: Self) -> Mask4 { unsafe { Mask4(vcltq_f32(self.0, rhs.0)) } }
        #[inline(always)]
        pub fn cmpgt(self, rhs: Self) -> Mask4 { unsafe { Mask4(vcgtq_f32(self.0, rhs.0)) } }

        // ── Trig ──────────────────────────────────────────────────────────────

        #[inline(always)]
        pub fn atan2(y: Self, x: Self) -> Self {
            unsafe { Self(crate::simd::neon::fast_atan2_x4(y.0, x.0)) }
        }

        #[inline(always)]
        pub fn to_degrees(self) -> Self {
            unsafe { Self(vmulq_f32(self.0, vdupq_n_f32(57.295_779_51_f32))) }
        }

        // ── Misc ──────────────────────────────────────────────────────────────

        /// Negate lanes where mask is true.
        /// XOR float bits with (mask & 0x8000_0000) — flips sign bit per active lane.
        #[inline(always)]
        pub fn neg_if(self, mask: Mask4) -> Self {
            unsafe {
                let sign_bit = vandq_u32(mask.0, vdupq_n_u32(0x8000_0000u32));
                Self(vreinterpretq_f32_u32(veorq_u32(
                    vreinterpretq_u32_f32(self.0),
                    sign_bit,
                )))
            }
        }
    }

    // ── Arithmetic operators ──────────────────────────────────────────────────

    impl Add for f32x4 { type Output=Self; #[inline(always)] fn add(self,r:Self)->Self { unsafe{Self(vaddq_f32(self.0,r.0))} } }
    impl Sub for f32x4 { type Output=Self; #[inline(always)] fn sub(self,r:Self)->Self { unsafe{Self(vsubq_f32(self.0,r.0))} } }
    impl Mul for f32x4 { type Output=Self; #[inline(always)] fn mul(self,r:Self)->Self { unsafe{Self(vmulq_f32(self.0,r.0))} } }
    impl Div for f32x4 { type Output=Self; #[inline(always)] fn div(self,r:Self)->Self { unsafe{Self(vdivq_f32(self.0,r.0))} } }
    impl Neg for f32x4 { type Output=Self; #[inline(always)] fn neg(self)->Self { unsafe{Self(vnegq_f32(self.0))} } }

    impl Add<f32> for f32x4 { type Output=Self; #[inline(always)] fn add(self,r:f32)->Self { self+Self::splat(r) } }
    impl Sub<f32> for f32x4 { type Output=Self; #[inline(always)] fn sub(self,r:f32)->Self { self-Self::splat(r) } }
    impl Mul<f32> for f32x4 { type Output=Self; #[inline(always)] fn mul(self,r:f32)->Self { self*Self::splat(r) } }
    impl Div<f32> for f32x4 { type Output=Self; #[inline(always)] fn div(self,r:f32)->Self { self/Self::splat(r) } }

    // ── Mask4 ─────────────────────────────────────────────────────────────────

    impl Mask4 {
        /// True if any lane is non-zero.
        /// NEON has no movemask equivalent — use u64 reinterpret trick.
        #[inline(always)]
        pub fn any(self) -> bool {
            unsafe {
                let u64v = vreinterpretq_u64_u32(self.0);
                (vgetq_lane_u64(u64v, 0) | vgetq_lane_u64(u64v, 1)) != 0
            }
        }

        /// 4-bit movemask equivalent: bit i = lane i non-zero.
        /// Used by collision to find first hit lane via trailing_zeros().
        #[inline(always)]
        pub fn movemask(self) -> u32 {
            unsafe {
                // NEON has no horizontal extract that gives the MSBs packed.
                // Manual per-lane test — 4 vgetq_lane_u32 + OR.
                let l0 = (vgetq_lane_u32(self.0, 0) != 0) as u32;
                let l1 = (vgetq_lane_u32(self.0, 1) != 0) as u32;
                let l2 = (vgetq_lane_u32(self.0, 2) != 0) as u32;
                let l3 = (vgetq_lane_u32(self.0, 3) != 0) as u32;
                l0 | (l1 << 1) | (l2 << 2) | (l3 << 3)
            }
        }

        /// Per-lane select via vbslq_f32 (bit-select — NEON's native blend).
        #[inline(always)]
        pub fn blend(self, if_true: f32x4, if_false: f32x4) -> f32x4 {
            unsafe { f32x4(vbslq_f32(self.0, if_true.0, if_false.0)) }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Scalar fallback — wasm32, armv7, and anything else.
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
mod platform {
    use core::ops::{Add, Sub, Mul, Div, Neg};

    #[derive(Copy, Clone)]
    pub struct f32x4(pub(crate) [f32; 4]);

    #[derive(Copy, Clone)]
    pub struct Mask4([u32; 4]); // 0xFFFF_FFFF = true, 0 = false

    // Helper: apply a 1-to-1 mapping across 4 lanes
    macro_rules! lanes {
        ($a:expr, $op:expr) => {
            [($op)($a.0[0]), ($op)($a.0[1]), ($op)($a.0[2]), ($op)($a.0[3])]
        };
    }
    macro_rules! lanes2 {
        ($a:expr, $b:expr, $op:expr) => {
            [($op)($a.0[0], $b.0[0]), ($op)($a.0[1], $b.0[1]),
             ($op)($a.0[2], $b.0[2]), ($op)($a.0[3], $b.0[3])]
        };
    }

    impl f32x4 {
        #[inline(always)] pub fn splat(v: f32) -> Self       { Self([v, v, v, v]) }
        #[inline(always)] pub fn zero() -> Self               { Self([0.0; 4]) }
        #[inline(always)] pub fn from_array(a: [f32; 4]) -> Self { Self(a) }
        #[inline(always)] pub fn to_array(self) -> [f32; 4]  { self.0 }

        #[inline(always)]
        pub fn abs(self) -> Self { Self(lanes!(self, |x: f32| x.abs())) }

        #[inline(always)]
        pub fn min(self, r: Self) -> Self { Self(lanes2!(self, r, |a: f32, b| a.min(b))) }

        #[inline(always)]
        pub fn max(self, r: Self) -> Self { Self(lanes2!(self, r, |a: f32, b| a.max(b))) }

        /// Quake-style rsqrt: bit-trick + one NR step → ~23-bit.
        #[inline(always)]
        pub fn inv_sqrt(self) -> Self {
            fn rsqrt(x: f32) -> f32 {
                let x  = x.max(1e-20);
                let x2 = x * 0.5;
                let mut i: u32 = x.to_bits();
                i = 0x5f37_59df_u32.wrapping_sub(i >> 1);
                let r = f32::from_bits(i);
                r * (1.5_f32 - x2 * r * r) // one NR step → ~23-bit
            }
            Self(lanes!(self, rsqrt))
        }

        #[inline(always)]
        pub fn sqrt(self) -> Self {
            Self(lanes!(self, |x: f32| x.max(0.0).sqrt()))
        }

        #[inline(always)]
        pub fn cmple(self, r: Self) -> Mask4 {
            Mask4(lanes2!(self, r, |a: f32, b| if a <= b { !0u32 } else { 0u32 }))
        }
        #[inline(always)]
        pub fn cmplt(self, r: Self) -> Mask4 {
            Mask4(lanes2!(self, r, |a: f32, b| if a < b  { !0u32 } else { 0u32 }))
        }
        #[inline(always)]
        pub fn cmpgt(self, r: Self) -> Mask4 {
            Mask4(lanes2!(self, r, |a: f32, b| if a > b  { !0u32 } else { 0u32 }))
        }

        #[inline(always)]
        pub fn atan2(y: Self, x: Self) -> Self {
            Self([crate::simd::fast_atan2(y.0[0], x.0[0]),
                  crate::simd::fast_atan2(y.0[1], x.0[1]),
                  crate::simd::fast_atan2(y.0[2], x.0[2]),
                  crate::simd::fast_atan2(y.0[3], x.0[3])])
        }

        #[inline(always)]
        pub fn to_degrees(self) -> Self { self * Self::splat(57.295_779_51_f32) }

        #[inline(always)]
        pub fn neg_if(self, mask: Mask4) -> Self {
            Self([if mask.0[0] != 0 { -self.0[0] } else { self.0[0] },
                  if mask.0[1] != 0 { -self.0[1] } else { self.0[1] },
                  if mask.0[2] != 0 { -self.0[2] } else { self.0[2] },
                  if mask.0[3] != 0 { -self.0[3] } else { self.0[3] }])
        }
    }

    // ── Arithmetic operators ──────────────────────────────────────────────────

    impl Add for f32x4 { type Output=Self; fn add(self,r:Self)->Self { Self(lanes2!(self,r,|a:f32,b|a+b)) } }
    impl Sub for f32x4 { type Output=Self; fn sub(self,r:Self)->Self { Self(lanes2!(self,r,|a:f32,b|a-b)) } }
    impl Mul for f32x4 { type Output=Self; fn mul(self,r:Self)->Self { Self(lanes2!(self,r,|a:f32,b|a*b)) } }
    impl Div for f32x4 { type Output=Self; fn div(self,r:Self)->Self { Self(lanes2!(self,r,|a:f32,b|a/b)) } }
    impl Neg for f32x4 { type Output=Self; fn neg(self)->Self { Self(lanes!(self,|a:f32|-a)) } }

    impl Add<f32> for f32x4 { type Output=Self; fn add(self,r:f32)->Self { self+Self::splat(r) } }
    impl Sub<f32> for f32x4 { type Output=Self; fn sub(self,r:f32)->Self { self-Self::splat(r) } }
    impl Mul<f32> for f32x4 { type Output=Self; fn mul(self,r:f32)->Self { self*Self::splat(r) } }
    impl Div<f32> for f32x4 { type Output=Self; fn div(self,r:f32)->Self { self/Self::splat(r) } }

    // ── Mask4 ─────────────────────────────────────────────────────────────────

    impl Mask4 {
        #[inline(always)]
        pub fn any(self) -> bool { (self.0[0] | self.0[1] | self.0[2] | self.0[3]) != 0 }

        #[inline(always)]
        pub fn movemask(self) -> u32 {
            (if self.0[0] != 0 { 1 } else { 0 })
          | (if self.0[1] != 0 { 2 } else { 0 })
          | (if self.0[2] != 0 { 4 } else { 0 })
          | (if self.0[3] != 0 { 8 } else { 0 })
        }

        #[inline(always)]
        pub fn blend(self, t: f32x4, f: f32x4) -> f32x4 {
            f32x4([if self.0[0]!=0{t.0[0]}else{f.0[0]},
                   if self.0[1]!=0{t.0[1]}else{f.0[1]},
                   if self.0[2]!=0{t.0[2]}else{f.0[2]},
                   if self.0[3]!=0{t.0[3]}else{f.0[3]}])
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Re-export the platform types
// ─────────────────────────────────────────────────────────────────────────────

pub use platform::{f32x4, Mask4};

// ─────────────────────────────────────────────────────────────────────────────
//  Platform-independent utilities
//  These use from_array / to_array so they compile identically on all targets.
// ─────────────────────────────────────────────────────────────────────────────

impl f32x4 {
    /// Gather one f32 field from 4 consecutive elements of a slice.
    ///
    /// ```ignore
    /// let lt = f32x4::load_from(projs, i, |p| p.lifetime);
    /// ```
    #[inline(always)]
    pub fn load_from<T>(slice: &[T], base: usize, f: impl Fn(&T) -> f32) -> Self {
        debug_assert!(base + 4 <= slice.len());
        Self::from_array([
            f(&slice[base]),
            f(&slice[base + 1]),
            f(&slice[base + 2]),
            f(&slice[base + 3]),
        ])
    }

    /// Scatter 4 lane values back into one f32 field of 4 consecutive slice elements.
    ///
    /// ```ignore
    /// lt_new.store_to(projs, i, |p, v| p.lifetime = v);
    /// ```
    #[inline(always)]
    pub fn store_to<T>(self, slice: &mut [T], base: usize, f: impl Fn(&mut T, f32)) {
        debug_assert!(base + 4 <= slice.len());
        let a = self.to_array();
        f(&mut slice[base],     a[0]);
        f(&mut slice[base + 1], a[1]);
        f(&mut slice[base + 2], a[2]);
        f(&mut slice[base + 3], a[3]);
    }

    /// Convenience: add self to an f32 field in-place across 4 slice elements.
    ///
    /// ```ignore
    /// dist_delta.add_to(projs, i, |p| &mut p.travel_dist);
    /// ```
    #[inline(always)]
    pub fn add_to<T>(self, slice: &mut [T], base: usize, f: impl Fn(&mut T) -> &mut f32) {
        debug_assert!(base + 4 <= slice.len());
        let a = self.to_array();
        *f(&mut slice[base])     += a[0];
        *f(&mut slice[base + 1]) += a[1];
        *f(&mut slice[base + 2]) += a[2];
        *f(&mut slice[base + 3]) += a[3];
    }
}
