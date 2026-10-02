//! `farm-cart`: cartridge and save formats (docs/LANGUAGES.md "Contracts between languages").
//!
//! Format 2 cartridges are FlatBuffers with compatibility JSON sections (content, new-game start
//! state, presentation) and an embedded asset table. Saves are binary (`FGSV`: header, slot
//! preview, zstd-compressed state) or JSON with the same header.
#![forbid(unsafe_code)]

/// Cartridge format version. Bumped on every breaking change to the cartridge layout.
pub const CART_FORMAT: u32 = 2;

pub mod cartridge;
pub mod save;
pub mod save_file;

pub use cartridge::{
    inline_assets, is_cartridge, load_cartridge, read_cartridge, Asset, AssetRef, AssetTable, CartPlugin, Cartridge,
    GameInfo, GameInfoOwned, LoadedCartridge, ASSET_URL_PREFIX,
};
pub use save::{migrate_game_state, migrate_game_state_json, migrate_game_state_owned};
pub use save_file::{
    is_binary_save, load_save, load_save_bytes, read_save_preview, write_save, write_save_binary, LoadedSave,
    SaveHeader, SavePreview, SaveTarget, SAVE_FORMAT,
};
