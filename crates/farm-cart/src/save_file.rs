//! Save files: a header plus the [`GameState`] (docs/EXPORT.md "What earlier phases must get
//! right").
//!
//! Saves outlive the cartridge that wrote them: a player's save from version 1.0 of a game has
//! to load in 1.1. So the header says which game wrote the save (`gameId`), which version of it,
//! and the content hash of that cartridge, and loading follows three rules:
//!
//! - a save from a **different game** is refused;
//! - a save from a **newer game version** loads with a warning;
//! - when the **content changed** since the save was written, inventory items are looked up by
//!   their stable string id: items that still exist take the current definition, items that no
//!   longer exist go to quarantine (`quarantinedItems`) instead of failing the load, and
//!   quarantined items whose id is back return to the inventory.
//!
//! In the compatibility phase the file is JSON (`{"header": {…}, "state": {…}}`); the zstd
//! FlatBuffers form arrives with phase 7. A bare `GameState` (what the web version writes) also
//! loads: it has no header, so it is treated as coming from unknown content.

use crate::save::{migrate_game_state, MAX_ERRORS};
use farm_sim::js;
use farm_sim::schema::{GameContent, GameProject, GameState, InventorySlot, Item};
use farm_sim::{hash_state, stable_json};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::cmp::Ordering;

/// Save file format version. Bumped on every breaking change to the file layout (the header
/// or the envelope); the `GameState` inside is versioned separately by `meta.saveVersion`.
pub const SAVE_FORMAT: u32 = 1;

/// The game a save belongs to: the running cartridge when loading, the writer when saving.
#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SaveTarget {
    /// Stable id of the game. Export settings override the project id when present.
    pub game_id: String,
    /// The game's own version string (the project `version`).
    pub game_version: String,
    /// Content hash of the cartridge ([`cart_hash`]).
    pub cart_hash: String,
}

impl SaveTarget {
    /// The target for a project and its compiled content. The export block is additive to
    /// project schema v8 and lands in the Rust project's passthrough fields.
    pub fn for_project(project: &GameProject, content: &GameContent) -> Self {
        let export = project.extra.get("export");
        let game_id = export
            .and_then(|value| value.get("gameId"))
            .and_then(Value::as_str)
            .filter(|id| !id.is_empty())
            .unwrap_or(&project.id);
        let game_version = export
            .and_then(|value| value.get("version"))
            .and_then(Value::as_str)
            .filter(|version| !version.is_empty())
            .unwrap_or(&project.version);
        Self { game_id: game_id.to_owned(), game_version: game_version.to_owned(), cart_hash: cart_hash(content) }
    }
}

/// The header at the front of every save file.
#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SaveHeader {
    /// [`SAVE_FORMAT`] of the writer.
    pub format: u32,
    pub game_id: String,
    pub game_version: String,
    pub cart_hash: String,
}

/// Content hash of a cartridge. In the compatibility phase the cartridge is the `GameContent`,
/// hashed like state (FNV-1a over stable JSON).
pub fn cart_hash(content: &GameContent) -> String {
    hash_state(content)
}

/// Serializes a save file for `state` written by `target`: stable JSON, so the same state
/// always gives the same bytes.
pub fn write_save(state: &GameState, target: &SaveTarget) -> String {
    let header = SaveHeader {
        format: SAVE_FORMAT,
        game_id: target.game_id.clone(),
        game_version: target.game_version.clone(),
        cart_hash: target.cart_hash.clone(),
    };
    let mut file = serde_json::Map::new();
    file.insert("header".to_owned(), serde_json::to_value(&header).unwrap_or(Value::Null));
    file.insert("state".to_owned(), serde_json::to_value(state).unwrap_or(Value::Null));
    stable_json::stringify_value(&Value::Object(file))
}

/// What loading a save produced. `ok == false` means `state` is `None` and `errors` says why.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct LoadedSave {
    pub ok: bool,
    pub state: Option<GameState>,
    /// The header, when the file had one (bare web saves have none).
    pub header: Option<SaveHeader>,
    /// Why the save was refused (at most 20).
    pub errors: Vec<String>,
    /// Loaded, but the player should know (a newer game version wrote it, items quarantined).
    pub warnings: Vec<String>,
    /// Ids of the inventory items moved to quarantine because the content no longer has them.
    pub quarantined: Vec<String>,
    /// Ids of quarantined items that came back because the content has them again.
    pub restored: Vec<String>,
    /// `meta.saveVersion` the state was written with (before migration).
    pub from_version: f64,
    /// The state went through save migrations.
    pub migrated: bool,
}

