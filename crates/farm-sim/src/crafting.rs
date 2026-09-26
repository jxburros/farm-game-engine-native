//! Crafting and machines (port of `Crafting.cs` / crafting.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::{GameState, MachineTypeDefinition, RecipeDefinition};
use indexmap::IndexSet;

/// Why a recipe can't be crafted right now (TS `CraftableStatus.reason`).
pub mod craft_block_reasons {
    pub const LOCKED: &str = "locked";
    pub const INGREDIENTS: &str = "ingredients";
    pub const STATION: &str = "station";
}

/// TS `CraftableStatus`. `reason` is one of [`craft_block_reasons`].
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CraftableStatus {
    pub craftable: bool,
    pub reason: Option<String>,
    pub message: Option<String>,
}

pub fn absolute_minute(state: &GameState) -> f64 {
    let _ = state;
    todo!("port Crafting.AbsoluteMinute")
}

pub fn recipe_by_id<'a>(ctx: &'a EngineContext, recipe_id: &str) -> Option<&'a RecipeDefinition> {
    let _ = (ctx, recipe_id);
    todo!("port Crafting.RecipeById")
}

pub fn is_recipe_unlocked(ctx: &EngineContext, state: &GameState, recipe: &RecipeDefinition) -> bool {
    let _ = (ctx, state, recipe);
    todo!("port Crafting.IsRecipeUnlocked")
}

pub fn has_ingredients(state: &GameState, recipe: &RecipeDefinition) -> bool {
    let _ = (state, recipe);
    todo!("port Crafting.HasIngredients")
}

pub fn nearby_station_categories(ctx: &EngineContext, state: &GameState) -> IndexSet<String> {
    let _ = (ctx, state);
    todo!("port Crafting.NearbyStationCategories")
}

pub fn station_providing<'a>(ctx: &'a EngineContext, category: &str) -> Option<&'a MachineTypeDefinition> {
    let _ = (ctx, category);
    todo!("port Crafting.StationProviding")
}

pub fn craftable_status(ctx: &EngineContext, state: &GameState, recipe: &RecipeDefinition) -> CraftableStatus {
    let _ = (ctx, state, recipe);
    todo!("port Crafting.CraftableStatus")
}

pub fn handle_craft(ctx: &EngineContext, state: &mut GameState, recipe_id: &str) -> Effects {
    let _ = (ctx, state, recipe_id);
    todo!("port Crafting.HandleCraft")
}

pub fn handle_place_machine(ctx: &EngineContext, state: &mut GameState, machine_type_id: &str) -> Effects {
    let _ = (ctx, state, machine_type_id);
    todo!("port Crafting.HandlePlaceMachine")
}

pub fn handle_machine_load(ctx: &EngineContext, state: &mut GameState, recipe_id: &str) -> Effects {
    let _ = (ctx, state, recipe_id);
    todo!("port Crafting.HandleMachineLoad")
}

/// C# `SettleMachines` returned a new state; here it settles in place.
pub fn settle_machines(ctx: &EngineContext, state: &mut GameState) {
    let _ = (ctx, state);
    todo!("port Crafting.SettleMachines")
}

pub fn collect_machine_output(ctx: &EngineContext, state: &mut GameState, scene_id: &str, x: f64, y: f64) -> Effects {
    let _ = (ctx, state, scene_id, x, y);
    todo!("port Crafting.CollectMachineOutput")
}
