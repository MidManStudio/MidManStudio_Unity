//! Projectile Core benchmarks — simulation tick + collision broad/narrow phase.
//!
//! Run:  cargo bench
//! HTML: criterion report in target/criterion/
//!
//! Note on imports: `simulation` and `collision` are private modules in lib.rs;
//! their public items are re-exported flat via `pub use simulation::*` and
//! `pub use collision::*`.  Bench files are separate crates and cannot
//! navigate private module paths — use `projectile_core::tick_all` etc.

use criterion::{black_box, criterion_group, criterion_main, BenchmarkId, Criterion, Throughput};
use projectile_core::{
    check_hits, check_hits_3d,
    CollisionTarget, CollisionTarget3D,
    HitResult, HitResult3D,
    NativeProjectile, NativeProjectile3D,
    tick_all, tick_all_3d,
    MOVE_STRAIGHT,
};

// ─────────────────────────────────────────────────────────────────────────────
//  Deterministic position generator (no rand dep)
// ─────────────────────────────────────────────────────────────────────────────

/// Minimal LCG — same constants as patterns.rs for reproducibility.
#[inline(always)]
fn lcg(seed: &mut u32) -> f32 {
    *seed = seed.wrapping_mul(1664525).wrapping_add(1013904223);
    (*seed >> 8) as f32 / 16_777_216.0
}

/// f32 in [0, range)
#[inline(always)]
fn rand_pos(seed: &mut u32, range: f32) -> f32 {
    lcg(seed) * range
}

// ─────────────────────────────────────────────────────────────────────────────
//  Simulation — 2D straight/arching (SSE2 x4 hot path on x86_64)
// ─────────────────────────────────────────────────────────────────────────────

fn bench_tick_2d(c: &mut Criterion) {
    let mut group = c.benchmark_group("tick_2d");

    for &count in &[10_000usize, 50_000usize] {
        group.throughput(Throughput::Elements(count as u64));

        let projs_template: Vec<NativeProjectile> = {
            let mut seed = 0xDEAD_BEEFu32;
            (0..count)
                .map(|_| {
                    let mut p: NativeProjectile = unsafe { std::mem::zeroed() };
                    p.alive = 1;
                    p.movement_type = MOVE_STRAIGHT;
                    p.lifetime = 10.0;
                    p.max_lifetime = 10.0;
                    p.x = rand_pos(&mut seed, 500.0);
                    p.y = rand_pos(&mut seed, 500.0);
                    p.vx = rand_pos(&mut seed, 20.0) - 10.0;
                    p.vy = rand_pos(&mut seed, 20.0) - 10.0;
                    p.ax = 0.0;
                    p.ay = -9.81 * 0.0; // straight only — no gravity on this path
                    p.scale_x = 1.0;
                    p.scale_y = 1.0;
                    p
                })
                .collect()
        };

        group.bench_with_input(BenchmarkId::from_parameter(count), &count, |b, _| {
            let mut projs = projs_template.clone();
            b.iter(|| tick_all(black_box(&mut projs), black_box(0.016)));
        });
    }

    group.finish();
}

// ─────────────────────────────────────────────────────────────────────────────
//  Simulation — 3D straight/arching (SSE2 x4 hot path on x86_64)
// ─────────────────────────────────────────────────────────────────────────────

fn bench_tick_3d(c: &mut Criterion) {
    let mut group = c.benchmark_group("tick_3d");

    for &count in &[10_000usize, 50_000usize] {
        group.throughput(Throughput::Elements(count as u64));

        let projs_template: Vec<NativeProjectile3D> = {
            let mut seed = 0xCAFE_BABEu32;
            (0..count)
                .map(|_| {
                    let mut p: NativeProjectile3D = unsafe { std::mem::zeroed() };
                    p.alive = 1;
                    p.movement_type = MOVE_STRAIGHT;
                    p.lifetime = 10.0;
                    p.max_lifetime = 10.0;
                    p.x = rand_pos(&mut seed, 500.0);
                    p.y = rand_pos(&mut seed, 500.0);
                    p.z = rand_pos(&mut seed, 500.0);
                    p.vx = rand_pos(&mut seed, 20.0) - 10.0;
                    p.vy = rand_pos(&mut seed, 20.0) - 10.0;
                    p.vz = rand_pos(&mut seed, 20.0) - 10.0;
                    p.scale_x = 1.0;
                    p.scale_y = 1.0;
                    p.scale_z = 1.0;
                    p
                })
                .collect()
        };

        group.bench_with_input(BenchmarkId::from_parameter(count), &count, |b, _| {
            let mut projs = projs_template.clone();
            b.iter(|| tick_all_3d(black_box(&mut projs), black_box(0.016)));
        });
    }

    group.finish();
}

// ─────────────────────────────────────────────────────────────────────────────
//  Collision — 2D spatial grid + SSE2 narrow phase
//
//  Scenario: projectiles scattered across a 200×200 world, targets clustered
//  into small groups (mimics enemies in a bullet-hell or tower-defence game).
//  ~5 % of projectiles should be inside a target cell to exercise the narrow
//  phase without making every bench call trivially fast (empty grid).
// ─────────────────────────────────────────────────────────────────────────────

