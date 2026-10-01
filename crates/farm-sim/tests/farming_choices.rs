//! Planting choices (#25), multi-tile crops (#30), crop quality and harvest items (#32), and the
//! `waterArea` / `modifyEnergy` event outcomes (#141), on the golden starter farm (soil at
//! x 5..=11, y 4..=8; spring; the player holds wheat and tomato seeds and basic fertilizer).

mod fixture_project;

use farm_sim::commands::Command;
use farm_sim::effects::Effect;
use farm_sim::engine::apply_command;
use farm_sim::farming::{crops, multi_tile};
use farm_sim::schema::{soil_states, Crop, CustomCropDefinition, EventOutcome, GameState, SkillState, TileMachine};
use farm_sim::{content_builtin, events, units, EngineContext};
use fixture_project::{at, give, has_message, make_engine, message_texts};

fn starter() -> (EngineContext, GameState) {
    make_engine("farming-choices", |_| {})
}

fn tile_crop(state: &GameState, x: usize, y: usize) -> Option<&Crop> {
    state.world.scenes[0].tiles[y][x].crop.as_ref()
}

fn held(state: &GameState, item_id: &str) -> u32 {
    state.player.inventory.iter().filter(|slot| slot.item.id == item_id).map(|slot| slot.quantity).sum()
}

fn interact_with(seed: Option<&str>, fertilizer: Option<&str>) -> Command {
    Command::InteractWith { seed_item_id: seed.map(str::to_owned), fertilizer_item_id: fertilizer.map(str::to_owned) }
}

// --- #25: choosing the seed and the fertilizer ---