/// Loads a save file (or a bare web `GameState`) for the running game `target` with `content`.
/// Never panics.
pub fn load_save(text: &str, target: &SaveTarget, content: &GameContent) -> LoadedSave {
    let raw: Value = match serde_json::from_str(text) {
        Ok(raw) => raw,
        Err(error) => return refused(None, format!("Save file is not valid JSON: {error}")),
    };
    let (header, state_raw) = match split_envelope(&raw) {
        Ok(parts) => parts,
        Err(error) => return refused(None, error),
    };

    let mut warnings = Vec::new();
    if let Some(header) = &header {
        if header.format > SAVE_FORMAT {
            return refused(
                Some(header.clone()),
                format!(
                    "Save file format {} is newer than this player supports ({SAVE_FORMAT}). Update the game.",
                    header.format
                ),
            );
        }
        if header.game_id != target.game_id {
            return refused(
                Some(header.clone()),
                format!("This save belongs to a different game ('{}', not '{}').", header.game_id, target.game_id),
            );
        }
        if compare_versions(&header.game_version, &target.game_version) == Ordering::Greater {
            warnings.push(format!(
                "This save was made with a newer version of the game ({}, this is {}). Some progress may not load.",
                header.game_version, target.game_version
            ));
        }
    }

    let migration = migrate_game_state(state_raw);
    let Some(mut state) = migration.data.filter(|_| migration.ok) else {
        return LoadedSave {
            header,
            errors: migration.errors,
            from_version: migration.from_version,
            migrated: migration.migrated,
            ..LoadedSave::default()
        };
    };

    let same_content = header.as_ref().is_some_and(|h| h.cart_hash == target.cart_hash);
    let (quarantined, restored) =
        if same_content { (Vec::new(), Vec::new()) } else { reconcile_items(&mut state, content) };
    if !quarantined.is_empty() {
        warnings.push(format!(
            "{} item{} in this save no longer exist{} in the game and {} set aside: {}.",
            quarantined.len(),
            if quarantined.len() == 1 { "" } else { "s" },
            if quarantined.len() == 1 { "s" } else { "" },
            if quarantined.len() == 1 { "was" } else { "were" },
            quarantined.join(", ")
        ));
    }

    LoadedSave {
        ok: true,
        state: Some(state),
        header,
        errors: Vec::new(),
        warnings,
        quarantined,
        restored,
        from_version: migration.from_version,
        migrated: migration.migrated,
    }
}

/// Maps a save's inventory onto `content` by stable item id (the save was written against other
/// content): slots whose item still exists take its current definition, slots whose item is gone
/// move to `quarantined_items`, and quarantined slots whose item is back return to the
/// inventory. Returns the ids quarantined and restored, in inventory order.
pub fn reconcile_items(state: &mut GameState, content: &GameContent) -> (Vec<String>, Vec<String>) {
    let current = |id: &str| -> Option<&Item> { content.items.iter().find(|item| item.id == id) };
    let refresh = |slot: &InventorySlot, item: &Item| InventorySlot { item: item.clone(), quantity: slot.quantity };

    let mut quarantined = Vec::new();
    let mut inventory = Vec::with_capacity(state.player.inventory.len());
    let mut set_aside = Vec::new();
    for slot in &state.player.inventory {
        match current(&slot.item.id) {
            Some(item) => inventory.push(refresh(slot, item)),
            None => {
                quarantined.push(slot.item.id.clone());
                set_aside.push(slot.clone());
            }
        }
    }

    let mut restored = Vec::new();
    let mut still_quarantined = Vec::new();
    for slot in &state.quarantined_items {
        match current(&slot.item.id) {
            Some(item) => {
                restored.push(slot.item.id.clone());
                inventory.push(refresh(slot, item));
            }
            None => still_quarantined.push(slot.clone()),
        }
    }

    state.player.inventory = inventory;
    state.quarantined_items = still_quarantined.into_iter().chain(set_aside).collect();
    (quarantined, restored)
}

/// Compares dotted version strings segment by segment: numeric segments numerically
/// (`1.10 > 1.9`), others as text, a missing segment as `0` (`1.0 == 1`).
pub fn compare_versions(a: &str, b: &str) -> Ordering {
    let a: Vec<&str> = a.trim().trim_start_matches(['v', 'V']).split('.').collect();
    let b: Vec<&str> = b.trim().trim_start_matches(['v', 'V']).split('.').collect();
    for i in 0..a.len().max(b.len()) {
        let x = a.get(i).copied().unwrap_or("0");
        let y = b.get(i).copied().unwrap_or("0");
        let order = match (x.parse::<u64>(), y.parse::<u64>()) {
            (Ok(x), Ok(y)) => x.cmp(&y),
            _ => js::compare_strings(x, y),
        };
        if order != Ordering::Equal {
            return order;
        }
    }
    Ordering::Equal
}

/// `{"header", "state"}` → its parts; any other object is a bare `GameState`.
fn split_envelope(raw: &Value) -> Result<(Option<SaveHeader>, &Value), String> {
    let Value::Object(map) = raw else {
        return Ok((None, raw));
    };
    match (map.get("header"), map.get("state")) {
        (Some(header), Some(state)) => {
            let header: SaveHeader =
                serde_json::from_value(header.clone()).map_err(|error| format!("Save header is not valid: {error}"))?;
            Ok((Some(header), state))
        }
        _ => Ok((None, raw)),
    }
}

fn refused(header: Option<SaveHeader>, error: String) -> LoadedSave {
    let mut errors = vec![error];
    errors.truncate(MAX_ERRORS);
    LoadedSave { header, errors, ..LoadedSave::default() }
}
