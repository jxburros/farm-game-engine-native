//! Standalone headless player: loads the same `game.cart` as the editor's Rust session,
//! replays commands, checks an expected hash and writes a portable save. The graphical
//! shell will use this cartridge and simulation path when it is added.
#![forbid(unsafe_code)]

use farm_cart::save_file::{self, SaveTarget};
use farm_sim::effects::Effect;
use farm_sim::replay::{self, ReplayInput};
use farm_sim::schema::{GameContent, GameProject};
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
    pub save_json: String,
}

/// Run a compiled cartridge with optional replay and incoming save. A bad cartridge, save,
/// replay or hash expectation is an error; the caller decides how to report it.
pub fn run_cartridge(bytes: &[u8], replay: &ReplayFile, load_save: Option<&str>) -> Result<HeadlessRun, String> {
    let cart = farm_cart::read_cartridge(bytes)?;
    let project: GameProject =
        serde_json::from_slice(cart.project_json).map_err(|error| format!("Cartridge project: {error}"))?;
    let content: GameContent =
        serde_json::from_slice(cart.content_json).map_err(|error| format!("Cartridge content: {error}"))?;
    let mut target = SaveTarget::for_project(&project, &content);
    if cart.info.game_id != target.game_id || cart.info.version != target.game_version {
        return Err("Cartridge game identity differs from its project data.".to_owned());
    }
    target.game_id = cart.info.game_id.to_owned();
    target.game_version = cart.info.version.to_owned();
    let content_hash = hash::hash_state(&content);
    let ctx = EngineContext::new(content);

    let (mut game_state, warnings) = if let Some(save) = load_save {
        let loaded = save_file::load_save(save, &target, &ctx.content);
        if !loaded.ok {
            return Err(loaded.errors.join("\n"));
        }
        (loaded.state.ok_or("Save loaded without a game state.")?, loaded.warnings)
    } else {
        let mut initial = state::create_game_state(&project, replay.seed.as_deref());
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
        let loaded = run_cartridge(CART, &ReplayFile::default(), Some(&first.save_json)).unwrap();
        assert_eq!(first.report.state_hash, loaded.report.state_hash);
    }

    #[test]
    fn rejects_bad_hash_and_wrong_game_save() {
        let replay = ReplayFile { expected_hash: Some("wrong".to_owned()), ..ReplayFile::default() };
        assert!(run_cartridge(CART, &replay, None).unwrap_err().contains("Replay hash mismatch"));
        let save = run_cartridge(CART, &ReplayFile::default(), None).unwrap().save_json;
        let wrong = save.replace("local.project-1", "another.game");
        assert!(run_cartridge(CART, &ReplayFile::default(), Some(&wrong)).unwrap_err().contains("different game"));
    }
}
