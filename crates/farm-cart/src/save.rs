//! Save migrations: port of `SaveMigrations.cs` (packages/engine-schemas/src/save.ts,
//! `SAVE_MIGRATIONS` / `migrateGameState`) plus the `GameState` checks from
//! `SchemaValidation.ValidateGameState`.
//!
//! Saves belong to the player's machine, so Rust owns their format and their migrations
//! (docs/LANGUAGES.md "Saves (Rust only)"). A save is the `GameState` JSON in authoring units
//! (the web version writes the same); the migrations run on the raw JSON tree with JavaScript
//! semantics (`??`, object spread), exactly like the TypeScript did, and the result is then
//! parsed into [`GameState`] and checked. [`migrate_game_state`] never panics on bad input.
//!
//! - v5 (schema v9, docs/NUMERICS.md): the simulation keeps integers. The v4→v5 step is the
//!   quantization the `farm_sim::units` adapters apply when the state is parsed (positions to
//!   1/8192 tile, energy to 1/1000 point, …) plus the version stamp; the zod `int()`
//!   refinements are still checked on the JSON as read.

use farm_sim::schema::primitives::{directions, quest_statuses};
use farm_sim::schema::save::{SaveMigrationResult, CURRENT_SAVE_VERSION};
use farm_sim::schema::GameState;
use farm_sim::units;
use serde_json::{Map, Value};

/// A save migration step: upgrades a version-n state to n+1, in place.
pub type SaveMigration = fn(&mut Map<String, Value>);

/// TS `SAVE_MIGRATIONS`: `(n, step)` upgrades a version-n `GameState` to n+1.
pub const SAVE_MIGRATIONS: &[(f64, SaveMigration)] =
    &[(1.0, migrate_v1_to_v2), (2.0, migrate_v2_to_v3), (3.0, migrate_v3_to_v4), (4.0, migrate_v4_to_v5)];

/// [`CURRENT_SAVE_VERSION`] as the number a save's JSON declares.
fn current_version() -> f64 {
    f64::from(CURRENT_SAVE_VERSION)
}

/// The most errors a failed parse reports (zod `issues.slice(0, 20)`).
pub(crate) const MAX_ERRORS: usize = 20;

/// Upgrade and validate serialized simulation state. This is deliberately independent from
/// project migrations: authored content and a player's live runtime state evolve on different
/// schedules. Never panics. Loaders that own the JSON tree use [`migrate_game_state_owned`],
/// which does not copy it.
pub fn migrate_game_state(raw: &Value) -> SaveMigrationResult {
    migrate_game_state_owned(raw.clone())
}

