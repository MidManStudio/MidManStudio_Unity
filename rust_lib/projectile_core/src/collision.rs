// collision.rs — spatial grid broad-phase, circle/sphere narrow-phase
//
// 2D: O(P*k) spatial hash grid, 256 buckets, u32 keys.
// 3D: O(P*k) spatial hash grid, 512 buckets, u64 keys.
//
// SSE2 narrow phase (x86/x86_64):
//   Processes 4 candidate target-pairs simultaneously using rsqrt_nr.
//   Reduces narrow-phase cost by ~2-3x on batches where the grid bucket
//   delivers 4+ candidates.  With typical ≤4 targets/bucket the gain is
//   bounded but non-trivial at high projectile counts.
//
// Grid tuning:
//   cell_size = 0.0 → default (4.0).
//   Rule of thumb: cell_size ≈ 2× largest target radius.

use crate::{
    NativeProjectile,   CollisionTarget,   HitResult,
    NativeProjectile3D, CollisionTarget3D, HitResult3D,
};

// ─────────────────────────────────────────────────────────────────────────────
//  Shared constants
// ─────────────────────────────────────────────────────────────────────────────

const BUCKET_ENTRIES: usize = 8;

// ─────────────────────────────────────────────────────────────────────────────
//  2D spatial hash grid (unchanged structure, narrow phase upgraded)
// ─────────────────────────────────────────────────────────────────────────────

const GRID_BUCKETS_2D: usize = 256;
const EMPTY_2D: u32 = u32::MAX;

struct CellGrid2D {
    keys:    [u32; GRID_BUCKETS_2D],
    counts:  [u8;  GRID_BUCKETS_2D],
    entries: [[u8; BUCKET_ENTRIES]; GRID_BUCKETS_2D],
}

impl CellGrid2D {
    #[inline(always)]
    fn new() -> Self {
        let mut g = unsafe { core::mem::zeroed::<Self>() };
        for k in g.keys.iter_mut() { *k = EMPTY_2D; }
        g
    }

    #[inline(always)]
    fn pack(cx: i32, cy: i32) -> u32 {
        let x = cx.clamp(-32768, 32767) as u16 as u32;
        let y = cy.clamp(-32768, 32767) as u16 as u32;
        (x << 16) | y
    }

    #[inline(always)]
    fn hash(key: u32) -> usize {
        (key.wrapping_mul(0x9e37_79b9) as usize) & (GRID_BUCKETS_2D - 1)
    }

    fn insert(&mut self, cx: i32, cy: i32, target_idx: usize) {
        debug_assert!(target_idx < 255);
        if target_idx > 254 { return; }
        let key  = Self::pack(cx, cy);
        let mut slot = Self::hash(key);
        for _ in 0..GRID_BUCKETS_2D {
            if self.keys[slot] == EMPTY_2D {
                self.keys[slot]   = key;
                self.counts[slot] = 0;
            }
            if self.keys[slot] == key {
                let c = self.counts[slot] as usize;
                if c < BUCKET_ENTRIES {
                    self.entries[slot][c] = target_idx as u8;
                    self.counts[slot]     = (c + 1) as u8;
                }
                return;
            }
            slot = (slot + 1) & (GRID_BUCKETS_2D - 1);
        }
    }

