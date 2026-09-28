//! Rust twin of the retired C# `GoldenParityTests.cs::ReplayMatchesTypeScript`.
//!
//! Every fixture under `fixtures/golden/replays` was recorded from the TypeScript reference
//! engine; the Rust port must reproduce each state hash, effect list and final state byte for
//! byte. All replays run in one test so the merge wave sees every divergence at once: a replay
//! that panics (a `todo!()` in a module that is still being ported, for instance) is reported
//! with the panic location and message, and the run continues with the next replay.

use farm_sim::replay::ReplayInput;
use farm_sim::schema::GameProject;
use farm_sim::{engine, hash, quests, stable_json, state, EngineContext};
use serde::Serialize;
use serde_json::Value;
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
        .collect();
    names.sort();
    names
}

/// Port of the C# `AssertSameStable`: the first differing character with context on each side.
fn same_stable(what: &str, expected: &str, actual: &str) -> Result<(), String> {
    if expected == actual {
        return Ok(());
    }
    let expected_chars: Vec<char> = expected.chars().collect();
    let actual_chars: Vec<char> = actual.chars().collect();
    let mut at = 0;
    while at < expected_chars.len() && at < actual_chars.len() && expected_chars[at] == actual_chars[at] {
        at += 1;
    }
    let excerpt = |chars: &[char]| -> String {
        let from = at.saturating_sub(300);
        let to = (at + 400).min(chars.len());
        chars[from..to].iter().collect()
    };
    Err(format!(
        "{what}: stable JSON differs at char {at} (expected {} chars, got {}).\nTS:   …{}…\nRust: …{}…",
        expected_chars.len(),
        actual_chars.len(),
        excerpt(&expected_chars),
        excerpt(&actual_chars)
    ))
}

/// Port of the C# `AssertSameJson`: optional stable-JSON equality, then the hash.
fn same_json<T: Serialize>(
    what: &str,
    expected_hash: &str,
    expected_json: Option<&Value>,
    actual: &T,
) -> Result<(), String> {
    let actual_stable = hash::stable_stringify(actual);
    if let Some(json) = expected_json {
        same_stable(what, &stable_json::stringify_value(json), &actual_stable)?;
    }
    let actual_hash = hash::hash_text(&actual_stable);
    if actual_hash != expected_hash {
        return Err(format!("{what}: expected hash {expected_hash}, got {actual_hash}"));
    }
    Ok(())
}

fn truncate_chars(text: &str, max: usize) -> String {
    text.chars().take(max).collect()
}

fn string_field<'a>(value: &'a Value, key: &str) -> Result<&'a str, String> {
    value[key].as_str().ok_or_else(|| format!("fixture has no string '{key}'"))
}

/// One replay, start to finish. `Err` carries the first divergence with the same diagnostics the
/// C# test prints (step index, input, state before the step, both effect lists).
fn run_replay(name: &str) -> Result<(), String> {
    let fixture = golden(&format!("replays/{name}.json"));
    let project: GameProject =
        serde_json::from_value(fixture["project"].clone()).map_err(|e| format!("project does not parse: {e}"))?;

    let content = state::create_content_from_project(&project);
    let expected_content_hash = string_field(&fixture, "contentHash")?;
    let actual_content_hash = hash::hash_state(&content);
    if actual_content_hash != expected_content_hash {
        return Err(format!("content: expected hash {expected_content_hash}, got {actual_content_hash}"));
    }
    let ctx = EngineContext::new(content);

    let seed = fixture["seed"].as_str();
    let mut game_state = state::create_game_state(&project, seed);
    same_json("created state", string_field(&fixture, "createdHash")?, None, &game_state)?;

    if fixture["autoStartQuests"].as_bool() == Some(true) {
        quests::auto_start_quests(&ctx, &mut game_state);
    }
    same_json("initial state", string_field(&fixture, "initialHash")?, Some(&fixture["initialState"]), &game_state)?;

    let steps = fixture["steps"].as_array().ok_or("fixture has no 'steps' array")?;
    for (index, step) in steps.iter().enumerate() {
        let input: ReplayInput = serde_json::from_value(step["input"].clone())
            .map_err(|e| format!("step {index}: input does not parse ({}): {e}", step["input"]))?;
        let before = game_state.clone();
        let effects = match &input {
            ReplayInput::Command { command } => engine::apply_command(&ctx, &mut game_state, command),
            ReplayInput::Tick { ticks } => engine::advance_tick(&ctx, &mut game_state, *ticks),
        };

        let expected_hash = string_field(step, "hash")?;
        let actual_hash = hash::hash_state(&game_state);
        let ts_effects = stable_json::stringify_value(&step["effects"]);
        let rust_effects = stable_json::stringify(&effects);
        if actual_hash != expected_hash {
            return Err(format!(
                "state diverged at step {index} ({}).\nexpected hash {expected_hash}, got {actual_hash}.\n\
                 state before step: {}\nRust effects: {rust_effects}\nTS effects: {ts_effects}",
                step["input"],
                truncate_chars(&hash::stable_stringify(&before), 4000),
            ));
        }
        if ts_effects != rust_effects {
            return Err(format!(
                "effects differ at step {index} ({}).\nTS:   {ts_effects}\nRust: {rust_effects}",
                step["input"]
            ));
        }
    }

    let step_count = fixture["stepCount"].as_f64().ok_or("fixture has no 'stepCount'")?;
    if step_count != steps.len() as f64 {
        return Err(format!("stepCount {step_count} but {} steps ran", steps.len()));
    }
    same_json("final state", string_field(&fixture, "finalHash")?, Some(&fixture["finalState"]), &game_state)?;

    let final_project = state::apply_state_to_project(&project, &game_state);
    same_stable(
        "final project",
        &stable_json::stringify_value(&fixture["finalProject"]),
        &hash::stable_stringify(&final_project),
    )
}

/// Location and message of the most recent panic, recorded by the hook installed below (the
/// payload of a caught panic carries only the message, not where it happened).
static LAST_PANIC: Mutex<Option<String>> = Mutex::new(None);

fn take_last_panic() -> Option<String> {
    LAST_PANIC.lock().map_or(None, |mut last| last.take())
}

#[test]
fn replays_match_the_typescript_engine() {
    let names: Vec<String> = fixture_names("replays").into_iter().filter(|name| name != "index").collect();
    assert!(!names.is_empty(), "no replay fixtures found");

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
        match panic::catch_unwind(AssertUnwindSafe(|| run_replay(name))) {
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
        "{} of {} replays diverged from the TypeScript engine:\n\n{}",
        failures.len(),
        names.len(),
        failures.join("\n\n")
    );
}
