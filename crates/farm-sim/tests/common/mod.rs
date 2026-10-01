//! Shared fixtures for the ported engine-core tests (C# `EngineTests.MakeProject` and
//! `CoreTestHelpers`). Tiles and scenes are built here directly, in the shape
//! `Tiles.CreateEmptyTile` / `Tiles.CreateEmptyScene` produce, so these tests do not wait on the
//! tiles port.
#![allow(dead_code)]

use farm_sim::schema::{
    Dialogue, DialogueOption, GameProject, GameState, InventorySlot, Item, MineConfig, Npc, Player, ProjectSettings,
    Quest, QuestObjective, QuestRewards, Scene, Tile, WeatherConfig, WeatherTableEntry, WeatherTypeDefinition,
};
use farm_sim::units;
use farm_sim::{content_builtin, state, Effect, EngineContext, HookBus};
use indexmap::IndexMap;
use serde_json::Value;

/// `Tiles.CreateEmptyTile(x, y, "grass")`.
pub fn empty_tile(x: i32, y: i32) -> Tile {
    Tile {
        x,
        y,
        r#type: "grass".to_owned(),
        background: "grass".to_owned(),
        overlay: None,
        object: None,
        collision: false,
        soil_moisture: 0,
        soil_fertility: 0,
        ..Tile::default()
    }
}

/// `Tiles.SetTileLayer(tile, newType)` for the layer types the fixtures use.
pub fn set_tile_layer(tile: &Tile, new_type: &str) -> Tile {
    let mut updated = Tile { r#type: new_type.to_owned(), ..tile.clone() };
    match new_type {
        "path" => updated.overlay = Some(new_type.to_owned()),
        "wall" | "door" => {
            updated.object = Some(new_type.to_owned());
            updated.collision = new_type == "wall";
        }
        _ => updated.background = new_type.to_owned(),
    }
    updated
}

/// `Tiles.CreateEmptyScene(id, name, width, height)`.
pub fn empty_scene(id: &str, name: &str, width: i32, height: i32) -> Scene {
    let mut tiles = Vec::new();
    let mut y = 0;
    while y < height {
        let mut row = Vec::new();
        let mut x = 0;
        while x < width {
            row.push(empty_tile(x, y));
            x += 1;
        }
        tiles.push(row);
        y += 1;
    }
    Scene { id: id.to_owned(), name: name.to_owned(), width, height, tiles, ..Scene::default() }
}

/// `CoreTestHelpers.Slot`.
pub fn slot(items: &[Item], id: &str, quantity: u32) -> InventorySlot {
    let item = items.iter().find(|i| i.id == id).unwrap_or_else(|| panic!("no built-in item '{id}'"));
    InventorySlot::new(item.clone(), quantity)
}

/// TS `effects.some(e => e.type === 'message' && predicate(e.text))`.
pub fn has_message(effects: &[Effect], predicate: impl Fn(&str) -> bool) -> bool {
    effects.iter().any(|e| matches!(e, Effect::Message { text, .. } if predicate(text)))
}

/// A message effect at the given level.
pub fn has_level(effects: &[Effect], level: &str) -> bool {
    effects.iter().any(|e| matches!(e, Effect::Message { level: l, .. } if l == level))
}

pub fn flag<'a>(state: &'a GameState, name: &str) -> Option<&'a Value> {
    state.flags.get(name)
}

/// TS `expect(state.flags[key]).toBe(true)`.
pub fn flag_is_true(state: &GameState, key: &str) -> bool {
    state.flags.get(key) == Some(&Value::Bool(true))
}

fn sun_only() -> Vec<WeatherTableEntry> {
    vec![WeatherTableEntry { weather_id: "sun".to_owned(), weight: 1 }]
}

/// Minimal test project (C# `EngineTests.MakeProject`): 6x6 open field with soil at (3,2), npc
/// at (1,1).
pub fn make_project() -> GameProject {
    let mut scene = empty_scene("scene-test", "Test Farm", 6, 6);
    scene.tiles[2][3] = set_tile_layer(&scene.tiles[2][3], "soil");
    scene.tiles[4][4] = set_tile_layer(&scene.tiles[4][4], "wall");

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
        // Same dialogues as the NPC's (TS `dialogues: npc.dialogue`).
        dialogues: npc.dialogue.clone(),
        npcs: vec![npc],
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
            completed_quests: vec![],
            ..Player::default()
        },
        items,
        quests: vec![quest],
        start_scene_id: "scene-test".to_owned(),
        mode: "play".to_owned(),
        selected_tile_type: "grass".to_owned(),
        current_time: 1_000_000.0,
        current_season: "spring".to_owned(),
        current_day: 1,
        current_time_minutes: units::minutes(6 * 60),
        current_year: 1,
        settings: ProjectSettings::default(),
        // Sun-only table keeps sim tests weather-independent (weather has its own dedicated
        // tests).
        weather: WeatherConfig {
            types: vec![WeatherTypeDefinition {
                id: "sun".to_owned(),
                name: "Sunny".to_owned(),
                ..WeatherTypeDefinition::default()
            }],
            table,
            ..WeatherConfig::default()
        },
        mine: MineConfig { enabled: false, ..MineConfig::default() },
        game_start_time: 1_000_000.0,
        ..GameProject::default()
    }
}

/// `EngineContext` + `GameState` for a (possibly mutated) test project, with a hook bus attached
/// so tests can drain the emitted hook events.
pub fn make_engine(mutate: impl FnOnce(&mut GameProject), seed: &str) -> (EngineContext, GameState) {
    let mut project = make_project();
    mutate(&mut project);
    let ctx = EngineContext::with_hooks(state::create_content_from_project(&project), HookBus::new());
    let game_state = state::create_game_state(&project, Some(seed));
    (ctx, game_state)
}
