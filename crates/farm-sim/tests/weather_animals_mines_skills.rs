//! The weather, animal, mine and skill cases of the retired C# `M4SystemsTests.cs`
//! (m4-systems.test.ts), the `onWeatherRoll` reroll cases of `M5SystemsTests.cs`, and the pure
//! clock cases of `M2SystemsTests.cs` — plus direct-module variants of the C# engine-level tests
//! so the ported logic is exercised before the engine dispatch lands.
//!
//! Most states come from the `project` in `fixtures/golden/content/starter-farm.json`, with the M4
//! fixture additions (animals, mine config) applied on top. Cases whose coordinates depend on the
//! C# field (`EngineTests.MakeProject()`, 6x6 with soil at (3,2)) use [`make_csharp_engine`].

use farm_sim::content_builtin::{self, ToolDefinition};
use farm_sim::hooks::{WeatherRollHookPayload, WeatherRollListener};
use farm_sim::schema::{
    AnimalState, GameProject, GameState, InventorySlot, MineConfig, Scene, SkillState, WeatherConfig,
    WeatherTableEntry, WeatherTypeDefinition,
};
use farm_sim::units;
use farm_sim::{
    animals, energy, game_time, mines, skills, stable_json, state, weather, Command, Effect, Effects, EngineContext,
    HookBus, Rng,
};
use indexmap::IndexMap;
use std::path::PathBuf;

mod common;

const SCENE: &str = "scene-farm";

