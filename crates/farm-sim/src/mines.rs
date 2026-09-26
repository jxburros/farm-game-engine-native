//! Mines (port of `Mines.cs` / mines.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::{GameState, Scene};

pub fn mine_floor_scene_id(floor: f64) -> String {
    let _ = floor;
    todo!("port Mines.MineFloorSceneId")
}

pub fn is_mine_scene(scene_id: &str) -> bool {
    let _ = scene_id;
    todo!("port Mines.IsMineScene")
}

pub fn generate_mine_floor(ctx: &EngineContext, engine_seed: &str, floor: f64) -> Scene {
    let _ = (ctx, engine_seed, floor);
    todo!("port Mines.GenerateMineFloor")
}

pub fn descend_mine(ctx: &EngineContext, state: &mut GameState, to_floor: f64) -> Effects {
    let _ = (ctx, state, to_floor);
    todo!("port Mines.DescendMine")
}

pub fn exit_mine(ctx: &EngineContext, state: &mut GameState) -> Effects {
    let _ = (ctx, state);
    todo!("port Mines.ExitMine")
}

pub fn maybe_reveal_ladder(ctx: &EngineContext, state: &mut GameState, scene_id: &str, x: f64, y: f64) -> Effects {
    let _ = (ctx, state, scene_id, x, y);
    todo!("port Mines.MaybeRevealLadder")
}
