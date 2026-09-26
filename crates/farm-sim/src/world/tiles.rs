//! Tiles and scene grids (port of `World/Tiles.cs` / world/tiles.ts). Pure: every function
//! returns new tiles.

use crate::schema::{Scene, Tile, VisualRef};

pub fn classify_tile_type(tile_type: &str) -> String {
    let _ = tile_type;
    todo!("port Tiles.ClassifyTileType")
}

pub fn create_empty_tile(x: f64, y: f64, tile_type: &str) -> Tile {
    let _ = (x, y, tile_type);
    todo!("port Tiles.CreateEmptyTile")
}

pub fn set_tile_layer(tile: &Tile, new_type: &str, visual: Option<&VisualRef>) -> Tile {
    let _ = (tile, new_type, visual);
    todo!("port Tiles.SetTileLayer")
}

pub fn create_empty_scene(id: &str, name: &str, width: f64, height: f64) -> Scene {
    let _ = (id, name, width, height);
    todo!("port Tiles.CreateEmptyScene")
}

pub fn clone_tiles(tiles: &[Vec<Tile>]) -> Vec<Vec<Tile>> {
    tiles.to_vec()
}

pub fn paint_rect(
    tiles: &[Vec<Tile>],
    x1: f64,
    y1: f64,
    x2: f64,
    y2: f64,
    tile_type: &str,
    visual: Option<&VisualRef>,
) -> Vec<Vec<Tile>> {
    let _ = (tiles, x1, y1, x2, y2, tile_type, visual);
    todo!("port Tiles.PaintRect")
}

pub fn flood_fill(
    tiles: &[Vec<Tile>],
    start_x: f64,
    start_y: f64,
    tile_type: &str,
    visual: Option<&VisualRef>,
) -> Vec<Vec<Tile>> {
    let _ = (tiles, start_x, start_y, tile_type, visual);
    todo!("port Tiles.FloodFill")
}

pub fn copy_tile_region(tiles: &[Vec<Tile>], x1: f64, y1: f64, x2: f64, y2: f64) -> Vec<Vec<Tile>> {
    let _ = (tiles, x1, y1, x2, y2);
    todo!("port Tiles.CopyTileRegion")
}

pub fn paste_tile_region(tiles: &[Vec<Tile>], region: &[Vec<Tile>], x: f64, y: f64) -> Vec<Vec<Tile>> {
    let _ = (tiles, region, x, y);
    todo!("port Tiles.PasteTileRegion")
}
