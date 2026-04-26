// collision.rs — spatial grid broad-phase, circle/sphere narrow-phase
//
// 2D: Unchanged O(P*k) spatial hash grid with u32 keys, 256 buckets.
// 3D: New   O(P*k) spatial hash grid with u64 keys, 512 buckets.
//
// Both grids are stack-allocated structs — no heap allocation in the hot path.
// The 3D grid is larger (~8.7 KB) vs 2D (~6 KB) due to u64 keys and more buckets.
//
// Grid tuning (applies to both 2D and 3D):
//   cell_size = 0.0 → use default (4.0).
//   Rule of thumb: cell_size ≈ 2× largest target radius.
//   Too small → many inserts per target.
//   Too large → many narrow-phase checks per cell.

use crate::{
    NativeProjectile,   CollisionTarget,   HitResult,
    NativeProjectile3D, CollisionTarget3D, HitResult3D,
};

// ─────────────────────────────────────────────────────────────────────────────
//  Shared constants
// ─────────────────────────────────────────────────────────────────────────────

/// Max targets stored per grid bucket.
/// With ≤128 targets and 256 buckets this rarely exceeds 2 in practice.
const BUCKET_ENTRIES: usize = 8;

// ─────────────────────────────────────────────────────────────────────────────
//  2D spatial hash grid (unchanged)
// ─────────────────────────────────────────────────────────────────────────────

const GRID_BUCKETS_2D: usize = 256;   // power of 2
const EMPTY_2D: u32 = u32::MAX;

struct CellGrid2D {
    keys:    [u32;                          GRID_BUCKETS_2D],
    counts:  [u8;                           GRID_BUCKETS_2D],
    entries: [[u8; BUCKET_ENTRIES];         GRID_BUCKETS_2D],
}

impl CellGrid2D {
    #[inline(always)]
    fn new() -> Self {
        // Zeroed then fill EMPTY sentinel — avoids slow static-init memcpy.
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
                let c = self.counts[slot] as usize;
                return &self.entries[slot][..c];
            }
            slot = (slot + 1) & (GRID_BUCKETS_2D - 1);
        }
        &[]
    }
}

/// 2D spatial-grid projectile-target collision check.
/// `cell_size` = world units per grid cell. Pass 0.0 for default (4.0).
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
        if p.alive == 0              { continue; }
        if hit_count >= max_hits     { break;    }

        let proj_r = p.scale_x * 0.5;
        let min_cx = ((p.x - proj_r) * inv).floor() as i32;
        let max_cx = ((p.x + proj_r) * inv).floor() as i32;
        let min_cy = ((p.y - proj_r) * inv).floor() as i32;
        let max_cy = ((p.y + proj_r) * inv).floor() as i32;

        for cx in min_cx..=max_cx {
            for cy in min_cy..=max_cy {
                for &ti_u8 in grid.query(cx, cy) {
                    let t = unsafe { targets.get_unchecked(ti_u8 as usize) };
                    let dx = p.x - t.x;
                    let dy = p.y - t.y;
                    let combined = proj_r + t.radius;
                    if dx*dx + dy*dy <= combined*combined {
                        out[hit_count] = HitResult {
                            proj_id:     p.proj_id,
                            proj_index:  pi as u32,
                            target_id:   t.target_id,
                            travel_dist: p.travel_dist,
                            hit_x:       p.x,
                            hit_y:       p.y,
                        };
                        hit_count += 1;
                        continue 'proj; // one hit per projectile per tick
                    }
                }
            }
        }
    }
    hit_count
}

// ─────────────────────────────────────────────────────────────────────────────
//  3D spatial hash grid (new)
//
//  Key differences from 2D:
//    - u64 keys (pack cx, cy, cz each as i16 into 48 bits)
//    - 512 buckets (more spatial spread in 3D)
//    - 3D sphere-sphere narrow phase
//    - Target cells: iterate all (cx,cy,cz) cells that overlap the target sphere
//    - Projectile probe: same approach in 3D
// ─────────────────────────────────────────────────────────────────────────────

const GRID_BUCKETS_3D: usize = 512;   // power of 2
const EMPTY_3D: u64 = u64::MAX;

struct CellGrid3D {
    keys:    [u64;                          GRID_BUCKETS_3D],
    counts:  [u8;                           GRID_BUCKETS_3D],
    entries: [[u8; BUCKET_ENTRIES];         GRID_BUCKETS_3D],
}

impl CellGrid3D {
    #[inline(always)]
    fn new() -> Self {
        let mut g = unsafe { core::mem::zeroed::<Self>() };
        for k in g.keys.iter_mut() { *k = EMPTY_3D; }
        g
    }

    /// Pack (cx, cy, cz) — each clamped to i16 range — into a u64 key.
    /// Bits [47:32] = cx as u16, [31:16] = cy as u16, [15:0] = cz as u16.
    #[inline(always)]
    fn pack(cx: i32, cy: i32, cz: i32) -> u64 {
        let x = cx.clamp(-32768, 32767) as u16 as u64;
        let y = cy.clamp(-32768, 32767) as u16 as u64;
        let z = cz.clamp(-32768, 32767) as u16 as u64;
        (x << 32) | (y << 16) | z
    }

    /// Fibonacci hash of a u64: XOR fold to 32 bits then Fibonacci multiply.
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
                let c = self.counts[slot] as usize;
                return &self.entries[slot][..c];
            }
            slot = (slot + 1) & (GRID_BUCKETS_3D - 1);
        }
        &[]
    }
}

/// 3D spatial-grid projectile-target sphere collision check.
/// `cell_size` = world units per grid cell. Pass 0.0 for default (4.0).
///
/// Architecture is identical to check_hits() — Phase 1 inserts targets,
/// Phase 2 queries each alive projectile. One hit per projectile per tick.
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
    // A target with radius r and cell_size c occupies at most
    // ceil(2r/c)+1 cells per axis → worst case (r≥c): 8 cells (2×2×2 cube).
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
    // Typical 3D bullet radius is 0.05-0.15 world units → falls in one cell.
    let mut hit_count = 0usize;
    'proj: for (pi, p) in projs.iter().enumerate() {
        if p.alive == 0              { continue; }
        if hit_count >= max_hits     { break;    }

        // Use scale_x as the collision radius (scale_x = scale_y = scale_z for uniform scale).
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
                    for &ti_u8 in grid.query(cx, cy, cz) {
                        let t = unsafe { targets.get_unchecked(ti_u8 as usize) };
                        let dx = p.x - t.x;
                        let dy = p.y - t.y;
                        let dz = p.z - t.z;
                        let combined = proj_r + t.radius;
                        if dx*dx + dy*dy + dz*dz <= combined*combined {
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
                            continue 'proj; // one hit per projectile per tick
                        }
                    }
                }
            }
        }
    }
    hit_count
}
