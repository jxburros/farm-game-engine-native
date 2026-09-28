//! The graphical player (`farm_player::Player`) over the C ABI: the editor's Play Mode.
//!
//! The editor hands the player the project it is editing (or a compiled cartridge), forwards
//! raw input events and the size of its surface, and shows the frames. All of Play Mode's HUD,
//! dialogue, shop, crafting, inventory, quest, minigame and toast UI is drawn in Rust by
//! `farm-ui`; the editor keeps only its own tools (restart, keep changes, the debug drawer),
//! which reach the game through [`fe_player_debug`] and [`fe_player_synced_project`].
//!
//! Same conventions as the sessions: Rust allocates results and .NET frees them with
//! `fe_bytes_free`; panics never unwind into .NET. On failure `out` holds the UTF-8 error
//! message. A player is used by one thread at a time (the editor runs it on a worker thread).

use crate::{view_json, FeBytes, FeResult};
use farm_player::{DebugAction, InputEvent, Player, PlayerError, PlayerOptions, PlayerRequest, ScreenKind};
use farm_sim::schema::GameProject;
use farm_sim::{stable_json, Command};
use serde::{Deserialize, Serialize};
use std::panic::{catch_unwind, AssertUnwindSafe};

/// Largest frame the editor may request, in pixels.
const MAX_PIXELS: u64 = 64 * 1024 * 1024;

/// Options for [`fe_player_new`].
#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
struct PlayerCreate {
    /// Seed of the game (the project's own seed when absent, like the C# `PlaySession`).
    seed: Option<String>,
    /// No floating pops, fades or flashes.
    reduced_motion: bool,
    /// Interface size (1 = 100 %).
    ui_scale: Option<f32>,
    /// Play the frames' sounds on the default output device (silent without one).
    audio: bool,
}

/// `{dt, events, width, height, render}` for [`fe_player_frame`].
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct FrameRequest {
    dt: f64,
    #[serde(default)]
    events: Vec<InputEvent>,
    width: u32,
    height: u32,
    /// False steps the game without drawing (`Player::step`).
    #[serde(default = "yes")]
    render: bool,
}

fn yes() -> bool {
    true
}

/// What a frame tells the editor besides the pixels.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct FrameInfo {
    /// Sound cues to play, with their gain.
    sounds: Vec<SoundInfo>,
    /// `quit`, `fullscreen:on`, `fullscreen:off`, `title:<text>`.
    requests: Vec<String>,
    screen: &'static str,
    /// An in-game panel (inventory, quests, crafting) or an engine modal is open.
    modal: bool,
}

#[derive(Serialize)]
struct SoundInfo {
    cue: String,
    gain: f32,
}

/// Queries for [`fe_player_query_json`].
#[derive(Deserialize)]
#[serde(tag = "type", rename_all = "camelCase", rename_all_fields = "camelCase")]
enum Query {
    /// What the debug drawer shows: clock, place, seed, scenes and seasons.
    Summary,
    /// The rectangle of a UI widget by its id path (`["pause", "Save"]`, numbers as slot or
    /// list indices), in frame pixels; `null` when it was not drawn last frame (tests).
    WidgetRect { path: Vec<serde_json::Value> },
    /// Toasts shown so far, oldest first (tests).
    Toasts,
    /// Recent plugin errors, oldest first.
    PluginErrors,
}

/// An opaque embedded player.
pub struct FePlayer {
    player: Player,
    /// The output device when the editor asked for sound.
    speaker: Option<farm_player::speaker::SpeakerThread>,
    poisoned: bool,
    last_error: String,
}

impl std::fmt::Debug for FePlayer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FePlayer").field("poisoned", &self.poisoned).finish_non_exhaustive()
    }
}

