//! Headless engine sessions for tools and tests (docs/LANGUAGES.md "FFI: .NET → Rust").
//!
//! A session accepts web-compatible project JSON or the F# FlatBuffers cartridge. Commands and
//! effects cross as JSON arrays, and state is stable JSON. The graphical player is
//! [`crate::player`] instead.

use crate::view_json;
use farm_cart::save_file::{self, SaveTarget};
use farm_sim::commands::Command;
use farm_sim::engine_types::{CommandRules, EngineContext};
use farm_sim::hooks::HookBus;
use farm_sim::schema::{GameProject, GameState};
use farm_sim::{engine, game_time, hash, quests, stable_json, state};

/// Immutable content plus the live state.
pub struct HostSession {
    ctx: EngineContext,
    state: GameState,
    /// The editor project JSON the session started from, as given (`None` for a cartridge):
    /// the state is written into it, so content keeps the values the creator typed.
    project: Option<serde_json::Value>,
    /// Which game this session's saves belong to (header of [`HostSession::save`]).
    target: SaveTarget,
}

impl std::fmt::Debug for HostSession {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("HostSession").field("game", &self.target.game_id).finish_non_exhaustive()
    }
}

impl HostSession {
    /// A session from migrated project JSON or a verified `FGCT` cartridge. `seed` `None` (or
    /// empty) uses the project's own `id:gameStartTime` seed, like the TS engine.
    /// `auto_start_quests` does what hosts do at game start.
    pub fn new(game: &[u8], seed: Option<&str>, auto_start_quests: bool) -> Result<Self, String> {
        let seed = seed.filter(|seed| !seed.is_empty());
        let (project, content, mut game_state, target) = if farm_cart::is_cartridge(game) {
            let cart = farm_cart::load_cartridge(game)?;
            let target = SaveTarget::for_cartridge(&cart);
            let game_state = state::create_game_state_from_start(&cart.start, seed);
            (None, cart.content, game_state, target)
        } else {
            let json: serde_json::Value = serde_json::from_slice(game).map_err(|e| format!("project JSON: {e}"))?;
            let project: GameProject =
                serde_json::from_value(json.clone()).map_err(|e| format!("project JSON: {e}"))?;
            let content = state::create_content_from_project(&project);
            let game_state = state::create_game_state(&project, seed);
            let target = SaveTarget::for_project(&project, &content);
            (Some(json), content, game_state, target)
        };
        let ctx = EngineContext::with_hooks(content, HookBus::new());
        if auto_start_quests {
            quests::auto_start_quests(&ctx, &mut game_state);
        }
        Ok(Self { ctx, state: game_state, project, target })
    }

    pub fn state(&self) -> &GameState {
        &self.state
    }

    /// Applies a JSON array of commands (`[{"type":"move","dir":"up"}, …]`) in order and
    /// returns the effects of all of them as a JSON array.
    pub fn apply(&mut self, commands: &[u8]) -> Result<String, String> {
        let commands: Vec<Command> = serde_json::from_slice(commands).map_err(|e| format!("commands JSON: {e}"))?;
        let mut effects = Vec::new();
        for command in &commands {
            effects.extend(engine::apply_command(&self.ctx, &mut self.state, command));
        }
        Ok(stable_json::stringify(&effects))
    }

    /// Advances `ticks` simulation ticks and returns their effects as a JSON array.
    pub fn tick(&mut self, ticks: u32) -> String {
        let effects = engine::advance_tick(&self.ctx, &mut self.state, u64::from(ticks));
        stable_json::stringify(&effects)
    }

    /// The full state as stable JSON.
    pub fn state_json(&self) -> String {
        stable_json::stringify(&self.state)
    }

    /// The state hash (`hashState`), 16 hex characters.
    pub fn hash(&self) -> String {
        hash::hash_state(&self.state)
    }

    /// The project with the live state written back (`applyStateToProject`), as stable JSON.
    pub fn project_json(&self) -> Result<String, String> {
        let project = self.project.as_ref().ok_or("A cartridge session has no editor project to write back to.")?;
        Ok(stable_json::stringify(&state::apply_state_to_project_json(project, &self.state)?))
    }

    /// Creator debug action: the overnight pass of a sleep command without requiring the player
    /// to be at a bed. Effects are discarded, as in the debug drawer; hook events remain.
    pub fn skip_day(&mut self) {
        game_time::perform_sleep(&self.ctx, &mut self.state, game_time::SleepOptions { collapsed: false });
    }

    /// Drains the hook events emitted since the last drain, as a JSON array of
    /// `{"hook":"onDayStart","payload":{…}}` objects, in engine order.
    pub fn hook_events(&mut self) -> String {
        view_json::to_json(&self.ctx.drain_hook_events())
    }

    /// Replaces the live state with a `GameState` as JSON, taken as it is (nothing is migrated
    /// or quarantined; only tile grids that don't match their scene's size are fixed). Invalid
    /// JSON leaves the state untouched.
    pub fn set_state(&mut self, state_json: &[u8]) -> Result<(), String> {
        let mut state: GameState = serde_json::from_slice(state_json).map_err(|e| format!("state JSON: {e}"))?;
        state::normalize_world(&mut state);
        self.state = state;
        Ok(())
    }

    /// Whether commands apply wherever the player stands (`true`: scripts, test harnesses and the
    /// golden replays) or only where a player could give them (`false`, the default; see
    /// [`CommandRules`]).
    pub fn set_scripted(&mut self, scripted: bool) {
        self.ctx.rules = if scripted { CommandRules::Scripted } else { CommandRules::Player };
    }

    /// A save file for the live state: `{"header": {…}, "state": {…}}` as stable JSON (see
    /// `farm_cart::save_file`).
    pub fn save(&self) -> String {
        save_file::write_save(&self.state, &self.target)
    }

    /// Loads a JSON save file (or a bare web `GameState`), replacing the state. Old saves are
    /// migrated and items the content no longer has are quarantined. Returns `{"warnings",
    /// "quarantined", "restored", "fromVersion", "migrated"}`; a refused save leaves the state
    /// untouched and the error lists the reasons.
    pub fn load_save(&mut self, save: &[u8]) -> Result<String, String> {
        let text = std::str::from_utf8(save).map_err(|e| format!("save file is not UTF-8: {e}"))?;
        let loaded = save_file::load_save(text, &self.target, &self.ctx.content);
        let Some(state) = loaded.state.filter(|_| loaded.ok) else {
            return Err(loaded.errors.join("\n"));
        };
        self.state = state;
        let report = serde_json::json!({
            "warnings": loaded.warnings,
            "quarantined": loaded.quarantined,
            "restored": loaded.restored,
            "fromVersion": loaded.from_version,
            "migrated": loaded.migrated,
        });
        Ok(stable_json::stringify_value(&report))
    }
}
