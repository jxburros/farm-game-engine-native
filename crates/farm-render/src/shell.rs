//! State → world snapshot, ported from ShellSnapshot.cs and game-shell/snapshot.ts.
use crate::snapshot::*;
use farm_sim::farming::crops;
use farm_sim::schema::{directions, GameContent, GameProject, GameState, Scene};
use farm_sim::world::tiles;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SnapshotOptions {
    pub tile_size: f64,
    pub padding: f64,
    pub pixel_x: Option<f64>,
    pub pixel_y: Option<f64>,
    pub camera: Option<SnapshotCamera>,
}

pub fn build_play(content: &GameContent, state: &GameState, scene: &Scene, options: &SnapshotOptions) -> WorldSnapshot {
    let mut npcs: Vec<_> = content
        .npcs
        .iter()
        .filter_map(|npc| {
            let live = state.npcs.get(&npc.id);
            if live.map_or(npc.scene_id.as_str(), |s| s.scene_id.as_str()) != scene.id {
                return None;
            }
            let (x, y) = live.map_or((npc.x, npc.y), |s| (s.x, s.y));
            let next = live.and_then(|s| s.path.as_ref()).and_then(|path| path.first());
            Some(SnapshotEntity {
                x,
                y,
                image_url: npc.custom_image.clone(),
                appearance: Some(npc.appearance.clone()),
                direction: next.map_or("down", |p| direction_toward(x, y, p.x, p.y)).to_owned(),
                moving: next.is_some(),
                ..Default::default()
            })
        })
        .collect();
    npcs.extend(state.animals.iter().filter(|a| a.scene_id == scene.id).map(|animal| {
        SnapshotEntity {
            x: animal.x,
            y: animal.y,
            kind: "animal".to_owned(),
            species_id: Some(animal.species_id.clone()),
            color: content.animal_species.iter().rev().find(|s| s.id == animal.species_id).map(|s| s.color.clone()),
            // C# string.Length counts UTF-16 code units, including surrogate pairs.
            direction: directions::ALL
                [(art_hash(animal.id.encode_utf16().count() as i32, (animal.x + animal.y) as i32) % 4) as usize]
                .to_owned(),
            ..Default::default()
        }
    }));
    WorldSnapshot {
        width: scene.width as i32,
        height: scene.height as i32,
        tile_size: options.tile_size,
        padding: options.padding,
        tile_gap: Some(0.0),
        camera: options.camera.clone(),
        tick: state.clock.tick,
        atmosphere: Some(SnapshotAtmosphere {
            time_minutes: state.clock.time_minutes,
            weather_id: Some(state.clock.weather_id.clone()),
            season: Some(state.clock.season.clone()),
            weather_overlay: content
                .weather
                .types
                .iter()
                .find(|w| w.id == state.clock.weather_id)
                .and_then(|w| w.overlay.clone()),
        }),
        tiles: build_tiles(content, scene, |_, _| false),
        npcs,
        player: SnapshotPlayer {
            entity: SnapshotEntity {
                x: state.player.x,
                y: state.player.y,
                direction: state.player.direction.clone(),
                moving: state.player.move_intent.dx != 0.0 || state.player.move_intent.dy != 0.0,
                ..Default::default()
            },
            pixel_x: options.pixel_x,
            pixel_y: options.pixel_y,
        },
        ..Default::default()
    }
}

pub fn build_editor(
    project: &GameProject,
    content: &GameContent,
    scene: &Scene,
    tile_size: f64,
    padding: f64,
) -> WorldSnapshot {
    let scene_npcs: Vec<_> = project.npcs.iter().filter(|n| n.scene_id == scene.id).collect();
    let occupied = |x: usize, y: usize| {
        (project.player.scene_id == scene.id
            && project.player.x.floor() == x as f64
            && project.player.y.floor() == y as f64)
            || scene_npcs.iter().any(|n| n.x == x as f64 && n.y == y as f64)
    };
    let mut npcs: Vec<_> = scene_npcs
        .iter()
        .map(|npc| SnapshotEntity {
            x: npc.x,
            y: npc.y,
            image_url: npc.custom_image.clone(),
            appearance: Some(npc.appearance.clone()),
            ..Default::default()
        })
        .collect();
    npcs.extend(project.animals.iter().filter(|a| a.scene_id == scene.id).map(|animal| SnapshotEntity {
        x: animal.x,
        y: animal.y,
        kind: "animal".to_owned(),
        species_id: Some(animal.species_id.clone()),
        color: content.animal_species.iter().rev().find(|s| s.id == animal.species_id).map(|s| s.color.clone()),
        ..Default::default()
    }));
    WorldSnapshot {
        width: scene.width as i32,
        height: scene.height as i32,
        tile_size,
        padding,
        tile_gap: Some(1.0),
        grid_overlay: true,
        tiles: build_tiles(content, scene, occupied),
        npcs,
        player: SnapshotPlayer {
            entity: SnapshotEntity {
                x: if project.player.scene_id == scene.id { project.player.x } else { -1000.0 },
                y: if project.player.scene_id == scene.id { project.player.y } else { -1000.0 },
                direction: if project.player.direction.is_empty() {
                    "down".to_owned()
                } else {
                    project.player.direction.clone()
                },
                image_url: project.player_custom_image.clone(),
                ..Default::default()
            },
            ..Default::default()
        },
        ..Default::default()
    }
}

