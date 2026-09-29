//! The crafting & station cases of the retired C# `M4SystemsTests.cs`
//! (m4-systems.test.ts), driven through the `crafting` handlers directly. The starter farm already
//! carries the built-in recipes and machine types (plus a Kitchen providing 'cooking').

mod fixture_project;

use farm_sim::crafting::{
    absolute_minute, collect_machine_output, craftable_status, handle_craft, handle_machine_load, handle_place_machine,
    has_ingredients, is_recipe_unlocked, nearby_station_categories, recipe_by_id, settle_machines, station_providing,
    CraftableStatus,
};
use farm_sim::effects::Effect;
use farm_sim::hooks::HookEvent;
use farm_sim::schema::{
    GameProject, GameState, MachineProcessing, MachineTypeDefinition, RecipeDefinition, RecipeIngredient, RecipeUnlock,
    TileMachine,
};
use farm_sim::units;
use farm_sim::{EngineContext, HookBus};
use fixture_project::{at, give, has_message, make_engine, quantity, starter_farm_project};

fn make_m4_engine(mutate: impl FnOnce(&mut GameProject)) -> (EngineContext, GameState) {
    make_engine("m4", mutate)
}

/// Directly stamp a machine instance onto a tile (bypasses placeMachine's item/facing rules).
fn place_machine_at(state: &mut GameState, scene_id: &str, x: usize, y: usize, type_id: &str) {
    for scene in &mut state.world.scenes {
        if scene.id == scene_id {
            scene.tiles[y][x].machine = Some(TileMachine { type_id: type_id.to_owned(), ..TileMachine::default() });
        }
    }
}

fn recipe<'a>(ctx: &'a EngineContext, id: &str) -> &'a RecipeDefinition {
    recipe_by_id(ctx, id).unwrap_or_else(|| panic!("recipe {id} exists"))
}

/// Adds a hand-craftable recipe gated behind a 'cooking' station, plus the machine that provides it.
/// The starter farm's own Kitchen also provides 'cooking' and would be found first, so it is
/// removed: the C# test content (`ContentBuiltin.CreateDefaultMachineTypes`) has no kitchen.
fn with_cooking_station() -> (EngineContext, GameState) {
    make_m4_engine(|project| {
        project.machine_types.retain(|machine| !machine.station_categories.iter().any(|c| c == "cooking"));
        project.machine_types.push(MachineTypeDefinition {
            id: "machine-test-kitchen".to_owned(),
            name: "Test Kitchen".to_owned(),
            description: "A test cooking station".to_owned(),
            color: "#c2703a".to_owned(),
            blocks_movement: true,
            station_categories: vec!["cooking".to_owned()],
            ..MachineTypeDefinition::default()
        });
        project.recipes.push(RecipeDefinition {
            id: "recipe-test-soup".to_owned(),
            name: "Test Soup".to_owned(),
            inputs: vec![RecipeIngredient { item_id: "material-fiber".to_owned(), quantity: 1 }],
            outputs: vec![RecipeIngredient { item_id: "feed-hay".to_owned(), quantity: 1 }],
            processing_minutes: 0,
            category: "cooking".to_owned(),
            requires_station_category: Some("cooking".to_owned()),
            ..RecipeDefinition::default()
        });
    })
}

// --- crafting (M4a) ---

#[test]
fn hand_crafts_an_instant_recipe_consuming_inputs() {
    let (ctx, mut state) = make_m4_engine(|_| {});
    give(&ctx, &mut state, "material-fiber", 6);
    let effects = handle_craft(&ctx, &mut state, "recipe-craft-hay");
    assert_eq!(quantity(&state, "feed-hay"), Some(2));
    assert_eq!(quantity(&state, "material-fiber"), Some(3));
    assert!(has_message(&effects, |t| t == "Crafted 2x Hay"));
}

