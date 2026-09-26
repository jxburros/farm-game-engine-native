//! Engine sessions over the C ABI (docs/LANGUAGES.md "FFI: .NET → Rust").
//!
//! Compatibility phase: the cartridge is the project JSON the web version writes, commands and
//! effects cross as JSON arrays, and views are stable JSON. FlatBuffers replace the JSON
//! buffers when the F# compiler produces cartridges (phase 3). The call shape stays the same:
//! coarse, handle-based, batched; Rust allocates results, .NET frees them with `fe_bytes_free`.
//!
//! A session is used by one thread at a time. Panics are caught at the boundary: the session
//! is then *poisoned* (its state may be half-updated) and every later call answers
//! `FeResult::Poisoned`; the host drops it and reports `fe_session_last_error`.

use crate::{FeBytes, FeResult};
use farm_sim::commands::Command;
use farm_sim::engine_types::EngineContext;
use farm_sim::hooks::HookBus;
use farm_sim::schema::{GameProject, GameState};
use farm_sim::{engine, hash, quests, stable_json, state};
use std::panic::{catch_unwind, AssertUnwindSafe};

/// An opaque engine session: immutable content plus the live state.
pub struct FeSession {
    ctx: EngineContext,
    state: GameState,
    project: GameProject,
    poisoned: bool,
    last_error: String,
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

/// Creates a session from project JSON (the web version's format, schema v8, already migrated).
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
        let project: GameProject = serde_json::from_slice(project_bytes).map_err(|e| format!("project JSON: {e}"))?;
        let content = state::create_content_from_project(&project);
        let ctx = EngineContext::with_hooks(content, HookBus::new());
        let mut game_state =
            state::create_game_state(&project, if seed_text.is_empty() { None } else { Some(seed_text) });
        if auto_start_quests {
            quests::auto_start_quests(&ctx, &mut game_state);
        }
        Ok(FeSession { ctx, state: game_state, project, poisoned: false, last_error: String::new() })
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
    with_session(session, out, |s| Ok(stable_json::stringify(&state::apply_state_to_project(&s.project, &s.state))))
}

/// Drains the hook events emitted since the last drain, as a JSON array of
/// `{"hook":"onDayStart","payload":{…}}` objects (the plugin host feeds them to plugins).
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_hook_events(session: *mut FeSession, out: *mut FeBytes) -> FeResult {
    with_session(session, out, |s| Ok(stable_json::stringify(&s.ctx.drain_hook_events())))
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