    #[inline(always)]
    fn query(&self, cx: i32, cy: i32) -> &[u8] {
        let key  = Self::pack(cx, cy);
        let mut slot = Self::hash(key);
        for _ in 0..GRID_BUCKETS_2D {
            if self.keys[slot] == EMPTY_2D { return &[]; }
            if self.keys[slot] == key {
                return &self.entries[slot][..self.counts[slot] as usize];
            }
            slot = (slot + 1) & (GRID_BUCKETS_2D - 1);
        }
        &[]
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  2D narrow-phase helpers
// ─────────────────────────────────────────────────────────────────────────────

/// Scalar 2D circle overlap test. Returns true if projectile overlaps target.
#[inline(always)]
fn overlaps_2d(px: f32, py: f32, proj_r: f32, t: &CollisionTarget) -> bool {
    let dx = px - t.x;
    let dy = py - t.y;
    let r  = proj_r + t.radius;
    dx * dx + dy * dy <= r * r
}

/// SSE2 batch overlap test: checks up to 4 targets simultaneously.
/// Returns a bitmask (bit i set = target i overlaps the projectile).
/// Source: rsqrt_nr adapted from mid-math/src/wide/float/sse2/f32x4.rs.
///
/// Why not full rsqrt path here: we only need the comparison (dist ≤ combined),
/// so we stay in squared space entirely — no sqrt needed at all.
/// The 4-wide SIMD version does 4 comparisons in ~6 SSE2 instructions vs
/// 4 × 3 scalar instructions = ~12 scalar ops.
#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[inline]
unsafe fn overlaps_2d_x4(
    px: f32, py: f32, proj_r: f32,
    targets: &[CollisionTarget],  // exactly 4 elements
) -> u32 {
    #[cfg(target_arch = "x86")]
    use core::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use core::arch::x86_64::*;

    debug_assert_eq!(targets.len(), 4);

    let px4 = _mm_set1_ps(px);
    let py4 = _mm_set1_ps(py);
    let pr4 = _mm_set1_ps(proj_r);

    let tx  = _mm_set_ps(targets[3].x, targets[2].x, targets[1].x, targets[0].x);
    let ty  = _mm_set_ps(targets[3].y, targets[2].y, targets[1].y, targets[0].y);
    let tr  = _mm_set_ps(targets[3].radius, targets[2].radius, targets[1].radius, targets[0].radius);

    let dx  = _mm_sub_ps(px4, tx);
    let dy  = _mm_sub_ps(py4, ty);
    let r   = _mm_add_ps(pr4, tr);

    // dist² = dx² + dy²;  combined² = r²
    let d2  = _mm_add_ps(_mm_mul_ps(dx, dx), _mm_mul_ps(dy, dy));
    let r2  = _mm_mul_ps(r, r);

    // overlap where dist² ≤ combined²
    let mask = _mm_cmple_ps(d2, r2);
    _mm_movemask_ps(mask) as u32
}

// ─────────────────────────────────────────────────────────────────────────────
//  2D collision check — public entry point
// ─────────────────────────────────────────────────────────────────────────────

pub fn check_hits(
    projs:     &[NativeProjectile],
    targets:   &[CollisionTarget],
    out:       &mut [HitResult],
    cell_size: f32,
) -> usize {
    let cell     = if cell_size > 0.0 { cell_size } else { 4.0 };
    let inv      = 1.0 / cell;
    let max_hits = out.len();

    if targets.is_empty() || projs.is_empty() || max_hits == 0 {
        return 0;
    }

    // Phase 1: insert active targets into grid
    let mut grid = CellGrid2D::new();
    for (ti, t) in targets.iter().enumerate() {
        if t.active == 0 { continue; }
        let min_cx = ((t.x - t.radius) * inv).floor() as i32;
        let max_cx = ((t.x + t.radius) * inv).floor() as i32;
        let min_cy = ((t.y - t.radius) * inv).floor() as i32;
        let max_cy = ((t.y + t.radius) * inv).floor() as i32;
        for cx in min_cx..=max_cx {
            for cy in min_cy..=max_cy {
                grid.insert(cx, cy, ti);
            }
        }
    }

    // Phase 2: query each alive projectile
    let mut hit_count = 0usize;
    'proj: for (pi, p) in projs.iter().enumerate() {
        if p.alive == 0          { continue; }
        if hit_count >= max_hits { break;    }

        let proj_r = p.scale_x * 0.5;
        let min_cx = ((p.x - proj_r) * inv).floor() as i32;
        let max_cx = ((p.x + proj_r) * inv).floor() as i32;
        let min_cy = ((p.y - proj_r) * inv).floor() as i32;
        let max_cy = ((p.y + proj_r) * inv).floor() as i32;

        for cx in min_cx..=max_cx {
            for cy in min_cy..=max_cy {
                let candidates = grid.query(cx, cy);
                let mut k = 0;

                // ── SSE2 batch: 4 candidates at a time ────────────────────
                #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
                {
                    while k + 4 <= candidates.len() {
                        // Gather 4 target structs
                        let t0 = &targets[candidates[k + 0] as usize];
                        let t1 = &targets[candidates[k + 1] as usize];
                        let t2 = &targets[candidates[k + 2] as usize];
                        let t3 = &targets[candidates[k + 3] as usize];

                        // All must be active for the SIMD path to be valid
                        if t0.active & t1.active & t2.active & t3.active != 0 {
                            let ts = [*t0, *t1, *t2, *t3];
                            let mask = unsafe { overlaps_2d_x4(p.x, p.y, proj_r, &ts) };
                            if mask != 0 {
                                // First hit: trailing zeros → lowest set bit index
                                let lane = mask.trailing_zeros() as usize;
                                let t = &targets[candidates[k + lane] as usize];
                                out[hit_count] = HitResult {
                                    proj_id:     p.proj_id,
                                    proj_index:  pi as u32,
                                    target_id:   t.target_id,
                                    travel_dist: p.travel_dist,
                                    hit_x:       p.x,
                                    hit_y:       p.y,
                                };
                                hit_count += 1;
                                continue 'proj;
                            }
                        } else {
                            // Mixed active/inactive — scalar fallback for this batch
                            for off in 0..4 {
                                let t = &targets[candidates[k + off] as usize];
                                if t.active == 0 { continue; }
                                if overlaps_2d(p.x, p.y, proj_r, t) {
                                    out[hit_count] = HitResult {
                                        proj_id:     p.proj_id,
                                        proj_index:  pi as u32,
                                        target_id:   t.target_id,
                                        travel_dist: p.travel_dist,
                                        hit_x:       p.x,
                                        hit_y:       p.y,
                                    };
                                    hit_count += 1;
                                    continue 'proj;
                                }
                            }
                        }
                        k += 4;
                    }
                }

                // ── Scalar remainder ──────────────────────────────────────
                while k < candidates.len() {
                    let t = &targets[candidates[k] as usize];
                    k += 1;
                    if t.active == 0 { continue; }
                    if overlaps_2d(p.x, p.y, proj_r, t) {
                        out[hit_count] = HitResult {
                            proj_id:     p.proj_id,
                            proj_index:  pi as u32,
                            target_id:   t.target_id,
                            travel_dist: p.travel_dist,
                            hit_x:       p.x,
                            hit_y:       p.y,
                        };
                        hit_count += 1;
                        continue 'proj;
                    }
                }
            }
        }
    }
    hit_count
}

// ─────────────────────────────────────────────────────────────────────────────
//  3D spatial hash grid
// ─────────────────────────────────────────────────────────────────────────────

const GRID_BUCKETS_3D: usize = 512;
const EMPTY_3D: u64 = u64::MAX;

struct CellGrid3D {
    keys:    [u64; GRID_BUCKETS_3D],
    counts:  [u8;  GRID_BUCKETS_3D],
    entries: [[u8; BUCKET_ENTRIES]; GRID_BUCKETS_3D],
}

impl CellGrid3D {
    #[inline(always)]
    fn new() -> Self {
        let mut g = unsafe { core::mem::zeroed::<Self>() };
        for k in g.keys.iter_mut() { *k = EMPTY_3D; }
        g
    }

