//! Events, conditions, outcomes and creator actions (port of `Events.cs` / events.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::{EventCondition, EventOutcome, GameEvent, GameState};
use indexmap::IndexMap;
use serde_json::Value;

/// TS `EventPosition`.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct EventPosition {
    pub x: f64,
    pub y: f64,
}

/// TS `PerformActionResult` minus the state (updated in place).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PerformActionResult {
    pub effects: Effects,
    pub ran: bool,
}

pub fn condition_met(
    ctx: &EngineContext,
    state: &GameState,
    condition: &EventCondition,
    pos: Option<EventPosition>,
) -> bool {
    let _ = (ctx, state, condition, pos);
    todo!("port GameEvents.ConditionMet")
}

pub fn flag_value<'a>(state: &'a GameState, name: &str) -> Option<&'a Value> {
    let _ = (state, name);
    todo!("port GameEvents.FlagValue")
}

pub fn apply_outcomes(ctx: &EngineContext, state: &mut GameState, outcomes: &[EventOutcome], depth: f64) -> Effects {
    let _ = (ctx, state, outcomes, depth);
    todo!("port GameEvents.ApplyOutcomes")
}

pub fn perform_action(ctx: &EngineContext, state: &mut GameState, action_id: &str) -> PerformActionResult {
    let _ = (ctx, state, action_id);
    todo!("port GameEvents.PerformAction")
}

pub fn start_minigame_session(
    ctx: &EngineContext,
    state: &mut GameState,
    minigame_id: &str,
    context: Option<&IndexMap<String, Value>>,
) -> Effects {
    let _ = (ctx, state, minigame_id, context);
    todo!("port GameEvents.StartMinigameSession")
}

pub fn fire_event(ctx: &EngineContext, state: &mut GameState, event: &GameEvent) -> Effects {
    let _ = (ctx, state, event);
    todo!("port GameEvents.FireEvent")
}

pub fn evaluate_events(
    ctx: &EngineContext,
    state: &mut GameState,
    trigger: &str,
    pos: Option<EventPosition>,
) -> Effects {
    let _ = (ctx, state, trigger, pos);
    todo!("port GameEvents.EvaluateEvents")
}
