//! Tiles and scene grids (port of `World/Tiles.cs` / world/tiles.ts). Pure: every function
//! returns new tiles.
//!
//! Tile layer rules — extracted from src/lib/game-helpers.ts, behavior-identical
//! (characterization-tested).

use crate::js;
use crate::schema::{Scene, Tile, VisualRef};
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

pub fn create_empty_tile(x: f64, y: f64, tile_type: &str) -> Tile {
    let layer = classify_tile_type(tile_type);
    Tile {
        x,
        y,
        r#type: tile_type.to_owned(),
        background: if layer == "background" { tile_type.to_owned() } else { "grass".to_owned() },
        overlay: if layer == "overlay" { Some(tile_type.to_owned()) } else { None },
        object: if layer == "object" { Some(tile_type.to_owned()) } else { None },
        collision: tile_type == "wall",
        soil_moisture: 0.0,
        soil_fertility: 0.0,
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

pub fn create_empty_scene(id: &str, name: &str, width: f64, height: f64) -> Scene {
    let mut tiles = Vec::new();
    let mut y = 0.0;
    while y < height {
        let mut row = Vec::new();
        let mut x = 0.0;
        while x < width {
            row.push(create_empty_tile(x, y, "grass"));
            x += 1.0;
        }
        tiles.push(row);
        y += 1.0;
    }

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

/// Clone a scene's tile grid (rows and tiles) for immutable updates.
pub fn clone_tiles(tiles: &[Vec<Tile>]) -> Vec<Vec<Tile>> {
    tiles.to_vec()
}

/// JS `Math.min`: NaN-propagating, unlike `f64::min`.
fn js_min(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() {
        f64::NAN
    } else {
        a.min(b)
    }
}

/// JS `Math.max`: NaN-propagating, unlike `f64::max`.
fn js_max(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() {
        f64::NAN
    } else {
        a.max(b)
    }
}

/// TS `tiles[0]?.length ?? 0`.
fn row_width(tiles: &[Vec<Tile>]) -> f64 {
    tiles.first().map_or(0.0, |row| row.len() as f64)
}

/// TS `` `${x},${y}` `` (the flood-fill `seen` key).
fn key(x: f64, y: f64) -> String {
    format!("{},{}", js::num(x), js::num(y))
}

/// Paint a rectangle (inclusive corners) with a tile type. Returns new tiles.
pub fn paint_rect(
    tiles: &[Vec<Tile>],
    x1: f64,
    y1: f64,
    x2: f64,
    y2: f64,
    tile_type: &str,
    visual: Option<&VisualRef>,
) -> Vec<Vec<Tile>> {
    let mut next = clone_tiles(tiles);
    let min_x = js_max(0.0, js_min(x1, x2));
    let max_x = js_min(row_width(tiles) - 1.0, js_max(x1, x2));
    let min_y = js_max(0.0, js_min(y1, y2));
    let max_y = js_min(tiles.len() as f64 - 1.0, js_max(y1, y2));
    let mut y = min_y;
    while y <= max_y {
        let mut x = min_x;
        while x <= max_x {
            let row = &mut next[y as usize];
            let mut painted = set_tile_layer(&row[x as usize], tile_type, visual);
            painted.crop = None;
            painted.node = None;
            row[x as usize] = painted;
            x += 1.0;
        }
        y += 1.0;
    }
    next
}

/// Flood-fill the contiguous region of the clicked tile's `type` with a new type
/// (4-directional). Returns new tiles; no-op when types match.
pub fn flood_fill(
    tiles: &[Vec<Tile>],
    start_x: f64,
    start_y: f64,
    tile_type: &str,
    visual: Option<&VisualRef>,
) -> Vec<Vec<Tile>> {
    let height = tiles.len() as f64;
    let width = row_width(tiles);
    if start_y < 0.0 || start_y >= height || start_x < 0.0 || start_x >= width {
        return tiles.to_vec();
    }
    let source_type = tiles[start_y as usize][start_x as usize].r#type.clone();
    if source_type == tile_type && visual.is_none() {
        return tiles.to_vec();
    }

    let mut next = clone_tiles(tiles);
    let mut queue: VecDeque<(f64, f64)> = VecDeque::new();
    queue.push_back((start_x, start_y));
    let mut seen: IndexSet<String> = IndexSet::new();
    seen.insert(key(start_x, start_y));
    const DIRS: [(f64, f64); 4] = [(0.0, -1.0), (0.0, 1.0), (-1.0, 0.0), (1.0, 0.0)];
    while let Some((x, y)) = queue.pop_front() {
        if next[y as usize][x as usize].r#type != source_type {
            continue;
        }
        let mut filled = set_tile_layer(&next[y as usize][x as usize], tile_type, visual);
        filled.crop = None;
        filled.node = None;
        next[y as usize][x as usize] = filled;
        for (dx, dy) in DIRS {
            let nx = x + dx;
            let ny = y + dy;
            let neighbor_key = key(nx, ny);
            if nx < 0.0 || nx >= width || ny < 0.0 || ny >= height || seen.contains(&neighbor_key) {
                continue;
            }
            if next[ny as usize][nx as usize].r#type != source_type {
                continue;
            }
            seen.insert(neighbor_key);
            queue.push_back((nx, ny));
        }
    }
    next
}

/// Extract a deep-copied tile region (inclusive corners, clamped).
pub fn copy_tile_region(tiles: &[Vec<Tile>], x1: f64, y1: f64, x2: f64, y2: f64) -> Vec<Vec<Tile>> {
    let min_x = js_max(0.0, js_min(x1, x2));
    let max_x = js_min(row_width(tiles) - 1.0, js_max(x1, x2));
    let min_y = js_max(0.0, js_min(y1, y2));
    let max_y = js_min(tiles.len() as f64 - 1.0, js_max(y1, y2));
    let mut region = Vec::new();
    let mut y = min_y;
    while y <= max_y {
        let mut row = Vec::new();
        let mut x = min_x;
        while x <= max_x {
            // `JsonDefaults.DeepClone(tile)`: a Rust clone is already deep.
            row.push(tiles[y as usize][x as usize].clone());
            x += 1.0;
        }
        region.push(row);
        y += 1.0;
    }
    region
}

/// Stamp a copied region with its top-left at (x, y), clamped to bounds.
pub fn paste_tile_region(tiles: &[Vec<Tile>], region: &[Vec<Tile>], x: f64, y: f64) -> Vec<Vec<Tile>> {
    let mut next = clone_tiles(tiles);
    let height = tiles.len() as f64;
    let width = row_width(tiles);
    for (dy, region_row) in region.iter().enumerate() {
        for (dx, source) in region_row.iter().enumerate() {
            let tx = x + dx as f64;
            let ty = y + dy as f64;
            if tx < 0.0 || tx >= width || ty < 0.0 || ty >= height {
                continue;
            }
            let mut pasted = source.clone();
            pasted.x = tx;
            pasted.y = ty;
            next[ty as usize][tx as usize] = pasted;
        }
    }
    next
}
