// math/mod.rs
// Self-contained SIMD math library extracted for projectile_core.
// No external dependencies — everything compiles from crate::simd intrinsics.
//
// Module structure:
//   f32x4   — 4-wide f32 type: SSE2 | NEON | scalar fallback
//   Vec2x4  — 4-wide 2D vector (SoA): wraps two f32x4 (x, y)
//   Vec3x4  — 4-wide 3D vector (SoA): wraps three f32x4 (x, y, z)
//
// Platform dispatch is fully compile-time inside f32x4 — Vec2x4 and Vec3x4
// are platform-independent and use only f32x4 operations.
//
// ── Import guide ─────────────────────────────────────────────────────────────
//
// In simulation.rs (outside the math subtree):
//   use crate::math::f32x4::f32x4;   // ← the TYPE, via its submodule path
//   use crate::math::{Vec2x4, Vec3x4};
//
// In vec2x4.rs / vec3x4.rs (inside the math subtree):
//   use crate::math::f32x4::{f32x4, Mask4};
//
// WHY no `pub use f32x4::{f32x4, Mask4}` here:
//   `pub mod f32x4` places the MODULE named `f32x4` in this module's type
//   namespace.  A `pub use f32x4::f32x4` would place the TYPE also named
//   `f32x4` in the same namespace → E0255 "defined multiple times".
//   Vec2x4 / Vec3x4 are fine because the type names differ from the module
//   names (vec2x4 vs Vec2x4, vec3x4 vs Vec3x4).

pub mod f32x4;
pub mod vec2x4;
pub mod vec3x4;

pub use vec2x4::Vec2x4;
pub use vec3x4::Vec3x4;
