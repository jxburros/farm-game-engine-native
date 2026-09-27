//! Energy (port of `Energy.cs` / energy.ts).
//!
//! Player energy (M2). Per-action costs come from tool definitions; hitting zero collapses the
//! player, ending the day with a penalty. Toggleable per project (settings.energyEnabled).

use crate::content_builtin::ToolDefinition;
use crate::effects::Effect;
use crate::engine_types::{Effects, EngineContext};
use crate::game_time::{self, SleepOptions};
use crate::js;
use crate::schema::GameState;

pub const LOW_ENERGY_FRACTION: f64 = 0.2;

/// TS `EnergySpendResult` minus the state (updated in place).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct EnergySpendResult {
    pub effects: Effects,
    /// True when the spend collapsed the player (day ended — stop processing).
    pub collapsed: bool,
}

/// Effective energy cost for a tool at a given tier (higher tiers are more efficient).
pub fn effective_energy_cost(definition: &ToolDefinition, tier: f64) -> f64 {
    1.0_f64.max(js::round(definition.energy_cost * (1.0 - 0.15 * (tier - 1.0))))
}

pub fn spend_energy(ctx: &EngineContext, state: &mut GameState, amount: f64) -> EnergySpendResult {
    if !ctx.content.settings.energy_enabled || amount <= 0.0 {
        return EnergySpendResult { effects: Vec::new(), collapsed: false };
    }

    let before = state.player.energy;
    let after = before - amount;
    let mut effects: Effects = Vec::new();

    if after <= 0.0 {
        state.player.energy = 0.0;
        let sleep_effects = game_time::perform_sleep(ctx, state, SleepOptions { collapsed: true });
        effects.extend(sleep_effects);
        return EnergySpendResult { effects, collapsed: true };
    }

    let low_threshold = state.player.max_energy * LOW_ENERGY_FRACTION;
    if after <= low_threshold && before > low_threshold {
        effects.push(Effect::message("info", "You are getting exhausted — consider sleeping."));
    }

    state.player.energy = after;
    EnergySpendResult { effects, collapsed: false }
}