    #[inline(always)]
    fn pack(cx: i32, cy: i32, cz: i32) -> u64 {
        let x = cx.clamp(-32768, 32767) as u16 as u64;
        let y = cy.clamp(-32768, 32767) as u16 as u64;
        let z = cz.clamp(-32768, 32767) as u16 as u64;
        (x << 32) | (y << 16) | z
    }

    #[inline(always)]
    fn hash(key: u64) -> usize {
        let k32 = ((key >> 32) as u32) ^ (key as u32);
        (k32.wrapping_mul(0x9e37_79b9) as usize) & (GRID_BUCKETS_3D - 1)
    }

    fn insert(&mut self, cx: i32, cy: i32, cz: i32, target_idx: usize) {
        debug_assert!(target_idx < 255);
        if target_idx > 254 { return; }
        let key  = Self::pack(cx, cy, cz);
        let mut slot = Self::hash(key);
        for _ in 0..GRID_BUCKETS_3D {
            if self.keys[slot] == EMPTY_3D {
                self.keys[slot]   = key;
                self.counts[slot] = 0;
            }
            if self.keys[slot] == key {
                let c = self.counts[slot] as usize;
                if c < BUCKET_ENTRIES {
                    self.entries[slot][c] = target_idx as u8;
                    self.counts[slot]     = (c + 1) as u8;
                }
                return;
            }
            slot = (slot + 1) & (GRID_BUCKETS_3D - 1);
        }
    }

