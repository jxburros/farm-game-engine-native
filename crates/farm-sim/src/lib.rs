//! `farm-sim`: the deterministic simulation core of Farming RPG Maker.
//!
//! This crate is a reducer: `(state, content, command | ticks) → (state, effects, hook events)`.
//! It does no I/O, spawns no threads and never reads a clock. Same seed + same command log ⇒
//! the same state, byte for byte, on every platform (and in the web version through wasm).
//!
//! **Native numerics** (schema v9, docs/NUMERICS.md): every quantity is an integer in a fixed
//! unit ([`units`]); game logic never does float arithmetic (`clippy::float_arithmetic` is
//! denied outside [`units`], which converts authoring numbers at the JSON boundary). The state
//! hash is xxh3-64 over a canonical binary encoding ([`hash`]). Rust is the reference
//! implementation: the golden replays are recorded from this crate.
//!
//! Layout mirrors the retired C# `FarmEngine.Core` (docs/PORTING.md): the data shapes live in [`schema`],
//! the root modules are the engine files, and gameplay systems live in their folders
//! (`farming`, …).
#![forbid(unsafe_code)]
#![deny(clippy::disallowed_types, clippy::disallowed_methods, clippy::float_arithmetic)]

pub mod animals;
pub mod commands;
pub mod content_builtin;
pub mod content_index;
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
pub mod messages;
pub mod mines;
pub mod npcs;
pub mod overlay;
pub mod packs;
pub mod quests;
pub mod replay;
pub mod rng;
pub mod schema;
pub mod skills;
pub mod social;
pub mod stable_json;
pub mod start;
pub mod state;
pub mod text;
pub mod tools;
pub mod units;
pub mod weather;
pub mod world;

pub use commands::Command;
pub use effects::Effect;
pub use engine::{advance_tick, apply_command};
pub use engine_types::{CommandRules, Effects, EngineContext, StepOutput};
pub use hash::{hash_state, hash_text, stable_stringify};
pub use hooks::{HookBus, HookEvent};
pub use rng::{Rng, RngState};
pub use schema::{GameContent, GameProject, GameState};
pub use start::{Presentation, StartState};
pub use state::{create_content_from_project, create_game_state, create_game_state_from_start};
