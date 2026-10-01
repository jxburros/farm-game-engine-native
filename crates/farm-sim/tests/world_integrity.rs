//! The world stays playable whatever content, plugins, saves or command logs throw at it: action
//! chains are bounded, warps and doors land on walkable tiles, malformed grids never panic,
//! fast players don't tunnel, placed machines can be taken back and never block doors, commands
//! apply only where a player could give them, and pack scenes are part of the world.

mod common;

use common::{empty_scene, has_message, make_engine, make_project, set_tile_layer};
use farm_sim::events::{self, MAX_ACTION_RUNS};
use farm_sim::schema::{
    ActionDef, DialogueState, EventOutcome, GameProject, GameState, InventorySlot, Item, MachineTypeDefinition, Npc,
    NpcScheduleEntry, PackInstallation, PluginMutation, SceneTransition, TileMachine,
};
use farm_sim::world::world_movement::{self, TilePoint};
use farm_sim::{engine, rng::Rng, state, units, Command, Effect, EngineContext, HookBus};
use serde_json::json;

fn perform(action_id: &str) -> Command {
    Command::PerformAction { action_id: action_id.to_owned() }
}

fn outcome(kind: &str) -> EventOutcome {
    EventOutcome { r#type: kind.to_owned(), ..EventOutcome::default() }
}

fn warp(scene_id: &str, x: i32, y: i32) -> EventOutcome {
    EventOutcome { scene_id: Some(scene_id.to_owned()), x: Some(x), y: Some(y), ..outcome("warpPlayer") }
}

fn action(id: &str, outcomes: Vec<EventOutcome>) -> ActionDef {
    ActionDef { id: id.to_owned(), name: id.to_owned(), outcomes, ..ActionDef::default() }
}

fn tile_of(state: &GameState) -> TilePoint {
    world_movement::player_tile(state)
}

fn walk(ctx: &EngineContext, state: &mut GameState, dx: i32, dy: i32, ticks: u64) -> Vec<Effect> {
    engine::apply_command(ctx, state, &Command::SetMoveIntent { dx, dy });
    engine::advance_tick(ctx, state, ticks)
}

// --- #26: action chains are bounded in breadth, not only depth ---

#[test]
fn a_wide_action_fan_out_stops_at_the_run_budget_and_says_so() {
    let (ctx, mut state) = make_engine(
        |p| {
            let mut outcomes = vec![EventOutcome { amount: Some(units::amount(1.0)), ..outcome("giveMoney") }];
            outcomes.extend(
                (0..12).map(|_| EventOutcome { action_id: Some("boom".to_owned()), ..outcome("performAction") }),
            );
            p.actions = vec![action("boom", outcomes)];
        },
        "fan-out",
    );
    let money = state.player.money;
    let effects = engine::apply_command(&ctx, &mut state, &perform("boom"));
    // Without the budget this ran 22,621 times.
    assert_eq!(state.player.money, money + i64::from(MAX_ACTION_RUNS));
    let warnings = effects
        .iter()
        .filter(|effect| matches!(effect, Effect::Message { level, text } if level == "error" && text.starts_with("Action chain limit reached")))
        .count();
    assert_eq!(warnings, 1, "one warning per command");

    // Each top-level call has its own budget.
    engine::apply_command(&ctx, &mut state, &perform("boom"));
    assert_eq!(state.player.money, money + 2 * i64::from(MAX_ACTION_RUNS));
}

#[test]
fn a_deep_chain_says_when_it_hits_the_depth_cap() {
    let (ctx, mut state) = make_engine(
        |p| {
            p.actions = vec![action(
                "loop",
                vec![EventOutcome { action_id: Some("loop".to_owned()), ..outcome("performAction") }],
            )]
        },
        "deep",
    );
    let result = events::perform_action(&ctx, &mut state, "loop");
    assert!(result.ran);
    assert!(has_message(&result.effects, |text| text.starts_with("Action chain limit reached at 'loop'")));
}

// --- #27: warps and doors never strand the player ---

fn second_scene(project: &mut GameProject) {
    let mut cave = empty_scene("scene-cave", "Cave", 4, 4);
    cave.tiles[2][2] = set_tile_layer(&cave.tiles[2][2], "wall");
    project.scenes.push(cave);
}

#[test]
fn a_warp_outside_the_scene_lands_on_the_nearest_tile_inside_and_the_player_can_walk() {
    let (ctx, mut state) = make_engine(|p| p.actions = vec![action("far", vec![warp("scene-test", 50, 50)])], "warp");
    let effects = engine::apply_command(&ctx, &mut state, &perform("far"));
    assert_eq!(tile_of(&state), TilePoint { x: 5, y: 5 });
    assert!(effects.contains(&Effect::SceneChanged { scene_id: "scene-test".to_owned(), x: 5, y: 5 }));
    assert!(has_message(&effects, |text| text.contains("can't be stood on")));
    walk(&ctx, &mut state, -1, 0, 20);
    assert!(tile_of(&state).x < 5, "the player is not stuck");
}

#[test]
fn a_warp_into_a_wall_lands_beside_it() {
    let (ctx, mut state) = make_engine(
        |p| {
            second_scene(p);
            p.actions = vec![action("into-wall", vec![warp("scene-cave", 2, 2)])];
        },
        "warp-wall",
    );
    engine::apply_command(&ctx, &mut state, &perform("into-wall"));
    assert_eq!(state.player.scene_id, "scene-cave");
    assert_eq!(tile_of(&state), TilePoint { x: 2, y: 1 });
}

#[test]
fn a_plugin_warp_far_outside_the_scene_lands_inside() {
    let (ctx, mut state) = make_engine(|_| {}, "plugin-warp");
    let mutation = PluginMutation::WarpPlayer { scene_id: "scene-test".to_owned(), x: 1_000_000, y: -7 };
    engine::apply_command(&ctx, &mut state, &Command::PluginMutation { plugin_id: "p".to_owned(), mutation });
    assert_eq!(tile_of(&state), TilePoint { x: 5, y: 0 });
}

#[test]
fn a_door_into_a_wall_lands_beside_it() {
    let (ctx, mut state) = make_engine(
        |p| {
            second_scene(p);
            p.scenes[0].transitions.push(SceneTransition {
                from_x: 3,
                from_y: 3,
                to_scene_id: "scene-cave".to_owned(),
                to_x: 2,
                to_y: 2,
                ..SceneTransition::default()
            });
        },
        "door",
    );
    let effects = engine::apply_command(&ctx, &mut state, &Command::Move { dir: "up".to_owned() });
    assert_eq!(state.player.scene_id, "scene-cave");
    assert_eq!(tile_of(&state), TilePoint { x: 2, y: 1 });
    assert!(effects.contains(&Effect::SceneChanged { scene_id: "scene-cave".to_owned(), x: 2, y: 1 }));
}

// --- #139: a pickup the inventory has no room for doesn't swallow the tile's door ---

#[test]
fn walking_onto_a_door_with_a_dropped_item_and_a_full_inventory_still_goes_through() {
    let (ctx, mut state) = make_engine(
        |p| {
            second_scene(p);
            p.scenes[0].transitions.push(SceneTransition {
                from_x: 3,
                from_y: 3,
                to_scene_id: "scene-cave".to_owned(),
                to_x: 1,
                to_y: 1,
                ..SceneTransition::default()
            });
            let stone = p.items.iter().find(|item| item.id == "seed-wheat").cloned().expect("an item");
            p.scenes[0].tiles[3][3].item = Some(Item { id: "pebble".to_owned(), stackable: false, ..stone });
            p.player.max_inventory_size = 3; // the three starting slots
        },
        "full",
    );
    let effects = walk(&ctx, &mut state, 0, -1, 20);
    assert_eq!(state.player.scene_id, "scene-cave", "the door fired");
    assert!(has_message(&effects, |text| text == "Inventory is full!"));
    let farm = world_movement::find_scene(&state, "scene-test").expect("farm");
    assert!(farm.tile(3, 3).and_then(|tile| tile.item.as_ref()).is_some(), "the item stays on the ground");
}

#[test]
fn enter_events_still_fire_when_a_pickup_is_refused() {
    let (ctx, mut state) = make_engine(
        |p| {
            let stone = p.items.iter().find(|item| item.id == "seed-wheat").cloned().expect("an item");
            p.scenes[0].tiles[3][3].item = Some(Item { id: "pebble".to_owned(), stackable: false, ..stone });
            p.player.max_inventory_size = 3;
            p.events = vec![farm_sim::schema::GameEvent {
                id: "ev-step".to_owned(),
                name: "Step".to_owned(),
                trigger: "enter".to_owned(),
                active: true,
                conditions: vec![farm_sim::schema::EventCondition::EnterTile { x: 3, y: 3, x2: None, y2: None }],
                outcomes: vec![EventOutcome { flag_name: Some("stepped".to_owned()), ..outcome("setFlag") }],
                ..farm_sim::schema::GameEvent::default()
            }];
        },
        "full-event",
    );
    walk(&ctx, &mut state, 0, -1, 6);
    assert_eq!(tile_of(&state), TilePoint { x: 3, y: 3 });
    assert_eq!(state.flags.get("stepped"), Some(&json!(true)));
}

// --- #28: malformed grids are fixed at load and never panic during play ---

#[test]
fn ragged_and_mis_sized_grids_are_normalized_when_a_game_starts() {
    let mut project = make_project();
    project.scenes[0].tiles[3].truncate(3);
    project.scenes[0].tiles.truncate(5);
    let game = state::create_game_state(&project, Some("ragged"));
    let scene = &game.world.scenes[0];
    assert_eq!(scene.tiles.len(), 6);
    assert!(scene.tiles.iter().all(|row| row.len() == 6));
    assert_eq!(scene.tiles[5][4].x, 4);
    assert_eq!(scene.tiles[5][4].y, 5);

    let mut project = make_project();
    project.scenes[0].width = 100_000;
    let game = state::create_game_state(&project, Some("wide"));
    assert_eq!(game.world.scenes[0].width, farm_sim::schema::MAX_SCENE_SIZE);
    assert!(game.world.scenes[0].tiles.iter().all(|row| row.len() == 256));
}

#[test]
fn a_grid_that_skipped_normalization_never_panics() {
    let (ctx, mut state) = make_engine(|_| {}, "raw");
    // A state replaced as-is (a host's setState): a short row and a scene wider than its grid.
    state.world.scenes[0].tiles[3].truncate(3);
    state.world.scenes[0].width = 100_000;
    walk(&ctx, &mut state, 0, -1, 20);
    engine::apply_command(&ctx, &mut state, &Command::Interact);
    for tool in ["hoe", "watering-can", "scythe"] {
        engine::apply_command(&ctx, &mut state, &Command::UseTool { tool: tool.to_owned() });
    }
    state.player.x = units::tile_center(5);
    walk(&ctx, &mut state, 1, 0, 40);
    assert_eq!(tile_of(&state).x, 5, "no tile beyond the grid to walk onto");
}

// --- #38: no tunneling at any speed ---

#[test]
fn a_very_fast_player_stops_at_a_one_tile_wall() {
    let (mut ctx, mut state) = make_engine(
        |p| {
            let mut corridor = empty_scene("scene-test", "Corridor", 12, 3);
            for y in 0..3 {
                corridor.tiles[y][5] = set_tile_layer(&corridor.tiles[y][5], "wall");
            }
            p.scenes = vec![corridor];
            p.npcs.clear();
            p.player.x = units::tiles(2);
            p.player.y = units::tiles(1);
        },
        "fast",
    );
    // 60 tiles a second: three tiles per tick.
    ctx.content.settings.movement.player_speed = units::TILE * 3;
    walk(&ctx, &mut state, 1, 0, 2);
    assert_eq!(tile_of(&state), TilePoint { x: 4, y: 1 });
}

// --- #94: extreme numbers saturate instead of overflowing ---

#[test]
fn extreme_amounts_weights_and_positions_do_not_overflow() {
    assert_eq!(units::div_round(i64::MAX, 1000), i64::MAX / 1000 + 1);
    assert_eq!(units::div_round(i64::MIN, 1000), i64::MIN / 1000 - 1);

    let (ctx, mut state) = make_engine(|_| {}, "extreme");
    let mutation = PluginMutation::GiveMoney { amount: i64::MAX };
    engine::apply_command(&ctx, &mut state, &Command::PluginMutation { plugin_id: "p".to_owned(), mutation });
    assert!(state.player.money > 0);

    let mut rng = Rng::new(farm_sim::rng::create_rng_state("weights"));
    for _ in 0..64 {
        assert!(rng.weighted(&[u32::MAX, u32::MAX, u32::MAX, 1]).is_some_and(|index| index < 4));
    }

    state.player.x = i32::MAX - 3;
    state.player.y = i32::MAX - 3;
    walk(&ctx, &mut state, 1, 1, 3);
}

// --- #29: machines can be picked up and never block doors or the mine ---

fn machine_project(p: &mut GameProject) {
    p.machine_types = vec![MachineTypeDefinition {
        id: "machine-keg".to_owned(),
        name: "Keg".to_owned(),
        item_id: Some("machine-keg-item".to_owned()),
        ..MachineTypeDefinition::default()
    }];
    let base = p.items.iter().find(|item| item.id == "seed-wheat").cloned().expect("an item");
    let keg = Item { id: "machine-keg-item".to_owned(), name: "Keg".to_owned(), r#type: "material".to_owned(), ..base };
    p.items.push(keg.clone());
    p.player.inventory.push(InventorySlot { item: keg, quantity: 2 });
}

fn place(ctx: &EngineContext, state: &mut GameState) -> Vec<Effect> {
    engine::apply_command(ctx, state, &Command::PlaceMachine { machine_type_id: "machine-keg".to_owned() })
}

fn keg_count(state: &GameState) -> u32 {
    state.player.inventory.iter().filter(|slot| slot.item.id == "machine-keg-item").map(|slot| slot.quantity).sum()
}

#[test]
fn an_idle_machine_can_be_picked_back_up() {
    let (ctx, mut state) = make_engine(machine_project, "pick-up");
    // Player at (3,4) facing up: (3,3).
    place(&ctx, &mut state);
    assert_eq!(keg_count(&state), 1);
    let effects = engine::apply_command(&ctx, &mut state, &Command::PickUpMachine);
    assert!(has_message(&effects, |text| text == "Picked up Keg"));
    assert_eq!(keg_count(&state), 2);
    assert!(state.world.scenes[0].tile(3, 3).expect("tile").machine.is_none());

    // Nothing there now.
    let effects = engine::apply_command(&ctx, &mut state, &Command::PickUpMachine);
    assert!(has_message(&effects, |text| text == "No machine there."));
}

#[test]
fn a_working_machine_or_a_full_inventory_keeps_it_in_place() {
    let (ctx, mut state) = make_engine(machine_project, "busy");
    place(&ctx, &mut state);
    let tile = state.world.scenes[0].tile_mut(3, 3).expect("tile");
    tile.machine.as_mut().expect("machine").processing =
        Some(farm_sim::schema::MachineProcessing { recipe_id: "r".to_owned(), completes_at_minute: i64::MAX });
    let before = state.clone();
    engine::apply_command(&ctx, &mut state, &Command::PickUpMachine);
    assert_eq!(state, before);

    let (ctx, mut state) = make_engine(machine_project, "full");
    state.player.inventory.retain(|slot| slot.item.id != "machine-keg-item");
    state.world.scenes[0].tile_mut(3, 3).expect("tile").machine =
        Some(TileMachine { type_id: "machine-keg".to_owned(), ..TileMachine::default() });
    state.player.max_inventory_size = u32::try_from(state.player.inventory.len()).expect("small");
    let before = state.clone();
    let effects = engine::apply_command(&ctx, &mut state, &Command::PickUpMachine);
    assert_eq!(state, before);
    assert!(has_message(&effects, |text| text == "Inventory is full!"));
}

#[test]
fn machines_cannot_go_on_doors_arrivals_the_mine_entrance_or_an_npc() {
    let door = |from: (i32, i32), to: (i32, i32)| SceneTransition {
        from_x: from.0,
        from_y: from.1,
        to_scene_id: "scene-test".to_owned(),
        to_x: to.0,
        to_y: to.1,
        ..SceneTransition::default()
    };
    // A door leaving from the faced tile (3,3).
    let (ctx, mut state) = make_engine(
        |p| {
            machine_project(p);
            p.scenes[0].transitions.push(door((3, 3), (0, 0)));
        },
        "door",
    );
    assert!(has_message(&place(&ctx, &mut state), |text| text.contains("stay clear")));
    assert_eq!(keg_count(&state), 2);

    // A door arriving on it.
    let (ctx, mut state) = make_engine(
        |p| {
            machine_project(p);
            p.scenes[0].transitions.push(door((0, 5), (3, 3)));
        },
        "arrival",
    );
    assert!(has_message(&place(&ctx, &mut state), |text| text.contains("stay clear")));

    // The mine entrance.
    let (ctx, mut state) = make_engine(
        |p| {
            machine_project(p);
            p.mine.enabled = true;
            p.mine.entrance_scene_id = Some("scene-test".to_owned());
            p.mine.entrance_x = Some(3);
            p.mine.entrance_y = Some(3);
        },
        "mine",
    );
    assert!(has_message(&place(&ctx, &mut state), |text| text.contains("stay clear")));

    // An NPC standing there.
    let (ctx, mut state) = make_engine(machine_project, "npc");
    state.npcs.get_mut("npc-test").expect("npc").x = units::tiles(3);
    state.npcs.get_mut("npc-test").expect("npc").y = units::tiles(3);
    assert!(has_message(&place(&ctx, &mut state), |text| text.contains("No room")));

    // Anywhere else is fine.
    let (ctx, mut state) = make_engine(machine_project, "free");
    assert!(has_message(&place(&ctx, &mut state), |text| text == "Placed Keg"));
}

#[test]
fn scheduled_npcs_walk_around_placed_machines() {
    let (ctx, mut state) = make_engine(
        |p| {
            machine_project(p);
            let npc: &mut Npc = &mut p.npcs[0];
            npc.can_move = true;
            npc.schedule = Some(vec![NpcScheduleEntry {
                minute: 0,
                scene_id: "scene-test".to_owned(),
                x: 1,
                y: 4,
                ..NpcScheduleEntry::default()
            }]);
        },
        "npc-path",
    );
    // A keg in the NPC's straight line (1,1) → (1,4).
    state.world.scenes[0].tile_mut(1, 2).expect("tile").machine =
        Some(TileMachine { type_id: "machine-keg".to_owned(), ..TileMachine::default() });
    for _ in 0..200 {
        engine::advance_tick(&ctx, &mut state, 1);
        let npc = &state.npcs["npc-test"];
        assert!(!(npc.x == units::tiles(1) && npc.y == units::tiles(2)), "the NPC stood on the machine");
        assert!(npc.path.iter().flatten().all(|step| !(step.x == 1 && step.y == 2)));
    }
    assert_eq!(state.npcs["npc-test"].y, units::tiles(4), "it got there around the keg");
}

// --- #96: commands apply only where a player could give them ---

#[test]
fn shops_open_only_facing_a_merchant() {
    let (ctx, mut state) = make_engine(
        |p| {
            p.shops = vec![farm_sim::schema::ShopDefinition {
                id: "shop-1".to_owned(),
                name: "Shop".to_owned(),
                ..Default::default()
            }];
            p.npcs[0].dialogue[0].options[0].open_shop_id = Some("shop-1".to_owned());
        },
        "shop",
    );
    let open = Command::OpenShop { shop_id: "shop-1".to_owned() };
    let effects = engine::apply_command(&ctx, &mut state, &open);
    assert!(state.shop.is_none());
    assert!(has_message(&effects, |text| text.contains("shopkeeper")));

    // Facing the NPC at (1,1) from (1,2).
    state.player.x = units::tile_center(1);
    state.player.y = units::tile_center(2);
    state.player.direction = "up".to_owned();
    engine::apply_command(&ctx, &mut state, &open);
    assert_eq!(state.shop.as_ref().map(|shop| shop.shop_id.as_str()), Some("shop-1"));

    // Scripts open it from anywhere.
    let (ctx, mut state) = make_engine(|p| p.shops = vec![Default::default()], "scripted");
    let ctx = ctx.with_rules(farm_sim::CommandRules::Scripted);
    engine::apply_command(&ctx, &mut state, &Command::OpenShop { shop_id: String::new() });
    assert!(state.shop.is_some());
}

#[test]
fn the_start_minigame_command_is_refused_so_rewards_are_not_on_demand() {
    let (ctx, mut state) = make_engine(
        |p| {
            p.minigames = vec![farm_sim::schema::MinigameDef {
                id: "mg".to_owned(),
                name: "Mg".to_owned(),
                kind: "timing-bar".to_owned(),
                ..Default::default()
            }]
        },
        "mg",
    );
    let before = state.clone();
    engine::apply_command(&ctx, &mut state, &Command::StartMinigame { minigame_id: "mg".to_owned() });
    engine::apply_command(&ctx, &mut state, &Command::ResolveMinigame { score: units::PROBABILITY_ONE });
    assert_eq!(state, before);
}

#[test]
fn an_open_dialogue_blocks_everything_but_its_own_commands() {
    let ctx = EngineContext::with_hooks(state::create_content_from_project(&make_project()), HookBus::new());
    let mut state = state::create_game_state(&make_project(), Some("modal"));
    state.dialogue = Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-1".to_owned() });
    let before = state.clone();
    for command in [
        Command::Interact,
        Command::UseTool { tool: "hoe".to_owned() },
        Command::Sleep,
        Command::Move { dir: "left".to_owned() },
        Command::Craft { recipe_id: "r".to_owned() },
        Command::PerformAction { action_id: "a".to_owned() },
    ] {
        assert!(engine::apply_command(&ctx, &mut state, &command).is_empty(), "{command:?}");
        assert_eq!(state, before, "{command:?}");
    }
    assert!(ctx.drain_hook_events().is_empty(), "refused commands reach no plugin");
    engine::apply_command(&ctx, &mut state, &Command::CloseDialogue);
    assert!(state.dialogue.is_none());
}

// --- #97: scenes from enabled content packs are part of the world ---

#[test]
fn a_pack_scene_is_reachable_by_warp_and_its_grid_is_fixed() {
    let pack: farm_sim::schema::ContentPack = serde_json::from_value(json!({
        "manifest": { "id": "mod-cave", "name": "Cave", "version": "1" },
        "content": {
            "scenes": [{ "id": "cave", "name": "Cave", "width": 4, "height": 3, "tiles": [] }],
            "actions": [{
                "id": "enter", "name": "Enter",
                // Scene references are not namespaced (as in the reference engine): written in full.
                "outcomes": [{ "type": "warpPlayer", "sceneId": "mod-cave:cave", "x": 1, "y": 1 }]
            }]
        },
        "plugins": []
    }))
    .expect("pack parses");
    let (ctx, mut state) =
        make_engine(|p| p.content_packs = vec![PackInstallation { pack, enabled: true }], "pack-scene");
    assert!(!state.world.scenes.iter().any(|scene| scene.id == "mod-cave:cave"), "not in the world until visited");

    let effects = engine::apply_command(&ctx, &mut state, &perform("mod-cave:enter"));
    assert_eq!(state.player.scene_id, "mod-cave:cave");
    assert!(effects.contains(&Effect::SceneChanged { scene_id: "mod-cave:cave".to_owned(), x: 1, y: 1 }));
    let cave = world_movement::find_scene(&state, "mod-cave:cave").expect("the pack scene joined the world");
    assert_eq!(cave.tiles.len(), 3);
    assert!(cave.tiles.iter().all(|row| row.len() == 4));
    // And the player can walk there.
    walk(&ctx, &mut state, 1, 0, 10);
    assert!(tile_of(&state).x > 1);
}
