// rust_lib/projectile_core/src/simd.rs
// ADDED: pub(crate) mod neon — NEON fast_atan2_x4 for aarch64.
// Previously f32x4.rs (NEON platform block) called crate::simd::neon::fast_atan2_x4
// which did not exist, causing a compile error on all aarch64 targets (iOS, Android ARM,
// Apple Silicon dev machines). The polynomial is identical to the SSE2 version;
// only the intrinsics differ.
//
// No other changes to existing SSE2 or scalar paths.

// ─────────────────────────────────────────────────────────────────────────────
//  SSE2 module — x86 / x86_64 only
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
pub(crate) mod sse2 {
    #[cfg(target_arch = "x86")]
    use core::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use core::arch::x86_64::*;

    // Source: mid-math/src/sse2.rs  m128_from_f32x4
    #[inline(always)]
    pub fn m128_from_f32x4(a: [f32; 4]) -> __m128 {
        unsafe { core::mem::transmute(a) }
    }

    // Source: mid-math/src/wide/float/sse2/f32x4.rs  rsqrt_nr
    #[inline(always)]
    pub unsafe fn rsqrt_nr(x: __m128) -> __m128 {
        let r     = _mm_rsqrt_ps(x);
        let half  = _mm_set1_ps(0.5_f32);
        let three = _mm_set1_ps(3.0_f32);
        let xrr   = _mm_mul_ps(x, _mm_mul_ps(r, r));
        _mm_mul_ps(_mm_mul_ps(half, r), _mm_sub_ps(three, xrr))
    }

    #[inline(always)]
    pub unsafe fn rsqrt_nr_ss(x: f32) -> f32 {
        let xv    = _mm_set_ss(x);
        let r     = _mm_rsqrt_ss(xv);
        let half  = _mm_set_ss(0.5_f32);
        let three = _mm_set_ss(3.0_f32);
        let xrr   = _mm_mul_ss(xv, _mm_mul_ss(r, r));
        _mm_cvtss_f32(_mm_mul_ss(_mm_mul_ss(half, r), _mm_sub_ss(three, xrr)))
    }

    // Source: mid-math/src/sse2.rs  m128_abs
    #[inline(always)]
    pub unsafe fn m128_abs(v: __m128) -> __m128 {
        _mm_andnot_ps(_mm_set1_ps(-0.0_f32), v)
    }

    // Source: mid-math/src/wide/float/sse2/f32x4.rs  f32x4::blend
    #[inline(always)]
    pub unsafe fn blend(mask: __m128, if_true: __m128, if_false: __m128) -> __m128 {
        _mm_or_ps(
            _mm_and_ps(mask, if_true),
            _mm_andnot_ps(mask, if_false),
        )
    }

    // Polynomial pattern: mid-math acos_approx Horner form
    // Coefficients: Nvidia Cg standard library fast atan2
    // Max error: ~0.005 rad (~0.3°). Cost: ~20 cycles for 4 values.
    pub unsafe fn fast_atan2_x4(y: __m128, x: __m128) -> __m128 {
        let ax = m128_abs(x);
        let ay = m128_abs(y);

        let min_v = _mm_min_ps(ax, ay);
        let max_v = _mm_max_ps(ax, ay);
        let safe  = _mm_max_ps(max_v, _mm_set1_ps(1e-10_f32));
        let a     = _mm_div_ps(min_v, safe);

        let s  = _mm_mul_ps(a, a);
        let c0 = m128_from_f32x4([-0.046_496_474_9_f32; 4]);
        let c1 = m128_from_f32x4([ 0.159_314_22_f32;  4]);
        let c2 = m128_from_f32x4([-0.327_622_764_f32; 4]);

        let t    = _mm_add_ps(_mm_mul_ps(c0, s), c1);
        let t    = _mm_add_ps(_mm_mul_ps(t,  s), c2);
        let t    = _mm_mul_ps(_mm_mul_ps(t,  s), a);
        let poly = _mm_add_ps(t, a);

        let ay_gt_ax = _mm_cmpgt_ps(ay, ax);
        let fpi2     = _mm_set1_ps(core::f32::consts::FRAC_PI_2);
        let r = blend(ay_gt_ax, _mm_sub_ps(fpi2, poly), poly);

        let x_neg = _mm_cmplt_ps(x, _mm_setzero_ps());
        let pi    = _mm_set1_ps(core::f32::consts::PI);
        let r = blend(x_neg, _mm_sub_ps(pi, r), r);

        let y_neg = _mm_cmplt_ps(y, _mm_setzero_ps());
        _mm_xor_ps(r, _mm_and_ps(y_neg, _mm_set1_ps(-0.0_f32)))
    }

    #[inline(always)]
    pub unsafe fn rad_to_deg_x4(v: __m128) -> __m128 {
        _mm_mul_ps(v, _mm_set1_ps(57.295_779_51_f32))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  NEON module — aarch64 only
//
//  Fills the crate::simd::neon::fast_atan2_x4 symbol that f32x4.rs (aarch64
//  platform block) calls.  Without this, the crate fails to compile on any
//  aarch64 target (iOS, Android ARM64, Apple Silicon).
//
//  Algorithm: identical polynomial to SSE2 version.
//    a = min(|x|, |y|) / max(|x|, |y|)  →  [0, 1]
//    poly = Horner: ((-0.0464964749·s + 0.15931422)·s − 0.327622764)·s·a + a
//    Reflect if |y| > |x|, adjust quadrant for x < 0, apply sign from y.
//
//  NEON differences from SSE2:
//    Comparisons return uint32x4_t (not float32x4_t).
//    vbslq_f32(mask: uint32x4_t, a, b) is the native blend.
//    vmlaq_f32(a, b, c) = a + b*c  (fused multiply-accumulate).
//    vdivq_f32 is AArch64-only (FDIV.4S) — safe since we only target aarch64.
//    Sign bit XOR: must reinterpret float↔uint32.
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(target_arch = "aarch64")]
pub(crate) mod neon {
    use core::arch::aarch64::*;

