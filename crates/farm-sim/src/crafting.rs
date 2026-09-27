//! Crafting and machines (port of `Crafting.cs` / crafting.ts).
//!
//! Crafting & machines (M4a). Hand recipes craft instantly; machine recipes load into a placed
//! machine and complete after processingMinutes of game time (absolute-minute comparison makes
//! overnight catch-up automatic).

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::hooks::{HookEvent, RecipeCraftHookPayload};
use crate::inventory;
use crate::js;
use crate::quests;
use crate::schema::{
    GameState, InventorySlot, MachineProcessing, MachineTypeDefinition, RecipeDefinition, RecipeIngredient, Scene,
    Tile, TileMachine,
};
use crate::skills;
use crate::world::world_movement;
use indexmap::IndexSet;
use serde::Serialize;

/// Why a recipe can't be crafted right now (TS `CraftableStatus.reason`).
pub mod craft_block_reasons {
    pub const LOCKED: &str = "locked";
    pub const INGREDIENTS: &str = "ingredients";
    pub const STATION: &str = "station";
}

/// TS `CraftableStatus`. `reason` is one of [`craft_block_reasons`].
#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct CraftableStatus {
    pub craftable: bool,
    pub reason: Option<String>,
    pub message: Option<String>,
}

/// `!string.IsNullOrEmpty(value)`: the string when it is present and non-empty.
fn non_empty(value: Option<&str>) -> Option<&str> {
    value.filter(|s| !s.is_empty())
}

/// Absolute game minute since day 1, 00:00.
pub fn absolute_minute(state: &GameState) -> f64 {
    (state.clock.day - 1.0) * 24.0 * 60.0 + state.clock.time_minutes
}

pub fn recipe_by_id<'a>(ctx: &'a EngineContext, recipe_id: &str) -> Option<&'a RecipeDefinition> {
    ctx.content.recipes.iter().find(|recipe| recipe.id == recipe_id)
}

pub fn is_recipe_unlocked(ctx: &EngineContext, state: &GameState, recipe: &RecipeDefinition) -> bool {
    let _ = ctx;
    let Some(unlock) = &recipe.unlock else {
        return true;
    };
    if let Some(skill) = &unlock.skill {
        if skills::skill_level(state, &skill.skill) < skill.level {
            return false;
        }
    }
    if let Some(quest_id) = non_empty(unlock.quest_id.as_deref()) {
        if !state.player.completed_quests.iter().any(|id| id == quest_id) {
            return false;
        }
    }
    if let Some(seasons) = unlock.seasons.as_ref().filter(|seasons| !seasons.is_empty()) {
        if !seasons.contains(&state.clock.season) {
            return false;
        }
    }
    true
}

pub fn has_ingredients(state: &GameState, recipe: &RecipeDefinition) -> bool {
    recipe.inputs.iter().all(|input| {
        let mut held = 0.0;
        for slot in &state.player.inventory {
            if slot.item.id == input.item_id {
                held += slot.quantity;
            }
        }
        held >= input.quantity
    })
}

/// TS `scene.tiles[y]?.[x]`.
fn tile_at(scene: &Scene, x: f64, y: f64) -> Option<&Tile> {
    if !(0.0..scene.tiles.len() as f64).contains(&y) || y != y.floor() {
        return None;
    }
    let row = &scene.tiles[y as usize];
    if !(0.0..row.len() as f64).contains(&x) || x != x.floor() {
        return None;
    }
    Some(&row[x as usize])
}

