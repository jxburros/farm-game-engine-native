//! Save migrations: port of `SaveMigrations.cs` (packages/engine-schemas/src/save.ts,
//! `SAVE_MIGRATIONS` / `migrateGameState`) plus the `GameState` checks from
//! `SchemaValidation.ValidateGameState`.
//!
//! Saves belong to the player's machine, so Rust owns their format and their migrations
//! (docs/LANGUAGES.md "Saves (Rust only)"). In the compatibility phase a save is the
//! `GameState` JSON the web version writes; the migrations run on the raw JSON tree with
//! JavaScript semantics (`??`, object spread), exactly like the TypeScript, and the result is
//! then parsed into [`GameState`] and checked. [`migrate_game_state`] never panics on bad input.

use farm_sim::js;
use farm_sim::schema::primitives::{directions, quest_statuses};
use farm_sim::schema::save::{center_coordinate, SaveMigrationResult, CURRENT_SAVE_VERSION};
use farm_sim::schema::GameState;
use serde_json::{Map, Value};

/// A save migration step: upgrades a version-n state to n+1, in place.
pub type SaveMigration = fn(&mut Map<String, Value>);

/// TS `SAVE_MIGRATIONS`: `(n, step)` upgrades a version-n `GameState` to n+1.
pub const SAVE_MIGRATIONS: &[(f64, SaveMigration)] =
    &[(1.0, migrate_v1_to_v2), (2.0, migrate_v2_to_v3), (3.0, migrate_v3_to_v4)];

/// The most errors a failed parse reports (zod `issues.slice(0, 20)`).
pub(crate) const MAX_ERRORS: usize = 20;

/// Upgrade and validate serialized simulation state. This is deliberately independent from
/// project migrations: authored content and a player's live runtime state evolve on different
/// schedules. Never panics.
pub fn migrate_game_state(raw: &Value) -> SaveMigrationResult {
    if !is_object_like(raw) {
        return fail(0.0, false, "Save state is not an object".to_owned());
    }
    let from_version = get(get(Some(raw), "meta"), "saveVersion").and_then(Value::as_f64).unwrap_or(1.0);
    if from_version > CURRENT_SAVE_VERSION {
        return fail(
            from_version,
            false,
            format!(
                "Save version {} is newer than this engine supports ({}). Update the engine.",
                js::num(from_version),
                js::num(CURRENT_SAVE_VERSION)
            ),
        );
    }

    let migrated = from_version < CURRENT_SAVE_VERSION;
    let mut state = spread(Some(raw));
    let mut version = from_version;
    while version < CURRENT_SAVE_VERSION {
        let Some((_, migrate)) = SAVE_MIGRATIONS.iter().find(|(from, _)| *from == version) else {
            return fail(from_version, false, format!("No save migration registered for version {}", js::num(version)));
        };
        migrate(&mut state);
        version += 1.0;
    }
    let mut meta = spread(state.get("meta"));
    meta.insert("saveVersion".to_owned(), js::value(CURRENT_SAVE_VERSION));
    state.insert("meta".to_owned(), Value::Object(meta));

    match parse_game_state(Value::Object(state)) {
        Ok(data) => {
            let errors = validate_game_state(&data);
            if errors.is_empty() {
                SaveMigrationResult { ok: true, data: Some(data), from_version, migrated, errors }
            } else {
                SaveMigrationResult { ok: false, data: None, from_version, migrated, errors }
            }
        }
        Err(error) => SaveMigrationResult { ok: false, data: None, from_version, migrated, errors: vec![error] },
    }
}

/// [`migrate_game_state`] over JSON text. Never panics.
pub fn migrate_game_state_json(json: &str) -> SaveMigrationResult {
    match serde_json::from_str::<Value>(json) {
        Ok(raw) => migrate_game_state(&raw),
        Err(error) => fail(0.0, false, format!("Save state is not valid JSON: {error}")),
    }
}

/// Re-validate (and migrate, if needed) an in-memory state. Never panics.
pub fn revalidate_game_state(state: &GameState) -> SaveMigrationResult {
    match serde_json::to_value(state) {
        Ok(raw) => migrate_game_state(&raw),
        Err(error) => fail(0.0, false, format!("Save state could not be serialized: {error}")),
    }
}

