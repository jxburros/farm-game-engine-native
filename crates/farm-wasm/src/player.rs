//! `Player`: the graphical player for web pages (mirrors `fe_player_*`).

use crate::storage::WebStorage;
use crate::{alive, bytes_arg, host_error, json_arg, set, FrameResult};
use farm_host::player::{is_engine_failure, FrameRequest, PlayerCreate};
use farm_host::{Guarded, HostPlayer};
use farm_player::PlayerOptions;
use js_sys::{Object, Uint8ClampedArray};
use serde::Deserialize;
use wasm_bindgen::prelude::*;
use wasm_bindgen::JsCast;

/// `new Player(game, options)`'s options: farm-ffi's plus the web's.
#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
struct WebPlayerOptions {
    #[serde(flatten)]
    create: PlayerCreate,
    mode: WebMode,
    /// A storage document (a string or the parsed object).
    storage: Option<serde_json::Value>,
}

#[derive(Debug, Default, Clone, Copy, Deserialize)]
#[serde(rename_all = "camelCase")]
enum WebMode {
    #[default]
    Embedded,
    Standalone,
}

/// Unix seconds, for the time a save was written.
fn now() -> i64 {
    if cfg!(target_arch = "wasm32") {
        (js_sys::Date::now() / 1000.0).floor() as i64
    } else {
        0
    }
}

/// The graphical player: the project or cartridge, its UI, saves and settings, drawn into RGBA
/// frames. `mode: "embedded"` (the default) is the editor's Play Mode; `"standalone"` is an
/// exported game with a title screen and save slots kept in memory (see `exportStorage`).
#[wasm_bindgen(js_name = Player)]
#[derive(Debug)]
pub struct WasmPlayer {
    player: Guarded<HostPlayer>,
    storage: WebStorage,
    /// The storage revision the page last heard about.
    reported: u64,
}

#[wasm_bindgen(js_class = Player)]
impl WasmPlayer {
    /// A player for a (migrated) project — JSON text, a parsed object or its UTF-8 bytes — or
    /// compiled cartridge bytes. `options`: {@link PlayerOptions} (object or JSON text).
    #[wasm_bindgen(constructor)]
    pub fn new(
        #[wasm_bindgen(unchecked_param_type = "Uint8Array | ArrayBuffer | string | object")] game: JsValue,
        #[wasm_bindgen(unchecked_param_type = "PlayerOptions | string")] options: Option<JsValue>,
    ) -> Result<WasmPlayer, JsValue> {
        alive()?;
        let game = bytes_arg(&game)?;
        let options = json_arg(&options.unwrap_or(JsValue::UNDEFINED))?;
        let storage = WebStorage::new();
        let player = farm_host::catch(|| {
            let options: WebPlayerOptions = if options.is_empty() {
                WebPlayerOptions::default()
            } else {
                serde_json::from_str(&options).map_err(|e| format!("player options: {e}"))?
            };
            match &options.storage {
                None | Some(serde_json::Value::Null) => {}
                Some(serde_json::Value::String(text)) => {
                    storage.import_json(text)?;
                }
                Some(document) => {
                    storage.import_json(&document.to_string())?;
                }
            }
            let mut player_options = match options.mode {
                WebMode::Embedded => PlayerOptions::embedded(),
                // A browser tab has nothing to quit to.
                WebMode::Standalone => PlayerOptions { can_quit: false, ..PlayerOptions::standalone() },
            };
            player_options.saves = Box::new(storage.clone());
            player_options.settings = Box::new(storage.clone());
            player_options.clock = Box::new(now);
            HostPlayer::new(&game, &options.create, player_options)
        })
        .map_err(host_error)?;
        let reported = storage.revision();
        Ok(WasmPlayer { player: Guarded::new(player).poisoning_on(is_engine_failure), storage, reported })
    }

    fn run<R>(&mut self, body: impl FnOnce(&mut HostPlayer) -> Result<R, String>) -> Result<R, JsValue> {
        alive()?;
        self.player.run(body).map_err(host_error)
    }

    /// Runs one frame: `request` is a {@link FrameRequest} (object or JSON text). Pass the
    /// previous result's `pixels` as `reuse` to draw into it instead of allocating a new array
    /// (when the size still matches).
    #[wasm_bindgen]
    pub fn frame(
        &mut self,
        #[wasm_bindgen(unchecked_param_type = "FrameRequest | string")] request: JsValue,
        reuse: Option<Uint8ClampedArray>,
    ) -> Result<FrameResult, JsValue> {
        let request = json_arg(&request)?;
        let outcome = self.run(|p| p.frame(&FrameRequest::parse(request.as_bytes())?))?;
        let result = Object::new();
        let (width, height) = outcome.size.unwrap_or((0, 0));
        set(&result, "width", width.into());
        set(&result, "height", height.into());
        let pixels = if outcome.size.is_some() {
            let data = self.player.get().pixels();
            match reuse {
                Some(array) if array.length() as usize == data.len() => {
                    array.copy_from(data);
                    array.into()
                }
                _ => Uint8ClampedArray::from(data).into(),
            }
        } else {
            JsValue::NULL
        };
        set(&result, "pixels", pixels);
        let info = js_sys::JSON::parse(&outcome.info_json()).unwrap_or(JsValue::NULL);
        set(&result, "info", info);
        set(&result, "storageChanged", self.take_storage_change().into());
        Ok(result.unchecked_into())
    }

