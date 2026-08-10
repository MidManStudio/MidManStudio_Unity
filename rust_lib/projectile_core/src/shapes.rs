// shapes.rs — additive point-sequence collider shapes (Box, Capsule, Edge,
// Polygon/custom curve) for RustSim's impact detector.
//
// WHY THIS IS A SEPARATE MODULE, NOT A CHANGE TO CollisionTarget/
// CollisionTarget3D: those structs and collision.rs's SSE2 narrow phase are
// a tight, size-critical fast path (see collision.rs's own header — 4-wide
// SIMD gathers depend on CollisionTarget staying x/y/[z]/radius only).
// Growing them to carry a variable-length point buffer would blow that up
// for every circle/sphere target in the game, including the overwhelming
// majority that will never need anything more than a radius. This module
// is purely additive instead: a second, parallel target list, tested with
// its own pass, that never touches CollisionTarget/CollisionTarget3D or
// collision.rs's existing SIMD path at all.
//
// UNIFIED REPRESENTATION: every shape type here — Box, Capsule, Edge, and a
// hand-authored Polygon/custom curve — is really just "a sequence of up to
// MAX_SHAPE_POINTS points with a per-shape thickness":
//   - Box     = 4 corner points, closed = true,  thickness = 0 (or a small skin)
//   - Capsule = 2 endpoint points, closed = false, thickness = capsule radius
//   - Edge    = 2 raw points, closed = false, thickness = 0 (or a small skin)
//   - Polygon = N points (open or closed), typically baked client-side from
//               a CatmullRom/Bezier/Linear spline — the exact same three
//               interpolation modes ProjectilePatternSO already offers —
//               resampled down to this fixed point cap at authoring time.
// Collision is one test either way: closest-point-on-segment against every
// consecutive pair of points (wrapping last→first when closed), compared to
// (projectile radius + shape thickness). shape_type is carried purely as a
// descriptive tag for the C# side (auto-detection UX, debugging) — Rust
// never branches on it; every shape runs through the same segment test.
//
// hit_x/hit_y/[hit_z] here report the actual closest point ON THE SHAPE —
// more precise than collision.rs's circle/sphere path, which reports the
// projectile's own center (see that module's HitResult docs). Both are
// correct for their own shape kind; this one just happens to have an exact
// surface point available essentially for free from the same distance test.

use crate::{HitResult, HitResult3D, NativeProjectile, NativeProjectile3D};

pub const MAX_SHAPE_POINTS: usize = 8;

// Descriptive only — see module doc. Kept here (not just in C#) so the two
// sides can't silently drift on what each number means.
pub const SHAPE_BOX:     u8 = 0;
pub const SHAPE_CAPSULE: u8 = 1;
pub const SHAPE_EDGE:    u8 = 2;
pub const SHAPE_POLYGON: u8 = 3;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Vec2Raw {
    pub x: f32,
    pub y: f32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Vec3Raw {
    pub x: f32,
    pub y: f32,
    pub z: f32,
}

/// 2D point-sequence shape collider. 76 bytes. Must match C# ShapeCollider2D exactly.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct ShapeCollider2D {
    pub target_id:   u32,                          // 0
    pub shape_type:  u8,                            // 4  — descriptive only, see module doc
    pub point_count: u8,                            // 5  — valid range 2..=MAX_SHAPE_POINTS
    pub closed:      u8,                            // 6  — 1 = wrap last point back to first
    pub active:      u8,                            // 7
    pub thickness:   f32,                            // 8  — capsule/edge radius; 0 for a bare box/polygon edge
    pub points:      [Vec2Raw; MAX_SHAPE_POINTS],    // 12 — WORLD space, refreshed by the caller every tick a shape moves (same convention CollisionTarget already uses for x/y)
}

impl Default for ShapeCollider2D {
    fn default() -> Self {
        Self { target_id: 0, shape_type: 0, point_count: 0, closed: 0, active: 0,
               thickness: 0.0, points: [Vec2Raw::default(); MAX_SHAPE_POINTS] }
    }
}

/// 3D point-sequence shape collider. 108 bytes. Must match C# ShapeCollider3D exactly.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct ShapeCollider3D {
    pub target_id:   u32,                          // 0
    pub shape_type:  u8,                            // 4
    pub point_count: u8,                            // 5
    pub closed:      u8,                            // 6
    pub active:      u8,                            // 7
    pub thickness:   f32,                            // 8
    pub points:      [Vec3Raw; MAX_SHAPE_POINTS],    // 12 — WORLD space
}

