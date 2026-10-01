//! Tiles and scene grids (port of `World/Tiles.cs` / world/tiles.ts). Pure: every function
//! returns new tiles.
//!
//! Tile layer rules — extracted from src/lib/game-helpers.ts, behavior-identical
//! (characterization-tested).

use crate::schema::{Scene, Tile, VisualRef, MAX_SCENE_SIZE};
use indexmap::IndexSet;
use std::collections::VecDeque;

/// Returns `"background"`, `"overlay"` or `"object"`.
pub fn classify_tile_type(tile_type: &str) -> String {
    match tile_type {
        "path" => "overlay".to_owned(),
        "wall" | "door" => "object".to_owned(),
        _ => "background".to_owned(),
    }
}

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

/// Clone a scene's tile grid (rows and tiles) for immutable updates.
pub fn clone_tiles(tiles: &[Vec<Tile>]) -> Vec<Vec<Tile>> {
    tiles.to_vec()
}

/// TS `tiles[0]?.length ?? 0`.
fn row_width(tiles: &[Vec<Tile>]) -> i64 {
    tiles.first().map_or(0, |row| row.len() as i64)
}

/// `tiles[y][x]` when the grid has that tile (rows may be ragged).
fn tile_in(tiles: &[Vec<Tile>], x: i64, y: i64) -> Option<&Tile> {
    tiles.get(usize::try_from(y).ok()?)?.get(usize::try_from(x).ok()?)
}

/// TS `` `${x},${y}` `` (the flood-fill `seen` key).
fn key(x: i64, y: i64) -> String {
    format!("{x},{y}")
}

/// The inclusive corner range clamped to the grid, per axis: `max(0, min(a, b))` to
/// `min(size − 1, max(a, b))`.
fn clamped_range(a: i32, b: i32, size: i64) -> (i64, i64) {
    let (a, b) = (i64::from(a), i64::from(b));
    (a.min(b).max(0), (size - 1).min(a.max(b)))
}

/// Paint a rectangle (inclusive corners) with a tile type. Returns new tiles.
pub fn paint_rect(
    tiles: &[Vec<Tile>],
    x1: i32,
    y1: i32,
    x2: i32,
    y2: i32,
    tile_type: &str,
    visual: Option<&VisualRef>,
) -> Vec<Vec<Tile>> {
    let mut next = clone_tiles(tiles);
    let (min_x, max_x) = clamped_range(x1, x2, row_width(tiles));
    let (min_y, max_y) = clamped_range(y1, y2, tiles.len() as i64);
    for y in min_y..=max_y {
        for x in min_x..=max_x {
            // A ragged row (shorter than the first) has no tile here.
            let Some(tile) = next.get_mut(y as usize).and_then(|row| row.get_mut(x as usize)) else { continue };
            let mut painted = set_tile_layer(tile, tile_type, visual);
            painted.crop = None;
            painted.node = None;
            *tile = painted;
        }
    }
    next
}

/// Flood-fill the contiguous region of the clicked tile's `type` with a new type
/// (4-directional). Returns new tiles; no-op when types match.
pub fn flood_fill(
    tiles: &[Vec<Tile>],
    start_x: i32,
    start_y: i32,
    tile_type: &str,
    visual: Option<&VisualRef>,
) -> Vec<Vec<Tile>> {
    let height = tiles.len() as i64;
    let width = row_width(tiles);
    let (start_x, start_y) = (i64::from(start_x), i64::from(start_y));
    if start_y < 0 || start_y >= height || start_x < 0 || start_x >= width {
        return tiles.to_vec();
    }
    let Some(source_type) = tile_in(tiles, start_x, start_y).map(|tile| tile.r#type.clone()) else {
        return tiles.to_vec();
    };
    if source_type == tile_type && visual.is_none() {
        return tiles.to_vec();
    }

    let mut next = clone_tiles(tiles);
    let mut queue: VecDeque<(i64, i64)> = VecDeque::new();
    queue.push_back((start_x, start_y));
    let mut seen: IndexSet<String> = IndexSet::new();
    seen.insert(key(start_x, start_y));
    const DIRS: [(i64, i64); 4] = [(0, -1), (0, 1), (-1, 0), (1, 0)];
    while let Some((x, y)) = queue.pop_front() {
        let Some(tile) = next.get_mut(y as usize).and_then(|row| row.get_mut(x as usize)) else { continue };
        if tile.r#type != source_type {
            continue;
        }
        let mut filled = set_tile_layer(tile, tile_type, visual);
        filled.crop = None;
        filled.node = None;
        *tile = filled;
        for (dx, dy) in DIRS {
            let nx = x + dx;
            let ny = y + dy;
            let neighbor_key = key(nx, ny);
            if nx < 0 || nx >= width || ny < 0 || ny >= height || seen.contains(&neighbor_key) {
                continue;
            }
            if tile_in(&next, nx, ny).is_none_or(|tile| tile.r#type != source_type) {
                continue;
            }
            seen.insert(neighbor_key);
            queue.push_back((nx, ny));
        }
    }
    next
}

/// Extract a deep-copied tile region (inclusive corners, clamped).
pub fn copy_tile_region(tiles: &[Vec<Tile>], x1: i32, y1: i32, x2: i32, y2: i32) -> Vec<Vec<Tile>> {
    let (min_x, max_x) = clamped_range(x1, x2, row_width(tiles));
    let (min_y, max_y) = clamped_range(y1, y2, tiles.len() as i64);
    let mut region = Vec::new();
    for y in min_y..=max_y {
        // `JsonDefaults.DeepClone(tile)`: a Rust clone is already deep.
        region.push((min_x..=max_x).filter_map(|x| tile_in(tiles, x, y).cloned()).collect());
    }
    region
}

/// Stamp a copied region with its top-left at (x, y), clamped to bounds.
pub fn paste_tile_region(tiles: &[Vec<Tile>], region: &[Vec<Tile>], x: i32, y: i32) -> Vec<Vec<Tile>> {
    let mut next = clone_tiles(tiles);
    let height = tiles.len() as i64;
    let width = row_width(tiles);
    for (dy, region_row) in region.iter().enumerate() {
        for (dx, source) in region_row.iter().enumerate() {
            let tx = i64::from(x) + dx as i64;
            let ty = i64::from(y) + dy as i64;
            if tx < 0 || tx >= width || ty < 0 || ty >= height {
                continue;
            }
            let Some(tile) = next.get_mut(ty as usize).and_then(|row| row.get_mut(tx as usize)) else { continue };
            let mut pasted = source.clone();
            pasted.x = tx as i32;
            pasted.y = ty as i32;
            *tile = pasted;
        }
    }
    next
}