fn screen_name(screen: ScreenKind) -> &'static str {
    match screen {
        ScreenKind::Title => "title",
        ScreenKind::Playing => "playing",
        ScreenKind::Pause => "pause",
        ScreenKind::Settings => "settings",
        ScreenKind::Credits => "credits",
        ScreenKind::LoadSlots => "loadSlots",
        ScreenKind::SaveSlots => "saveSlots",
        ScreenKind::NewGameSlots => "newGameSlots",
        ScreenKind::Confirm => "confirm",
    }
}

fn request_name(request: &PlayerRequest) -> String {
    match request {
        PlayerRequest::Quit => "quit".to_owned(),
        PlayerRequest::SetFullscreen(on) => format!("fullscreen:{}", if *on { "on" } else { "off" }),
        PlayerRequest::SetTitle(title) => format!("title:{title}"),
    }
}

fn error_text(error: &PlayerError) -> String {
    error.to_string()
}

impl FePlayer {
    fn modal(&self) -> bool {
        self.player.panel().is_some()
            || self
                .player
                .state()
                .is_some_and(|state| state.dialogue.is_some() || state.shop.is_some() || state.minigame.is_some())
    }

    fn frame(&mut self, request: FrameRequest) -> Result<Vec<u8>, String> {
        if request.width == 0 || request.height == 0 {
            return Err("A frame needs a size.".to_owned());
        }
        if u64::from(request.width) * u64::from(request.height) > MAX_PIXELS {
            return Err(format!("The requested frame is too large ({}×{}).", request.width, request.height));
        }
        let dt = if request.dt.is_finite() { request.dt.clamp(0.0, 0.25) } else { 0.0 };
        let (sounds, requests, pixels) = if request.render {
            let output =
                self.player.frame(dt, &request.events, request.width, request.height).map_err(|e| error_text(&e))?;
            let pixels = Some((output.pixels.width(), output.pixels.height(), output.pixels.data().to_vec()));
            (output.sounds, output.requests, pixels)
        } else {
            let output =
                self.player.step(dt, &request.events, request.width, request.height).map_err(|e| error_text(&e))?;
            (output.sounds, output.requests, None)
        };
        if let Some(speaker) = &self.speaker {
            for sound in &sounds {
                speaker.play(sound.clone());
            }
        }
        let info = FrameInfo {
            sounds: sounds.into_iter().map(|sound| SoundInfo { cue: sound.cue, gain: sound.gain }).collect(),
            requests: requests.iter().map(request_name).collect(),
            screen: screen_name(self.player.screen()),
            modal: self.modal(),
        };
        let json = view_json::to_json(&info).into_bytes();
        let (width, height, data) = pixels.unwrap_or((0, 0, Vec::new()));
        let mut out = Vec::with_capacity(12 + json.len() + data.len());
        out.extend_from_slice(&width.to_le_bytes());
        out.extend_from_slice(&height.to_le_bytes());
        out.extend_from_slice(&(json.len() as u32).to_le_bytes());
        out.extend_from_slice(&json);
        out.extend_from_slice(&data);
        Ok(out)
    }

