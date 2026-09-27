//! `farm-cart`: cartridge and save formats (docs/LANGUAGES.md "Contracts between languages").
//!
//! Format 1 cartridges have a FlatBuffers header and compatibility JSON content; saves are
//! still JSON with a versioned header. Indexed content tables and compressed binary saves
//! are later migration steps.
#![forbid(unsafe_code)]

/// Cartridge format version. Bumped on every breaking change to the cartridge layout.
pub const CART_FORMAT: u32 = 1;

pub mod cartridge;
pub mod save;
pub mod save_file;

pub use cartridge::{is_cartridge, read_cartridge, Cartridge, GameInfo};
pub use save::{migrate_game_state, migrate_game_state_json};
pub use save_file::{load_save, write_save, LoadedSave, SaveHeader, SaveTarget, SAVE_FORMAT};
