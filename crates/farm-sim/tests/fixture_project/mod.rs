//! Shared fixture for the systems tests: the `project` of
//! `fixtures/golden/content/starter-farm.json`. The C# tests build `EngineTests.MakeProject()`;
//! the Rust port builds its states from the golden starter farm instead (npc-farmer at (3, 6),
//! player at (8, 9), 100 money, spring day 1) and mutates the project per test.
#![allow(dead_code)]

use farm_sim::effects::Effect;
use farm_sim::schema::{GameProject, GameState, InventorySlot, Item};
use farm_sim::{state, EngineContext};
use serde_json::Value;
use std::path::PathBuf;

/// The golden starter farm's project, deserialized.
pub fn starter_farm_project() -> GameProject {
    let path: PathBuf =
        [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", "content", "starter-farm.json"].iter().collect();
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
    let fixture: Value = serde_json::from_str(&text).expect("valid fixture JSON");
    serde_json::from_value(fixture["project"].clone()).expect("starter-farm project is a GameProject")
}

/// C# `MakeEngine(mutate)`: content + a seeded state from the (mutated) starter project.
pub fn make_engine(seed: &str, mutate: impl FnOnce(&mut GameProject)) -> (EngineContext, GameState) {
    let mut project = starter_farm_project();
    mutate(&mut project);
    let ctx = EngineContext::new(state::create_content_from_project(&project));
    let state = state::create_game_state(&project, Some(seed));
    (ctx, state)
}

/// C# `CoreTestHelpers.Slot(items, id, quantity)`.
pub fn slot(items: &[Item], id: &str, quantity: u32) -> InventorySlot {
    let item = items.iter().find(|i| i.id == id).unwrap_or_else(|| panic!("item {id} exists")).clone();
    InventorySlot::new(item, quantity)
}

pub fn find_item<'a>(ctx: &'a EngineContext, id: &str) -> &'a Item {
    ctx.content.items.iter().find(|i| i.id == id).unwrap_or_else(|| panic!("item {id} exists in content"))
}

/// C# M4 `Give`: appends a NEW slot (no merge) so tests control the slot layout.
pub fn give(ctx: &EngineContext, state: &mut GameState, item_id: &str, quantity: u32) {
    let item = find_item(ctx, item_id).clone();
    state.player.inventory.push(InventorySlot::new(item, quantity));
}

/// C# M4 `Quantity`: the first slot's quantity for an item id.
pub fn quantity(state: &GameState, item_id: &str) -> Option<u32> {
    state.player.inventory.iter().find(|s| s.item.id == item_id).map(|s| s.quantity)
}

/// C# `At`: put the player on a tile facing a direction.
/// Stands the player on tile `(x, y)` (its corner, as v8 tests wrote tile indices).
pub fn at(state: &mut GameState, x: i32, y: i32, direction: &str) {
    state.player.x = farm_sim::units::tiles(x);
    state.player.y = farm_sim::units::tiles(y);
    state.player.direction = direction.to_owned();
}

/// C# `CoreTestHelpers.HasMessage`: TS `effects.some(e => e.type === 'message' && predicate(e.text))`.
pub fn has_message(effects: &[Effect], predicate: impl Fn(&str) -> bool) -> bool {
    effects.iter().any(|effect| matches!(effect, Effect::Message { text, .. } if predicate(text)))
}

/// The message texts in an effect list, in order.
pub fn message_texts(effects: &[Effect]) -> Vec<String> {
    effects
        .iter()
        .filter_map(|effect| match effect {
            Effect::Message { text, .. } => Some(text.clone()),
            _ => None,
        })
        .collect()
}