fn starter_farm_project() -> GameProject {
    let path: PathBuf =
        [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", "content", "starter-farm.json"].iter().collect();
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
    let fixture: serde_json::Value = serde_json::from_str(&text).expect("valid fixture JSON");
    serde_json::from_value(fixture["project"].clone()).expect("starter-farm project is a GameProject")
}

/// M4 fixture: recipes, machines, animals, fishing, mining enabled (on the starter farm).
fn m4_project() -> GameProject {
    with_m4_content(starter_farm_project(), SCENE)
}

/// The M4 additions of [`m4_project`] applied to `project`.
fn with_m4_content(mut project: GameProject, entrance_scene: &str) -> GameProject {
    project.recipes = content_builtin::create_default_recipes();
    project.machine_types = content_builtin::create_default_machine_types();
    project.animal_species = content_builtin::create_default_animal_species();
    project.fish_tables = content_builtin::create_default_fish_tables();
    project.mine = MineConfig {
        enabled: true,
        entrance_scene_id: Some(entrance_scene.to_owned()),
        entrance_x: Some(5),
        entrance_y: Some(5),
        floors: 10,
        bands: content_builtin::create_default_mine_bands(),
        ..MineConfig::default()
    };
    project
}

/// Exactly the C# `M4SystemsTests.MakeEngine`: the 6x6 `EngineTests.MakeProject` field (soil at
/// (3,2), player at (3,4) facing up) with the M4 additions. For the cases whose coordinates
/// depend on that layout.
fn make_csharp_engine(mutate: impl FnOnce(&mut GameProject)) -> (EngineContext, GameState) {
    let mut project = with_m4_content(common::make_project(), "scene-test");
    mutate(&mut project);
    let ctx = EngineContext::new(state::create_content_from_project(&project));
    let state = state::create_game_state(&project, Some("m4"));
    (ctx, state)
}

fn make_engine(mutate: impl FnOnce(&mut GameProject)) -> (EngineContext, GameState) {
    let mut project = m4_project();
    mutate(&mut project);
    let ctx = EngineContext::new(state::create_content_from_project(&project));
    let state = state::create_game_state(&project, Some("m4"));
    (ctx, state)
}

fn give(state: &mut GameState, item_id: &str, quantity: u32, ctx: &EngineContext) {
    let item = ctx.content.items.iter().find(|i| i.id == item_id).unwrap_or_else(|| panic!("item {item_id}")).clone();
    state.player.inventory.push(InventorySlot { item, quantity });
}

fn quantity(state: &GameState, item_id: &str) -> Option<u32> {
    state.player.inventory.iter().find(|s| s.item.id == item_id).map(|s| s.quantity)
}

fn at(state: &mut GameState, x: i32, y: i32, direction: &str) {
    state.player.x = farm_sim::units::tiles(x);
    state.player.y = farm_sim::units::tiles(y);
    state.player.direction = direction.to_owned();
}

fn has_message(effects: &Effects, predicate: impl Fn(&str) -> bool) -> bool {
    effects.iter().any(|effect| matches!(effect, Effect::Message { text, .. } if predicate(text)))
}

fn only(weather_id: &str) -> Vec<WeatherTableEntry> {
    vec![WeatherTableEntry { weather_id: weather_id.to_owned(), weight: 1 }]
}

fn always_weather(weather_id: &str, types: Vec<WeatherTypeDefinition>) -> WeatherConfig {
    let mut table = IndexMap::new();
    for season in ["spring", "summer", "fall", "winter"] {
        table.insert(season.to_owned(), only(weather_id));
    }
    WeatherConfig { types, table, ..WeatherConfig::default() }
}

fn weather_type(id: &str, name: &str, waters: bool, damage: f64) -> WeatherTypeDefinition {
    WeatherTypeDefinition {
        id: id.to_owned(),
        name: name.to_owned(),
        waters_outdoor_soil: waters,
        crop_damage_chance: units::chance(damage),
        ..WeatherTypeDefinition::default()
    }
}

// --- clock (M2SystemsTests: pure) ---

#[test]
fn formats_time_of_day() {
    assert_eq!(game_time::format_time_of_day(units::minutes(6 * 60)), "6:00 AM");
    assert_eq!(game_time::format_time_of_day(units::minutes(12 * 60)), "12:00 PM");
    assert_eq!(game_time::format_time_of_day(units::minutes(13 * 60 + 30)), "1:30 PM");
    assert_eq!(game_time::format_time_of_day(units::minutes(0)), "12:00 AM");
    assert_eq!(game_time::format_time_of_day(units::minutes(25 * 60)), "1:00 AM"); // past-midnight wrap
    assert_eq!(game_time::format_time_of_day(units::time_of_day(9.0 * 60.0 + 5.5)), "9:05 AM");
}

#[test]
fn classifies_day_phases() {
    assert_eq!(game_time::day_phase(units::minutes(6 * 60)), "morning");
    assert_eq!(game_time::day_phase(units::minutes(12 * 60)), "day");
    assert_eq!(game_time::day_phase(units::minutes(18 * 60)), "evening");
    assert_eq!(game_time::day_phase(units::minutes(23 * 60)), "night");
    assert_eq!(game_time::day_phase(units::minutes(4 * 60)), "night");
    assert_eq!(game_time::day_phase(units::minutes(26 * 60)), "night");
}

// --- weather (M4b) ---

#[test]
fn rain_waters_soil_and_crops_at_day_start() {
    let (ctx, mut current) = make_csharp_engine(|project| {
        project.weather = always_weather(
            "rain",
            vec![weather_type("rain", "Rain", true, 0.0), weather_type("storm", "Storm", true, 1.0)],
        );
    });
    at(&mut current, 3, 3, "up");
    farm_sim::apply_command(&ctx, &mut current, &Command::Interact); // plant wheat on (3,2)
    farm_sim::apply_command(&ctx, &mut current, &Command::Sleep);
    assert_eq!(current.clock.weather_id, "rain");
    let tile = &current.world.scenes[0].tiles[2][3];
    assert_eq!(tile.soil_state.as_deref(), Some("watered"));
    assert!(tile.crop.as_ref().is_some_and(|c| c.watered));
    // Rainy day 2: sleeping again grows the crop without manual watering.
    farm_sim::apply_command(&ctx, &mut current, &Command::Sleep);
    assert_eq!(current.world.scenes[0].tiles[2][3].crop.as_ref().and_then(|c| c.days_grown), Some(1));
}

#[test]
fn storms_can_destroy_crops_overnight() {
    let (ctx, mut current) = make_engine(|project| {
        project.weather = always_weather("storm", vec![weather_type("storm", "Storm", true, 1.0)]);
    });
    at(&mut current, 8, 5, "up");
    farm_sim::apply_command(&ctx, &mut current, &Command::Interact);
    // First sleep rolls storm for day 2; second sleep's overnight pass damages with p=1.
    farm_sim::apply_command(&ctx, &mut current, &Command::Sleep);
    farm_sim::apply_command(&ctx, &mut current, &Command::Sleep);
    assert!(current.world.scenes[0].tiles[4][8].crop.is_none());
}

#[test]
fn weather_lookups_resolve_content_types() {
    let (ctx, state) = make_engine(|_| {});
    assert_eq!(weather::weather_type_by_id(&ctx, "rain").map(|t| t.name.as_str()), Some("Rain"));
    assert!(weather::weather_type_by_id(&ctx, "sharknado").is_none());
    assert_eq!(weather::current_weather(&ctx, &state).map(|t| t.id.as_str()), Some("sun"));
}

#[test]
fn roll_weather_draws_once_from_the_seasons_table() {
    let (ctx, state) = make_engine(|project| {
        project.weather = always_weather("rain", vec![weather_type("rain", "Rain", true, 0.0)]);
    });
    let mut rng = Rng::new(state.rng.clone());
    assert_eq!(weather::roll_weather(&ctx, "spring", &mut rng), "rain");
    // Exactly one draw: the state advanced by a single float.
    let mut expected = Rng::new(state.rng.clone());
    expected.next_u32();
    assert_eq!(rng.state, expected.state);
    // Same seed, same roll.
    let mut again = Rng::new(state.rng.clone());
    assert_eq!(weather::roll_weather(&ctx, "spring", &mut again), "rain");
}

#[test]
fn roll_weather_falls_back_to_the_first_type_without_a_table() {
    let (ctx, state) = make_engine(|project| {
        project.weather =
            WeatherConfig { types: vec![weather_type("snow", "Snow", false, 0.0)], ..WeatherConfig::default() };
    });
    let mut rng = Rng::new(state.rng.clone());
    assert_eq!(weather::roll_weather(&ctx, "spring", &mut rng), "snow");
    assert_eq!(rng.state, state.rng, "no table → no draw");

    let (ctx, _) = make_engine(|project| {
        project.weather = WeatherConfig::default();
    });
    assert_eq!(weather::roll_weather(&ctx, "spring", &mut rng), "sun");
}

// --- animals (M4c) ---

fn clucky() -> AnimalState {
    AnimalState {
        id: "animal-1".to_owned(),
        species_id: "animal-chicken".to_owned(),
        name: "Clucky".to_owned(),
        scene_id: SCENE.to_owned(),
        x: units::tiles(3),
        y: units::tiles(3),
        mood: 70,
        fed_today: false,
        petted_today: false,
        age_days: 5,
        days_since_product: 0,
        product_ready: false,
        ..AnimalState::default()
    }
}

fn ranch_engine() -> (EngineContext, GameState) {
    make_engine(|project| project.animals = vec![clucky()])
}

#[test]
fn feeding_petting_and_daily_product_flow() {
    // C# `RanchEngine` on the 6x6 field: Clucky at (3,3), the player at (3,4) facing up.
    let (ctx, mut current) = make_csharp_engine(|project| {
        project.animals = vec![AnimalState { scene_id: "scene-test".to_owned(), ..clucky() }]
    });
    give(&mut current, "feed-hay", 2, &ctx);

    // Interact 1: feeds (consumes hay)
    let step = farm_sim::apply_command(&ctx, &mut current, &Command::Interact);
    assert!(has_message(&step, |t| t.contains("Fed Clucky")));
    assert_eq!(quantity(&current, "feed-hay"), Some(1));

    // Interact 2: pets
    let step = farm_sim::apply_command(&ctx, &mut current, &Command::Interact);
    assert!(has_message(&step, |t| t.contains("happy")));

    // Overnight: fed adult chicken with interval 1 → egg ready
    farm_sim::apply_command(&ctx, &mut current, &Command::Sleep);
    assert!(current.animals[0].product_ready);
    assert!(!current.animals[0].fed_today);

    // New day: feeding takes priority again (uses the last hay)…
    farm_sim::apply_command(&ctx, &mut current, &Command::Interact);
    assert!(current.animals[0].fed_today);
    // …then the next interact collects the egg.
    farm_sim::apply_command(&ctx, &mut current, &Command::Interact);
    assert!(current.player.inventory.iter().any(|s| s.item.id == "product-egg"));
    assert!(!current.animals[0].product_ready);
}

#[test]
fn neglected_animals_lose_mood_and_produce_nothing() {
    let (ctx, mut current) = ranch_engine();
    farm_sim::apply_command(&ctx, &mut current, &Command::Sleep);
    assert_eq!(current.animals[0].mood, 55); // -15 unfed
    assert!(!current.animals[0].product_ready);
}

#[test]
fn advance_animals_nightly_neglect_lowers_mood_and_produces_nothing() {
    let (ctx, mut current) = ranch_engine();
    animals::advance_animals_nightly(&ctx, &mut current);
    assert_eq!(current.animals[0].mood, 55); // -15 unfed
    assert!(!current.animals[0].product_ready);
    assert_eq!(current.animals[0].age_days, 6);
    assert_eq!(current.animals[0].days_since_product, 0);
}

#[test]
fn advance_animals_nightly_fed_adult_rolls_a_product_and_resets_the_day_flags() {
    let (ctx, mut current) = ranch_engine();
    current.animals[0].fed_today = true;
    current.animals[0].petted_today = true;
    animals::advance_animals_nightly(&ctx, &mut current);
    let animal = &current.animals[0];
    assert_eq!(animal.mood, 78); // +4 fed, +4 petted
    assert!(animal.product_ready);
    assert_eq!(animal.days_since_product, 1);
    assert!(!animal.fed_today);
    assert!(!animal.petted_today);

    // A second night with the product still waiting: no further roll, mood clamps at 100.
    current.animals[0].fed_today = true;
    current.animals[0].mood = 99;
    animals::advance_animals_nightly(&ctx, &mut current);
    assert_eq!(current.animals[0].mood, 100);
    assert_eq!(current.animals[0].days_since_product, 1);
}

#[test]
fn advance_animals_nightly_skips_unknown_species_and_empty_herds() {
    let (ctx, mut current) = make_engine(|project| {
        project.animals = vec![AnimalState { species_id: "animal-dragon".to_owned(), ..clucky() }];
    });
    let before = current.animals.clone();
    animals::advance_animals_nightly(&ctx, &mut current);
    assert_eq!(current.animals, before);

    let (ctx, mut empty) = make_engine(|_| {});
    animals::advance_animals_nightly(&ctx, &mut empty);
    assert!(empty.animals.is_empty());
}

#[test]
fn petting_raises_mood_once_per_day() {
    let (ctx, mut current) = ranch_engine();
    let animal = animals::animal_at(&current, SCENE, 3, 3).expect("Clucky at (3,3)").clone();
    assert!(animals::animal_at(&current, SCENE, 4, 3).is_none());

    // No hay in the inventory and no product: petting is the interaction.
    let effects = animals::handle_animal_interaction(&ctx, &mut current, &animal);
    assert_eq!(effects, vec![Effect::message("success", "Clucky looks happy! ♥")]);
    assert!(current.animals[0].petted_today);
    assert_eq!(current.animals[0].mood, 78);

    let petted = current.animals[0].clone();
    let effects = animals::handle_animal_interaction(&ctx, &mut current, &petted);
    assert_eq!(effects, vec![Effect::message("info", "Clucky is content.")]);
    assert_eq!(current.animals[0].mood, 78);
}

#[test]
fn interacting_with_an_unknown_species_does_nothing() {
    let (ctx, mut current) = ranch_engine();
    let stranger = AnimalState { species_id: "animal-dragon".to_owned(), ..clucky() };
    let before = current.clone();
    assert!(animals::handle_animal_interaction(&ctx, &mut current, &stranger).is_empty());
    assert_eq!(current, before);
}

#[test]
fn feeding_consumes_hay_and_collecting_takes_the_product() {
    let (ctx, mut current) = ranch_engine();
    give(&mut current, "feed-hay", 2, &ctx);
    let animal = current.animals[0].clone();
    let effects = animals::handle_animal_interaction(&ctx, &mut current, &animal);
    assert_eq!(effects, vec![Effect::message("success", "Fed Clucky")]);
    assert_eq!(quantity(&current, "feed-hay"), Some(1));
    assert!(current.animals[0].fed_today);
    assert_eq!(current.animals[0].mood, 75);

    current.animals[0].product_ready = true;
    current.animals[0].fed_today = true;
    let ready = current.animals[0].clone();
    let effects = animals::handle_animal_interaction(&ctx, &mut current, &ready);
    assert_eq!(effects, vec![Effect::message("success", "Collected Egg from Clucky")]);
    assert!(current.player.inventory.iter().any(|s| s.item.id == "product-egg"));
    assert!(!current.animals[0].product_ready);
}

// --- mining (M4f) ---

#[test]
fn mine_scene_ids_round_trip() {
    assert_eq!(mines::mine_floor_scene_id(3), "mine-floor-3");
    assert_eq!(mines::mine_floor_scene_id(10), "mine-floor-10");
    assert!(mines::is_mine_scene("mine-floor-1"));
    assert!(!mines::is_mine_scene(SCENE));
    assert!(!mines::is_mine_scene(""));
}

#[test]
fn floors_generate_deterministically_from_seed_plus_floor() {
    let (ctx, _) = make_engine(|_| {});
    let a = mines::generate_mine_floor(&ctx, "seed-x", 3);
    let b = mines::generate_mine_floor(&ctx, "seed-x", 3);
    let c = mines::generate_mine_floor(&ctx, "seed-y", 3);
    assert_eq!(stable_json::stringify(&a), stable_json::stringify(&b));
    assert_ne!(stable_json::stringify(&a), stable_json::stringify(&c));
    // Has rocks from the band table
    let node_count = a.tiles.iter().flatten().filter(|tile| tile.node.is_some()).count();
    assert!(node_count > 5);
    assert_eq!(a.extra.get("generated"), Some(&serde_json::Value::Bool(true)));
}

#[test]
fn entrance_interaction_descends_exit_returns_to_the_surface_and_drops_floors() {
    let (ctx, mut current) = make_engine(|_| {});
    // Entrance at (5,5). Stand at (5,4) facing down → (5,5).
    at(&mut current, 5, 4, "down");
    farm_sim::apply_command(&ctx, &mut current, &Command::Interact);
    assert_eq!(current.player.scene_id, "mine-floor-1");
    assert_eq!(current.mine.current_floor, 1);
    assert!(current.world.scenes.iter().any(|s| s.id == "mine-floor-1"));

    // The exit check triggers when facing/at the entry — face up from (1,2).
    at(&mut current, 1, 2, "up");
    farm_sim::apply_command(&ctx, &mut current, &Command::ExitMine);
    assert_eq!(current.player.scene_id, SCENE);
    assert!(!current.world.scenes.iter().any(|s| s.id == "mine-floor-1"));
}

#[test]
fn descend_command_respects_floor_bounds_and_records_depth() {
    let (ctx, mut current) = make_engine(|_| {});
    farm_sim::apply_command(&ctx, &mut current, &Command::DescendMine { floor: 99 });
    assert_eq!(current.mine.current_floor, 10); // clamped to config.floors
    assert_eq!(current.mine.deepest_floor, 10);
}

/// A stand-in generated floor so descend/exit can be exercised without `create_empty_scene`.
fn stub_floor(floor: u32) -> Scene {
    Scene { id: mines::mine_floor_scene_id(floor), name: format!("Mine — Floor {floor}"), ..Scene::default() }
}

#[test]
fn descend_mine_clamps_to_the_floor_count_and_reuses_an_existing_floor() {
    let (ctx, mut current) = make_engine(|_| {});
    // Floor 10 already exists, so no generation is needed to reach it.
    current.world.scenes.push(stub_floor(10));
    let effects = mines::descend_mine(&ctx, &mut current, 99);
    assert_eq!(current.mine.current_floor, 10); // clamped to config.floors
    assert_eq!(current.mine.deepest_floor, 10);
    assert_eq!(current.player.scene_id, "mine-floor-10");
    assert_eq!((current.player.x, current.player.y), (units::pos(1.5), units::pos(1.5)));
    assert_eq!(current.world.scenes.len(), 2, "the existing floor is reused");
    assert_eq!(
        effects,
        vec![
            Effect::SceneChanged { scene_id: "mine-floor-10".to_owned(), x: 1, y: 1 },
            Effect::message("info", "Mine — floor 10 (elevator checkpoint)"),
        ]
    );

    // Going back up to an existing shallower floor keeps the deepest record.
    current.world.scenes.push(stub_floor(1));
    let effects = mines::descend_mine(&ctx, &mut current, 0);
    assert_eq!(current.mine.current_floor, 1);
    assert_eq!(current.mine.deepest_floor, 10);
    assert!(has_message(&effects, |t| t == "Mine — floor 1"));
}

#[test]
fn descend_mine_is_a_no_op_when_mining_is_disabled() {
    let (ctx, mut current) = make_engine(|project| project.mine.enabled = false);
    let before = current.clone();
    assert!(mines::descend_mine(&ctx, &mut current, 1).is_empty());
    assert_eq!(current, before);
}

#[test]
fn exit_mine_returns_to_the_entrance_and_drops_generated_floors() {
    let (ctx, mut current) = make_engine(|_| {});
    current.world.scenes.push(stub_floor(1));
    current.player.scene_id = "mine-floor-1".to_owned();
    current.mine.current_floor = 1;
    current.mine.deepest_floor = 1;

    let effects = mines::exit_mine(&ctx, &mut current);
    assert_eq!(current.player.scene_id, SCENE);
    assert_eq!((current.player.x, current.player.y), (units::pos(5.5), units::pos(5.5)));
    assert_eq!(current.mine.current_floor, 0);
    assert_eq!(current.mine.deepest_floor, 1);
    assert!(!current.world.scenes.iter().any(|s| mines::is_mine_scene(&s.id)));
    assert_eq!(
        effects,
        vec![
            Effect::SceneChanged { scene_id: SCENE.to_owned(), x: 5, y: 5 },
            Effect::message("info", "You climb back to the surface."),
        ]
    );
}

#[test]
fn exit_mine_defaults_to_the_start_scene_center() {
    let (ctx, mut current) = make_engine(|project| {
        project.mine.entrance_scene_id = None;
        project.mine.entrance_x = None;
        project.mine.entrance_y = None;
    });
    current.player.scene_id = "mine-floor-1".to_owned();
    let effects = mines::exit_mine(&ctx, &mut current);
    // The starter farm is 16×12: floor(16/2) = 8, floor(12/2) = 6.
    assert_eq!(current.player.scene_id, SCENE);
    assert_eq!((current.player.x, current.player.y), (units::pos(8.5), units::pos(6.5)));
    assert!(matches!(&effects[0], Effect::SceneChanged { scene_id, x, y } if scene_id == SCENE && *x == 8 && *y == 6));

    // No such entrance scene: nothing happens.
    let (ctx, mut current) = make_engine(|project| project.mine.entrance_scene_id = Some("nowhere".to_owned()));
    let before = current.clone();
    assert!(mines::exit_mine(&ctx, &mut current).is_empty());
    assert_eq!(current, before);
}

#[test]
fn maybe_reveal_ladder_rolls_the_rng_and_marks_the_tile() {
    let (ctx, mut current) = make_engine(|project| project.mine.ladder_chance = units::PROBABILITY_ONE);
    let mut floor = stub_floor(1);
    floor.tiles = vec![vec![Default::default(); 3]; 3];
    current.world.scenes.push(floor);
    let rng_before = current.rng.clone();

    let effects = mines::maybe_reveal_ladder(&ctx, &mut current, "mine-floor-1", 2, 1);
    assert_eq!(effects, vec![Effect::message("success", "A ladder to the next floor appears!")]);
    assert_eq!(current.world.scenes[1].tiles[1][2].ladder_down, Some(true));
    let mut expected = Rng::new(rng_before);
    expected.next_u32();
    assert_eq!(current.rng, expected.state, "exactly one draw");
}

#[test]
fn maybe_reveal_ladder_still_draws_when_the_roll_fails() {
    let (ctx, mut current) = make_engine(|project| project.mine.ladder_chance = 0);
    let mut floor = stub_floor(1);
    floor.tiles = vec![vec![Default::default(); 3]; 3];
    current.world.scenes.push(floor);
    let mut expected = Rng::new(current.rng.clone());
    expected.next_u32();

    assert!(mines::maybe_reveal_ladder(&ctx, &mut current, "mine-floor-1", 2, 1).is_empty());
    assert_eq!(current.world.scenes[1].tiles[1][2].ladder_down, None);
    assert_eq!(current.rng, expected.state);
}

#[test]
fn maybe_reveal_ladder_ignores_surface_scenes() {
    let (ctx, mut current) = make_engine(|project| project.mine.ladder_chance = units::PROBABILITY_ONE);
    let before = current.clone();
    assert!(mines::maybe_reveal_ladder(&ctx, &mut current, SCENE, 2, 2).is_empty());
    assert_eq!(current, before, "no draw, no change outside the mine");
}

// --- skills (M4g) ---

#[test]
fn harvesting_grants_farming_xp_and_levels_up_at_thresholds() {
    let (ctx, mut current) = make_engine(|_| {});
    at(&mut current, 8, 5, "up");
    current.player.skills = IndexMap::from([("farming".to_owned(), SkillState { xp: 45, level: 0 })]);
    farm_sim::apply_command(&ctx, &mut current, &Command::Interact); // plant
    for _ in 0..3 {
        farm_sim::apply_command(&ctx, &mut current, &Command::UseTool { tool: "watering-can".to_owned() });
        farm_sim::apply_command(&ctx, &mut current, &Command::Sleep);
    }
    let step = farm_sim::apply_command(&ctx, &mut current, &Command::Interact); // harvest: +8 xp → 53 ≥ 50
    assert_eq!(current.player.skills["farming"].level, 1);
    assert!(has_message(&step, |t| t.contains("Farming level 1")));
}

#[test]
fn skills_can_be_disabled_per_project() {
    let (ctx, mut current) = make_engine(|project| project.settings.skills_enabled = false);
    at(&mut current, 8, 5, "up");
    farm_sim::apply_command(&ctx, &mut current, &Command::Interact);
    for _ in 0..3 {
        farm_sim::apply_command(&ctx, &mut current, &Command::UseTool { tool: "watering-can".to_owned() });
        farm_sim::apply_command(&ctx, &mut current, &Command::Sleep);
    }
    farm_sim::apply_command(&ctx, &mut current, &Command::Interact);
    assert!(!current.player.skills.contains_key("farming"));
}

#[test]
fn skill_level_for_xp_walks_the_curve() {
    let curve = [0, 50, 150, 300];
    assert_eq!(skills::skill_level_for_xp(&curve, 0), 0);
    assert_eq!(skills::skill_level_for_xp(&curve, 49), 0);
    assert_eq!(skills::skill_level_for_xp(&curve, 50), 1);
    assert_eq!(skills::skill_level_for_xp(&curve, 299), 2);
    assert_eq!(skills::skill_level_for_xp(&curve, 10_000), 3);
    assert_eq!(skills::skill_level_for_xp(&[], 10_000), 0);
}

#[test]
fn grant_xp_levels_up_at_the_threshold_with_a_message() {
    let (ctx, mut current) = make_engine(|_| {});
    current.player.skills = IndexMap::from([("farming".to_owned(), SkillState { xp: 45, level: 0 })]);
    let effects = skills::grant_xp(&ctx, &mut current, "farming", 8); // 53 ≥ 50
    assert_eq!(effects, vec![Effect::message("success", "Farming level 1!")]);
    assert_eq!(current.player.skills["farming"], SkillState { xp: 53, level: 1 });
    assert_eq!(skills::skill_level(&current, "farming"), 1);

    // Below the next threshold: XP accrues silently, and a new skill starts from zero.
    assert!(skills::grant_xp(&ctx, &mut current, "farming", 1).is_empty());
    assert!(skills::grant_xp(&ctx, &mut current, "mining", 10).is_empty());
    assert_eq!(current.player.skills["mining"], SkillState { xp: 10, level: 0 });
    assert_eq!(current.player.skills.keys().collect::<Vec<_>>(), ["farming", "mining"]);
}

#[test]
fn grant_xp_is_a_no_op_when_disabled_or_for_non_positive_xp() {
    let (ctx, mut current) = make_engine(|project| project.settings.skills_enabled = false);
    assert!(skills::grant_xp(&ctx, &mut current, "farming", 8).is_empty());
    assert!(!current.player.skills.contains_key("farming"));

    let (ctx, mut current) = make_engine(|_| {});
    assert!(skills::grant_xp(&ctx, &mut current, "farming", 0).is_empty());
    assert!(!current.player.skills.contains_key("farming"));
}

#[test]
fn farming_yield_bonus_is_one_per_four_levels() {
    let (_, mut current) = make_engine(|_| {});
    assert_eq!(skills::skill_level(&current, "farming"), 0);
    assert_eq!(skills::farming_yield_bonus(&current), 0);
    current.player.skills.insert("farming".to_owned(), SkillState { xp: 9_999, level: 9 });
    assert_eq!(skills::farming_yield_bonus(&current), 2);
}

// --- energy (M2) ---

#[test]
fn effective_energy_cost_discounts_higher_tiers_down_to_one() {
    let hoe = ToolDefinition { energy_cost: units::points(10), ..ToolDefinition::default() };
    assert_eq!(energy::effective_energy_cost(&hoe, 1), units::points(10));
    assert_eq!(energy::effective_energy_cost(&hoe, 2), units::points(9)); // round(8.5) rounds half up
    assert_eq!(energy::effective_energy_cost(&hoe, 3), units::points(7));
    assert_eq!(energy::effective_energy_cost(&hoe, 20), units::points(1));
}

#[test]
fn spend_energy_deducts_and_warns_when_crossing_the_low_threshold() {
    let (ctx, mut current) = make_engine(|_| {});
    let result = energy::spend_energy(&ctx, &mut current, units::points(30));
    assert_eq!(result, energy::EnergySpendResult { effects: vec![], collapsed: false });
    assert_eq!(current.player.energy, units::points(70));

    current.player.energy = units::points(25);
    let result = energy::spend_energy(&ctx, &mut current, units::points(6)); // 25 → 19 crosses 20
    assert!(!result.collapsed);
    assert_eq!(result.effects, vec![Effect::message("info", "You are getting exhausted — consider sleeping.")]);
    assert_eq!(current.player.energy, units::points(19));

    // Already below the threshold: no second warning.
    let result = energy::spend_energy(&ctx, &mut current, units::points(1));
    assert!(result.effects.is_empty());
    assert_eq!(current.player.energy, units::points(18));
}

#[test]
fn spend_energy_is_a_no_op_when_disabled_or_free() {
    let (ctx, mut current) = make_engine(|project| project.settings.energy_enabled = false);
    let result = energy::spend_energy(&ctx, &mut current, units::points(500));
    assert!(!result.collapsed && result.effects.is_empty());
    assert_eq!(current.player.energy, units::points(100));

    let (ctx, mut current) = make_engine(|_| {});
    let result = energy::spend_energy(&ctx, &mut current, units::points(0));
    assert!(!result.collapsed && result.effects.is_empty());
    assert_eq!(current.player.energy, units::points(100));
}

#[test]
fn spend_energy_collapses_the_player_and_ends_the_day() {
    let (ctx, mut current) = make_engine(|_| {});
    current.player.energy = units::points(5);
    let result = energy::spend_energy(&ctx, &mut current, units::points(5));
    assert!(result.collapsed);
    assert!(has_message(&result.effects, |t| t == "You collapsed from exhaustion! Lost $50."));
    assert_eq!(current.clock.day, 2);
    assert_eq!(current.player.energy, units::points(50));
    assert_eq!(current.player.money, 50);
}

// --- onWeatherRoll reroll capability (M5SystemsTests) ---

struct WeatherOverride(&'static str);

impl WeatherRollListener for WeatherOverride {
    fn on_weather_roll(&mut self, _payload: &WeatherRollHookPayload) -> Vec<String> {
        vec![self.0.to_owned()]
    }
}

fn engine_with_override(seed: &str, weather_id: &'static str) -> (EngineContext, GameState) {
    let project = starter_farm_project();
    let mut hooks = HookBus::new();
    hooks.set_weather_roll_listener(Box::new(WeatherOverride(weather_id)));
    let ctx = EngineContext::with_hooks(state::create_content_from_project(&project), hooks);
    let state = state::create_game_state(&project, Some(seed));
    (ctx, state)
}

#[test]
fn a_hook_listener_can_override_the_rolled_weather_with_a_valid_type() {
    let (ctx, mut next) = engine_with_override("wx", "sun");
    game_time::perform_sleep(&ctx, &mut next, game_time::SleepOptions { collapsed: false });
    assert_eq!(next.clock.weather_id, "sun");
    let events = ctx.drain_hook_events();
    assert!(events.iter().any(|event| event.hook() == "onWeatherRoll"));
}

#[test]
fn invalid_overrides_are_ignored() {
    let (ctx, mut next) = engine_with_override("wx2", "sharknado");
    game_time::perform_sleep(&ctx, &mut next, game_time::SleepOptions { collapsed: false });
    assert!(ctx.content.weather.types.iter().any(|weather_type| weather_type.id == next.clock.weather_id));
    assert_ne!(next.clock.weather_id, "sharknado");
}

// --- indoor scenes keep the weather out (#34) ---

#[test]
fn rain_and_storms_skip_indoor_scenes() {
    for (weather_id, waters, damage) in [("rain", true, 0.0), ("storm", true, 1.0)] {
        let (ctx, mut current) = make_engine(|project| {
            project.weather = always_weather(weather_id, vec![weather_type(weather_id, weather_id, waters, damage)]);
            project.scenes[0].indoor = Some(true);
        });
        at(&mut current, 8, 5, "up");
        farm_sim::apply_command(&ctx, &mut current, &Command::Interact);
        assert!(current.world.scenes[0].tiles[4][8].crop.is_some());
        // Day 2 rolls the weather; day 3's overnight pass would water or wreck an outdoor bed.
        farm_sim::apply_command(&ctx, &mut current, &Command::Sleep);
        farm_sim::apply_command(&ctx, &mut current, &Command::Sleep);
        assert_eq!(current.clock.weather_id, weather_id);
        let tile = &current.world.scenes[0].tiles[4][8];
        let crop = tile.crop.as_ref().expect("the greenhouse crop survives the storm");
        assert!(!crop.watered, "{weather_id} does not water indoor crops");
        assert_ne!(tile.soil_state.as_deref(), Some("watered"));
    }
}

#[test]
fn storms_keep_npcs_home_only_outdoors() {
    use farm_sim::schema::NpcScheduleEntry;
    // A storm keeps scheduled NPCs from walking outside; inside, an indoor schedule still runs.
    let run = |indoor: bool| {
        let (ctx, mut current) = make_engine(|project| {
            let storm = WeatherTypeDefinition { npcs_stay_inside: true, ..weather_type("storm", "Storm", false, 0.0) };
            project.weather = always_weather("storm", vec![storm]);
            project.scenes[0].indoor = Some(indoor);
            let npc = &mut project.npcs[0];
            let scene_id = npc.scene_id.clone();
            npc.schedule = Some(vec![NpcScheduleEntry {
                minute: 0,
                scene_id,
                x: units::tile_of(npc.x) + 1,
                y: units::tile_of(npc.y),
                ..Default::default()
            }]);
        });
        current.clock.weather_id = "storm".to_owned();
        let id = ctx.content.npcs[0].id.clone();
        let before = current.npcs[&id].clone();
        farm_sim::engine::advance_tick(&ctx, &mut current, 40);
        current.npcs[&id] != before
    };
    assert!(!run(false), "outdoors the storm keeps the NPC put");
    assert!(run(true), "indoors the NPC keeps its schedule");
}

#[test]
fn generated_mine_floors_are_indoor() {
    let (ctx, _) = make_engine(|project| *project = with_m4_content(project.clone(), SCENE));
    assert!(mines::generate_mine_floor(&ctx, "seed", 1).is_indoor());
}
