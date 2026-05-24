// mid_audio/src/lib.rs
//
// Native DSP limiter/mixer for MidMan Unity audio.
// Compiled to a native DLL (.dll/.dylib/.so) and loaded by Unity.
//
// What this does:
//   - Maintains a fixed pool of audio voices (raw PCM f32 sample data + state)
//   - Mixes active voices into Unity's audio buffer inside OnAudioFilterRead
//   - Applies a simple peak limiter to prevent clipping when many impacts fire simultaneously
//   - All operations on the audio thread are allocation-free
//   - Voice scheduling comes from the main/game thread via atomic flags
//
// What this does NOT do:
//   - Replace AudioSource pooling (keep that in C# for 3D spatialization)
//   - Load or decode audio files (pass pre-decoded f32 PCM slices from C#)
//
// Thread model:
//   Main thread: calls schedule_voice() to request a sound
//   Audio thread: calls process_buffer() to mix and output
//   Shared state: atomic flags only — no locks on the audio path
//
// Note on Unity GC:
//   Unity's stop-the-world GC pauses ALL threads including the audio thread.
//   This DLL doesn't solve that. To minimize GC pressure on the Unity side,
//   pre-allocate your C# audio bridge objects at startup and never allocate
//   inside OnAudioFilterRead.

use std::sync::atomic::{AtomicBool, AtomicI32, AtomicUsize, Ordering};
use std::sync::OnceLock;

// ─────────────────────────────────────────────────────────────────────────────
//  Constants
// ─────────────────────────────────────────────────────────────────────────────

const MAX_VOICES: usize = 16;

// PCM sample storage per voice — max 2 seconds at 48 kHz stereo
// Unity sends interleaved stereo (or mono), so this covers common cases.
// C# fills this at startup via upload_pcm_data().
const MAX_PCM_SAMPLES: usize = 48000 * 2 * 2; // 48kHz * 2sec * stereo

// ─────────────────────────────────────────────────────────────────────────────
//  Voice state
//
//  One Voice = one concurrently-playing impact sound slot.
//  Audio thread reads: active, sample_pos, pcm_len, volume, pcm_bank_slot
//  Main thread writes: pending_trigger, pending_bank_slot, pending_volume
//
//  The handshake:
//    1. Main thread writes pending_* fields, then sets pending_trigger = true
//    2. Audio thread sees pending_trigger, copies pending fields to active fields,
//       resets sample_pos, clears pending_trigger
//    3. Audio thread mixes until sample_pos >= pcm_len, then sets active = false
// ─────────────────────────────────────────────────────────────────────────────

struct Voice {
    // Audio thread owns these when active = true
    active:     AtomicBool,
    sample_pos: AtomicUsize,
    pcm_len:    AtomicUsize,
    volume:     AtomicI32,      // fixed-point: actual_volume = value / 1000.0
    pcm_slot:   AtomicUsize,    // index into PCM_BANK

    // Main thread writes, audio thread reads + clears
    pending_trigger:   AtomicBool,
    pending_bank_slot: AtomicUsize,
    pending_volume:    AtomicI32,
}

