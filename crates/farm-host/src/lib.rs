//! `farm-host`: what a host that embeds the game asks of it, independent of how it calls.
//!
//! Both bindings speak the same JSON protocol and share this crate, so the editor's Play Mode
//! (.NET, through the C ABI in `farm-ffi`) and the web version (JavaScript, through
//! `wasm-bindgen` in `farm-wasm`) run exactly the same code:
//!
//! - [`player::HostPlayer`]: the graphical player (`farm_player::Player`), driven by
//!   `{dt, events, width, height, render}` frame requests;
//! - [`session::HostSession`]: a headless game (commands, ticks, state, saves);
//! - [`render`]: stateless render requests and [`render::HostPreview`], Edit Mode's map;
//! - [`view_json`]: JSON for host views in engine order.
//!
//! Every call takes and returns plain bytes and strings; an `Err` is a message for the host to
//! show. [`Guarded`] adds the boundary rules: panics are caught (where the target can unwind)
//! and poison the handle, and a poisoned handle refuses every later call.
#![forbid(unsafe_code)]

pub mod player;
pub mod render;
pub mod session;
pub mod view_json;

pub use player::HostPlayer;
pub use render::HostPreview;
pub use session::HostSession;

use std::panic::{catch_unwind, AssertUnwindSafe};

/// Why a guarded call failed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ErrorKind {
    /// Bad input or a refused request; the handle stays usable.
    Invalid,
    /// The call panicked; the handle is now poisoned.
    Panic,
    /// An earlier failure poisoned the handle.
    Poisoned,
}

/// A failed guarded call.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HostError {
    pub kind: ErrorKind,
    /// What went wrong (for [`ErrorKind::Poisoned`], the error that poisoned the handle).
    pub message: String,
}

impl std::fmt::Display for HostError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for HostError {}

/// The text of a panic payload.
pub fn panic_message(payload: &(dyn std::any::Any + Send)) -> String {
    if let Some(s) = payload.downcast_ref::<&str>() {
        (*s).to_owned()
    } else if let Some(s) = payload.downcast_ref::<String>() {
        s.clone()
    } else {
        "panic".to_owned()
    }
}

/// Runs `body`, turning a panic into [`ErrorKind::Panic`] and an `Err` into
/// [`ErrorKind::Invalid`] (creating a handle, stateless requests).
///
/// On targets that abort on panic (`wasm32-unknown-unknown`) nothing is caught: the panic traps
/// and the host sees the trap instead.
pub fn catch<R>(body: impl FnOnce() -> Result<R, String>) -> Result<R, HostError> {
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(Ok(value)) => Ok(value),
        Ok(Err(message)) => Err(HostError { kind: ErrorKind::Invalid, message }),
        Err(payload) => Err(HostError { kind: ErrorKind::Panic, message: panic_message(&*payload) }),
    }
}

/// A handle with the boundary rules: see [`Guarded::run`].
#[derive(Debug)]
pub struct Guarded<T> {
    inner: T,
    poisoned: bool,
    last_error: String,
    /// Whether a failed call left the handle unusable besides panics (the player stopped after
    /// an engine failure). Asked of the handle itself, never of the error's wording.
    poisons: fn(&T) -> bool,
}

impl<T> Guarded<T> {
    pub fn new(inner: T) -> Self {
        Self { inner, poisoned: false, last_error: String::new(), poisons: |_| false }
    }

    /// Also poison the handle when a call fails and `poisons` then says the handle stopped.
    pub fn poisoning_when(mut self, poisons: fn(&T) -> bool) -> Self {
        self.poisons = poisons;
        self
    }

    /// Runs `body` on the handle. A poisoned handle answers [`ErrorKind::Poisoned`] with the
    /// error that poisoned it; an `Err` is recorded as the last error ([`ErrorKind::Invalid`],
    /// or [`ErrorKind::Poisoned`] when it poisons); a panic poisons ([`ErrorKind::Panic`]).
    pub fn run<R>(&mut self, body: impl FnOnce(&mut T) -> Result<R, String>) -> Result<R, HostError> {
        if self.poisoned {
            return Err(HostError { kind: ErrorKind::Poisoned, message: self.last_error.clone() });
        }
        let inner = &mut self.inner;
        match catch_unwind(AssertUnwindSafe(|| body(inner))) {
            Ok(Ok(value)) => Ok(value),
            Ok(Err(message)) => {
                self.poisoned = (self.poisons)(&self.inner);
                self.last_error.clone_from(&message);
                let kind = if self.poisoned { ErrorKind::Poisoned } else { ErrorKind::Invalid };
                Err(HostError { kind, message })
            }
            Err(payload) => {
                self.poisoned = true;
                self.last_error = panic_message(&*payload);
                Err(HostError { kind: ErrorKind::Panic, message: self.last_error.clone() })
            }
        }
    }

    /// The message of the last error or panic (empty when none).
    pub fn last_error(&self) -> &str {
        &self.last_error
    }

    pub fn is_poisoned(&self) -> bool {
        self.poisoned
    }

    /// The handle, bypassing the guard (read-only).
    pub fn get(&self) -> &T {
        &self.inner
    }
}

/// An RGBA8 image: `width × height` pixels, row-major.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Rgba {
    pub width: u32,
    pub height: u32,
    pub data: Vec<u8>,
}

impl Rgba {
    /// Converts premultiplied pixels to straight alpha in place (what the web's `ImageData`
    /// expects). Opaque pixels are unchanged.
    pub fn unpremultiply(&mut self) {
        unpremultiply(&mut self.data);
    }
}

/// Converts premultiplied RGBA8 to straight alpha in place.
pub fn unpremultiply(data: &mut [u8]) {
    for pixel in data.chunks_exact_mut(4) {
        let alpha = u32::from(pixel[3]);
        if alpha == 0 || alpha == 255 {
            continue;
        }
        for channel in &mut pixel[..3] {
            *channel = ((u32::from(*channel) * 255 + alpha / 2) / alpha).min(255) as u8;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn guarded_handles_record_errors_and_poison() {
        // The handle says whether it stopped (here: a negative number); the wording does not.
        let mut handle = Guarded::new(0).poisoning_when(|n| *n < 0);
        assert_eq!(handle.run(|n| Ok(*n + 1)), Ok(1));
        let error = handle.run(|_| Err::<(), _>("fatal-sounding input".to_owned())).unwrap_err();
        assert_eq!((error.kind, handle.last_error()), (ErrorKind::Invalid, "fatal-sounding input"));
        assert!(!handle.is_poisoned());
        let error = handle
            .run(|n| {
                *n = -1;
                Err::<(), _>("fatal: engine".to_owned())
            })
            .unwrap_err();
        assert_eq!(error.kind, ErrorKind::Poisoned);
        let error = handle.run(|n| Ok(*n)).unwrap_err();
        assert_eq!((error.kind, error.message.as_str()), (ErrorKind::Poisoned, "fatal: engine"));
    }

    #[test]
    fn unpremultiplies_translucent_pixels_only() {
        let mut data = vec![10, 20, 30, 255, 0, 0, 0, 0, 64, 32, 0, 128];
        unpremultiply(&mut data);
        assert_eq!(data, vec![10, 20, 30, 255, 0, 0, 0, 0, 128, 64, 0, 128]);
    }
}
