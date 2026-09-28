//! `farm-player`: the Farming RPG Maker game player.
//!
//! - [`Player`] is the embeddable graphical player: a [`session::PlaySession`], the in-game UI
//!   (`farm-ui`), the game shell (title, save slots, pause menu, settings, credits), saves and
//!   settings stores, and rendering, driven by one [`Player::frame`] call per frame. It is
//!   platform independent (no window, clock or OS access) and `Send`; the editor's Play Mode
//!   embeds it through farm-ffi.
//! - [`desktop`] (feature `desktop`, on by default) is the exported game: a window, gamepads,
//!   audio, user folders and crash logs around a `Player`.
//! - `speaker` (feature `audio-out`, part of `desktop`) plays a frame's sounds on the default
//!   output device; the editor enables it without the rest of the desktop game.
//! - [`run_cartridge`] is the headless path: load a cartridge, replay commands, check a hash,
//!   write a save (`--headless`).
#![forbid(unsafe_code)]

pub mod audio;
#[cfg(feature = "desktop")]
pub mod desktop;
pub mod input;
pub mod player;
pub mod plugins;
pub mod render;
pub mod saves;
pub mod script;
pub mod session;
#[cfg(feature = "audio-out")]
pub mod speaker;

pub use audio::{Mixer, SoundRequest};
pub use farm_ui::{GamepadButton, Settings};
pub use input::{FrameInput, GamepadAxis, InputEvent, InputRouter, PointerButton};
pub use player::{FrameOutput, Player, PlayerError, PlayerMode, PlayerOptions, PlayerRequest, ScreenKind, StepOutput};
pub use saves::{
    FsSaveStore, FsSettingsStore, MemorySaveStore, MemorySettingsStore, SaveStore, SettingsStore, SLOT_COUNT,
};
pub use session::{DebugAction, PlaySession};

use farm_cart::save_file::{self, SaveTarget};
use farm_sim::effects::Effect;
use farm_sim::replay::{self, ReplayInput};
use farm_sim::{hash, quests, state, EngineContext};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ReplayFile {
    pub seed: Option<String>,
    #[serde(default)]
    pub auto_start_quests: bool,
    #[serde(default)]
    pub inputs: Vec<ReplayInput>,
    pub expected_hash: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HeadlessReport {
    pub game_id: String,
    pub game_version: String,
    pub content_hash: String,
    pub state_hash: String,
    pub effects: Vec<Effect>,
    pub warnings: Vec<String>,
}

#[derive(Debug, Clone)]
pub struct HeadlessRun {
    pub report: HeadlessReport,
    /// The final state as a JSON save (debugging, the web version).
    pub save_json: String,
    /// The final state as a binary `FGSV` save (what the player writes to its save slots).
    pub save_binary: Vec<u8>,
}

/// Run a compiled cartridge with optional replay and incoming save. A bad cartridge, save,
/// replay or hash expectation is an error; the caller decides how to report it.
pub fn run_cartridge(bytes: &[u8], replay: &ReplayFile, load_save: Option<&[u8]>) -> Result<HeadlessRun, String> {
    let cart = farm_cart::load_cartridge(bytes)?;
    let target = SaveTarget::for_cartridge(&cart);
    let content_hash = hash::hash_state(&cart.content);
    let ctx = EngineContext::new(cart.content);

    let (mut game_state, warnings) = if let Some(save) = load_save {
        let loaded = save_file::load_save_bytes(save, &target, &ctx.content);
        if !loaded.ok {
            return Err(loaded.errors.join("\n"));
        }
        (loaded.state.ok_or("Save loaded without a game state.")?, loaded.warnings)
    } else {
        let mut initial = state::create_game_state_from_start(&cart.start, replay.seed.as_deref());
        if replay.auto_start_quests {
            quests::auto_start_quests(&ctx, &mut initial);
        }
        (initial, Vec::new())
    };

    let result = replay::run_replay(&ctx, &mut game_state, &replay.inputs);
    if let Some(expected) = &replay.expected_hash {
        if *expected != result.hash {
            return Err(format!("Replay hash mismatch: expected {expected}, got {}.", result.hash));
        }
    }
    let save_json = save_file::write_save(&game_state, &target);
    let mut preview = save_file::SavePreview::of_state(&game_state);
    preview.farm_name = cart.info.title.clone();
    let save_binary = save_file::write_save_binary(&game_state, &target, &preview);
    Ok(HeadlessRun {
        report: HeadlessReport {
            game_id: target.game_id,
            game_version: target.game_version,
            content_hash,
            state_hash: result.hash,
            effects: result.effects,
            warnings,
        },
        save_json,
        save_binary,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const CART: &[u8] = include_bytes!("../../../fixtures/golden/cartridges/project-v8.cart");

    #[test]
    fn replay_and_save_are_deterministic() {
        let replay = ReplayFile { inputs: vec![ReplayInput::Tick { ticks: 20.0 }], ..ReplayFile::default() };
        let first = run_cartridge(CART, &replay, None).unwrap();
        let second = run_cartridge(CART, &replay, None).unwrap();
        assert_eq!(first.report.state_hash, second.report.state_hash);
        assert_eq!(first.save_json, second.save_json);
        assert_eq!(first.report.game_id, "local.project-1");
        let loaded = run_cartridge(CART, &ReplayFile::default(), Some(first.save_json.as_bytes())).unwrap();
        assert_eq!(first.report.state_hash, loaded.report.state_hash);
        assert_eq!(first.save_binary, second.save_binary);
        let binary = run_cartridge(CART, &ReplayFile::default(), Some(&first.save_binary)).unwrap();
        assert_eq!(first.report.state_hash, binary.report.state_hash);
    }

    #[test]
    fn rejects_bad_hash_and_wrong_game_save() {
        let replay = ReplayFile { expected_hash: Some("wrong".to_owned()), ..ReplayFile::default() };
        assert!(run_cartridge(CART, &replay, None).unwrap_err().contains("Replay hash mismatch"));
        let save = run_cartridge(CART, &ReplayFile::default(), None).unwrap().save_json;
        let wrong = save.replace("local.project-1", "another.game");
        assert!(run_cartridge(CART, &ReplayFile::default(), Some(wrong.as_bytes()))
            .unwrap_err()
            .contains("different game"));
    }
}
