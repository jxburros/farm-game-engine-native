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
use crate::messages;
use crate::rng::{self, Rng};
use crate::schema::{GameState, MineBand, MineConfig, MineProgress, Scene, TileNode, MAX_SCENE_SIZE};
use crate::units;
use crate::world::{tiles, world_movement};

pub const MINE_SCENE_PREFIX: &str = "mine-floor-";

/// Where the player arrives on a floor, and the tile whose interaction climbs out.
pub const FLOOR_ENTRY: (i32, i32) = (1, 1);

/// The smallest floor side: a wall ring around at least one tile (the entry).
pub const MIN_FLOOR_SIZE: i32 = 3;

/// The scene id of mine floor `floor`.
pub fn mine_floor_scene_id(floor: u32) -> String {
    format!("{MINE_SCENE_PREFIX}{floor}")
}

/// Whether the id has the shape of a mine floor's. A creator can name a scene that way too; see
/// [`is_mine_floor`].
pub fn is_mine_scene(scene_id: &str) -> bool {
    scene_id.starts_with(MINE_SCENE_PREFIX)
}

/// Whether `scene_id` is a mine floor the engine generated (not an authored scene that happens
/// to use the prefix).
pub fn is_mine_floor(state: &GameState, scene_id: &str) -> bool {
    is_mine_scene(scene_id) && world_movement::find_scene(state, scene_id).is_some_and(Scene::is_generated)
}

/// A floor side from content, clamped to `MIN_FLOOR_SIZE..=MAX_SCENE_SIZE`: two content numbers
/// must not make the game allocate billions of tiles (an allocation failure aborts the process),
/// and a side under 3 would put the entry on the wall or outside the floor.
pub fn floor_side(configured: u32) -> i32 {
    i32::try_from(configured).unwrap_or(i32::MAX).clamp(MIN_FLOOR_SIZE, MAX_SCENE_SIZE)
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
        floor_side(config.floor_width),
        floor_side(config.floor_height),
    );
    // TS: createRngState(hashStringToU32(...)) — the numeric-seed overload.
    let seed = rng::hash_string_to_u32(&format!("{engine_seed}:mine:{floor}"));
    let mut rng = Rng::new(rng::create_rng_state_from_u32(seed));

    // Cave look: floor tiles + wall border
    let (width, height) = (scene.width, scene.height);
    for (y, row) in scene.tiles.iter_mut().enumerate() {
        for (x, tile) in row.iter_mut().enumerate() {
            let (x, y) = (x as i32, y as i32);
            let border = x == 0 || y == 0 || x == width - 1 || y == height - 1;
            tile.r#type = if border { "wall" } else { "floor" }.to_owned();
            tile.background = "floor".to_owned();
            tile.overlay = None;
            tile.object = if border { Some("wall".to_owned()) } else { None };
            tile.collision = border;
        }
    }

    let (entry_x, entry_y) = FLOOR_ENTRY;
    let band = band_for_floor(config, floor);

    if let Some(band) = band {
        let weights: Vec<u32> = band.rocks.iter().map(|rock| rock.weight).collect();
        for y in 1..scene.height - 1 {
            for x in 1..scene.width - 1 {
                place_rock(ctx, band, &weights, &mut rng, &mut scene, x, y, x == entry_x && y == entry_y);
            }
        }
    }

    // Mark the scene as generated so the project bridge skips it and `exitMine` drops it.
    scene.generated = Some(true);
    // Underground: the weather stays outside.
    scene.indoor = Some(true);
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
    let Some(node_def) = ctx.node_type(node_type_id) else {
        return;
    };
    if let Some(tile) = scene.tile_mut(x, y) {
        tile.node =
            Some(TileNode { type_id: node_type_id.clone(), remaining_health: node_def.health, ..TileNode::default() });
    }
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

    let (entry_x, entry_y) = FLOOR_ENTRY;
    state.player.scene_id = scene_id.clone();
    state.player.x = units::tile_center(entry_x);
    state.player.y = units::tile_center(entry_y);
    state.mine = MineProgress { current_floor: floor, deepest_floor: state.mine.deepest_floor.max(floor) };

    // `floor % 0` is NaN in JS: never a checkpoint.
    let checkpoint = floor.checked_rem(config.elevator_every) == Some(0);
    let announcement = if checkpoint { &messages::MINE_FLOOR_CHECKPOINT } else { &messages::MINE_FLOOR };
    vec![Effect::SceneChanged { scene_id, x: entry_x, y: entry_y }, Effect::say("info", announcement.with(&[&floor]))]
}

