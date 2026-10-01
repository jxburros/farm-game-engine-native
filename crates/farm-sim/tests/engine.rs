//! Port of the retired C# `EngineTests.cs` (engine.test.ts): the engine reducer
//! end to end. Most cases drive the whole simulation (movement, tools, crops, dialogue, sleep);
//! the cases at the end exercise the reducer itself (`apply_command` dispatch, the `onCommand`
//! hook, the per-tick clock).

mod common;

use common::{has_message, make_engine, make_project};
use farm_sim::schema::{DialogueState, GameProject, GameState, MinigameSession, Quest, QuestObjective, QuestRewards};
use farm_sim::units;
use farm_sim::{engine, game_time, state, Command, Effect, EngineContext, HookEvent};

fn engine_state(seed: &str) -> (EngineContext, GameState) {
    make_engine(|_| {}, seed)
}

/// Water the facing tile then sleep — one full watered day for a crop.
fn water_and_sleep(ctx: &EngineContext, state: &mut GameState, days: u32) {
    for _ in 0..days {
        engine::apply_command(ctx, state, &Command::UseTool { tool: "watering-can".to_owned() });
        engine::apply_command(ctx, state, &Command::Sleep);
    }
}

fn facing_soil(state: &mut GameState) {
    state.player.x = units::tiles(3);
    state.player.y = units::tiles(3);
    state.player.direction = "up".to_owned();
}

fn r#move(dir: &str) -> Command {
    Command::Move { dir: dir.to_owned() }
}

