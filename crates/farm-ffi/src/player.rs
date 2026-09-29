//! The graphical player (`farm_player::Player`) over the C ABI: the editor's Play Mode.
//!
//! The editor hands the player the project it is editing (or a compiled cartridge), forwards
//! raw input events and the size of its surface, and shows the frames. All of Play Mode's HUD,
//! dialogue, shop, crafting, inventory, quest, minigame and toast UI is drawn in Rust by
//! `farm-ui`; the editor keeps only its own tools (restart, keep changes, the debug drawer),
//! which reach the game through [`fe_player_debug`] and [`fe_player_synced_project`]. The
//! requests and answers are `farm_host::player`'s, shared with farm-wasm.
//!
//! Same conventions as the sessions: Rust allocates results and .NET frees them with
//! `fe_bytes_free`; panics never unwind into .NET. On failure `out` holds the UTF-8 error
//! message. A player is used by one thread at a time (the editor runs it on a worker thread).

use crate::{bytes_arg, write, write_empty, FeBytes, FeResult};
use farm_host::player::{is_engine_failure, FrameRequest, PlayerCreate};
use farm_host::{Guarded, HostPlayer};
use farm_player::speaker::SpeakerThread;
use farm_player::PlayerOptions;

/// An opaque embedded player.
pub struct FePlayer {
    player: Guarded<HostPlayer>,
    /// The output device when the editor asked for sound.
    speaker: Option<SpeakerThread>,
}

impl std::fmt::Debug for FePlayer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FePlayer").field("poisoned", &self.player.is_poisoned()).finish_non_exhaustive()
    }
}

/// Creates an embedded player for a (migrated) project's JSON or a compiled cartridge.
/// `options` is `{"seed"?, "reducedMotion"?, "uiScale"?, "audio"?, "locale"?}` (may be empty); with
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
    let result = farm_host::catch(|| {
        let mut create = PlayerCreate::parse(options)?;
        // Play Mode: the editor's accessibility option always wins (off unless it asks).
        create.reduced_motion.get_or_insert(false);
        let player = HostPlayer::new(game, &create, PlayerOptions::embedded())?;
        let speaker = create.audio.then(SpeakerThread::start);
        Ok(FePlayer { player: Guarded::new(player).poisoning_on(is_engine_failure), speaker })
    });
    match result {
        Ok(player) => {
            *out = Box::into_raw(Box::new(player));
            FeResult::Ok
        }
        Err(e) => {
            write(error, e.message.into_bytes());
            e.kind.into()
        }
    }
}

unsafe fn with_player<F>(player: *mut FePlayer, out: *mut FeBytes, body: F) -> FeResult
where
    F: FnOnce(&mut HostPlayer, Option<&SpeakerThread>) -> Result<Vec<u8>, String>,
{
    write_empty(out);
    if player.is_null() {
        return FeResult::InvalidArgument;
    }
    let FePlayer { player, speaker } = &mut *player;
    match player.run(|p| body(p, speaker.as_ref())) {
        Ok(bytes) => {
            write(out, bytes);
            FeResult::Ok
        }
        Err(e) => {
            write(out, e.message.into_bytes());
            e.kind.into()
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
    with_player(player, out, |p, speaker| {
        let outcome = p.frame(&FrameRequest::parse(bytes)?)?;
        if let Some(speaker) = speaker {
            for sound in &outcome.sounds {
                speaker.play(sound.clone());
            }
        }
        let json = outcome.info_json().into_bytes();
        let ((width, height), data) = match outcome.size {
            Some(size) => (size, p.pixels()),
            None => ((0, 0), &[][..]),
        };
        let mut out = Vec::with_capacity(12 + json.len() + data.len());
        out.extend_from_slice(&width.to_le_bytes());
        out.extend_from_slice(&height.to_le_bytes());
        out.extend_from_slice(&(json.len() as u32).to_le_bytes());
        out.extend_from_slice(&json);
        out.extend_from_slice(data);
        Ok(out)
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
    with_player(player, out, |p, _| p.debug(bytes).map(|()| Vec::new()))
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
    with_player(player, out, |p, _| p.commands(bytes).map(|()| Vec::new()))
}

/// The live game state as stable JSON.
///
/// # Safety
/// `player` from [`fe_player_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_player_state_json(player: *mut FePlayer, out: *mut FeBytes) -> FeResult {
    with_player(player, out, |p, _| p.state_json().map(String::into_bytes))
}

/// The state hash (`hashState`) of the live game.
///
/// # Safety
/// `player` from [`fe_player_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_player_hash(player: *mut FePlayer, out: *mut FeBytes) -> FeResult {
    with_player(player, out, |p, _| p.hash().map(String::into_bytes))
}

/// The editor project with the live state written back ("keep changes"), as stable JSON.
/// Fails for a player started from a cartridge.
///
/// # Safety
/// `player` from [`fe_player_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_player_synced_project(player: *mut FePlayer, out: *mut FeBytes) -> FeResult {
    with_player(player, out, |p, _| p.synced_project().map(String::into_bytes))
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
    with_player(player, out, |p, _| p.query_json(bytes).map(String::into_bytes))
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