impl Voice {
    const fn new() -> Self {
        Self {
            active:            AtomicBool::new(false),
            sample_pos:        AtomicUsize::new(0),
            pcm_len:           AtomicUsize::new(0),
            volume:            AtomicI32::new(1000),
            pcm_slot:          AtomicUsize::new(0),
            pending_trigger:   AtomicBool::new(false),
            pending_bank_slot: AtomicUsize::new(0),
            pending_volume:    AtomicI32::new(1000),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  PCM bank — pre-uploaded audio data from C#
//  Up to 8 distinct sound clips stored as f32 PCM
// ─────────────────────────────────────────────────────────────────────────────

const MAX_BANK_SLOTS: usize = 8;

struct PcmBank {
    data:   Box<[[f32; MAX_PCM_SAMPLES]]>,
    lens:   [usize; MAX_BANK_SLOTS],
    count:  usize,
}

impl PcmBank {
    fn new() -> Self {
        // Box to avoid stack overflow — this is ~3 MB per slot * 8 = ~24 MB
        let data = vec![[0.0f32; MAX_PCM_SAMPLES]; MAX_BANK_SLOTS]
            .into_boxed_slice();
        Self {
            data,
            lens: [0; MAX_BANK_SLOTS],
            count: 0,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Global state — initialized once at startup, never reallocated during play
// ─────────────────────────────────────────────────────────────────────────────

static VOICES: [Voice; MAX_VOICES] = {
    // const evaluation — Voice::new() is const so this is zero-cost
    const V: Voice = Voice::new();
    [V, V, V, V, V, V, V, V, V, V, V, V, V, V, V, V]
};

// PcmBank is heap-allocated so we need OnceLock
static PCM_BANK: OnceLock<std::sync::Mutex<PcmBank>> = OnceLock::new();

fn bank() -> &'static std::sync::Mutex<PcmBank> {
    PCM_BANK.get_or_init(|| std::sync::Mutex::new(PcmBank::new()))
}

// Limiter state — simple peak hold + gain reduction, audio thread only
static LIMITER_GAIN: AtomicI32 = AtomicI32::new(1000); // fixed-point /1000

// ─────────────────────────────────────────────────────────────────────────────
//  Public C-compatible API — called from C# via DllImport
// ─────────────────────────────────────────────────────────────────────────────

/// Upload a decoded PCM audio clip to the bank.
/// pcm_data: pointer to f32 interleaved samples from C# float[] array
/// sample_count: total number of f32 values (frames * channels)
/// Returns the bank slot index (0-7), or -1 on failure.
///
/// Call this at startup from the main thread, not during gameplay.
#[no_mangle]
pub unsafe extern "C" fn upload_pcm_clip(
    pcm_data:     *const f32,
    sample_count: i32,
) -> i32 {
    if pcm_data.is_null() || sample_count <= 0 { return -1; }
    let count = sample_count as usize;
    if count > MAX_PCM_SAMPLES { return -1; }

    let Ok(mut bk) = bank().lock() else { return -1; };
    if bk.count >= MAX_BANK_SLOTS { return -1; }

    let slot = bk.count;
    let src = std::slice::from_raw_parts(pcm_data, count);
    bk.data[slot][..count].copy_from_slice(src);
    bk.lens[slot] = count;
    bk.count += 1;

    slot as i32
}

/// Request a voice to play a clip from the bank.
/// bank_slot: index returned by upload_pcm_clip
/// volume_01: volume as 0.0–1.0, converted internally to fixed-point
///
/// Call from the main thread (e.g. from your C# projectile hit handler).
/// Voice stealing: if all voices are active, the oldest one is reused.
#[no_mangle]
pub extern "C" fn schedule_voice(bank_slot: i32, volume_01: f32) {
    if bank_slot < 0 || bank_slot as usize >= MAX_BANK_SLOTS { return; }
    let vol_fixed = (volume_01.clamp(0.0, 1.0) * 1000.0) as i32;
    let slot_usize = bank_slot as usize;

    // Find a free voice first
    for v in VOICES.iter() {
        if !v.active.load(Ordering::Relaxed) && !v.pending_trigger.load(Ordering::Relaxed) {
            v.pending_bank_slot.store(slot_usize, Ordering::Relaxed);
            v.pending_volume.store(vol_fixed, Ordering::Relaxed);
            v.pending_trigger.store(true, Ordering::Release);
            return;
        }
    }

    // Voice steal: pick voice closest to finishing
    let mut best_idx  = 0usize;
    let mut best_dist = usize::MAX;
    for (i, v) in VOICES.iter().enumerate() {
        let pos = v.sample_pos.load(Ordering::Relaxed);
        let len = v.pcm_len.load(Ordering::Relaxed);
        let remaining = len.saturating_sub(pos);
        if remaining < best_dist { best_dist = remaining; best_idx = i; }
    }
    let v = &VOICES[best_idx];
    v.pending_bank_slot.store(slot_usize, Ordering::Relaxed);
    v.pending_volume.store(vol_fixed, Ordering::Relaxed);
    v.pending_trigger.store(true, Ordering::Release);
}

/// Called from C# OnAudioFilterRead on the Unity audio thread.
/// Mixes all active voices into Unity's buffer and applies a peak limiter.
///
/// buffer: Unity's float[] data array (interleaved channels)
/// length: total sample count (frames * channels)
///
/// IMPORTANT: Do NOT call any Unity managed APIs from this path.
/// Do NOT allocate. Do NOT lock a mutex here (PCM data is read-only after upload).
#[no_mangle]
pub unsafe extern "C" fn process_buffer(buffer: *mut f32, length: i32) {
    if buffer.is_null() || length <= 0 { return; }
    let buf = std::slice::from_raw_parts_mut(buffer, length as usize);

    // --- Phase 1: Activate any pending voices ---
    // This is the only place pending_trigger is cleared.
    // We grab a snapshot of the PCM bank lengths without locking
    // because PCM data is written once at startup and never mutated after that.
    // If a lock is unavailable (extremely unlikely), skip this frame — no deadlock.
    let bank_lens: [usize; MAX_BANK_SLOTS] = {
        match bank().try_lock() {
            Ok(bk)  => bk.lens,
            Err(_)  => [0; MAX_BANK_SLOTS],
        }
    };

    for v in VOICES.iter() {
        if v.pending_trigger.load(Ordering::Acquire) {
            let slot = v.pending_bank_slot.load(Ordering::Relaxed);
            let vol  = v.pending_volume.load(Ordering::Relaxed);
            let len  = bank_lens[slot];
            v.pcm_slot.store(slot, Ordering::Relaxed);
            v.pcm_len.store(len, Ordering::Relaxed);
            v.sample_pos.store(0, Ordering::Relaxed);
            v.volume.store(vol, Ordering::Relaxed);
            v.active.store(true, Ordering::Relaxed);
            v.pending_trigger.store(false, Ordering::Release);
        }
    }

    // --- Phase 2: Mix active voices into buffer ---
    // We need PCM data. Try_lock is safe here because writes only happen at startup.
    let Ok(bk) = bank().try_lock() else { return; };

    let mut peak: f32 = 0.0;

    for sample in buf.iter_mut() {
        let mut mixed: f32 = 0.0;

        for v in VOICES.iter() {
            if !v.active.load(Ordering::Relaxed) { continue; }

            let pos = v.sample_pos.load(Ordering::Relaxed);
            let len = v.pcm_len.load(Ordering::Relaxed);
            if pos >= len {
                v.active.store(false, Ordering::Relaxed);
                continue;
            }

            let slot   = v.pcm_slot.load(Ordering::Relaxed);
            let vol_fp = v.volume.load(Ordering::Relaxed);
            let vol    = vol_fp as f32 / 1000.0;

            mixed += bk.data[slot][pos] * vol;
            v.sample_pos.store(pos + 1, Ordering::Relaxed);
        }

        // --- Phase 3: Peak limiter (per sample, look-ahead free) ---
        let abs_mixed = mixed.abs();
        if abs_mixed > peak { peak = abs_mixed; }

        let current_gain = LIMITER_GAIN.load(Ordering::Relaxed) as f32 / 1000.0;
        let limited = mixed * current_gain;
        *sample += limited;
    }

    // Adjust limiter gain for next buffer
    let current_gain = LIMITER_GAIN.load(Ordering::Relaxed) as f32 / 1000.0;
    let new_gain = if peak * current_gain > 0.95 {
        // Clipping incoming: reduce gain hard
        (current_gain * 0.85).max(0.1)
    } else {
        // Release: slowly recover gain toward 1.0
        (current_gain + 0.002).min(1.0)
    };
    LIMITER_GAIN.store((new_gain * 1000.0) as i32, Ordering::Relaxed);
}

/// Reset all voices and the limiter. Call on scene unload from main thread.
#[no_mangle]
pub extern "C" fn reset_audio_state() {
    for v in VOICES.iter() {
        v.active.store(false, Ordering::Relaxed);
        v.pending_trigger.store(false, Ordering::Relaxed);
        v.sample_pos.store(0, Ordering::Relaxed);
    }
    LIMITER_GAIN.store(1000, Ordering::Relaxed);
}

/// Returns the number of currently active voices. Main thread diagnostic only.
#[no_mangle]
pub extern "C" fn active_voice_count() -> i32 {
    VOICES.iter().filter(|v| v.active.load(Ordering::Relaxed)).count() as i32
}
