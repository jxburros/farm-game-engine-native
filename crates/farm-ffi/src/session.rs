//! Engine sessions over the C ABI (docs/LANGUAGES.md "FFI: .NET → Rust").
//!
//! Compatibility phase: sessions accept web-compatible project JSON or the F# FlatBuffers
//! cartridge. Commands and effects cross as JSON arrays, and views are stable JSON. The call
//! shape stays coarse, handle-based and batched; Rust allocates results and .NET frees them
//! with `fe_bytes_free`.
//!
//! A session is used by one thread at a time. Panics are caught at the boundary: the session
//! is then *poisoned* (its state may be half-updated) and every later call answers
//! `FeResult::Poisoned`; the host drops it and reports `fe_session_last_error`.

use crate::{view_json, FeBytes, FeResult};
use farm_cart::save_file::{self, SaveTarget};
use farm_runtime::{host, input, panels, timestep::FixedTimestep};
use farm_sim::commands::Command;
use farm_sim::engine_types::EngineContext;
use farm_sim::hooks::HookBus;
use farm_sim::schema::{GameProject, GameState};
use farm_sim::Presentation;
use farm_sim::{engine, game_time, hash, overlay, quests, stable_json, state};
use std::panic::{catch_unwind, AssertUnwindSafe};

/// An opaque engine session: immutable content plus the live state.
pub struct FeSession {
    ctx: EngineContext,
    state: GameState,
    /// The editor project the session started from; `None` for a cartridge, which carries no
    /// project (its `presentation` has what the runtime views need).
    project: Option<GameProject>,
    presentation: Presentation,
    /// Which game this session's saves belong to (header of [`fe_session_save`]).
    target: SaveTarget,
    /// The state as the host last received it through [`fe_session_state_changes`], so the next
    /// call sends only the sections that changed since. `None` until the first call.
    host_view: Option<GameState>,
    poisoned: bool,
    last_error: String,
    runtime: RuntimeState,
}

#[derive(Debug, Default)]
struct RuntimeState {
    timestep: FixedTimestep,
    generation: u64,
    minigame: Option<(farm_sim::schema::MinigameSession, host::HostedMinigame)>,
}

impl RuntimeState {
    fn sync_minigame(&mut self, state: &GameState) {
        if self.minigame.as_ref().is_some_and(|(active, _)| state.minigame.as_ref() != Some(active)) {
            self.minigame = None;
        }
    }
}

#[derive(serde::Deserialize)]
#[serde(tag = "type", rename_all = "camelCase", rename_all_fields = "camelCase")]
enum RuntimeRequest {
    PrepareFrame { input: host::InputFrame, seconds: f64, host_modal_open: bool },
    PollFrame { input: host::InputFrame, host_modal_open: bool },
    Panels { host_modal_open: bool },
    Calendar,
    Snapshot { scene_id: String, options: farm_render::SnapshotOptions },
    AudioCues { effects: Vec<farm_sim::Effect> },
    MountMinigame { random: f64 },
    MinigameInput { generation: u64, input: host::MinigameInput },
    DisposeMinigame { generation: u64 },
}

