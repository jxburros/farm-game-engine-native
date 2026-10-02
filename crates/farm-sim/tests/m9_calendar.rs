//! Port of the retired C# `M9CalendarTests.cs` (engine-core/src/m9-calendar.test.ts):
//! M9 calendar tests — creator-configurable seasons + festival days.
//!
//! The C# built its engine from `EngineTests.MakeProject()`; here states come from the
//! `project` in `fixtures/golden/content/starter-farm.json`. Tests that go through the overnight
//! pass use `crafting::settle_machines`, and the crop/event ones the engine dispatch.

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
    current.clock.day_of_season = 10;
    farm_sim::apply_command(&ctx, &mut current, &Command::Sleep);
    assert_eq!(current.clock.season, "b");
    assert_eq!(current.world.scenes[0].tiles[4][8].crop.as_ref().and_then(|c| c.withered), Some(true));
}

// --- the clock's own calendar position (#23) ---

/// A minimal game on `calendar` that starts on `current_day` with `current_season`.
fn calendar_game(calendar: CalendarConfig, current_season: &str, current_day: u32) -> (EngineContext, GameState) {
    let mut project = GameProject { id: "calendar".to_owned(), ..GameProject::default() };
    project.settings.calendar = calendar;
    project.current_season = current_season.to_owned();
    project.current_day = current_day;
    project.current_year = 1;
    let ctx = EngineContext::new(state::create_content_from_project(&project));
    let state = state::create_game_state(&project, Some("calendar"));
    (ctx, state)
}

/// Sleeps `days` times: per new day, its season, day of season, year and whether a festival
/// message came.
fn sleep_days(ctx: &EngineContext, state: &mut GameState, days: u32) -> Vec<(String, u32, u32, Option<String>)> {
    (0..days)
        .map(|_| {
            let effects = game_time::perform_sleep(ctx, state, game_time::SleepOptions::default());
            let festival = effects.iter().find_map(|effect| match effect {
                Effect::Message { text, .. } if text.starts_with("Today is the ") => Some(text.clone()),
                _ => None,
            });
            (state.clock.season.clone(), state.clock.day_of_season, state.clock.year, festival)
        })
        .collect()
}

fn issue_calendar() -> CalendarConfig {
    CalendarConfig {
        seasons: vec![season("spring", "Spring", 5), season("summer", "Summer", 20), season("fall", "Fall", 5)],
        festivals: vec![CalendarFestival {
            id: "summer-fest".to_owned(),
            name: "Summer Fest".to_owned(),
            season_id: "summer".to_owned(),
            day: 3,
        }],
    }
}

#[test]
fn a_game_that_starts_in_a_later_season_keeps_every_season_length_and_festival_day() {
    // Starting in summer on absolute day 1 (spring by the absolute day) used to give summer 5
    // days, fall days 6–20 and the summer festival on fall day 3.
    let (ctx, mut state) = calendar_game(issue_calendar(), "summer", 1);
    assert_eq!((state.clock.season.as_str(), state.clock.day_of_season), ("summer", 1));
    let days = sleep_days(&ctx, &mut state, 60);
    let summer = days.iter().take_while(|(season, ..)| season == "summer").count();
    assert_eq!(summer, 19, "summer days 2–20 follow the start");
    assert_eq!(days[19].0, "fall");
    assert_eq!(days[19].1, 1);
    let fall = days.iter().skip(19).take_while(|(season, ..)| season == "fall").count();
    assert_eq!(fall, 5);
    for (season, day, _, festival) in &days {
        assert!(*day >= 1 && *day <= game_time::season_by_id(&ctx.content.settings.calendar, season).unwrap().days);
        assert_eq!(festival.is_some(), season == "summer" && *day == 3, "{season} {day}");
    }
    // Spring starts year 2.
    assert_eq!(days[24], ("spring".to_owned(), 1, 2, None));
}

#[test]
fn the_clock_keeps_its_place_when_season_lengths_change() {
    let (_, mut state) = calendar_game(issue_calendar(), "summer", 1);
    state.clock.day_of_season = 18;
    // Summer shrinks to 10 days: day 18 becomes its last day.
    let mut shorter = issue_calendar();
    shorter.seasons[1].days = 10;
    game_time::reconcile_clock(&shorter, &mut state.clock);
    assert_eq!(state.clock.day_of_season, 10);
    let ctx = EngineContext::new(state::create_content_from_project(&{
        let mut project = GameProject::default();
        project.settings.calendar = shorter;
        project
    }));
    game_time::perform_sleep(&ctx, &mut state, game_time::SleepOptions::default());
    assert_eq!((state.clock.season.as_str(), state.clock.day_of_season), ("fall", 1));
}

#[test]
fn a_clock_without_a_day_of_season_takes_its_absolute_day_place() {
    let calendar = issue_calendar();
    let mut clock = farm_sim::schema::ClockState { day: 9, season: "summer".to_owned(), ..Default::default() };
    // Absolute day 9 is summer day 4.
    game_time::reconcile_clock(&calendar, &mut clock);
    assert_eq!(clock.day_of_season, 4);
    // Not summer by the absolute day: its place in its own season, within the season.
    let mut clock = farm_sim::schema::ClockState { day: 27, season: "spring".to_owned(), ..Default::default() };
    game_time::reconcile_clock(&calendar, &mut clock);
    assert_eq!(clock.day_of_season, 2);
    let mut clock = farm_sim::schema::ClockState { day: 24, season: "spring".to_owned(), ..Default::default() };
    game_time::reconcile_clock(&calendar, &mut clock);
    assert_eq!(clock.day_of_season, 5, "summer day 19 is past spring's 5 days");
}