/// The deepest floor the mine entrance's elevator reaches: the last checkpoint at or above the
/// deepest floor reached (at least floor 1).
pub fn elevator_floor(config: &MineConfig, state: &GameState) -> u32 {
    // floor(deepest / every) × every; JS divides by zero into NaN, and max(1, NaN) is NaN.
    let checkpoint = state
        .mine
        .deepest_floor
        .checked_div(config.elevator_every)
        .map_or(0, |elevators| elevators * config.elevator_every);
    checkpoint.max(1)
}

/// Whether the player stands next to (or on, or facing) the mine entrance.
fn beside_entrance(config: &MineConfig, state: &GameState) -> bool {
    let (Some(scene_id), Some(x), Some(y)) =
        (config.entrance_scene_id.as_deref(), config.entrance_x, config.entrance_y)
    else {
        return false;
    };
    let player = world_movement::player_tile(state);
    state.player.scene_id == scene_id
        && (i64::from(player.x) - i64::from(x)).abs() <= 1
        && (i64::from(player.y) - i64::from(y)).abs() <= 1
}

/// The `descendMine` command under [`crate::CommandRules::Player`]: why it does not apply now
/// (`None` when it does). It works on a mine floor or beside the entrance, down to one floor past
/// the current floor or the elevator's deepest checkpoint.
pub fn descend_refusal(ctx: &EngineContext, state: &GameState, to_floor: u32) -> Option<Effects> {
    let config = &ctx.content.mine;
    if !config.enabled {
        return Some(Vec::new());
    }
    let in_mine = is_mine_floor(state, &state.player.scene_id);
    if !in_mine && !beside_entrance(config, state) {
        return Some(vec![Effect::say("info", &messages::MINE_NOT_AT_ENTRANCE)]);
    }
    let reachable =
        elevator_floor(config, state).max(if in_mine { state.mine.current_floor } else { 0 }).saturating_add(1);
    if to_floor > reachable {
        return Some(vec![Effect::say("info", &messages::MINE_NOT_THAT_DEEP)]);
    }
    None
}

/// The `exitMine` command under [`crate::CommandRules::Player`]: only on a mine floor.
pub fn exit_refusal(state: &GameState) -> Option<Effects> {
    if is_mine_floor(state, &state.player.scene_id) {
        None
    } else {
        Some(vec![Effect::say("info", &messages::MINE_NOT_IN_MINE)])
    }
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

    // Drop generated floors so they regenerate fresh next visit. Authored scenes stay, whatever
    // their id.
    state.world.scenes.retain(|scene| !(scene.is_generated() && is_mine_scene(&scene.id)));
    state.player.scene_id = target_scene_id.clone();
    state.player.x = units::tile_center(x);
    state.player.y = units::tile_center(y);
    state.mine.current_floor = 0;

    vec![Effect::SceneChanged { scene_id: target_scene_id, x, y }, Effect::say("info", &messages::MINE_CLIMB_BACK)]
}

/// Ladder discovery: called when a node is destroyed inside a mine scene. Rolls the ladder
/// chance and, when successful, drops a ladder on the tile.
pub fn maybe_reveal_ladder(ctx: &EngineContext, state: &mut GameState, scene_id: &str, x: i32, y: i32) -> Effects {
    if !is_mine_floor(state, scene_id) {
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
    if let Some(tile) = state.world.scenes[scene_index].tile_mut(x, y) {
        tile.ladder_down = Some(true);
    }

    vec![Effect::say("success", &messages::MINE_LADDER)]
}
