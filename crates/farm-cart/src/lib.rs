//! `farm-cart`: cartridge and save formats (docs/LANGUAGES.md "Contracts between languages").
//!
//! In the compatibility phase a cartridge is the project's `GameContent` as stable JSON, and
//! saves are the `GameState` JSON the web version writes, upgraded by the save migrations in
//! [`save`] and wrapped in a header by [`save_file`]. The FlatBuffers cartridge and the zstd-compressed binary save arrive with the F#
//! compiler (phase 3) and the native-numerics switch (phase 7).
#![forbid(unsafe_code)]

/// Cartridge format version. Bumped on every breaking change to the cartridge layout.
pub const CART_FORMAT: u32 = 1;

pub mod save;
pub mod save_file;

pub use save::{migrate_game_state, migrate_game_state_json};
pub use save_file::{load_save, write_save, LoadedSave, SaveHeader, SaveTarget, SAVE_FORMAT};