#[test]
fn interact_with_plants_the_chosen_seed_and_no_fertilizer_unless_named() {
    let (ctx, mut state) = starter();
    give(&ctx, &mut state, "seed-carrot", 3);
    at(&mut state, 6, 4, "down");
    let effects = apply_command(&ctx, &mut state, &interact_with(Some("seed-carrot"), None));
    assert_eq!(tile_crop(&state, 6, 5).map(|crop| crop.r#type.as_str()), Some("carrot"));
    assert_eq!(held(&state, "seed-carrot"), 2);
    assert_eq!(held(&state, "seed-wheat"), 10);
    assert_eq!(held(&state, "fertilizer-basic"), 10, "no fertilizer was asked for");
    assert!(has_message(&effects, |t| t == "Planted Carrot! Water it so it grows."));

    at(&mut state, 7, 4, "down");
    let effects = apply_command(&ctx, &mut state, &interact_with(Some("seed-wheat"), Some("fertilizer-basic")));
    assert_eq!(tile_crop(&state, 7, 5).map(|crop| crop.quality.as_str()), Some("silver"));
    assert_eq!(held(&state, "fertilizer-basic"), 9);
    assert!(has_message(&effects, |t| t == "Planted Wheat! (Fertilized) Water it so it grows."));
}

#[test]
fn interact_with_skips_seeds_that_cannot_grow_this_season() {
    let (ctx, mut state) = starter();
    // Fall: the first seed held (wheat grows in fall) is fine, so put strawberries first.
    state.clock.season = "fall".to_owned();
    state.player.inventory.retain(|slot| slot.item.id != "seed-wheat");
    let strawberry = ctx.content.items.iter().find(|item| item.id == "seed-strawberry").unwrap().clone();
    state.player.inventory.insert(0, farm_sim::schema::InventorySlot::new(strawberry, 2));
    give(&ctx, &mut state, "seed-carrot", 1);
    at(&mut state, 6, 4, "down");
    // Plain interact keeps the recorded games' rule: the first seed, out of season or not.
    let effects = apply_command(&ctx, &mut state, &Command::Interact);
    assert!(has_message(&effects, |t| t == "Strawberry cannot grow in fall!"));
    apply_command(&ctx, &mut state, &interact_with(None, None));
    assert_eq!(tile_crop(&state, 6, 5).map(|crop| crop.r#type.as_str()), Some("carrot"));
    assert_eq!(held(&state, "seed-strawberry"), 2);
    assert_eq!(held(&state, "fertilizer-basic"), 10, "no fertilizer unless named");
}

#[test]
fn a_chosen_seed_out_of_season_or_not_held_says_why() {
    let (ctx, mut state) = starter();
    at(&mut state, 6, 4, "down");
    let effects = apply_command(&ctx, &mut state, &interact_with(Some("seed-tomato"), None));
    assert_eq!(message_texts(&effects), vec!["Tomato can't grow in spring! It grows in summer.".to_owned()]);
    let effects = apply_command(&ctx, &mut state, &interact_with(Some("seed-pumpkin"), None));
    assert_eq!(message_texts(&effects), vec!["You have no Pumpkin Seeds to plant.".to_owned()]);
    let effects = apply_command(&ctx, &mut state, &interact_with(Some("seed-wheat"), Some("fertilizer-quality")));
    assert!(has_message(&effects, |t| t.starts_with("You have no ") && t.ends_with(" to use.")));
    assert!(tile_crop(&state, 6, 5).is_none());
}

#[test]
fn interact_with_is_a_plain_interact_away_from_soil() {
    let (ctx, mut state) = starter();
    // The farmer NPC stands at (3, 6): facing it opens the dialogue.
    at(&mut state, 3, 5, "down");
    apply_command(&ctx, &mut state, &interact_with(Some("seed-wheat"), None));
    assert!(state.dialogue.is_some());
}

// --- #30: multi-tile crops act as one crop ---

/// Plants a cauliflower (2×2, spring) with its root at (6, 5), covering (6..=7, 5..=6).
fn plant_cauliflower(ctx: &EngineContext, state: &mut GameState, fertilizer: Option<&str>) {
    give(ctx, state, "seed-cauliflower", 1);
    at(state, 6, 4, "down");
    apply_command(ctx, state, &interact_with(Some("seed-cauliflower"), fertilizer));
    for (x, y) in [(6, 5), (7, 5), (6, 6), (7, 6)] {
        assert_eq!(tile_crop(state, x, y).map(|crop| crop.r#type.as_str()), Some("cauliflower"), "({x}, {y})");
    }
}

const GROUP: [(usize, usize); 4] = [(6, 5), (7, 5), (6, 6), (7, 6)];

#[test]
fn watering_one_tile_of_a_multi_tile_crop_waters_all_of_it() {
    let (ctx, mut state) = starter();
    plant_cauliflower(&ctx, &mut state, None);
    // Water the bottom-right tile only, from below.
    at(&mut state, 7, 7, "up");
    apply_command(&ctx, &mut state, &Command::UseTool { tool: "watering-can".to_owned() });
    for (x, y) in GROUP {
        assert!(tile_crop(&state, x, y).unwrap().watered, "({x}, {y})");
    }
    apply_command(&ctx, &mut state, &Command::Sleep);
    for (x, y) in GROUP {
        assert_eq!(tile_crop(&state, x, y).unwrap().days_grown, Some(1), "({x}, {y})");
    }
}

#[test]
fn a_multi_tile_crop_is_harvested_once_and_cleared_whole() {
    let (ctx, mut state) = starter();
    plant_cauliflower(&ctx, &mut state, None);
    for (x, y) in GROUP {
        let crop = state.world.scenes[0].tiles[y][x].crop.as_mut().unwrap();
        crop.days_grown = Some(6);
    }
    // Harvest from a tile that isn't the root.
    at(&mut state, 7, 7, "up");
    let effects = apply_command(&ctx, &mut state, &Command::Interact);
    let harvested: Vec<u32> = effects
        .iter()
        .filter_map(|effect| match effect {
            Effect::CropHarvested { quantity, .. } => Some(*quantity),
            _ => None,
        })
        .collect();
    assert_eq!(harvested.len(), 1, "one harvest: {effects:?}");
    assert_eq!(held(&state, "crop-cauliflower"), harvested[0]);
    assert!(harvested[0] <= 3, "one roll of a 1..=1 yield (a mutation triples at most)");
    for (x, y) in GROUP {
        assert!(tile_crop(&state, x, y).is_none(), "({x}, {y}) cleared");
    }
    assert_eq!(state.player.skills.get("farming").map(|skill| skill.xp), Some(8));
}

#[test]
fn the_scythe_clears_a_whole_withered_multi_tile_crop() {
    let (ctx, mut state) = starter();
    give(&ctx, &mut state, "tool-scythe", 1);
    plant_cauliflower(&ctx, &mut state, None);
    for (x, y) in GROUP {
        state.world.scenes[0].tiles[y][x].crop.as_mut().unwrap().withered = Some(true);
    }
    at(&mut state, 6, 4, "down");
    apply_command(&ctx, &mut state, &Command::UseTool { tool: "scythe".to_owned() });
    for (x, y) in GROUP {
        assert!(tile_crop(&state, x, y).is_none(), "({x}, {y}) cleared");
    }
}

#[test]
fn fertilizer_feeds_the_soil_under_every_tile_of_a_multi_tile_crop() {
    let (ctx, mut state) = starter();
    plant_cauliflower(&ctx, &mut state, Some("fertilizer-basic"));
    for (x, y) in GROUP {
        assert_eq!(state.world.scenes[0].tiles[y][x].soil_state.as_deref(), Some(soil_states::FERTILIZED));
    }
}

#[test]
fn a_multi_tile_crop_that_loses_a_tile_overnight_is_lost_whole() {
    let (ctx, mut state) = starter();
    plant_cauliflower(&ctx, &mut state, None);
    let before = multi_tile::before_night(&mut state);
    // A storm takes one tile.
    state.world.scenes[0].tiles[6][7].crop = None;
    multi_tile::after_night(&mut state, &before);
    for (x, y) in GROUP {
        assert!(tile_crop(&state, x, y).is_none(), "({x}, {y})");
    }
}

#[test]
fn a_multi_tile_crop_does_not_fit_over_a_machine() {
    let (ctx, mut state) = starter();
    state.world.scenes[0].tiles[6][7].machine =
        Some(TileMachine { type_id: "machine-furnace".to_owned(), ..TileMachine::default() });
    give(&ctx, &mut state, "seed-cauliflower", 1);
    at(&mut state, 6, 4, "down");
    let effects = apply_command(&ctx, &mut state, &interact_with(Some("seed-cauliflower"), None));
    assert!(has_message(&effects, |t| t == "Not enough space for this crop!"));
    assert!(tile_crop(&state, 6, 5).is_none());
}

// --- #32: harvest quality and the harvest item ---

/// A wheat (spring, 3 days) planted at (6, 5) and grown.
fn mature_wheat(ctx: &EngineContext, state: &mut GameState) {
    at(state, 6, 4, "down");
    apply_command(ctx, state, &interact_with(Some("seed-wheat"), None));
    state.world.scenes[0].tiles[5][6].crop.as_mut().unwrap().days_grown = Some(3);
}

#[test]
fn farming_skill_raises_harvest_quality_and_the_shop_pays_for_it() {
    let (ctx, mut state) = make_engine("quality", |project| {
        project.shops.push(farm_sim::schema::ShopDefinition {
            id: "shop-q".to_owned(),
            name: "Q".to_owned(),
            buys_items: true,
            sell_price_multiplier: units::MILLI_ONE,
            ..farm_sim::schema::ShopDefinition::default()
        });
    });
    state.player.skills.insert("farming".to_owned(), SkillState { xp: 300, level: 3 });
    mature_wheat(&ctx, &mut state);
    let effects = apply_command(&ctx, &mut state, &Command::Interact);
    let slot = state.player.inventory.iter().find(|slot| slot.item.id == "crop-wheat").expect("harvested");
    assert_eq!(slot.quality.as_deref(), Some("silver"));
    let quantity = slot.quantity;
    // The message's value is what the units sell for: 25 × 1.25 each.
    let worth = 25 * 1250 / 1000 * i64::from(quantity);
    assert!(has_message(&effects, |t| t.ends_with(&format!("(worth ~${worth})"))), "{effects:?}");

    // Selling: normal quality is not held; silver sells at the silver price.
    apply_command(&ctx, &mut state, &Command::OpenShop { shop_id: "shop-q".to_owned() });
    let money = state.player.money;
    let refused = apply_command(
        &ctx,
        &mut state,
        &Command::SellItem { item_id: "crop-wheat".to_owned(), quantity: 1, quality: Some("normal".to_owned()) },
    );
    assert!(has_message(&refused, |t| t == "You don't have that many."));
    apply_command(
        &ctx,
        &mut state,
        &Command::SellItem { item_id: "crop-wheat".to_owned(), quantity: 1, quality: Some("silver".to_owned()) },
    );
    assert_eq!(state.player.money, money + 31);
    // Without a quality, any quality sells, lowest first, each at its own price.
    give(&ctx, &mut state, "crop-wheat", 1);
    let effects = apply_command(
        &ctx,
        &mut state,
        &Command::SellItem { item_id: "crop-wheat".to_owned(), quantity: 2, quality: None },
    );
    if quantity >= 2 {
        assert!(has_message(&effects, |t| t == "Sold 2x Wheat for $56"), "{effects:?}");
    }
}

#[test]
fn harvest_quality_never_merges_with_other_qualities() {
    let (ctx, mut state) = starter();
    give(&ctx, &mut state, "crop-wheat", 2);
    state.player.skills.insert("farming".to_owned(), SkillState { xp: 750, level: 6 });
    mature_wheat(&ctx, &mut state);
    apply_command(&ctx, &mut state, &Command::Interact);
    let wheat: Vec<(Option<&str>, u32)> = state
        .player
        .inventory
        .iter()
        .filter(|slot| slot.item.id == "crop-wheat")
        .map(|slot| (slot.quality.as_deref(), slot.quantity))
        .collect();
    assert_eq!(wheat[0], (None, 2));
    assert_eq!(wheat[1].0, Some("gold"));
}

#[test]
fn a_crop_can_name_its_harvest_item() {
    let mut wheat: CustomCropDefinition =
        crops::to_custom_crop_definition(&content_builtin::crop_definitions()["wheat"]);
    wheat.harvest_item_id = Some("material-fiber".to_owned());
    let (ctx, mut state) = make_engine("harvest-item", |project| project.custom_crops = Some(vec![wheat]));
    mature_wheat(&ctx, &mut state);
    apply_command(&ctx, &mut state, &Command::Interact);
    assert!(held(&state, "material-fiber") >= 1);
    assert_eq!(held(&state, "crop-wheat"), 0);
}

#[test]
fn a_missing_harvest_item_is_an_error_not_a_silent_no_op() {
    let (ctx, mut state) =
        make_engine("no-harvest-item", |project| project.items.retain(|item| item.id != "crop-wheat"));
    mature_wheat(&ctx, &mut state);
    let effects = apply_command(&ctx, &mut state, &Command::Interact);
    assert!(has_message(&effects, |t| t == "Wheat can't be harvested: its harvest item 'crop-wheat' is missing."));
    assert!(tile_crop(&state, 6, 5).is_some(), "the crop stays");
}

// --- #22: selling draws on every slot ---

#[test]
fn selling_counts_every_slot_holding_the_item() {
    let (ctx, mut state) = make_engine("sell", |project| {
        project.shops.push(farm_sim::schema::ShopDefinition {
            id: "shop-s".to_owned(),
            name: "S".to_owned(),
            buys_items: true,
            sell_price_multiplier: units::MILLI_ONE,
            ..farm_sim::schema::ShopDefinition::default()
        });
    });
    give(&ctx, &mut state, "crop-carrot", 30);
    give(&ctx, &mut state, "crop-carrot", 30);
    apply_command(&ctx, &mut state, &Command::OpenShop { shop_id: "shop-s".to_owned() });
    let effects = apply_command(
        &ctx,
        &mut state,
        &Command::SellItem { item_id: "crop-carrot".to_owned(), quantity: 50, quality: None },
    );
    assert!(has_message(&effects, |t| t == "Sold 50x Carrot for $1000"), "{effects:?}");
    assert_eq!(held(&state, "crop-carrot"), 10);
}

// --- #141: waterArea is the watering can over an area; modifyEnergy saturates ---

#[test]
fn water_area_waters_like_the_watering_can() {
    let (ctx, mut state) = starter();
    // Around (8, 6): a fertilized tile, a withered crop and a dry crop.
    let tiles = &mut state.world.scenes[0].tiles;
    tiles[5][7].soil_state = Some(soil_states::FERTILIZED.to_owned());
    tiles[5][8].crop = Some(Crop { r#type: "wheat".to_owned(), withered: Some(true), ..Crop::default() });
    tiles[7][9].crop = Some(Crop { r#type: "wheat".to_owned(), days_without_water: 2, ..Crop::default() });
    // A stored coordinate that disagrees with the grid position must not matter.
    tiles[7][9].x = 0;
    tiles[7][9].y = 0;
    let mut by_can = state.clone();

    at(&mut state, 8, 6, "down");
    let area = EventOutcome { r#type: "waterArea".to_owned(), radius: Some(1), ..EventOutcome::default() };
    events::apply_outcomes(&ctx, &mut state, &[area], 0);

    give(&ctx, &mut by_can, "tool-watering-can", 1);
    for y in 5..=7 {
        for x in 7..=9 {
            at(&mut by_can, x, y + 1, "up");
            by_can.player.energy = by_can.player.max_energy;
            apply_command(&ctx, &mut by_can, &Command::UseTool { tool: "watering-can".to_owned() });
        }
    }
    for y in 4..=8 {
        for x in 6..=10 {
            assert_eq!(state.world.scenes[0].tiles[y][x], by_can.world.scenes[0].tiles[y][x], "tile ({x}, {y})");
        }
    }
    let tiles = &state.world.scenes[0].tiles;
    assert_eq!(tiles[5][7].soil_state.as_deref(), Some(soil_states::FERTILIZED));
    assert!(!tiles[5][8].crop.as_ref().unwrap().watered);
    assert_eq!(tiles[7][9].crop.as_ref().unwrap().days_without_water, 0);
    // Outside the area nothing changed.
    assert_eq!(tiles[4][6].soil_moisture, 0);
}

#[test]
fn modify_energy_saturates_out_of_range_amounts() {
    let (ctx, mut state) = starter();
    state.player.energy = units::points(10);
    let huge = EventOutcome { r#type: "modifyEnergy".to_owned(), amount: Some(i64::MAX), ..EventOutcome::default() };
    events::apply_outcomes(&ctx, &mut state, &[huge], 0);
    assert_eq!(state.player.energy, state.player.max_energy);
}