    fn query(&self, query: Query) -> Result<String, String> {
        match query {
            Query::Summary => {
                let state = self.player.state().ok_or("No game is running.")?;
                let content = self.player.session().map(|session| session.content()).ok_or("No game is running.")?;
                let calendar = farm_runtime::host::calendar_view(content, state);
                let summary = serde_json::json!({
                    "tick": state.clock.tick,
                    "day": state.clock.day,
                    "season": state.clock.season,
                    "year": state.clock.year,
                    "timeText": calendar.time_text,
                    "sceneId": state.player.scene_id,
                    "x": state.player.x,
                    "y": state.player.y,
                    "money": state.player.money,
                    "seed": state.meta.engine_seed,
                    "scenes": state.world.scenes.iter().map(|scene| serde_json::json!({"id": scene.id, "name": scene.name})).collect::<Vec<_>>(),
                    "seasons": calendar.seasons.iter().map(|season| serde_json::json!({"id": season.id, "name": season.name})).collect::<Vec<_>>(),
                });
                Ok(view_json::to_json(&summary))
            }
            Query::WidgetRect { path } => {
                let mut parts = path.iter();
                let first =
                    parts.next().and_then(serde_json::Value::as_str).ok_or("A widget path starts with a name.")?;
                let mut id = farm_ui::WidgetId::new(first);
                for part in parts {
                    id = match part {
                        serde_json::Value::String(text) => id.with(text.as_str()),
                        serde_json::Value::Number(number) => {
                            let index = number.as_u64().ok_or("Widget path numbers must be whole and positive.")?;
                            id.with(u32::try_from(index).map_err(|_| "Widget path number too large.")?)
                        }
                        _ => return Err("Widget path parts are strings or numbers.".to_owned()),
                    };
                }
                let rect = self
                    .player
                    .widget_rect(id)
                    .map(|r| serde_json::json!({"x": r.x, "y": r.y, "width": r.width, "height": r.height}));
                Ok(view_json::to_json(&rect))
            }
            Query::Toasts => {
                let toasts: Vec<_> = self
                    .player
                    .toast_history()
                    .iter()
                    .map(|(text, kind)| {
                        let kind = match kind {
                            farm_ui::game::ToastKind::Info => "info",
                            farm_ui::game::ToastKind::Success => "success",
                            farm_ui::game::ToastKind::Error => "error",
                        };
                        serde_json::json!({"text": text, "kind": kind})
                    })
                    .collect();
                Ok(view_json::to_json(&toasts))
            }
            Query::PluginErrors => Ok(view_json::to_json(&self.player.plugin_errors())),
        }
    }
}

unsafe fn bytes_arg<'a>(ptr: *const u8, len: usize) -> Option<&'a [u8]> {
    if ptr.is_null() {
        (len == 0).then_some(&[])
    } else {
        Some(std::slice::from_raw_parts(ptr, len))
    }
}

unsafe fn write(out: *mut FeBytes, bytes: Vec<u8>) {
    if !out.is_null() {
        *out = FeBytes::from_vec(bytes);
    }
}

unsafe fn write_empty(out: *mut FeBytes) {
    if !out.is_null() {
        *out = FeBytes::empty();
    }
}

fn panic_message(payload: Box<dyn std::any::Any + Send>) -> String {
    if let Some(s) = payload.downcast_ref::<&str>() {
        (*s).to_owned()
    } else if let Some(s) = payload.downcast_ref::<String>() {
        s.clone()
    } else {
        "panic".to_owned()
    }
}

/// Creates an embedded player for a (migrated) project's JSON or a compiled cartridge.
/// `options` is `{"seed"?, "reducedMotion"?, "uiScale"?, "audio"?}` (may be empty); with
/// `audio` the frames' sounds play on the default output device. On failure `error`
/// holds the message.
///
/// # Safety
/// `game` points to `len` readable bytes and `options` to `options_len` bytes (or is null with
/// length 0); `out` and `error` are valid pointers. Free the player with [`fe_player_free`].
#[no_mangle]
pub unsafe extern "C" fn fe_player_new(
    game: *const u8,
    len: usize,
    options: *const u8,
    options_len: usize,
    out: *mut *mut FePlayer,
    error: *mut FeBytes,
) -> FeResult {
    write_empty(error);
    if out.is_null() {
        return FeResult::InvalidArgument;
    }
    *out = std::ptr::null_mut();
    let (Some(game), Some(options)) = (bytes_arg(game, len), bytes_arg(options, options_len)) else {
        return FeResult::InvalidArgument;
    };
    let result = catch_unwind(AssertUnwindSafe(|| -> Result<FePlayer, String> {
        let create: PlayerCreate = if options.is_empty() {
            PlayerCreate::default()
        } else {
            serde_json::from_slice(options).map_err(|e| format!("player options: {e}"))?
        };
        let mut player_options = PlayerOptions::embedded();
        player_options.seed = create.seed.filter(|seed| !seed.is_empty());
        let mut player = if farm_cart::is_cartridge(game) {
            Player::from_cartridge_bytes(game, player_options)
        } else {
            let project: GameProject = serde_json::from_slice(game).map_err(|e| format!("project JSON: {e}"))?;
            Player::from_project(project, player_options)
        }
        .map_err(|e| error_text(&e))?;
        let mut settings = player.settings().clone();
        settings.accessibility.reduced_motion = create.reduced_motion;
        if let Some(scale) = create.ui_scale.filter(|scale| scale.is_finite() && *scale > 0.0) {
            settings.display.ui_scale = scale;
        }
        player.set_settings(settings);
        let speaker = create.audio.then(farm_player::speaker::SpeakerThread::start);
        Ok(FePlayer { player, speaker, poisoned: false, last_error: String::new() })
    }));
    match result {
        Ok(Ok(player)) => {
            *out = Box::into_raw(Box::new(player));
            FeResult::Ok
        }
        Ok(Err(message)) => {
            write(error, message.into_bytes());
            FeResult::InvalidArgument
        }
        Err(payload) => {
            write(error, panic_message(payload).into_bytes());
            FeResult::Panic
        }
    }
}