/// Port of `SchemaValidation.ValidateGameState`: the zod refinements serde's types don't express
/// (integers, enums, ranges). Returns at most [`MAX_ERRORS`] messages as `path: message`.
pub fn validate_game_state(state: &GameState) -> Vec<String> {
    let mut errors = Vec::new();
    let mut error = |path: String, message: String| errors.push(format!("{path}: {message}"));
    let int = |path: String, value: f64, error: &mut dyn FnMut(String, String)| {
        if !js::is_integer(value) {
            error(path, format!("Expected integer, received {}", js::num(value)));
        }
    };
    let one_of = |path: String, value: &str, allowed: &[&str], error: &mut dyn FnMut(String, String)| {
        if !allowed.contains(&value) {
            let expected: Vec<String> = allowed.iter().map(|a| format!("'{a}'")).collect();
            error(path, format!("Invalid enum value. Expected {}, received '{value}'", expected.join(" | ")));
        }
    };

    int("meta.saveVersion".into(), state.meta.save_version, &mut error);
    int("clock.tick".into(), state.clock.tick, &mut error);
    int("clock.day".into(), state.clock.day, &mut error);
    int("clock.year".into(), state.clock.year, &mut error);

    let player = &state.player;
    one_of("player.direction".into(), &player.direction, directions::ALL, &mut error);
    for (axis, value) in [("dx", player.move_intent.dx), ("dy", player.move_intent.dy)] {
        let path = format!("player.moveIntent.{axis}");
        int(path.clone(), value, &mut error);
        if value < -1.0 {
            error(path.clone(), "Number must be greater than or equal to -1".into());
        }
        if value > 1.0 {
            error(path, "Number must be less than or equal to 1".into());
        }
    }
    for (name, skill) in &player.skills {
        int(format!("player.skills.{name}.level"), skill.level, &mut error);
    }

    for (id, npc) in &state.npcs {
        int(format!("npcs.{id}.x"), npc.x, &mut error);
        int(format!("npcs.{id}.y"), npc.y, &mut error);
        if let Some(patrol) = npc.patrol_index {
            int(format!("npcs.{id}.patrolIndex"), patrol, &mut error);
        }
        for (i, point) in npc.path.iter().flatten().enumerate() {
            int(format!("npcs.{id}.path.{i}.x"), point.x, &mut error);
            int(format!("npcs.{id}.path.{i}.y"), point.y, &mut error);
        }
    }
    for (id, quest) in &state.quests {
        one_of(format!("quests.{id}.status"), &quest.status, quest_statuses::ALL, &mut error);
    }

    int("mine.deepestFloor".into(), state.mine.deepest_floor, &mut error);
    int("mine.currentFloor".into(), state.mine.current_floor, &mut error);

    if state.rng.algorithm != "xoshiro128ss" {
        error("rng.algorithm".into(), "Invalid literal value, expected \"xoshiro128ss\"".into());
    }

    errors.truncate(MAX_ERRORS);
    errors
}

// ---- migration steps --------------------------------------------------------------------------

/// `{ ...(state.meta ?? {}), saveVersion: n, packs: state.meta?.packs ?? [] }`.
fn stamp_meta(state: &mut Map<String, Value>, save_version: f64) {
    let mut meta = spread(state.get("meta"));
    meta.insert("saveVersion".to_owned(), js::value(save_version));
    default(&mut meta, "packs", || Value::Array(Vec::new()));
    state.insert("meta".to_owned(), Value::Object(meta));
}

/// `key: { ...(state[key] ?? {}), ...defaults }` for one nested object.
fn with_object(state: &mut Map<String, Value>, key: &str, update: impl FnOnce(&mut Map<String, Value>)) {
    let mut object = spread(state.get(key));
    update(&mut object);
    state.insert(key.to_owned(), Value::Object(object));
}

fn migrate_v1_to_v2(state: &mut Map<String, Value>) {
    stamp_meta(state, 2.0);
    with_object(state, "clock", |clock| {
        default(clock, "tick", || js::value(0.0));
        default(clock, "timeMinutes", || js::value(360.0));
        default(clock, "day", || js::value(1.0));
        default(clock, "season", || Value::from("spring"));
        default(clock, "year", || js::value(1.0));
        default(clock, "weatherId", || Value::from("sun"));
    });
    with_object(state, "player", |player| {
        default(player, "energy", || js::value(100.0));
        default(player, "maxEnergy", || js::value(100.0));
    });
    default(state, "shop", || Value::Null);
    default(state, "shopPurchasesToday", || Value::Object(Map::new()));
}

