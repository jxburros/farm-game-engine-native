//! F# ↔ Rust parity (#117, #118).
//!
//! The project, content and start schema exists twice: as the F# records of the authoring core
//! (`src/FarmEngine.Authoring/Schema.fs`, `SchemaJson.fs`) and as the serde types here. Both
//! sides can also turn a project into what a game runs on: F# for cartridges
//! (`ContentCompiler`, `CartridgeCompiler`), Rust for the editor's previews and headless sessions
//! (`create_content_from_project`, `StartState::from_project`, `Presentation::from_project`).
//!
//! `fixtures/parity/*.json` are recorded by the F# side (`ParityTests.fs`, `FARM_RECORD_PARITY=1`):
//!
//! - `kitchen-sink.json` / `kitchen-sink-content.json`: a project and a content value with every
//!   field of every F# record set. Rust must read every field and write it back the same, so a
//!   field added on one side only fails here.
//! - every other file: a project (a template, a fixture project, an example pack installed) with
//!   what the F# pipeline compiles from it. Rust's pipeline must give the same content, start
//!   and presentation: compared after both pass through the Rust types, exactly as a cartridge
//!   loads.
//!
//! The reverse direction (F# against content Rust records) is `ParityTests.fs` over
//! `fixtures/golden/content`.

use farm_sim::schema::{GameContent, GameProject};
use farm_sim::{create_content_from_project, Presentation, StartState};
use serde::de::DeserializeOwned;
use serde::Serialize;
use serde_json::Value;
use std::path::PathBuf;

fn parity_dir() -> PathBuf {
    [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "parity"].iter().collect()
}

/// Every parity fixture, by file stem, in ordinal order.
fn fixtures() -> Vec<(String, Value)> {
    let dir = parity_dir();
    let mut files: Vec<PathBuf> = std::fs::read_dir(&dir)
        .unwrap_or_else(|e| panic!("list {}: {e}", dir.display()))
        .map(|entry| entry.expect("directory entry").path())
        .filter(|path| path.extension().is_some_and(|ext| ext == "json"))
        .collect();
    files.sort();
    assert!(files.len() > 10, "expected the F#-recorded parity fixtures in {}", dir.display());
    files
        .into_iter()
        .map(|path| {
            let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
            let name = path.file_stem().expect("file stem").to_string_lossy().into_owned();
            (name, serde_json::from_str(&text).unwrap_or_else(|e| panic!("parse {}: {e}", path.display())))
        })
        .collect()
}

/// The differences between two JSON trees (at most 40). Numbers may differ by what the
/// fixed-point grids make of them (docs/NUMERICS.md: 0.03 on the 2⁻³² chance grid, a speed on
/// 1/8192 of a tile a tick) when `grid` is set; otherwise they must be equal.
fn differences(path: &str, expected: &Value, actual: &Value, grid: bool, out: &mut Vec<String>) {
    const LIMIT: usize = 40;
    if out.len() >= LIMIT {
        return;
    }
    match (expected, actual) {
        (Value::Number(a), Value::Number(b)) => {
            let (a, b) = (a.as_f64().unwrap_or(f64::NAN), b.as_f64().unwrap_or(f64::NAN));
            let close = a == b || (grid && (a - b).abs() <= 0.002_f64.max(1e-6 * a.abs()));
            if !close {
                out.push(format!("{path}: {a} / {b}"));
            }
        }
        (Value::Object(a), Value::Object(b)) => {
            for key in a.keys().chain(b.keys().filter(|key| !a.contains_key(*key))) {
                match (a.get(key), b.get(key)) {
                    (Some(x), Some(y)) => differences(&format!("{path}.{key}"), x, y, grid, out),
                    (Some(x), None) => out.push(format!("{path}.{key}: {} / (absent)", short(x))),
                    (None, Some(y)) => out.push(format!("{path}.{key}: (absent) / {}", short(y))),
                    (None, None) => {}
                }
            }
        }
        (Value::Array(a), Value::Array(b)) if a.len() == b.len() => {
            for (index, (x, y)) in a.iter().zip(b).enumerate() {
                differences(&format!("{path}[{index}]"), x, y, grid, out);
            }
        }
        _ if expected == actual => {}
        _ => out.push(format!("{path}: {} / {}", short(expected), short(actual))),
    }
}

/// Every difference (up to the limit), one per line.
fn first_difference(path: &str, expected: &Value, actual: &Value, grid: bool) -> Option<String> {
    let mut out = Vec::new();
    differences(path, expected, actual, grid, &mut out);
    (!out.is_empty()).then(|| out.join("\n  "))
}