/// Station categories provided by machines within a 1-tile radius (8-
/// neighborhood, inclusive of the player's own tile) in the current scene.
/// Player tile is floored — correct whether coordinates are integer grid
/// cells or fractional (smooth-movement) positions.
pub fn nearby_station_categories(ctx: &EngineContext, state: &GameState) -> IndexSet<String> {
    let mut categories = IndexSet::new();
    let Some(scene) = world_movement::find_scene(state, &state.player.scene_id) else {
        return categories;
    };

    let px = state.player.x.floor();
    let py = state.player.y.floor();
    let mut y = py - 1.0;
    while y <= py + 1.0 {
        let mut x = px - 1.0;
        while x <= px + 1.0 {
            if let Some(machine) = tile_at(scene, x, y).and_then(|tile| tile.machine.as_ref()) {
                let machine_type = ctx.content.machine_types.iter().find(|r#type| r#type.id == machine.type_id);
                for category in machine_type.map(|r#type| r#type.station_categories.as_slice()).unwrap_or(&[]) {
                    categories.insert(category.clone());
                }
            }
            x += 1.0;
        }
        y += 1.0;
    }
    categories
}

/// The machine type (if any) that provides a given station category — used to name it in messages/UI.
pub fn station_providing<'a>(ctx: &'a EngineContext, category: &str) -> Option<&'a MachineTypeDefinition> {
    ctx.content.machine_types.iter().find(|r#type| r#type.station_categories.iter().any(|c| c == category))
}

/// Why a hand-craftable recipe can't be crafted right now (locked / missing
/// ingredients / missing station) — shared by the game-shell and editor
/// crafting UIs so they don't duplicate the rules.
pub fn craftable_status(ctx: &EngineContext, state: &GameState, recipe: &RecipeDefinition) -> CraftableStatus {
    if !is_recipe_unlocked(ctx, state, recipe) {
        return CraftableStatus {
            craftable: false,
            reason: Some(craft_block_reasons::LOCKED.to_owned()),
            message: Some("Recipe not unlocked yet.".to_owned()),
        };
    }
    if !has_ingredients(state, recipe) {
        return CraftableStatus {
            craftable: false,
            reason: Some(craft_block_reasons::INGREDIENTS.to_owned()),
            message: Some("Missing ingredients.".to_owned()),
        };
    }
    if let Some(category) = non_empty(recipe.requires_station_category.as_deref()) {
        if !nearby_station_categories(ctx, state).contains(category) {
            let station = station_providing(ctx, category);
            let message = match station {
                Some(station) => format!("You need to be near a {} to craft that.", station.name),
                None => format!("You need to be near a {category} station to craft that."),
            };
            return CraftableStatus {
                craftable: false,
                reason: Some(craft_block_reasons::STATION.to_owned()),
                message: Some(message),
            };
        }
    }
    CraftableStatus { craftable: true, reason: None, message: None }
}

/// C# `ConsumeInputs`: removes every recipe input from the player's inventory (in place).
fn consume_inputs(state: &mut GameState, recipe: &RecipeDefinition) {
    let mut inventory = std::mem::take(&mut state.player.inventory);
    for input in &recipe.inputs {
        inventory = inventory::remove_item(&inventory, &input.item_id, input.quantity);
    }
    state.player.inventory = inventory;
}

/// C# `GrantOutputsResult` minus the state: the inventory after granting (the caller decides
/// whether to store it), the messages, and whether every output fit.
struct GrantOutputsResult {
    inventory: Vec<InventorySlot>,
    effects: Effects,
    all_added: bool,
}

/// C# `GrantOutputs`, pure over the inventory: `CollectMachineOutput` discards a partial grant.
fn grant_outputs(
    ctx: &EngineContext,
    inventory: &[InventorySlot],
    max_inventory_size: f64,
    outputs: &[RecipeIngredient],
) -> GrantOutputsResult {
    let mut inventory = inventory.to_vec();
    let mut effects = Vec::new();
    let mut all_added = true;
    for output in outputs {
        let Some(item) = ctx.content.items.iter().find(|i| i.id == output.item_id) else {
            continue;
        };
        let result = inventory::add_item(&inventory, item, output.quantity, max_inventory_size, None);
        if result.added {
            inventory = result.inventory;
            effects.push(Effect::message(
                message_levels::SUCCESS,
                format!("Crafted {}x {}", js::num(output.quantity), item.name),
            ));
        } else {
            all_added = false;
            effects.push(Effect::message(message_levels::ERROR, "Inventory is full!"));
        }
    }
    GrantOutputsResult { inventory, effects, all_added }
}

