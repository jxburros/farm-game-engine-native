//! Skills (port of `Skills.cs` / skills.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::GameState;

pub fn skill_level_for_xp(curve: &[f64], xp: f64) -> f64 {
    let _ = (curve, xp);
    todo!("port Skills.SkillLevelForXp")
}

pub fn grant_xp(ctx: &EngineContext, state: &mut GameState, skill: &str, xp: f64) -> Effects {
    let _ = (ctx, state, skill, xp);
    todo!("port Skills.GrantXp")
}

pub fn skill_level(state: &GameState, skill: &str) -> f64 {
    let _ = (state, skill);
    todo!("port Skills.SkillLevel")
}

pub fn farming_yield_bonus(state: &GameState) -> f64 {
    let _ = state;
    todo!("port Skills.FarmingYieldBonus")
}
