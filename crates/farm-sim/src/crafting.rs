//! Crafting and machines (port of `Crafting.cs` / crafting.ts).
//!
//! Crafting & machines (M4a). Hand recipes craft instantly; machine recipes load into a placed
//! machine and complete after processingMinutes of game time (absolute-minute comparison makes
//! overnight catch-up automatic).

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::hooks::{HookEvent, RecipeCraftHookPayload};
use crate::inventory;
use crate::messages::{self, Message};
use crate::mines;
use crate::quests;
use crate::schema::{
    GameState, InventorySlot, MachineProcessing, MachineTypeDefinition, RecipeDefinition, RecipeIngredient, TileMachine,
};
use crate::skills;
use crate::units;
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
    /// Why it can't be crafted (serialized as its English text).
    pub message: Option<Message>,
}

/// `!string.IsNullOrEmpty(value)`: the string when it is present and non-empty.
fn non_empty(value: Option<&str>) -> Option<&str> {
    value.filter(|s| !s.is_empty())
}

/// Absolute game time since day 1, 00:00, in micro-minutes.
pub fn absolute_minute(state: &GameState) -> i64 {
    (i64::from(state.clock.day) - 1) * i64::from(units::MINUTES_PER_DAY) * i64::from(units::MINUTE)
        + i64::from(state.clock.time_minutes)
}

/// The crafting recipe `recipe_id`.
pub fn recipe_by_id<'a>(ctx: &'a EngineContext, recipe_id: &str) -> Option<&'a RecipeDefinition> {
    ctx.recipe(recipe_id)
}

/// Whether the player meets every requirement of the recipe's `unlock` (skill level, completed
/// quest, season); a recipe without one is always available.
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

