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
use crate::{events, fishing, inventory};
use serde_json::Value;
use std::cmp::Ordering;

/// JS `Math.min`: NaN propagates (Rust's `f64::min` returns the other operand).
fn js_min(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() {
        f64::NAN
    } else {
        a.min(b)
    }
}

/// JS `Math.max`: NaN propagates (Rust's `f64::max` returns the other operand).
fn js_max(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() {
        f64::NAN
    } else {
        a.max(b)
    }
}

/// Clamp an untrusted score into [0, 1]; NaN counts as 0.
pub fn clamp_score(score: f64) -> f64 {
    if !score.is_finite() {
        return 0.0;
    }
    js_max(0.0, js_min(1.0, score))
}

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
pub fn handle_resolve_minigame(ctx: &EngineContext, state: &mut GameState, raw_score: f64) -> Effects {
    // The session closes as soon as it resolves (C# `state with { Minigame = null }`).
    let Some(session) = state.minigame.take() else { return vec![] };
    let score = clamp_score(raw_score);
    let definition = ctx.content.minigames.iter().find(|def| def.id == session.minigame_id);

    let mut effects = Vec::new();

    // Built-in binding: the fishing minigame resolves the pending cast.
    if session.context.get("builtin").and_then(Value::as_str) == Some("fishing") {
        let rod_tier = session.context.get("rodTier").and_then(Value::as_f64).unwrap_or(1.0);
        effects.extend(fishing::resolve_fishing(ctx, state, rod_tier, Some(score)).effects);
    }

    if let Some(definition) = definition.filter(|def| !def.result_tiers.is_empty()) {
        let mut tiers: Vec<_> = definition.result_tiers.iter().collect();
        tiers.sort_by(|a, b| {
            let d = b.min_score - a.min_score;
            if d > 0.0 {
                Ordering::Greater
            } else if d < 0.0 {
                Ordering::Less
            } else {
                Ordering::Equal
            }
        });
        if let Some(tier) = tiers.into_iter().find(|candidate| score >= candidate.min_score) {
            effects.extend(events::apply_outcomes(ctx, state, &tier.outcomes, 0.0));
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
        state.player.inventory = inventory::remove_item(&state.player.inventory, item_id, 1.0);
    }
    result.effects
}