fn build_tiles(
    content: &GameContent,
    scene: &Scene,
    hide_item: impl Fn(usize, usize) -> bool,
) -> Vec<Vec<SnapshotTile>> {
    (0..scene.height.max(0.0).ceil() as usize)
        .map(|y| {
            (0..scene.width.max(0.0).ceil() as usize)
                .map(|x| {
                    let empty = tiles::create_empty_tile(x as f64, y as f64, "grass");
                    let tile = scene.tiles.get(y).and_then(|row| row.get(x)).unwrap_or(&empty);
                    let background = if tile.background.is_empty() { &tile.r#type } else { &tile.background };
                    SnapshotTile {
                        background: background.clone(),
                        overlay: tile.overlay.clone(),
                        object: tile.object.clone(),
                        image_url: tile.custom_image.clone(),
                        tilled: background == "soil",
                        watered: background == "soil"
                            && (tile.soil_state.as_deref() == Some("watered") || tile.soil_moisture > 0.0),
                        fertilized: background == "soil"
                            && (tile.soil_state.as_deref() == Some("fertilized") || tile.soil_fertility > 0.0),
                        crop: tile.crop.as_ref().and_then(|crop| {
                            content.crops.get(&crop.r#type).map(|definition| SnapshotCrop {
                                color_index: crop.stage.max(0.0) as i32,
                                mature: crops::is_crop_mature_by_days(crop, definition),
                                withered: crop.withered == Some(true),
                                crop_id: Some(crop.r#type.clone()),
                                stages: definition.stages.max(0.0) as i32,
                                ..Default::default()
                            })
                        }),
                        node: tile.node.as_ref().map(|node| SnapshotNode {
                            color: content
                                .node_types
                                .iter()
                                .rev()
                                .find(|n| n.id == node.type_id)
                                .map_or("#7a5a3a", |n| n.color.as_str())
                                .to_owned(),
                            depleted: node.remaining_health <= 0.0,
                            type_id: Some(node.type_id.clone()),
                            ..Default::default()
                        }),
                        machine: tile.machine.as_ref().map(|machine| SnapshotMachine {
                            color: content
                                .machine_types
                                .iter()
                                .rev()
                                .find(|m| m.id == machine.type_id)
                                .map_or("#9a7b4f", |m| m.color.as_str())
                                .to_owned(),
                            working: machine.processing.is_some(),
                            output_ready: machine.output.as_ref().is_some_and(|out| !out.is_empty()),
                            type_id: Some(machine.type_id.clone()),
                            ..Default::default()
                        }),
                        ladder_down: tile.ladder_down == Some(true),
                        item: tile.item.as_ref().filter(|_| !hide_item(x, y)).map(|item| SnapshotItem {
                            image_url: item.custom_image.clone(),
                            item_type: Some(item.r#type.clone()),
                            ..Default::default()
                        }),
                        ..Default::default()
                    }
                })
                .collect()
        })
        .collect()
}

fn direction_toward(x: f64, y: f64, tx: f64, ty: f64) -> &'static str {
    let (dx, dy) = (tx - x, ty - y);
    if dx.abs() > dy.abs() {
        if dx < 0.0 {
            "left"
        } else {
            "right"
        }
    } else if dy < 0.0 {
        "up"
    } else {
        "down"
    }
}

fn art_hash(x: i32, y: i32) -> u32 {
    let mut h = (x as u32).wrapping_mul(0x9E3779B1) ^ (y as u32).wrapping_mul(0x85EBCA77);
    h ^= h >> 15;
    h = h.wrapping_mul(0x2C1B3C6D);
    h ^= h >> 12;
    h = h.wrapping_mul(0x297A2D39);
    h ^ (h >> 15)
}
