//! Port of the movement, pathfinding and NPC cases of
//! the retired C# `FreeMovementTests.cs`, `M3SystemsTests.cs` and the
//! movement section of `EngineTests.cs`, plus the tile helpers they build on.
//!
//! Fixture geometry (`make_project`, the C# `EngineTests.MakeProject`): 6×6 scene, player starts
//! centered on tile (3,4) → position (3.5, 4.5); soil at (3,2); wall tile at (4,4); NPC at (1,1).
//!
//! Tests that go through `engine::apply_command` / `engine::advance_tick`, or whose tile change
//! settles through `GameEvents`, are `#[ignore]`d until those modules are ported; the same cases
//! are covered here directly wherever the occupied tile does not change.

use farm_sim::commands::Command;
use farm_sim::effects::Effect;
use farm_sim::engine::{advance_tick, apply_command};
use farm_sim::npcs::npc_movement::advance_npcs;
use farm_sim::replay;
use farm_sim::schema::{
    default_project_settings, Dialogue, DialogueOption, DialogueState, GameProject, GridPoint, InventorySlot, Item,
    MineConfig, MoveIntent, NodeTypeDefinition, Npc, NpcScheduleEntry, Player, Quest, QuestObjective, QuestRewards,
    SceneTransition, TileMachine, TileNode, VisualRef, WeatherConfig, WeatherTableEntry, WeatherTypeDefinition,
};
use farm_sim::units;
use farm_sim::world::pathfinding::{find_path, is_walkable, PathPoint, Walkability};
use farm_sim::world::tiles;
use farm_sim::world::world_movement::{
    self, can_move_to, direction_from_intent, facing_target, handle_move, integrate_movement, player_tile,
    PLAYER_HALF_WIDTH,
};
use farm_sim::{content_builtin, hash_state, state, EngineContext, GameState};
use indexmap::{IndexMap, IndexSet};

/// A position in tiles, for comparing with authoring numbers.
fn t(position: i32) -> f64 {
    units::position_to_tiles(position)
}

fn slot(items: &[Item], id: &str, quantity: u32) -> InventorySlot {
    InventorySlot { item: items.iter().find(|item| item.id == id).expect("built-in item").clone(), quantity }
}

/// The C# `EngineTests.MakeProject`.
fn make_project() -> GameProject {
    let mut scene = tiles::create_empty_scene("scene-test", "Test Farm", 6, 6);
    scene.tiles[2][3] = tiles::set_tile_layer(&scene.tiles[2][3], "soil", None);
    scene.tiles[4][4] = tiles::set_tile_layer(&scene.tiles[4][4], "wall", None);

    let items = content_builtin::create_default_items();
    let npc = Npc {
        id: "npc-test".to_owned(),
        name: "Testy".to_owned(),
        x: units::tiles(1),
        y: units::tiles(1),
        scene_id: "scene-test".to_owned(),
        dialogue: vec![
            Dialogue {
                id: "dlg-1".to_owned(),
                npc_id: "npc-test".to_owned(),
                text: "Hello!".to_owned(),
                options: vec![
                    DialogueOption { text: "Bye".to_owned(), ..DialogueOption::default() },
                    DialogueOption {
                        text: "Gift me".to_owned(),
                        give_money: Some(25),
                        next_dialogue_id: Some("dlg-2".to_owned()),
                        ..DialogueOption::default()
                    },
                ],
                ..Dialogue::default()
            },
            Dialogue {
                id: "dlg-2".to_owned(),
                npc_id: "npc-test".to_owned(),
                text: "More?".to_owned(),
                options: vec![DialogueOption { text: "No".to_owned(), ..DialogueOption::default() }],
                ..Dialogue::default()
            },
        ],
        can_move: false,
        appearance: "farmer".to_owned(),
        ..Npc::default()
    };

    let quest = Quest {
        id: "quest-wheat".to_owned(),
        name: "Wheat!".to_owned(),
        description: "Harvest 1 wheat".to_owned(),
        status: "active".to_owned(),
        objectives: vec![QuestObjective {
            id: "obj-1".to_owned(),
            r#type: "harvest".to_owned(),
            description: "Harvest wheat".to_owned(),
            target_crop_type: Some("wheat".to_owned()),
            target_crop_quantity: Some(1),
            completed: false,
            progress: 0,
            ..QuestObjective::default()
        }],
        rewards: QuestRewards { money: Some(100), ..QuestRewards::default() },
        ..Quest::default()
    };

    let sun_only = || vec![WeatherTableEntry { weather_id: "sun".to_owned(), weight: 1 }];
    let mut table = IndexMap::new();
    for season in ["spring", "summer", "fall", "winter"] {
        table.insert(season.to_owned(), sun_only());
    }

    GameProject {
        schema_version: 4,
        id: "proj-test".to_owned(),
        name: "Test".to_owned(),
        version: "2".to_owned(),
        scenes: vec![scene],
        npcs: vec![npc.clone()],
        items: items.clone(),
        events: Vec::new(),
        // Same list as the NPC's dialogue (TS `dialogues: npc.dialogue`).
        dialogues: npc.dialogue.clone(),
        quests: vec![quest],
        player: Player {
            x: units::tiles(3),
            y: units::tiles(4),
            direction: "up".to_owned(),
            scene_id: "scene-test".to_owned(),
            inventory: vec![
                slot(&items, "seed-wheat", 5),
                slot(&items, "tool-hoe", 1),
                slot(&items, "tool-watering-can", 1),
            ],
            max_inventory_size: 10,
            money: 100,
            active_quests: vec!["quest-wheat".to_owned()],
            completed_quests: Vec::new(),
            ..Player::default()
        },
        event_flags: IndexMap::new(),
        start_scene_id: "scene-test".to_owned(),
        mode: "play".to_owned(),
        selected_tile_type: "grass".to_owned(),
        selected_npc_id: None,
        selected_item_id: None,
        current_time: 1_000_000,
        custom_assets: Vec::new(),
        current_season: "spring".to_owned(),
        current_day: 1,
        current_time_minutes: units::minutes(6 * 60),
        current_year: 1,
        shops: Vec::new(),
        node_types: Vec::new(),
        settings: default_project_settings(),
        recipes: Vec::new(),
        actions: Vec::new(),
        minigames: Vec::new(),
        machine_types: Vec::new(),
        // Sun-only table keeps sim tests weather-independent (weather has its own dedicated tests).
        weather: WeatherConfig {
            types: vec![WeatherTypeDefinition {
                id: "sun".to_owned(),
                name: "Sunny".to_owned(),
                ..WeatherTypeDefinition::default()
            }],
            table,
            ..WeatherConfig::default()
        },
        animal_species: Vec::new(),
        animals: Vec::new(),
        fish_tables: Vec::new(),
        mine: MineConfig { enabled: false, ..MineConfig::default() },
        content_packs: Vec::new(),
        game_start_time: 1_000_000,
        ..GameProject::default()
    }
}