/// Batched runtime operations. Input is polled after simulation ticks so a modal opened
/// during that tick blocks same-frame actions, just as in the reference shell.
///
/// # Safety
/// `session` comes from [`fe_session_new`]; `request` points to `len` readable bytes;
/// `out` is a valid output pointer. Calls must be serialized with other session calls.
#[no_mangle]
pub unsafe extern "C" fn fe_session_runtime_json(
    session: *mut FeSession,
    request: *const u8,
    len: usize,
    out: *mut FeBytes,
) -> FeResult {
    write_empty(out);
    let Some(bytes) = bytes_arg(request, len) else { return FeResult::InvalidArgument };
    with_session(session, out, |s| {
        let request: RuntimeRequest = serde_json::from_slice(bytes).map_err(|e| format!("runtime request: {e}"))?;
        match request {
            RuntimeRequest::PrepareFrame { input, seconds, host_modal_open } => Ok(view_json::to_json(
                &host::prepare_frame(&mut s.runtime.timestep, &input, &s.state, seconds, host_modal_open)?,
            )),
            RuntimeRequest::PollFrame { input, host_modal_open } => Ok(view_json::to_json(&input::poll_play_frame(
                &input.tracker(),
                &s.state,
                &s.ctx.content,
                host_modal_open,
            ))),
            RuntimeRequest::Panels { host_modal_open } => {
                let views = panels::render(
                    s.presentation.game_panels.iter(),
                    &panels::PanelState::from_game_state(&s.state, host_modal_open),
                );
                Ok(view_json::to_json(&views))
            }
            RuntimeRequest::Calendar => Ok(view_json::to_json(&host::calendar_view(&s.ctx.content, &s.state))),
            RuntimeRequest::Snapshot { scene_id, options } => {
                let scene = s
                    .state
                    .world
                    .scenes
                    .iter()
                    .find(|scene| scene.id == scene_id)
                    .ok_or("Snapshot scene not found.")?;
                Ok(view_json::to_json(&farm_render::shell_snapshot(&s.ctx.content, &s.state, scene, &options)))
            }
            RuntimeRequest::AudioCues { effects } => {
                Ok(view_json::to_json(&effects.iter().map(farm_runtime::audio::sfx_for_effect).collect::<Vec<_>>()))
            }
            RuntimeRequest::MountMinigame { random } => {
                let active = s.state.minigame.as_ref().ok_or("No active minigame.")?;
                let definition = s.ctx.content.minigames.iter().find(|def| def.id == active.minigame_id);
                let mounted = host::HostedMinigame::mount(definition, random)?;
                let view = mounted.view();
                s.runtime.generation += 1;
                s.runtime.minigame = Some((active.clone(), mounted));
                Ok(view_json::to_json(&serde_json::json!({"generation": s.runtime.generation, "view": view})))
            }
            RuntimeRequest::MinigameInput { generation, input } => {
                let (active, mounted) = s.runtime.minigame.as_mut().ok_or("Minigame is not mounted.")?;
                if generation != s.runtime.generation || s.state.minigame.as_ref() != Some(active) {
                    return Err("Minigame input belongs to an expired session.".to_owned());
                }
                Ok(view_json::to_json(&mounted.apply(input)?))
            }
            RuntimeRequest::DisposeMinigame { generation } => {
                if generation == s.runtime.generation {
                    s.runtime.minigame = None;
                }
                Ok("null".to_owned())
            }
        }
    })
}

