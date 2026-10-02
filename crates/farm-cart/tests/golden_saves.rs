//! Save migrations against the goldens.
//!
//! - `fixtures/golden/v8/saves` holds the saves the TypeScript reference engine migrated (v1–v4
//!   inputs, with its results). They are migration inputs now: every one must still load, keep
//!   its values (up to the v9 quantization) and come out as a v5 state.
//! - `fixtures/golden/saves` holds the same inputs plus a current (v5) and a future (v6) save,
//!   with the results recorded from this engine (`FARM_RECORD_GOLDENS=1` rewrites them).

use farm_cart::migrate_game_state;
use farm_sim::schema::save::CURRENT_SAVE_VERSION;
use farm_sim::{hash_state, stable_stringify};
use serde_json::{json, Value};
use std::path::{Path, PathBuf};

#[path = "../../farm-sim/tests/recording/mod.rs"]
mod recording;

static RECORDER: recording::Recorder = recording::Recorder::new("FARM_RECORD_GOLDENS", "saves");

fn golden_dir(relative: &str) -> PathBuf {
    [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", relative].iter().collect()
}

fn fixtures(dir: &Path) -> Vec<(String, Value)> {
    let mut paths: Vec<PathBuf> = std::fs::read_dir(dir)
        .unwrap_or_else(|e| panic!("list {}: {e}", dir.display()))
        .map(|entry| entry.expect("directory entry").path())
        .filter(|path| path.extension().is_some_and(|ext| ext == "json"))
        .collect();
    paths.sort();
    paths
        .into_iter()
        .map(|path| {
            let name = path.file_stem().expect("stem").to_string_lossy().into_owned();
            let text = std::fs::read_to_string(&path).expect("read fixture");
            (name, serde_json::from_str(&text).expect("valid fixture JSON"))
        })
        .collect()
}

/// Numbers equal up to the v9 grids (1/8192 tile, 1/1000 point, 1e-6 minute, 2⁻³²), keys and
/// other values exactly. `skip` names paths whose values changed on purpose.
fn same_within_grid(path: &str, expected: &Value, actual: &Value, skip: &[&str], out: &mut Vec<String>) {
    if skip.contains(&path) {
        return;
    }
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
                same_within_grid(
                    &child,
                    a.get(key).unwrap_or(&Value::Null),
                    b.get(key).unwrap_or(&Value::Null),
                    skip,
                    out,
                );
            }
        }
        (Value::Array(a), Value::Array(b)) if a.len() == b.len() => {
            for (index, (x, y)) in a.iter().zip(b).enumerate() {
                same_within_grid(&format!("{path}.{index}"), x, y, skip, out);
            }
        }
        _ if expected == actual => {}
        _ => out.push(format!("{path}: v8 {expected}, now {actual}")),
    }
}

#[test]
fn every_v8_save_still_loads_with_its_values() {
    let mut failures = Vec::new();
    let saves = fixtures(&golden_dir("v8/saves"));
    assert!(saves.len() >= 8, "expected the v8 save fixtures, found {}", saves.len());
    for (name, fixture) in &saves {
        let expected = &fixture["result"];
        let result = migrate_game_state(&fixture["input"]);
        if name == "save-v5-future" {
            // v8 refused saveVersion 5 as newer than itself; v5 is the current version now.
            if !result.ok || result.migrated {
                failures.push(format!("{name}: a v5 save should load as current ({:?})", result.errors));
            }
            continue;
        }
        if result.ok != expected["ok"].as_bool().expect("ok") {
            failures
                .push(format!("{name}: ok = {} (errors {:?}), v8 said {}", result.ok, result.errors, expected["ok"]));
            continue;
        }
        if Some(result.from_version) != expected["fromVersion"].as_f64() {
            failures.push(format!("{name}: fromVersion {} vs v8 {}", result.from_version, expected["fromVersion"]));
        }
        if !result.ok {
            let errors: Vec<&str> =
                expected["errors"].as_array().expect("errors").iter().filter_map(Value::as_str).collect();
            if result.errors != errors {
                failures.push(format!("{name}: errors {:?} vs v8 {errors:?}", result.errors));
            }
            continue;
        }
        // Every v8 save migrates now (to v5).
        if !result.migrated {
            failures.push(format!("{name}: a v8 save must migrate to v{CURRENT_SAVE_VERSION}"));
        }
        let data = result.data.expect("ok result has data");
        assert_eq!(data.meta.save_version, CURRENT_SAVE_VERSION);
        let v8: Value = serde_json::from_str(fixture["stable"].as_str().expect("stable")).expect("v8 stable JSON");
        let now: Value = serde_json::from_str(&stable_stringify(&data)).expect("stable JSON");
        let mut differences = Vec::new();
        // `clock.dayOfSeason` is newer than v8 (#23); loading with the game's calendar fills it.
        same_within_grid("", &v8, &now, &[".meta.saveVersion", ".clock.dayOfSeason"], &mut differences);
        if !differences.is_empty() {
            failures.push(format!("{name}: values moved beyond the grid:\n  {}", differences.join("\n  ")));
        }
    }
    assert!(failures.is_empty(), "{}", failures.join("\n\n"));
}

