use criterion::{black_box, criterion_group, criterion_main, Criterion};
use projectile_core::{NativeProjectile, NativeProjectile3D};
use projectile_core::simulation::{tick_all, tick_all_3d, MOVE_STRAIGHT};

fn bench_2d_tick(c: &mut Criterion) {
    let count = 50_000;
    
    // Create 50k dummy 2D projectiles initialized for the straight movement hot path
    let mut projs: Vec<NativeProjectile> = (0..count).map(|_| {
        // Using zeroed as a fallback since we don't have a Default impl context,
        // but we ensure the vital fields for tick logic are populated.
        let mut p: NativeProjectile = unsafe { std::mem::zeroed() };
        p.alive = 1;
        p.movement_type = MOVE_STRAIGHT;
        p.lifetime = 10.0;
        p.vx = 10.0;
        p.vy = 5.0;
        p.ax = 0.0;
        p.ay = 0.0;
        p
    }).collect();

    c.bench_function("tick_all_2d_50k", |b| {
        b.iter(|| {
            // black_box prevents the compiler from optimizing the loop away
            tick_all(black_box(&mut projs), black_box(0.016));
        })
    });
}

fn bench_3d_tick(c: &mut Criterion) {
    let count = 50_000;
    
    // Create 50k dummy 3D projectiles initialized for the straight movement hot path
    let mut projs: Vec<NativeProjectile3D> = (0..count).map(|_| {
        let mut p: NativeProjectile3D = unsafe { std::mem::zeroed() };
        p.alive = 1;
        p.movement_type = MOVE_STRAIGHT;
        p.lifetime = 10.0;
        p.vx = 10.0;
        p.vy = 5.0;
        p.vz = 2.0;
        p.ax = 0.0;
        p.ay = 0.0;
        p.az = 0.0;
        p
    }).collect();

    c.bench_function("tick_all_3d_50k", |b| {
        b.iter(|| {
            tick_all_3d(black_box(&mut projs), black_box(0.016));
        })
    });
}

// Group the benchmarks and generate the main function
criterion_group!(benches, bench_2d_tick, bench_3d_tick);
criterion_main!(benches);
