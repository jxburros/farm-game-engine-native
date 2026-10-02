//! Tiles and scene grids (port of `World/Tiles.cs` / world/tiles.ts). Pure: every function
//! returns new tiles.
//!
//! Tile layer rules — extracted from src/lib/game-helpers.ts, behavior-identical
//! (characterization-tested).

use crate::schema::{Scene, Tile, VisualRef, MAX_SCENE_SIZE};

/// Returns `"background"`, `"overlay"` or `"object"`.
pub fn classify_tile_type(tile_type: &str) -> String {
    match tile_type {
        "path" => "overlay".to_owned(),
        "wall" | "door" => "object".to_owned(),
        _ => "background".to_owned(),
    }
}

/// A plain tile of `tile_type`, the type routed to its layer (`classify_tile_type`).
pub fn create_empty_tile(x: i32, y: i32, tile_type: &str) -> Tile {
    let layer = classify_tile_type(tile_type);
    Tile {
        x,
        y,
        r#type: tile_type.to_owned(),
        background: if layer == "background" { tile_type.to_owned() } else { "grass".to_owned() },
        overlay: if layer == "overlay" { Some(tile_type.to_owned()) } else { None },
        object: if layer == "object" { Some(tile_type.to_owned()) } else { None },
        collision: tile_type == "wall",
        soil_moisture: 0,
        soil_fertility: 0,
        ..Tile::default()
    }
}

/// `tile` with `new_type` on its layer (and `visual` as that layer's art), keeping the other
/// layers.
pub fn set_tile_layer(tile: &Tile, new_type: &str, visual: Option<&VisualRef>) -> Tile {
    let layer = classify_tile_type(new_type);
    let mut updated = tile.clone();
    updated.r#type = new_type.to_owned();
    if visual.is_some() {
        updated.custom_image = None;
    }
    if visual.is_some() || tile.visuals.is_some() {
        // { ...tile.visuals, [layer]: visual }
        let mut visuals = tile.visuals.clone().unwrap_or_default();
        match layer.as_str() {
            "background" => visuals.background = visual.cloned(),
            "overlay" => visuals.overlay = visual.cloned(),
            _ => visuals.object = visual.cloned(),
        }
        updated.visuals = Some(visuals);
    }

    match layer.as_str() {
        "background" => updated.background = new_type.to_owned(),
        "overlay" => updated.overlay = Some(new_type.to_owned()),
        "object" => {
            updated.object = Some(new_type.to_owned());
            updated.collision = new_type == "wall";
        }
        _ => {}
    }

    updated
}

/// A `width` × `height` scene of grass.
pub fn create_empty_scene(id: &str, name: &str, width: i32, height: i32) -> Scene {
    let tiles =
        (0..height.max(0)).map(|y| (0..width.max(0)).map(|x| create_empty_tile(x, y, "grass")).collect()).collect();

    Scene {
        id: id.to_owned(),
        name: name.to_owned(),
        width,
        height,
        tiles,
        transitions: Vec::new(),
        npcs: Vec::new(),
        events: Vec::new(),
        ..Scene::default()
    }
}

/// Make a scene's grid match its size, so every tile inside `width × height` exists: the size is
/// clamped to `1..=MAX_SCENE_SIZE`, rows and columns beyond it are cut, and missing ones are
/// filled with grass. Returns whether anything changed. Runs wherever a world enters the engine
/// (new games, loaded saves, pack scenes): a hand-edited project, an import or a pack can carry a
/// grid of another size, and the simulation would otherwise read tiles that are not there.
pub fn normalize_scene_grid(scene: &mut Scene) -> bool {
    let width = scene.width.clamp(1, MAX_SCENE_SIZE);
    let height = scene.height.clamp(1, MAX_SCENE_SIZE);
    let mut changed = width != scene.width || height != scene.height;
    scene.width = width;
    scene.height = height;
    let (columns, rows) = (width as usize, height as usize);
    if scene.tiles.len() != rows {
        changed = true;
        scene.tiles.truncate(rows);
    }
    for (y, row) in scene.tiles.iter_mut().enumerate() {
        if row.len() != columns {
            changed = true;
            row.truncate(columns);
            let start = row.len();
            row.extend((start..columns).map(|x| create_empty_tile(x as i32, y as i32, "grass")));
        }
    }
    for y in scene.tiles.len()..rows {
        scene.tiles.push((0..columns).map(|x| create_empty_tile(x as i32, y as i32, "grass")).collect());
    }
    changed
}