    fn take_storage_change(&mut self) -> bool {
        let revision = self.storage.revision();
        let changed = revision != self.reported;
        self.reported = revision;
        changed
    }

    /// A debug-drawer action (`{type:"addMoney",amount:500}`, `fullEnergy`, `addMinutes`,
    /// `setSeason`, `giveFirst`, `teleport`, `setFlag`, `skipDay`).
    #[wasm_bindgen]
    pub fn debug(
        &mut self,
        #[wasm_bindgen(unchecked_param_type = "object | string")] action: JsValue,
    ) -> Result<(), JsValue> {
        let action = json_arg(&action)?;
        self.run(|p| p.debug(action.as_bytes()))
    }

    /// Runs engine commands (an array, or its JSON) as if the player had done them.
    #[wasm_bindgen]
    pub fn commands(
        &mut self,
        #[wasm_bindgen(unchecked_param_type = "object[] | string")] commands: JsValue,
    ) -> Result<(), JsValue> {
        let commands = json_arg(&commands)?;
        self.run(|p| p.commands(commands.as_bytes()))
    }

    /// The live game state as stable JSON.
    #[wasm_bindgen(js_name = stateJson)]
    pub fn state_json(&mut self) -> Result<String, JsValue> {
        self.run(|p| p.state_json())
    }

    /// The state hash (`hashState`) of the live game: 16 hex characters.
    #[wasm_bindgen]
    pub fn hash(&mut self) -> Result<String, JsValue> {
        self.run(|p| p.hash())
    }

    /// The project with the live state written back ("keep changes"), as stable JSON. Throws
    /// for a player started from a cartridge.
    #[wasm_bindgen(js_name = syncedProject)]
    pub fn synced_project(&mut self) -> Result<String, JsValue> {
        self.run(|p| p.synced_project())
    }

    /// Read-only queries answered as JSON: `{type:"summary"}`, `{type:"widgetRect",path:[…]}`,
    /// `{type:"toasts"}`, `{type:"pluginErrors"}`.
    #[wasm_bindgen(js_name = queryJson)]
    pub fn query_json(
        &mut self,
        #[wasm_bindgen(unchecked_param_type = "object | string")] query: JsValue,
    ) -> Result<String, JsValue> {
        let query = json_arg(&query)?;
        self.run(|p| p.query_json(query.as_bytes()))
    }

    /// The game's id, title and version (namespace stored saves by `gameId`).
    #[wasm_bindgen(js_name = gameInfo, unchecked_return_type = "{gameId: string; title: string; version: string; author: string | null; company: string | null; credits: string | null}")]
    pub fn game_info(&self) -> Result<JsValue, JsValue> {
        alive()?;
        let info = self.player.get().player().info();
        let json = serde_json::json!({
            "gameId": info.game_id,
            "title": info.title,
            "version": info.version,
            "author": info.author,
            "company": info.company,
            "credits": info.credits,
        });
        js_sys::JSON::parse(&json.to_string())
    }

    /// The save slots and settings as a {@link StorageDocument} JSON string, for the page to
    /// persist (IndexedDB, `localStorage`) when a frame reports `storageChanged`.
    #[wasm_bindgen(js_name = exportStorage)]
    pub fn export_storage(&mut self) -> Result<String, JsValue> {
        alive()?;
        self.reported = self.storage.revision();
        Ok(self.storage.export_json())
    }

    /// Replaces the save slots and settings with a stored document (object or JSON text). The
    /// title screen's slot list and the settings update at once.
    #[wasm_bindgen(js_name = importStorage)]
    pub fn import_storage(
        &mut self,
        #[wasm_bindgen(unchecked_param_type = "StorageDocument | string")] document: JsValue,
    ) -> Result<(), JsValue> {
        let document = json_arg(&document)?;
        let storage = self.storage.clone();
        self.run(|p| {
            let settings = storage.import_json(&document)?;
            let player = p.player_mut();
            if let Some(text) = settings {
                match farm_player::saves::settings_from_toml(&text) {
                    Ok(settings) => player.set_settings(settings),
                    Err(error) => return Err(format!("stored settings: {error}")),
                }
            }
            player.reload_saves();
            Ok(())
        })?;
        self.reported = self.storage.revision();
        Ok(())
    }
}