impl std::fmt::Debug for FeSession {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FeSession").field("poisoned", &self.poisoned).finish_non_exhaustive()
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

unsafe fn bytes_arg<'a>(ptr: *const u8, len: usize) -> Option<&'a [u8]> {
    if ptr.is_null() {
        if len == 0 {
            Some(&[])
        } else {
            None
        }
    } else {
        Some(std::slice::from_raw_parts(ptr, len))
    }
}

unsafe fn write_bytes(out: *mut FeBytes, text: String) {
    if !out.is_null() {
        *out = FeBytes::from_vec(text.into_bytes());
    }
}

unsafe fn write_empty(out: *mut FeBytes) {
    if !out.is_null() {
        *out = FeBytes::empty();
    }
}

/// Creates a session from migrated project JSON or a verified `FGCT` cartridge.
/// `seed` may be empty (then the project's own `id:gameStartTime` seed applies, like the TS
/// engine). `auto_start_quests` does what hosts do at game start.
///
/// # Safety
/// `project_json` must point to `len` readable bytes; `seed` to `seed_len` bytes (or be null with
/// `seed_len == 0`); `out` and `error` must be valid pointers. The session is freed with
/// [`fe_session_free`].
#[no_mangle]
pub unsafe extern "C" fn fe_session_new(
    project_json: *const u8,
    len: usize,
    seed: *const u8,
    seed_len: usize,
    auto_start_quests: bool,
    out: *mut *mut FeSession,
    error: *mut FeBytes,
) -> FeResult {
    write_empty(error);
    if out.is_null() {
        return FeResult::InvalidArgument;
    }
    *out = std::ptr::null_mut();
    let (Some(project_bytes), Some(seed_bytes)) = (bytes_arg(project_json, len), bytes_arg(seed, seed_len)) else {
        return FeResult::InvalidArgument;
    };
    let seed_text = match std::str::from_utf8(seed_bytes) {
        Ok(s) => s,
        Err(_) => return FeResult::InvalidArgument,
    };
    let result = catch_unwind(AssertUnwindSafe(|| -> Result<FeSession, String> {
        let seed = if seed_text.is_empty() { None } else { Some(seed_text) };
        let (project, presentation, content, mut game_state, target) = if farm_cart::is_cartridge(project_bytes) {
            let cart = farm_cart::load_cartridge(project_bytes)?;
            let target = SaveTarget::for_cartridge(&cart);
            let game_state = state::create_game_state_from_start(&cart.start, seed);
            (None, cart.presentation, cart.content, game_state, target)
        } else {
            let project: GameProject =
                serde_json::from_slice(project_bytes).map_err(|e| format!("project JSON: {e}"))?;
            let content = state::create_content_from_project(&project);
            let game_state = state::create_game_state(&project, seed);
            let target = SaveTarget::for_project(&project, &content);
            let presentation = Presentation::from_project(&project);
            (Some(project), presentation, content, game_state, target)
        };
        let ctx = EngineContext::with_hooks(content, HookBus::new());
        if auto_start_quests {
            quests::auto_start_quests(&ctx, &mut game_state);
        }
        Ok(FeSession {
            ctx,
            state: game_state,
            project,
            presentation,
            target,
            host_view: None,
            poisoned: false,
            last_error: String::new(),
            runtime: RuntimeState::default(),
        })
    }));
    match result {
        Ok(Ok(session)) => {
            *out = Box::into_raw(Box::new(session));
            FeResult::Ok
        }
        Ok(Err(message)) => {
            write_bytes(error, message);
            FeResult::InvalidArgument
        }
        Err(payload) => {
            write_bytes(error, panic_message(payload));
            FeResult::Panic
        }
    }
}

/// Frees a session. Null is a no-op.
///
/// # Safety
/// `session` must have come from [`fe_session_new`] and must not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn fe_session_free(session: *mut FeSession) {
    if !session.is_null() {
        drop(Box::from_raw(session));
    }
}

unsafe fn with_session<F>(session: *mut FeSession, out: *mut FeBytes, body: F) -> FeResult
where
    F: FnOnce(&mut FeSession) -> Result<String, String>,
{
    write_empty(out);
    if session.is_null() {
        return FeResult::InvalidArgument;
    }
    let session = &mut *session;
    if session.poisoned {
        return FeResult::Poisoned;
    }
    match catch_unwind(AssertUnwindSafe(|| body(session))) {
        Ok(Ok(text)) => {
            write_bytes(out, text);
            FeResult::Ok
        }
        Ok(Err(message)) => {
            session.last_error = message;
            FeResult::InvalidArgument
        }
        Err(payload) => {
            session.poisoned = true;
            session.last_error = panic_message(payload);
            FeResult::Panic
        }
    }
}

/// Applies a JSON array of commands (`[{"type":"move","dir":"up"}, …]`) in order and returns the
/// effects of all of them as a JSON array.
///
/// # Safety
/// `session` from [`fe_session_new`]; `commands_json` points to `len` bytes; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_apply(
    session: *mut FeSession,
    commands_json: *const u8,
    len: usize,
    out: *mut FeBytes,
) -> FeResult {
    let Some(bytes) = bytes_arg(commands_json, len) else {
        write_empty(out);
        return FeResult::InvalidArgument;
    };
    with_session(session, out, |s| {
        let commands: Vec<Command> = serde_json::from_slice(bytes).map_err(|e| format!("commands JSON: {e}"))?;
        let mut effects = Vec::new();
        for command in &commands {
            effects.extend(engine::apply_command(&s.ctx, &mut s.state, command));
            s.runtime.sync_minigame(&s.state);
        }
        Ok(stable_json::stringify(&effects))
    })
}

/// Advances `ticks` simulation ticks and returns their effects as a JSON array.
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_tick(session: *mut FeSession, ticks: u32, out: *mut FeBytes) -> FeResult {
    with_session(session, out, |s| {
        let effects = engine::advance_tick(&s.ctx, &mut s.state, f64::from(ticks));
        s.runtime.sync_minigame(&s.state);
        Ok(stable_json::stringify(&effects))
    })
}