/// First half of C# `RejectsCraftingWithoutIngredientsOrBelowSkillUnlocks`.
#[test]
fn rejects_crafting_without_ingredients() {
    let (ctx, mut state) = make_m4_engine(|_| {});
    let before = state.clone();
    let no_ingredients = handle_craft(&ctx, &mut state, "recipe-craft-hay");
    assert!(has_message(&no_ingredients, |t| t == "Missing ingredients."));
    assert_eq!(no_ingredients, vec![Effect::message("error", "Missing ingredients.")]);
    assert_eq!(state, before);
}

/// Second half of C# `RejectsCraftingWithoutIngredientsOrBelowSkillUnlocks`.
#[test]
fn rejects_crafting_below_skill_unlocks() {
    let (ctx, mut state) = make_m4_engine(|_| {});
    give(&ctx, &mut state, "material-wood", 20);
    give(&ctx, &mut state, "material-stone", 20);
    let locked = handle_craft(&ctx, &mut state, "recipe-craft-preserves-jar");
    assert!(has_message(&locked, |t| t == "Recipe not unlocked yet."));
}

#[test]
fn places_a_machine_loads_a_job_and_finishes_it_after_processing_time() {
    let (ctx, mut state) = make_m4_engine(|_| {});
    give(&ctx, &mut state, "machine-furnace", 1);
    give(&ctx, &mut state, "ore-copper", 3);
    give(&ctx, &mut state, "material-wood", 1);

    // Facing up from (3,4) at the C# test farm; the starter farm's (8,9) faces (8,8).
    at(&mut state, 8, 9, "up");
    handle_place_machine(&ctx, &mut state, "machine-furnace");
    let machine = state.world.scenes[0].tiles[8][8].machine.clone();
    assert_eq!(machine.map(|m| m.type_id), Some("machine-furnace".to_owned()));
    assert_eq!(quantity(&state, "machine-furnace"), None);

    handle_machine_load(&ctx, &mut state, "recipe-smelt-copper");
    let processing = state.world.scenes[0].tiles[8][8].machine.as_ref().and_then(|m| m.processing.clone());
    assert_eq!(processing.map(|p| p.recipe_id), Some("recipe-smelt-copper".to_owned()));
    assert_eq!(quantity(&state, "ore-copper"), None);

    // Sleeping jumps time far past the 120-minute job → output ready.
    farm_sim::game_time::perform_sleep(&ctx, &mut state, farm_sim::game_time::SleepOptions::default());
    let machine = state.world.scenes[0].tiles[8][8].machine.clone().expect("machine stays");
    assert_eq!(machine.processing, None);
    assert_eq!(machine.output, Some(vec![RecipeIngredient { item_id: "bar-copper".to_owned(), quantity: 1 }]));

    // Interact collects.
    collect_machine_output(&ctx, &mut state, "scene-farm", 8, 8);
    assert_eq!(quantity(&state, "bar-copper"), Some(1));
    assert_eq!(state.world.scenes[0].tiles[8][8].machine.as_ref().and_then(|m| m.output.clone()), None);
}

#[test]
fn machines_block_movement() {
    let (ctx, mut state) = make_m4_engine(|_| {});
    give(&ctx, &mut state, "machine-furnace", 1);
    at(&mut state, 8, 9, "up");
    handle_place_machine(&ctx, &mut state, "machine-furnace");
    farm_sim::world::world_movement::handle_move(&ctx, &mut state, "up");
    assert_eq!(state.player.y, units::tiles(9)); // blocked by the furnace at (8,8)
}

// --- crafting stations & categories ---

#[test]
fn fails_a_station_gated_recipe_away_from_the_station_naming_it_in_the_message() {
    let (ctx, mut state) = with_cooking_station();
    give(&ctx, &mut state, "material-fiber", 5);

    let away = handle_craft(&ctx, &mut state, "recipe-test-soup");
    assert!(away.contains(&Effect::message("error", "You need to be near a Test Kitchen to craft that.")));
    // Nothing consumed on failure.
    assert_eq!(quantity(&state, "material-fiber"), Some(5));
    assert_eq!(quantity(&state, "feed-hay"), None);
}

