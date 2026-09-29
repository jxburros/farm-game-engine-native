//! Port of `ShellSnapshot.cs`: [`shell_snapshot`] builds a play-mode [`WorldSnapshot`] from
//! engine content and live state (packages/game-shell/src/snapshot.ts), and [`editor_snapshot`]
//! the Edit Mode counterpart from the project (web `GameView.buildSnapshot` outside play).
//! Neither mutates the simulation, reads I/O or OS state. Art bindings and transient pops are
//! added afterwards ([`crate::apply_graphics`], the host's pops).

use crate::builtin_art::BuiltinArt;
use crate::num::to_int;
use crate::snapshot::{
    SnapshotAtmosphere, SnapshotCamera, SnapshotCrop, SnapshotEntity, SnapshotItem, SnapshotMachine, SnapshotNode,
    SnapshotTile, WorldSnapshot,
};
use farm_sim::farming::crops;
use farm_sim::schema::{directions, soil_states, tile_types, GameContent, GameProject, GameState, Scene};
use farm_sim::units::{self, MicroMinutes};
use farm_sim::world::tiles;
use serde::{Deserialize, Serialize};
use std::sync::Arc;

/// Options of [`shell_snapshot`] (C# `ShellSnapshotOptions`).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SnapshotOptions {
    pub tile_size: f64,
    pub padding: f64,
    /// Interpolated player position in world pixels (play mode).
    pub pixel_x: Option<f64>,
    pub pixel_y: Option<f64>,
    pub camera: Option<SnapshotCamera>,
}

fn direction_toward(x: f64, y: f64, target_x: f64, target_y: f64) -> &'static str {
    let (dx, dy) = (target_x - x, target_y - y);
    if dx.abs() > dy.abs() {
        if dx < 0.0 {
            directions::LEFT
        } else {
            directions::RIGHT
        }
    } else if dy < 0.0 {
        directions::UP
    } else {
        directions::DOWN
    }
}

/// Definition lookup by id where later definitions win (`new Map(entries)`).
fn last_by_id<'a, T>(definitions: &'a [T], id: &str, key: impl Fn(&T) -> &str) -> Option<&'a T> {
    definitions.iter().rev().find(|definition| key(definition) == id)
}

/// `(int)scene.Height` rows / columns, looping like C# `for (y = 0; y < scene.Height; y++)`.
fn cells(extent: i32) -> usize {
    usize::try_from(extent).unwrap_or(0)
}

/// A simulation position (1/8192 tile) in tiles, as the draw list uses them.
fn tiles_of(position: i32) -> f64 {
    units::position_to_tiles(position)
}