/// The full state as stable JSON (the debug drawer, saves, and the differential tests).
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_state_json(session: *mut FeSession, out: *mut FeBytes) -> FeResult {
    with_session(session, out, |s| Ok(stable_json::stringify(&s.state)))
}

/// The state hash (`hashState`), 16 hex characters.
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_hash(session: *mut FeSession, out: *mut FeBytes) -> FeResult {
    with_session(session, out, |s| Ok(hash::hash_state(&s.state)))
}

/// The project with the live state written back (`applyStateToProject`), as stable JSON.
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_project_json(session: *mut FeSession, out: *mut FeBytes) -> FeResult {
    with_session(session, out, |s| {
        let project = s.project.as_ref().ok_or("A cartridge session has no editor project to write back to.")?;
        Ok(stable_json::stringify(&state::apply_state_to_project(project, &s.state)))
    })
}

/// Read-only overlay queries as one JSON object. No live state is copied or changed.
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_overlay_json(session: *mut FeSession, out: *mut FeBytes) -> FeResult {
    with_session(session, out, |s| Ok(view_json::to_json(&overlay::overlay_view(&s.ctx, &s.state))))
}

/// Creator debug action: run the same overnight pass as a sleep command without requiring
/// the player to be at a bed. Effects are intentionally discarded, as in the debug drawer.
/// Hook events remain available through [`fe_session_hook_events`].
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_skip_day(session: *mut FeSession, out: *mut FeBytes) -> FeResult {
    with_session(session, out, |s| {
        game_time::perform_sleep(&s.ctx, &mut s.state, game_time::SleepOptions { collapsed: false });
        Ok(String::new())
    })
}

/// Drains the hook events emitted since the last drain, as a JSON array of
/// `{"hook":"onDayStart","payload":{…}}` objects (the plugin host feeds them to plugins).
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_hook_events(session: *mut FeSession, out: *mut FeBytes) -> FeResult {
    // Engine order, not sorted: plugins see payload keys in the order the C# engine sends them.
    with_session(session, out, |s| Ok(view_json::to_json(&s.ctx.drain_hook_events())))
}

/// Replaces the live state with `state_json` (a `GameState`, as the debug drawer's creator
/// tools edit it). Unlike [`fe_session_load_save`] nothing is migrated or quarantined: the
/// state is taken as it is. Invalid JSON leaves the state untouched and answers
/// `InvalidArgument` with the reason in [`fe_session_last_error`].
///
/// # Safety
/// `session` from [`fe_session_new`]; `state_json` points to `len` bytes; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_set_state(
    session: *mut FeSession,
    state_json: *const u8,
    len: usize,
    out: *mut FeBytes,
) -> FeResult {
    let Some(bytes) = bytes_arg(state_json, len) else {
        write_empty(out);
        return FeResult::InvalidArgument;
    };
    with_session(session, out, |s| {
        s.state = serde_json::from_slice(bytes).map_err(|e| format!("state JSON: {e}"))?;
        s.runtime.sync_minigame(&s.state);
        Ok(String::new())
    })
}

/// Calls `$apply!` with every top-level `GameState` field and its JSON key, in declaration
/// order. The `state_changes_cover_every_section` test fails when a field is missing here.
macro_rules! state_sections {
    ($apply:ident) => {
        $apply! {
            meta => "meta",
            clock => "clock",
            world => "world",
            player => "player",
            npcs => "npcs",
            quests => "quests",
            dialogue => "dialogue",
            shop => "shop",
            minigame => "minigame",
            shop_purchases_today => "shopPurchasesToday",
            social => "social",
            animals => "animals",
            mine => "mine",
            flags => "flags",
            quarantined_items => "quarantinedItems",
            rng => "rng",
        }
    };
}

/// The top-level `GameState` sections that changed since the previous call, as one JSON object
/// (`{"clock":{…},"player":{…}}`; `{}` when nothing changed). Each section is compared by value
/// with what the host was sent last time, so an unchanged section costs a comparison and no
/// serialization. `full` sends every section (the host's first read, or when it lost track).
/// Values are in engine order ([`view_json`]): this feeds the host's live mirror of the state.
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_state_changes(session: *mut FeSession, full: bool, out: *mut FeBytes) -> FeResult {
    with_session(session, out, |s| {
        if full {
            s.host_view = None;
        }
        Ok(state_changes(&mut s.host_view, &s.state))
    })
}