fn make_engine_with(seed: &str, mutate: impl FnOnce(GameProject) -> GameProject) -> (EngineContext, GameState) {
    let project = mutate(make_project());
    let ctx = EngineContext::new(state::create_content_from_project(&project));
    let state = state::create_game_state(&project, Some(seed));
    (ctx, state)
}

fn make_engine(seed: &str) -> (EngineContext, GameState) {
    make_engine_with(seed, |project| project)
}

/// The C# `CloseTo(expected, actual, digits)`.
fn assert_close(expected: f64, actual: f64, digits: i32) {
    assert!(
        (expected - actual).abs() < 10f64.powi(-digits) / 2.0,
        "expected {actual} to be close to {expected} ({digits} digits)"
    );
}

/// What the `setMoveIntent` command does to the player before ticks integrate it: hold the
/// intent and face it (the C# `WithIntent` helper goes through the engine instead).
fn hold_intent(state: &mut GameState, dx: i32, dy: i32) {
    let intent = MoveIntent { dx, dy };
    state.player.direction = direction_from_intent(&intent, &state.player.direction);
    state.player.move_intent = intent;
}

/// Integrates `ticks` ticks of free movement directly (no engine), collecting the effects.
fn integrate(ctx: &EngineContext, state: &mut GameState, ticks: usize) -> Vec<Effect> {
    let mut effects = Vec::new();
    for _ in 0..ticks {
        effects.extend(integrate_movement(ctx, state));
    }
    effects
}

fn with_intent(ctx: &EngineContext, state: &mut GameState, dx: i32, dy: i32) -> Vec<Effect> {
    apply_command(ctx, state, &Command::SetMoveIntent { dx, dy })
}

fn p(x: i32, y: i32) -> PathPoint {
    PathPoint { x, y }
}

fn walkability(scene: &farm_sim::schema::Scene) -> Walkability<'_> {
    Walkability { scene, node_types: None, blocked: None }
}

// --- tiles ---