fn crop_type(state: &GameState) -> Option<String> {
    state.world.scenes[0].tiles[2][3].crop.as_ref().map(|c| c.r#type.clone())
}

// --- movement ---

#[test]
fn moves_the_player_and_updates_direction() {
    let (ctx, mut state) = engine_state("engine-test");
    let effects = engine::apply_command(&ctx, &mut state, &r#move("left"));
    assert_eq!(state.player.x, units::pos(2.5));
    assert_eq!(state.player.y, units::pos(4.5));
    assert_eq!(state.player.direction, "left");
    assert!(effects.contains(&Effect::PlayerMoved { x: 2, y: 4 }));
}

#[test]
fn blocks_movement_into_walls_but_still_turns() {
    let (ctx, mut state) = engine_state("engine-test");
    engine::apply_command(&ctx, &mut state, &r#move("right"));
    assert_eq!(state.player.x, units::pos(3.5));
    assert_eq!(state.player.direction, "right");
}

#[test]
fn blocks_movement_out_of_bounds() {
    let (ctx, mut state) = engine_state("engine-test");
    for _ in 0..10 {
        engine::apply_command(&ctx, &mut state, &r#move("down"));
    }
    assert_eq!(state.player.y, units::pos(5.5));
}

#[test]
fn blocks_movement_onto_npcs() {
    let (ctx, mut state) = engine_state("engine-test");
    state.player.x = units::tiles(1);
    state.player.y = units::tiles(2);
    engine::apply_command(&ctx, &mut state, &r#move("up"));
    assert_eq!(state.player.y, units::tiles(2)); // npc at (1,1)
}

// --- tools ---

#[test]
fn tills_grass_into_soil_consuming_durability_and_energy() {
    let (ctx, mut state) = engine_state("engine-test");
    // player at (3,4) facing up → target (3,3) is grass
    let effects = engine::apply_command(&ctx, &mut state, &Command::UseTool { tool: "hoe".to_owned() });
    let tile = &state.world.scenes[0].tiles[3][3];
    assert_eq!(tile.background, "soil");
    assert_eq!(tile.soil_state.as_deref(), Some("dry"));
    assert!(effects.contains(&Effect::message("success", "Tilled soil!")));
    let hoe = state.player.inventory.iter().find(|s| s.item.tool_type.as_deref() == Some("hoe")).expect("hoe");
    assert_eq!(hoe.item.durability, Some(99));
    assert_eq!(state.player.energy, units::points(96)); // hoe costs 4
}

#[test]
fn waters_soil_and_marks_crops_watered() {
    let (ctx, mut state) = engine_state("engine-test");
    facing_soil(&mut state);
    engine::apply_command(&ctx, &mut state, &Command::UseTool { tool: "watering-can".to_owned() });
    let tile = &state.world.scenes[0].tiles[2][3];
    assert_eq!(tile.soil_moisture, 100);
    assert_eq!(tile.soil_state.as_deref(), Some("watered"));
    assert_eq!(state.player.energy, units::points(98)); // watering can costs 2
}

#[test]
fn reports_missing_tools() {
    let (ctx, mut state) = engine_state("engine-test");
    state.player.inventory.clear();
    let before = state.clone();
    let effects = engine::apply_command(&ctx, &mut state, &Command::UseTool { tool: "watering-can".to_owned() });
    assert!(effects.contains(&Effect::message("error", "You need a watering can!")));
    assert_eq!(state, before);
}

// --- planting and harvesting (day-based) ---

#[test]
fn plants_a_seed_on_soil_consuming_it_crop_starts_unwatered() {
    let (ctx, mut state) = engine_state("engine-test");
    facing_soil(&mut state);
    engine::apply_command(&ctx, &mut state, &Command::Interact);
    let crop = state.world.scenes[0].tiles[2][3].crop.clone().expect("planted crop");
    assert_eq!(crop.r#type, "wheat");
    assert!(!crop.watered);
    assert_eq!(crop.days_grown, Some(0));
    assert_eq!(crop.planted_on_day, Some(1));
    let seeds = state.player.inventory.iter().find(|s| s.item.id == "seed-wheat").expect("seeds");
    assert_eq!(seeds.quantity, 4);
}

#[test]
fn refuses_out_of_season_crops() {
    let (ctx, mut state) = engine_state("engine-test");
    state.clock.season = "summer".to_owned();
    facing_soil(&mut state);
    let effects = engine::apply_command(&ctx, &mut state, &Command::Interact);
    assert_eq!(crop_type(&state), None);
    assert!(has_message(&effects, |t| t.contains("cannot grow in summer")));
}

#[test]
fn crops_mature_after_growth_days_watered_days_harvest_yields_items_but_no_auto_money() {
    let (ctx, mut state) = engine_state("engine-test");
    facing_soil(&mut state);
    engine::apply_command(&ctx, &mut state, &Command::Interact); // plant wheat (3 growth days)
    water_and_sleep(&ctx, &mut state, 3);

    assert_eq!(state.world.scenes[0].tiles[2][3].crop.as_ref().and_then(|c| c.days_grown), Some(3));
    assert_eq!(state.clock.day, 4);

    let money_before_harvest = state.player.money;
    let effects = engine::apply_command(&ctx, &mut state, &Command::Interact);
    assert_eq!(crop_type(&state), None);
    let wheat = state.player.inventory.iter().find(|s| s.item.id == "crop-wheat").expect("harvested wheat");
    assert!(wheat.quantity >= 1);
    // No auto-sell: money only moves via the quest reward (100).
    assert_eq!(state.player.money, money_before_harvest + 100);
    assert!(state.player.completed_quests.contains(&"quest-wheat".to_owned()));
    assert_eq!(state.quests["quest-wheat"].status, "completed");
    assert!(effects.iter().any(|e| matches!(e, Effect::QuestCompleted { .. })));
}

#[test]
fn unwatered_crops_do_not_grow_overnight() {
    let (ctx, mut state) = engine_state("engine-test");
    facing_soil(&mut state);
    engine::apply_command(&ctx, &mut state, &Command::Interact);
    engine::apply_command(&ctx, &mut state, &Command::Sleep); // no watering
    let crop = state.world.scenes[0].tiles[2][3].crop.clone().expect("crop");
    assert_eq!(crop.days_grown, Some(0));
    assert_eq!(crop.days_without_water, 1);
}

#[test]
fn is_not_harvestable_before_maturity() {
    let (ctx, mut state) = engine_state("engine-test");
    facing_soil(&mut state);
    engine::apply_command(&ctx, &mut state, &Command::Interact);
    let effects = engine::apply_command(&ctx, &mut state, &Command::Interact);
    assert!(effects.contains(&Effect::message("info", "Crop is not ready to harvest yet")));
    assert!(state.world.scenes[0].tiles[2][3].crop.is_some());
}

// --- dialogue ---

#[test]
fn opens_dialogue_when_interacting_with_an_npc() {
    let (ctx, mut state) = engine_state("engine-test");
    state.player.x = units::tiles(1);
    state.player.y = units::tiles(2);
    state.player.direction = "up".to_owned();
    engine::apply_command(&ctx, &mut state, &Command::Interact);
    assert_eq!(state.dialogue, Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-1".to_owned() }));
}

#[test]
fn applies_option_rewards_and_follows_next_dialogue_id() {
    let (ctx, mut state) = engine_state("engine-test");
    state.dialogue = Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-1".to_owned() });
    engine::apply_command(&ctx, &mut state, &Command::ChooseDialogueOption { index: 1 });
    assert_eq!(state.player.money, 125);
    assert_eq!(state.dialogue, Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-2".to_owned() }));

    engine::apply_command(&ctx, &mut state, &Command::ChooseDialogueOption { index: 0 });
    assert_eq!(state.dialogue, None);
}

#[test]
fn close_dialogue_clears_dialogue_state() {
    let (ctx, mut state) = engine_state("engine-test");
    state.dialogue = Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-1".to_owned() });
    engine::apply_command(&ctx, &mut state, &Command::CloseDialogue);
    assert_eq!(state.dialogue, None);
}

#[test]
fn talking_to_an_npc_progresses_talk_quests() {
    let mut project = make_project();
    project.quests.push(Quest {
        id: "quest-talk".to_owned(),
        name: "Say hi".to_owned(),
        description: "Talk to Testy".to_owned(),
        status: "active".to_owned(),
        objectives: vec![QuestObjective {
            id: "obj-t".to_owned(),
            r#type: "talk".to_owned(),
            description: "Talk".to_owned(),
            target_npc_id: Some("npc-test".to_owned()),
            completed: false,
            progress: 0,
            ..QuestObjective::default()
        }],
        rewards: QuestRewards { money: Some(10), ..QuestRewards::default() },
        ..Quest::default()
    });
    project.player.active_quests.push("quest-talk".to_owned());
    let ctx = EngineContext::new(state::create_content_from_project(&project));
    let mut game_state = state::create_game_state(&project, Some("talk"));
    game_state.player.x = units::tiles(1);
    game_state.player.y = units::tiles(2);
    game_state.player.direction = "up".to_owned();
    engine::apply_command(&ctx, &mut game_state, &Command::Interact);
    assert!(game_state.player.completed_quests.contains(&"quest-talk".to_owned()));
    assert_eq!(game_state.player.money, 110);
}

// --- state ⇄ project bridge ---

#[test]
fn apply_state_to_project_round_trips_player_world_quests_and_flags() {
    let project = make_project();
    let ctx = EngineContext::new(state::create_content_from_project(&project));
    let mut game_state = state::create_game_state(&project, Some("bridge"));

    engine::apply_command(&ctx, &mut game_state, &r#move("left"));
    facing_soil(&mut game_state);
    engine::apply_command(&ctx, &mut game_state, &Command::Interact);

    let synced = state::apply_state_to_project(&project, &game_state);
    assert_eq!(synced.player.x, units::tiles(3));
    assert_eq!(synced.player.y, units::tiles(3));
    assert_eq!(synced.scenes[0].tiles[2][3].crop.as_ref().map(|c| c.r#type.as_str()), Some("wheat"));
    assert_eq!(synced.rng_state.as_ref(), Some(&game_state.rng));
    assert_eq!(synced.current_time_minutes, game_state.clock.time_minutes);
    // editor-only fields are preserved
    assert_eq!(synced.mode, project.mode);
    assert_eq!(synced.custom_assets, project.custom_assets);
}

#[test]
fn apply_state_to_project_json_keeps_the_values_the_creator_typed() {
    let mut project_json = serde_json::to_value(make_project()).expect("project serializes");
    // Off the 2⁻³² grid: reading it into a GameProject quantizes it.
    project_json["customCrops"] = serde_json::json!([{
        "id": "wheat", "name": "Wheat", "seedCost": 5, "baseHarvestValue": 12.5, "growthDays": 2,
        "stages": 3, "seasons": ["spring"], "mutationChance": 0.01, "customField": [1, 2.25]
    }]);
    let project: farm_sim::schema::GameProject = serde_json::from_value(project_json.clone()).expect("project");
    let ctx = EngineContext::new(state::create_content_from_project(&project));
    let mut game_state = state::create_game_state(&project, Some("bridge"));
    engine::apply_command(&ctx, &mut game_state, &r#move("left"));
    game_state.player.money = 1234;

    let synced = state::apply_state_to_project_json(&project_json, &game_state).expect("write-back");
    assert_eq!(synced["customCrops"], project_json["customCrops"], "content stays as typed");
    assert_eq!(synced["player"]["money"], 1234);
    let typed = serde_json::to_value(state::apply_state_to_project(&project, &game_state)).expect("typed");
    for key in ["scenes", "npcs", "quests", "animals", "rngState", "currentTimeMinutes"] {
        assert_eq!(synced[key], typed[key], "{key} comes from the state");
    }
    assert_eq!(synced["player"]["x"], typed["player"]["x"]);
    assert_ne!(typed["customCrops"], project_json["customCrops"], "the typed write-back quantizes");
}

#[test]
fn advance_tick_advances_the_game_clock_and_nothing_else_before_day_end() {
    let (ctx, mut game_state) = engine_state("clock");
    let before = game_state.clone();
    let effects = engine::advance_tick(&ctx, &mut game_state, 20); // 20 ticks = 1 real second = 1 game minute
    assert_eq!(game_state.clock.tick, 20);
    assert_eq!(game_state.clock.time_minutes, units::minutes(6 * 60 + 1));
    assert_eq!(game_state.player, before.player);
    assert_eq!(game_state.world, before.world);
    assert!(effects.is_empty());
}

#[test]
fn the_clock_passing_day_end_forces_a_collapse_into_the_next_day() {
    let (ctx, mut game_state) = engine_state("collapse");
    // 2am is 20h of game time from 6:00 = 1200 game minutes = 1200 real seconds = 24000 ticks
    let effects = engine::advance_tick(&ctx, &mut game_state, 24000);
    assert_eq!(game_state.clock.day, 2);
    assert_eq!(game_state.clock.time_minutes, units::minutes(6 * 60));
    assert!(has_message(&effects, |t| t.contains("collapsed")));
    // collapse penalty: half energy, money fee
    assert_eq!(game_state.player.energy, units::points(50));
    assert_eq!(game_state.player.money, 50);
}

// --- the reducer itself (no other gameplay module involved) ---

#[test]
fn apply_command_emits_the_on_command_hook_with_the_command_type() {
    let (ctx, mut game_state) = engine_state("hooks");
    ctx.drain_hook_events();
    engine::apply_command(&ctx, &mut game_state, &Command::CancelMinigame);
    engine::apply_command(&ctx, &mut game_state, &Command::PerformAction { action_id: "nope".to_owned() });
    let commands: Vec<String> = ctx
        .drain_hook_events()
        .into_iter()
        .filter_map(|event| match event {
            HookEvent::Command(payload) => Some(payload.command_type),
            _ => None,
        })
        .collect();
    assert_eq!(commands, vec!["cancelMinigame".to_owned(), "performAction".to_owned()]);
}

#[test]
fn advance_tick_with_no_ticks_is_a_no_op() {
    let (ctx, mut game_state) = engine_state("no-ticks");
    let before = game_state.clone();
    assert!(engine::advance_tick(&ctx, &mut game_state, 0).is_empty());
    // A negative count reads as 0 ticks.
    let negative: farm_sim::replay::ReplayInput = serde_json::from_str(r#"{"kind":"tick","ticks":-3}"#).unwrap();
    assert_eq!(negative, farm_sim::replay::ticks(0));
    assert_eq!(game_state, before);
}

#[test]
fn an_open_minigame_pauses_the_clock_and_freezes_the_body() {
    let (ctx, mut game_state) = engine_state("minigame-pause");
    assert!(ctx.content.settings.time.pauses_in_modals(), "pauseInModals is on by default");
    game_state.minigame = Some(MinigameSession { minigame_id: "mg".to_owned(), context: Default::default() });
    game_state.player.move_intent.dx = 1;
    let before = game_state.clone();
    // A whole game day of ticks: the clock stays put, so nothing minute-based runs and the day
    // never ends under the open minigame.
    let effects = engine::advance_tick(&ctx, &mut game_state, 20 * 60 * 24);
    assert!(effects.is_empty());
    assert_eq!(game_state.clock.tick, 20 * 60 * 24);
    assert_eq!(game_state.clock.time_minutes, before.clock.time_minutes);
    assert_eq!(game_state.clock.day, before.clock.day);
    assert_eq!(game_state.player, before.player);
    assert_eq!(game_state.npcs, before.npcs);

    // Closing it starts the clock again.
    game_state.minigame = None;
    engine::advance_tick(&ctx, &mut game_state, 20);
    assert_eq!(game_state.clock.time_minutes, units::time_of_day(361.0));
}

#[test]
fn ticks_step_the_clock_on_the_minute_quantum_while_a_minigame_freezes_the_body() {
    // Without `pauseInModals` (v8 behavior) the clock runs on under a modal.
    let (ctx, mut game_state) =
        make_engine(|project| project.settings.time.pause_in_modals = Some(false), "minigame-clock");
    game_state.minigame = Some(MinigameSession { minigame_id: "mg".to_owned(), context: Default::default() });
    game_state.player.move_intent.dx = 1;
    let before = game_state.clone();
    // 10 ticks = half a game minute: no minute boundary, so no NPC/machine/event step either.
    let effects = engine::advance_tick(&ctx, &mut game_state, 10);
    assert!(effects.is_empty());
    assert_eq!(game_state.clock.tick, 10);
    assert_eq!(game_state.clock.time_minutes, units::time_of_day(360.5));
    assert_eq!(game_state.player, before.player);
    assert_eq!(game_state.world, before.world);
    // advanceTick(N) is N × advanceTick(1). A fractional count in a replay reads rounded (2.5
    // → 3 ticks, as v8's loop ran).
    let mut stepped = before.clone();
    for _ in 0..3 {
        engine::advance_tick(&ctx, &mut stepped, 1);
    }
    let mut batched = before.clone();
    let fractional: farm_sim::replay::ReplayInput = serde_json::from_str(r#"{"kind":"tick","ticks":2.5}"#).unwrap();
    assert_eq!(fractional, farm_sim::replay::ticks(3));
    engine::advance_tick(&ctx, &mut batched, 3);
    assert_eq!(batched, stepped);
}

// --- the day window, modals and the clock (#24, #37) ---

#[test]
fn an_inverted_day_window_does_not_collapse_the_player_every_tick() {
    // dayStartMinute ≥ dayEndMinute used to collapse on every tick: ~1,200 days and every coin
    // gone in a minute of play.
    let (_, mut game_state) = engine_state("window");
    let mut project = make_project();
    project.settings.time.day_start_minute = 1560;
    project.settings.time.day_end_minute = 1500;
    // Settings resolution replaces just the time section.
    assert_eq!(state::settings_fallbacks(&project.settings), vec!["settings.time".to_owned()]);
    let resolved = state::resolve_settings(&project.settings);
    assert_eq!(resolved.time, farm_sim::schema::TimeConfig::default());
    assert_eq!(resolved.calendar, project.settings.calendar);
    // The engine guards content that skipped resolution, too.
    let mut content = state::create_content_from_project(&project);
    content.settings.time = project.settings.time.clone();
    let ctx = EngineContext::new(content);
    game_state.clock.time_minutes = units::minutes(1559);
    let money = game_state.player.money;
    engine::advance_tick(&ctx, &mut game_state, 61 * 20);
    assert_eq!(game_state.clock.day, 2, "one collapse at the default 26:00, then a normal day");
    assert!(game_state.player.money >= money - 50);
    // The collapse came one game minute in; an hour of the new day has passed since.
    assert_eq!(game_state.clock.time_minutes, units::time_of_day(7.0 * 60.0));
}

#[test]
fn a_day_end_the_clock_cannot_reach_falls_back_to_the_default_window() {
    let mut time = farm_sim::schema::TimeConfig::default();
    assert!(game_time::is_valid_time_config(&time));
    time.day_end_minute = game_time::MAX_DAY_END_MINUTE;
    assert!(game_time::is_valid_time_config(&time));
    time.day_end_minute = game_time::MAX_DAY_END_MINUTE + 1;
    assert!(!game_time::is_valid_time_config(&time), "the u32 clock saturates before 71:35");
    let time = farm_sim::schema::TimeConfig { day_start_minute: 600, day_end_minute: 659, ..Default::default() };
    assert!(!game_time::is_valid_time_config(&time), "a day shorter than an hour");
    let time = farm_sim::schema::TimeConfig { minutes_per_real_second: 0, ..Default::default() };
    assert!(!game_time::is_valid_time_config(&time));
    let fastest = units::from_authoring::<units::MinuteRate>(f64::from(game_time::MAX_MINUTES_PER_REAL_SECOND));
    let time = farm_sim::schema::TimeConfig { minutes_per_real_second: fastest, ..Default::default() };
    assert!(game_time::is_valid_time_config(&time));
    let time = farm_sim::schema::TimeConfig { minutes_per_real_second: fastest + 1, ..Default::default() };
    assert!(!game_time::is_valid_time_config(&time));
}

#[test]
fn sleeping_closes_an_open_minigame() {
    let (ctx, mut game_state) = engine_state("sleep-minigame");
    game_state.minigame = Some(MinigameSession { minigame_id: "mg".to_owned(), context: Default::default() });
    game_state.dialogue = Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-1".to_owned() });
    farm_sim::game_time::perform_sleep(&ctx, &mut game_state, farm_sim::game_time::SleepOptions::default());
    assert!(game_state.minigame.is_none());
    assert!(game_state.dialogue.is_none());
}

#[test]
fn time_of_day_ranges_match_after_midnight_and_wrap_past_it() {
    use farm_sim::events::time_of_day_matches;
    let at = |hour: f64| units::time_of_day(hour * 60.0);
    // The clock counts on to 26:00; "0:00–2:00" is how the game shows 24:00–26:00.
    assert!(time_of_day_matches(at(24.5), at(0.0), at(2.0)));
    assert!(time_of_day_matches(at(1.0), at(0.0), at(2.0)));
    assert!(!time_of_day_matches(at(12.0), at(0.0), at(2.0)));
    // Ranges written in clock minutes past midnight still match as before.
    assert!(time_of_day_matches(at(25.0), at(24.0), at(26.0)));
    assert!(time_of_day_matches(at(8.0), at(6.0), at(12.0)));
    assert!(!time_of_day_matches(at(13.0), at(6.0), at(12.0)));
    // Start after end: wraps past midnight.
    assert!(time_of_day_matches(at(23.0), at(22.0), at(2.0)));
    assert!(time_of_day_matches(at(25.5), at(22.0), at(2.0)));
    assert!(!time_of_day_matches(at(12.0), at(22.0), at(2.0)));
}

#[test]
fn npcs_take_a_step_per_minute_at_fast_clock_rates() {
    use farm_sim::schema::NpcScheduleEntry;
    let walker = |project: &mut GameProject| {
        project.npcs[0].schedule = Some(vec![NpcScheduleEntry {
            minute: 0,
            scene_id: "scene-test".to_owned(),
            x: 5,
            y: 1,
            ..NpcScheduleEntry::default()
        }]);
        project.player.x = units::tiles(1);
        project.player.y = units::tiles(5);
    };
    // One game minute per tick: three ticks, three steps.
    let (ctx, mut slow) = make_engine(walker, "walk");
    let start = slow.npcs["npc-test"].clone();
    engine::advance_tick(&ctx, &mut slow, 60);
    let slow_position = (slow.npcs["npc-test"].x, slow.npcs["npc-test"].y);
    // Three game minutes per tick (60 per real second): one tick, the same three steps.
    let (ctx, mut fast) = make_engine(
        |project| {
            walker(project);
            project.settings.time.minutes_per_real_second = units::from_authoring::<units::MinuteRate>(60.0);
        },
        "walk",
    );
    engine::advance_tick(&ctx, &mut fast, 1);
    assert_eq!(fast.clock.time_minutes, slow.clock.time_minutes);
    assert_ne!((start.x, start.y), slow_position);
    assert_eq!((fast.npcs["npc-test"].x, fast.npcs["npc-test"].y), slow_position);
}

// --- Keep changes and round trips (#36, #142) ---

#[test]
fn keep_changes_carries_flags_and_the_live_state_into_the_next_playtest() {
    use farm_sim::schema::{GridPoint, ShopSession};
    let project = make_project();
    let mut game_state = state::create_game_state(&project, Some("keep"));
    game_state.flags.insert("count".to_owned(), serde_json::json!(3));
    game_state.flags.insert("name".to_owned(), serde_json::json!("Ada"));
    game_state.flags.insert("met".to_owned(), serde_json::json!(true));
    game_state.clock.tick = 1234;
    game_state.shop = Some(ShopSession { shop_id: "shop-general".to_owned() });
    game_state.shop_purchases_today.insert("shop-general".to_owned(), [("seed-wheat".to_owned(), 4)].into());
    game_state.mine.current_floor = 2;
    let npc = game_state.npcs.get_mut("npc-test").unwrap();
    npc.path = Some(vec![GridPoint { x: 2, y: 1 }]);
    npc.patrol_index = Some(1);

    let kept = state::apply_state_to_project(&project, &game_state);
    assert_eq!(kept.event_flags, game_state.flags, "flag values go back as they are");
    let next = state::create_game_state(&kept, Some("keep"));
    assert_eq!(next.flags, game_state.flags);
    assert_eq!(next.clock.tick, 1234);
    assert_eq!(next.shop, game_state.shop);
    assert_eq!(next.shop_purchases_today, game_state.shop_purchases_today);
    assert_eq!(next.mine.current_floor, 2);
    assert_eq!(next.npcs["npc-test"], game_state.npcs["npc-test"]);

    // The JSON write-back keeps the project's key order (a removed key no longer swaps the last
    // key into its place).
    let mut project_json = serde_json::to_value(&project).unwrap();
    // An equipped tool in the middle of the player object, which the state no longer has.
    let player = project_json["player"].as_object().unwrap().clone();
    let mut with_tool = serde_json::Map::new();
    for (key, value) in player {
        let after_direction = key == "direction";
        with_tool.insert(key, value);
        if after_direction {
            with_tool.insert("equippedTool".to_owned(), serde_json::json!("tool-hoe"));
        }
    }
    project_json["player"] = serde_json::Value::Object(with_tool);
    let keys =
        |json: &serde_json::Value| -> Vec<String> { json["player"].as_object().unwrap().keys().cloned().collect() };
    let before: Vec<String> = keys(&project_json).into_iter().filter(|key| key != "equippedTool").collect();
    let synced = state::apply_state_to_project_json(&project_json, &game_state).unwrap();
    assert!(synced["player"].get("equippedTool").is_none());
    let after: Vec<String> = keys(&synced).into_iter().filter(|key| before.contains(key)).collect();
    assert_eq!(after, before);
    // A fresh game has nothing extra to keep.
    let fresh = state::create_game_state(&project, Some("keep"));
    assert!(state::apply_state_to_project(&project, &fresh).kept_state.is_none());
}

#[test]
fn the_npc_picked_on_a_shared_tile_does_not_depend_on_state_order() {
    let (ctx, mut game_state) = make_engine(
        |project| {
            let mut second = project.npcs[0].clone();
            second.id = "npc-second".to_owned();
            project.npcs.insert(0, second);
            for npc in &mut project.npcs {
                npc.x = units::tiles(3);
                npc.y = units::tiles(3);
            }
        },
        "shared-tile",
    );
    // A save sorts `npcs` by id; content order decides.
    game_state.npcs.sort_keys();
    game_state.npcs.reverse();
    let mut reordered = game_state.clone();
    reordered.npcs.reverse();
    engine::apply_command(&ctx, &mut game_state, &Command::Interact);
    engine::apply_command(&ctx, &mut reordered, &Command::Interact);
    assert_eq!(game_state.dialogue.as_ref().map(|d| d.npc_id.as_str()), Some("npc-second"));
    assert_eq!(reordered.dialogue, game_state.dialogue);
}

#[test]
fn whole_float_values_hash_and_save_like_integers() {
    let (_, mut game_state) = engine_state("floats");
    let mut float_state = game_state.clone();
    game_state.flags.insert("n".to_owned(), serde_json::json!(1));
    float_state.flags.insert("n".to_owned(), serde_json::json!(1.0));
    assert_eq!(farm_sim::hash_state(&game_state), farm_sim::hash_state(&float_state));
    // Integers keep every digit in stable JSON.
    let big = serde_json::json!({ "n": 9_007_199_254_740_993_u64, "m": -9_007_199_254_740_993_i64 });
    assert_eq!(farm_sim::stable_json::stringify_value(&big), r#"{"m":-9007199254740993,"n":9007199254740993}"#);
    assert_eq!(
        units::canonical_json(serde_json::json!([1.0, -0.0, 1.5, 1e300])),
        serde_json::json!([1, 0, 1.5, 1e300])
    );
}

#[test]
fn an_all_zero_rng_state_is_reseeded() {
    let project = GameProject { rng_state: Some(farm_sim::schema::RngState::default()), ..make_project() };
    let game_state = state::create_game_state(&project, Some("zero"));
    assert!(!game_state.rng.is_degenerate());
    assert_eq!(game_state.rng, farm_sim::rng::create_rng_state("zero"));
    // A degenerate state handed to the generator does not draw 0 forever either.
    let mut rng = farm_sim::Rng::new(farm_sim::schema::RngState::default());
    assert!((0..4).map(|_| rng.next_u32()).any(|draw| draw != 0));
}

#[test]
fn one_invalid_setting_no_longer_resets_every_setting() {
    use farm_sim::schema::CalendarFestival;
    let mut settings = make_project().settings;
    settings.locale = "fr".to_owned();
    settings.max_energy = units::points(250);
    settings.calendar.festivals = vec![
        CalendarFestival { id: "bad".to_owned(), name: "Bad".to_owned(), season_id: "spring".to_owned(), day: 0 },
        CalendarFestival { id: "good".to_owned(), name: "Good".to_owned(), season_id: "spring".to_owned(), day: 4 },
    ];
    assert!(!state::is_valid_settings(&settings));
    assert_eq!(state::settings_fallbacks(&settings), vec!["settings.calendar.festivals.0".to_owned()]);
    let resolved = state::resolve_settings(&settings);
    assert_eq!(resolved.locale, "fr");
    assert_eq!(resolved.max_energy, units::points(250));
    assert_eq!(resolved.calendar.festivals.iter().map(|f| f.id.as_str()).collect::<Vec<_>>(), vec!["good"]);
    assert_eq!(resolved.calendar.seasons, settings.calendar.seasons);

    settings.max_energy = 0;
    let resolved = state::resolve_settings(&settings);
    assert_eq!(resolved.max_energy, farm_sim::schema::ProjectSettings::default().max_energy);
    assert_eq!(resolved.locale, "fr");
}
