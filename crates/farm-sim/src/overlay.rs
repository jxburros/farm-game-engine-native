//! The rule-derived data the play overlays read, in one batched query: the open dialogue and its
//! visible options, the open shop with today's stock remainders, recipe availability and
//! ingredients, and the tile the player faces.
//!
//! Every in-game UI (the Rust `farm-ui`, the editor's C# overlays through `farm-ffi`) asks this
//! module instead of re-deriving a rule. The UI never decides whether an option is visible, a
//! purchase allowed or a recipe craftable; it shows what this says and runs engine commands.
//! Nothing here changes state.

use crate::crafting::{self, CraftableStatus};
use crate::engine_types::EngineContext;
use crate::schema::{Dialogue, DialogueOption, GameState, ShopDefinition, Tile};
use crate::world::world_movement;
use crate::{dialogue_system, economy, social};
use serde::Serialize;
use std::collections::BTreeMap;

/// A tile coordinate (the facing tile). May lie outside the scene.
#[derive(Debug, Clone, Copy, PartialEq, Serialize)]
pub struct FacingTile {
    pub x: i32,
    pub y: i32,
}

/// One read of everything the overlays need. The JSON form (camelCase) is what the editor's
/// overlays deserialize (`fe_session_overlay_json`). A `null` stock remainder means unlimited:
/// JSON cannot carry positive infinity.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OverlayView<'a> {
    pub dialogue: Option<&'a Dialogue>,
    /// The options the player may pick, in order: `chooseDialogueOption` indexes this list.
    pub visible_dialogue_options: Vec<DialogueOption>,
    pub shop: Option<&'a ShopDefinition>,
    /// Stock left today per item id of the open shop (`None`: unlimited).
    pub stock_remaining: BTreeMap<&'a str, Option<u32>>,
    pub facing: FacingTile,
    /// Hand and machine recipes by id.
    pub craftable: BTreeMap<&'a str, CraftableStatus>,
    /// Whether the inventory holds every input of a recipe (machine loading).
    pub has_ingredients: BTreeMap<&'a str, bool>,
}

/// Builds the overlay view for `state`.
pub fn overlay_view<'a>(ctx: &'a EngineContext, state: &GameState) -> OverlayView<'a> {
    let dialogue = state
        .dialogue
        .as_ref()
        .and_then(|active| dialogue_system::find_dialogue(ctx, &active.npc_id, &active.dialogue_id));
    let visible_dialogue_options =
        dialogue.map(|dialogue| social::visible_dialogue_options(ctx, state, dialogue)).unwrap_or_default();
    let shop = state.shop.as_ref().and_then(|active| economy::find_shop(ctx, &active.shop_id));
    let mut stock_remaining = BTreeMap::new();
    if let Some(shop) = shop {
        for entry in &shop.stock {
            let remaining = economy::remaining_daily_stock(state, &shop.id, &entry.item_id, entry.daily_limit);
            stock_remaining.insert(entry.item_id.as_str(), remaining);
        }
    }
    let facing = world_movement::facing_target(state);
    let mut craftable = BTreeMap::new();
    let mut has_ingredients = BTreeMap::new();
    for recipe in &ctx.content.recipes {
        craftable.insert(recipe.id.as_str(), crafting::craftable_status(ctx, state, recipe));
        has_ingredients.insert(recipe.id.as_str(), crafting::has_ingredients(state, recipe));
    }
    OverlayView {
        dialogue,
        visible_dialogue_options,
        shop,
        stock_remaining,
        facing: FacingTile { x: facing.x, y: facing.y },
        craftable,
        has_ingredients,
    }
}

impl OverlayView<'_> {
    /// Units of `item_id` the open shop still sells today (`None` when unlimited or unknown).
    pub fn remaining(&self, item_id: &str) -> Option<u32> {
        self.stock_remaining.get(item_id).copied().flatten()
    }

    /// Whether a recipe can be crafted now, with the reason when not (unknown recipes are not).
    pub fn status(&self, recipe_id: &str) -> CraftableStatus {
        self.craftable.get(recipe_id).cloned().unwrap_or_default()
    }

    /// Whether the inventory holds every input of a recipe.
    pub fn ingredients(&self, recipe_id: &str) -> bool {
        self.has_ingredients.get(recipe_id).copied().unwrap_or(false)
    }

    /// The tile the player faces in the current scene, when it lies inside it.
    pub fn facing_tile<'s>(&self, state: &'s GameState) -> Option<&'s Tile> {
        let scene = state.world.scenes.iter().find(|scene| scene.id == state.player.scene_id)?;
        let (x, y) = (self.facing.x, self.facing.y);
        let (x, y) = (usize::try_from(x).ok()?, usize::try_from(y).ok()?);
        scene.tiles.get(y)?.get(x)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schema::{DialogueState, GameProject, ShopSession};

    fn starter() -> GameProject {
        let fixture: serde_json::Value =
            serde_json::from_str(include_str!("../../../fixtures/golden/content/starter-farm.json")).unwrap();
        serde_json::from_value(fixture["project"].clone()).unwrap()
    }

    #[test]
    fn reads_dialogue_shop_recipes_and_the_facing_tile() {
        let project = starter();
        let ctx = EngineContext::new(crate::create_content_from_project(&project));
        let mut state = crate::create_game_state(&project, Some("overlay"));
        let npc = ctx.content.npcs.iter().find(|npc| !npc.dialogue.is_empty()).unwrap();
        state.dialogue = Some(DialogueState { npc_id: npc.id.clone(), dialogue_id: npc.dialogue[0].id.clone() });
        let shop = ctx.content.shops[0].clone();
        state.shop = Some(ShopSession { shop_id: shop.id.clone() });
        let view = overlay_view(&ctx, &state);
        assert_eq!(view.dialogue.map(|d| d.id.as_str()), Some(npc.dialogue[0].id.as_str()));
        assert_eq!(view.visible_dialogue_options, social::visible_dialogue_options(&ctx, &state, &npc.dialogue[0]));
        assert_eq!(view.shop.map(|s| s.id.as_str()), Some(shop.id.as_str()));
        assert_eq!(view.stock_remaining.len(), shop.stock.len());
        assert_eq!(view.craftable.len(), ctx.content.recipes.len());
        assert_eq!(view.remaining("no-such-item"), None);
        assert!(!view.status("no-such-recipe").craftable);
        assert!(!view.ingredients("no-such-recipe"));
        let target = world_movement::facing_target(&state);
        assert_eq!((view.facing.x, view.facing.y), (target.x, target.y));
        let tile = view.facing_tile(&state).unwrap();
        assert_eq!((tile.x, tile.y), (target.x, target.y));
        let json = serde_json::to_value(&view).unwrap();
        assert!(json.get("visibleDialogueOptions").is_some() && json.get("hasIngredients").is_some());
    }
}
