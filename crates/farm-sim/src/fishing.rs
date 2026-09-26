//! Fishing (port of `Fishing.cs` / fishing.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::{FishTable, GameState};

/// TS `FishingResult` minus the state (updated in place).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct FishingResult {
    pub effects: Effects,
    pub caught: bool,
}

pub fn active_fish_table<'a>(ctx: &'a EngineContext, state: &GameState) -> Option<&'a FishTable> {
    let _ = (ctx, state);
    todo!("port Fishing.ActiveFishTable")
}

/// TS `resolveFishing(ctx, state, rodTier, options?)`; `score` is `options.score`.
pub fn resolve_fishing(ctx: &EngineContext, state: &mut GameState, rod_tier: f64, score: Option<f64>) -> FishingResult {
    let _ = (ctx, state, rod_tier, score);
    todo!("port Fishing.ResolveFishing")
}
