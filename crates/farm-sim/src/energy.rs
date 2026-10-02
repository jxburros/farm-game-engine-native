//! Energy (port of `Energy.cs` / energy.ts).
//!
//! Player energy (M2). Per-action costs come from tool definitions; hitting zero collapses the
//! player, ending the day with a penalty. Toggleable per project (settings.energyEnabled).

use crate::content_builtin::ToolDefinition;
use crate::effects::Effect;
use crate::engine_types::{Effects, EngineContext};
use crate::game_time::{self, SleepOptions};
use crate::messages;
use crate::schema::GameState;
use crate::units;

/// Below this fraction of max energy (1/5) the player is warned.
pub const LOW_ENERGY_DIVISOR: i32 = 5;

/// TS `EnergySpendResult` minus the state (updated in place).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct EnergySpendResult {
    pub effects: Effects,
    /// True when the spend collapsed the player (day ended — stop processing).
    pub collapsed: bool,
}

/// Effective energy cost for a tool at a given tier (higher tiers are more efficient): 15% less
/// per tier above 1, rounded to whole points, at least 1 point.
pub fn effective_energy_cost(definition: &ToolDefinition, tier: i32) -> i32 {
    let percent = 100 - 15 * (i64::from(tier) - 1);
    let whole_points =
        units::div_round(i64::from(definition.energy_cost) * percent, 100 * i64::from(units::ENERGY_POINT));
    units::points(i32::try_from(whole_points.max(1)).unwrap_or(i32::MAX))
}

/// Spend `amount` energy (thousandths of a point); running out collapses the player, which ends
/// the day. Nothing happens with energy off.
pub fn spend_energy(ctx: &EngineContext, state: &mut GameState, amount: i32) -> EnergySpendResult {
    if !ctx.content.settings.energy_enabled || amount <= 0 {
        return EnergySpendResult { effects: Vec::new(), collapsed: false };
    }

    let before = state.player.energy;
    let after = before.saturating_sub(amount);
    let mut effects: Effects = Vec::new();

    if after <= 0 {
        state.player.energy = 0;
        let sleep_effects = game_time::perform_sleep(ctx, state, SleepOptions { collapsed: true });
        effects.extend(sleep_effects);
        return EnergySpendResult { effects, collapsed: true };
    }

    // after <= max/5 < before, without truncating max/5.
    let max = i64::from(state.player.max_energy);
    let divisor = i64::from(LOW_ENERGY_DIVISOR);
    if i64::from(after) * divisor <= max && i64::from(before) * divisor > max {
        effects.push(Effect::say("info", &messages::EXHAUSTED));
    }

    state.player.energy = after;
    EnergySpendResult { effects, collapsed: false }
}