    /// Fast atan2(y, x) for 4 lanes simultaneously using NEON.
    /// Max error ~0.005 rad (~0.3°). Called by f32x4::atan2 on aarch64.
    ///
    /// # Safety
    /// Caller must ensure pointers are valid NEON registers. The function
    /// itself performs no memory accesses.
    pub unsafe fn fast_atan2_x4(y: float32x4_t, x: float32x4_t) -> float32x4_t {
        let zero = vdupq_n_f32(0.0_f32);

        let ax = vabsq_f32(x);
        let ay = vabsq_f32(y);

        // a = min(|x|, |y|) / max(|x|, |y|), clamped to [0, 1]
        let min_v = vminq_f32(ax, ay);
        let max_v = vmaxq_f32(ax, ay);
        // Guard divide-by-zero: max(max_v, 1e-10)
        let safe  = vmaxq_f32(max_v, vdupq_n_f32(1e-10_f32));
        let a     = vdivq_f32(min_v, safe);

        // s = a²
        let s = vmulq_f32(a, a);

        // Horner polynomial: ((c0·s + c1)·s + c2)·s·a + a
        // vmlaq_f32(acc, b, c) = acc + b*c
        let c0 = vdupq_n_f32(-0.046_496_474_9_f32);
        let c1 = vdupq_n_f32( 0.159_314_22_f32);
        let c2 = vdupq_n_f32(-0.327_622_764_f32);

        let t    = vmlaq_f32(c1, c0, s);          // c0·s + c1
        let t    = vmlaq_f32(c2, t,  s);          // (c0·s + c1)·s + c2
        let t    = vmulq_f32(vmulq_f32(t, s), a); // (...)·s·a
        let poly = vaddq_f32(t, a);               // + a

        // Reflect: if |y| > |x|, result = π/2 - poly
        let fpi2     = vdupq_n_f32(core::f32::consts::FRAC_PI_2);
        let ay_gt_ax = vcgtq_f32(ay, ax);                 // uint32x4_t
        let reflected = vsubq_f32(fpi2, poly);
        // vbslq_f32(mask, a, b): select a where mask != 0, b elsewhere
        let r = vbslq_f32(ay_gt_ax, reflected, poly);

        // Quadrant: if x < 0, result = π - result
        let pi    = vdupq_n_f32(core::f32::consts::PI);
        let x_neg = vcltq_f32(x, zero);            // uint32x4_t
        let r = vbslq_f32(x_neg, vsubq_f32(pi, r), r);

        // Sign: negate lanes where y < 0 by XOR-ing the IEEE754 sign bit.
        // vandq_u32(mask, 0x8000_0000) isolates the sign bit per active lane.
        let y_neg    = vcltq_f32(y, zero);          // uint32x4_t
        let sign_bit = vandq_u32(y_neg, vdupq_n_u32(0x8000_0000_u32));
        vreinterpretq_f32_u32(
            veorq_u32(vreinterpretq_u32_f32(r), sign_bit)
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Scalar fallback (non-x86, non-aarch64 — WASM, armv7, etc.)
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(not(any(target_arch = "x86", target_arch = "x86_64")))]
#[inline(always)]
fn rsqrt_scalar_fallback(x: f32) -> f32 {
    let x2  = x * 0.5;
    let mut i: u32 = x.to_bits();
    i = 0x5f37_59df - (i >> 1);
    let r = f32::from_bits(i);
    r * (1.5_f32 - x2 * r * r)
}

// ─────────────────────────────────────────────────────────────────────────────
//  Platform-unified public API
// ─────────────────────────────────────────────────────────────────────────────

#[inline(always)]
pub fn fast_inv_sqrt(x: f32) -> f32 {
    if x <= 0.0 { return 0.0; }
    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    { unsafe { sse2::rsqrt_nr_ss(x) } }
    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64")))]
    { rsqrt_scalar_fallback(x) }
}

#[inline(always)]
pub fn fast_sqrt(x: f32) -> f32 {
    if x <= 0.0 { return 0.0; }
    x * fast_inv_sqrt(x)
}

#[allow(dead_code)]
#[inline(always)]
pub fn fast_length_2d(dx: f32, dy: f32) -> f32 {
    fast_sqrt(dx * dx + dy * dy)
}

#[allow(dead_code)]
#[inline(always)]
pub fn fast_length_3d(dx: f32, dy: f32, dz: f32) -> f32 {
    fast_sqrt(dx * dx + dy * dy + dz * dz)
}

/// Fast atan2 approximation — scalar. Max error ~0.005 rad (~0.3°).
/// Polynomial: mid-math acos_approx Horner structure + Nvidia Cg coefficients.
#[inline(always)]
pub fn fast_atan2(y: f32, x: f32) -> f32 {
    use core::f32::consts::{FRAC_PI_2, PI};

    let ax = x.abs();
    let ay = y.abs();

    let min_v = ax.min(ay);
    let max_v = ax.max(ay);
    let safe  = if max_v < 1e-10 { 1e-10_f32 } else { max_v };
    let a     = min_v / safe;

    let s = a * a;
    let r = ((-0.046_496_474_9_f32 * s + 0.159_314_22_f32) * s - 0.327_622_764_f32) * s * a + a;

    let r = if ay > ax     { FRAC_PI_2 - r } else { r };
    let r = if x  < 0.0   { PI - r }        else { r };
    if y < 0.0 { -r } else { r }
          }
