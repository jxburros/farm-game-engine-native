//! The data types, content assembly and state creation against the goldens.
//!
//! - `fixtures/golden/v8/{content,replays}` hold the TypeScript engine's fixtures. Their projects
//!   are migration inputs now: each must still load, and content assembly must give what the v8
//!   engine gave up to the v9 grids (docs/NUMERICS.md).
//! - `fixtures/golden/content` holds the same projects with what this engine records for them
//!   (`projectStable`, content `stable`, `contentHash`, `stateHash`), rewritten with
//!   `FARM_RECORD_GOLDENS=1 cargo test -p farm-sim --test golden_content`.
//!
//! The sharp tool is the stable-JSON comparison: when it differs, the first differing character
//! is reported with 200 characters of context on each side.

use farm_sim::schema::{GameProject, GameState};
use farm_sim::{hash_state, stable_json, state};
use serde_json::{json, Value};
use std::path::PathBuf;

mod recording;

static RECORDER: recording::Recorder = recording::Recorder::new("FARM_RECORD_GOLDENS", "content");

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
        let from = at.saturating_sub(200);
        let to = (at + 200).min(chars.len());
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

fn same_hash(what: &str, expected: &Value, actual: &str) -> Result<(), String> {
    let expected = expected.as_str().unwrap_or_default();
    if expected == actual {
        Ok(())
    } else {
        Err(format!("{what}: expected hash {expected}, got {actual}"))
    }
}

/// Equal JSON trees, numbers up to the v9 grids (1/8192 tile, 1/1000 point, 1e-6 minute, 2⁻³²).
fn within_grid(path: &str, expected: &Value, actual: &Value, out: &mut Vec<String>) {
    match (expected, actual) {
        (Value::Number(a), Value::Number(b)) => {
            let (a, b) = (a.as_f64().unwrap_or(f64::NAN), b.as_f64().unwrap_or(f64::NAN));
            if (a - b).abs() > 1e-3 {
                out.push(format!("{path}: v8 {a}, now {b}"));
            }
        }
        (Value::Object(a), Value::Object(b)) => {
            for key in a.keys().chain(b.keys().filter(|key| !a.contains_key(*key))) {
                let child = format!("{path}.{key}");
                within_grid(&child, a.get(key).unwrap_or(&Value::Null), b.get(key).unwrap_or(&Value::Null), out);
            }
        }
        (Value::Array(a), Value::Array(b)) if a.len() == b.len() => {
            for (index, (x, y)) in a.iter().zip(b).enumerate() {
                within_grid(&format!("{path}.{index}"), x, y, out);
            }
        }
        _ if expected == actual => {}
        _ => out.push(format!("{path}: v8 {expected}, now {actual}")),
    }
}

fn same_within_grid(what: &str, expected: &Value, actual: &Value) -> Result<(), String> {
    let mut out = Vec::new();
    within_grid("", expected, actual, &mut out);
    if out.is_empty() {
        Ok(())
    } else {
        out.truncate(20);
        Err(format!("{what}: differs beyond the v9 grids:\n  {}", out.join("\n  ")))
    }
}

fn parse_stable(text: &Value) -> Value {
    serde_json::from_str(text.as_str().unwrap_or("null")).unwrap_or(Value::Null)
}

/// A v8 project (a TypeScript golden input) still loads: it deserializes, and writing it back
/// changes nothing beyond moving numbers onto their grids.
fn v8_project(name: &str, project: &Value) -> Result<GameProject, String> {
    let typed: GameProject =
        serde_json::from_value(project.clone()).map_err(|e| format!("{name}: deserialize project: {e}"))?;
    let written = serde_json::to_value(&typed).map_err(|e| e.to_string())?;
    same_within_grid(&format!("{name} project round-trip"), project, &written)?;
    Ok(typed)
}

fn check_v8_content(name: &str) -> Result<(), String> {
    let fixture = golden(&format!("v8/content/{name}.json"));
    let project = v8_project(name, &fixture["project"])?;
    let content = state::create_content_from_project(&project);
    let now: Value = serde_json::from_str(&stable_json::stringify(&content)).map_err(|e| e.to_string())?;
    same_within_grid(&format!("{name} content"), &parse_stable(&fixture["stable"]), &now)
}