#[test]
fn succeeds_once_a_machine_providing_the_station_category_is_within_1_tile() {
    let (ctx, mut state) = with_cooking_station();
    give(&ctx, &mut state, "material-fiber", 5);

    // Player sits at (8,9); a kitchen at (7,9) is an 8-neighborhood tile away.
    place_machine_at(&mut state, "scene-farm", 7, 9, "machine-test-kitchen");
    handle_craft(&ctx, &mut state, "recipe-test-soup");
    assert_eq!(quantity(&state, "feed-hay"), Some(1));
    assert_eq!(quantity(&state, "material-fiber"), Some(4));
}

#[test]
fn nearby_station_categories_floors_fractional_player_coordinates_onto_the_right_tile() {
    let (ctx, mut state) = with_cooking_station();
    place_machine_at(&mut state, "scene-farm", 7, 9, "machine-test-kitchen");
    let mut fractional = state.clone();
    fractional.player.x = units::pos(8.7);
    fractional.player.y = units::pos(9.2);
    // floor(8.7)=8, floor(9.2)=9 → still adjacent to the kitchen at (7,9).
    assert!(nearby_station_categories(&ctx, &fractional).contains("cooking"));

    let mut far_away = state.clone();
    far_away.player.x = units::pos(0.1);
    far_away.player.y = units::pos(0.1);
    assert!(!nearby_station_categories(&ctx, &far_away).contains("cooking"));
}

#[test]
fn craftable_status_reports_ingredients_when_short_on_inputs() {
    let (ctx, state) = make_m4_engine(|_| {});
    let status = craftable_status(&ctx, &state, recipe(&ctx, "recipe-craft-hay"));
    assert_eq!(
        status,
        CraftableStatus {
            craftable: false,
            reason: Some("ingredients".to_owned()),
            message: Some("Missing ingredients.".to_owned()),
        }
    );
}

#[test]
fn craftable_status_reports_locked_when_unlock_conditions_are_not_met() {
    let (ctx, mut state) = make_m4_engine(|_| {});
    give(&ctx, &mut state, "material-wood", 20);
    give(&ctx, &mut state, "material-stone", 20);
    let status = craftable_status(&ctx, &state, recipe(&ctx, "recipe-craft-preserves-jar"));
    assert_eq!(
        status,
        CraftableStatus {
            craftable: false,
            reason: Some("locked".to_owned()),
            message: Some("Recipe not unlocked yet.".to_owned()),
        }
    );
}

#[test]
fn craftable_status_reports_station_when_ingredients_and_unlocks_are_fine_but_no_station_is_nearby() {
    let (ctx, mut state) = with_cooking_station();
    give(&ctx, &mut state, "material-fiber", 5);
    let status = craftable_status(&ctx, &state, recipe(&ctx, "recipe-test-soup"));
    assert_eq!(
        status,
        CraftableStatus {
            craftable: false,
            reason: Some("station".to_owned()),
            message: Some("You need to be near a Test Kitchen to craft that.".to_owned()),
        }
    );
}

#[test]
fn craftable_status_reports_craftable_true_once_every_condition_is_satisfied() {
    let (ctx, mut state) = with_cooking_station();
    give(&ctx, &mut state, "material-fiber", 5);
    place_machine_at(&mut state, "scene-farm", 7, 9, "machine-test-kitchen");
    let status = craftable_status(&ctx, &state, recipe(&ctx, "recipe-test-soup"));
    assert_eq!(status, CraftableStatus { craftable: true, reason: None, message: None });
}

// --- the crafting.ts branches reachable without the other modules ---

