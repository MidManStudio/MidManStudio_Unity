// mid_audio/src/lib.rs  — v2: Peak Limiter DSP
//
// Revised architecture. The PCM bank / voice management approach is removed.
//
// WHY THE PREVIOUS APPROACH WAS WRONG:
//   AudioClip.GetData() requires Decompress On Load AND fully-loaded clip data.
//   Unity loads clips asynchronously; calling GetData() in Awake() races against
//   the loader and produces "data larger than clip loaded" warnings even with
//   correct import settings. Decoding clips in managed code also doubles memory
//   usage and bypasses Unity's hardware audio decoders.
//
// WHAT THIS FILE NOW DOES:
//   Applies a peak limiter to Unity's already-mixed audio output.
//   Called from OnAudioFilterRead on the AudioListener — affects all game audio.
//   Runs at audio-thread speed, isolated from managed GC stalls.
//
// WHAT IT NO LONGER DOES (by design):
//   - Decodes or stores AudioClip data (Unity's AudioSource handles this correctly)
//   - Manages voice allocation (C# AudioSource pool handles this)
//
// Limiter algorithm:
//   1. Find peak of the input buffer (after applying current gain)
//   2. If peak > threshold: multiply gain by attack_factor  (fast gain reduction)
//   3. If peak ≤ threshold: add release_step to gain        (slow gain recovery)
//   4. Apply CURRENT gain to this buffer; new gain takes effect next buffer.
//   Lookahead-free — ~10-20ms latency at 48kHz/512 samples (imperceptible).
//
// Thread model:
//   Audio thread calls: process_buffer, get_limiter_gain (Relaxed reads only)
//   Main thread calls:  set_limiter_params, set_limiter_enabled, reset_limiter
//   All shared state: atomics only — no locks on the audio path.

use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};

// ─────────────────────────────────────────────────────────────────────────────
//  Limiter state — fixed-point: actual = value / 1000.0
// ─────────────────────────────────────────────────────────────────────────────

/// Current gain [0.05, 1.0]. Audio thread reads; main thread reads for display.
static GAIN: AtomicI32 = AtomicI32::new(1000);    // 1.0

/// Threshold above which limiting activates. Default 0.95 = -0.45 dBFS.
static THRESHOLD: AtomicI32 = AtomicI32::new(950);

/// Gain multiplier applied per buffer when peak exceeds threshold. 
/// 850 = 0.85 → fast limiting. Higher = softer knee.
static ATTACK: AtomicI32 = AtomicI32::new(850);

/// Gain increment per buffer during recovery (0.002 default).
/// Smaller = slower recovery (more transparent). Fixed-point × 1_000_000.
static RELEASE: AtomicI32 = AtomicI32::new(2_000); // 0.002 × 1_000_000

static ENABLED: AtomicBool = AtomicBool::new(true);

// ─────────────────────────────────────────────────────────────────────────────
//  DSP — audio thread
// ─────────────────────────────────────────────────────────────────────────────

/// Apply peak limiter to Unity's mixed audio output.
///
/// Attach MID_AudioLimiter.cs to the AudioListener GameObject.
/// OnAudioFilterRead on the AudioListener receives the FINAL mixed output
/// from all AudioSources in the scene.
///
/// buffer: Unity's float[] data (interleaved channels, range [-1, 1])
/// length: total sample count (frames × channels)
///
/// DO NOT allocate. DO NOT call Unity APIs. DO NOT block.
#[no_mangle]
pub unsafe extern "C" fn process_buffer(buffer: *mut f32, length: i32) {
    if buffer.is_null() || length <= 0 { return; }
    if !ENABLED.load(Ordering::Relaxed) { return; }

    let buf = std::slice::from_raw_parts_mut(buffer, length as usize);

    let gain      = GAIN.load(Ordering::Relaxed)      as f32 / 1000.0;
    let threshold = THRESHOLD.load(Ordering::Relaxed) as f32 / 1000.0;
    let attack    = ATTACK.load(Ordering::Relaxed)    as f32 / 1000.0;
    let release   = RELEASE.load(Ordering::Relaxed)   as f32 / 1_000_000.0;

    // ── Phase 1: find peak after applying current gain ─────────────────────
    let mut peak = 0.0f32;
    for &s in buf.iter() {
        let v = (s * gain).abs();
        if v > peak { peak = v; }
    }

    // ── Phase 2: apply current gain ────────────────────────────────────────
    for s in buf.iter_mut() {
        *s *= gain;
    }

    // ── Phase 3: compute next gain ─────────────────────────────────────────
    let next_gain = if peak > threshold {
        (gain * attack).max(0.05)       // reduce: fast
    } else {
        (gain + release).min(1.0)       // recover: slow
    };

    GAIN.store((next_gain * 1000.0) as i32, Ordering::Relaxed);
}

// ─────────────────────────────────────────────────────────────────────────────
//  Configuration — main thread
// ─────────────────────────────────────────────────────────────────────────────

/// Configure limiter behaviour.
/// threshold : 0.1–1.0 (default 0.95). Peak level that triggers gain reduction.
/// attack    : 0.01–0.999 (default 0.85). Gain multiplier per buffer when limiting.
///             Lower = harder, faster limiting. Higher = softer knee.
/// release   : 0.0001–0.05 (default 0.002). Gain recovery per buffer.
///             Lower = slower, more transparent recovery.
#[no_mangle]
pub extern "C" fn set_limiter_params(threshold: f32, attack: f32, release: f32) {
    THRESHOLD.store((threshold.clamp(0.1, 1.0)    * 1000.0)     as i32, Ordering::Relaxed);
    ATTACK.store   ((attack.clamp(0.01, 0.999)    * 1000.0)     as i32, Ordering::Relaxed);
    RELEASE.store  ((release.clamp(0.0001, 0.05)  * 1_000_000.0)as i32, Ordering::Relaxed);
}

/// Enable or disable the limiter. Disabled = unity pass-through (no gain change).
#[no_mangle]
pub extern "C" fn set_limiter_enabled(enabled: u8) {
    ENABLED.store(enabled != 0, Ordering::Relaxed);
}

/// Reset gain to 1.0. Call on scene load/unload.
#[no_mangle]
pub extern "C" fn reset_limiter() {
    GAIN.store(1000, Ordering::Relaxed);
}

/// Returns current limiter gain [0.05, 1.0]. Read from main thread for Inspector display.
/// Relaxed ordering — 1-buffer display lag is acceptable.
#[no_mangle]
pub extern "C" fn get_limiter_gain() -> f32 {
    GAIN.load(Ordering::Relaxed) as f32 / 1000.0
}

/// Returns configured threshold.
#[no_mangle]
pub extern "C" fn get_limiter_threshold() -> f32 {
    THRESHOLD.load(Ordering::Relaxed) as f32 / 1000.0
}

/// Architecture version. 1 = PCM bank (removed). 2 = limiter only (current).
#[no_mangle]
pub extern "C" fn mid_audio_version() -> i32 { 2 }
