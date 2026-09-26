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
//!
//! Layout mirrors `src/FarmEngine.Core` (docs/PORTING.md): the data shapes live in [`schema`],
//! the root modules are the engine files, and gameplay systems live in their folders
//! (`farming`, …).
#![forbid(unsafe_code)]
#![deny(clippy::disallowed_types, clippy::disallowed_methods)]

pub mod animals;
pub mod commands;
pub mod content_builtin;
pub mod crafting;
pub mod dialogue_system;
pub mod economy;
pub mod effects;
pub mod energy;
pub mod engine;
pub mod engine_types;
pub mod events;
pub mod extensibility;
pub mod farming;
pub mod fishing;
pub mod game_time;
pub mod gathering;
pub mod hash;
pub mod hooks;
pub mod inventory;
pub mod js;
pub mod mines;
pub mod npcs;
pub mod packs;
pub mod quests;
pub mod replay;
pub mod rng;
pub mod schema;
pub mod skills;
pub mod social;
pub mod stable_json;
pub mod state;
pub mod tools;
pub mod weather;
pub mod world;

pub use commands::Command;
pub use effects::Effect;
pub use engine::{advance_tick, apply_command};
pub use engine_types::{Effects, EngineContext, StepOutput};
pub use hash::{hash_state, hash_text, stable_stringify};
pub use hooks::{HookBus, HookEvent};
pub use rng::{Rng, RngState};
pub use schema::{GameContent, GameProject, GameState};
pub use state::{create_content_from_project, create_game_state};