impl Default for ShapeCollider3D {
    fn default() -> Self {
        Self { target_id: 0, shape_type: 0, point_count: 0, closed: 0, active: 0,
               thickness: 0.0, points: [Vec3Raw::default(); MAX_SHAPE_POINTS] }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  2D narrow-phase
// ─────────────────────────────────────────────────────────────────────────────

#[inline(always)]
fn closest_point_on_segment_2d(px: f32, py: f32, ax: f32, ay: f32, bx: f32, by: f32) -> (f32, f32) {
    let abx = bx - ax;
    let aby = by - ay;
    let len2 = abx * abx + aby * aby;
    if len2 < 1e-12 {
        return (ax, ay); // degenerate (duplicate) point — treat as a single point
    }
    let t = (((px - ax) * abx) + ((py - ay) * aby)) / len2;
    let t = t.clamp(0.0, 1.0);
    (ax + abx * t, ay + aby * t)
}

/// Loose AABB over a shape's current points, expanded by thickness — a cheap
/// reject before paying for the full segment-distance loop. Recomputed fresh
/// each call rather than cached, since points are already being refreshed by
/// the caller every tick for anything that moves (same as CollisionTarget).
#[inline(always)]
fn shape_aabb_2d(s: &ShapeCollider2D) -> (f32, f32, f32, f32) {
    let n = (s.point_count as usize).min(MAX_SHAPE_POINTS).max(1);
    let mut min_x = s.points[0].x; let mut max_x = s.points[0].x;
    let mut min_y = s.points[0].y; let mut max_y = s.points[0].y;
    for p in &s.points[1..n] {
        if p.x < min_x { min_x = p.x } if p.x > max_x { max_x = p.x }
        if p.y < min_y { min_y = p.y } if p.y > max_y { max_y = p.y }
    }
    (min_x - s.thickness, min_y - s.thickness, max_x + s.thickness, max_y + s.thickness)
}

/// Appends shape-collider hits starting at out[hit_offset..]. Returns the new
/// TOTAL populated count (hit_offset + hits written this call) — same
/// "populated up to this index" convention check_hits_grid_ex's out_hit_count
/// uses, so callers append by passing their existing circle-pass count
/// straight through as hit_offset and use the return value as their new total,
/// no extra arithmetic needed.
pub fn check_hits_shapes_2d(
    projs:      &[NativeProjectile],
    shapes:     &[ShapeCollider2D],
    out:        &mut [HitResult],
    hit_offset: usize,
) -> usize {
    let max_hits = out.len();
    let mut hit_count = hit_offset.min(max_hits);

    if shapes.is_empty() || projs.is_empty() || hit_count >= max_hits {
        return hit_count;
    }

    'proj: for (pi, p) in projs.iter().enumerate() {
        if p.alive == 0          { continue; }
        if hit_count >= max_hits { break;    }

        let proj_r = p.scale_x * 0.5;

        for s in shapes.iter() {
            if s.active == 0 { continue; }
            let n = (s.point_count as usize).min(MAX_SHAPE_POINTS);
            if n < 2 { continue; }

            let r = proj_r + s.thickness;
            let (min_x, min_y, max_x, max_y) = shape_aabb_2d(s);
            if p.x < min_x - r || p.x > max_x + r || p.y < min_y - r || p.y > max_y + r {
                continue; // AABB reject — cheaper than the segment loop below
            }

            let seg_count = if s.closed != 0 { n } else { n - 1 };
            let mut best_d2 = f32::MAX;
            let mut best_x  = 0.0f32;
            let mut best_y  = 0.0f32;

            for i in 0..seg_count {
                let a = s.points[i];
                let b = s.points[(i + 1) % n];
                let (cx, cy) = closest_point_on_segment_2d(p.x, p.y, a.x, a.y, b.x, b.y);
                let dx = p.x - cx;
                let dy = p.y - cy;
                let d2 = dx * dx + dy * dy;
                if d2 < best_d2 { best_d2 = d2; best_x = cx; best_y = cy; }
            }

            if best_d2 <= r * r {
                out[hit_count] = HitResult {
                    proj_id:     p.proj_id,
                    proj_index:  pi as u32,
                    target_id:   s.target_id,
                    travel_dist: p.travel_dist,
                    hit_x:       best_x,
                    hit_y:       best_y,
                };
                hit_count += 1;
                continue 'proj;
            }
        }
    }

    hit_count
}

// ─────────────────────────────────────────────────────────────────────────────
//  3D narrow-phase
// ─────────────────────────────────────────────────────────────────────────────

#[inline(always)]
fn closest_point_on_segment_3d(
    px: f32, py: f32, pz: f32,
    ax: f32, ay: f32, az: f32,
    bx: f32, by: f32, bz: f32,
) -> (f32, f32, f32) {
    let abx = bx - ax; let aby = by - ay; let abz = bz - az;
    let len2 = abx * abx + aby * aby + abz * abz;
    if len2 < 1e-12 {
        return (ax, ay, az);
    }
    let t = (((px - ax) * abx) + ((py - ay) * aby) + ((pz - az) * abz)) / len2;
    let t = t.clamp(0.0, 1.0);
    (ax + abx * t, ay + aby * t, az + abz * t)
}

#[inline(always)]
fn shape_aabb_3d(s: &ShapeCollider3D) -> (f32, f32, f32, f32, f32, f32) {
    let n = (s.point_count as usize).min(MAX_SHAPE_POINTS).max(1);
    let mut min_x = s.points[0].x; let mut max_x = s.points[0].x;
    let mut min_y = s.points[0].y; let mut max_y = s.points[0].y;
    let mut min_z = s.points[0].z; let mut max_z = s.points[0].z;
    for p in &s.points[1..n] {
        if p.x < min_x { min_x = p.x } if p.x > max_x { max_x = p.x }
        if p.y < min_y { min_y = p.y } if p.y > max_y { max_y = p.y }
        if p.z < min_z { min_z = p.z } if p.z > max_z { max_z = p.z }
    }
    (min_x - s.thickness, min_y - s.thickness, min_z - s.thickness,
     max_x + s.thickness, max_y + s.thickness, max_z + s.thickness)
}

pub fn check_hits_shapes_3d(
    projs:      &[NativeProjectile3D],
    shapes:     &[ShapeCollider3D],
    out:        &mut [HitResult3D],
    hit_offset: usize,
) -> usize {
    let max_hits = out.len();
    let mut hit_count = hit_offset.min(max_hits);

    if shapes.is_empty() || projs.is_empty() || hit_count >= max_hits {
        return hit_count;
    }

    'proj: for (pi, p) in projs.iter().enumerate() {
        if p.alive == 0          { continue; }
        if hit_count >= max_hits { break;    }

        let proj_r = p.scale_x * 0.5;

        for s in shapes.iter() {
            if s.active == 0 { continue; }
            let n = (s.point_count as usize).min(MAX_SHAPE_POINTS);
            if n < 2 { continue; }

            let r = proj_r + s.thickness;
            let (min_x, min_y, min_z, max_x, max_y, max_z) = shape_aabb_3d(s);
            if p.x < min_x - r || p.x > max_x + r
                || p.y < min_y - r || p.y > max_y + r
                || p.z < min_z - r || p.z > max_z + r {
                continue;
            }

            let seg_count = if s.closed != 0 { n } else { n - 1 };
            let mut best_d2 = f32::MAX;
            let mut best_x  = 0.0f32;
            let mut best_y  = 0.0f32;
            let mut best_z  = 0.0f32;

            for i in 0..seg_count {
                let a = s.points[i];
                let b = s.points[(i + 1) % n];
                let (cx, cy, cz) = closest_point_on_segment_3d(
                    p.x, p.y, p.z, a.x, a.y, a.z, b.x, b.y, b.z);
                let dx = p.x - cx; let dy = p.y - cy; let dz = p.z - cz;
                let d2 = dx * dx + dy * dy + dz * dz;
                if d2 < best_d2 { best_d2 = d2; best_x = cx; best_y = cy; best_z = cz; }
            }

            if best_d2 <= r * r {
                out[hit_count] = HitResult3D {
                    proj_id:     p.proj_id,
                    proj_index:  pi as u32,
                    target_id:   s.target_id,
                    travel_dist: p.travel_dist,
                    hit_x:       best_x,
                    hit_y:       best_y,
                    hit_z:       best_z,
                };
                hit_count += 1;
                continue 'proj;
            }
        }
    }