/// The recorded inputs: the v8 inputs (the v5 "future" one excepted), a current v5 save (the
/// v8 v4-current save as this engine writes it) and a future v6 save.
fn recorded_inputs() -> Vec<(String, Value)> {
    let mut inputs: Vec<(String, Value)> = fixtures(&golden_dir("v8/saves"))
        .into_iter()
        .filter(|(name, _)| name != "save-v5-future")
        .map(|(name, fixture)| {
            let name = if name == "save-v4-current" { "save-v4".to_owned() } else { name };
            (name, fixture["input"].clone())
        })
        .collect();
    let v4 = inputs.iter().find(|(name, _)| name == "save-v4").expect("v4 save").1.clone();
    let current = migrate_game_state(&v4).data.expect("the v4 save loads");
    let current = serde_json::to_value(&current).expect("state serializes");
    let mut future = current.clone();
    future["meta"]["saveVersion"] = json!(CURRENT_SAVE_VERSION + 1);
    inputs.push(("save-v5-current".to_owned(), current));
    inputs.push(("save-v6-future".to_owned(), future));
    inputs.sort_by(|a, b| a.0.cmp(&b.0));
    inputs
}

fn record(input: &Value) -> Value {
    let result = migrate_game_state(input);
    let mut fixture = json!({
        "input": input,
        "result": {
            "ok": result.ok,
            "fromVersion": result.from_version,
            "migrated": result.migrated,
            "errors": result.errors,
        },
    });
    if let Some(data) = &result.data {
        fixture["stable"] = Value::String(stable_stringify(data));
        fixture["hash"] = Value::String(hash_state(data));
    }
    fixture
}

#[test]
fn save_migrations_match_the_recorded_goldens() {
    let dir = golden_dir("saves");
    let inputs = recorded_inputs();
    if RECORDER.enabled() {
        for entry in std::fs::read_dir(&dir).expect("list saves") {
            let path = entry.expect("entry").path();
            let stem = path.file_stem().map(|stem| stem.to_string_lossy().into_owned()).unwrap_or_default();
            if path.extension().is_some_and(|ext| ext == "json") && !inputs.iter().any(|(name, _)| *name == stem) {
                std::fs::remove_file(&path).expect("remove old golden");
                RECORDER.removed(&path);
            }
        }
        for (name, input) in &inputs {
            let text = serde_json::to_string_pretty(&record(input)).expect("encode") + "\n";
            RECORDER.write(&dir.join(format!("{name}.json")), text.as_bytes()).expect("write golden");
        }
        RECORDER.finish();
    }
    let recorded = fixtures(&dir);
    let names: Vec<&String> = recorded.iter().map(|(name, _)| name).collect();
    let expected_names: Vec<&String> = inputs.iter().map(|(name, _)| name).collect();
    assert_eq!(names, expected_names, "the recorded saves are the v8 inputs plus v5-current and v6-future");
    let mut failures = Vec::new();
    for (name, fixture) in &recorded {
        let actual = record(&fixture["input"]);
        if &actual != fixture {
            failures.push(format!(
                "{name}: differs from the recorded golden\nrecorded: {}\nnow:      {}",
                fixture["result"], actual["result"]
            ));
            if actual.get("hash") != fixture.get("hash") {
                failures.push(format!("{name}: hash {:?} vs recorded {:?}", actual.get("hash"), fixture.get("hash")));
            }
        }
    }
    assert!(failures.is_empty(), "{}", failures.join("\n\n"));
}
