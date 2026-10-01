//! The v8 → v9 comparison (docs/NUMERICS.md "Goldens").
//!
//! Schema v9 moved the simulation from JavaScript doubles to integers in fixed units, so the
//! state hashes changed; what must not change is the game a player sees. Before the switch, the
//! v8 engine played every golden replay and recorded, per step, the outcomes a player can see
//! (money, energy, inventory, calendar, quests, flags, player tile, NPCs, friendship, animals,
//! crops and tile contents, effects) into `fixtures/golden/v8/outcomes`. This test plays the same
//! replays on the current engine and compares, step by step.
//!
//! The summaries are built from the state's JSON (authoring units), so they do not depend on how
//! the engine stores numbers. The files were recorded once, with `FARM_RECORD_V8_OUTCOMES=1`, by
//! the v8 engine; never re-record them with a later engine.

use farm_sim::replay::{self, ReplayInput};
use farm_sim::schema::GameProject;
use farm_sim::{quests, stable_json, state, EngineContext};
use serde_json::{json, Map, Value};
use std::path::PathBuf;

fn golden_dir(relative: &str) -> PathBuf {
    [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", relative].iter().collect()
}

fn replay_names() -> Vec<String> {
    let dir = golden_dir("v8/replays");
    let dir = if dir.exists() { dir } else { golden_dir("replays") };
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

fn replay_fixture(name: &str) -> Value {
    let v8 = golden_dir(&format!("v8/replays/{name}.json"));
    let path = if v8.exists() { v8 } else { golden_dir(&format!("replays/{name}.json")) };
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
    serde_json::from_str(&text).unwrap_or_else(|e| panic!("parse {}: {e}", path.display()))
}

fn floor(value: &Value) -> Value {
    match value.as_f64() {
        Some(number) => json!(number.floor()),
        None => Value::Null,
    }
}

/// A number as the player reads it: whole numbers stay whole, others keep three decimals.
fn shown(value: &Value) -> Value {
    match value.as_f64() {
        Some(number) => {
            let milli = (number * 1000.0).round() / 1000.0;
            json!(milli)
        }
        None => value.clone(),
    }
}

fn tile_summary(tile: &Value) -> Option<Value> {
    let mut out = Map::new();
    if let Some(crop) = tile.get("crop") {
        out.insert(
            "crop".to_owned(),
            json!([
                crop["type"],
                crop["stage"],
                crop.get("withered").cloned().unwrap_or(Value::Bool(false)),
                crop["quality"],
                crop["mutation"],
                crop["watered"],
                crop["harvestCount"],
                crop.get("daysGrown").cloned().unwrap_or(Value::Null),
            ]),
        );
    }
    if let Some(item) = tile.get("item") {
        out.insert("item".to_owned(), item["id"].clone());
    }
    if let Some(node) = tile.get("node") {
        out.insert("node".to_owned(), json!([node["typeId"], node["remainingHealth"]]));
    }
    if let Some(machine) = tile.get("machine") {
        out.insert(
            "machine".to_owned(),
            json!([
                machine["typeId"],
                machine.get("processing").map(|p| p["recipeId"].clone()).unwrap_or(Value::Null),
                machine.get("output").cloned().unwrap_or(Value::Null),
            ]),
        );
    }
    if tile.get("ladderDown") == Some(&Value::Bool(true)) {
        out.insert("ladder".to_owned(), Value::Bool(true));
    }
    if let Some(soil) = tile.get("soilState") {
        out.insert("soil".to_owned(), soil.clone());
    }
    if out.is_empty() {
        None
    } else {
        out.insert("type".to_owned(), tile["type"].clone());
        Some(Value::Object(out))
    }
}

/// The player-visible outcome of a state, as a flat object of comparable parts.
fn summarize(state: &Value, effects: &Value) -> Map<String, Value> {
    let player = &state["player"];
    let clock = &state["clock"];
    let mut out = Map::new();
    out.insert("money".to_owned(), player["money"].clone());
    out.insert("energy".to_owned(), json!([shown(&player["energy"]), shown(&player["maxEnergy"])]));
    out.insert(
        "inventory".to_owned(),
        Value::Array(
            player["inventory"]
                .as_array()
                .map(|slots| {
                    slots
                        .iter()
                        .map(|slot| {
                            json!([
                                slot["item"]["id"],
                                slot["quantity"],
                                slot["item"].get("durability").cloned().unwrap_or(Value::Null)
                            ])
                        })
                        .collect()
                })
                .unwrap_or_default(),
        ),
    );
    out.insert("skills".to_owned(), player["skills"].clone());
    out.insert("activeQuests".to_owned(), player["activeQuests"].clone());
    out.insert("completedQuests".to_owned(), player["completedQuests"].clone());
    out.insert("equippedTool".to_owned(), player.get("equippedTool").cloned().unwrap_or(Value::Null));
    out.insert(
        "calendar".to_owned(),
        json!([clock["day"], clock["season"], clock["year"], clock["weatherId"], floor(&clock["timeMinutes"])]),
    );
    out.insert(
        "player".to_owned(),
        json!([player["sceneId"], floor(&player["x"]), floor(&player["y"]), player["direction"]]),
    );
    out.insert("quests".to_owned(), state["quests"].clone());
    out.insert("flags".to_owned(), state["flags"].clone());
    out.insert(
        "npcs".to_owned(),
        Value::Object(
            state["npcs"]
                .as_object()
                .map(|npcs| {
                    npcs.iter()
                        .map(|(id, npc)| (id.clone(), json!([npc["sceneId"], floor(&npc["x"]), floor(&npc["y"])])))
                        .collect()
                })
                .unwrap_or_default(),
        ),
    );
    out.insert("social".to_owned(), state["social"].clone());
    out.insert(
        "animals".to_owned(),
        Value::Array(
            state["animals"]
                .as_array()
                .map(|animals| {
                    animals
                        .iter()
                        .map(|a| {
                            json!([
                                a["id"],
                                a["sceneId"],
                                floor(&a["x"]),
                                floor(&a["y"]),
                                shown(&a["mood"]),
                                a["fedToday"],
                                a["pettedToday"],
                                a["ageDays"],
                                a["productReady"]
                            ])
                        })
                        .collect()
                })
                .unwrap_or_default(),
        ),
    );
    out.insert("mine".to_owned(), state["mine"].clone());
    out.insert(
        "open".to_owned(),
        json!([state["dialogue"], state["shop"], state["minigame"].get("minigameId").cloned().unwrap_or(Value::Null)]),
    );
    out.insert("quarantined".to_owned(), state["quarantinedItems"].clone());
    let mut tiles = Map::new();
    if let Some(scenes) = state["world"]["scenes"].as_array() {
        for scene in scenes {
            let scene_id = scene["id"].as_str().unwrap_or_default();
            for row in scene["tiles"].as_array().into_iter().flatten() {
                for tile in row.as_array().into_iter().flatten() {
                    if let Some(summary) = tile_summary(tile) {
                        let x = tile["x"].as_f64().unwrap_or(f64::NAN);
                        let y = tile["y"].as_f64().unwrap_or(f64::NAN);
                        tiles.insert(format!("{scene_id}:{x},{y}"), summary);
                    }
                }
            }
        }
    }
    out.insert("tiles".to_owned(), Value::Object(tiles));
    out.insert("effects".to_owned(), effects.clone());
    out.into_iter().map(|(key, value)| (key, normalize(value))).collect()
}

/// Every number as a double, so `5` (an integer field) and `5.0` (a v8 double) compare equal.
fn normalize(value: Value) -> Value {
    match value {
        Value::Number(number) => json!(number.as_f64().unwrap_or(f64::NAN)),
        Value::Array(items) => Value::Array(items.into_iter().map(normalize).collect()),
        Value::Object(map) => Value::Object(map.into_iter().map(|(key, value)| (key, normalize(value))).collect()),
        other => other,
    }
}

/// Plays one replay and returns the summary after creation (index 0) and after every step.
fn play(name: &str) -> Vec<Map<String, Value>> {
    let fixture = replay_fixture(name);
    let mut project: GameProject = serde_json::from_value(fixture["project"].clone()).expect("project parses");
    // Intended divergence: v8 ran the clock while a dialogue, shop or minigame was open. Since
    // `time.pauseInModals` (on unless a project turns it off) it stops; v8 projects never set
    // it, so they are compared with it off.
    project.settings.time.pause_in_modals = Some(false);
    let ctx = EngineContext::new(state::create_content_from_project(&project));
    let mut game_state = state::create_game_state(&project, fixture["seed"].as_str());
    if fixture["autoStartQuests"].as_bool() == Some(true) {
        quests::auto_start_quests(&ctx, &mut game_state);
    }
    let to_json = |state: &farm_sim::GameState| serde_json::to_value(state).expect("state serializes");
    let mut summaries = vec![summarize(&to_json(&game_state), &json!([]))];
    for step in fixture["steps"].as_array().expect("steps") {
        let input: ReplayInput = serde_json::from_value(step["input"].clone()).expect("input parses");
        let result = replay::run_replay(&ctx, &mut game_state, std::slice::from_ref(&input));
        let effects: Value = serde_json::from_str(&stable_json::stringify(&result.effects)).expect("effects");
        summaries.push(summarize(&to_json(&game_state), &effects));
    }
    summaries
}

/// Stores the summaries as the first one in full, then per step only the parts that changed
/// (tiles per tile, `null` for a tile that emptied).
fn encode(summaries: &[Map<String, Value>]) -> Value {
    let mut steps = Vec::new();
    let mut previous: Option<&Map<String, Value>> = None;
    for (index, summary) in summaries.iter().enumerate() {
        let mut delta = Map::new();
        delta.insert("step".to_owned(), json!(index));
        for (key, value) in summary {
            let before = previous.and_then(|p| p.get(key));
            if before == Some(value) {
                continue;
            }
            if key == "tiles" {
                if let (Some(Value::Object(old)), Value::Object(new)) = (before, value) {
                    let mut changes = Map::new();
                    for (tile, content) in new {
                        if old.get(tile) != Some(content) {
                            changes.insert(tile.clone(), content.clone());
                        }
                    }
                    for tile in old.keys() {
                        if !new.contains_key(tile) {
                            changes.insert(tile.clone(), Value::Null);
                        }
                    }
                    delta.insert("tilesChanged".to_owned(), Value::Object(changes));
                    continue;
                }
            }
            delta.insert(key.clone(), value.clone());
        }
        steps.push(Value::Object(delta));
        previous = Some(summary);
    }
    Value::Array(steps)
}

fn decode(encoded: &Value) -> Vec<Map<String, Value>> {
    let mut out: Vec<Map<String, Value>> = Vec::new();
    let mut current = Map::new();
    for delta in encoded.as_array().expect("outcome steps") {
        for (key, value) in delta.as_object().expect("step object") {
            match key.as_str() {
                "step" => {}
                "tilesChanged" => {
                    let tiles = current.entry("tiles").or_insert_with(|| json!({}));
                    let tiles = tiles.as_object_mut().expect("tiles object");
                    for (tile, content) in value.as_object().expect("tile changes") {
                        if content.is_null() {
                            tiles.shift_remove(tile);
                        } else {
                            tiles.insert(tile.clone(), content.clone());
                        }
                    }
                }
                _ => {
                    current.insert(key.clone(), value.clone());
                }
            }
        }
        out.push(current.iter().map(|(key, value)| (key.clone(), normalize(value.clone()))).collect());
    }
    out
}

fn outcome_path(name: &str) -> PathBuf {
    golden_dir(&format!("v8/outcomes/{name}.json"))
}

/// Tiles compared one by one, so a difference names the tile.
fn differences(expected: &Map<String, Value>, actual: &Map<String, Value>) -> Vec<String> {
    let mut out = Vec::new();
    for (key, want) in expected {
        let got = actual.get(key).unwrap_or(&Value::Null);
        if key == "tiles" {
            let empty = Map::new();
            let want = want.as_object().unwrap_or(&empty);
            let got = got.as_object().unwrap_or(&empty);
            for (tile, content) in want {
                if got.get(tile) != Some(content) {
                    out.push(format!("tile {tile}: v8 {content}, now {}", got.get(tile).unwrap_or(&Value::Null)));
                }
            }
            for (tile, content) in got {
                if !want.contains_key(tile) {
                    out.push(format!("tile {tile}: v8 empty, now {content}"));
                }
            }
        } else if want != got {
            out.push(format!("{key}: v8 {want}, now {got}"));
        }
    }
    out
}

#[test]
fn replays_play_the_same_game_as_v8() {
    let record = std::env::var("FARM_RECORD_V8_OUTCOMES").is_ok_and(|v| v == "1");
    let mut report = Vec::new();
    for name in replay_names() {
        let summaries = play(&name);
        let path = outcome_path(&name);
        if record {
            std::fs::create_dir_all(path.parent().expect("parent")).expect("create outcomes dir");
            let text = serde_json::to_string(&encode(&summaries)).expect("encode");
            std::fs::write(&path, text + "\n").expect("write outcomes");
            continue;
        }
        let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
        let expected = decode(&serde_json::from_str(&text).expect("outcomes parse"));
        if expected.len() != summaries.len() {
            report.push(format!("{name}: {} steps recorded, {} played", expected.len(), summaries.len()));
            continue;
        }
        let mut lines = Vec::new();
        for (index, (want, got)) in expected.iter().zip(&summaries).enumerate() {
            for difference in differences(want, got) {
                lines.push(format!("  step {index}: {difference}"));
            }
        }
        if !lines.is_empty() {
            let total = lines.len();
            lines.truncate(40);
            report.push(format!("{name}: {total} differences\n{}", lines.join("\n")));
        }
    }
    assert!(report.is_empty(), "replays differ from v8:\n{}", report.join("\n"));
}
