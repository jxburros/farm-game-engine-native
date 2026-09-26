//! `farm-sim`: the deterministic simulation core of Farming RPG Maker.
//!
//! This crate is a reducer: `(state, content, command | ticks) → (state, effects, hook events)`.
//! It does no I/O, spawns no threads and never reads a clock. Same seed + same command log ⇒
//! the same state, byte for byte, on every platform (and in the web version through wasm).
//!
//! **Compatibility phase** (docs/LANGUAGES.md, phases 1–6): every number is an `f64` and the
//! [`js`] module reproduces the JavaScript semantics the TypeScript reference engine has
//! (`Math.round`, stable sort, `String(number)`), so the golden replays recorded from the
//! TypeScript engine hash identically here. The native-numerics switch (phase 7) replaces
//! this with integer and fixed-point types.
#![forbid(unsafe_code)]
#![deny(clippy::disallowed_types, clippy::disallowed_methods)]

pub mod hash;
pub mod js;
pub mod rng;
pub mod stable_json;

pub use hash::{hash_state, hash_text, stable_stringify};
pub use rng::{Rng, RngState};
