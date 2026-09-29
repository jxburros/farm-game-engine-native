//! Custom actions, usable items and minigames (port of `Extensibility.cs` / extensibility.ts).
//!
//! Minigame session lifecycle + item-triggered actions (extensibility layer).
//!
//! A minigame is opened by the simulation (`startMinigame`), played in the host (an
//! implementation registered for the def's `kind`), and resolved by a single deterministic
//! command carrying the score — so replays only need the command log, never the realtime input
//! of the minigame itself.

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::hooks::{HookEvent, MinigameResolveHookPayload};
use crate::schema::GameState;
use crate::units;
use crate::{events, fishing, inventory};
use serde_json::Value;

pub fn handle_start_minigame(ctx: &EngineContext, state: &mut GameState, minigame_id: &str) -> Effects {
    events::start_minigame_session(ctx, state, minigame_id, None)
}

pub fn handle_cancel_minigame(state: &mut GameState) -> Effects {
    if state.minigame.is_none() {
        return vec![];
    }
    state.minigame = None;
    vec![]
}

/// Resolve the open minigame with a score in [0, 1]: built-in bindings run first (fishing), then
/// the def's score tiers (highest matching `minScore` wins), then the `onMinigameResolve` hook for
/// plugins.
pub fn handle_resolve_minigame(ctx: &EngineContext, state: &mut GameState, score: u64) -> Effects {
    // The session closes as soon as it resolves (C# `state with { Minigame = null }`).
    let Some(session) = state.minigame.take() else { return vec![] };
    // The score reads as a 0–1 fraction clamped onto the grid, like v8's `clampScore`.
    let score = score.min(units::PROBABILITY_ONE);
    let definition = ctx.content.minigames.iter().find(|def| def.id == session.minigame_id);

    let mut effects = Vec::new();

    // Built-in binding: the fishing minigame resolves the pending cast.
    if session.context.get("builtin").and_then(Value::as_str) == Some("fishing") {
        let rod_tier =
            session.context.get("rodTier").and_then(units::json_int).map_or(1, |tier| i32::try_from(tier).unwrap_or(1));
        effects.extend(fishing::resolve_fishing(ctx, state, rod_tier, Some(score)).effects);
    }

    if let Some(definition) = definition.filter(|def| !def.result_tiers.is_empty()) {
        let mut tiers: Vec<_> = definition.result_tiers.iter().collect();
        // Highest minScore first (stable for equal scores).
        tiers.sort_by(|a, b| b.min_score.cmp(&a.min_score));
        if let Some(tier) = tiers.into_iter().find(|candidate| score >= candidate.min_score) {
            effects.extend(events::apply_outcomes(ctx, state, &tier.outcomes, 0));
        }
    }

    ctx.emit(HookEvent::MinigameResolve(MinigameResolveHookPayload { minigame_id: session.minigame_id, score }));
    effects
}

/// Use an inventory item: runs its bound action, consuming one on a successful run when the item
/// declares `consumeOnUse`.
pub fn handle_use_item(ctx: &EngineContext, state: &mut GameState, item_id: &str) -> Effects {
    let Some(slot) = state.player.inventory.iter().find(|entry| entry.item.id == item_id) else {
        return vec![Effect::message(message_levels::ERROR, "You don't have that item.")];
    };
    let Some(use_action_id) = slot.item.use_action_id.clone().filter(|id| !id.is_empty()) else {
        return vec![Effect::message(message_levels::INFO, format!("{} can't be used like that.", slot.item.name))];
    };
    let consume_on_use = slot.item.consume_on_use == Some(true);

    let result = events::perform_action(ctx, state, &use_action_id);
    if result.ran && consume_on_use {
        state.player.inventory = inventory::remove_item(&state.player.inventory, item_id, 1);
    }
    result.effects
}
