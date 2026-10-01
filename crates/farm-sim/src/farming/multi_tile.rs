//! Multi-tile crops (the built-in 2×2 pumpkin and cauliflower): one crop spread over several
//! tiles that share a `multiTileId`. The tiles act as one crop: watering any of them waters the
//! crop, a harvest pays once and clears or regrows every tile, the scythe clears them all, and
//! overnight they grow, wither and fall to a storm together.

use crate::engine_types::EngineContext;
use crate::schema::{GameState, Scene};
use indexmap::IndexMap;

/// The grid positions `(x, y)` of every tile of the crop on `(x, y)` in `scene`, the root tile
/// first, then row by row. A single-tile crop (or an empty tile) is just `(x, y)`.
///
/// The search covers the footprint the crop's definition allows around the tile (the whole
/// scene when the definition no longer says), and reads the grid with bounds checks.
pub fn group_positions(ctx: &EngineContext, scene: &Scene, x: usize, y: usize) -> Vec<(usize, usize)> {
    let Some(crop) = scene.tiles.get(y).and_then(|row| row.get(x)).and_then(|tile| tile.crop.as_ref()) else {
        return vec![(x, y)];
    };
    let Some(group_id) = crop.multi_tile_id.as_deref() else {
        return vec![(x, y)];
    };
    let widest = scene.tiles.iter().map(Vec::len).max().unwrap_or(0);
    let (rows, columns) = match ctx.content.crops.get(&crop.r#type).and_then(|def| def.multi_tile.as_ref()) {
        Some(size) => {
            let (width, height) = (size.width.max(1) as usize, size.height.max(1) as usize);
            (
                y.saturating_sub(height - 1)..=y.saturating_add(height - 1),
                x.saturating_sub(width - 1)..=x.saturating_add(width - 1),
            )
        }
        None => (0..=scene.tiles.len(), 0..=widest),
    };
    let mut positions = Vec::new();
    let mut root = None;
    for ty in rows {
        let Some(row) = scene.tiles.get(ty) else { continue };
        for tx in columns.clone() {
            let Some(member) = row
                .get(tx)
                .and_then(|tile| tile.crop.as_ref())
                .filter(|c| c.multi_tile_id.as_deref() == Some(group_id))
            else {
                continue;
            };
            if member.is_multi_tile_root == Some(true) && root.is_none() {
                root = Some(positions.len());
            }
            positions.push((tx, ty));
        }
    }
    if let Some(index) = root {
        let root = positions.remove(index);
        positions.insert(0, root);
    }
    if positions.is_empty() {
        positions.push((x, y));
    }
    positions
}

/// Tiles per multi-tile crop, keyed by (scene index, `multiTileId`).
pub type GroupSizes = IndexMap<(usize, String), usize>;

fn group_sizes(state: &GameState) -> GroupSizes {
    let mut sizes = GroupSizes::new();
    for (scene_index, scene) in state.world.scenes.iter().enumerate() {
        for tile in scene.tiles.iter().flatten() {
            if let Some(id) = tile.crop.as_ref().and_then(|crop| crop.multi_tile_id.as_ref()) {
                *sizes.entry((scene_index, id.clone())).or_default() += 1;
            }
        }
    }
    sizes
}

/// Before the nightly world pass: a multi-tile crop is watered when any of its tiles is
/// (older saves, or watering from before this rule, may have watered only some), so its tiles
/// grow alike. Returns the tiles per crop for [`after_night`].
pub fn before_night(state: &mut GameState) -> GroupSizes {
    let sizes = group_sizes(state);
    if sizes.is_empty() {
        return sizes;
    }
    for scene in &mut state.world.scenes {
        let watered: Vec<String> = scene
            .tiles
            .iter()
            .flatten()
            .filter_map(|tile| tile.crop.as_ref())
            .filter(|crop| crop.watered && crop.withered != Some(true))
            .filter_map(|crop| crop.multi_tile_id.clone())
            .collect();
        if watered.is_empty() {
            continue;
        }
        for crop in scene.tiles.iter_mut().flatten().filter_map(|tile| tile.crop.as_mut()) {
            if crop.withered != Some(true) && crop.multi_tile_id.as_ref().is_some_and(|id| watered.contains(id)) {
                crop.watered = true;
            }
        }
    }
    sizes
}

/// After the nightly world pass: a multi-tile crop that lost a tile overnight (storm damage
/// rolls per tile) is destroyed whole, so no half crop is left behind.
pub fn after_night(state: &mut GameState, before: &GroupSizes) {
    if before.is_empty() {
        return;
    }
    let after = group_sizes(state);
    for ((scene_index, id), count) in before {
        if after.get(&(*scene_index, id.clone())).copied().unwrap_or(0) == *count {
            continue;
        }
        let Some(scene) = state.world.scenes.get_mut(*scene_index) else { continue };
        for tile in scene.tiles.iter_mut().flatten() {
            if tile.crop.as_ref().is_some_and(|crop| crop.multi_tile_id.as_ref() == Some(id)) {
                tile.crop = None;
            }
        }
    }
}