    hit_count
}

#[cfg(test)]
mod tests {
    use super::*;

    fn mk_proj(x: f32, y: f32, radius: f32) -> NativeProjectile {
        NativeProjectile { x, y, scale_x: radius * 2.0, alive: 1, ..Default::default() }
    }

    fn mk_edge(a: (f32, f32), b: (f32, f32), thickness: f32, target_id: u32) -> ShapeCollider2D {
        let mut s = ShapeCollider2D {
            target_id, shape_type: SHAPE_EDGE, point_count: 2, closed: 0, active: 1,
            thickness, points: Default::default(),
        };
        s.points[0] = Vec2Raw { x: a.0, y: a.1 };
        s.points[1] = Vec2Raw { x: b.0, y: b.1 };
        s
    }

    #[test]
    fn edge_hit_reports_closest_point_not_projectile_center() {
        let projs  = [mk_proj(5.0, 0.3, 0.5)];
        let shapes = [mk_edge((0.0, 0.0), (10.0, 0.0), 0.0, 42)];
        let mut hits = [HitResult::default(); 4];

        let n = check_hits_shapes_2d(&projs, &shapes, &mut hits, 0);
        assert_eq!(n, 1);
        assert_eq!(hits[0].target_id, 42);
        // Closest point on the segment y=0 from (5,0.3) is (5,0) — not the
        // projectile's own (5,0.3) center, unlike the circle/sphere hit path.
        assert!((hits[0].hit_x - 5.0).abs() < 1e-4);
        assert!((hits[0].hit_y - 0.0).abs() < 1e-4);
    }

