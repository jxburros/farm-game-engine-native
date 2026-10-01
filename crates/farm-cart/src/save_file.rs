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
//!   quarantined items whose id is back return to the inventory; and the world is rebased onto
//!   the new maps ([`rebase_world`]): a fixed map, a new door or a new scene reaches old saves,
//!   while what the player did there (crops, soil, nodes, machines, dropped items) stays; and
//!   NPCs new in the content join the world ([`add_missing_npcs`]).
//!
//! Every load also fits the clock's day of season to the game's calendar and reseeds an
//! all-zero random state.
//!
//! Two envelopes carry the same header and state:
//!
//! - the binary save (`schemas/save.fbs`, identifier `FGSV`) the standalone player writes: the
//!   header, a slot preview (farm name, date, money, play time, thumbnail) readable without
//!   touching the state, and the state as zstd-compressed stable JSON;
//! - JSON (`{"header": {…}, "state": {…}}`), for the editor's debug tools and the web version.
//!
//! A bare `GameState` (what the web version writes) also loads: it has no header, so it is
//! treated as coming from unknown content.

use crate::save::{migrate_game_state, MAX_ERRORS};
use farm_cart_schema::farm_engine::save as fb_save;
use farm_cart_schema::farm_engine::save::save_file_buffer_has_identifier;
use farm_cart_schema::flatbuffers::FlatBufferBuilder;
use farm_sim::schema::{
    GameContent, GameProject, GameState, InventorySlot, Item, NpcState, Scene, SceneTransition, Tile,
};
use farm_sim::{game_time, hash_state, rng, stable_json, text};
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

    /// The target for a loaded cartridge: its game info names the game.
    pub fn for_cartridge(cartridge: &crate::LoadedCartridge) -> Self {
        Self {
            game_id: cartridge.info.game_id.clone(),
            game_version: cartridge.info.version.clone(),
            cart_hash: cart_hash(&cartridge.content),
        }
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

/// Content hash of a cartridge: the `GameContent`, hashed like state (xxh3-64 over the
/// canonical encoding). A save written against other content (or before v9, when this was
/// FNV-1a over stable JSON) reconciles its items with the current content when it loads.
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

/// Loads a JSON save file (or a bare web `GameState`) for the running game `target` with
/// `content`. Never panics.
pub fn load_save(text: &str, target: &SaveTarget, content: &GameContent) -> LoadedSave {
    let raw: Value = match serde_json::from_str(text) {
        Ok(raw) => raw,
        Err(error) => return refused(None, format!("Save file is not valid JSON: {error}")),
    };
    let (header, state_raw) = match split_envelope(&raw) {
        Ok(parts) => parts,
        Err(error) => return refused(None, error),
    };
    load_parts(header, state_raw, target, content)
}

/// Loads a save in either envelope: a binary `FGSV` save or UTF-8 JSON. Never panics.
pub fn load_save_bytes(bytes: &[u8], target: &SaveTarget, content: &GameContent) -> LoadedSave {
    if !is_binary_save(bytes) {
        return match std::str::from_utf8(bytes) {
            Ok(text) => load_save(text, target, content),
            Err(error) => refused(None, format!("Save file is neither a save nor UTF-8 JSON: {error}")),
        };
    }
    let (header, _preview, state_json) = match read_binary(bytes, true) {
        Ok(parts) => parts,
        Err((header, error)) => return refused(header, error),
    };
    let raw: Value = match serde_json::from_slice(&state_json.unwrap_or_default()) {
        Ok(raw) => raw,
        Err(error) => return refused(Some(header), format!("Save state is not valid JSON: {error}")),
    };
    load_parts(Some(header), &raw, target, content)
}

fn load_parts(header: Option<SaveHeader>, state_raw: &Value, target: &SaveTarget, content: &GameContent) -> LoadedSave {
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

    // A tile grid that doesn't match its scene's size (a hand-edited save) is fixed before the
    // simulation reads it.
    let repaired = farm_sim::state::normalize_world(&mut state);
    if !repaired.is_empty() {
        warnings.push(format!(
            "The map of {} in this save did not match its size and was repaired.",
            repaired.iter().map(|id| format!("'{id}'")).collect::<Vec<_>>().join(", ")
        ));
    }

    let same_content = header.as_ref().is_some_and(|h| h.cart_hash == target.cart_hash);
    let (quarantined, restored) =
        if same_content { (Vec::new(), Vec::new()) } else { reconcile_items(&mut state, content) };
    // A save written by another version of this game takes its maps; a headerless save may be
    // from this very content, and a rebase would undo what events changed on the map.
    if header.is_some() && !same_content {
        rebase_world(&mut state, content);
    }
    if !same_content {
        add_missing_npcs(&mut state, content);
    }
    // Older saves have no day of season, and season lengths may have changed (#23).
    game_time::reconcile_clock(&content.settings.calendar, &mut state.clock);
    // An all-zero random state draws 0 forever: every chance roll succeeds (#140).
    if state.rng.is_degenerate() {
        state.rng = rng::create_rng_state(&format!("{}:{}", state.meta.engine_seed, state.clock.tick));
        warnings.push("This save had no random number state; a new one was started.".to_owned());
    }
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

/// What a save slot shows without loading the game (`SavePreview` in `schemas/save.fbs`).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct SavePreview {
    pub farm_name: String,
    pub day: f64,
    pub season: String,
    pub year: f64,
    pub money: f64,
    /// Wall-clock seconds played.
    pub play_seconds: f64,
    /// Seconds since the Unix epoch when the save was written (0 when unknown).
    pub saved_at: i64,
    /// A small PNG of the scene (empty when none).
    pub thumbnail_png: Vec<u8>,
    /// The day within `season` (0 in saves written before it existed: derive it from `day`).
    pub day_of_season: f64,
}

impl SavePreview {
    /// The in-game date and money of `state`; the host adds the name, play time, clock time and
    /// thumbnail it knows.
    pub fn of_state(state: &GameState) -> Self {
        Self {
            // The FlatBuffers fields stay `double` (a schema field is never retyped); they hold
            // whole numbers.
            day: f64::from(state.clock.day),
            season: state.clock.season.clone(),
            day_of_season: f64::from(state.clock.day_of_season),
            year: f64::from(state.clock.year),
            money: state.player.money as f64,
            ..Self::default()
        }
    }
}

/// Compression byte of a binary save.
const COMPRESSION_STORED: u8 = 0;
const COMPRESSION_ZSTD: u8 = 1;

/// Largest decompressed state a save may claim (a guard against decompression bombs).
const MAX_STATE_BYTES: usize = 256 * 1024 * 1024;

/// Is `bytes` a binary (`FGSV`) save?
pub fn is_binary_save(bytes: &[u8]) -> bool {
    bytes.len() >= 8 && save_file_buffer_has_identifier(bytes)
}

/// Serializes a binary save: header, `preview`, and the state as zstd-compressed stable JSON.
/// The same state, target and preview always give the same bytes.
pub fn write_save_binary(state: &GameState, target: &SaveTarget, preview: &SavePreview) -> Vec<u8> {
    let json = stable_json::stringify(state);
    let compressed = ruzstd::encoding::compress_to_vec(json.as_bytes(), ruzstd::encoding::CompressionLevel::Fastest);
    let mut builder = FlatBufferBuilder::with_capacity(compressed.len() + preview.thumbnail_png.len() + 1024);
    let farm_name = builder.create_string(&preview.farm_name);
    let season = builder.create_string(&preview.season);
    let thumbnail = builder.create_vector(&preview.thumbnail_png);
    let preview_offset = fb_save::SavePreview::create(
        &mut builder,
        &fb_save::SavePreviewArgs {
            farm_name: Some(farm_name),
            day: preview.day,
            season: Some(season),
            year: preview.year,
            money: preview.money,
            play_seconds: preview.play_seconds,
            saved_at: preview.saved_at,
            thumbnail_png: Some(thumbnail),
            day_of_season: preview.day_of_season,
        },
    );
    let game_id = builder.create_string(&target.game_id);
    let game_version = builder.create_string(&target.game_version);
    let cart_hash = builder.create_string(&target.cart_hash);
    let state_offset = builder.create_vector(&compressed);
    let root = fb_save::SaveFile::create(
        &mut builder,
        &fb_save::SaveFileArgs {
            save_format: SAVE_FORMAT,
            game_id: Some(game_id),
            game_version: Some(game_version),
            cart_hash: Some(cart_hash),
            preview: Some(preview_offset),
            compression: COMPRESSION_ZSTD,
            state: Some(state_offset),
        },
    );
    fb_save::finish_save_file_buffer(&mut builder, root);
    builder.finished_data().to_vec()
}

/// The header and slot preview of a binary save, without decompressing the state.
pub fn read_save_preview(bytes: &[u8]) -> Result<(SaveHeader, SavePreview), String> {
    read_binary(bytes, false).map(|(header, preview, _)| (header, preview)).map_err(|(_, error)| error)
}

type BinaryParts = (SaveHeader, SavePreview, Option<Vec<u8>>);

fn read_binary(bytes: &[u8], with_state: bool) -> Result<BinaryParts, (Option<SaveHeader>, String)> {
    if !is_binary_save(bytes) {
        return Err((None, "Not a Farm Engine save (FGSV identifier missing).".to_owned()));
    }
    let file = fb_save::root_as_save_file(bytes).map_err(|error| (None, format!("Save file is damaged: {error}")))?;
    let header = SaveHeader {
        format: file.save_format(),
        game_id: file.game_id().to_owned(),
        game_version: file.game_version().to_owned(),
        cart_hash: file.cart_hash().to_owned(),
    };
    let preview = file
        .preview()
        .map(|p| SavePreview {
            farm_name: p.farm_name().unwrap_or_default().to_owned(),
            day: p.day(),
            season: p.season().unwrap_or_default().to_owned(),
            year: p.year(),
            money: p.money(),
            play_seconds: p.play_seconds(),
            saved_at: p.saved_at(),
            thumbnail_png: p.thumbnail_png().map(|t| t.bytes().to_vec()).unwrap_or_default(),
            day_of_season: p.day_of_season(),
        })
        .unwrap_or_default();
    if header.format > SAVE_FORMAT {
        return Err((
            Some(header.clone()),
            format!(
                "Save file format {} is newer than this player supports ({SAVE_FORMAT}). Update the game.",
                header.format
            ),
        ));
    }
    if !with_state {
        return Ok((header, preview, None));
    }
    let data = file.state().bytes();
    let state = match file.compression() {
        COMPRESSION_STORED => data.to_vec(),
        COMPRESSION_ZSTD => decompress(data).map_err(|error| (Some(header.clone()), error))?,
        other => return Err((Some(header), format!("Save file uses an unknown compression ({other})."))),
    };
    Ok((header, preview, Some(state)))
}

fn decompress(data: &[u8]) -> Result<Vec<u8>, String> {
    use std::io::Read;
    let mut decoder = ruzstd::decoding::StreamingDecoder::new(data)
        .map_err(|error| format!("Save state could not be decompressed: {error}"))?;
    let mut out = Vec::new();
    let mut limited = (&mut decoder).take(MAX_STATE_BYTES as u64 + 1);
    limited.read_to_end(&mut out).map_err(|error| format!("Save state could not be decompressed: {error}"))?;
    if out.len() > MAX_STATE_BYTES {
        return Err("Save state is too large.".to_owned());
    }
    Ok(out)
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

/// Rebases the save's world onto the maps of `content` (the save was written by another version
/// of the game, #36). Each authored scene comes from the content, tiles, transitions and
/// collision included; on tiles both versions have, the save keeps what play made of them:
/// crops, dropped items, gathering nodes (mined or regrown), machines, soil state, moisture and
/// fertility, and hoed ground (soil where the content has grass or floor). Transitions events
/// locked or unlocked keep that state. Scenes new in the content are added; generated scenes
/// (mine floors) and scenes the content no longer has stay as saved.
pub fn rebase_world(state: &mut GameState, content: &GameContent) {
    let mut saved: Vec<Scene> = std::mem::take(&mut state.world.scenes);
    let mut scenes = Vec::with_capacity(content.scenes.len() + saved.len());
    for authored in &content.scenes {
        let mut scene = authored.clone();
        if let Some(index) = saved.iter().position(|old| old.id == authored.id) {
            let old = saved.remove(index);
            for (row, old_row) in scene.tiles.iter_mut().zip(&old.tiles) {
                for (tile, old_tile) in row.iter_mut().zip(old_row) {
                    keep_play(tile, old_tile);
                }
            }
            for transition in &mut scene.transitions {
                let same = |t: &&SceneTransition| {
                    t.from_x == transition.from_x
                        && t.from_y == transition.from_y
                        && t.to_scene_id == transition.to_scene_id
                };
                if let Some(old_transition) = old.transitions.iter().find(same) {
                    transition.locked = old_transition.locked;
                }
            }
        }
        scenes.push(scene);
    }
    scenes.extend(saved);
    state.world.scenes = scenes;
}

/// The parts of `old` (a saved tile) that play changed, onto `tile` (the content's tile).
fn keep_play(tile: &mut Tile, old: &Tile) {
    let hoed = old.background == "soil" && (tile.background == "grass" || tile.background == "floor");
    if hoed {
        tile.background = old.background.clone();
        tile.r#type = old.r#type.clone();
    }
    tile.crop = old.crop.clone();
    tile.item = old.item.clone();
    tile.node = old.node.clone();
    tile.machine = old.machine.clone();
    tile.soil_state = old.soil_state.clone();
    tile.soil_moisture = old.soil_moisture;
    tile.soil_fertility = old.soil_fertility;
}

/// Gives every NPC of `content` that the save has no state for one at its authored place (an
/// NPC added by a game update could otherwise not be talked to and never moved, #36). Returns
/// the ids added.
pub fn add_missing_npcs(state: &mut GameState, content: &GameContent) -> Vec<String> {
    let mut added = Vec::new();
    for npc in &content.npcs {
        if !state.npcs.contains_key(&npc.id) {
            state.npcs.insert(
                npc.id.clone(),
                NpcState { x: npc.x, y: npc.y, scene_id: npc.scene_id.clone(), ..NpcState::default() },
            );
            added.push(npc.id.clone());
        }
    }
    added
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
            _ => text::compare_strings(x, y),
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
