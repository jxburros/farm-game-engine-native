//! Port of the retired C# `M9CalendarTests.cs` (engine-core/src/m9-calendar.test.ts):
//! M9 calendar tests — creator-configurable seasons + festival days.
//!
//! The C# built its engine from `EngineTests.MakeProject()`; here states come from the
//! `project` in `fixtures/golden/content/starter-farm.json`. Tests that go through the overnight
//! pass need `crafting::settle_machines`, and the crop/event ones need the engine dispatch; those
//! are `#[ignore]`d until the other modules land.

use farm_sim::engine::advance_tick;
use farm_sim::farming::crops;
use farm_sim::schema::{
    event_outcome_types, CalendarConfig, CalendarFestival, CalendarSeason, CustomCropDefinition, EventCondition,
    EventOutcome, GameContent, GameEvent, GameProject, GameState, InventorySlot, Item, ProjectSettings, WeatherConfig,
    WeatherTableEntry, WeatherTypeDefinition,
};
use farm_sim::units;
use farm_sim::{game_time, state, weather, Command, Effect, Effects, EngineContext};
use indexmap::IndexMap;
use std::path::PathBuf;

fn season(id: &str, name: &str, days: u32) -> CalendarSeason {
    CalendarSeason { id: id.to_owned(), name: name.to_owned(), days }
}

fn custom_calendar() -> CalendarConfig {
    CalendarConfig {
        seasons: vec![season("a", "Alpha", 10), season("b", "Beta", 10), season("c", "Gamma", 10)],
        festivals: Vec::new(),
    }
}

fn calendar_with_festival() -> CalendarConfig {
    CalendarConfig {
        seasons: vec![season("a", "Alpha", 10)],
        festivals: vec![CalendarFestival {
            id: "harvest-fest".to_owned(),
            name: "Harvest Festival".to_owned(),
            season_id: "a".to_owned(),
            day: 5,
        }],
    }
}