fn migrate_v2_to_v3(state: &mut Map<String, Value>) {
    stamp_meta(state, 3.0);
    with_object(state, "clock", |clock| default(clock, "weatherId", || Value::from("sun")));
    with_object(state, "player", |player| default(player, "skills", || Value::Object(Map::new())));
    default(state, "social", || Value::Object(Map::new()));
    default(state, "animals", || Value::Array(Vec::new()));
    default(state, "mine", || {
        let mut mine = Map::new();
        mine.insert("deepestFloor".to_owned(), js::value(0.0));
        mine.insert("currentFloor".to_owned(), js::value(0.0));
        Value::Object(mine)
    });
    default(state, "quarantinedItems", || Value::Array(Vec::new()));
}

fn migrate_v3_to_v4(state: &mut Map<String, Value>) {
    stamp_meta(state, 4.0);
    with_object(state, "player", |player| {
        // Grid saves stored the occupied tile; the free-movement position is that tile's
        // center. Fractional values pass through untouched.
        for axis in ["x", "y"] {
            let value = get_in(player, axis).cloned().unwrap_or_else(|| js::value(0.0));
            let centered = match value.as_f64() {
                Some(number) => js::value(center_coordinate(number)),
                None => value,
            };
            player.insert(axis.to_owned(), centered);
        }
        default(player, "moveIntent", || {
            let mut intent = Map::new();
            intent.insert("dx".to_owned(), js::value(0.0));
            intent.insert("dy".to_owned(), js::value(0.0));
            Value::Object(intent)
        });
    });
}

// ---- JavaScript semantics over the JSON tree (the Rust side of C# `RawJson`) -----------------

/// `typeof x === 'object' && x !== null`: objects and arrays.
fn is_object_like(value: &Value) -> bool {
    matches!(value, Value::Object(_) | Value::Array(_))
}

/// Optional-chaining read (`obj?.key`): `None` for non-objects, missing keys and JSON null.
fn get<'a>(object: Option<&'a Value>, key: &str) -> Option<&'a Value> {
    match object {
        Some(Value::Object(map)) => get_in(map, key),
        _ => None,
    }
}

/// `map[key]`, treating JSON null as missing (the `??` test).
fn get_in<'a>(map: &'a Map<String, Value>, key: &str) -> Option<&'a Value> {
    map.get(key).filter(|value| !value.is_null())
}

/// The spread idiom `{ ...obj, key: obj.key ?? fallback }`.
fn default(map: &mut Map<String, Value>, key: &str, fallback: impl FnOnce() -> Value) {
    if get_in(map, key).is_none() {
        map.insert(key.to_owned(), fallback());
    }
}

/// `{ ...value }`: an object copies its keys, arrays and strings spread to index keys (strings
/// by UTF-16 code unit), other primitives and null/undefined spread to `{}`.
fn spread(value: Option<&Value>) -> Map<String, Value> {
    match value {
        Some(Value::Object(map)) => map.clone(),
        Some(Value::Array(items)) => items.iter().enumerate().map(|(i, item)| (i.to_string(), item.clone())).collect(),
        Some(Value::String(text)) => text
            .encode_utf16()
            .enumerate()
            .map(|(i, unit)| (i.to_string(), Value::String(String::from_utf16_lossy(&[unit]))))
            .collect(),
        _ => Map::new(),
    }
}

// ---- parsing -----------------------------------------------------------------------------------

/// zod `safeParse` without the refinements: deserialize into [`GameState`] (defaults applied,
/// unknown keys dropped). Type errors are reported zod-style
/// (`player.money: Expected number, received string`).
fn parse_game_state(state: Value) -> Result<GameState, String> {
    let original = state.clone();
    serde_path_to_error::deserialize::<_, GameState>(state).map_err(|error| {
        let path: Vec<String> = error
            .path()
            .iter()
            .filter_map(|segment| match segment {
                serde_path_to_error::Segment::Map { key } => Some(key.clone()),
                serde_path_to_error::Segment::Seq { index } => Some(index.to_string()),
                serde_path_to_error::Segment::Enum { variant } => Some(variant.clone()),
                serde_path_to_error::Segment::Unknown => None,
            })
            .collect();
        let found = path.iter().try_fold(&original, |node, segment| match node {
            Value::Object(map) => map.get(segment),
            Value::Array(items) => segment.parse::<usize>().ok().and_then(|i| items.get(i)),
            _ => None,
        });
        let message = error.inner().to_string();
        let expected = expected_kind(&message);
        match (expected, found) {
            (Some(expected), Some(node)) => {
                format!("{}: Expected {expected}, received {}", path.join("."), kind_name(node))
            }
            _ => format!("{}: {message}", path.join(".")),
        }
    })
}

