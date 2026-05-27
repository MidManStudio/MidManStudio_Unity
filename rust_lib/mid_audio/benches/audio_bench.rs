//! mid_audio benchmarks — v2: peak limiter DSP
//!
//! Groups:
//!   process_buffer  — limiter cost with various buffer sizes and input amplitudes
//!                     (0.5 = quiet → no limiting, 1.5 = loud → sustained limiting)
//!   params          — cost of set_limiter_params (main thread configuration call)
//!   gain_read       — cost of get_limiter_gain diagnostic read

use criterion::{black_box, criterion_group, criterion_main, BenchmarkId, Criterion, Throughput};
use mid_audio::{process_buffer, set_limiter_params, set_limiter_enabled, reset_limiter, get_limiter_gain};

// ─────────────────────────────────────────────────────────────────────────────
//  Helpers
// ─────────────────────────────────────────────────────────────────────────────

/// Fill buffer with a sine wave at a given amplitude (1.0 = 0 dBFS).
fn make_signal(samples: usize, amplitude: f32) -> Vec<f32> {
    (0..samples)
        .map(|i| (i as f32 * 440.0 * std::f32::consts::TAU / 48000.0).sin() * amplitude)
        .collect()
}

// ─────────────────────────────────────────────────────────────────────────────
//  Group 1: process_buffer
//
//  Benchmarks three scenarios:
//    quiet   — amplitude 0.5  → peak 0.5 < threshold 0.95 → recovery path
//    nominal — amplitude 0.9  → peak 0.9 < threshold 0.95 → mostly recovery
//    loud    — amplitude 1.5  → peak 1.5 > threshold 0.95 → attack (limiting) path
//
//  Buffer sizes: 256, 512, 1024 (Unity DSP buffer at 48kHz)
//  Throughput = samples processed per second.
// ─────────────────────────────────────────────────────────────────────────────

fn bench_process_buffer(c: &mut Criterion) {
    let mut group = c.benchmark_group("process_buffer");

    // Default limiter params
    unsafe { set_limiter_params(0.95, 0.85, 0.002); }

    for &buf_size in &[256usize, 512, 1024] {
        group.throughput(Throughput::Elements(buf_size as u64));

        for &(label, amplitude) in &[("quiet", 0.5f32), ("nominal", 0.9), ("loud", 1.5)] {
            let bench_label = format!("{buf_size}samp_{label}");
            let signal_template = make_signal(buf_size, amplitude);

            group.bench_with_input(
                BenchmarkId::new("mix", &bench_label),
                &(buf_size, amplitude),
                |b, _| {
                    b.iter_custom(|iters| {
                        let mut total = std::time::Duration::ZERO;

                        for _ in 0..iters {
                            // Reset gain so each iter starts from the same state
                            unsafe { reset_limiter(); }
                            let mut buf = signal_template.clone();

                            let start = std::time::Instant::now();
                            unsafe { process_buffer(black_box(buf.as_mut_ptr()), black_box(buf.len() as i32)); }
                            total += start.elapsed();

                            black_box(&buf);
                        }
                        total
                    });
                },
            );
        }
    }

    group.finish();
}

// ─────────────────────────────────────────────────────────────────────────────
//  Group 2: params
//  Cost of set_limiter_params (atomic stores — should be ~10-20 ns)
// ─────────────────────────────────────────────────────────────────────────────

fn bench_params(c: &mut Criterion) {
    let mut group = c.benchmark_group("params");

    group.bench_function("set_limiter_params", |b| {
        b.iter(|| unsafe {
            set_limiter_params(
                black_box(0.95),
                black_box(0.85),
                black_box(0.002),
            );
        });
    });

    group.bench_function("get_limiter_gain", |b| {
        b.iter(|| black_box(unsafe { get_limiter_gain() }));
    });

    group.finish();
}

// ─────────────────────────────────────────────────────────────────────────────
//  Group 3: enable_disable
//  Cost of toggling limiter — verify it's free enough for real-time use
// ─────────────────────────────────────────────────────────────────────────────

fn bench_enable(c: &mut Criterion) {
    let mut group = c.benchmark_group("enable_disable");

    let mut buf = make_signal(512, 1.0);

    group.bench_function("disabled_passthrough", |b| {
        b.iter(|| {
            unsafe {
                set_limiter_enabled(0); // disabled = pass-through
                process_buffer(black_box(buf.as_mut_ptr()), black_box(buf.len() as i32));
                set_limiter_enabled(1);
            }
            black_box(&buf);
        });
    });

    group.finish();
}

// ─────────────────────────────────────────────────────────────────────────────
//  Harness
// ─────────────────────────────────────────────────────────────────────────────

criterion_group!(benches, bench_process_buffer, bench_params, bench_enable);
criterion_main!(benches);
