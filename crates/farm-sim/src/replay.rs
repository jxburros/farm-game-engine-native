//! Replay harness (port of `Replay.cs` / replay.ts): run a scripted input log through the engine
//! and hash the final state. Golden replay tests fail on any nondeterminism.

use crate::commands::Command;
use crate::engine;
use crate::engine_types::{Effects, EngineContext};
use crate::hash;
use crate::schema::GameState;
use serde::{Deserialize, Serialize};

/// One entry of a replay input log. JSON shape matches the TS union:
/// `{"kind":"command","command":{…}}` / `{"kind":"tick","ticks":N}`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum ReplayInput {
    Command { command: Command },
    Tick { ticks: f64 },
}

/// TS `ReplayResult`: the final state hash and every effect, in order (the state itself was
/// updated in place).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ReplayResult {
    pub hash: String,
    pub effects: Effects,
}

pub fn run_replay(ctx: &EngineContext, state: &mut GameState, inputs: &[ReplayInput]) -> ReplayResult {
    let mut effects = Vec::new();
    for input in inputs {
        match input {
            ReplayInput::Tick { ticks } => effects.extend(engine::advance_tick(ctx, state, *ticks)),
            ReplayInput::Command { command } => effects.extend(engine::apply_command(ctx, state, command)),
        }
    }
    ReplayResult { hash: hash::hash_state(state), effects }
}

/// TS `command(cmd)`.
pub fn command(command: Command) -> ReplayInput {
    ReplayInput::Command { command }
}

/// TS `ticks(count)`.
pub fn ticks(count: f64) -> ReplayInput {
    ReplayInput::Tick { ticks: count }
}
