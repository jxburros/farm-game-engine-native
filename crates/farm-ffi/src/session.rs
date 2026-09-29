//! Engine sessions over the C ABI (docs/LANGUAGES.md "FFI: .NET → Rust").
//!
//! A headless game for tools (the editor's Play Mode uses the graphical player in
//! [`crate::player`] instead): sessions accept web-compatible project JSON or the F# FlatBuffers
//! cartridge. Commands and effects cross as JSON arrays, and state is stable JSON (see
//! `farm_host::session`, shared with farm-wasm). The call shape stays coarse, handle-based and
//! batched; Rust allocates results and .NET frees them with `fe_bytes_free`.
//!
//! A session is used by one thread at a time. Panics are caught at the boundary: the session
//! is then *poisoned* (its state may be half-updated) and every later call answers
//! `FeResult::Poisoned`; the host drops it and reports `fe_session_last_error`.

use crate::{bytes_arg, write, write_empty, FeBytes, FeResult};
use farm_host::{Guarded, HostSession};

/// An opaque engine session: immutable content plus the live state.
pub struct FeSession {
    session: Guarded<HostSession>,
}

impl std::fmt::Debug for FeSession {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FeSession").field("poisoned", &self.session.is_poisoned()).finish_non_exhaustive()
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
    let Ok(seed_text) = std::str::from_utf8(seed_bytes) else {
        return FeResult::InvalidArgument;
    };
    match farm_host::catch(|| HostSession::new(project_bytes, Some(seed_text), auto_start_quests)) {
        Ok(session) => {
            *out = Box::into_raw(Box::new(FeSession { session: Guarded::new(session) }));
            FeResult::Ok
        }
        Err(e) => {
            write(error, e.message.into_bytes());
            e.kind.into()
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

/// Runs `body` on the session. Only successes write `out`; an error's message is kept for
/// [`fe_session_last_error`].
unsafe fn with_session<F>(session: *mut FeSession, out: *mut FeBytes, body: F) -> FeResult
where
    F: FnOnce(&mut HostSession) -> Result<String, String>,
{
    write_empty(out);
    if session.is_null() {
        return FeResult::InvalidArgument;
    }
    match (*session).session.run(body) {
        Ok(text) => {
            write(out, text.into_bytes());
            FeResult::Ok
        }
        Err(e) => e.kind.into(),
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
    with_session(session, out, |s| s.apply(bytes))
}

/// Advances `ticks` simulation ticks and returns their effects as a JSON array.
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_tick(session: *mut FeSession, ticks: u32, out: *mut FeBytes) -> FeResult {
    with_session(session, out, |s| Ok(s.tick(ticks)))
}

/// The full state as stable JSON (the debug drawer, saves, and the differential tests).
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_state_json(session: *mut FeSession, out: *mut FeBytes) -> FeResult {
    with_session(session, out, |s| Ok(s.state_json()))
}

/// The state hash (`hashState`), 16 hex characters.
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_hash(session: *mut FeSession, out: *mut FeBytes) -> FeResult {
    with_session(session, out, |s| Ok(s.hash()))
}

/// The project with the live state written back (`applyStateToProject`), as stable JSON.
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_project_json(session: *mut FeSession, out: *mut FeBytes) -> FeResult {
    with_session(session, out, |s| s.project_json())
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
        s.skip_day();
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
    with_session(session, out, |s| Ok(s.hook_events()))
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
    with_session(session, out, |s| s.set_state(bytes).map(|()| String::new()))
}

/// A save file for the live state: `{"header": {…}, "state": {…}}` as stable JSON (see
/// `farm_cart::save_file`).
///
/// # Safety
/// `session` from [`fe_session_new`]; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_session_save(session: *mut FeSession, out: *mut FeBytes) -> FeResult {
    with_session(session, out, |s| Ok(s.save()))
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
    with_session(session, out, |s| s.load_save(bytes))
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
    write(out, (*session).session.last_error().as_bytes().to_vec());
    FeResult::Ok
}

#[cfg(test)]
mod tests {
    use super::*;
    use farm_sim::stable_json;

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
