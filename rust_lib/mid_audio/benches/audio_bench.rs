//! mid_audio benchmarks — DSP mixing, voice scheduling, full-cycle cost.
//!
//! Run:  cargo bench
//! HTML: target/criterion/
//!
//! Benchmark groups:
//!   process_buffer  — DSP mixing with 0 / 4 / 8 / 16 active voices
//!                     across 256 / 512 / 1024 sample buffers.
//!                     Throughput = samples mixed per second.
//!   schedule_voice  — Main-thread enqueue cost with a free slot vs full
//!                     pool (voice stealing path).
//!   full_cycle      — Realistic: schedule N voices then immediately mix one
//!                     buffer. Models per-impact-frame cost in game code.
//!
//! Thread model note:
//!   In production, process_buffer runs on Unity's audio thread and
//!   schedule_voice runs on the main thread. Here both run on the bench
//!   thread, which is valid for isolated cost measurement since all shared
//!   state uses atomics — no data race can occur on a single thread.
//!
//! Global state between iterations:
//!   VOICES and PCM_BANK are module-level statics. Each bench group uses
//!   iter_custom() to reset and rebuild state outside the timed section,
//!   ensuring each measured call sees consistent input conditions.

use criterion::{
    black_box, criterion_group, criterion_main, BenchmarkId, Criterion, Throughput,
};
use mid_audio::{
    active_voice_count, process_buffer, reset_audio_state, schedule_voice, upload_pcm_clip,
};
use std::time::Duration;

// ─────────────────────────────────────────────────────────────────────────────
//  PCM fixture — a simple 440 Hz sine wave, 2 seconds at 48 kHz stereo.
//  Long enough that a single process_buffer call never exhausts the clip.
// ─────────────────────────────────────────────────────────────────────────────

fn make_sine_pcm(sample_count: usize) -> Vec<f32> {
    (0..sample_count)
        .map(|i| {
            let t = i as f32 / 48000.0;
            (t * 440.0 * std::f32::consts::TAU).sin() * 0.5
        })
        .collect()
}

/// Upload the sine fixture to the PCM bank.
/// Returns the bank slot index. Panics if upload fails.
fn upload_fixture(sample_count: usize) -> i32 {
    let pcm = make_sine_pcm(sample_count);
    let slot = unsafe { upload_pcm_clip(pcm.as_ptr(), pcm.len() as i32) };
    assert!(slot >= 0, "upload_pcm_clip failed — bank full or null pointer");
    slot
}

