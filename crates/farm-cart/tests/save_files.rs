//! Save files across game updates (docs/EXPORT.md "Tests": a save from version 1 of a game still
//! loads after content is added to and removed from that game).

use farm_cart::save_file::{cart_hash, compare_versions};
use farm_cart::{load_save, write_save, SaveTarget};
use farm_sim::schema::{GameContent, GameProject, GameState, InventorySlot};
use farm_sim::{stable_stringify, state};
use std::cmp::Ordering;
use std::path::PathBuf;

fn starter_project() -> GameProject {
    let path: PathBuf =
        [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", "content", "starter-farm.json"].iter().collect();
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
    let fixture: serde_json::Value = serde_json::from_str(&text).expect("valid fixture JSON");
    serde_json::from_value(fixture["project"].clone()).expect("starter-farm project")
}

fn build(project: &GameProject) -> (GameContent, SaveTarget) {
    let content = state::create_content_from_project(project);
    let target = SaveTarget::for_project(project, &content);
    (content, target)
}

fn inventory_ids(state: &GameState) -> Vec<&str> {
    state.player.inventory.iter().map(|slot| slot.item.id.as_str()).collect()
}

#[test]
fn a_save_round_trips_unchanged_on_the_same_cartridge() {
    let project = starter_project();
    let (content, target) = build(&project);
    let state = state::create_game_state(&project, Some("save-file"));
    let text = write_save(&state, &target);
    assert_eq!(text, write_save(&state, &target), "writing is deterministic");

    let loaded = load_save(&text, &target, &content);
    assert!(loaded.ok, "{:?}", loaded.errors);
    assert_eq!(loaded.warnings, Vec::<String>::new());
    assert_eq!(stable_stringify(loaded.state.as_ref().unwrap()), stable_stringify(&state));
    assert_eq!(loaded.header.expect("header").game_id, project.id);
    assert!(!loaded.migrated);
}

#[test]
fn a_save_from_another_game_is_refused() {
    let project = starter_project();
    let (content, target) = build(&project);
    let state = state::create_game_state(&project, Some("save-file"));
    let text = write_save(&state, &SaveTarget { game_id: "someone-else".to_owned(), ..target.clone() });
    let loaded = load_save(&text, &target, &content);
    assert!(!loaded.ok);
    assert!(loaded.state.is_none());
    assert_eq!(
        loaded.errors,
        vec![format!("This save belongs to a different game ('someone-else', not '{}').", project.id)]
    );
}

#[test]
fn a_save_from_a_newer_game_version_loads_with_a_warning() {
    let project = starter_project();
    let (content, target) = build(&project);
    let state = state::create_game_state(&project, Some("save-file"));
    let text = write_save(&state, &SaveTarget { game_version: "99.0".to_owned(), ..target.clone() });
    let loaded = load_save(&text, &target, &content);
    assert!(loaded.ok);
    assert_eq!(loaded.warnings.len(), 1);
    assert!(loaded.warnings[0].contains("newer version of the game (99.0"), "{}", loaded.warnings[0]);
}

#[test]
fn a_newer_save_format_is_refused() {
    let project = starter_project();
    let (content, target) = build(&project);
    let state = state::create_game_state(&project, Some("save-file"));
    let text = write_save(&state, &target).replacen("\"format\":1", "\"format\":2", 1);
    let loaded = load_save(&text, &target, &content);
    assert!(!loaded.ok);
    assert!(loaded.errors[0].starts_with("Save file format 2 is newer"), "{:?}", loaded.errors);
}

#[test]
fn saves_survive_items_being_added_changed_and_removed() {
    // Version 1 of the game: the save holds some of its items.
    let v1 = starter_project();
    let (_, target_v1) = build(&v1);
    let mut save_state = state::create_game_state(&v1, Some("save-file"));
    let removed_id = save_state.player.inventory[0].item.id.clone();
    let kept_id = save_state.player.inventory[1].item.id.clone();
    let text = write_save(&save_state, &target_v1);

    // Version 1.1: the creator removes one item, reprices another and adds a new one.
    let mut v11 = v1.clone();
    v11.version = "1.1".to_owned();
    v11.items.retain(|item| item.id != removed_id);
    for item in &mut v11.items {
        if item.id == kept_id {
            item.value += 7.0;
        }
    }
    let mut added = v11.items[0].clone();
    added.id = "item-added-in-1-1".to_owned();
    v11.items.push(added);
    let (content_v11, target_v11) = build(&v11);
    assert_ne!(target_v1.cart_hash, target_v11.cart_hash);

    let loaded = load_save(&text, &target_v11, &content_v11);
    assert!(loaded.ok, "{:?}", loaded.errors);
    let state = loaded.state.expect("state");
    assert_eq!(loaded.quarantined, vec![removed_id.clone()]);
    assert!(!inventory_ids(&state).contains(&removed_id.as_str()));
    assert_eq!(
        state.quarantined_items.iter().map(|s| s.item.id.as_str()).collect::<Vec<_>>(),
        vec![removed_id.as_str()]
    );
    assert!(loaded.warnings.iter().any(|w| w.contains(&removed_id)), "{:?}", loaded.warnings);
    // The kept item takes the 1.1 definition.
    let kept = state.player.inventory.iter().find(|s| s.item.id == kept_id).expect("kept slot");
    let current = content_v11.items.iter().find(|i| i.id == kept_id).unwrap();
    assert_eq!(kept.item, *current);

    // Version 1.2 brings the removed item back: the quarantined slot returns.
    save_state = state;
    let text = write_save(&save_state, &target_v11);
    let mut v12 = v1.clone();
    v12.version = "1.2".to_owned();
    let (content_v12, target_v12) = build(&v12);
    let loaded = load_save(&text, &target_v12, &content_v12);
    assert!(loaded.ok);
    assert_eq!(loaded.restored, vec![removed_id.clone()]);
    let state = loaded.state.unwrap();
    assert!(inventory_ids(&state).contains(&removed_id.as_str()));
    assert!(state.quarantined_items.is_empty());
}

#[test]
fn bare_web_saves_load_and_reconcile() {
    let project = starter_project();
    let (content, target) = build(&project);
    let mut state = state::create_game_state(&project, Some("save-file"));
    let mut ghost = state.player.inventory[0].clone();
    ghost.item.id = "item-from-nowhere".to_owned();
    state.player.inventory.push(InventorySlot { quantity: 2.0, ..ghost });
    let bare = serde_json::to_string(&state).unwrap();

    let loaded = load_save(&bare, &target, &content);
    assert!(loaded.ok, "{:?}", loaded.errors);
    assert!(loaded.header.is_none());
    assert_eq!(loaded.quarantined, vec!["item-from-nowhere".to_owned()]);
}

#[test]
fn old_web_saves_are_migrated_on_load() {
    let path: PathBuf =
        [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", "saves", "save-v1.json"].iter().collect();
    let fixture: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap();
    let project = starter_project();
    let (content, target) = build(&project);
    let loaded = load_save(&fixture["input"].to_string(), &target, &content);
    assert!(loaded.ok, "{:?}", loaded.errors);
    assert!(loaded.migrated);
    assert_eq!(loaded.from_version, 1.0);
}

#[test]
fn garbage_is_refused_softly() {
    let project = starter_project();
    let (content, target) = build(&project);
    assert!(load_save("not json", &target, &content).errors[0].starts_with("Save file is not valid JSON"));
    assert_eq!(load_save("42", &target, &content).errors, vec!["Save state is not an object"]);
    let bad_header = r#"{"header": {"format": "one"}, "state": {}}"#;
    assert!(load_save(bad_header, &target, &content).errors[0].starts_with("Save header is not valid"));
}

#[test]
fn versions_compare_numerically() {
    assert_eq!(compare_versions("1.10", "1.9"), Ordering::Greater);
    assert_eq!(compare_versions("1.0", "1"), Ordering::Equal);
    assert_eq!(compare_versions("v2.0", "1.5.3"), Ordering::Greater);
    assert_eq!(compare_versions("1.0-beta", "1.0-alpha"), Ordering::Greater);
    assert_eq!(compare_versions("0.9", "1.0"), Ordering::Less);
}

#[test]
fn the_cart_hash_follows_content() {
    let project = starter_project();
    let (content, _) = build(&project);
    assert_eq!(cart_hash(&content), cart_hash(&content.clone()));
    assert_eq!(cart_hash(&content).len(), 16);
}

#[test]
fn binary_saves_round_trip_and_carry_a_preview() {
    use farm_cart::{is_binary_save, load_save_bytes, read_save_preview, write_save_binary, SavePreview};
    let project = starter_project();
    let (content, target) = build(&project);
    let state = state::create_game_state(&project, Some("binary-save"));
    let mut preview = SavePreview::of_state(&state);
    preview.farm_name = "Willow Creek".to_owned();
    preview.play_seconds = 125.5;
    preview.saved_at = 1_790_000_000;
    preview.thumbnail_png = vec![0x89, b'P', b'N', b'G'];
    let bytes = write_save_binary(&state, &target, &preview);
    assert!(is_binary_save(&bytes));
    assert_eq!(bytes, write_save_binary(&state, &target, &preview), "writing is deterministic");
    // zstd keeps saves small: well under the stable JSON size.
    assert!(bytes.len() * 2 < write_save(&state, &target).len(), "{} bytes", bytes.len());

    let (header, read_preview) = read_save_preview(&bytes).unwrap();
    assert_eq!(header.game_id, target.game_id);
    assert_eq!(header.cart_hash, target.cart_hash);
    assert_eq!(read_preview, preview);

    let loaded = load_save_bytes(&bytes, &target, &content);
    assert!(loaded.ok, "{:?}", loaded.errors);
    assert_eq!(stable_stringify(loaded.state.as_ref().unwrap()), stable_stringify(&state));

    // The same loader still reads JSON saves.
    let json = write_save(&state, &target);
    assert!(load_save_bytes(json.as_bytes(), &target, &content).ok);
}

#[test]
fn damaged_or_foreign_binary_saves_are_refused() {
    use farm_cart::{load_save_bytes, write_save_binary, SavePreview};
    let project = starter_project();
    let (content, target) = build(&project);
    let state = state::create_game_state(&project, Some("binary-save"));
    let bytes = write_save_binary(&state, &target, &SavePreview::default());

    let other = SaveTarget { game_id: "other.game".to_owned(), ..target.clone() };
    let refused = load_save_bytes(&bytes, &other, &content);
    assert!(!refused.ok);
    assert!(refused.errors[0].starts_with("This save belongs to a different game"), "{:?}", refused.errors);

    let mut truncated = bytes.clone();
    truncated.truncate(bytes.len() / 2);
    assert!(!load_save_bytes(&truncated, &target, &content).ok);

    // Corrupt the compressed state (the last bytes of the buffer belong to it).
    let mut corrupt = bytes.clone();
    let len = corrupt.len();
    for byte in &mut corrupt[len - 40..len - 8] {
        *byte ^= 0x5a;
    }
    let damaged = load_save_bytes(&corrupt, &target, &content);
    assert!(!damaged.ok);

    assert!(!load_save_bytes(&[0xff, 0xfe, 0x00], &target, &content).ok);
}