unsafe fn with_player<F>(player: *mut FePlayer, out: *mut FeBytes, body: F) -> FeResult
where
    F: FnOnce(&mut FePlayer) -> Result<Vec<u8>, String>,
{
    write_empty(out);
    if player.is_null() {
        return FeResult::InvalidArgument;
    }
    let player = &mut *player;
    if player.poisoned {
        write(out, player.last_error.clone().into_bytes());
        return FeResult::Poisoned;
    }
    match catch_unwind(AssertUnwindSafe(|| body(player))) {
        Ok(Ok(bytes)) => {
            write(out, bytes);
            FeResult::Ok
        }
        Ok(Err(message)) => {
            // An engine failure ("The game stopped…") poisons the player, which refuses later
            // frames itself; mirror that so every later call fails fast.
            player.poisoned = message.starts_with("The game stopped");
            player.last_error.clone_from(&message);
            write(out, message.into_bytes());
            if player.poisoned {
                FeResult::Poisoned
            } else {
                FeResult::InvalidArgument
            }
        }
        Err(payload) => {
            player.poisoned = true;
            player.last_error = panic_message(payload);
            write(out, player.last_error.clone().into_bytes());
            FeResult::Panic
        }
    }
}

/// Runs one frame. `request` is `{"dt":0.016,"events":[…InputEvent…],"width":1280,
/// "height":800,"render":true}`. `out` receives the frame width and height and the length of a
/// JSON info block as little-endian `u32`s, then the JSON (`{"sounds":[{"cue","gain"}],
/// "requests":[…],"screen":"playing","modal":false}`), then `width × height` premultiplied RGBA8
/// pixels (none when `render` is false).
///
/// # Safety
/// `player` from [`fe_player_new`]; `request` points to `len` bytes; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_player_frame(
    player: *mut FePlayer,
    request: *const u8,
    len: usize,
    out: *mut FeBytes,
) -> FeResult {
    let Some(bytes) = bytes_arg(request, len) else {
        write_empty(out);
        return FeResult::InvalidArgument;
    };
    with_player(player, out, |p| {
        let request: FrameRequest = serde_json::from_slice(bytes).map_err(|e| format!("frame request: {e}"))?;
        p.frame(request)
    })
}