/// Schedule `voice_count` voices and activate them by running one buffer fill.
/// Called outside the timed section to set up a known voice state.
fn prime_voices(slot: i32, voice_count: usize, buf: &mut [f32]) {
    for _ in 0..voice_count {
        unsafe { schedule_voice(slot, 1.0); }
    }
    // process_buffer activates pending voices AND starts mixing.
    // We run it once here so all voices are in the active state
    // before the measured iteration begins.
    unsafe { process_buffer(buf.as_mut_ptr(), buf.len() as i32); }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Group 1: process_buffer — DSP mixing cost vs voice count and buffer size
// ─────────────────────────────────────────────────────────────────────────────

fn bench_process_buffer(c: &mut Criterion) {
    let mut group = c.benchmark_group("process_buffer");

    // Upload once — slot persists for the life of this process.
    // 2 seconds stereo at 48 kHz = 192 000 samples, ~750 KB.
    let slot = upload_fixture(192_000);

    let buf_sizes: &[usize]    = &[256, 512, 1024];
    let voice_counts: &[usize] = &[0, 4, 8, 16];

    for &buf_size in buf_sizes {
        for &vc in voice_counts {
            // Throughput label: samples mixed per call
            group.throughput(Throughput::Elements(buf_size as u64));

            let label = format!("{buf_size}s_{vc}v");
            let mut buf = vec![0.0f32; buf_size];

            group.bench_with_input(
                BenchmarkId::new("mix", &label),
                &(buf_size, vc),
                |b, &(_bs, voice_count)| {
                    b.iter_custom(|iters| {
                        let mut total = Duration::ZERO;
                        for _ in 0..iters {
                            // ── Setup (not timed) ─────────────────────────────
                            unsafe { reset_audio_state(); }
                            if voice_count > 0 {
                                prime_voices(slot, voice_count, &mut buf);
                            }

                            // ── Timed: one buffer fill ────────────────────────
                            let start = std::time::Instant::now();
                            unsafe {
                                process_buffer(
                                    black_box(buf.as_mut_ptr()),
                                    black_box(buf.len() as i32),
                                );
                            }
                            total += start.elapsed();

                            black_box(&buf); // prevent dead-code elimination
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
//  Group 2: schedule_voice — main-thread enqueue cost
//
//  Two sub-benchmarks:
//   free_slot   — pool has available slots (common case when < 16 impacts/frame)
//   voice_steal — all 16 slots occupied; steals the slot closest to finishing
// ─────────────────────────────────────────────────────────────────────────────

fn bench_schedule_voice(c: &mut Criterion) {
    let mut group = c.benchmark_group("schedule_voice");

    let slot = upload_fixture(96_000); // 1 second mono, plenty for setup
    let mut activation_buf = vec![0.0f32; 64]; // small buffer for priming

    // ── free_slot: schedule into an empty pool ────────────────────────────────
    group.bench_function("free_slot", |b| {
        b.iter_custom(|iters| {
            let mut total = Duration::ZERO;
            for _ in 0..iters {
                unsafe { reset_audio_state(); } // guarantee free slots
                let start = std::time::Instant::now();
                unsafe { schedule_voice(black_box(slot), black_box(1.0)); }
                total += start.elapsed();
            }
            total
        });
    });

    // ── voice_steal: schedule into a full pool ─────────────────────────────────
    // All 16 voices are active (sample_pos > 0) so the stealing loop runs.
    group.bench_function("voice_steal", |b| {
        b.iter_custom(|iters| {
            let mut total = Duration::ZERO;
            for _ in 0..iters {
                // Fill and activate all 16 slots outside timed section
                unsafe { reset_audio_state(); }
                prime_voices(slot, 16, &mut activation_buf);

                // Sanity: pool should be full
                debug_assert_eq!(
                    unsafe { active_voice_count() },
                    16,
                    "pool not full before steal bench"
                );

                // Timed: this call must steal the best slot
                let start = std::time::Instant::now();
                unsafe { schedule_voice(black_box(slot), black_box(1.0)); }
                total += start.elapsed();
            }
            total
        });
    });

    group.finish();
}

// ─────────────────────────────────────────────────────────────────────────────
//  Group 3: full_cycle — realistic per-impact frame cost
//
//  Models what happens when a projectile hits:
//    1. schedule_voice() on the main thread
//    2. process_buffer() on the audio thread (simulated here in-series)
//
//  This is the end-to-end cost the game pays per impact event that coincides
//  with an audio thread callback. Measured for N simultaneous impacts.
// ─────────────────────────────────────────────────────────────────────────────

fn bench_full_cycle(c: &mut Criterion) {
    let mut group = c.benchmark_group("full_cycle");

    let slot = upload_fixture(192_000);
    let buf_size = 512usize; // standard Unity DSP buffer at 48 kHz

    for &impact_count in &[1usize, 4, 8, 16] {
        group.throughput(Throughput::Elements(impact_count as u64));

        let mut buf = vec![0.0f32; buf_size];

        group.bench_with_input(
            BenchmarkId::from_parameter(impact_count),
            &impact_count,
            |b, &n| {
                b.iter_custom(|iters| {
                    let mut total = Duration::ZERO;
                    for _ in 0..iters {
                        unsafe { reset_audio_state(); }

                        // Timed: schedule N voices + fill one buffer (the audio frame)
                        let start = std::time::Instant::now();
                        for _ in 0..n {
                            unsafe { schedule_voice(slot, 1.0); }
                        }
                        unsafe {
                            process_buffer(buf.as_mut_ptr(), buf.len() as i32);
                        }
                        total += start.elapsed();

                        black_box(&buf);
                    }
                    total
                });
            },
        );
    }

    group.finish();
}

// ─────────────────────────────────────────────────────────────────────────────
//  Group 4: limiter — peak hold + gain recovery cost under heavy load
//
//  Schedules 16 full-amplitude voices and runs multiple buffer fills to
//  observe the limiter gain recovery over time. Measures per-buffer cost
//  when the limiter is actively engaged vs recovered.
// ─────────────────────────────────────────────────────────────────────────────

fn bench_limiter(c: &mut Criterion) {
    let mut group = c.benchmark_group("limiter");

    // Generate full-amplitude clip to guarantee limiter engagement
    let full_amp: Vec<f32> = (0..96_000).map(|_| 1.0f32).collect();
    let slot = unsafe { upload_pcm_clip(full_amp.as_ptr(), full_amp.len() as i32) };
    assert!(slot >= 0);

    let buf_size = 512usize;

    // ── engaged: limiter under peak load ─────────────────────────────────────
    group.bench_function("engaged", |b| {
        let mut buf = vec![0.0f32; buf_size];
        b.iter_custom(|iters| {
            let mut total = Duration::ZERO;
            for _ in 0..iters {
                unsafe { reset_audio_state(); }
                // Schedule 16 full-amplitude voices — limiter will engage
                for _ in 0..16 {
                    unsafe { schedule_voice(slot, 1.0); }
                }
                let start = std::time::Instant::now();
                unsafe { process_buffer(buf.as_mut_ptr(), buf.len() as i32); }
                total += start.elapsed();
                black_box(&buf);
            }
            total
        });
    });

    // ── idle: limiter recovering gain (no voices, gain slowly returns to 1.0) ─
    group.bench_function("idle_recovery", |b| {
        let mut buf = vec![0.0f32; buf_size];
        b.iter_custom(|iters| {
            let mut total = Duration::ZERO;
            for _ in 0..iters {
                unsafe { reset_audio_state(); }
                let start = std::time::Instant::now();
                unsafe { process_buffer(buf.as_mut_ptr(), buf.len() as i32); }
                total += start.elapsed();
                black_box(&buf);
            }
            total
        });
    });

    group.finish();
}

// ─────────────────────────────────────────────────────────────────────────────
//  Criterion harness
// ─────────────────────────────────────────────────────────────────────────────

criterion_group!(
    benches,
    bench_process_buffer,
    bench_schedule_voice,
    bench_full_cycle,
    bench_limiter,
);
criterion_main!(benches);
