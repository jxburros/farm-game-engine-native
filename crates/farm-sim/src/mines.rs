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
use crate::js;
use crate::rng::{self, Rng};
use crate::schema::{GameState, MineBand, MineConfig, MineProgress, Scene, TileNode};
use crate::world::tiles;
use serde_json::Value;

pub const MINE_SCENE_PREFIX: &str = "mine-floor-";

pub fn mine_floor_scene_id(floor: f64) -> String {
    format!("{MINE_SCENE_PREFIX}{}", js::num(floor))
}

pub fn is_mine_scene(scene_id: &str) -> bool {
    scene_id.starts_with(MINE_SCENE_PREFIX)
}

fn band_for_floor(config: &MineConfig, floor: f64) -> Option<&MineBand> {
    config.bands.iter().find(|band| floor >= band.from_floor && floor <= band.to_floor).or_else(|| config.bands.last())
}

/// Deterministically generate a mine floor: walled border, entry ladder at the top-left corner
/// area, rocks from the depth band's weighted table.
pub fn generate_mine_floor(ctx: &EngineContext, engine_seed: &str, floor: f64) -> Scene {
    let config = &ctx.content.mine;
    let mut scene = tiles::create_empty_scene(
        &mine_floor_scene_id(floor),
        &format!("Mine — Floor {}", js::num(floor)),
        config.floor_width,
        config.floor_height,
    );
    // TS: createRngState(hashStringToU32(...)) — the numeric-seed overload.
    let seed = rng::hash_string_to_u32(&format!("{engine_seed}:mine:{}", js::num(floor)));
    let mut rng = Rng::new(rng::create_rng_state_from_number(f64::from(seed)));

    // Cave look: floor tiles + wall border
    let mut y = 0.0;
    while y < scene.height {
        let mut x = 0.0;
        while x < scene.width {
            let border = x == 0.0 || y == 0.0 || x == scene.width - 1.0 || y == scene.height - 1.0;
            let tile = &mut scene.tiles[y as usize][x as usize];
            tile.r#type = if border { "wall" } else { "floor" }.to_owned();
            tile.background = "floor".to_owned();
            tile.overlay = None;
            tile.object = if border { Some("wall".to_owned()) } else { None };
            tile.collision = border;
            x += 1.0;
        }
        y += 1.0;
    }

    const ENTRY_X: f64 = 1.0;
    const ENTRY_Y: f64 = 1.0;
    let band = band_for_floor(config, floor);

    if let Some(band) = band {
        let weights: Vec<f64> = band.rocks.iter().map(|rock| rock.weight).collect();
        let mut y = 1.0;
        while y < scene.height - 1.0 {
            let mut x = 1.0;
            while x < scene.width - 1.0 {
                place_rock(ctx, band, &weights, &mut rng, &mut scene, x, y, x == ENTRY_X && y == ENTRY_Y);
                x += 1.0;
            }
            y += 1.0;
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
    weights: &[f64],
    rng: &mut Rng,
    scene: &mut Scene,
    x: f64,
    y: f64,
    is_entry: bool,
) {
    if is_entry {
        return;
    }
    if rng.float() >= band.density {
        return;
    }
    let pick = rng.weighted(weights);
    let Some(rock) = usize::try_from(pick).ok().and_then(|i| band.rocks.get(i)) else {
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
pub fn descend_mine(ctx: &EngineContext, state: &mut GameState, to_floor: f64) -> Effects {
    let config = &ctx.content.mine;
    if !config.enabled {
        return Vec::new();
    }
    let floor = 1.0_f64.max(config.floors.min(to_floor));

    let scene_id = mine_floor_scene_id(floor);
    if !state.world.scenes.iter().any(|scene| scene.id == scene_id) {
        let generated = generate_mine_floor(ctx, &state.meta.engine_seed, floor);
        state.world.scenes.push(generated);
    }

    state.player.scene_id = scene_id.clone();
    state.player.x = 1.5;
    state.player.y = 1.5;
    state.mine = MineProgress { current_floor: floor, deepest_floor: state.mine.deepest_floor.max(floor) };

    let checkpoint = if floor % config.elevator_every == 0.0 { " (elevator checkpoint)" } else { "" };
    vec![
        Effect::SceneChanged { scene_id, x: 1.0, y: 1.0 },
        Effect::message("info", format!("Mine — floor {}{checkpoint}", js::num(floor))),
    ]
}

/// Leave the mine back to the entrance scene.
pub fn exit_mine(ctx: &EngineContext, state: &mut GameState) -> Effects {
    let config = &ctx.content.mine;
    let target_scene_id = config.entrance_scene_id.clone().unwrap_or_else(|| ctx.content.start_scene_id.clone());
    let Some(target) = state.world.scenes.iter().find(|scene| scene.id == target_scene_id) else {
        return Vec::new();
    };
    let x = config.entrance_x.unwrap_or_else(|| (target.width / 2.0).floor());
    let y = config.entrance_y.unwrap_or_else(|| (target.height / 2.0).floor());

    // Drop generated floors so they regenerate fresh next visit.
    state.world.scenes.retain(|scene| !is_mine_scene(&scene.id));
    state.player.scene_id = target_scene_id.clone();
    state.player.x = x + 0.5;
    state.player.y = y + 0.5;
    state.mine.current_floor = 0.0;

    vec![
        Effect::SceneChanged { scene_id: target_scene_id, x, y },
        Effect::message("info", "You climb back to the surface."),
    ]
}

/// Ladder discovery: called when a node is destroyed inside a mine scene. Rolls the ladder
/// chance and, when successful, drops a ladder on the tile.
pub fn maybe_reveal_ladder(ctx: &EngineContext, state: &mut GameState, scene_id: &str, x: f64, y: f64) -> Effects {
    if !is_mine_scene(scene_id) {
        return Vec::new();
    }
    let config = &ctx.content.mine;
    let mut rng = Rng::new(state.rng.clone());
    let revealed = rng.float() < config.ladder_chance;
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