/// The zod type name serde expected, from its `invalid type: …, expected …` message.
fn expected_kind(message: &str) -> Option<&'static str> {
    let expected = message.split(", expected ").nth(1)?;
    Some(if expected.starts_with("f64") || expected.starts_with("u32") || expected.contains("number") {
        "number"
    } else if expected.contains("string") {
        "string"
    } else if expected.starts_with("a boolean") {
        "boolean"
    } else if expected.contains("sequence") || expected.contains("array") {
        "array"
    } else {
        "object"
    })
}

fn kind_name(value: &Value) -> &'static str {
    match value {
        Value::Null => "null",
        Value::Bool(_) => "boolean",
        Value::Number(_) => "number",
        Value::String(_) => "string",
        Value::Array(_) => "array",
        Value::Object(_) => "object",
    }
}

fn fail(from_version: f64, migrated: bool, error: String) -> SaveMigrationResult {
    SaveMigrationResult { ok: false, data: None, from_version, migrated, errors: vec![error] }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn minimal_v4() -> Value {
        json!({
            "meta": { "saveVersion": 4, "engineSeed": "t", "packs": [] },
            "clock": { "tick": 0, "timeMinutes": 360, "day": 1, "season": "spring", "year": 1, "weatherId": "sun" },
            "world": { "scenes": [] },
            "player": { "x": 1.5, "y": 1.5, "moveIntent": { "dx": 0, "dy": 0 }, "direction": "down", "sceneId": "s",
                        "inventory": [], "maxInventorySize": 20, "money": 0, "energy": 100, "maxEnergy": 100,
                        "skills": {}, "activeQuests": [], "completedQuests": [] },
            "npcs": {}, "quests": {}, "dialogue": null, "shop": null, "minigame": null,
            "shopPurchasesToday": {}, "social": {}, "animals": [], "mine": { "deepestFloor": 0, "currentFloor": 0 },
            "flags": {}, "quarantinedItems": [], "rng": { "algorithm": "xoshiro128ss", "s": [1, 2, 3, 4] }
        })
    }

    #[test]
    fn a_current_save_passes_through_unmigrated() {
        let result = migrate_game_state(&minimal_v4());
        assert!(result.ok, "{:?}", result.errors);
        assert!(!result.migrated);
        assert_eq!(result.from_version, 4.0);
    }

    #[test]
    fn fractional_versions_have_no_migration() {
        let mut raw = minimal_v4();
        raw["meta"]["saveVersion"] = json!(2.5);
        let result = migrate_game_state(&raw);
        assert!(!result.ok);
        assert!(!result.migrated);
        assert_eq!(result.errors, vec!["No save migration registered for version 2.5"]);
    }

    #[test]
    fn type_errors_are_reported_zod_style() {
        let mut raw = minimal_v4();
        raw["player"]["money"] = json!("lots");
        let result = migrate_game_state(&raw);
        assert!(!result.ok);
        assert_eq!(result.errors, vec!["player.money: Expected number, received string"]);
    }

    #[test]
    fn refinements_are_checked() {
        let mut raw = minimal_v4();
        raw["player"]["direction"] = json!("north");
        raw["player"]["moveIntent"]["dx"] = json!(2);
        raw["clock"]["day"] = json!(1.5);
        let result = migrate_game_state(&raw);
        assert!(!result.ok);
        assert_eq!(
            result.errors,
            vec![
                "clock.day: Expected integer, received 1.5",
                "player.direction: Invalid enum value. Expected 'up' | 'down' | 'left' | 'right', received 'north'",
                "player.moveIntent.dx: Number must be less than or equal to 1",
            ]
        );
    }

    #[test]
    fn invalid_json_text_fails_softly() {
        let result = migrate_game_state_json("{ not json");
        assert!(!result.ok);
        assert_eq!(result.from_version, 0.0);
        assert!(result.errors[0].starts_with("Save state is not valid JSON: "));
    }

    #[test]
    fn grid_positions_move_to_tile_centers() {
        let mut raw = minimal_v4();
        raw["meta"]["saveVersion"] = json!(3);
        raw["player"]["x"] = json!(4);
        raw["player"]["y"] = json!(2.25);
        let result = migrate_game_state(&raw);
        assert!(result.ok, "{:?}", result.errors);
        let data = result.data.expect("data");
        assert_eq!((data.player.x, data.player.y), (4.5, 2.25));
        assert!(result.migrated);
    }
}
