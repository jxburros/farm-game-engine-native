//! `farm-ffi`: the C ABI that `FarmEngine.Interop` (C#) binds to.
//!
//! Conventions (docs/LANGUAGES.md "FFI: .NET → Rust"):
//! - coarse, handle-based, batched calls; never one call per tile or entity;
//! - Rust allocates result buffers, .NET copies what it needs and frees them with
//!   [`fe_bytes_free`]; no pointer into Rust memory outlives the next call on that session;
//! - panics are caught at the boundary and returned as error results, never unwound into .NET.
//!
//! `lib.rs` has the primitives (version, stable hash, buffers); [`session`] has the engine
//! sessions (create from project JSON, apply commands, tick, read state/hash/views); [`render`]
//! has `farm-render` requests and the Edit Mode map preview; [`player`] the graphical player.
//!
//! The requests and answers themselves live in `farm-host`, which farm-wasm shares: this crate
//! only moves bytes across the C ABI and maps [`farm_host::HostError`]s to [`FeResult`]s.

use std::panic::{catch_unwind, AssertUnwindSafe};

pub mod player;
pub mod render;
pub mod session;
/// JSON for host views in engine order (moved to `farm-host`, shared with farm-wasm).
pub use farm_host::view_json;
pub use render::FePreview;
pub use session::FeSession;

/// A Rust-allocated byte buffer handed to .NET. Free it with [`fe_bytes_free`].
#[repr(C)]
#[derive(Debug)]
pub struct FeBytes {
    pub ptr: *mut u8,
    pub len: usize,
    pub cap: usize,
}

impl FeBytes {
    pub(crate) fn from_vec(mut vec: Vec<u8>) -> Self {
        let bytes = Self { ptr: vec.as_mut_ptr(), len: vec.len(), cap: vec.capacity() };
        std::mem::forget(vec);
        bytes
    }

    pub(crate) fn empty() -> Self {
        Self { ptr: std::ptr::null_mut(), len: 0, cap: 0 }
    }
}

/// Result codes shared by every call.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FeResult {
    Ok = 0,
    InvalidArgument = 1,
    Panic = 2,
    /// A previous call on this session panicked; its state is not trustworthy.
    Poisoned = 3,
}

impl From<farm_host::ErrorKind> for FeResult {
    fn from(kind: farm_host::ErrorKind) -> Self {
        match kind {
            farm_host::ErrorKind::Invalid => FeResult::InvalidArgument,
            farm_host::ErrorKind::Panic => FeResult::Panic,
            farm_host::ErrorKind::Poisoned => FeResult::Poisoned,
        }
    }
}

/// The `len` bytes at `ptr`; null is accepted only with length 0.
pub(crate) unsafe fn bytes_arg<'a>(ptr: *const u8, len: usize) -> Option<&'a [u8]> {
    if ptr.is_null() {
        (len == 0).then_some(&[])
    } else {
        Some(std::slice::from_raw_parts(ptr, len))
    }
}

/// Hands `bytes` to the caller through `out` (ignored when `out` is null).
pub(crate) unsafe fn write(out: *mut FeBytes, bytes: Vec<u8>) {
    if !out.is_null() {
        *out = FeBytes::from_vec(bytes);
    }
}

pub(crate) unsafe fn write_empty(out: *mut FeBytes) {
    if !out.is_null() {
        *out = FeBytes::empty();
    }
}

/// The crate version as a NUL-terminated UTF-8 string (static; do not free).
#[no_mangle]
pub extern "C" fn fe_version() -> *const std::os::raw::c_char {
    static VERSION: &str = concat!(env!("CARGO_PKG_VERSION"), "\0");
    VERSION.as_ptr().cast()
}

/// Frees a buffer returned by this library. Passing an empty buffer is a no-op.
///
/// # Safety
/// `bytes` must have come from this library and must not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn fe_bytes_free(bytes: FeBytes) {
    if !bytes.ptr.is_null() {
        drop(Vec::from_raw_parts(bytes.ptr, bytes.len, bytes.cap));
    }
}

/// The v8 text hash (FNV-1a over UTF-16, two 32-bit lanes, hex) of `len` UTF-8 bytes at `text`,
/// written to `out` as 16 ASCII characters (no terminator). Since v9 it is not the state hash:
/// `fe_session_hash`/`fe_player_hash` are xxh3-64 over the state's canonical binary encoding
/// (docs/NUMERICS.md), so hashing a state's JSON with this never matches them. Kept for the F#
/// tests' v8 text hashes and as a cheap check that the library loads.
///
/// # Safety
/// `text` must point to `len` readable bytes of valid UTF-8; `out` must be a valid pointer.
#[no_mangle]
pub unsafe extern "C" fn fe_hash_text(text: *const u8, len: usize, out: *mut FeBytes) -> FeResult {
    if text.is_null() || out.is_null() {
        return FeResult::InvalidArgument;
    }
    let input = std::slice::from_raw_parts(text, len);
    let Ok(input) = std::str::from_utf8(input) else {
        return FeResult::InvalidArgument;
    };
    match catch_unwind(AssertUnwindSafe(|| farm_sim::hash_text(input))) {
        Ok(hash) => {
            *out = FeBytes::from_vec(hash.into_bytes());
            FeResult::Ok
        }
        Err(_) => {
            *out = FeBytes::empty();
            FeResult::Panic
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hashes_through_the_c_abi() {
        let text = "{}";
        let mut out = FeBytes::empty();
        let result = unsafe { fe_hash_text(text.as_ptr(), text.len(), &mut out) };
        assert_eq!(result, FeResult::Ok);
        let hash = unsafe { std::slice::from_raw_parts(out.ptr, out.len) }.to_vec();
        assert_eq!(String::from_utf8(hash).unwrap(), "5465b8257807bf56");
        unsafe { fe_bytes_free(out) };
    }
}