#[test]
fn a_repeated_season_id_cannot_trap_the_calendar() {
    // [a, b, a]: Problems rejects the repeat; the engine counts it once, so the calendar cycles
    // a → b → a with one year per cycle instead of skipping the third season forever.
    let calendar = CalendarConfig {
        seasons: vec![season("a", "A", 2), season("b", "B", 2), season("a", "A again", 3)],
        festivals: Vec::new(),
    };
    assert_eq!(season_ids(&game_time::calendar_seasons(&calendar)), vec!["a", "b"]);
    let (ctx, mut state) = calendar_game(calendar, "a", 1);
    let days = sleep_days(&ctx, &mut state, 8);
    let seasons: Vec<(&str, u32, u32)> = days.iter().map(|(s, d, y, _)| (s.as_str(), *d, *y)).collect();
    assert_eq!(
        seasons,
        vec![("a", 2, 1), ("b", 1, 1), ("b", 2, 1), ("a", 1, 2), ("a", 2, 2), ("b", 1, 2), ("b", 2, 2), ("a", 1, 3)]
    );
}

#[test]
fn keep_changes_carries_the_day_of_season_only_when_the_day_does_not() {
    let mut project = GameProject { id: "calendar".to_owned(), ..GameProject::default() };
    project.settings.calendar = issue_calendar();
    project.current_season = "summer".to_owned();
    project.current_day = 1;
    let ctx = EngineContext::new(state::create_content_from_project(&project));
    let mut game = state::create_game_state(&project, Some("keep"));
    sleep_days(&ctx, &mut game, 10);
    assert_eq!((game.clock.season.as_str(), game.clock.day_of_season, game.clock.day), ("summer", 11, 11));
    let kept = state::apply_state_to_project(&project, &game);
    assert_eq!(kept.current_day_of_season, Some(11), "absolute day 11 is summer day 6");
    // The next playtest starts where this one ended.
    let next = state::create_game_state(&kept, Some("keep"));
    assert_eq!((next.clock.season.as_str(), next.clock.day_of_season), ("summer", 11));

    // A game that started on day 1 of the first season needs no extra field.
    project.current_season = "spring".to_owned();
    let mut game = state::create_game_state(&project, Some("keep"));
    sleep_days(&ctx, &mut game, 10);
    assert_eq!(state::apply_state_to_project(&project, &game).current_day_of_season, None);
}

proptest::proptest! {
    #![proptest_config(proptest::prelude::ProptestConfig { cases: 48, ..Default::default() })]

    /// For any season lengths, start season and start day, every season after the start lasts
    /// exactly its configured length, every festival fires on its configured day and only then,
    /// and the year turns when the first season comes back.
    #[test]
    fn seasons_last_their_length_and_festivals_fire_on_their_day(
        lengths in proptest::collection::vec(1u32..=6, 1..=4),
        start in 0usize..4,
        start_day in 1u32..=40,
        festival_season in 0usize..4,
        festival_day in 1u32..=6,
    ) {
        let seasons: Vec<CalendarSeason> =
            lengths.iter().enumerate().map(|(i, days)| season(&format!("s{i}"), &format!("S{i}"), *days)).collect();
        let festival_season = festival_season % seasons.len();
        let festival_day = festival_day.min(seasons[festival_season].days);
        let calendar = CalendarConfig {
            festivals: vec![CalendarFestival {
                id: "fest".to_owned(),
                name: "Fest".to_owned(),
                season_id: seasons[festival_season].id.clone(),
                day: festival_day,
            }],
            seasons: seasons.clone(),
        };
        let start = start % seasons.len();
        let (ctx, mut state) = calendar_game(calendar, &seasons[start].id, start_day);
        let year_length: u32 = lengths.iter().sum();
        let days = sleep_days(&ctx, &mut state, year_length * 3);

        // Runs of one season after the first change have the season's length.
        let mut runs: Vec<(String, u32)> = Vec::new();
        for (season, ..) in &days {
            match runs.last_mut() {
                Some((current, count)) if current == season => *count += 1,
                _ => runs.push((season.clone(), 1)),
            }
        }
        for (season, count) in runs.iter().skip(1).take(runs.len().saturating_sub(2)) {
            let index = seasons.iter().position(|s| &s.id == season).unwrap();
            proptest::prop_assert_eq!(*count, lengths[index], "season {} ran {} days", season, count);
        }
        for (season, day, _, festival) in &days {
            let on_festival = *season == seasons[festival_season].id && *day == festival_day;
            proptest::prop_assert_eq!(festival.is_some(), on_festival);
        }
        // Each return of the first season starts a new year.
        let mut year = 1;
        let mut previous = seasons[start].id.clone();
        for (season, day, y, _) in &days {
            if *season == seasons[0].id && *day == 1 && *season != previous || (seasons.len() == 1 && *day == 1) {
                year += 1;
            }
            proptest::prop_assert_eq!(*y, year);
            previous = season.clone();
        }
    }
}