/// Whether the inventory holds every input of `recipe` in its quantity (summed over stacks).
pub fn has_ingredients(state: &GameState, recipe: &RecipeDefinition) -> bool {
    recipe.inputs.iter().all(|input| {
        let mut held: u64 = 0;
        for slot in &state.player.inventory {
            if slot.item.id == input.item_id {
                held += u64::from(slot.quantity);
            }
        }
        held >= u64::from(input.quantity)
    })
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

    let player = world_movement::player_tile(state);
    for y in player.y - 1..=player.y + 1 {
        for x in player.x - 1..=player.x + 1 {
            if let Some(machine) = scene.tile(x, y).and_then(|tile| tile.machine.as_ref()) {
                let machine_type = ctx.machine_type(&machine.type_id);
                for category in machine_type.map(|r#type| r#type.station_categories.as_slice()).unwrap_or(&[]) {
                    categories.insert(category.clone());
                }
            }
        }
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
            message: Some(messages::RECIPE_LOCKED.with(&[])),
        };
    }
    if !has_ingredients(state, recipe) {
        return CraftableStatus {
            craftable: false,
            reason: Some(craft_block_reasons::INGREDIENTS.to_owned()),
            message: Some(messages::MISSING_INGREDIENTS.with(&[])),
        };
    }
    if let Some(category) = non_empty(recipe.requires_station_category.as_deref()) {
        if !nearby_station_categories(ctx, state).contains(category) {
            let station = station_providing(ctx, category);
            let message = match station {
                Some(station) => messages::NEED_STATION.with(&[&station.name]),
                None => messages::NEED_STATION_CATEGORY.with(&[&category]),
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

/// C# `ConsumeInputs`: the inventory without every recipe input.
fn consume_inputs(inventory: &[InventorySlot], recipe: &RecipeDefinition) -> Vec<InventorySlot> {
    let mut inventory = inventory.to_vec();
    for input in &recipe.inputs {
        inventory = inventory::remove_item(&inventory, &input.item_id, input.quantity);
    }
    inventory
}

/// The inventory after granting every output, and the "Crafted …" messages; `None` when any
/// output doesn't fit. Outputs are all-or-nothing: a recipe with several outputs never grants
/// some of them.
struct GrantedOutputs {
    inventory: Vec<InventorySlot>,
    effects: Effects,
}

/// C# `GrantOutputs`, pure over the inventory. Outputs naming an unknown item are skipped.
fn grant_outputs(
    ctx: &EngineContext,
    inventory: &[InventorySlot],
    max_inventory_size: u32,
    outputs: &[RecipeIngredient],
) -> Option<GrantedOutputs> {
    let mut inventory = inventory.to_vec();
    let mut effects = Vec::new();
    for output in outputs {
        let Some(item) = ctx.item(&output.item_id) else {
            continue;
        };
        let result = inventory::add_item(&inventory, item, output.quantity, max_inventory_size, None);
        if !result.added {
            return None;
        }
        inventory = result.inventory;
        effects.push(Effect::say(message_levels::SUCCESS, messages::CRAFTED.with(&[&output.quantity, &item.name])));
    }
    Some(GrantedOutputs { inventory, effects })
}

/// Craft objectives count the items made: one progress call per output, with its quantity. A
/// hand craft also reports its recipe id once, as it always did, so objectives written against
/// recipe ids (older hand-written content) keep counting.
fn progress_craft_quests(
    ctx: &EngineContext,
    state: &mut GameState,
    recipe_id: Option<&str>,
    outputs: &[RecipeIngredient],
) -> Effects {
    let mut effects = Vec::new();
    if let Some(recipe_id) = recipe_id {
        effects.extend(quests::progress_quests(ctx, state, "craft", recipe_id, 1));
    }
    for output in outputs.iter().filter(|output| Some(output.item_id.as_str()) != recipe_id) {
        effects.extend(quests::progress_quests(ctx, state, "craft", &output.item_id, output.quantity));
    }
    effects
}

/// The message for outputs that don't fit.
fn no_room() -> Effect {
    Effect::say(message_levels::ERROR, &messages::INVENTORY_FULL)
}

/// Hand-craft an instant recipe. Nothing happens (no ingredients used, no hooks, no quest
/// progress) unless every output fits in the inventory left after the ingredients are used.
pub fn handle_craft(ctx: &EngineContext, state: &mut GameState, recipe_id: &str) -> Effects {
    let Some(recipe) = recipe_by_id(ctx, recipe_id) else {
        return vec![Effect::say(message_levels::ERROR, &messages::UNKNOWN_RECIPE)];
    };
    if non_empty(recipe.machine_type_id.as_deref()).is_some() {
        return vec![Effect::say(message_levels::INFO, &messages::RECIPE_NEEDS_MACHINE)];
    }
    let status = craftable_status(ctx, state, recipe);
    if !status.craftable {
        return vec![Effect::say(
            message_levels::ERROR,
            status.message.unwrap_or_else(|| messages::CANNOT_CRAFT.with(&[])),
        )];
    }

    let consumed = consume_inputs(&state.player.inventory, recipe);
    let Some(granted) = grant_outputs(ctx, &consumed, state.player.max_inventory_size, &recipe.outputs) else {
        return vec![no_room()];
    };
    state.player.inventory = granted.inventory;
    ctx.emit(HookEvent::RecipeCraft(RecipeCraftHookPayload { recipe_id: recipe_id.to_owned() }));

    let mut effects = granted.effects;
    effects.extend(progress_craft_quests(ctx, state, Some(recipe_id), &recipe.outputs));
    effects
}

/// Place a machine (consumes its item) on the facing tile.
pub fn handle_place_machine(ctx: &EngineContext, state: &mut GameState, machine_type_id: &str) -> Effects {
    let Some(machine_type) = ctx.machine_type(machine_type_id) else {
        return vec![Effect::say(message_levels::ERROR, &messages::UNKNOWN_MACHINE)];
    };

    if let Some(item_id) = non_empty(machine_type.item_id.as_deref()) {
        let held = state.player.inventory.iter().any(|slot| slot.item.id == item_id);
        if !held {
            return vec![Effect::say(message_levels::ERROR, messages::NEED_MACHINE_ITEM.with(&[&machine_type.name]))];
        }
    }

    let Some(scene) = world_movement::find_scene(state, &state.player.scene_id) else {
        return Vec::new();
    };
    let world_movement::TilePoint { x, y } = world_movement::facing_target(state);
    // Outside the scene (or a tile a ragged grid lacks): the command is a no-op.
    let Some(tile) = scene.tile(x, y) else {
        return Vec::new();
    };
    if tile.collision || tile.crop.is_some() || tile.node.is_some() || tile.machine.is_some() || tile.item.is_some() {
        return vec![Effect::say(message_levels::ERROR, &messages::NO_ROOM_TO_PLACE)];
    }
    let npc_there =
        state.npcs.values().any(|npc| npc.scene_id == scene.id && npc.x == units::tiles(x) && npc.y == units::tiles(y));
    if npc_there {
        return vec![Effect::say(message_levels::ERROR, &messages::NO_ROOM_TO_PLACE)];
    }
    if keeps_clear(ctx, state, &scene.id, x, y) {
        return vec![Effect::say(message_levels::ERROR, &messages::SPOT_MUST_STAY_CLEAR)];
    }

    let scene_id = scene.id.clone();
    let Some(scene_index) = state.world.scenes.iter().position(|s| s.id == scene_id) else {
        return Vec::new();
    };
    if let Some(tile) = state.world.scenes[scene_index].tile_mut(x, y) {
        tile.machine = Some(TileMachine { type_id: machine_type_id.to_owned(), ..TileMachine::default() });
    }

    if let Some(item_id) = non_empty(machine_type.item_id.as_deref()) {
        state.player.inventory = inventory::remove_item(&state.player.inventory, item_id, 1);
    }

    vec![Effect::say(message_levels::SUCCESS, messages::PLACED.with(&[&machine_type.name]))]
}

/// Tiles a placed machine would make unusable, because the player has to walk onto them or
/// interact with them: a door's tile, a door's arrival tile, the mine entrance, and the exit tile
/// of a mine floor.
fn keeps_clear(ctx: &EngineContext, state: &GameState, scene_id: &str, x: i32, y: i32) -> bool {
    let world = state.world.scenes.iter();
    // Pack scenes join the world on first visit; their doors count already.
    let unvisited = ctx.content.scenes.iter().filter(|scene| world_movement::find_scene(state, &scene.id).is_none());
    for scene in world.chain(unvisited) {
        for transition in &scene.transitions {
            if scene.id == scene_id && transition.from_x == x && transition.from_y == y {
                return true;
            }
            if transition.to_scene_id == scene_id && transition.to_x == x && transition.to_y == y {
                return true;
            }
        }
    }
    let mine = &ctx.content.mine;
    if mine.enabled
        && mine.entrance_scene_id.as_deref() == Some(scene_id)
        && mine.entrance_x == Some(x)
        && mine.entrance_y == Some(y)
    {
        return true;
    }
    mines::is_mine_floor(state, scene_id) && (x, y) == mines::FLOOR_ENTRY
}

/// Pick the machine on the facing tile back up: its item returns to the inventory. Only an idle
/// machine can be picked up (collect its output first).
pub fn handle_pick_up_machine(ctx: &EngineContext, state: &mut GameState) -> Effects {
    let Some(scene) = world_movement::find_scene(state, &state.player.scene_id) else {
        return Vec::new();
    };
    let world_movement::TilePoint { x, y } = world_movement::facing_target(state);
    let Some(machine) = scene.tile(x, y).and_then(|tile| tile.machine.as_ref()) else {
        return vec![Effect::say(message_levels::INFO, &messages::NO_MACHINE)];
    };
    if machine.processing.is_some() {
        return vec![Effect::say(message_levels::INFO, &messages::MACHINE_STILL_WORKING)];
    }
    if machine.output.as_ref().is_some_and(|output| !output.is_empty()) {
        return vec![Effect::say(message_levels::INFO, &messages::COLLECT_FIRST)];
    }

    let scene_id = scene.id.clone();
    let machine_type = ctx.machine_type(&machine.type_id);
    let name = machine_type.map_or("Machine", |r#type| r#type.name.as_str()).to_owned();
    // The item it was placed from comes back; a machine of a removed type, or one placed
    // without an item, is just taken away.
    let item =
        machine_type.and_then(|r#type| non_empty(r#type.item_id.as_deref())).and_then(|item_id| ctx.item(item_id));
    if let Some(item) = item {
        let result = inventory::add_item(&state.player.inventory, item, 1, state.player.max_inventory_size, None);
        if !result.added {
            return vec![Effect::say(message_levels::ERROR, &messages::INVENTORY_FULL)];
        }
        state.player.inventory = result.inventory;
    }

    if let Some(tile) =
        state.world.scenes.iter_mut().find(|scene| scene.id == scene_id).and_then(|scene| scene.tile_mut(x, y))
    {
        tile.machine = None;
    }
    vec![Effect::say(message_levels::SUCCESS, messages::PICKED_UP.with(&[&name]))]
}

/// Load a recipe into the machine on the facing tile.
pub fn handle_machine_load(ctx: &EngineContext, state: &mut GameState, recipe_id: &str) -> Effects {
    let Some(scene) = world_movement::find_scene(state, &state.player.scene_id) else {
        return Vec::new();
    };
    let world_movement::TilePoint { x, y } = world_movement::facing_target(state);
    let Some(machine) = scene.tile(x, y).and_then(|tile| tile.machine.as_ref()) else {
        return vec![Effect::say(message_levels::INFO, &messages::NO_MACHINE)];
    };
    if machine.processing.is_some() {
        return vec![Effect::say(message_levels::INFO, &messages::MACHINE_BUSY)];
    }
    if machine.output.as_ref().is_some_and(|output| !output.is_empty()) {
        return vec![Effect::say(message_levels::INFO, &messages::COLLECT_FIRST)];
    }

    let Some(recipe) = recipe_by_id(ctx, recipe_id) else {
        return vec![Effect::say(message_levels::ERROR, &messages::UNKNOWN_RECIPE)];
    };
    if recipe.machine_type_id.as_deref() != Some(machine.type_id.as_str()) {
        return vec![Effect::say(message_levels::ERROR, &messages::MACHINE_CANNOT_RUN)];
    }
    if !is_recipe_unlocked(ctx, state, recipe) {
        return vec![Effect::say(message_levels::ERROR, &messages::RECIPE_LOCKED)];
    }
    if !has_ingredients(state, recipe) {
        return vec![Effect::say(message_levels::ERROR, &messages::MISSING_INGREDIENTS)];
    }

    let scene_id = scene.id.clone();
    state.player.inventory = consume_inputs(&state.player.inventory, recipe);
    let Some(scene_index) = state.world.scenes.iter().position(|s| s.id == scene_id) else {
        return Vec::new();
    };
    let completes_at_minute = absolute_minute(state) + i64::from(recipe.processing_minutes) * i64::from(units::MINUTE);
    if let Some(machine) = state.world.scenes[scene_index].tile_mut(x, y).and_then(|tile| tile.machine.as_mut()) {
        machine.processing = Some(MachineProcessing { recipe_id: recipe_id.to_owned(), completes_at_minute });
    }

    vec![Effect::say(message_levels::SUCCESS, messages::STARTED_RECIPE.with(&[&recipe.name]))]
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
pub fn collect_machine_output(ctx: &EngineContext, state: &mut GameState, scene_id: &str, x: i32, y: i32) -> Effects {
    let scene = world_movement::find_scene(state, scene_id);
    let tile = scene.and_then(|scene| scene.tile(x, y));
    let output = tile.and_then(|tile| tile.machine.as_ref()).and_then(|machine| machine.output.as_ref());
    let Some(output) = output.filter(|output| !output.is_empty()) else {
        return Vec::new();
    };
    let output = output.clone();

    // All or nothing: the goods stay in the machine until every output fits.
    let Some(granted) = grant_outputs(ctx, &state.player.inventory, state.player.max_inventory_size, &output) else {
        return vec![no_room()];
    };
    let Some(scene_index) = state.world.scenes.iter().position(|s| s.id == scene_id) else {
        return Vec::new();
    };
    state.player.inventory = granted.inventory;
    if let Some(machine) = state.world.scenes[scene_index].tile_mut(x, y).and_then(|tile| tile.machine.as_mut()) {
        machine.output = None;
    }

    let mut effects = granted.effects;
    effects.extend(progress_craft_quests(ctx, state, None, &output));
    effects
}
