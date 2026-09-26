//! Energy (port of `Energy.cs` / energy.ts).

use crate::content_builtin::ToolDefinition;
use crate::engine_types::{Effects, EngineContext};
use crate::schema::GameState;

/// TS `EnergySpendResult` minus the state (updated in place).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct EnergySpendResult {
    pub effects: Effects,
    pub collapsed: bool,
}

pub fn effective_energy_cost(definition: &ToolDefinition, tier: f64) -> f64 {
    let _ = (definition, tier);
    todo!("port Energy.EffectiveEnergyCost")
}

pub fn spend_energy(ctx: &EngineContext, state: &mut GameState, amount: f64) -> EnergySpendResult {
    let _ = (ctx, state, amount);
    todo!("port Energy.SpendEnergy")
}