#[test]
fn create_empty_scene_builds_a_grass_grid() {
    let scene = tiles::create_empty_scene("s", "S", 4, 3);
    assert_eq!(scene.width, 4);
    assert_eq!(scene.height, 3);
    assert_eq!(scene.tiles.len(), 3);
    for (y, row) in scene.tiles.iter().enumerate() {
        assert_eq!(row.len(), 4);
        for (x, tile) in row.iter().enumerate() {
            assert_eq!((tile.x, tile.y), (x as i32, y as i32));
            assert_eq!(tile.r#type, "grass");
            assert_eq!(tile.background, "grass");
            assert_eq!(tile.overlay, None);
            assert_eq!(tile.object, None);
            assert!(!tile.collision);
        }
    }
    assert!(scene.transitions.is_empty() && scene.npcs.is_empty() && scene.events.is_empty());
}

#[test]
fn create_empty_tile_routes_the_type_to_its_layer() {
    let wall = tiles::create_empty_tile(1, 2, "wall");
    assert_eq!(
        (wall.background.as_str(), wall.overlay.as_deref(), wall.object.as_deref()),
        ("grass", None, Some("wall"))
    );
    assert!(wall.collision);
    let path = tiles::create_empty_tile(0, 0, "path");
    assert_eq!(
        (path.background.as_str(), path.overlay.as_deref(), path.object.as_deref()),
        ("grass", Some("path"), None)
    );
    assert!(!path.collision);
    let door = tiles::create_empty_tile(0, 0, "door");
    assert_eq!(door.object.as_deref(), Some("door"));
    assert!(!door.collision);
}

#[test]
fn set_tile_layer_updates_one_layer_and_keeps_the_others() {
    let grass = tiles::create_empty_tile(0, 0, "grass");
    let wall = tiles::set_tile_layer(&grass, "wall", None);
    assert_eq!(wall.r#type, "wall");
    assert_eq!(wall.background, "grass");
    assert_eq!(wall.object.as_deref(), Some("wall"));
    assert!(wall.collision);
    assert_eq!(wall.visuals, None);

    let door = tiles::set_tile_layer(&wall, "door", None);
    assert!(!door.collision);

    let soil = tiles::set_tile_layer(&wall, "soil", None);
    assert_eq!(soil.background, "soil");
    assert_eq!(soil.object.as_deref(), Some("wall"));
    assert!(soil.collision, "changing the background keeps the object layer's collision");

    let mut with_image = grass.clone();
    with_image.custom_image = Some("img".to_owned());
    let visual = VisualRef { asset_id: "asset-1".to_owned(), animation: None, frame: None };
    let painted = tiles::set_tile_layer(&with_image, "path", Some(&visual));
    assert_eq!(painted.custom_image, None);
    assert_eq!(painted.overlay.as_deref(), Some("path"));
    let visuals = painted.visuals.expect("visuals set");
    assert_eq!(visuals.overlay, Some(visual));
    assert_eq!(visuals.background, None);
}

#[test]
fn paint_rect_clamps_to_the_grid_and_clears_crops_and_nodes() {
    let mut scene = tiles::create_empty_scene("s", "S", 4, 4);
    scene.tiles[1][1].node = Some(TileNode { type_id: "tree".to_owned(), remaining_health: 3, ..TileNode::default() });
    let painted = tiles::paint_rect(&scene.tiles, 2, 2, -5, -5, "wall", None);
    for (y, row) in painted.iter().enumerate() {
        for (x, tile) in row.iter().enumerate() {
            if x <= 2 && y <= 2 {
                assert_eq!(tile.r#type, "wall", "({x},{y})");
                assert!(tile.collision);
                assert_eq!(tile.node, None);
            } else {
                assert_eq!(tile.r#type, "grass", "({x},{y})");
            }
        }
    }
    // Pure: the input grid is untouched.
    assert_eq!(scene.tiles[0][0].r#type, "grass");
    assert!(scene.tiles[1][1].node.is_some());
}

#[test]
fn flood_fill_replaces_the_contiguous_region_of_the_source_type() {
    let mut scene = tiles::create_empty_scene("s", "S", 4, 4);
    for y in 0..4 {
        scene.tiles[y][2] = tiles::set_tile_layer(&scene.tiles[y][2], "wall", None);
    }
    let filled = tiles::flood_fill(&scene.tiles, 0, 0, "soil", None);
    for row in &filled {
        assert_eq!(row[0].r#type, "soil");
        assert_eq!(row[1].r#type, "soil");
        assert_eq!(row[2].r#type, "wall");
        assert_eq!(row[3].r#type, "grass", "the wall column stops the fill");
    }
    // No-op when the types already match, and outside the grid.
    assert_eq!(tiles::flood_fill(&scene.tiles, 0, 0, "grass", None), scene.tiles);
    assert_eq!(tiles::flood_fill(&scene.tiles, 9, 0, "soil", None), scene.tiles);
}

#[test]
fn copy_and_paste_tile_region_round_trip_with_rewritten_coordinates() {
    let mut scene = tiles::create_empty_scene("s", "S", 4, 4);
    scene.tiles[1][1] = tiles::set_tile_layer(&scene.tiles[1][1], "soil", None);
    scene.tiles[2][2] = tiles::set_tile_layer(&scene.tiles[2][2], "wall", None);
    let region = tiles::copy_tile_region(&scene.tiles, 2, 2, 1, 1);
    assert_eq!(region.len(), 2);
    assert_eq!(region[0][0].r#type, "soil");
    assert_eq!(region[1][1].r#type, "wall");

    let pasted = tiles::paste_tile_region(&scene.tiles, &region, 3, 3);
    assert_eq!(pasted[3][3].r#type, "soil");
    assert_eq!((pasted[3][3].x, pasted[3][3].y), (3, 3));
    // The rest of the region falls outside the grid and is skipped.
    assert_eq!(pasted[3][2].r#type, "grass");
    let empty = tiles::copy_tile_region(&scene.tiles, 10, 10, 12, 12);
    assert!(empty.is_empty());
}

// --- pathfinding (M3SystemsTests) ---

#[test]
fn routes_around_obstacles() {
    let mut scene = tiles::create_empty_scene("s", "S", 5, 5);
    // wall across row 2 except (4,2)
    for x in 0..4 {
        scene.tiles[2][x] = tiles::set_tile_layer(&scene.tiles[2][x], "wall", None);
    }
    let path = find_path(&walkability(&scene), p(0, 0), p(0, 4)).expect("reachable");
    assert_eq!(path.last().copied(), Some(p(0, 4)));
    // must pass through the gap at (4,2)
    assert!(path.iter().any(|point| point.x == 4 && point.y == 2));
    // exclusive of start, one tile per step
    assert!(!path.contains(&p(0, 0)));
    assert_eq!(path.len(), 12);
}

#[test]
fn returns_none_when_unreachable() {
    let mut scene = tiles::create_empty_scene("s", "S", 5, 5);
    for x in 0..5 {
        scene.tiles[2][x] = tiles::set_tile_layer(&scene.tiles[2][x], "wall", None);
    }
    assert_eq!(find_path(&walkability(&scene), p(0, 0), p(0, 4)), None);
    // A blocked goal is unreachable outright.
    assert_eq!(find_path(&walkability(&scene), p(0, 0), p(0, 2)), None);
}

#[test]
fn is_deterministic() {
    let scene = tiles::create_empty_scene("s", "S", 8, 8);
    let a = find_path(&walkability(&scene), p(0, 0), p(7, 7));
    let b = find_path(&walkability(&scene), p(0, 0), p(7, 7));
    assert_eq!(a, b);
    let path = a.expect("reachable");
    assert_eq!(path.len(), 14);
    // Ties break by insertion order (up, down, left, right): the first step is down.
    assert_eq!(path[0], p(0, 1));
}

#[test]
fn same_tile_is_an_empty_path_and_blocked_tiles_are_avoided() {
    let scene = tiles::create_empty_scene("s", "S", 3, 3);
    assert_eq!(find_path(&walkability(&scene), p(1, 1), p(1, 1)), Some(Vec::new()));

    let mut blocked = IndexSet::new();
    blocked.insert("1,0".to_owned());
    blocked.insert("1,1".to_owned());
    let w = Walkability { scene: &scene, node_types: None, blocked: Some(&blocked) };
    assert!(!is_walkable(&w, 1, 0));
    assert!(!is_walkable(&w, -1, 0));
    assert!(!is_walkable(&w, 0, 3));
    assert!(is_walkable(&w, 0, 0));
    let path = find_path(&w, p(0, 0), p(2, 0)).expect("reachable around the blocked column");
    assert_eq!(path, vec![p(0, 1), p(0, 2), p(1, 2), p(2, 2), p(2, 1), p(2, 0)]);
}

#[test]
fn gathering_nodes_block_unless_their_type_says_otherwise() {
    let mut scene = tiles::create_empty_scene("s", "S", 3, 3);
    scene.tiles[0][1].node = Some(TileNode { type_id: "weeds".to_owned(), remaining_health: 1, ..TileNode::default() });
    scene.tiles[0][2].node = Some(TileNode { type_id: "weeds".to_owned(), remaining_health: 0, ..TileNode::default() });
    // Unknown node types block by default.
    assert!(!is_walkable(&walkability(&scene), 1, 0));
    // Depleted nodes never block.
    assert!(is_walkable(&walkability(&scene), 2, 0));

    let mut node_types = IndexMap::new();
    node_types.insert(
        "weeds".to_owned(),
        NodeTypeDefinition { id: "weeds".to_owned(), blocks_movement: false, ..NodeTypeDefinition::default() },
    );
    let w = Walkability { scene: &scene, node_types: Some(&node_types), blocked: None };
    assert!(is_walkable(&w, 1, 0));
}

// --- facing from intent (FreeMovementTests) ---

#[test]
fn faces_the_pressed_cardinal_direction() {
    assert_eq!(direction_from_intent(&MoveIntent { dx: 1, dy: 0 }, "up"), "right");
    assert_eq!(direction_from_intent(&MoveIntent { dx: 0, dy: 1 }, "left"), "down");
}

#[test]
fn keeps_the_current_facing_on_a_matching_diagonal() {
    assert_eq!(direction_from_intent(&MoveIntent { dx: 1, dy: -1 }, "up"), "up");
    assert_eq!(direction_from_intent(&MoveIntent { dx: 1, dy: -1 }, "right"), "right");
}

#[test]
fn prefers_the_horizontal_axis_on_a_non_matching_diagonal() {
    assert_eq!(direction_from_intent(&MoveIntent { dx: 1, dy: -1 }, "down"), "right");
}

#[test]
fn keeps_facing_when_idle() {
    assert_eq!(direction_from_intent(&MoveIntent { dx: 0, dy: 0 }, "left"), "left");
}

// --- tile helpers and collision ---

#[test]
fn player_tile_and_facing_target_floor_the_box_center() {
    let (_, mut state) = make_engine("tiles");
    assert_eq!(player_tile(&state), world_movement::TilePoint { x: 3, y: 4 });
    assert_eq!(facing_target(&state), world_movement::TilePoint { x: 3, y: 3 });
    state.player.x = units::pos(2.999);
    state.player.direction = "right".to_owned();
    assert_eq!(player_tile(&state), world_movement::TilePoint { x: 2, y: 4 });
    assert_eq!(facing_target(&state), world_movement::TilePoint { x: 3, y: 4 });
    state.player.direction = "sideways".to_owned();
    assert_eq!(facing_target(&state), world_movement::TilePoint { x: 2, y: 4 });
}

#[test]
fn can_move_to_checks_bounds_walls_machines_and_npcs() {
    let (_, state) = make_engine("collision");
    let scene = &state.world.scenes[0];
    assert!(!can_move_to(scene, 4, 4, &state.npcs, None, None, None), "wall");
    assert!(!can_move_to(scene, -1, 0, &state.npcs, None, None, None));
    assert!(!can_move_to(scene, 6, 0, &state.npcs, None, None, None));
    assert!(!can_move_to(scene, 0, 6, &state.npcs, None, None, None));
    assert!(!can_move_to(scene, 1, 1, &state.npcs, None, None, None), "npc");
    assert!(can_move_to(scene, 1, 1, &state.npcs, Some("npc-test"), None, None), "excluded npc");
    assert!(can_move_to(scene, 0, 0, &state.npcs, None, None, None));

    let mut with_machine = scene.clone();
    with_machine.tiles[0][0].machine = Some(TileMachine { type_id: "furnace".to_owned(), ..TileMachine::default() });
    assert!(!can_move_to(&with_machine, 0, 0, &state.npcs, None, None, None), "unknown machine types block");
}

// --- discrete moves (EngineTests movement) ---

#[test]
fn blocks_movement_into_walls_but_still_turns() {
    let (ctx, mut state) = make_engine("engine-test");
    let effects = handle_move(&ctx, &mut state, "right");
    assert_eq!(state.player.x, units::pos(3.5));
    assert_eq!(state.player.direction, "right");
    assert!(effects.is_empty());
}

#[test]
fn blocks_movement_onto_npcs() {
    let (ctx, mut state) = make_engine("engine-test");
    state.player.x = units::tiles(1);
    state.player.y = units::tiles(2);
    handle_move(&ctx, &mut state, "up");
    assert_eq!(state.player.y, units::tiles(2)); // npc at (1,1)
    assert_eq!(state.player.direction, "up");
}

#[test]
fn blocks_movement_out_of_bounds() {
    let (ctx, mut state) = make_engine("engine-test");
    state.player.x = units::pos(0.5);
    state.player.y = units::pos(5.5);
    handle_move(&ctx, &mut state, "down");
    handle_move(&ctx, &mut state, "left");
    assert_eq!((state.player.x, state.player.y), (units::pos(0.5), units::pos(5.5)));
    assert_eq!(state.player.direction, "left");
}

#[test]
fn moves_the_player_and_updates_direction() {
    let (ctx, mut state) = make_engine("engine-test");
    let effects = handle_move(&ctx, &mut state, "left");
    assert_eq!(state.player.x, units::pos(2.5));
    assert_eq!(state.player.y, units::pos(4.5));
    assert_eq!(state.player.direction, "left");
    assert!(effects.contains(&Effect::PlayerMoved { x: 2, y: 4 }));
}

#[test]
fn blocks_movement_out_of_bounds_after_walking_to_the_edge() {
    let (ctx, mut state) = make_engine("engine-test");
    for _ in 0..10 {
        handle_move(&ctx, &mut state, "down");
    }
    assert_eq!(state.player.y, units::pos(5.5));
}

// --- free movement (FreeMovementTests) ---

#[test]
fn starts_on_the_tile_center_with_an_idle_intent() {
    let (_, state) = make_engine("free-move-test");
    assert_eq!(state.player.x, units::pos(3.5));
    assert_eq!(state.player.y, units::pos(4.5));
    assert_eq!(state.player.move_intent, MoveIntent { dx: 0, dy: 0 });
}

#[test]
fn integrates_position_from_a_held_intent_at_the_configured_speed() {
    let (ctx, mut state) = make_engine("free-move-test");
    // 2 ticks up at 4.5 tiles/s and 20 t/s = 0.45 tiles (the occupied tile stays (3,4)). The
    // speed is 1843/8192 tile per tick on the position grid, 0.00005 tile less per tick.
    hold_intent(&mut state, 0, -1);
    let effects = integrate(&ctx, &mut state, 2);
    assert_eq!(state.player.x, units::pos(3.5));
    assert_eq!(state.player.y, units::pos(4.5) - 2 * 1843);
    assert_close(4.5 - 0.45, t(state.player.y), 3);
    assert_eq!(state.player.direction, "up");
    assert!(effects.is_empty(), "no tile change, no effects");
}

#[test]
fn integrates_position_from_a_held_intent_at_the_configured_speed_via_engine() {
    let (ctx, mut state) = make_engine("free-move-test");
    // 4 ticks up at 4.5 tiles/s and 20 t/s = 0.9 tiles.
    with_intent(&ctx, &mut state, 0, -1);
    advance_tick(&ctx, &mut state, 4);
    assert_eq!(state.player.x, units::pos(3.5));
    assert_eq!(state.player.y, units::pos(4.5) - 4 * 1843);
    assert_close(4.5 - 0.9, t(state.player.y), 3);
    assert_eq!(state.player.direction, "up");
}

#[test]
fn does_not_move_without_an_intent() {
    let (ctx, mut state) = make_engine("free-move-test");
    let effects = integrate(&ctx, &mut state, 10);
    assert_eq!(state.player.x, units::pos(3.5));
    assert_eq!(state.player.y, units::pos(4.5));
    assert!(effects.is_empty());
}

#[test]
fn does_not_move_without_an_intent_and_stops_when_the_intent_clears() {
    let (ctx, state) = make_engine("free-move-test");
    let mut idle = state.clone();
    advance_tick(&ctx, &mut idle, 10);
    assert_eq!(idle.player.x, units::pos(3.5));
    assert_eq!(idle.player.y, units::pos(4.5));

    let mut moving = state;
    with_intent(&ctx, &mut moving, 0, -1);
    advance_tick(&ctx, &mut moving, 2);
    let mut stopped = moving.clone();
    with_intent(&ctx, &mut stopped, 0, 0);
    advance_tick(&ctx, &mut stopped, 10);
    assert_eq!(moving.player.y, stopped.player.y);
}

#[test]
fn normalizes_diagonal_movement_no_sqrt2_speed_advantage() {
    let (ctx, mut state) = make_engine("free-move-test");
    // 2 ticks keep the box inside tile (3,4).
    hold_intent(&mut state, -1, 1);
    integrate(&ctx, &mut state, 2);
    let dx = t(state.player.x) - 3.5;
    let dy = t(state.player.y) - 4.5;
    assert_close(0.45, (dx * dx + dy * dy).sqrt(), 3);
    assert_eq!(units::pos(3.5) - state.player.x, state.player.y - units::pos(4.5));
}

#[test]
fn normalizes_diagonal_movement_via_engine() {
    let (ctx, mut state) = make_engine("free-move-test");
    with_intent(&ctx, &mut state, -1, 1);
    advance_tick(&ctx, &mut state, 4);
    let dx = t(state.player.x) - 3.5;
    let dy = t(state.player.y) - 4.5;
    assert_close(0.9, (dx * dx + dy * dy).sqrt(), 3);
    assert_eq!(units::pos(3.5) - state.player.x, state.player.y - units::pos(4.5));
}

#[test]
fn clamps_against_a_wall_tile_with_a_collision_skin() {
    let (ctx, mut state) = make_engine("free-move-test");
    // Wall at (4,4); moving right from (3.5,4.5) stops at 4 − halfWidth.
    hold_intent(&mut state, 1, 0);
    let effects = integrate(&ctx, &mut state, 20);
    // The collision skin is one position unit.
    assert_eq!(state.player.x, units::tiles(4) - PLAYER_HALF_WIDTH - 1);
    assert_eq!(state.player.y, units::pos(4.5));
    assert!(effects.is_empty(), "never left tile (3,4)");
    assert_eq!(state.player.direction, "right");
}

#[test]
fn slides_along_blockers_on_diagonal_input() {
    let (ctx, mut state) = make_engine("free-move-test");
    // Up is blocked by the NPC tile at (1,1) while the box overlaps column 1; the leftward
    // component keeps sliding. (The C# starts at x = 1.5 and crosses into column 0, which
    // settles through GameEvents; starting at 1.9 keeps the occupied tile at (1,2).)
    state.player.x = units::pos(1.9);
    state.player.y = units::pos(2.9);
    hold_intent(&mut state, -1, -1);
    integrate(&ctx, &mut state, 5);
    assert_eq!(state.player.y, units::tiles(2) + PLAYER_HALF_WIDTH + 1);
    assert!(t(state.player.x) < 1.9 - 0.7);
    assert_eq!(player_tile(&state), world_movement::TilePoint { x: 1, y: 2 });
}

#[test]
fn slides_along_blockers_on_diagonal_input_via_engine() {
    let (ctx, mut state) = make_engine("free-move-test");
    state.player.x = units::pos(1.5);
    state.player.y = units::pos(2.9);
    with_intent(&ctx, &mut state, -1, -1);
    advance_tick(&ctx, &mut state, 5);
    assert_eq!(state.player.y, units::tiles(2) + PLAYER_HALF_WIDTH + 1);
    assert!(state.player.x < units::tiles(1));
}

#[test]
fn is_contained_by_scene_bounds() {
    let (ctx, mut state) = make_engine("free-move-test");
    // Start on the bottom row so the occupied tile never changes.
    state.player.y = units::pos(5.5);
    hold_intent(&mut state, 0, 1);
    integrate(&ctx, &mut state, 60);
    assert_eq!(state.player.y, units::tiles(6) - PLAYER_HALF_WIDTH - 1);
    assert_eq!(player_tile(&state), world_movement::TilePoint { x: 3, y: 5 });
}

#[test]
fn is_contained_by_scene_bounds_via_engine() {
    let (ctx, mut state) = make_engine("free-move-test");
    with_intent(&ctx, &mut state, 0, 1);
    advance_tick(&ctx, &mut state, 60);
    assert_eq!(state.player.y, units::tiles(6) - PLAYER_HALF_WIDTH - 1);
}

#[test]
fn is_blocked_by_npc_occupied_tiles() {
    let (ctx, mut state) = make_engine("free-move-test");
    // Approach the NPC at (1,1) from tile (1,2): the clamp lands inside the same tile.
    state.player.x = units::pos(1.5);
    state.player.y = units::pos(2.5);
    hold_intent(&mut state, 0, -1);
    integrate(&ctx, &mut state, 40);
    assert_eq!(state.player.y, units::tiles(2) + PLAYER_HALF_WIDTH + 1);
    assert_eq!(state.player.x, units::pos(1.5));
}

#[test]
fn is_blocked_by_npc_occupied_tiles_via_engine() {
    let (ctx, mut state) = make_engine("free-move-test");
    // Approach the NPC at (1,1) from below (start on tile (1,3)).
    state.player.x = units::pos(1.5);
    state.player.y = units::pos(3.5);
    with_intent(&ctx, &mut state, 0, -1);
    advance_tick(&ctx, &mut state, 40);
    assert_eq!(state.player.y, units::tiles(2) + PLAYER_HALF_WIDTH + 1);
}

#[test]
fn freezes_while_a_dialogue_is_open() {
    let (ctx, mut state) = make_engine("free-move-test");
    with_intent(&ctx, &mut state, 0, -1);
    state.dialogue = Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-1".to_owned() });
    advance_tick(&ctx, &mut state, 10);
    assert_eq!(state.player.y, units::pos(4.5));
}

#[test]
fn picks_up_ground_items_when_crossing_onto_their_tile() {
    let (ctx, mut state) = make_engine_with("pickup", |mut project| {
        let item = project.items.iter().find(|i| i.id == "seed-wheat").expect("seed-wheat").clone();
        project.scenes[0].tiles[3][3].item = Some(item);
        project
    });
    let quantity =
        |state: &GameState| state.player.inventory.iter().find(|s| s.item.id == "seed-wheat").map_or(0, |s| s.quantity);
    let before = quantity(&state);
    with_intent(&ctx, &mut state, 0, -1);
    let effects = advance_tick(&ctx, &mut state, 20);
    assert_eq!(quantity(&state), before + 1);
    assert!(effects.iter().any(|e| e.type_name() == "playerMoved"));
    assert!(effects.contains(&Effect::message("success", "Picked up Wheat Seeds")));
    assert_eq!(state.world.scenes[0].tiles[3][3].item, None);
}

#[test]
fn fires_transitions_when_the_occupied_tile_changes() {
    let (ctx, mut state) = make_engine_with("transition", |mut project| {
        project.scenes[0].transitions = vec![SceneTransition {
            from_x: 3,
            from_y: 2,
            to_scene_id: "scene-cave".to_owned(),
            to_x: 2,
            to_y: 2,
            ..SceneTransition::default()
        }];
        project.scenes.push(tiles::create_empty_scene("scene-cave", "Cave", 5, 5));
        project
    });
    // 7 ticks up crosses from tile (3,4) into the transition tile (3,2).
    with_intent(&ctx, &mut state, 0, -1);
    let effects = advance_tick(&ctx, &mut state, 7);
    assert_eq!(state.player.scene_id, "scene-cave");
    // Transition lands on the target tile's center.
    assert_eq!(state.player.x, units::pos(2.5));
    assert_eq!(state.player.y, units::pos(2.5));
    assert!(effects.iter().any(|e| matches!(e, Effect::SceneChanged { scene_id, .. } if scene_id == "scene-cave")));
    assert!(effects.contains(&Effect::message("success", "Entered Cave")));
}

#[test]
fn locked_transitions_do_not_fire() {
    let (ctx, mut state) = make_engine_with("locked", |mut project| {
        project.scenes[0].transitions = vec![SceneTransition {
            from_x: 3,
            from_y: 5,
            to_scene_id: "scene-cave".to_owned(),
            to_x: 2,
            to_y: 2,
            locked: Some(true),
            ..SceneTransition::default()
        }];
        project.scenes.push(tiles::create_empty_scene("scene-cave", "Cave", 5, 5));
        project
    });
    apply_command(&ctx, &mut state, &Command::Move { dir: "down".to_owned() });
    assert_eq!(state.player.scene_id, "scene-test"); // stayed
    assert_eq!(state.player.y, units::pos(5.5));
}

#[test]
fn replays_deterministically_from_the_intent_command_log() {
    let script = vec![
        replay::command(Command::SetMoveIntent { dx: 0, dy: -1 }),
        replay::ticks(7),
        replay::command(Command::SetMoveIntent { dx: -1, dy: -1 }),
        replay::ticks(11),
        replay::command(Command::SetMoveIntent { dx: 1, dy: 0 }),
        replay::ticks(13),
        replay::command(Command::SetMoveIntent { dx: 0, dy: 0 }),
        replay::ticks(5),
    ];
    let (ctx_a, mut a) = make_engine("replay-seed");
    let (ctx_b, mut b) = make_engine("replay-seed");
    let result_a = replay::run_replay(&ctx_a, &mut a, &script);
    let result_b = replay::run_replay(&ctx_b, &mut b, &script);
    assert_eq!(result_a.hash, result_b.hash);
    assert_eq!(hash_state(&a.player), hash_state(&b.player));
}

#[test]
fn clamps_intents_to_unit_components() {
    let (ctx, mut state) = make_engine("free-move-test");
    with_intent(&ctx, &mut state, 5, -3);
    assert_eq!(state.player.move_intent, MoveIntent { dx: 1, dy: -1 });
}

// --- NPC schedules & movement (M3SystemsTests) ---
//
// The C# drives these through `Engine.AdvanceTick(ctx, state, 20)` (one game minute per call);
// here `advance_npcs` is called directly with one minute. All of them need `Weather.CurrentWeather`.

fn scheduled_to(project: GameProject, scene_id: &str, x: i32, y: i32) -> GameProject {
    let mut project = project;
    project.npcs[0].can_move = true;
    project.npcs[0].schedule =
        Some(vec![NpcScheduleEntry { minute: 6 * 60, scene_id: scene_id.to_owned(), x, y, ..Default::default() }]);
    project
}

#[test]
fn walks_an_npc_toward_its_scheduled_destination_one_step_per_minute() {
    let (ctx, mut state) = make_engine_with("m3", |project| scheduled_to(project, "scene-test", 4, 1));
    // npc starts at (1,1); destination (4,1) = 3 steps
    for step in 1..=3 {
        advance_npcs(&ctx, &mut state, 1);
        assert_eq!(state.npcs["npc-test"].x, units::tiles(1 + step), "one step per minute");
    }
    assert_eq!(state.npcs["npc-test"].x, units::tiles(4));
    assert_eq!(state.npcs["npc-test"].y, units::tiles(1));
    assert_eq!(state.npcs["npc-test"].path, Some(Vec::new()), "the consumed path stays as an empty list");
}

#[test]
fn teleports_for_cross_scene_schedule_entries() {
    let (ctx, mut state) = make_engine_with("m3", |mut project| {
        project.scenes.push(tiles::create_empty_scene("scene-house", "House", 5, 5));
        scheduled_to(project, "scene-house", 2, 2)
    });
    advance_npcs(&ctx, &mut state, 1);
    assert_eq!(state.npcs["npc-test"].scene_id, "scene-house");
    assert_eq!(state.npcs["npc-test"].x, units::tiles(2));
    assert_eq!(state.npcs["npc-test"].path, None);
}

#[test]
fn pauses_when_the_player_stands_adjacent() {
    let (ctx, mut state) = make_engine_with("m3", |project| scheduled_to(project, "scene-test", 4, 1));
    state.player.x = units::tiles(1);
    state.player.y = units::tiles(2);
    advance_npcs(&ctx, &mut state, 1);
    assert_eq!(state.npcs["npc-test"].x, units::tiles(1));
    assert_eq!(state.npcs["npc-test"].y, units::tiles(1));
}

#[test]
fn wandering_npcs_stay_within_their_radius_and_use_the_seeded_rng() {
    let (ctx, state) = make_engine_with("m3", |mut project| {
        project.npcs[0].can_move = true;
        project.npcs[0].move_pattern = Some("wander".to_owned());
        project.npcs[0].wander_radius = Some(2);
        project
    });
    let mut a = state.clone();
    let mut b = state.clone();
    for _ in 0..30 {
        advance_npcs(&ctx, &mut a, 1);
        advance_npcs(&ctx, &mut b, 1);
    }
    // deterministic across identical runs
    assert_eq!(a.npcs["npc-test"], b.npcs["npc-test"]);
    assert_eq!(a.rng, b.rng);
    assert_ne!(a.rng, state.rng, "wander draws advance the seeded RNG");
    assert!((units::tile_of(a.npcs["npc-test"].x) - 1).abs() <= 2);
    assert!((units::tile_of(a.npcs["npc-test"].y) - 1).abs() <= 2);
}

#[test]
fn patrolling_npcs_loop_over_their_waypoints() {
    let (ctx, mut state) = make_engine_with("m3", |mut project| {
        project.npcs[0].can_move = true;
        project.npcs[0].move_pattern = Some("patrol".to_owned());
        project.npcs[0].patrol_points = Some(vec![GridPoint { x: 3, y: 1 }, GridPoint { x: 1, y: 1 }]);
        project
    });
    advance_npcs(&ctx, &mut state, 1);
    assert_eq!((state.npcs["npc-test"].x, state.npcs["npc-test"].y), (units::tiles(2), units::tiles(1)));
    advance_npcs(&ctx, &mut state, 1);
    assert_eq!((state.npcs["npc-test"].x, state.npcs["npc-test"].y), (units::tiles(3), units::tiles(1)));
    // At the waypoint: advance the patrol index, then head back.
    advance_npcs(&ctx, &mut state, 1);
    assert_eq!(state.npcs["npc-test"].patrol_index, Some(1));
    assert_eq!(state.npcs["npc-test"].path, None);
    advance_npcs(&ctx, &mut state, 1);
    assert_eq!((state.npcs["npc-test"].x, state.npcs["npc-test"].y), (units::tiles(2), units::tiles(1)));
}

#[test]
fn stationary_npcs_and_zero_minutes_leave_the_state_alone() {
    // Both early returns happen before the weather lookup, so this runs without Weather.
    let (ctx, state) = make_engine("m3");
    let mut untouched = state.clone();
    advance_npcs(&ctx, &mut untouched, 1);
    assert_eq!(untouched, state);
    let (ctx, state) = make_engine_with("m3", |project| scheduled_to(project, "scene-test", 4, 1));
    let mut zero = state.clone();
    advance_npcs(&ctx, &mut zero, 0);
    assert_eq!(zero, state);
}
