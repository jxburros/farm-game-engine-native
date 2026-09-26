//! Tool use and interaction (port of `Farming/FarmingActions.cs` / farming/actions.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::GameState;

pub fn handle_use_tool(ctx: &EngineContext, state: &mut GameState, tool_type: &str) -> Effects {
    let _ = (ctx, state, tool_type);
    todo!("port FarmingActions.HandleUseTool")
}

pub fn handle_interact(ctx: &EngineContext, state: &mut GameState) -> Effects {
    let _ = (ctx, state);
    todo!("port FarmingActions.HandleInteract")
}
