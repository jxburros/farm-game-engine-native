//! The golden replays.
//!
//! `fixtures/golden/v8/replays` holds the replays the TypeScript reference engine recorded
//! (project, seed and input log, with its hashes, effects and states). Since v9 Rust is the
//! reference (docs/NUMERICS.md "Goldens"): `fixtures/golden/replays` holds the same inputs with
//! the hashes, effects and states this engine produces, recorded with
//! `FARM_RECORD_GOLDENS=1 cargo test -p farm-sim --test golden_replays` and reviewed as a diff.
//! Every replay must reproduce its recorded hash after every step, byte for byte. (The v8
//! replays themselves are still played by `v8_outcomes`, which checks that v9 plays the same
//! game as v8.)
//!
//! All replays run in one test so a change shows every divergence at once: a replay that
//! panics is reported with the panic location and message, and the run continues.

use farm_sim::replay::ReplayInput;
use farm_sim::schema::GameProject;
use farm_sim::{engine, hash, quests, stable_json, state, EngineContext};
use serde::Serialize;
use serde_json::{json, Map, Value};
use std::panic::{self, AssertUnwindSafe};
use std::path::PathBuf;
use std::sync::Mutex;

fn golden_dir(relative: &str) -> PathBuf {
    [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", relative].iter().collect()
}

fn golden(relative: &str) -> Value {
    let path = golden_dir(relative);
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
    serde_json::from_str(&text).unwrap_or_else(|e| panic!("parse {}: {e}", path.display()))
}

/// Fixture names (without `.json`) in a golden subfolder, in ordinal order.
fn fixture_names(folder: &str) -> Vec<String> {
    let dir = golden_dir(folder);
    let mut names: Vec<String> = std::fs::read_dir(&dir)
        .unwrap_or_else(|e| panic!("list {}: {e}", dir.display()))
        .map(|entry| entry.expect("directory entry").path())
        .filter(|path| path.extension().is_some_and(|ext| ext == "json"))
        .map(|path| path.file_stem().expect("file stem").to_string_lossy().into_owned())
        .filter(|name| name != "index")
        .collect();
    names.sort();
    names
}

fn recording() -> bool {
    std::env::var("FARM_RECORD_GOLDENS").is_ok_and(|v| v == "1")
}

/// A value as the fixtures store it: its stable JSON, parsed (keys sorted, JS numbers).
fn stable_value<T: Serialize>(value: &T) -> Value {
    serde_json::from_str(&stable_json::stringify(value)).expect("stable JSON parses")
}

/// Plays one replay's inputs and returns the fixture this engine records for it.
fn play(source: &Value) -> Result<Value, String> {
    let project: GameProject =
        serde_json::from_value(source["project"].clone()).map_err(|e| format!("project does not parse: {e}"))?;
    let content = state::create_content_from_project(&project);
    let content_hash = hash::hash_state(&content);
    let ctx = EngineContext::new(content);

    let seed = source["seed"].as_str();
    let mut game = state::create_game_state(&project, seed);
    let created_hash = hash::hash_state(&game);
    if source["autoStartQuests"].as_bool() == Some(true) {
        quests::auto_start_quests(&ctx, &mut game);
    }
    let initial_hash = hash::hash_state(&game);
    let initial_state = stable_value(&game);

    let inputs = source["steps"].as_array().ok_or("fixture has no 'steps' array")?;
    let mut steps = Vec::with_capacity(inputs.len());
    for (index, step) in inputs.iter().enumerate() {
        let input: ReplayInput = serde_json::from_value(step["input"].clone())
            .map_err(|e| format!("step {index}: input does not parse ({}): {e}", step["input"]))?;
        let effects = match &input {
            ReplayInput::Command { command } => engine::apply_command(&ctx, &mut game, command),
            ReplayInput::Tick { ticks } => engine::advance_tick(&ctx, &mut game, *ticks),
        };
        steps.push(json!({
            "input": step["input"],
            "hash": hash::hash_state(&game),
            "effects": stable_value(&effects),
        }));
    }

    let mut fixture = Map::new();
    for key in ["name", "description", "seed", "autoStartQuests", "project"] {
        fixture.insert(key.to_owned(), source[key].clone());
    }
    fixture.insert("contentHash".to_owned(), Value::String(content_hash));
    fixture.insert("createdHash".to_owned(), Value::String(created_hash));
    fixture.insert("initialHash".to_owned(), Value::String(initial_hash));
    fixture.insert("initialState".to_owned(), initial_state);
    fixture.insert("stepCount".to_owned(), json!(steps.len()));
    fixture.insert("steps".to_owned(), Value::Array(steps));
    fixture.insert("finalHash".to_owned(), Value::String(hash::hash_state(&game)));
    fixture.insert("finalState".to_owned(), stable_value(&game));
    // Keep changes as the hosts do it: the state written into the project JSON as given, so
    // content keeps what the creator typed (docs/NUMERICS.md).
    let final_project = state::apply_state_to_project_json(&source["project"], &game)?;
    fixture.insert("finalProject".to_owned(), stable_value(&final_project));
    Ok(Value::Object(fixture))
}

/// The first place `actual` departs from the recorded fixture: the first step whose hash or
/// effects differ, else the first differing top-level key.
fn first_difference(recorded: &Value, actual: &Value) -> Option<String> {
    for key in ["contentHash", "createdHash", "initialHash"] {
        if recorded[key] != actual[key] {
            return Some(format!("{key}: recorded {}, now {}", recorded[key], actual[key]));
        }
    }
    let (recorded_steps, actual_steps) = (recorded["steps"].as_array(), actual["steps"].as_array());
    if let (Some(recorded_steps), Some(actual_steps)) = (recorded_steps, actual_steps) {
        for (index, (want, got)) in recorded_steps.iter().zip(actual_steps).enumerate() {
            if want["hash"] != got["hash"] {
                return Some(format!(
                    "state diverged at step {index} ({}): recorded hash {}, now {}",
                    want["input"], want["hash"], got["hash"]
                ));
            }
            if want["effects"] != got["effects"] {
                return Some(format!(
                    "effects differ at step {index} ({}).\nrecorded: {}\nnow:      {}",
                    want["input"], want["effects"], got["effects"]
                ));
            }
        }
    }
    let keys: Vec<&String> = actual.as_object().map(|map| map.keys().collect()).unwrap_or_default();
    for key in keys {
        if recorded[key.as_str()] != actual[key.as_str()] {
            let text = |value: &Value| value.to_string().chars().take(400).collect::<String>();
            return Some(format!(
                "{key}: recorded {}…, now {}…",
                text(&recorded[key.as_str()]),
                text(&actual[key.as_str()])
            ));
        }
    }
    None
}

fn check(name: &str) -> Result<(), String> {
    let source = golden(&format!("v8/replays/{name}.json"));
    let actual = play(&source)?;
    let path = golden_dir(&format!("replays/{name}.json"));
    if recording() {
        let text = serde_json::to_string(&actual).map_err(|e| e.to_string())? + "\n";
        std::fs::write(&path, text).map_err(|e| format!("write {}: {e}", path.display()))?;
        return Ok(());
    }
    let recorded = golden(&format!("replays/{name}.json"));
    match first_difference(&recorded, &actual) {
        Some(difference) => Err(difference),
        None => Ok(()),
    }
}

/// Location and message of the most recent panic, recorded by the hook installed below (the
/// payload of a caught panic carries only the message, not where it happened).
static LAST_PANIC: Mutex<Option<String>> = Mutex::new(None);

fn take_last_panic() -> Option<String> {
    LAST_PANIC.lock().map_or(None, |mut last| last.take())
}

#[test]
fn replays_match_the_recorded_goldens() {
    let names = fixture_names("v8/replays");
    assert!(!names.is_empty(), "no replay fixtures found");
    if !recording() {
        assert_eq!(fixture_names("replays"), names, "every v8 replay has a recorded v9 golden");
    }

    let previous_hook = panic::take_hook();
    panic::set_hook(Box::new(|info| {
        let location = info.location().map(|l| format!("{}:{}", l.file(), l.line())).unwrap_or_default();
        let payload = info.payload();
        let message = payload
            .downcast_ref::<&str>()
            .map(|s| (*s).to_owned())
            .or_else(|| payload.downcast_ref::<String>().cloned())
            .unwrap_or_default();
        if let Ok(mut last) = LAST_PANIC.lock() {
            *last = Some(format!("panicked at {location}: {message}"));
        }
    }));

    let mut failures = Vec::new();
    for name in &names {
        let _ = take_last_panic();
        match panic::catch_unwind(AssertUnwindSafe(|| check(name))) {
            Ok(Ok(())) => println!("{name}: ok"),
            Ok(Err(message)) => failures.push(format!("{name}: {message}")),
            Err(_) => {
                let detail = take_last_panic().unwrap_or_else(|| "panicked".to_owned());
                failures.push(format!("{name}: {detail}"));
            }
        }
    }
    panic::set_hook(previous_hook);

    assert!(
        failures.is_empty(),
        "{} of {} replays diverged from their recorded goldens:\n\n{}",
        failures.len(),
        names.len(),
        failures.join("\n\n")
    );
}