fn check_v8_replay_project(name: &str) -> Result<(), String> {
    let fixture = golden(&format!("v8/replays/{name}.json"));
    v8_project(name, &fixture["project"]).map(|_| ())
}

/// What this engine records for a content fixture's project.
fn record_content(name: &str, project_json: &Value) -> Result<Value, String> {
    let project: GameProject =
        serde_json::from_value(project_json.clone()).map_err(|e| format!("{name}: deserialize project: {e}"))?;
    let content = state::create_content_from_project(&project);
    let created = state::create_game_state(&project, Some(&format!("content:{name}")));
    Ok(json!({
        "name": name,
        "project": project_json,
        "projectStable": stable_json::stringify(&project),
        "stable": stable_json::stringify(&content),
        "contentHash": hash_state(&content),
        "stateHash": hash_state(&created),
    }))
}

fn check_content_fixture(name: &str) -> Result<(), String> {
    if RECORDER.enabled() {
        let source = golden(&format!("v8/content/{name}.json"));
        let fixture = record_content(name, &source["project"])?;
        let path = golden_dir(&format!("content/{name}.json"));
        let text = serde_json::to_string(&fixture).map_err(|e| e.to_string())? + "\n";
        return RECORDER.write(&path, text.as_bytes());
    }
    let fixture = golden(&format!("content/{name}.json"));
    let actual = record_content(name, &fixture["project"])?;
    for key in ["projectStable", "stable"] {
        same_stable(
            &format!("{name} {key}"),
            fixture[key].as_str().unwrap_or_default(),
            actual[key].as_str().unwrap_or_default(),
        )?;
    }
    same_hash(
        &format!("{name} contentHash"),
        &fixture["contentHash"],
        actual["contentHash"].as_str().unwrap_or_default(),
    )?;
    same_hash(&format!("{name} stateHash"), &fixture["stateHash"], actual["stateHash"].as_str().unwrap_or_default())
}

fn check_save_fixture(name: &str) -> Result<(), String> {
    let fixture = golden(&format!("saves/{name}.json"));
    if fixture["result"]["ok"] != Value::Bool(true) {
        return Ok(());
    }
    let stable = fixture["stable"].as_str().unwrap_or_default();
    let typed: GameState = serde_json::from_str(stable).map_err(|e| format!("{name}: deserialize save: {e}"))?;
    same_stable(&format!("{name} save round-trip"), stable, &stable_json::stringify(&typed))?;
    same_hash(&format!("{name} save hash"), &fixture["hash"], &hash_state(&typed))
}

/// Runs every fixture and reports all failures at once, so one broken type does not hide the
/// others.
fn check_all(folder: &str, check: fn(&str) -> Result<(), String>) {
    let names = fixture_names(folder);
    assert!(!names.is_empty(), "no fixtures under fixtures/golden/{folder}");
    let checked: Vec<&String> = names.iter().filter(|name| name.as_str() != "index").collect();
    let failures: Vec<String> = checked.iter().filter_map(|name| check(name).err()).collect();
    eprintln!(
        "checked {} {folder} fixtures: {}",
        checked.len(),
        checked.iter().map(|s| s.as_str()).collect::<Vec<_>>().join(", ")
    );
    assert!(
        failures.is_empty(),
        "{} of {} {folder} fixtures failed:\n\n{}",
        failures.len(),
        checked.len(),
        failures.join("\n\n")
    );
}

#[test]
fn every_v8_content_project_still_loads_and_assembles_the_same_content() {
    check_all("v8/content", check_v8_content);
}

#[test]
fn every_v8_replay_project_still_loads() {
    check_all("v8/replays", check_v8_replay_project);
}

#[test]
fn content_assembly_matches_the_recorded_goldens() {
    check_all("v8/content", check_content_fixture);
    if RECORDER.enabled() {
        RECORDER.finish();
    }
}

#[test]
fn saves_round_trip_exactly() {
    check_all("saves", check_save_fixture);
}
