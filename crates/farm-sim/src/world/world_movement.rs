//! Player movement and collision (port of `World/WorldMovement.cs` / world/movement.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::{GameState, MachineTypeDefinition, MoveIntent, NodeTypeDefinition, NpcState, Scene};
use indexmap::IndexMap;

/// TS `getDirectionVector` result.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct DirectionVector {
    pub dx: f64,
    pub dy: f64,
}

/// Integer tile coordinates.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct TilePoint {
    pub x: f64,
    pub y: f64,
}

pub fn get_direction_vector(direction: &str) -> DirectionVector {
    let _ = direction;
    todo!("port WorldMovement.GetDirectionVector")
}

pub fn player_tile(state: &GameState) -> TilePoint {
    let _ = state;
    todo!("port WorldMovement.PlayerTile")
}

pub fn facing_target(state: &GameState) -> TilePoint {
    let _ = state;
    todo!("port WorldMovement.FacingTarget")
}

pub fn direction_from_intent(intent: &MoveIntent, current: &str) -> String {
    let _ = (intent, current);
    todo!("port WorldMovement.DirectionFromIntent")
}

pub fn can_move_to(
    scene: &Scene,
    x: f64,
    y: f64,
    npcs: &IndexMap<String, NpcState>,
    exclude_npc_id: Option<&str>,
    node_types: Option<&IndexMap<String, NodeTypeDefinition>>,
    machine_types: Option<&IndexMap<String, MachineTypeDefinition>>,
) -> bool {
    let _ = (scene, x, y, npcs, exclude_npc_id, node_types, machine_types);
    todo!("port WorldMovement.CanMoveTo")
}

pub fn find_scene<'a>(state: &'a GameState, scene_id: &str) -> Option<&'a Scene> {
    let _ = (state, scene_id);
    todo!("port WorldMovement.FindScene")
}

/// TS `byId`: definitions keyed by id, insertion order kept.
pub fn by_id<T: Clone>(defs: &[T], id: impl Fn(&T) -> &str) -> IndexMap<String, T> {
    let mut map = IndexMap::new();
    for def in defs {
        map.insert(id(def).to_owned(), def.clone());
    }
    map
}

pub fn handle_move(ctx: &EngineContext, state: &mut GameState, dir: &str) -> Effects {
    let _ = (ctx, state, dir);
    todo!("port WorldMovement.HandleMove")
}

pub fn integrate_movement(ctx: &EngineContext, state: &mut GameState, dt_seconds: f64) -> Effects {
    let _ = (ctx, state, dt_seconds);
    todo!("port WorldMovement.IntegrateMovement")
}
