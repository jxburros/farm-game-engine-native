//! Custom actions, usable items and minigames (port of `Extensibility.cs` / extensibility.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::GameState;

pub fn clamp_score(score: f64) -> f64 {
    let _ = score;
    todo!("port Extensibility.ClampScore")
}

pub fn handle_start_minigame(ctx: &EngineContext, state: &mut GameState, minigame_id: &str) -> Effects {
    let _ = (ctx, state, minigame_id);
    todo!("port Extensibility.HandleStartMinigame")
}

pub fn handle_cancel_minigame(state: &mut GameState) -> Effects {
    let _ = state;
    todo!("port Extensibility.HandleCancelMinigame")
}

pub fn handle_resolve_minigame(ctx: &EngineContext, state: &mut GameState, raw_score: f64) -> Effects {
    let _ = (ctx, state, raw_score);
    todo!("port Extensibility.HandleResolveMinigame")
}

pub fn handle_use_item(ctx: &EngineContext, state: &mut GameState, item_id: &str) -> Effects {
    let _ = (ctx, state, item_id);
    todo!("port Extensibility.HandleUseItem")
}