#[test]
fn absolute_minute_counts_from_day_one_midnight() {
    let (_, mut state) = make_m4_engine(|_| {});
    assert_eq!(state.clock.day, 1);
    assert_eq!(state.clock.time_minutes, units::minutes(360));
    // In micro-minutes.
    let minute = i64::from(units::MINUTE);
    assert_eq!(absolute_minute(&state), 360 * minute);
    state.clock.day = 3;
    state.clock.time_minutes = units::minutes(90);
    assert_eq!(absolute_minute(&state), (2 * 24 * 60 + 90) * minute);
}

#[test]
fn has_ingredients_counts_across_every_slot_of_the_same_item() {
    let (ctx, mut state) = make_m4_engine(|_| {});
    let hay = recipe(&ctx, "recipe-craft-hay");
    assert!(!has_ingredients(&state, hay));
    give(&ctx, &mut state, "material-fiber", 2);
    assert!(!has_ingredients(&state, hay));
    give(&ctx, &mut state, "material-fiber", 1);
    assert!(has_ingredients(&state, hay));
}

#[test]
fn recipe_unlocks_gate_on_completed_quests_and_seasons() {
    let (ctx, mut state) = make_m4_engine(|_| {});
    let open = recipe(&ctx, "recipe-craft-hay");
    assert!(is_recipe_unlocked(&ctx, &state, open));

    let mut quest_gated = open.clone();
    quest_gated.unlock =
        Some(RecipeUnlock { quest_id: Some("quest-first-harvest".to_owned()), ..RecipeUnlock::default() });
    assert!(!is_recipe_unlocked(&ctx, &state, &quest_gated));
    state.player.completed_quests.push("quest-first-harvest".to_owned());
    assert!(is_recipe_unlocked(&ctx, &state, &quest_gated));

    let mut season_gated = open.clone();
    season_gated.unlock = Some(RecipeUnlock { seasons: Some(vec!["summer".to_owned()]), ..RecipeUnlock::default() });
    assert!(!is_recipe_unlocked(&ctx, &state, &season_gated));
    state.clock.season = "summer".to_owned();
    assert!(is_recipe_unlocked(&ctx, &state, &season_gated));

    // Empty lists and empty ids do not gate.
    let mut vacuous = open.clone();
    vacuous.unlock =
        Some(RecipeUnlock { quest_id: Some(String::new()), seasons: Some(Vec::new()), ..RecipeUnlock::default() });
    assert!(is_recipe_unlocked(&ctx, &state, &vacuous));
}

#[test]
fn craft_rejects_unknown_and_machine_recipes_without_touching_state() {
    let (ctx, mut state) = make_m4_engine(|_| {});
    give(&ctx, &mut state, "ore-copper", 3);
    give(&ctx, &mut state, "material-wood", 1);
    let before = state.clone();
    assert_eq!(handle_craft(&ctx, &mut state, "recipe-nope"), vec![Effect::message("error", "Unknown recipe.")]);
    assert_eq!(
        handle_craft(&ctx, &mut state, "recipe-smelt-copper"),
        vec![Effect::message("info", "That recipe needs a machine — load it there.")]
    );
    assert_eq!(state, before);
}

#[test]
fn station_providing_names_the_first_machine_type_for_a_category() {
    let (starter, _) = make_m4_engine(|_| {});
    assert_eq!(station_providing(&starter, "cooking").map(|m| m.id.as_str()), Some("machine-kitchen"));
    let (ctx, _) = with_cooking_station();
    assert_eq!(station_providing(&ctx, "cooking").map(|m| m.id.as_str()), Some("machine-test-kitchen"));
    assert_eq!(station_providing(&ctx, "magic").map(|m| m.name.as_str()), Some("Enchanter's Altar"));
    assert_eq!(station_providing(&ctx, "alchemy"), None);
    assert_eq!(recipe_by_id(&ctx, "recipe-test-soup").map(|r| r.name.as_str()), Some("Test Soup"));
    assert_eq!(recipe_by_id(&ctx, "recipe-nope"), None);
}