fn build_tiles(
    scene: &Scene,
    content: &GameContent,
    hide_item_at: impl Fn(usize, usize) -> bool,
) -> Vec<Vec<SnapshotTile>> {
    let mut rows = Vec::with_capacity(cells(scene.height));
    for y in 0..cells(scene.height) {
        let mut row = Vec::with_capacity(cells(scene.width));
        let source_row = scene.tiles.get(y);
        for x in 0..cells(scene.width) {
            let empty;
            let tile = match source_row.and_then(|r| r.get(x)) {
                Some(tile) => tile,
                None => {
                    empty = tiles::create_empty_tile(x as i32, y as i32, tile_types::GRASS);
                    &empty
                }
            };
            let background = if tile.background.is_empty() { tile.r#type.clone() } else { tile.background.clone() };
            let soil = background == tile_types::SOIL;
            // Soil state (read only): plantable soil is drawn tilled; wet after watering or rain
            // (moisture), speckled once fertilized.
            let watered = soil && (tile.soil_state.as_deref() == Some(soil_states::WATERED) || tile.soil_moisture > 0);
            let fertilized =
                soil && (tile.soil_state.as_deref() == Some(soil_states::FERTILIZED) || tile.soil_fertility > 0);
            let crop = tile.crop.as_ref().and_then(|crop| {
                let definition = content.crops.get(&crop.r#type)?;
                Some(SnapshotCrop {
                    sprite: None,
                    color_index: i32::try_from(crop.stage).unwrap_or(i32::MAX),
                    mature: crops::is_crop_mature_by_days(crop, definition),
                    withered: crop.withered == Some(true),
                    crop_id: Some(crop.r#type.clone()),
                    stages: i32::try_from(definition.stages).unwrap_or(i32::MAX),
                })
            });
            let node = tile.node.as_ref().map(|node| SnapshotNode {
                sprite: None,
                color: last_by_id(&content.node_types, &node.type_id, |d| &d.id)
                    .map_or("#7a5a3a", |d| d.color.as_str())
                    .to_owned(),
                depleted: node.remaining_health <= 0,
                type_id: Some(node.type_id.clone()),
            });
            let machine = tile.machine.as_ref().map(|machine| SnapshotMachine {
                sprite: None,
                color: last_by_id(&content.machine_types, &machine.type_id, |d| &d.id)
                    .map_or("#9a7b4f", |d| d.color.as_str())
                    .to_owned(),
                working: machine.processing.is_some(),
                output_ready: machine.output.as_ref().is_some_and(|output| !output.is_empty()),
                type_id: Some(machine.type_id.clone()),
            });
            let item = tile.item.as_ref().filter(|_| !hide_item_at(x, y)).map(|item| SnapshotItem {
                image_url: item.custom_image.as_deref().map(Arc::from),
                sprite: None,
                item_type: Some(item.r#type.clone()),
            });
            row.push(SnapshotTile {
                background,
                overlay: tile.overlay.clone(),
                object: tile.object.clone(),
                image_url: tile.custom_image.as_deref().map(Arc::from),
                art_layers: None,
                crop,
                node,
                machine,
                ladder_down: tile.ladder_down == Some(true),
                item,
                tilled: soil,
                watered,
                fertilized,
            });
        }
        rows.push(row);
    }
    rows
}

/// Port of `ShellSnapshot.BuildShellSnapshot`: the play-mode world of `scene` from content and
/// live state, with a contiguous world (no editor seams) and the clock's atmosphere.
pub fn shell_snapshot(
    content: &GameContent,
    state: &GameState,
    scene: &Scene,
    options: &SnapshotOptions,
) -> WorldSnapshot {
    let tiles = build_tiles(scene, content, |_, _| false);

    let mut npcs = Vec::new();
    for npc in &content.npcs {
        let live = state.npcs.get(&npc.id);
        if live.map_or(&npc.scene_id, |live| &live.scene_id) != &scene.id {
            continue;
        }
        let (x, y) = live.map_or((npc.x, npc.y), |live| (live.x, live.y));
        let (x, y) = (tiles_of(x), tiles_of(y));
        // A scheduled or patrolling NPC with path steps left is walking toward the next step.
        let next = live.and_then(|live| live.path.as_ref()).and_then(|path| path.first());
        npcs.push(SnapshotEntity {
            x,
            y,
            image_url: npc.custom_image.as_deref().map(Arc::from),
            appearance: Some(npc.appearance.clone()),
            direction: next
                .map_or(directions::DOWN, |next| direction_toward(x, y, f64::from(next.x), f64::from(next.y)))
                .to_owned(),
            moving: next.is_some(),
            ..SnapshotEntity::default()
        });
    }
    for animal in state.animals.iter().filter(|animal| animal.scene_id == scene.id) {
        // Animals face a stable direction chosen from their id so a herd isn't uniform. .NET
        // `string.Length` counts UTF-16 units.
        let length = animal.id.encode_utf16().count() as i32;
        let (x, y) = (tiles_of(animal.x), tiles_of(animal.y));
        let direction = directions::ALL[(BuiltinArt::hash(length, to_int(x + y)) % 4) as usize];
        npcs.push(SnapshotEntity {
            x,
            y,
            color: last_by_id(&content.animal_species, &animal.species_id, |d| &d.id).map(|d| d.color.clone()),
            kind: "animal".to_owned(),
            species_id: Some(animal.species_id.clone()),
            direction: direction.to_owned(),
            ..SnapshotEntity::default()
        });
    }

    let clock = &state.clock;
    WorldSnapshot {
        width: scene.width,
        height: scene.height,
        tile_size: options.tile_size,
        padding: options.padding,
        tile_gap: Some(0.0),
        camera: options.camera,
        pixel_art: None,
        grid_overlay: false,
        tick: clock.tick as f64,
        atmosphere: Some(SnapshotAtmosphere {
            time_minutes: units::to_f64::<MicroMinutes>(clock.time_minutes),
            weather_id: Some(clock.weather_id.clone()),
            season: Some(clock.season.clone()),
            weather_overlay: content
                .weather
                .types
                .iter()
                .find(|weather| weather.id == clock.weather_id)
                .and_then(|weather| weather.overlay.clone()),
        }),
        tiles,
        npcs,
        player: SnapshotEntity {
            x: tiles_of(state.player.x),
            y: tiles_of(state.player.y),
            direction: state.player.direction.clone(),
            pixel_x: options.pixel_x,
            pixel_y: options.pixel_y,
            moving: state.player.move_intent.dx != 0 || state.player.move_intent.dy != 0,
            ..SnapshotEntity::default()
        },
        pops: None,
    }
}

/// Port of `ShellSnapshot.BuildEditorSnapshot`: the authored `scene` of `project` with 1px grid
/// seams and the grid overlay, NPCs and animals at their authored positions, and items hidden
/// under the player and NPCs. `content` supplies the merged crop, node, machine and species
/// definitions (`farm_sim::state::create_content_from_project`). Edit Mode then calls
/// [`crate::apply_graphics`] with [`crate::GraphicsSource::from_project`], tick 0, not moving.
pub fn editor_snapshot(
    project: &GameProject,
    content: &GameContent,
    scene: &Scene,
    tile_size: f64,
    padding: f64,
) -> WorldSnapshot {
    let scene_npcs: Vec<_> = project.npcs.iter().filter(|npc| npc.scene_id == scene.id).collect();
    let player = &project.player;
    let occupied = |x: usize, y: usize| {
        let (x, y) = (x as i32, y as i32);
        (player.scene_id == scene.id && units::tile_of(player.x) == x && units::tile_of(player.y) == y)
            || scene_npcs.iter().any(|npc| npc.x == units::tiles(x) && npc.y == units::tiles(y))
    };
    let tiles = build_tiles(scene, content, occupied);

    let mut npcs: Vec<SnapshotEntity> = scene_npcs
        .iter()
        .map(|npc| SnapshotEntity {
            x: tiles_of(npc.x),
            y: tiles_of(npc.y),
            image_url: npc.custom_image.as_deref().map(Arc::from),
            appearance: Some(npc.appearance.clone()),
            ..SnapshotEntity::default()
        })
        .collect();
    npcs.extend(project.animals.iter().filter(|animal| animal.scene_id == scene.id).map(|animal| SnapshotEntity {
        x: tiles_of(animal.x),
        y: tiles_of(animal.y),
        color: last_by_id(&content.animal_species, &animal.species_id, |d| &d.id).map(|d| d.color.clone()),
        kind: "animal".to_owned(),
        species_id: Some(animal.species_id.clone()),
        ..SnapshotEntity::default()
    }));

    // The player marker only belongs on its own scene.
    let on_scene = player.scene_id == scene.id;
    WorldSnapshot {
        width: scene.width,
        height: scene.height,
        tile_size,
        padding,
        tile_gap: Some(1.0),
        camera: None,
        pixel_art: None,
        grid_overlay: true,
        tick: 0.0,
        atmosphere: None,
        tiles,
        npcs,
        player: SnapshotEntity {
            x: if on_scene { tiles_of(player.x) } else { -1000.0 },
            y: if on_scene { tiles_of(player.y) } else { -1000.0 },
            direction: if player.direction.is_empty() { directions::DOWN.to_owned() } else { player.direction.clone() },
            image_url: project.player_custom_image.as_deref().map(Arc::from),
            ..SnapshotEntity::default()
        },
        pops: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::Value;

    fn starter() -> GameProject {
        let fixture: Value =
            serde_json::from_str(include_str!("../../../fixtures/golden/content/starter-farm.json")).unwrap();
        serde_json::from_value(fixture["project"].clone()).unwrap()
    }

    #[test]
    fn snapshot_is_read_only_and_tracks_crop_maturity() {
        let project = starter();
        let content = farm_sim::state::create_content_from_project(&project);
        let mut state = farm_sim::state::create_game_state(&project, Some("snapshot"));
        let mut crop = crops::create_planted_crop("wheat", 1, false);
        crop.days_grown = Some(99);
        state.world.scenes[0].tiles[0][0].crop = Some(crop);
        let before = farm_sim::hash_state(&state);
        let options = SnapshotOptions { tile_size: 32.0, padding: 12.0, pixel_x: Some(15.5), ..Default::default() };
        let view = shell_snapshot(&content, &state, &state.world.scenes[0], &options);
        assert!(view.tiles[0][0].crop.as_ref().unwrap().mature);
        assert_eq!(view.player.pixel_x, Some(15.5));
        assert_eq!(view.tiles.len(), state.world.scenes[0].height as usize);
        assert_eq!(view.tile_gap, Some(0.0));
        assert!(!view.grid_overlay);
        assert!(view.atmosphere.is_some());
        assert_eq!(farm_sim::hash_state(&state), before);
    }

    #[test]
    fn shell_snapshot_maps_layers_nodes_and_entities() {
        let project = starter();
        let content = farm_sim::state::create_content_from_project(&project);
        let state = farm_sim::state::create_game_state(&project, None);
        let scene = state.world.scenes.iter().find(|s| s.id == state.player.scene_id).unwrap();
        let view = shell_snapshot(&content, &state, scene, &SnapshotOptions { tile_size: 32.0, ..Default::default() });
        assert_eq!((view.width, view.height), (scene.width as i32, scene.height as i32));
        assert!(view.tiles.iter().all(|row| row.len() == scene.width as usize));
        let expected_npcs = content.npcs.iter().filter(|n| n.scene_id == scene.id).count()
            + state.animals.iter().filter(|a| a.scene_id == scene.id).count();
        assert_eq!(view.npcs.len(), expected_npcs);
        let tile = &scene.tiles[0][0];
        let background = if tile.background.is_empty() { &tile.r#type } else { &tile.background };
        assert_eq!(&view.tiles[0][0].background, background);
        let (nx, ny, node_tile) = scene
            .tiles
            .iter()
            .enumerate()
            .find_map(|(y, row)| row.iter().enumerate().find(|(_, t)| t.node.is_some()).map(|(x, t)| (x, y, t)))
            .unwrap();
        let node = view.tiles[ny][nx].node.as_ref().unwrap();
        let type_id = &node_tile.node.as_ref().unwrap().type_id;
        assert_eq!(node.color, content.node_types.iter().rev().find(|n| &n.id == type_id).unwrap().color);
        assert!(!node.depleted);
    }

    #[test]
    fn editor_snapshot_uses_grid_seams_and_hides_items_under_the_player() {
        let mut project = starter();
        let content = farm_sim::state::create_content_from_project(&project);
        let scene_index = project.scenes.iter().position(|s| s.id == project.player.scene_id).unwrap();
        let (px, py) = (units::tile_of(project.player.x) as usize, units::tile_of(project.player.y) as usize);
        let item = project.items[0].clone();
        project.scenes[scene_index].tiles[py][px].item = Some(item.clone());
        project.scenes[scene_index].tiles[0][1].item = Some(item);
        let scene = project.scenes[scene_index].clone();
        let view = editor_snapshot(&project, &content, &scene, 28.0, 12.0);
        assert_eq!(view.tile_gap, Some(1.0));
        assert!(view.grid_overlay);
        assert_eq!(view.tile_size, 28.0);
        assert!(view.atmosphere.is_none());
        assert!(view.tiles[py][px].item.is_none());
        assert!(view.tiles[0][1].item.is_some());
        // Off-scene players are parked far away.
        let mut other = scene.clone();
        other.id = "elsewhere".into();
        let off = editor_snapshot(&project, &content, &other, 28.0, 12.0);
        assert_eq!((off.player.x, off.player.y), (-1000.0, -1000.0));
    }

    #[test]
    fn missing_cells_fall_back_to_grass_and_animals_face_a_stable_direction() {
        let project = starter();
        let content = farm_sim::state::create_content_from_project(&project);
        let mut state = farm_sim::state::create_game_state(&project, None);
        let scene_id = state.world.scenes[0].id.clone();
        state.world.scenes[0].tiles.pop();
        state.animals.push(farm_sim::schema::AnimalState {
            id: "cow-\u{1F404}".into(),
            species_id: "missing-species".into(),
            scene_id,
            x: 2,
            y: 3,
            ..Default::default()
        });
        let view = shell_snapshot(&content, &state, &state.world.scenes[0], &SnapshotOptions::default());
        assert_eq!(view.tiles.last().unwrap()[0].background, "grass");
        let cow = view.npcs.last().unwrap();
        assert_eq!(cow.kind, "animal");
        assert_eq!(cow.color, None);
        // Length 6 in UTF-16 (the emoji is a surrogate pair), position sum 5.
        let expected = directions::ALL[(BuiltinArt::hash(6, 5) % 4) as usize];
        assert_eq!(cow.direction, expected);
    }
}
