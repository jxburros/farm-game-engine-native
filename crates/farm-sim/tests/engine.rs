//! Port of `tests/FarmEngine.Core.Tests/Core/EngineTests.cs` (engine.test.ts): the engine reducer
//! end to end. Most cases drive the whole simulation (movement, tools, crops, dialogue, sleep),
//! so they are `#[ignore]`d until those module ports merge; the cases at the end exercise the
//! reducer itself (`apply_command` dispatch, the `onCommand` hook, the per-tick clock).

mod common;

use common::{has_message, make_engine, make_project};
use farm_sim::schema::{DialogueState, GameState, MinigameSession, Quest, QuestObjective, QuestRewards};
use farm_sim::{engine, state, Command, Effect, EngineContext, HookEvent};

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
    state.player.x = 3.0;
    state.player.y = 3.0;
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
    assert_eq!(state.player.x, 2.5);
    assert_eq!(state.player.y, 4.5);
    assert_eq!(state.player.direction, "left");
    assert!(effects.contains(&Effect::PlayerMoved { x: 2.0, y: 4.0 }));
}

#[test]
fn blocks_movement_into_walls_but_still_turns() {
    let (ctx, mut state) = engine_state("engine-test");
    engine::apply_command(&ctx, &mut state, &r#move("right"));
    assert_eq!(state.player.x, 3.5);
    assert_eq!(state.player.direction, "right");
}

#[test]
fn blocks_movement_out_of_bounds() {
    let (ctx, mut state) = engine_state("engine-test");
    for _ in 0..10 {
        engine::apply_command(&ctx, &mut state, &r#move("down"));
    }
    assert_eq!(state.player.y, 5.5);
}

#[test]
fn blocks_movement_onto_npcs() {
    let (ctx, mut state) = engine_state("engine-test");
    state.player.x = 1.0;
    state.player.y = 2.0;
    engine::apply_command(&ctx, &mut state, &r#move("up"));
    assert_eq!(state.player.y, 2.0); // npc at (1,1)
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
    assert_eq!(hoe.item.durability, Some(99.0));
    assert_eq!(state.player.energy, 96.0); // hoe costs 4
}

#[test]
fn waters_soil_and_marks_crops_watered() {
    let (ctx, mut state) = engine_state("engine-test");
    facing_soil(&mut state);
    engine::apply_command(&ctx, &mut state, &Command::UseTool { tool: "watering-can".to_owned() });
    let tile = &state.world.scenes[0].tiles[2][3];
    assert_eq!(tile.soil_moisture, 100.0);
    assert_eq!(tile.soil_state.as_deref(), Some("watered"));
    assert_eq!(state.player.energy, 98.0); // watering can costs 2
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
    assert_eq!(crop.days_grown, Some(0.0));
    assert_eq!(crop.planted_on_day, Some(1.0));
    let seeds = state.player.inventory.iter().find(|s| s.item.id == "seed-wheat").expect("seeds");
    assert_eq!(seeds.quantity, 4.0);
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

    assert_eq!(state.world.scenes[0].tiles[2][3].crop.as_ref().and_then(|c| c.days_grown), Some(3.0));
    assert_eq!(state.clock.day, 4.0);

    let money_before_harvest = state.player.money;
    let effects = engine::apply_command(&ctx, &mut state, &Command::Interact);
    assert_eq!(crop_type(&state), None);
    let wheat = state.player.inventory.iter().find(|s| s.item.id == "crop-wheat").expect("harvested wheat");
    assert!(wheat.quantity >= 1.0);
    // No auto-sell: money only moves via the quest reward (100).
    assert_eq!(state.player.money, money_before_harvest + 100.0);
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
    assert_eq!(crop.days_grown, Some(0.0));
    assert_eq!(crop.days_without_water, 1.0);
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
    state.player.x = 1.0;
    state.player.y = 2.0;
    state.player.direction = "up".to_owned();
    engine::apply_command(&ctx, &mut state, &Command::Interact);
    assert_eq!(state.dialogue, Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-1".to_owned() }));
}

#[test]
fn applies_option_rewards_and_follows_next_dialogue_id() {
    let (ctx, mut state) = engine_state("engine-test");
    state.dialogue = Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-1".to_owned() });
    engine::apply_command(&ctx, &mut state, &Command::ChooseDialogueOption { index: 1.0 });
    assert_eq!(state.player.money, 125.0);
    assert_eq!(state.dialogue, Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-2".to_owned() }));

    engine::apply_command(&ctx, &mut state, &Command::ChooseDialogueOption { index: 0.0 });
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
            progress: 0.0,
            ..QuestObjective::default()
        }],
        rewards: QuestRewards { money: Some(10.0), ..QuestRewards::default() },
        ..Quest::default()
    });
    project.player.active_quests.push("quest-talk".to_owned());
    let ctx = EngineContext::new(state::create_content_from_project(&project));
    let mut game_state = state::create_game_state(&project, Some("talk"));
    game_state.player.x = 1.0;
    game_state.player.y = 2.0;
    game_state.player.direction = "up".to_owned();
    engine::apply_command(&ctx, &mut game_state, &Command::Interact);
    assert!(game_state.player.completed_quests.contains(&"quest-talk".to_owned()));
    assert_eq!(game_state.player.money, 110.0);
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
    assert_eq!(synced.player.x, 3.0);
    assert_eq!(synced.player.y, 3.0);
    assert_eq!(synced.scenes[0].tiles[2][3].crop.as_ref().map(|c| c.r#type.as_str()), Some("wheat"));
    assert_eq!(synced.rng_state.as_ref(), Some(&game_state.rng));
    assert_eq!(synced.current_time_minutes, game_state.clock.time_minutes);
    // editor-only fields are preserved
    assert_eq!(synced.mode, project.mode);
    assert_eq!(synced.custom_assets, project.custom_assets);
}

#[test]
fn advance_tick_advances_the_game_clock_and_nothing_else_before_day_end() {
    let (ctx, mut game_state) = engine_state("clock");
    let before = game_state.clone();
    let effects = engine::advance_tick(&ctx, &mut game_state, 20.0); // 20 ticks = 1 real second = 1 game minute
    assert_eq!(game_state.clock.tick, 20.0);
    assert!((game_state.clock.time_minutes - (6.0 * 60.0 + 1.0)).abs() < 0.005);
    assert_eq!(game_state.player, before.player);
    assert_eq!(game_state.world, before.world);
    assert!(effects.is_empty());
}

#[test]
fn the_clock_passing_day_end_forces_a_collapse_into_the_next_day() {
    let (ctx, mut game_state) = engine_state("collapse");
    // 2am is 20h of game time from 6:00 = 1200 game minutes = 1200 real seconds = 24000 ticks
    let effects = engine::advance_tick(&ctx, &mut game_state, 24000.0);
    assert_eq!(game_state.clock.day, 2.0);
    assert_eq!(game_state.clock.time_minutes, 6.0 * 60.0);
    assert!(has_message(&effects, |t| t.contains("collapsed")));
    // collapse penalty: half energy, money fee
    assert_eq!(game_state.player.energy, 50.0);
    assert_eq!(game_state.player.money, 50.0);
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
    assert!(engine::advance_tick(&ctx, &mut game_state, 0.0).is_empty());
    assert!(engine::advance_tick(&ctx, &mut game_state, -3.0).is_empty());
    assert_eq!(game_state, before);
}

#[test]
fn ticks_step_the_clock_on_the_minute_quantum_while_a_minigame_freezes_the_body() {
    let (ctx, mut game_state) = engine_state("minigame-clock");
    game_state.minigame = Some(MinigameSession { minigame_id: "mg".to_owned(), context: Default::default() });
    game_state.player.move_intent.dx = 1.0;
    let before = game_state.clone();
    // 10 ticks = half a game minute: no minute boundary, so no NPC/machine/event step either.
    let effects = engine::advance_tick(&ctx, &mut game_state, 10.0);
    assert!(effects.is_empty());
    assert_eq!(game_state.clock.tick, 10.0);
    assert_eq!(game_state.clock.time_minutes, 360.5);
    assert_eq!(game_state.player, before.player);
    assert_eq!(game_state.world, before.world);
    // advanceTick(N) is N × advanceTick(1): a fractional count rounds up like the C# loop.
    let mut stepped = before.clone();
    for _ in 0..3 {
        engine::advance_tick(&ctx, &mut stepped, 1.0);
    }
    let mut batched = before.clone();
    engine::advance_tick(&ctx, &mut batched, 2.5);
    assert_eq!(batched, stepped);
}
