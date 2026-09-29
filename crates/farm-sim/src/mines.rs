//! Mines (port of `Mines.cs` / mines.ts).
//!
//! Mining (M4f): procedurally generated floors, deterministic per (engine seed, floor number).
//! Floors are transient world scenes that are NOT synced back to the project — leaving and
//! re-entering regenerates the identical layout (state on a floor resets when you leave,
//! genre-standard and deliberate). Ladders down are revealed by breaking rocks; elevator
//! checkpoints every N floors let you skip ahead. Hazards v1 = the energy cost of swinging your
//! pickaxe (combat is an explicit non-goal until demanded).

use crate::effects::Effect;
use crate::engine_types::{Effects, EngineContext};
use crate::rng::{self, Rng};
use crate::schema::{GameState, MineBand, MineConfig, MineProgress, Scene, TileNode};
use crate::units;
use crate::world::tiles;
use serde_json::Value;

pub const MINE_SCENE_PREFIX: &str = "mine-floor-";

pub fn mine_floor_scene_id(floor: u32) -> String {
    format!("{MINE_SCENE_PREFIX}{floor}")
}

pub fn is_mine_scene(scene_id: &str) -> bool {
    scene_id.starts_with(MINE_SCENE_PREFIX)
}

fn band_for_floor(config: &MineConfig, floor: u32) -> Option<&MineBand> {
    config.bands.iter().find(|band| floor >= band.from_floor && floor <= band.to_floor).or_else(|| config.bands.last())
}

/// Deterministically generate a mine floor: walled border, entry ladder at the top-left corner
/// area, rocks from the depth band's weighted table.
pub fn generate_mine_floor(ctx: &EngineContext, engine_seed: &str, floor: u32) -> Scene {
    let config = &ctx.content.mine;
    let mut scene = tiles::create_empty_scene(
        &mine_floor_scene_id(floor),
        &format!("Mine — Floor {floor}"),
        i32::try_from(config.floor_width).unwrap_or(i32::MAX),
        i32::try_from(config.floor_height).unwrap_or(i32::MAX),
    );
    // TS: createRngState(hashStringToU32(...)) — the numeric-seed overload.
    let seed = rng::hash_string_to_u32(&format!("{engine_seed}:mine:{floor}"));
    let mut rng = Rng::new(rng::create_rng_state_from_u32(seed));

    // Cave look: floor tiles + wall border
    for y in 0..scene.height {
        for x in 0..scene.width {
            let border = x == 0 || y == 0 || x == scene.width - 1 || y == scene.height - 1;
            let tile = &mut scene.tiles[y as usize][x as usize];
            tile.r#type = if border { "wall" } else { "floor" }.to_owned();
            tile.background = "floor".to_owned();
            tile.overlay = None;
            tile.object = if border { Some("wall".to_owned()) } else { None };
            tile.collision = border;
        }
    }

    const ENTRY_X: i32 = 1;
    const ENTRY_Y: i32 = 1;
    let band = band_for_floor(config, floor);

    if let Some(band) = band {
        let weights: Vec<u32> = band.rocks.iter().map(|rock| rock.weight).collect();
        for y in 1..scene.height - 1 {
            for x in 1..scene.width - 1 {
                place_rock(ctx, band, &weights, &mut rng, &mut scene, x, y, x == ENTRY_X && y == ENTRY_Y);
            }
        }
    }

    // Mark the scene as generated so the project bridge skips it.
    scene.extra.insert("generated".to_owned(), Value::Bool(true));
    scene
}

