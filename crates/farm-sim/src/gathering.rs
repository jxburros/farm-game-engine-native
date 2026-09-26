//! Gathering nodes (port of `Gathering.cs` / gathering.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::{GameState, NodeTypeDefinition, Tile};

/// TS `NodeStrikeOutcome` minus the state (updated in place).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct NodeStrikeOutcome {
    pub effects: Effects,
    pub struck: bool,
}

pub fn node_type_by_id<'a>(ctx: &'a EngineContext, type_id: &str) -> Option<&'a NodeTypeDefinition> {
    let _ = (ctx, type_id);
    todo!("port Gathering.NodeTypeById")
}

pub fn is_node_active(tile: &Tile) -> bool {
    let _ = tile;
    todo!("port Gathering.IsNodeActive")
}

pub fn replace_first_dash(value: &str) -> String {
    let _ = value;
    todo!("port Gathering.ReplaceFirstDash")
}

#[allow(clippy::too_many_arguments)]
pub fn strike_node(
    ctx: &EngineContext,
    state: &mut GameState,
    scene_id: &str,
    x: f64,
    y: f64,
    tool_type: &str,
    tool_tier: f64,
    tool_power: f64,
) -> NodeStrikeOutcome {
    let _ = (ctx, state, scene_id, x, y, tool_type, tool_tier, tool_power);
    todo!("port Gathering.StrikeNode")
}