    #[test]
    fn edge_miss_outside_radius() {
        let projs  = [mk_proj(5.0, 5.0, 0.5)];
        let shapes = [mk_edge((0.0, 0.0), (10.0, 0.0), 0.0, 42)];
        let mut hits = [HitResult::default(); 4];
        assert_eq!(check_hits_shapes_2d(&projs, &shapes, &mut hits, 0), 0);
    }

    #[test]
    fn closed_box_wraps_last_segment_back_to_first() {
        // 2x2 box centered at origin, 4 corners, closed. Projectile sits just
        // outside the right edge — only detectable if the wrap segment
        // (point[3] -> point[0]) AND every other edge is actually tested.
        let mut s = ShapeCollider2D {
            target_id: 7, shape_type: SHAPE_BOX, point_count: 4, closed: 1, active: 1,
            thickness: 0.0, points: Default::default(),
        };
        s.points[0] = Vec2Raw { x: -1.0, y: -1.0 };
        s.points[1] = Vec2Raw { x:  1.0, y: -1.0 };
        s.points[2] = Vec2Raw { x:  1.0, y:  1.0 };
        s.points[3] = Vec2Raw { x: -1.0, y:  1.0 };

        let projs = [mk_proj(1.2, 0.0, 0.5)];
        let mut hits = [HitResult::default(); 4];
        let n = check_hits_shapes_2d(&projs, &[s], &mut hits, 0);
        assert_eq!(n, 1);
        assert_eq!(hits[0].target_id, 7);
    }

    #[test]
    fn hit_offset_appends_without_clobbering_existing_hits() {
        let projs  = [mk_proj(5.0, 0.0, 0.5)];
        let shapes = [mk_edge((0.0, 0.0), (10.0, 0.0), 0.0, 99)];
        let mut hits = [HitResult::default(); 4];
        hits[0] = HitResult { target_id: 111, ..Default::default() };

        let total = check_hits_shapes_2d(&projs, &shapes, &mut hits, 1);
        assert_eq!(total, 2);
        assert_eq!(hits[0].target_id, 111); // untouched
        assert_eq!(hits[1].target_id, 99);  // appended after it
    }

    #[test]
    fn inactive_shape_never_hits() {
        let projs = [mk_proj(5.0, 0.0, 0.5)];
        let mut s = mk_edge((0.0, 0.0), (10.0, 0.0), 0.0, 42);
        s.active = 0;
        let mut hits = [HitResult::default(); 4];
        assert_eq!(check_hits_shapes_2d(&projs, &[s], &mut hits, 0), 0);
    }

    #[test]
    fn capsule_thickness_extends_hit_range() {
        let projs = [mk_proj(5.0, 2.0, 0.1)]; // small projectile, 2 units above the line
        let shapes = [mk_edge((0.0, 0.0), (10.0, 0.0), 2.0, 42)]; // thickness=2 → capsule-like
        let mut hits = [HitResult::default(); 4];
        assert_eq!(check_hits_shapes_2d(&projs, &shapes, &mut hits, 0), 1);
    }

    fn mk_proj_3d(x: f32, y: f32, z: f32, radius: f32) -> NativeProjectile3D {
        NativeProjectile3D { x, y, z, scale_x: radius * 2.0, alive: 1, ..Default::default() }
    }

    #[test]
    fn edge_3d_hit_reports_closest_point() {
        let mut s = ShapeCollider3D {
            target_id: 5, shape_type: SHAPE_EDGE, point_count: 2, closed: 0, active: 1,
            thickness: 0.0, points: Default::default(),
        };
        s.points[0] = Vec3Raw { x: 0.0, y: 0.0, z: 0.0 };
        s.points[1] = Vec3Raw { x: 10.0, y: 0.0, z: 0.0 };

        let projs = [mk_proj_3d(5.0, 0.3, 0.0, 0.5)];
        let mut hits = [HitResult3D::default(); 4];
        let n = check_hits_shapes_3d(&projs, &[s], &mut hits, 0);
        assert_eq!(n, 1);
        assert!((hits[0].hit_x - 5.0).abs() < 1e-4);
        assert!((hits[0].hit_y - 0.0).abs() < 1e-4);
        assert!((hits[0].hit_z - 0.0).abs() < 1e-4);
    }
}