/// One interior tile of the generation loop (each C# `continue` is an early return). Draws
/// the density roll, then the weighted rock pick, in that order.
#[allow(clippy::too_many_arguments)]
fn place_rock(
    ctx: &EngineContext,
    band: &MineBand,
    weights: &[u32],
    rng: &mut Rng,
    scene: &mut Scene,
    x: i32,
    y: i32,
    is_entry: bool,
) {
    if is_entry {
        return;
    }
    // The roll fails when draw / 2³² ≥ density / 1000.
    if u64::from(rng.next_u32()) * u64::from(units::MILLI_ONE) >= u64::from(band.density) * units::PROBABILITY_ONE {
        return;
    }
    let Some(rock) = rng.weighted(weights).and_then(|i| band.rocks.get(i)) else {
        return;
    };
    let node_type_id = &rock.node_type_id;
    let Some(node_def) = ctx.content.node_types.iter().find(|def| def.id == *node_type_id) else {
        return;
    };
    scene.tiles[y as usize][x as usize].node =
        Some(TileNode { type_id: node_type_id.clone(), remaining_health: node_def.health, ..TileNode::default() });
}

/// Enter the mine (from the configured entrance) or descend one floor.
pub fn descend_mine(ctx: &EngineContext, state: &mut GameState, to_floor: u32) -> Effects {
    let config = &ctx.content.mine;
    if !config.enabled {
        return Vec::new();
    }
    let floor = config.floors.min(to_floor).max(1);

    let scene_id = mine_floor_scene_id(floor);
    if !state.world.scenes.iter().any(|scene| scene.id == scene_id) {
        let generated = generate_mine_floor(ctx, &state.meta.engine_seed, floor);
        state.world.scenes.push(generated);
    }

    state.player.scene_id = scene_id.clone();
    state.player.x = units::tile_center(1);
    state.player.y = units::tile_center(1);
    state.mine = MineProgress { current_floor: floor, deepest_floor: state.mine.deepest_floor.max(floor) };

    // `floor % 0` is NaN in JS: never a checkpoint.
    let checkpoint = if floor.checked_rem(config.elevator_every) == Some(0) { " (elevator checkpoint)" } else { "" };
    vec![
        Effect::SceneChanged { scene_id, x: 1, y: 1 },
        Effect::message("info", format!("Mine — floor {floor}{checkpoint}")),
    ]
}

/// Leave the mine back to the entrance scene.
pub fn exit_mine(ctx: &EngineContext, state: &mut GameState) -> Effects {
    let config = &ctx.content.mine;
    let target_scene_id = config.entrance_scene_id.clone().unwrap_or_else(|| ctx.content.start_scene_id.clone());
    let Some(target) = state.world.scenes.iter().find(|scene| scene.id == target_scene_id) else {
        return Vec::new();
    };
    let x = config.entrance_x.unwrap_or_else(|| target.width.div_euclid(2));
    let y = config.entrance_y.unwrap_or_else(|| target.height.div_euclid(2));

    // Drop generated floors so they regenerate fresh next visit.
    state.world.scenes.retain(|scene| !is_mine_scene(&scene.id));
    state.player.scene_id = target_scene_id.clone();
    state.player.x = units::tile_center(x);
    state.player.y = units::tile_center(y);
    state.mine.current_floor = 0;

    vec![
        Effect::SceneChanged { scene_id: target_scene_id, x, y },
        Effect::message("info", "You climb back to the surface."),
    ]
}

/// Ladder discovery: called when a node is destroyed inside a mine scene. Rolls the ladder
/// chance and, when successful, drops a ladder on the tile.
pub fn maybe_reveal_ladder(ctx: &EngineContext, state: &mut GameState, scene_id: &str, x: i32, y: i32) -> Effects {
    if !is_mine_scene(scene_id) {
        return Vec::new();
    }
    let config = &ctx.content.mine;
    let mut rng = Rng::new(state.rng.clone());
    let revealed = rng.chance(config.ladder_chance);
    state.rng = rng.state;
    if !revealed {
        return Vec::new();
    }

    let Some(scene_index) = state.world.scenes.iter().position(|scene| scene.id == scene_id) else {
        return Vec::new();
    };
    // C# cloned the tile grid; the reducer edits the tile in place.
    state.world.scenes[scene_index].tiles[y as usize][x as usize].ladder_down = Some(true);

    vec![Effect::message("success", "A ladder to the next floor appears!")]
}
