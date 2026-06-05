// math/mod.rs
// Self-contained SIMD math library extracted for projectile_core.
// No external dependencies — everything compiles from crate::simd intrinsics.
//
// Module structure:
//   f32x4   — 4-wide f32 scalar type: SSE2 | NEON | scalar fallback
//   Vec2x4  — 4-wide 2D vector (SoA): wraps two f32x4 (x, y)
//   Vec3x4  — 4-wide 3D vector (SoA): wraps three f32x4 (x, y, z)
//
// Platform dispatch is fully compile-time inside f32x4 — Vec2x4 and Vec3x4
// are platform-independent and use only f32x4 operations.
//
// Usage in simulation.rs:
//   use crate::math::{f32x4, Vec2x4, Vec3x4};
//
// Usage in collision.rs:
//   use crate::math::f32x4; // for the 4-wide overlap tests

pub mod f32x4;
pub mod vec2x4;
pub mod vec3x4;

pub use f32x4::{f32x4, Mask4};
pub use vec2x4::Vec2x4;
pub use vec3x4::Vec3x4;