/// Hand-craft an instant recipe.
pub fn handle_craft(ctx: &EngineContext, state: &mut GameState, recipe_id: &str) -> Effects {
    let Some(recipe) = recipe_by_id(ctx, recipe_id) else {
        return vec![Effect::message(message_levels::ERROR, "Unknown recipe.")];
    };
    if non_empty(recipe.machine_type_id.as_deref()).is_some() {
        return vec![Effect::message(message_levels::INFO, "That recipe needs a machine — load it there.")];
    }
    let status = craftable_status(ctx, state, recipe);
    if !status.craftable {
        return vec![Effect::message(
            message_levels::ERROR,
            status.message.unwrap_or_else(|| "Cannot craft that right now.".to_owned()),
        )];
    }

    consume_inputs(state, recipe);
    let granted = grant_outputs(ctx, &state.player.inventory, state.player.max_inventory_size, &recipe.outputs);
    state.player.inventory = granted.inventory;
    ctx.emit(HookEvent::RecipeCraft(RecipeCraftHookPayload { recipe_id: recipe_id.to_owned() }));

    let quest_effects = quests::progress_quests(ctx, state, "craft", recipe_id, 1.0);
    let mut effects = granted.effects;
    effects.extend(quest_effects);
    effects
}

/// Place a machine (consumes its item) on the facing tile.
pub fn handle_place_machine(ctx: &EngineContext, state: &mut GameState, machine_type_id: &str) -> Effects {
    let Some(machine_type) = ctx.content.machine_types.iter().find(|r#type| r#type.id == machine_type_id) else {
        return vec![Effect::message(message_levels::ERROR, "Unknown machine.")];
    };

    if let Some(item_id) = non_empty(machine_type.item_id.as_deref()) {
        let held = state.player.inventory.iter().any(|slot| slot.item.id == item_id);
        if !held {
            return vec![Effect::message(
                message_levels::ERROR,
                format!("You need a {} in your inventory.", machine_type.name),
            )];
        }
    }

    let Some(scene) = world_movement::find_scene(state, &state.player.scene_id) else {
        return Vec::new();
    };
    let world_movement::TilePoint { x, y } = world_movement::facing_target(state);
    if x < 0.0 || x >= scene.width || y < 0.0 || y >= scene.height {
        return Vec::new();
    }
    // `scene.tiles[y][x]`: the reference engine throws when the grid is ragged; here the
    // command is simply a no-op.
    let Some(tile) = tile_at(scene, x, y) else {
        return Vec::new();
    };
    if tile.collision || tile.crop.is_some() || tile.node.is_some() || tile.machine.is_some() || tile.item.is_some() {
        return vec![Effect::message(message_levels::ERROR, "No room to place it there.")];
    }

    let scene_id = scene.id.clone();
    let Some(scene_index) = state.world.scenes.iter().position(|s| s.id == scene_id) else {
        return Vec::new();
    };
    state.world.scenes[scene_index].tiles[y as usize][x as usize].machine =
        Some(TileMachine { type_id: machine_type_id.to_owned(), ..TileMachine::default() });

    if let Some(item_id) = non_empty(machine_type.item_id.as_deref()) {
        state.player.inventory = inventory::remove_item(&state.player.inventory, item_id, 1.0);
    }

    vec![Effect::message(message_levels::SUCCESS, format!("Placed {}", machine_type.name))]
}