fn short(value: &Value) -> String {
    let text = value.to_string();
    if text.chars().count() > 300 {
        format!("{}…", text.chars().take(300).collect::<String>())
    } else {
        text
    }
}

/// A value read into the Rust type `T` and written back.
fn reencode<T: DeserializeOwned + Serialize>(what: &str, value: &Value) -> Result<(T, Value), String> {
    let typed: T =
        serde_json::from_value(value.clone()).map_err(|e| format!("{what}: Rust cannot read F#'s JSON: {e}"))?;
    let written = serde_json::to_value(&typed).map_err(|e| format!("{what}: {e}"))?;
    Ok((typed, written))
}

/// A recorded section with each `{"$project": key}` (a value F# copies from the project
/// unchanged, recorded once) replaced by the project's value.
fn resolve(section: &Value, project: &Value) -> Value {
    match section {
        Value::Object(members) => Value::Object(
            members
                .iter()
                .map(|(key, value)| {
                    let value = match value.get("$project").and_then(Value::as_str) {
                        Some(from) if value.as_object().is_some_and(|o| o.len() == 1) => {
                            project.get(from).cloned().unwrap_or(Value::Null)
                        }
                        _ => value.clone(),
                    };
                    (key.clone(), value)
                })
                .collect(),
        ),
        other => other.clone(),
    }
}

/// F# wrote `value` from its records; Rust reads it and writes back the same JSON.
fn same_schema<T: DeserializeOwned + Serialize>(what: &str, value: &Value) -> Result<T, String> {
    let (typed, written) = reencode::<T>(what, value)?;
    match first_difference(what, value, &written, true) {
        Some(difference) => Err(format!("{what} (F# / Rust re-encoded): {difference}")),
        None => Ok(typed),
    }
}

/// The section F# compiled, read as a cartridge reads it, against what Rust builds.
fn same_output<T: DeserializeOwned + Serialize>(what: &str, fsharp: &Value, rust: &T) -> Result<(), String> {
    let (_, fsharp) = reencode::<T>(what, fsharp)?;
    let rust = serde_json::to_value(rust).map_err(|e| e.to_string())?;
    match first_difference(what, &fsharp, &rust, false) {
        Some(difference) => Err(format!("{what} (F# / Rust): {difference}")),
        None => Ok(()),
    }
}

#[test]
fn rust_reads_and_writes_every_field_the_fsharp_records_have() {
    let mut failures = Vec::new();
    for (name, fixture) in fixtures() {
        if let Some(project) = fixture.get("project") {
            if let Err(error) = same_schema::<GameProject>(&format!("{name}: project"), project) {
                failures.push(error);
            }
        }
        if name == "kitchen-sink-content" {
            if let Err(error) = same_schema::<GameContent>(&format!("{name}: content"), &fixture["content"]) {
                failures.push(error);
            }
        }
    }
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}

#[test]
fn the_rust_pipeline_compiles_what_the_fsharp_pipeline_compiles() {
    let mut failures = Vec::new();
    let mut checked = 0;
    for (name, fixture) in fixtures() {
        let Some(project_json) = fixture.get("project") else { continue };
        let project: GameProject = match serde_json::from_value(project_json.clone()) {
            Ok(project) => project,
            Err(error) => {
                failures.push(format!("{name}: Rust cannot read the project: {error}"));
                continue;
            }
        };
        let content = resolve(&fixture["content"], project_json);
        let start = resolve(&fixture["start"], project_json);
        let presentation = resolve(&fixture["presentation"], project_json);
        // On purpose: a cartridge ships only the art the game uses (docs/EXPORT.md), a preview
        // keeps the whole library. The art both have must match.
        let shipped: Vec<&str> = presentation["customAssets"]
            .as_array()
            .map(|assets| assets.iter().filter_map(|asset| asset["id"].as_str()).collect())
            .unwrap_or_default();
        let mut rust_presentation = Presentation::from_project(&project);
        rust_presentation.custom_assets.retain(|asset| shipped.contains(&asset.id.as_str()));
        let results = [
            same_output(&format!("{name}: content"), &content, &create_content_from_project(&project)),
            same_output(&format!("{name}: start"), &start, &StartState::from_project(&project)),
            same_output(&format!("{name}: presentation"), &presentation, &rust_presentation),
        ];
        failures.extend(results.into_iter().filter_map(Result::err));
        checked += 1;
    }
    assert!(checked > 10, "only {checked} pipeline fixtures");
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}
