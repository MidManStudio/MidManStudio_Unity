// state.rs — snapshot save/restore for client reconciliation

use crate::NativeProjectile;
use core::mem;

const PROJ_SIZE: usize = mem::size_of::<NativeProjectile>();

/// Write all projectiles as raw bytes into buf.
/// Returns bytes written.  buf must be >= count * PROJ_SIZE bytes.
pub fn save(projs: &[NativeProjectile], buf: *mut u8, buf_len: usize) -> usize {
    let needed = projs.len() * PROJ_SIZE;
    if buf_len < needed { return 0; }
    unsafe {
        let src = projs.as_ptr() as *const u8;
        std::ptr::copy_nonoverlapping(src, buf, needed);
    }
    needed
}

/// Read raw bytes back into out_projs.
/// Returns number of projectiles restored.
pub fn restore(out: &mut [NativeProjectile], buf: *const u8, buf_len: usize) -> usize {
    let count = buf_len / PROJ_SIZE;
    let count = count.min(out.len());
    if count == 0 { return 0; }
    unsafe {
        let dst = out.as_mut_ptr() as *mut u8;
        std::ptr::copy_nonoverlapping(buf, dst, count * PROJ_SIZE);
    }
    count
      }
