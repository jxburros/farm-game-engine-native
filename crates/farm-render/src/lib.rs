//! Read-only presentation of live simulation state. Compatibility snapshots match the
//! native Skia host's `WorldSnapshot`; GPU execution and art decoration are separate work.
#![forbid(unsafe_code)]

use farm_sim::farming::crops;
use farm_sim::schema::{directions, GameContent, GameState, Scene};
use farm_sim::world::tiles;
use serde::Deserialize;
use serde_json::{json, Value};

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SnapshotOptions {
    pub tile_size: f64,
    pub padding: f64,
    pub pixel_x: Option<f64>,
    pub pixel_y: Option<f64>,
    pub camera: Option<Camera>,
}

#[derive(Debug, Clone, serde::Serialize, Deserialize)]
pub struct Camera {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
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

// BuiltinArt.Hash: wrapping arithmetic preserves the original uint32 variant selection.
fn art_hash(x: u32, y: u32) -> u32 {
    let mut h = x.wrapping_mul(0x9E37_79B1) ^ y.wrapping_mul(0x85EB_CA77);
    h ^= h >> 15;
    h = h.wrapping_mul(0x2C1B_3C6D);
    h ^= h >> 12;
    h = h.wrapping_mul(0x297A_2D39);
    h ^ (h >> 15)
}

/// Port of `ShellSnapshot.BuildShellSnapshot`: no simulation mutation, I/O or OS state.
/// Art bindings and transient pops are decorated by the host after this projection.
pub fn snapshot(content: &GameContent, state: &GameState, scene: &Scene, options: &SnapshotOptions) -> Value {
    let mut rows = Vec::new();
    for y in 0..scene.height.max(0.0).ceil() as usize {
        let mut row = Vec::new();
        for x in 0..scene.width.max(0.0).ceil() as usize {
            let empty = tiles::create_empty_tile(x as f64, y as f64, "grass");
            let tile = scene.tiles.get(y).and_then(|row| row.get(x)).unwrap_or(&empty);
            let background = if tile.background.is_empty() { &tile.r#type } else { &tile.background };
            let crop = tile.crop.as_ref().and_then(|crop| {
                content.crops.get(&crop.r#type).map(|definition| {
                    json!({
                        "colorIndex": crop.stage.max(0.0) as i32,
                        "mature": crops::is_crop_mature_by_days(crop, definition),
                        "withered": crop.withered == Some(true), "cropId": crop.r#type,
                        "stages": definition.stages.max(0.0) as i32,
                    })
                })
            });
            let node = tile.node.as_ref().map(|node| json!({
                "color": content.node_types.iter().rev().find(|def| def.id == node.type_id).map_or("#7a5a3a", |def| def.color.as_str()),
                "depleted": node.remaining_health <= 0.0, "typeId": node.type_id,
            }));
            let machine = tile.machine.as_ref().map(|machine| json!({
                "color": content.machine_types.iter().rev().find(|def| def.id == machine.type_id).map_or("#9a7b4f", |def| def.color.as_str()),
                "working": machine.processing.is_some(),
                "outputReady": machine.output.as_ref().is_some_and(|output| !output.is_empty()), "typeId": machine.type_id,
            }));
            row.push(json!({
                "background": background, "overlay": tile.overlay, "object": tile.object,
                "imageUrl": tile.custom_image, "tilled": background == "soil",
                "watered": background == "soil" && (tile.soil_state.as_deref() == Some("watered") || tile.soil_moisture > 0.0),
                "fertilized": background == "soil" && (tile.soil_state.as_deref() == Some("fertilized") || tile.soil_fertility > 0.0),
                "crop": crop, "node": node, "machine": machine, "ladderDown": tile.ladder_down == Some(true),
                "item": tile.item.as_ref().map(|item| json!({"imageUrl": item.custom_image, "itemType": item.r#type})),
            }));
        }
        rows.push(row);
    }
    let mut npcs = Vec::new();
    for npc in &content.npcs {
        let live = state.npcs.get(&npc.id);
        if live.map_or(&npc.scene_id, |live| &live.scene_id) != &scene.id {
            continue;
        }
        let (x, y) = live.map_or((npc.x, npc.y), |live| (live.x, live.y));
        let next = live.and_then(|live| live.path.as_ref()).and_then(|path| path.first());
        npcs.push(json!({
            "x": x, "y": y, "imageUrl": npc.custom_image, "appearance": npc.appearance,
            "direction": next.map_or(directions::DOWN, |next| direction_toward(x, y, next.x, next.y)),
            "moving": next.is_some(),
        }));
    }
    for animal in state.animals.iter().filter(|animal| animal.scene_id == scene.id) {
        npcs.push(json!({
            "x": animal.x, "y": animal.y, "kind": "animal", "speciesId": animal.species_id,
            "color": content.animal_species.iter().rev().find(|def| def.id == animal.species_id).map(|def| &def.color),
            // .NET string.Length counts UTF-16 units (including surrogate pairs).
            "direction": directions::ALL[(art_hash(animal.id.encode_utf16().count() as u32, (animal.x + animal.y) as i32 as u32) % 4) as usize],
        }));
    }
    json!({
        "width": scene.width as i32, "height": scene.height as i32,
        "tileSize": options.tile_size, "padding": options.padding, "tileGap": 0,
        "camera": options.camera, "gridOverlay": false, "tick": state.clock.tick,
        "atmosphere": {
            "timeMinutes": state.clock.time_minutes, "weatherId": state.clock.weather_id, "season": state.clock.season,
            "weatherOverlay": content.weather.types.iter().find(|weather| weather.id == state.clock.weather_id).and_then(|weather| weather.overlay.as_ref()),
        },
        "tiles": rows, "npcs": npcs,
        "player": {
            "x": state.player.x, "y": state.player.y, "direction": state.player.direction,
            "pixelX": options.pixel_x, "pixelY": options.pixel_y,
            "moving": state.player.move_intent.dx != 0.0 || state.player.move_intent.dy != 0.0,
        },
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn snapshot_is_read_only_and_tracks_crop_maturity() {
        let fixture: Value =
            serde_json::from_str(include_str!("../../../fixtures/golden/content/starter-farm.json")).unwrap();
        let project = serde_json::from_value(fixture["project"].clone()).unwrap();
        let content = farm_sim::state::create_content_from_project(&project);
        let mut state = farm_sim::state::create_game_state(&project, Some("snapshot"));
        let mut crop = crops::create_planted_crop("wheat", 1.0, false);
        crop.days_grown = Some(99.0);
        state.world.scenes[0].tiles[0][0].crop = Some(crop);
        let before = farm_sim::hash_state(&state);
        let options =
            SnapshotOptions { tile_size: 32.0, padding: 12.0, pixel_x: Some(15.5), pixel_y: None, camera: None };
        let view = snapshot(&content, &state, &state.world.scenes[0], &options);
        assert_eq!(view["tiles"][0][0]["crop"]["mature"], true);
        assert_eq!(view["player"]["pixelX"], 15.5);
        assert_eq!(view["tiles"].as_array().unwrap().len(), state.world.scenes[0].height as usize);
        assert_eq!(farm_sim::hash_state(&state), before);
    }
}