/// [`migrate_game_state`] taking the tree by value: a save's state is migrated, parsed and
/// checked in place, without the copies a crafted multi-megabyte save would multiply (#80).
pub fn migrate_game_state_owned(raw: Value) -> SaveMigrationResult {
    if !is_object_like(&raw) {
        return fail(0.0, false, "Save state is not an object".to_owned());
    }
    let from_version = get(get(Some(&raw), "meta"), "saveVersion").and_then(Value::as_f64).unwrap_or(1.0);
    if from_version > current_version() {
        return fail(
            from_version,
            false,
            format!(
                "Save version {} is newer than this engine supports ({}). Update the engine.",
                units::format_number(from_version),
                CURRENT_SAVE_VERSION
            ),
        );
    }

    let migrated = from_version < current_version();
    // Whole doubles as integers (`1.0` → `1`): stable JSON writes them that way, so a state that
    // went through a save compares and hashes the same as before (#142).
    let mut state = spread(units::canonical_json(raw));
    let mut version = from_version;
    while version < current_version() {
        let Some((_, migrate)) = SAVE_MIGRATIONS.iter().find(|(from, _)| *from == version) else {
            return fail(
                from_version,
                false,
                format!("No save migration registered for version {}", units::format_number(version)),
            );
        };
        migrate(&mut state);
        version += 1.0;
    }
    with_object(&mut state, "meta", |meta| {
        meta.insert("saveVersion".to_owned(), Value::from(CURRENT_SAVE_VERSION));
    });

    let raw_state = Value::Object(state);
    match parse_game_state(&raw_state) {
        Ok(data) => {
            let errors = validate_game_state_json(&raw_state, &data);
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
        Ok(raw) => migrate_game_state_owned(raw),
        Err(error) => fail(0.0, false, format!("Save state is not valid JSON: {error}")),
    }
}

/// Re-validate (and migrate, if needed) an in-memory state. Never panics.
pub fn revalidate_game_state(state: &GameState) -> SaveMigrationResult {
    match serde_json::to_value(state) {
        Ok(raw) => migrate_game_state_owned(raw),
        Err(error) => fail(0.0, false, format!("Save state could not be serialized: {error}")),
    }
}

/// Port of `SchemaValidation.ValidateGameState`: the zod refinements serde's types don't express
/// (integers, enums, ranges). Returns at most [`MAX_ERRORS`] messages as `path: message`.
pub fn validate_game_state(state: &GameState) -> Vec<String> {
    match serde_json::to_value(state) {
        Ok(raw) => validate_game_state_json(&raw, state),
        Err(error) => vec![format!("(root): {error}")],
    }
}

/// [`validate_game_state`] for a state parsed from `raw`: the `int()` refinements are checked on
/// the numbers as the JSON wrote them (parsing rounds them onto the integer grids), the rest on
/// the parsed state.
pub fn validate_game_state_json(raw: &Value, state: &GameState) -> Vec<String> {
    let mut errors = Vec::new();
    let mut error = |path: String, message: String| errors.push(format!("{path}: {message}"));
    // `path` names the error, `at` walks the raw JSON (absent keys took their defaults).
    let int = |path: String, at: &[&str], error: &mut dyn FnMut(String, String)| {
        let value = at.iter().try_fold(raw, |node, key| node.get(key)).and_then(Value::as_f64);
        if let Some(value) = value.filter(|value| !units::is_integer(*value)) {
            error(path, format!("Expected integer, received {}", units::format_number(value)));
        }
    };
    let one_of = |path: String, value: &str, allowed: &[&str], error: &mut dyn FnMut(String, String)| {
        if !allowed.contains(&value) {
            let expected: Vec<String> = allowed.iter().map(|a| format!("'{a}'")).collect();
            error(path, format!("Invalid enum value. Expected {}, received '{value}'", expected.join(" | ")));
        }
    };

    int("meta.saveVersion".into(), &["meta", "saveVersion"], &mut error);
    int("clock.tick".into(), &["clock", "tick"], &mut error);
    int("clock.day".into(), &["clock", "day"], &mut error);
    int("clock.year".into(), &["clock", "year"], &mut error);

    let player = &state.player;
    one_of("player.direction".into(), &player.direction, directions::ALL, &mut error);
    for (axis, value) in [("dx", player.move_intent.dx), ("dy", player.move_intent.dy)] {
        let path = format!("player.moveIntent.{axis}");
        int(path.clone(), &["player", "moveIntent", axis], &mut error);
        if value < -1 {
            error(path.clone(), "Number must be greater than or equal to -1".into());
        }
        if value > 1 {
            error(path, "Number must be less than or equal to 1".into());
        }
    }
    for name in player.skills.keys() {
        int(format!("player.skills.{name}.level"), &["player", "skills", name, "level"], &mut error);
    }

    for (id, npc) in &state.npcs {
        int(format!("npcs.{id}.x"), &["npcs", id, "x"], &mut error);
        int(format!("npcs.{id}.y"), &["npcs", id, "y"], &mut error);
        if npc.patrol_index.is_some() {
            int(format!("npcs.{id}.patrolIndex"), &["npcs", id, "patrolIndex"], &mut error);
        }
        for i in 0..npc.path.as_ref().map_or(0, Vec::len) {
            let index = i.to_string();
            int(format!("npcs.{id}.path.{i}.x"), &["npcs", id, "path", &index, "x"], &mut error);
            int(format!("npcs.{id}.path.{i}.y"), &["npcs", id, "path", &index, "y"], &mut error);
        }
    }
    for (id, quest) in &state.quests {
        one_of(format!("quests.{id}.status"), &quest.status, quest_statuses::ALL, &mut error);
    }

    int("mine.deepestFloor".into(), &["mine", "deepestFloor"], &mut error);
    int("mine.currentFloor".into(), &["mine", "currentFloor"], &mut error);

    if state.rng.algorithm != "xoshiro128ss" {
        error("rng.algorithm".into(), "Invalid literal value, expected \"xoshiro128ss\"".into());
    }

    errors.truncate(MAX_ERRORS);
    errors
}

// ---- migration steps --------------------------------------------------------------------------

/// `{ ...(state.meta ?? {}), saveVersion: n, packs: state.meta?.packs ?? [] }`.
fn stamp_meta(state: &mut Map<String, Value>, save_version: u32) {
    with_object(state, "meta", |meta| {
        meta.insert("saveVersion".to_owned(), Value::from(save_version));
        default(meta, "packs", || Value::Array(Vec::new()));
    });
}

/// `key: { ...(state[key] ?? {}), ...defaults }` for one nested object, updated in place (the
/// key keeps its position, the object is not copied).
fn with_object(state: &mut Map<String, Value>, key: &str, update: impl FnOnce(&mut Map<String, Value>)) {
    let slot = state.entry(key.to_owned()).or_insert(Value::Null);
    let mut object = spread(std::mem::take(slot));
    update(&mut object);
    *slot = Value::Object(object);
}

fn migrate_v1_to_v2(state: &mut Map<String, Value>) {
    stamp_meta(state, 2);
    with_object(state, "clock", |clock| {
        default(clock, "tick", || Value::from(0));
        default(clock, "timeMinutes", || Value::from(360));
        default(clock, "day", || Value::from(1));
        default(clock, "season", || Value::from("spring"));
        default(clock, "year", || Value::from(1));
        default(clock, "weatherId", || Value::from("sun"));
    });
    with_object(state, "player", |player| {
        default(player, "energy", || Value::from(100));
        default(player, "maxEnergy", || Value::from(100));
    });
    default(state, "shop", || Value::Null);
    default(state, "shopPurchasesToday", || Value::Object(Map::new()));
}

fn migrate_v2_to_v3(state: &mut Map<String, Value>) {
    stamp_meta(state, 3);
    with_object(state, "clock", |clock| default(clock, "weatherId", || Value::from("sun")));
    with_object(state, "player", |player| default(player, "skills", || Value::Object(Map::new())));
    default(state, "social", || Value::Object(Map::new()));
    default(state, "animals", || Value::Array(Vec::new()));
    default(state, "mine", || {
        let mut mine = Map::new();
        mine.insert("deepestFloor".to_owned(), Value::from(0));
        mine.insert("currentFloor".to_owned(), Value::from(0));
        Value::Object(mine)
    });
    default(state, "quarantinedItems", || Value::Array(Vec::new()));
}

fn migrate_v3_to_v4(state: &mut Map<String, Value>) {
    stamp_meta(state, 4);
    with_object(state, "player", |player| {
        // Grid saves stored the occupied tile; the free-movement position is that tile's
        // center. Fractional values pass through untouched.
        for axis in ["x", "y"] {
            let value = get_in(player, axis).cloned().unwrap_or_else(|| Value::from(0));
            let centered = match value.as_f64() {
                Some(number) if units::is_integer(number) => units::value(number + 0.5),
                _ => value,
            };
            player.insert(axis.to_owned(), centered);
        }
        default(player, "moveIntent", || {
            let mut intent = Map::new();
            intent.insert("dx".to_owned(), Value::from(0));
            intent.insert("dy".to_owned(), Value::from(0));
            Value::Object(intent)
        });
    });
}

/// v5 (schema v9): the state is quantized onto the integer grids when it is parsed; the step
/// itself only stamps the version.
fn migrate_v4_to_v5(state: &mut Map<String, Value>) {
    stamp_meta(state, 5);
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

/// `{ ...value }` where the result must be an object: an object keeps its keys, anything else
/// gives `{}`.
///
/// JavaScript spreads arrays and strings to index keys (strings by UTF-16 code unit), but every
/// object spread here is then parsed into a schema struct that drops unknown keys, so `{}` parses
/// to the same state. It also keeps a crafted save from exploding a long string into one map
/// entry per character (#80).
fn spread(value: Value) -> Map<String, Value> {
    match value {
        Value::Object(map) => map,
        _ => Map::new(),
    }
}

// ---- parsing -----------------------------------------------------------------------------------

/// zod `safeParse` without the refinements: deserialize into [`GameState`] (defaults applied,
/// unknown keys dropped). Type errors are reported zod-style
/// (`player.money: Expected number, received string`).
fn parse_game_state(state: &Value) -> Result<GameState, String> {
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
        let found = path.iter().try_fold(state, |node, segment| match node {
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

    fn minimal_v5() -> Value {
        json!({
            "meta": { "saveVersion": 5, "engineSeed": "t", "packs": [] },
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
        let result = migrate_game_state(&minimal_v5());
        assert!(result.ok, "{:?}", result.errors);
        assert!(!result.migrated);
        assert_eq!(result.from_version, 5.0);
    }

    #[test]
    fn a_v4_save_is_quantized_and_stamped_v5() {
        let mut raw = minimal_v5();
        raw["meta"]["saveVersion"] = json!(4);
        raw["player"]["x"] = json!(1.00001);
        raw["player"]["energy"] = json!(97.25);
        raw["clock"]["timeMinutes"] = json!(390.05);
        let result = migrate_game_state(&raw);
        assert!(result.ok, "{:?}", result.errors);
        assert!(result.migrated);
        let data = result.data.expect("data");
        assert_eq!(data.meta.save_version, 5);
        assert_eq!(data.player.x, 8192); // 1.00001 × 8192 = 8192.08 → 8192
        assert_eq!(data.player.energy, 97_250);
        assert_eq!(data.clock.time_minutes, 390_050_000);
        let written = serde_json::to_value(&data).expect("serializes");
        assert_eq!(written["player"]["x"], json!(1));
        assert_eq!(written["player"]["energy"], json!(97.25));
        assert_eq!(written["meta"]["saveVersion"], json!(5));
    }

    #[test]
    fn fractional_versions_have_no_migration() {
        let mut raw = minimal_v5();
        raw["meta"]["saveVersion"] = json!(2.5);
        let result = migrate_game_state(&raw);
        assert!(!result.ok);
        assert!(!result.migrated);
        assert_eq!(result.errors, vec!["No save migration registered for version 2.5"]);
    }

    #[test]
    fn type_errors_are_reported_zod_style() {
        let mut raw = minimal_v5();
        raw["player"]["money"] = json!("lots");
        let result = migrate_game_state(&raw);
        assert!(!result.ok);
        assert_eq!(result.errors, vec!["player.money: Expected number, received string"]);
    }

    #[test]
    fn refinements_are_checked() {
        let mut raw = minimal_v5();
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
    fn strings_and_arrays_where_objects_belong_are_not_spread() {
        // JavaScript would spread these to one key per character or item; a crafted save could
        // make that millions of map entries per migration step (#80).
        assert!(spread(json!("x".repeat(100_000))).is_empty());
        assert!(spread(json!([1, 2, 3])).is_empty());
        let mut raw = minimal_v5();
        raw["meta"]["saveVersion"] = json!(1);
        raw["clock"] = json!("x".repeat(100_000));
        let result = migrate_game_state_owned(raw);
        assert!(result.ok, "{:?}", result.errors);
        // The clock took the v1→v2 defaults, as from `{}`.
        let data = result.data.expect("data");
        assert_eq!((data.clock.day, data.clock.season.as_str()), (1, "spring"));
        // A meta string has no version: it is a v1 save whose meta starts from `{}`.
        let mut raw = minimal_v5();
        raw["meta"] = json!("y".repeat(100_000));
        let result = migrate_game_state(&raw);
        assert_eq!((result.from_version, result.migrated), (1.0, true));
        assert_eq!(result.data.map(|data| data.meta.save_version), result.ok.then_some(5));
    }

    #[test]
    fn grid_positions_move_to_tile_centers() {
        let mut raw = minimal_v5();
        raw["meta"]["saveVersion"] = json!(3);
        raw["player"]["x"] = json!(4);
        raw["player"]["y"] = json!(2.25);
        let result = migrate_game_state(&raw);
        assert!(result.ok, "{:?}", result.errors);
        let data = result.data.expect("data");
        assert_eq!((data.player.x, data.player.y), (units::pos(4.5), units::pos(2.25)));
        assert!(result.migrated);
    }
}
