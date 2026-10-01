//! `Session`: a headless game for tools, tests and replays (mirrors `fe_session_*`).

use crate::{bytes_arg, enter, host_error, json_arg};
use farm_host::{Guarded, HostSession};
use wasm_bindgen::prelude::*;

/// A headless game: immutable content plus the live state. Commands and effects cross as JSON
/// arrays and state as stable JSON, exactly as through farm-ffi, so hashes match every host.
#[wasm_bindgen(js_name = Session)]
#[derive(Debug)]
pub struct WasmSession {
    session: Guarded<HostSession>,
}

#[wasm_bindgen(js_class = Session)]
impl WasmSession {
    /// A session for a (migrated) project — JSON text, a parsed object or its UTF-8 bytes — or
    /// compiled cartridge bytes. Without a `seed` the project's own seed applies;
    /// `autoStartQuests` does what hosts do at game start.
    #[wasm_bindgen(constructor)]
    pub fn new(
        #[wasm_bindgen(unchecked_param_type = "Uint8Array | ArrayBuffer | string | object")] game: JsValue,
        seed: Option<String>,
        #[wasm_bindgen(js_name = autoStartQuests)] auto_start_quests: Option<bool>,
    ) -> Result<WasmSession, JsValue> {
        let game = bytes_arg(&game)?;
        let _call = enter()?;
        let session = farm_host::catch(|| HostSession::new(&game, seed.as_deref(), auto_start_quests.unwrap_or(false)))
            .map_err(host_error)?;
        Ok(WasmSession { session: Guarded::new(session) })
    }

    fn run<R>(&mut self, body: impl FnOnce(&mut HostSession) -> Result<R, String>) -> Result<R, JsValue> {
        let _call = enter()?;
        self.session.run(body).map_err(host_error)
    }

    /// Applies commands (an array, or its JSON) in order; returns the effects of all of them as
    /// a JSON array.
    #[wasm_bindgen]
    pub fn apply(
        &mut self,
        #[wasm_bindgen(unchecked_param_type = "object[] | string")] commands: JsValue,
    ) -> Result<String, JsValue> {
        let commands = json_arg(&commands)?;
        self.run(|s| s.apply(commands.as_bytes()))
    }

    /// Advances `ticks` simulation ticks (20 per second); returns their effects as a JSON array.
    #[wasm_bindgen]
    pub fn tick(&mut self, ticks: u32) -> Result<String, JsValue> {
        self.run(|s| Ok(s.tick(ticks)))
    }

    /// The full state as stable JSON.
    #[wasm_bindgen(js_name = stateJson)]
    pub fn state_json(&mut self) -> Result<String, JsValue> {
        self.run(|s| Ok(s.state_json()))
    }

    /// The state hash (`hashState`), 16 hex characters.
    #[wasm_bindgen]
    pub fn hash(&mut self) -> Result<String, JsValue> {
        self.run(|s| Ok(s.hash()))
    }

    /// The project with the live state written back, as stable JSON. Throws for a cartridge.
    #[wasm_bindgen(js_name = projectJson)]
    pub fn project_json(&mut self) -> Result<String, JsValue> {
        self.run(|s| s.project_json())
    }

    /// The overnight pass of a sleep without walking to a bed (the debug drawer); effects are
    /// discarded, hook events remain.
    #[wasm_bindgen(js_name = skipDay)]
    pub fn skip_day(&mut self) -> Result<(), JsValue> {
        self.run(|s| {
            s.skip_day();
            Ok(())
        })
    }

    /// Drains the hook events emitted since the last call: a JSON array of
    /// `{"hook":"onDayStart","payload":{…}}` in engine order.
    #[wasm_bindgen(js_name = hookEvents)]
    pub fn hook_events(&mut self) -> Result<String, JsValue> {
        self.run(|s| Ok(s.hook_events()))
    }

    /// Whether commands apply wherever the player stands (`true`: scripts, test harnesses and the
    /// golden replays) or only where a player could give them (`false`, the default: no
    /// `descendMine` away from the mine, no `openShop` without facing the merchant, nothing but
    /// the open dialogue's, shop's or minigame's own commands while one is open, …).
    #[wasm_bindgen(js_name = setScripted)]
    pub fn set_scripted(&mut self, scripted: bool) -> Result<(), JsValue> {
        self.run(|s| {
            s.set_scripted(scripted);
            Ok(())
        })
    }

    /// Replaces the live state with a `GameState` (object or JSON text), taken as it is (only
    /// tile grids that don't match their scene's size are fixed).
    #[wasm_bindgen(js_name = setState)]
    pub fn set_state(
        &mut self,
        #[wasm_bindgen(unchecked_param_type = "object | string")] state: JsValue,
    ) -> Result<(), JsValue> {
        let state = json_arg(&state)?;
        self.run(|s| s.set_state(state.as_bytes()))
    }

    /// A JSON save of the live state (`{"header":{…},"state":{…}}`, stable JSON).
    #[wasm_bindgen]
    pub fn save(&mut self) -> Result<String, JsValue> {
        self.run(|s| Ok(s.save()))
    }

    /// Loads a JSON save (or a bare web `GameState`), migrating old saves and quarantining items
    /// the game no longer has. Returns `{"warnings","quarantined","restored","fromVersion",
    /// "migrated"}` as JSON; a refused save throws and keeps the state.
    #[wasm_bindgen(js_name = loadSave)]
    pub fn load_save(
        &mut self,
        #[wasm_bindgen(unchecked_param_type = "string | Uint8Array | object")] save: JsValue,
    ) -> Result<String, JsValue> {
        let save = bytes_arg(&save)?;
        self.run(|s| s.load_save(&save))
    }
}