    #[inline(always)]
    fn query(&self, cx: i32, cy: i32, cz: i32) -> &[u8] {
        let key  = Self::pack(cx, cy, cz);
        let mut slot = Self::hash(key);
        for _ in 0..GRID_BUCKETS_3D {
            if self.keys[slot] == EMPTY_3D { return &[]; }
            if self.keys[slot] == key {
                return &self.entries[slot][..self.counts[slot] as usize];
            }
            slot = (slot + 1) & (GRID_BUCKETS_3D - 1);
        }
        &[]
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  3D narrow-phase helpers
// ─────────────────────────────────────────────────────────────────────────────

#[inline(always)]
fn overlaps_3d(px: f32, py: f32, pz: f32, proj_r: f32, t: &CollisionTarget3D) -> bool {
    let dx = px - t.x;
    let dy = py - t.y;
    let dz = pz - t.z;
    let r  = proj_r + t.radius;
    dx*dx + dy*dy + dz*dz <= r*r
}

/// SSE2 batch 3D overlap: 4 targets simultaneously using squared-distance comparison.
/// Loads x/y/z from 4 targets — 12 scalar loads — runs 3 add+mul lanes in parallel.
#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[inline]
unsafe fn overlaps_3d_x4(
    px: f32, py: f32, pz: f32, proj_r: f32,
    targets: &[CollisionTarget3D], // exactly 4 elements
) -> u32 {
    #[cfg(target_arch = "x86")]
    use core::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use core::arch::x86_64::*;

    debug_assert_eq!(targets.len(), 4);

    let px4 = _mm_set1_ps(px);
    let py4 = _mm_set1_ps(py);
    let pz4 = _mm_set1_ps(pz);
    let pr4 = _mm_set1_ps(proj_r);

    let tx = _mm_set_ps(targets[3].x, targets[2].x, targets[1].x, targets[0].x);
    let ty = _mm_set_ps(targets[3].y, targets[2].y, targets[1].y, targets[0].y);
    let tz = _mm_set_ps(targets[3].z, targets[2].z, targets[1].z, targets[0].z);
    let tr = _mm_set_ps(targets[3].radius, targets[2].radius, targets[1].radius, targets[0].radius);

    let dx = _mm_sub_ps(px4, tx);
    let dy = _mm_sub_ps(py4, ty);
    let dz = _mm_sub_ps(pz4, tz);
    let r  = _mm_add_ps(pr4, tr);

    let d2 = _mm_add_ps(
        _mm_add_ps(_mm_mul_ps(dx, dx), _mm_mul_ps(dy, dy)),
        _mm_mul_ps(dz, dz),
    );
    let r2   = _mm_mul_ps(r, r);
    let mask = _mm_cmple_ps(d2, r2);
    _mm_movemask_ps(mask) as u32
}

// ─────────────────────────────────────────────────────────────────────────────
//  3D collision check — public entry point
// ─────────────────────────────────────────────────────────────────────────────

pub fn check_hits_3d(
    projs:     &[NativeProjectile3D],
    targets:   &[CollisionTarget3D],
    out:       &mut [HitResult3D],
    cell_size: f32,
) -> usize {
    let cell     = if cell_size > 0.0 { cell_size } else { 4.0 };
    let inv      = 1.0 / cell;
    let max_hits = out.len();

    if targets.is_empty() || projs.is_empty() || max_hits == 0 {
        return 0;
    }

    // Phase 1: insert active targets into 3D grid
    let mut grid = CellGrid3D::new();
    for (ti, t) in targets.iter().enumerate() {
        if t.active == 0 { continue; }
        let min_cx = ((t.x - t.radius) * inv).floor() as i32;
        let max_cx = ((t.x + t.radius) * inv).floor() as i32;
        let min_cy = ((t.y - t.radius) * inv).floor() as i32;
        let max_cy = ((t.y + t.radius) * inv).floor() as i32;
        let min_cz = ((t.z - t.radius) * inv).floor() as i32;
        let max_cz = ((t.z + t.radius) * inv).floor() as i32;
        for cx in min_cx..=max_cx {
            for cy in min_cy..=max_cy {
                for cz in min_cz..=max_cz {
                    grid.insert(cx, cy, cz, ti);
                }
            }
        }
    }

    // Phase 2: query each alive projectile
    let mut hit_count = 0usize;
    'proj: for (pi, p) in projs.iter().enumerate() {
        if p.alive == 0          { continue; }
        if hit_count >= max_hits { break;    }

        let proj_r = p.scale_x * 0.5;
        let min_cx = ((p.x - proj_r) * inv).floor() as i32;
        let max_cx = ((p.x + proj_r) * inv).floor() as i32;
        let min_cy = ((p.y - proj_r) * inv).floor() as i32;
        let max_cy = ((p.y + proj_r) * inv).floor() as i32;
        let min_cz = ((p.z - proj_r) * inv).floor() as i32;
        let max_cz = ((p.z + proj_r) * inv).floor() as i32;

        for cx in min_cx..=max_cx {
            for cy in min_cy..=max_cy {
                for cz in min_cz..=max_cz {
                    let candidates = grid.query(cx, cy, cz);
                    let mut k = 0;

                    // ── SSE2 batch: 4 at a time ───────────────────────────
                    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
                    {
                        while k + 4 <= candidates.len() {
                            let t0 = &targets[candidates[k + 0] as usize];
                            let t1 = &targets[candidates[k + 1] as usize];
                            let t2 = &targets[candidates[k + 2] as usize];
                            let t3 = &targets[candidates[k + 3] as usize];

                            if t0.active & t1.active & t2.active & t3.active != 0 {
                                let ts = [*t0, *t1, *t2, *t3];
                                let mask = unsafe {
                                    overlaps_3d_x4(p.x, p.y, p.z, proj_r, &ts)
                                };
                                if mask != 0 {
                                    let lane = mask.trailing_zeros() as usize;
                                    let t    = &targets[candidates[k + lane] as usize];
                                    out[hit_count] = HitResult3D {
                                        proj_id:     p.proj_id,
                                        proj_index:  pi as u32,
                                        target_id:   t.target_id,
                                        travel_dist: p.travel_dist,
                                        hit_x:       p.x,
                                        hit_y:       p.y,
                                        hit_z:       p.z,
                                    };
                                    hit_count += 1;
                                    continue 'proj;
                                }
                            } else {
                                for off in 0..4 {
                                    let t = &targets[candidates[k + off] as usize];
                                    if t.active == 0 { continue; }
                                    if overlaps_3d(p.x, p.y, p.z, proj_r, t) {
                                        out[hit_count] = HitResult3D {
                                            proj_id:     p.proj_id,
                                            proj_index:  pi as u32,
                                            target_id:   t.target_id,
                                            travel_dist: p.travel_dist,
                                            hit_x:       p.x,
                                            hit_y:       p.y,
                                            hit_z:       p.z,
                                        };
                                        hit_count += 1;
                                        continue 'proj;
                                    }
                                }
                            }
                            k += 4;
                        }
                    }

                    // ── Scalar remainder ──────────────────────────────────
                    while k < candidates.len() {
                        let t = &targets[candidates[k] as usize];
                        k += 1;
                        if t.active == 0 { continue; }
                        if overlaps_3d(p.x, p.y, p.z, proj_r, t) {
                            out[hit_count] = HitResult3D {
                                proj_id:     p.proj_id,
                                proj_index:  pi as u32,
                                target_id:   t.target_id,
                                travel_dist: p.travel_dist,
                                hit_x:       p.x,
                                hit_y:       p.y,
                                hit_z:       p.z,
                            };
                            hit_count += 1;
                            continue 'proj;
                        }
                    }
                }
            }
        }
    }
    hit_count
}