/// Writes the sections of `state` that differ from `view` (all of them when `view` is `None`)
/// and brings `view` up to date.
fn state_changes(view: &mut Option<GameState>, state: &GameState) -> String {
    let mut out = String::from("{");
    let mut first = true;
    let mut member = |out: &mut String, key: &str, value: &dyn erased::Section| {
        if !first {
            out.push(',');
        }
        first = false;
        value.push_member(out, key);
    };
    match view {
        None => {
            macro_rules! all {
                ($($field:ident => $key:literal),* $(,)?) => {$(
                    member(&mut out, $key, &state.$field);
                )*};
            }
            state_sections!(all);
            *view = Some(state.clone());
        }
        Some(seen) => {
            macro_rules! changed {
                ($($field:ident => $key:literal),* $(,)?) => {$(
                    if seen.$field != state.$field {
                        member(&mut out, $key, &state.$field);
                        seen.$field.clone_from(&state.$field);
                    }
                )*};
            }
            state_sections!(changed);
        }
    }
    out.push('}');
    out
}

mod erased {
    use crate::view_json;
    use serde::Serialize;

    /// A serializable state section behind `dyn` (so one closure writes every section type).
    pub trait Section {
        fn push_member(&self, out: &mut String, key: &str);
    }

    impl<T: Serialize> Section for T {
        fn push_member(&self, out: &mut String, key: &str) {
            view_json::push_member(out, key, self);
        }
    }
}

/// A save file for the live state: `{"header": {…}, "state": {…}}` as stable JSON (see
/// `farm_cart::save_file`).
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_save(session: *mut FeSession, out: *mut FeBytes) -> FeResult {
    with_session(session, out, |s| Ok(save_file::write_save(&s.state, &s.target)))
}

/// Loads a save file (or a bare web `GameState`) into the session, replacing its state. Old
/// saves are migrated and items the content no longer has are quarantined. Returns
/// `{"warnings": […], "quarantined": […], "restored": […], "fromVersion": n, "migrated": b}`; a
/// refused save leaves the state untouched and answers `InvalidArgument` with the reasons in
/// [`fe_session_last_error`].
///
/// # Safety
/// `session` from [`fe_session_new`]; `save` points to `len` bytes; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_load_save(
    session: *mut FeSession,
    save: *const u8,
    len: usize,
    out: *mut FeBytes,
) -> FeResult {
    let Some(bytes) = bytes_arg(save, len) else {
        write_empty(out);
        return FeResult::InvalidArgument;
    };
    with_session(session, out, |s| {
        let text = std::str::from_utf8(bytes).map_err(|e| format!("save file is not UTF-8: {e}"))?;
        let loaded = save_file::load_save(text, &s.target, &s.ctx.content);
        let Some(state) = loaded.state.filter(|_| loaded.ok) else {
            return Err(loaded.errors.join("\n"));
        };
        s.state = state;
        s.runtime.minigame = None;
        s.runtime.timestep.reset();
        let report = serde_json::json!({
            "warnings": loaded.warnings,
            "quarantined": loaded.quarantined,
            "restored": loaded.restored,
            "fromVersion": loaded.from_version,
            "migrated": loaded.migrated,
        });
        Ok(stable_json::stringify_value(&report))
    })
}

/// The message of the last error or panic on this session (empty when none).
///
/// # Safety
/// `session` from [`fe_session_new`] (poisoned sessions are fine); `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_last_error(session: *mut FeSession, out: *mut FeBytes) -> FeResult {
    write_empty(out);
    if session.is_null() {
        return FeResult::InvalidArgument;
    }
    write_bytes(out, (*session).last_error.clone());
    FeResult::Ok
}

#[cfg(test)]
mod tests {
    use super::*;

