//! The engine is a pure reducer over (state, content, command | tick) (port of `Engine.cs` /
//! engine.ts). Same seed + same command/tick log ⇒ same state.

use crate::commands::Command;
use crate::engine_types::{Effects, EngineContext};
use crate::schema::GameState;

/// Simulation runs at a fixed rate; rendering interpolates between ticks.
pub const TICKS_PER_SECOND: f64 = 20.0;
pub const MS_PER_TICK: f64 = 1000.0 / TICKS_PER_SECOND;

pub fn apply_command(ctx: &EngineContext, state: &mut GameState, command: &Command) -> Effects {
    let _ = (ctx, state, command);
    todo!("port Engine.ApplyCommand (dispatch + ApplyPluginMutation)")
}

pub fn advance_tick(ctx: &EngineContext, state: &mut GameState, ticks: f64) -> Effects {
    let _ = (ctx, state, ticks);
    todo!("port Engine.AdvanceTick / AdvanceSingleTick")
}