/// A creator debug-drawer action (`{"type":"addMoney","amount":500}`, `fullEnergy`,
/// `addMinutes`, `setSeason`, `giveFirst`, `teleport`, `setFlag`, `skipDay`).
///
/// # Safety
/// `player` from [`fe_player_new`]; `action` points to `len` bytes; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_player_debug(
    player: *mut FePlayer,
    action: *const u8,
    len: usize,
    out: *mut FeBytes,
) -> FeResult {
    let Some(bytes) = bytes_arg(action, len) else {
        write_empty(out);
        return FeResult::InvalidArgument;
    };
    with_player(player, out, |p| {
        let action: DebugAction = serde_json::from_slice(bytes).map_err(|e| format!("debug action: {e}"))?;
        p.player.debug(&action).map_err(|e| error_text(&e))?;
        Ok(Vec::new())
    })
}

/// Runs engine commands (a JSON array) as if the player had done them (tests and tools).
///
/// # Safety
/// `player` from [`fe_player_new`]; `commands` points to `len` bytes; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_player_commands(
    player: *mut FePlayer,
    commands: *const u8,
    len: usize,
    out: *mut FeBytes,
) -> FeResult {
    let Some(bytes) = bytes_arg(commands, len) else {
        write_empty(out);
        return FeResult::InvalidArgument;
    };
    with_player(player, out, |p| {
        let commands: Vec<Command> = serde_json::from_slice(bytes).map_err(|e| format!("commands JSON: {e}"))?;
        for command in &commands {
            p.player.run_command(command).map_err(|e| error_text(&e))?;
        }
        Ok(Vec::new())
    })
}

/// The live game state as stable JSON.
///
/// # Safety
/// `player` from [`fe_player_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_player_state_json(player: *mut FePlayer, out: *mut FeBytes) -> FeResult {
    with_player(player, out, |p| {
        let state = p.player.state().ok_or("No game is running.")?;
        Ok(stable_json::stringify(state).into_bytes())
    })
}

/// The state hash (`hashState`) of the live game.
///
/// # Safety
/// `player` from [`fe_player_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_player_hash(player: *mut FePlayer, out: *mut FeBytes) -> FeResult {
    with_player(player, out, |p| {
        let state = p.player.state().ok_or("No game is running.")?;
        Ok(farm_sim::hash_state(state).into_bytes())
    })
}

/// The editor project with the live state written back ("keep changes"), as stable JSON.
/// Fails for a player started from a cartridge.
///
/// # Safety
/// `player` from [`fe_player_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_player_synced_project(player: *mut FePlayer, out: *mut FeBytes) -> FeResult {
    with_player(player, out, |p| {
        let project = p.player.synced_project().ok_or("This game was not started from an editor project.")?;
        Ok(stable_json::stringify(&project).into_bytes())
    })
}

/// Read-only queries (`{"type":"summary"}`, `{"type":"widgetRect","path":[…]}`,
/// `{"type":"toasts"}`, `{"type":"pluginErrors"}`), answered as JSON.
///
/// # Safety
/// `player` from [`fe_player_new`]; `query` points to `len` bytes; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_player_query_json(
    player: *mut FePlayer,
    query: *const u8,
    len: usize,
    out: *mut FeBytes,
) -> FeResult {
    let Some(bytes) = bytes_arg(query, len) else {
        write_empty(out);
        return FeResult::InvalidArgument;
    };
    with_player(player, out, |p| {
        let query: Query = serde_json::from_slice(bytes).map_err(|e| format!("player query: {e}"))?;
        p.query(query).map(String::into_bytes)
    })
}