    fn starter_project() -> Vec<u8> {
        let path: std::path::PathBuf =
            [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", "content", "starter-farm.json"]
                .iter()
                .collect();
        let text = std::fs::read_to_string(path).expect("fixture");
        let fixture: serde_json::Value = serde_json::from_str(&text).expect("json");
        serde_json::to_vec(&fixture["project"]).expect("project")
    }

    unsafe fn take(bytes: FeBytes) -> String {
        if bytes.ptr.is_null() {
            return String::new();
        }
        let text = String::from_utf8(std::slice::from_raw_parts(bytes.ptr, bytes.len).to_vec()).unwrap();
        crate::fe_bytes_free(bytes);
        text
    }

    #[test]
    fn creates_a_session_and_hashes_the_created_state() {
        let project = starter_project();
        let seed = "content:starter-farm";
        let mut session: *mut FeSession = std::ptr::null_mut();
        let mut error = FeBytes::empty();
        let result = unsafe {
            fe_session_new(project.as_ptr(), project.len(), seed.as_ptr(), seed.len(), false, &mut session, &mut error)
        };
        assert_eq!(result, FeResult::Ok, "{}", unsafe { take(error) });
        let mut out = FeBytes::empty();
        assert_eq!(unsafe { fe_session_hash(session, &mut out) }, FeResult::Ok);
        let hash = unsafe { take(out) };
        // The content golden records hashState(createGameState(project, "content:starter-farm")).
        let path: std::path::PathBuf =
            [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", "content", "starter-farm.json"]
                .iter()
                .collect();
        let fixture: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap();
        assert_eq!(hash, fixture["stateHash"].as_str().unwrap());
        unsafe { fe_session_free(session) };
    }

    /// Runs one `out`-returning call and takes its buffer.
    fn call(f: impl FnOnce(*mut FeBytes) -> FeResult) -> (FeResult, String) {
        let mut out = FeBytes::empty();
        let result = f(&mut out);
        (result, unsafe { take(out) })
    }

    #[test]
    fn saves_and_loads_through_the_c_abi() {
        let project = starter_project();
        let seed = "save-ffi";
        let mut session: *mut FeSession = std::ptr::null_mut();
        let mut error = FeBytes::empty();
        let result = unsafe {
            fe_session_new(project.as_ptr(), project.len(), seed.as_ptr(), seed.len(), false, &mut session, &mut error)
        };
        assert_eq!(result, FeResult::Ok, "{}", unsafe { take(error) });
        let hash = || call(|out| unsafe { fe_session_hash(session, out) });
        let (_, hash_before) = hash();
        let (result, save) = call(|out| unsafe { fe_session_save(session, out) });
        assert_eq!(result, FeResult::Ok);
        assert!(save.starts_with("{\"header\":{"), "{save}");

        // Change the state, then load the save back over it.
        let sleep = br#"[{"type":"sleep"}]"#;
        assert_eq!(call(|out| unsafe { fe_session_apply(session, sleep.as_ptr(), sleep.len(), out) }).0, FeResult::Ok);
        assert_ne!(hash().1, hash_before);
        let (result, report) = call(|out| unsafe { fe_session_load_save(session, save.as_ptr(), save.len(), out) });
        assert_eq!(result, FeResult::Ok);
        assert_eq!(report, r#"{"fromVersion":4,"migrated":false,"quarantined":[],"restored":[],"warnings":[]}"#);
        assert_eq!(hash().1, hash_before);

        // A refused save keeps the state and reports why.
        let foreign = save.replacen(r#""gameId":""#, r#""gameId":"other-"#, 1);
        let (result, _) = call(|out| unsafe { fe_session_load_save(session, foreign.as_ptr(), foreign.len(), out) });
        assert_eq!(result, FeResult::InvalidArgument);
        let (_, message) = call(|out| unsafe { fe_session_last_error(session, out) });
        assert!(message.starts_with("This save belongs to a different game"), "{message}");
        assert_eq!(hash().1, hash_before);
        unsafe { fe_session_free(session) };
    }

    fn new_session(seed: &str) -> *mut FeSession {
        let project = starter_project();
        let mut session: *mut FeSession = std::ptr::null_mut();
        let mut error = FeBytes::empty();
        let result = unsafe {
            fe_session_new(project.as_ptr(), project.len(), seed.as_ptr(), seed.len(), false, &mut session, &mut error)
        };
        assert_eq!(result, FeResult::Ok, "{}", unsafe { take(error) });
        session
    }

    #[test]
    fn replaces_the_state_for_debug_tools() {
        let session = new_session("set-state");
        let (_, state) = call(|out| unsafe { fe_session_state_json(session, out) });
        let mut value: serde_json::Value = serde_json::from_str(&state).unwrap();
        value["player"]["money"] = serde_json::json!(12345);
        let edited = value.to_string();
        assert_eq!(
            call(|out| unsafe { fe_session_set_state(session, edited.as_ptr(), edited.len(), out) }).0,
            FeResult::Ok
        );
        let (_, after) = call(|out| unsafe { fe_session_state_json(session, out) });
        assert_eq!(after, stable_json::stringify_value(&value));

        // Broken JSON keeps the state and reports why.
        let broken = br#"{"player": 5}"#;
        let (result, _) = call(|out| unsafe { fe_session_set_state(session, broken.as_ptr(), broken.len(), out) });
        assert_eq!(result, FeResult::InvalidArgument);
        let (_, message) = call(|out| unsafe { fe_session_last_error(session, out) });
        assert!(message.starts_with("state JSON"), "{message}");
        assert_eq!(call(|out| unsafe { fe_session_state_json(session, out) }).1, after);
        unsafe { fe_session_free(session) };
    }

    #[test]
    fn state_changes_cover_every_section() {
        let session = new_session("changes");
        let changes = |full: bool| {
            let (result, text) = call(|out| unsafe { fe_session_state_changes(session, full, out) });
            assert_eq!(result, FeResult::Ok);
            serde_json::from_str::<serde_json::Map<String, serde_json::Value>>(&text).unwrap()
        };

        // The first read sends every section, in declaration order, equal to the whole state.
        let first = changes(false);
        let (_, state) = call(|out| unsafe { fe_session_state_json(session, out) });
        let whole: serde_json::Map<String, serde_json::Value> = serde_json::from_str(&state).unwrap();
        let mut keys: Vec<&String> = first.keys().collect();
        let mut expected: Vec<&String> = whole.keys().collect();
        keys.sort();
        expected.sort();
        assert_eq!(keys, expected);
        assert_eq!(
            stable_json::stringify_value(&serde_json::Value::Object(first.clone())),
            stable_json::stringify_value(&serde_json::Value::Object(whole))
        );

        // Nothing happened: nothing to send.
        assert!(changes(false).is_empty());

        // A tick moves the clock but leaves the metadata alone.
        assert_eq!(call(|out| unsafe { fe_session_tick(session, 3, out) }).0, FeResult::Ok);
        let after_tick = changes(false);
        assert!(after_tick.contains_key("clock"), "{after_tick:?}");
        assert!(!after_tick.contains_key("meta"), "{after_tick:?}");
        assert!(changes(false).is_empty());

        // A replaced state shows up like any other change; `full` resends everything.
        let (_, state) = call(|out| unsafe { fe_session_state_json(session, out) });
        let mut value: serde_json::Value = serde_json::from_str(&state).unwrap();
        value["player"]["money"] = serde_json::json!(7);
        let edited = value.to_string();
        assert_eq!(
            call(|out| unsafe { fe_session_set_state(session, edited.as_ptr(), edited.len(), out) }).0,
            FeResult::Ok
        );
        let after_set = changes(false);
        assert_eq!(after_set.keys().collect::<Vec<_>>(), vec!["player"]);
        assert_eq!(after_set["player"]["money"], serde_json::json!(7));
        assert_eq!(changes(true).len(), first.len());
        unsafe { fe_session_free(session) };
    }

    #[test]
    fn rejects_bad_json_without_panicking() {
        let mut session: *mut FeSession = std::ptr::null_mut();
        let mut error = FeBytes::empty();
        let bad = b"{not json";
        let result =
            unsafe { fe_session_new(bad.as_ptr(), bad.len(), std::ptr::null(), 0, false, &mut session, &mut error) };
        assert_eq!(result, FeResult::InvalidArgument);
        assert!(unsafe { take(error) }.starts_with("project JSON"));
        assert!(session.is_null());
    }
}