fn bench_collision_2d(c: &mut Criterion) {
    let mut group = c.benchmark_group("collision_2d");

    // (proj_count, target_count) pairs
    let cases: &[(usize, usize)] = &[
        (10_000, 64),
        (10_000, 255), // max supported targets (u8 index, 255 = sentinel)
        (50_000, 64),
        (50_000, 255),
    ];

    for &(proj_count, target_count) in cases {
        group.throughput(Throughput::Elements(proj_count as u64));

        // Build targets — clustered in the centre of the world
        let targets: Vec<CollisionTarget> = {
            let mut seed = 0xABCD_1234u32;
            (0..target_count)
                .map(|_| CollisionTarget {
                    x:         50.0 + rand_pos(&mut seed, 100.0),
                    y:         50.0 + rand_pos(&mut seed, 100.0),
                    radius:    1.0,
                    target_id: seed,
                    active:    1,
                    _pad:      [0; 3],
                })
                .collect()
        };

        // Projectiles: most scattered widely, ~5 % aimed into the target cluster
        let projs: Vec<NativeProjectile> = {
            let mut seed = 0x1234_ABCDu32;
            (0..proj_count)
                .map(|i| {
                    let mut p: NativeProjectile = unsafe { std::mem::zeroed() };
                    p.alive = 1;
                    p.movement_type = MOVE_STRAIGHT;
                    p.lifetime = 5.0;
                    p.scale_x = 0.5;
                    // Every 20th projectile is inside the cluster, the rest spread wide
                    if i % 20 == 0 {
                        p.x = 50.0 + rand_pos(&mut seed, 100.0);
                        p.y = 50.0 + rand_pos(&mut seed, 100.0);
                    } else {
                        p.x = rand_pos(&mut seed, 200.0);
                        p.y = rand_pos(&mut seed, 200.0);
                    }
                    p.vx = 10.0;
                    p.vy = 0.0;
                    p
                })
                .collect()
        };

        let mut hits = vec![HitResult::default(); proj_count.min(512)];

        let label = format!("{}p_{}t", proj_count, target_count);
        group.bench_function(&label, |b| {
            b.iter(|| {
                check_hits(
                    black_box(&projs),
                    black_box(&targets),
                    black_box(&mut hits),
                    black_box(2.0), // cell_size
                )
            })
        });
    }

    group.finish();
}

// ─────────────────────────────────────────────────────────────────────────────
//  Collision — 3D spatial grid + SSE2 narrow phase
// ─────────────────────────────────────────────────────────────────────────────

fn bench_collision_3d(c: &mut Criterion) {
    let mut group = c.benchmark_group("collision_3d");

    let cases: &[(usize, usize)] = &[
        (10_000, 64),
        (10_000, 255),
        (50_000, 64),
        (50_000, 255),
    ];

    for &(proj_count, target_count) in cases {
        group.throughput(Throughput::Elements(proj_count as u64));

        let targets: Vec<CollisionTarget3D> = {
            let mut seed = 0xFEED_F00Du32;
            (0..target_count)
                .map(|_| CollisionTarget3D {
                    x:         50.0 + rand_pos(&mut seed, 100.0),
                    y:         50.0 + rand_pos(&mut seed, 100.0),
                    z:         50.0 + rand_pos(&mut seed, 100.0),
                    radius:    1.0,
                    target_id: seed,
                    active:    1,
                    _pad:      [0; 3],
                })
                .collect()
        };

        let projs: Vec<NativeProjectile3D> = {
            let mut seed = 0xBEEF_F00Du32;
            (0..proj_count)
                .map(|i| {
                    let mut p: NativeProjectile3D = unsafe { std::mem::zeroed() };
                    p.alive = 1;
                    p.movement_type = MOVE_STRAIGHT;
                    p.lifetime = 5.0;
                    p.scale_x = 0.5;
                    if i % 20 == 0 {
                        p.x = 50.0 + rand_pos(&mut seed, 100.0);
                        p.y = 50.0 + rand_pos(&mut seed, 100.0);
                        p.z = 50.0 + rand_pos(&mut seed, 100.0);
                    } else {
                        p.x = rand_pos(&mut seed, 200.0);
                        p.y = rand_pos(&mut seed, 200.0);
                        p.z = rand_pos(&mut seed, 200.0);
                    }
                    p.vx = 10.0;
                    p
                })
                .collect()
        };

        let mut hits = vec![HitResult3D::default(); proj_count.min(512)];

        let label = format!("{}p_{}t", proj_count, target_count);
        group.bench_function(&label, |b| {
            b.iter(|| {
                check_hits_3d(
                    black_box(&projs),
                    black_box(&targets),
                    black_box(&mut hits),
                    black_box(2.0),
                )
            })
        });
    }

    group.finish();
}

// ─────────────────────────────────────────────────────────────────────────────
//  Criterion harness
// ─────────────────────────────────────────────────────────────────────────────

criterion_group!(
    benches,
    bench_tick_2d,
    bench_tick_3d,
    bench_collision_2d,
    bench_collision_3d,
);
criterion_main!(benches);