/// Frees a player. Null is a no-op.
///
/// # Safety
/// `player` must have come from [`fe_player_new`] and must not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn fe_player_free(player: *mut FePlayer) {
    if !player.is_null() {
        drop(Box::from_raw(player));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn starter_project() -> String {
        let fixture: serde_json::Value =
            serde_json::from_str(include_str!("../../../fixtures/golden/content/starter-farm.json")).unwrap();
        fixture["project"].to_string()
    }

    unsafe fn take(bytes: FeBytes) -> Vec<u8> {
        if bytes.ptr.is_null() {
            return Vec::new();
        }
        let data = std::slice::from_raw_parts(bytes.ptr, bytes.len).to_vec();
        crate::fe_bytes_free(bytes);
        data
    }

    fn new_player(game: &[u8], options: &str) -> *mut FePlayer {
        let mut player: *mut FePlayer = std::ptr::null_mut();
        let mut error = FeBytes::empty();
        let result = unsafe {
            fe_player_new(game.as_ptr(), game.len(), options.as_ptr(), options.len(), &mut player, &mut error)
        };
        assert_eq!(result, FeResult::Ok, "{}", String::from_utf8_lossy(&unsafe { take(error) }));
        player
    }

    fn call(f: impl FnOnce(*mut FeBytes) -> FeResult) -> (FeResult, Vec<u8>) {
        let mut out = FeBytes::empty();
        let result = f(&mut out);
        (result, unsafe { take(out) })
    }

    fn frame(player: *mut FePlayer, request: &str) -> (u32, u32, serde_json::Value, usize) {
        let (result, bytes) = call(|out| unsafe { fe_player_frame(player, request.as_ptr(), request.len(), out) });
        assert_eq!(result, FeResult::Ok, "{}", String::from_utf8_lossy(&bytes));
        let word = |at: usize| u32::from_le_bytes(bytes[at..at + 4].try_into().unwrap());
        let (width, height, json_len) = (word(0), word(4), word(8) as usize);
        let info = serde_json::from_slice(&bytes[12..12 + json_len]).unwrap();
        (width, height, info, bytes.len() - 12 - json_len)
    }

    fn state(player: *mut FePlayer) -> serde_json::Value {
        let (result, bytes) = call(|out| unsafe { fe_player_state_json(player, out) });
        assert_eq!(result, FeResult::Ok);
        serde_json::from_slice(&bytes).unwrap()
    }

    #[test]
    fn sound_is_opt_in_and_silent_without_a_device() {
        let project = starter_project();
        // Sleeping plays a cue; with audio on it also goes to the output device (none on CI).
        let player = new_player(project.as_bytes(), r#"{"audio":true}"#);
        let sleep = br#"[{"type":"sleep"}]"#;
        assert_eq!(call(|out| unsafe { fe_player_commands(player, sleep.as_ptr(), sleep.len(), out) }).0, FeResult::Ok);
        let (_, _, info, _) = frame(player, r#"{"dt":0.016,"events":[],"width":64,"height":40}"#);
        assert!(info["sounds"].is_array());
        unsafe { fe_player_free(player) };
    }

    #[test]
    fn an_embedded_player_renders_frames_and_takes_input() {
        let project = starter_project();
        let player = new_player(project.as_bytes(), r#"{"seed":"ffi-player"}"#);
        let (width, height, info, pixels) = frame(player, r#"{"dt":0,"events":[],"width":320,"height":200}"#);
        assert_eq!((width, height), (320, 200));
        assert_eq!(pixels, 320 * 200 * 4);
        assert_eq!(info["screen"], "playing", "embedded players start in the game");
        assert_eq!(info["modal"], false);

        let x = state(player)["player"]["x"].as_f64().unwrap();
        frame(player, r#"{"dt":0.05,"events":[{"type":"keyDown","key":"d"}],"width":320,"height":200}"#);
        for _ in 0..20 {
            frame(player, r#"{"dt":0.05,"events":[],"width":320,"height":200,"render":false}"#);
        }
        frame(player, r#"{"dt":0.05,"events":[{"type":"keyUp","key":"d"}],"width":320,"height":200}"#);
        assert!(state(player)["player"]["x"].as_f64().unwrap() > x, "held D walked right");
        let (_, _, _, pixels) = frame(player, r#"{"dt":0,"events":[],"width":8,"height":8,"render":false}"#);
        assert_eq!(pixels, 0, "render:false steps without pixels");

        // The debug drawer's actions and summary.
        let money = state(player)["player"]["money"].as_f64().unwrap();
        let action = br#"{"type":"addMoney","amount":500}"#;
        assert_eq!(call(|out| unsafe { fe_player_debug(player, action.as_ptr(), action.len(), out) }).0, FeResult::Ok);
        assert_eq!(state(player)["player"]["money"].as_f64().unwrap(), money + 500.0);
        let query = br#"{"type":"summary"}"#;
        let (result, summary) = call(|out| unsafe { fe_player_query_json(player, query.as_ptr(), query.len(), out) });
        assert_eq!(result, FeResult::Ok);
        let summary: serde_json::Value = serde_json::from_slice(&summary).unwrap();
        assert_eq!(summary["seed"], "ffi-player");
        assert!(!summary["scenes"].as_array().unwrap().is_empty());
        assert!(!summary["seasons"].as_array().unwrap().is_empty());

        // Commands, then "keep changes" writes the state back into the project.
        let commands = br#"[{"type":"sleep"}]"#;
        assert_eq!(
            call(|out| unsafe { fe_player_commands(player, commands.as_ptr(), commands.len(), out) }).0,
            FeResult::Ok
        );
        let (result, synced) = call(|out| unsafe { fe_player_synced_project(player, out) });
        assert_eq!(result, FeResult::Ok);
        let synced: serde_json::Value = serde_json::from_slice(&synced).unwrap();
        assert_eq!(synced["currentDay"], 2);
        assert_eq!(synced["player"]["money"].as_f64().unwrap(), money + 500.0);

        // The HUD's Inventory button is a widget the host can find (tests click it).
        let query = br#"{"type":"widgetRect","path":["hud","inventory"]}"#;
        let (result, rect) = call(|out| unsafe { fe_player_query_json(player, query.as_ptr(), query.len(), out) });
        assert_eq!(result, FeResult::Ok, "{}", String::from_utf8_lossy(&rect));
        unsafe { fe_player_free(player) };
        unsafe { fe_player_free(std::ptr::null_mut()) };
    }

    #[test]
    fn bad_requests_report_errors_without_poisoning() {
        let project = starter_project();
        let player = new_player(project.as_bytes(), "");
        for request in [r#"{"dt":0,"width":0,"height":10}"#, r#"{"dt":0,"width":100000,"height":100000}"#, "{oops"] {
            let (result, message) =
                call(|out| unsafe { fe_player_frame(player, request.as_ptr(), request.len(), out) });
            assert_eq!(result, FeResult::InvalidArgument, "{request}");
            assert!(!message.is_empty());
        }
        let bad = br#"{"type":"nope"}"#;
        assert_eq!(
            call(|out| unsafe { fe_player_debug(player, bad.as_ptr(), bad.len(), out) }).0,
            FeResult::InvalidArgument
        );
        frame(player, r#"{"dt":0,"width":16,"height":16}"#);
        unsafe { fe_player_free(player) };

        let mut out: *mut FePlayer = std::ptr::null_mut();
        let mut error = FeBytes::empty();
        let bad = b"{not json";
        let result = unsafe { fe_player_new(bad.as_ptr(), bad.len(), std::ptr::null(), 0, &mut out, &mut error) };
        assert_eq!(result, FeResult::InvalidArgument);
        assert!(String::from_utf8(unsafe { take(error) }).unwrap().starts_with("project JSON"));
        assert!(out.is_null());
    }

    #[test]
    fn a_cartridge_player_has_no_project_to_write_back() {
        let cart = include_bytes!("../../../fixtures/golden/cartridges/project-v8.cart");
        let player = new_player(cart, "");
        frame(player, r#"{"dt":0,"width":64,"height":64}"#);
        let (result, message) = call(|out| unsafe { fe_player_synced_project(player, out) });
        assert_eq!(result, FeResult::InvalidArgument);
        assert!(String::from_utf8(message).unwrap().contains("not started from an editor project"));
        unsafe { fe_player_free(player) };
    }
}