fn starter_farm_project() -> GameProject {
    let path: PathBuf =
        [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", "content", "starter-farm.json"].iter().collect();
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
    let fixture: serde_json::Value = serde_json::from_str(&text).expect("valid fixture JSON");
    serde_json::from_value(fixture["project"].clone()).expect("starter-farm project is a GameProject")
}

fn make_engine(mutate: impl FnOnce(&mut GameProject)) -> (EngineContext, GameState) {
    let mut project = starter_farm_project();
    mutate(&mut project);
    let ctx = EngineContext::new(state::create_content_from_project(&project));
    let state = state::create_game_state(&project, Some("m9"));
    (ctx, state)
}

fn with_custom_calendar(calendar: CalendarConfig) -> impl FnOnce(&mut GameProject) {
    move |project| {
        project.current_season = calendar.seasons[0].id.clone();
        project.settings.calendar = calendar;
        project.current_day = 1;
        project.current_year = 1;
    }
}

fn has_message(effects: &Effects, predicate: impl Fn(&str) -> bool) -> bool {
    effects.iter().any(|effect| matches!(effect, Effect::Message { text, .. } if predicate(text)))
}

fn season_ids(seasons: &[CalendarSeason]) -> Vec<&str> {
    seasons.iter().map(|s| s.id.as_str()).collect()
}

// --- calendar math (pure functions) ---

#[test]
fn resolves_day_of_season_season_year_across_uneven_and_even_custom_calendars() {
    let calendar = custom_calendar();
    assert_eq!(season_ids(&game_time::calendar_seasons(&calendar)), ["a", "b", "c"]);
    assert_eq!(game_time::day_of_season(&calendar, 1), 1);
    assert_eq!(game_time::day_of_season(&calendar, 10), 10);
    assert_eq!(game_time::day_of_season(&calendar, 11), 1);
    assert_eq!(game_time::season_for_day(&calendar, 1), "a");
    assert_eq!(game_time::season_for_day(&calendar, 10), "a");
    assert_eq!(game_time::season_for_day(&calendar, 11), "b");
    assert_eq!(game_time::season_for_day(&calendar, 21), "c");
    assert_eq!(game_time::season_for_day(&calendar, 30), "c");
    assert_eq!(game_time::season_for_day(&calendar, 31), "a");
    assert_eq!(game_time::year_for_day(&calendar, 30), 1);
    assert_eq!(game_time::year_for_day(&calendar, 31), 2);
}

#[test]
fn honors_each_seasons_own_length_for_heterogeneous_calendars() {
    let uneven =
        CalendarConfig { seasons: vec![season("short", "Short", 5), season("long", "Long", 20)], festivals: vec![] };
    assert_eq!(game_time::day_of_season(&uneven, 5), 5);
    assert_eq!(game_time::season_for_day(&uneven, 5), "short");
    assert_eq!(game_time::day_of_season(&uneven, 6), 1);
    assert_eq!(game_time::season_for_day(&uneven, 6), "long");
    assert_eq!(game_time::day_of_season(&uneven, 25), 20);
    assert_eq!(game_time::season_for_day(&uneven, 25), "long");
    assert_eq!(game_time::season_for_day(&uneven, 26), "short"); // year wraps: 5 + 20 = 25 days/year
    assert_eq!(game_time::year_for_day(&uneven, 25), 1);
    assert_eq!(game_time::year_for_day(&uneven, 26), 2);
}

#[test]
fn falls_back_to_the_classic_four_season_calendar_when_seasons_is_empty_or_degenerate() {
    let empty = CalendarConfig { seasons: vec![], festivals: vec![] };
    assert_eq!(season_ids(&game_time::calendar_seasons(&empty)), ["spring", "summer", "fall", "winter"]);
    assert_eq!(game_time::season_for_day(&empty, 1), "spring");
    assert_eq!(game_time::season_for_day(&empty, 29), "summer");

    let all_zero = CalendarConfig { seasons: vec![season("x", "X", 0)], festivals: vec![] };
    assert_eq!(season_ids(&game_time::calendar_seasons(&all_zero)), ["spring", "summer", "fall", "winter"]);
}

#[test]
fn season_by_id_looks_up_the_effective_fallback_safe_season_list() {
    let calendar = custom_calendar();
    assert_eq!(game_time::season_by_id(&calendar, "b").map(|s| s.name.as_str()), Some("Beta"));
    assert!(game_time::season_by_id(&calendar, "nope").is_none());
    let empty = CalendarConfig { seasons: vec![], festivals: vec![] };
    assert_eq!(game_time::season_by_id(&empty, "spring").map(|s| s.name.as_str()), Some("Spring"));
}

// --- custom calendar day/season/year rollover via performSleep ---

#[test]
fn rolls_season_and_year_correctly_across_a_3x10_day_calendar() {
    let calendar = custom_calendar();
    let (ctx, mut current) = make_engine(with_custom_calendar(calendar.clone()));
    assert_eq!(current.clock.day, 1);
    assert_eq!(current.clock.season, "a");
    assert_eq!(current.clock.year, 1);

    // Sleep 9 times: day 1 → day 10, season stays 'a'.
    for _ in 0..9 {
        game_time::perform_sleep(&ctx, &mut current, game_time::SleepOptions::default());
    }
    assert_eq!(current.clock.day, 10);
    assert_eq!(current.clock.season, "a");
    assert_eq!(game_time::day_of_season(&calendar, current.clock.day), 10);

    // Sleep once more: day 10 → 11 crosses into season 'b'.
    let rolled = game_time::perform_sleep(&ctx, &mut current, game_time::SleepOptions::default());
    assert_eq!(current.clock.day, 11);
    assert_eq!(current.clock.season, "b");
    assert_eq!(current.clock.year, 1);
    assert!(has_message(&rolled, |text| text.contains("Beta has arrived")));

    // Sleep through 'b' and 'c' (19 more days) to complete the year: day 11 → day 30.
    for _ in 0..19 {
        game_time::perform_sleep(&ctx, &mut current, game_time::SleepOptions::default());
    }
    assert_eq!(current.clock.day, 30);
    assert_eq!(current.clock.season, "c");
    assert_eq!(current.clock.year, 1);

    // One more sleep wraps back to season 'a' and rolls the year.
    let year_rolled = game_time::perform_sleep(&ctx, &mut current, game_time::SleepOptions::default());
    assert_eq!(current.clock.day, 31);
    assert_eq!(current.clock.season, "a");
    assert_eq!(current.clock.year, 2);
    assert!(has_message(&year_rolled, |text| text.contains("Year 2 begins")));
}

#[test]
fn formats_day_x_of_season_using_the_calendars_day_of_season_count() {
    let (ctx, mut state) = make_engine(with_custom_calendar(custom_calendar()));
    let effects = game_time::perform_sleep(&ctx, &mut state, game_time::SleepOptions::default());
    assert!(has_message(&effects, |text| text == "Day 2 of a, Year 1"));
}

// --- festivals ---

#[test]
fn announces_the_festival_only_on_its_configured_day() {
    let (ctx, mut current) = make_engine(with_custom_calendar(calendar_with_festival()));
    // Day 1 → 2: not the festival.
    let not_yet = game_time::perform_sleep(&ctx, &mut current, game_time::SleepOptions::default());
    assert!(!has_message(&not_yet, |text| text.contains("Harvest Festival")));

    // Advance to day 4, then sleep into day 5 (the festival).
    for _ in 0..2 {
        game_time::perform_sleep(&ctx, &mut current, game_time::SleepOptions::default());
    }
    assert_eq!(current.clock.day, 4);
    let festival_day = game_time::perform_sleep(&ctx, &mut current, game_time::SleepOptions::default());
    assert_eq!(current.clock.day, 5);
    assert!(has_message(&festival_day, |text| text == "Today is the Harvest Festival!"));

    // The day after is quiet again.
    let after = game_time::perform_sleep(&ctx, &mut current, game_time::SleepOptions::default());
    assert!(!has_message(&after, |text| text.contains("Harvest Festival")));
}

#[test]
fn festival_on_day_resolves_the_festival_for_its_day_only() {
    let calendar = calendar_with_festival();
    assert_eq!(game_time::festival_on_day(&calendar, 5).map(|f| f.id.as_str()), Some("harvest-fest"));
    assert!(game_time::festival_on_day(&calendar, 4).is_none());
    // next year, same day-of-season
    assert_eq!(game_time::festival_on_day(&calendar, 15).map(|f| f.id.as_str()), Some("harvest-fest"));
}

#[test]
fn the_festival_id_event_condition_is_true_only_on_the_festival_day() {
    let festival_event = GameEvent {
        id: "evt-festival".to_owned(),
        name: "Festival banner".to_owned(),
        scene_id: String::new(),
        trigger: "tick".to_owned(),
        conditions: vec![EventCondition::FestivalId { festival_id: "harvest-fest".to_owned() }],
        outcomes: vec![EventOutcome {
            r#type: event_outcome_types::SET_FLAG.to_owned(),
            flag_name: Some("festival-seen".to_owned()),
            ..EventOutcome::default()
        }],
        active: true,
        repeatable: true,
        ..GameEvent::default()
    };
    let (ctx, state) = make_engine(|project| {
        with_custom_calendar(calendar_with_festival())(project);
        project.events = vec![festival_event];
    });

    // Not the festival day yet: the tick event does not fire.
    let mut early = state.clone();
    advance_tick(&ctx, &mut early, 20);
    assert!(!early.flags.contains_key("festival-seen"));

    // Sleep to the festival day (day 5), then tick.
    let mut current = state;
    for _ in 0..4 {
        game_time::perform_sleep(&ctx, &mut current, game_time::SleepOptions::default());
    }
    assert_eq!(current.clock.day, 5);
    advance_tick(&ctx, &mut current, 20);
    assert_eq!(current.flags.get("festival-seen"), Some(&serde_json::Value::Bool(true)));

    // The next day, the condition is false again.
    game_time::perform_sleep(&ctx, &mut current, game_time::SleepOptions::default());
    current.flags = IndexMap::new();
    advance_tick(&ctx, &mut current, 20);
    assert!(!current.flags.contains_key("festival-seen"));
}

// --- weather fallback for custom seasons ---
// The TS tests build ctx via makeProject + createContentFromProject, which only copies
// project.weather into content.weather; these build that content directly so the weather
// lookup is covered before integration.

fn context_with_weather(weather: WeatherConfig) -> EngineContext {
    EngineContext::new(GameContent {
        settings: ProjectSettings { calendar: custom_calendar(), ..ProjectSettings::default() },
        weather,
        ..GameContent::default()
    })
}

fn sun_entry() -> WeatherTableEntry {
    WeatherTableEntry { weather_id: "sun".to_owned(), weight: 1 }
}

#[test]
fn falls_back_to_another_configured_table_when_a_custom_season_has_none_of_its_own() {
    let mut table = IndexMap::new();
    // Only season 'a' gets an explicit table; 'b' and 'c' are untouched.
    table.insert("a".to_owned(), vec![sun_entry()]);
    let ctx = context_with_weather(WeatherConfig {
        types: vec![WeatherTypeDefinition {
            id: "sun".to_owned(),
            name: "Sunny".to_owned(),
            waters_outdoor_soil: false,
            crop_damage_chance: 0,
            npcs_stay_inside: false,
            overlay: None,
            ..WeatherTypeDefinition::default()
        }],
        table,
        ..WeatherConfig::default()
    });
    assert_eq!(weather::weather_table_for_season(&ctx, "a"), vec![sun_entry()]);
    // 'b' has no table of its own — falls back to 'a's table rather than an empty roll.
    assert_eq!(weather::weather_table_for_season(&ctx, "b"), vec![sun_entry()]);
}

#[test]
fn returns_an_empty_table_when_nothing_is_configured_at_all() {
    let ctx = context_with_weather(WeatherConfig { types: vec![], table: IndexMap::new(), ..WeatherConfig::default() });
    assert!(weather::weather_table_for_season(&ctx, "a").is_empty());
}

// --- crops with custom season ids ---

fn custom_crop() -> CustomCropDefinition {
    CustomCropDefinition {
        id: "moonflower".to_owned(),
        name: "Moonflower".to_owned(),
        seed_cost: 10,
        base_harvest_value: 20,
        growth_time: 15000,
        growth_days: Some(3),
        stages: 4,
        seasons: vec!["a".to_owned()],
        can_regrow: false,
        yield_min: 1,
        yield_max: 2,
        mutation_chance: Some(farm_sim::units::chance(0.01)),
        ..CustomCropDefinition::default()
    }
}

fn seed_item() -> Item {
    Item {
        id: "seed-moonflower".to_owned(),
        name: "Moonflower Seeds".to_owned(),
        description: "A custom-season seed".to_owned(),
        r#type: "seed".to_owned(),
        stackable: true,
        max_stack: 99,
        value: 10,
        crop_type: Some("moonflower".to_owned()),
        ..Item::default()
    }
}

#[test]
fn can_grow_in_season_accepts_custom_season_ids_directly() {
    let definition = crops::to_crop_definition(&custom_crop());
    assert!(crops::can_grow_in_season(Some(&definition), "a"));
    assert!(!crops::can_grow_in_season(Some(&definition), "b"));
}

#[test]
fn grows_while_its_season_is_current_and_withers_once_the_calendar_rolls_out_of_it() {
    let (ctx, state) = make_engine(|project| {
        with_custom_calendar(custom_calendar())(project);
        project.custom_crops = Some(vec![custom_crop()]);
        // Stand on the starter farm's soil bed (5..11, 4..6) facing an empty soil tile at (8,4).
        project.player.x = units::tiles(8);
        project.player.y = units::tiles(5);
        project.player.direction = "up".to_owned();
        let watering_can = project.items.iter().find(|i| i.id == "tool-watering-can").expect("watering can").clone();
        project.player.inventory = vec![InventorySlot::new(seed_item(), 1), InventorySlot::new(watering_can, 1)];
    });

    // Plant on the soil tile (facing up from (8,5) → (8,4)).
    let mut current = state;
    farm_sim::apply_command(&ctx, &mut current, &Command::Interact);
    let tile = &current.world.scenes[0].tiles[4][8];
    assert_eq!(tile.crop.as_ref().map(|c| c.r#type.as_str()), Some("moonflower"));
    assert_ne!(tile.crop.as_ref().and_then(|c| c.withered), Some(true));

    // Water + sleep for two days, still within season 'a' (10 days long) — grows, doesn't wither.
    for _ in 0..2 {
        farm_sim::apply_command(&ctx, &mut current, &Command::UseTool { tool: "watering-can".to_owned() });
        farm_sim::apply_command(&ctx, &mut current, &Command::Sleep);
    }
    let tile = &current.world.scenes[0].tiles[4][8];
    assert_ne!(tile.crop.as_ref().and_then(|c| c.withered), Some(true));
    assert_eq!(tile.crop.as_ref().and_then(|c| c.days_grown), Some(2));

    // Jump to the last day of season 'a' and sleep past the boundary into 'b'.
    current.clock.day = 10;
    farm_sim::apply_command(&ctx, &mut current, &Command::Sleep);
    assert_eq!(current.clock.season, "b");
    assert_eq!(current.world.scenes[0].tiles[4][8].crop.as_ref().and_then(|c| c.withered), Some(true));
}