#[test]
fn settle_machines_finishes_due_jobs_and_leaves_running_ones() {
    let (ctx, mut state) = make_m4_engine(|_| {});
    let start = absolute_minute(&state);
    place_machine_at(&mut state, "scene-farm", 1, 1, "machine-furnace");
    place_machine_at(&mut state, "scene-farm", 2, 1, "machine-furnace");
    place_machine_at(&mut state, "scene-farm", 3, 1, "machine-preserves");
    let set_processing = |state: &mut GameState, x: usize, recipe_id: &str, completes_at_minute: i64| {
        let machine = state.world.scenes[0].tiles[1][x].machine.as_mut().expect("machine stamped");
        machine.processing = Some(MachineProcessing { recipe_id: recipe_id.to_owned(), completes_at_minute });
    };
    let minute = i64::from(units::MINUTE);
    set_processing(&mut state, 1, "recipe-smelt-copper", start + 120 * minute);
    set_processing(&mut state, 2, "recipe-smelt-iron", start + 180 * minute);
    set_processing(&mut state, 3, "recipe-gone", start);

    // Nothing but the already-due unknown recipe settles at the start minute.
    let before = state.clone();
    settle_machines(&ctx, &mut state);
    let machine_at = |state: &GameState, x: usize| state.world.scenes[0].tiles[1][x].machine.clone().expect("machine");
    assert_eq!(machine_at(&state, 1), machine_at(&before, 1));
    assert_eq!(machine_at(&state, 2), machine_at(&before, 2));
    assert_eq!(machine_at(&state, 3).processing, None);
    assert_eq!(machine_at(&state, 3).output, Some(Vec::new()));

    // 120 minutes later the copper job completes; the iron job keeps running.
    state.clock.time_minutes += units::minutes(120);
    settle_machines(&ctx, &mut state);
    let copper = machine_at(&state, 1);
    assert_eq!(copper.processing, None);
    assert_eq!(copper.output, Some(vec![RecipeIngredient { item_id: "bar-copper".to_owned(), quantity: 1 }]));
    assert_eq!(copper.type_id, "machine-furnace");
    assert!(machine_at(&state, 2).processing.is_some());
    assert_eq!(machine_at(&state, 2).output, None);

    // The overnight catch-up is an absolute-minute comparison: a new day settles the rest.
    state.clock.day += 1;
    settle_machines(&ctx, &mut state);
    assert_eq!(
        machine_at(&state, 2).output,
        Some(vec![RecipeIngredient { item_id: "bar-iron".to_owned(), quantity: 1 }])
    );
    // Player state is untouched by settling.
    assert_eq!(state.player, before.player);
}

#[test]
fn collect_machine_output_leaves_a_full_inventory_and_the_output_alone() {
    let (ctx, mut state) = make_m4_engine(|_| {});
    place_machine_at(&mut state, "scene-farm", 1, 1, "machine-furnace");
    state.world.scenes[0].tiles[1][1].machine.as_mut().expect("machine").output =
        Some(vec![RecipeIngredient { item_id: "bar-copper".to_owned(), quantity: 1 }]);
    state.player.max_inventory_size = state.player.inventory.len() as u32;
    let before = state.clone();
    let effects = collect_machine_output(&ctx, &mut state, "scene-farm", 1, 1);
    assert_eq!(effects, vec![Effect::message("error", "Inventory is full!")]);
    assert_eq!(state, before);
}

#[test]
fn hand_craft_emits_the_recipe_hook_before_progressing_quests() {
    // The success path continues into quests::progress_quests (another module); this checks
    // the failure path emits nothing and the bus stays attached to the context.
    let project = starter_farm_project();
    let ctx = EngineContext::with_hooks(farm_sim::state::create_content_from_project(&project), HookBus::new());
    let mut state = farm_sim::state::create_game_state(&project, Some("m4"));
    handle_craft(&ctx, &mut state, "recipe-craft-hay");
    let events: Vec<HookEvent> = ctx.drain_hook_events();
    assert!(events.is_empty());
}