/// Load a recipe into the machine on the facing tile.
pub fn handle_machine_load(ctx: &EngineContext, state: &mut GameState, recipe_id: &str) -> Effects {
    let Some(scene) = world_movement::find_scene(state, &state.player.scene_id) else {
        return Vec::new();
    };
    let world_movement::TilePoint { x, y } = world_movement::facing_target(state);
    let tile = tile_at(scene, x, y);
    let Some(machine) = tile.and_then(|tile| tile.machine.as_ref()) else {
        return vec![Effect::message(message_levels::INFO, "No machine there.")];
    };
    if machine.processing.is_some() {
        return vec![Effect::message(message_levels::INFO, "It is already working.")];
    }
    if machine.output.as_ref().is_some_and(|output| !output.is_empty()) {
        return vec![Effect::message(message_levels::INFO, "Collect the finished goods first.")];
    }

    let Some(recipe) = recipe_by_id(ctx, recipe_id) else {
        return vec![Effect::message(message_levels::ERROR, "Unknown recipe.")];
    };
    if recipe.machine_type_id.as_deref() != Some(machine.type_id.as_str()) {
        return vec![Effect::message(message_levels::ERROR, "This machine cannot run that recipe.")];
    }
    if !is_recipe_unlocked(ctx, state, recipe) {
        return vec![Effect::message(message_levels::ERROR, "Recipe not unlocked yet.")];
    }
    if !has_ingredients(state, recipe) {
        return vec![Effect::message(message_levels::ERROR, "Missing ingredients.")];
    }

    let scene_id = scene.id.clone();
    consume_inputs(state, recipe);
    let Some(scene_index) = state.world.scenes.iter().position(|s| s.id == scene_id) else {
        return Vec::new();
    };
    let completes_at_minute = absolute_minute(state) + recipe.processing_minutes;
    if let Some(machine) = state.world.scenes[scene_index].tiles[y as usize][x as usize].machine.as_mut() {
        machine.processing = Some(MachineProcessing { recipe_id: recipe_id.to_owned(), completes_at_minute });
    }

    vec![Effect::message(message_levels::SUCCESS, format!("Started {}", recipe.name))]
}

/// Settle machine jobs whose completion time has passed (called from ticks
/// and the nightly pass — overnight processing "catches up" for free since
/// completion is an absolute-minute comparison).
///
/// C# `SettleMachines` returned a new state; here it settles in place.
pub fn settle_machines(ctx: &EngineContext, state: &mut GameState) {
    let now = absolute_minute(state);

    for scene in &mut state.world.scenes {
        for row in &mut scene.tiles {
            for tile in row.iter_mut() {
                let Some(machine) = tile.machine.as_mut() else {
                    continue;
                };
                let Some(processing) = machine.processing.as_ref() else {
                    continue;
                };
                if processing.completes_at_minute > now {
                    continue;
                }
                let recipe = recipe_by_id(ctx, &processing.recipe_id);
                machine.processing = None;
                machine.output = Some(recipe.map(|recipe| recipe.outputs.clone()).unwrap_or_default());
            }
        }
    }
}

/// Collect finished machine output (invoked from interact).
pub fn collect_machine_output(ctx: &EngineContext, state: &mut GameState, scene_id: &str, x: f64, y: f64) -> Effects {
    let scene = world_movement::find_scene(state, scene_id);
    let tile = scene.and_then(|scene| tile_at(scene, x, y));
    let output = tile.and_then(|tile| tile.machine.as_ref()).and_then(|machine| machine.output.as_ref());
    let Some(output) = output.filter(|output| !output.is_empty()) else {
        return Vec::new();
    };
    let output = output.clone();

    let granted = grant_outputs(ctx, &state.player.inventory, state.player.max_inventory_size, &output);
    if !granted.all_added {
        return granted.effects;
    }
    state.player.inventory = granted.inventory;

    let Some(scene_index) = state.world.scenes.iter().position(|s| s.id == scene_id) else {
        return granted.effects;
    };
    if let Some(machine) = state.world.scenes[scene_index].tiles[y as usize][x as usize].machine.as_mut() {
        machine.output = None;
    }

    granted.effects
}
